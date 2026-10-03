/*
 * Contents/MacOS/typedown-webkit-export in Typedown.app: printing, PDF export and picture export on macOS, the
 * part Services/WebKitExport does through WebKitGTK on Linux. Uno's web view exposes no print operation, so the
 * exported HTML file is loaded into an offscreen WKWebView here and printed from it: to a file for "Export PDF",
 * with the print panel for "Print…", or snapshotted whole for "Export image". A separate process, because the
 * WebKit and AppKit calls need delegates and blocks that are awkward to build from .NET.
 *
 *   typedown-webkit-export pdf   <html> <pdf> [margin-mm]
 *   typedown-webkit-export png   <html> <png> [width]
 *   typedown-webkit-export print <html> [margin-mm]
 *
 * Exit code 0 when the file was written (or the print panel was dismissed), 1 when it failed, 2 on bad usage.
 *
 *   clang -O2 -fobjc-arc -arch arm64 -mmacosx-version-min=12.0 -framework AppKit -framework WebKit \
 *     -o typedown-webkit-export packaging/macos-webkit-export.m
 */
#import <AppKit/AppKit.h>
#import <WebKit/WebKit.h>

static const CGFloat MillimetresPerPoint = 25.4 / 72.0;

@interface Exporter : NSObject <WKNavigationDelegate>
@property (nonatomic) NSString *mode;
@property (nonatomic) NSURL *output;
@property (nonatomic) CGFloat margin;   // points
@property (nonatomic) CGFloat width;    // points, for the picture
@property (nonatomic) NSWindow *window;
@property (nonatomic) WKWebView *view;
@property (nonatomic) int result;
@end

@implementation Exporter

- (void)startWithHtml:(NSURL *)html
{
    // A page-ish width for the PDF, so the document's media queries see a document and not a phone.
    NSRect frame = NSMakeRect(0, 0, [self.mode isEqualToString:@"png"] ? self.width : 900, 1200);
    self.window = [[NSWindow alloc] initWithContentRect:frame styleMask:NSWindowStyleMaskBorderless
                                                backing:NSBackingStoreBuffered defer:NO];
    self.view = [[WKWebView alloc] initWithFrame:frame configuration:[WKWebViewConfiguration new]];
    self.view.navigationDelegate = self;
    self.window.contentView = self.view;
    // Pictures next to the document are referenced by absolute file paths, so the page may read any file.
    [self.view loadFileURL:html allowingReadAccessToURL:[NSURL fileURLWithPath:@"/"]];
}

- (void)webView:(WKWebView *)webView didFinishNavigation:(WKNavigation *)navigation
{
    // Diagrams and fonts may still be settling after the load event; one more turn of the run loop is enough
    // for what the exported page does at load.
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(0.3 * NSEC_PER_SEC)), dispatch_get_main_queue(), ^{
        if ([self.mode isEqualToString:@"png"]) [self snapshot];
        else [self print];
    });
}

- (void)webView:(WKWebView *)webView didFailNavigation:(WKNavigation *)navigation withError:(NSError *)error
{
    [self failWith:error];
}

- (void)webView:(WKWebView *)webView didFailProvisionalNavigation:(WKNavigation *)navigation withError:(NSError *)error
{
    [self failWith:error];
}

- (void)failWith:(NSError *)error
{
    fprintf(stderr, "typedown-webkit-export: %s\n", error.localizedDescription.UTF8String);
    [self finish:1];
}

- (void)print
{
    NSPrintInfo *info = [[NSPrintInfo sharedPrintInfo] copy];
    info.topMargin = info.bottomMargin = info.leftMargin = info.rightMargin = self.margin;
    info.horizontalPagination = NSPrintingPaginationModeFit;
    info.verticalPagination = NSPrintingPaginationModeAutomatic;
    info.verticallyCentered = NO;
    BOOL toFile = [self.mode isEqualToString:@"pdf"];
    if (toFile)
    {
        info.jobDisposition = NSPrintSaveJob;
        info.dictionary[NSPrintJobSavingURL] = self.output;
    }
    NSPrintOperation *op = [self.view printOperationWithPrintInfo:info];
    op.showsPrintPanel = !toFile;
    op.showsProgressPanel = NO;
    // WKWebView prints blank pages unless its print view has a size and the operation runs modally for a window.
    op.view.frame = self.view.bounds;
    if (!toFile) [NSApp activateIgnoringOtherApps:YES];
    [op runOperationModalForWindow:self.window delegate:self
                    didRunSelector:@selector(printOperationDidRun:success:contextInfo:) contextInfo:NULL];
}

- (void)printOperationDidRun:(NSPrintOperation *)op success:(BOOL)success contextInfo:(void *)contextInfo
{
    if ([self.mode isEqualToString:@"pdf"])
        [self finish:success && [[NSFileManager defaultManager] fileExistsAtPath:self.output.path] ? 0 : 1];
    else
        [self finish:0]; // a cancelled print panel is not a failure
}

- (void)snapshot
{
    // The picture covers the whole document: grow the view to the page's height first.
    [self.view evaluateJavaScript:@"Math.max(document.documentElement.scrollHeight, document.body ? document.body.scrollHeight : 0)"
                completionHandler:^(id height, NSError *error) {
        CGFloat h = [height isKindOfClass:[NSNumber class]] ? [height doubleValue] : 0;
        if (h < 1) h = self.view.frame.size.height;
        NSRect frame = NSMakeRect(0, 0, self.width, h);
        [self.window setContentSize:frame.size];
        self.view.frame = frame;
        dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(0.2 * NSEC_PER_SEC)), dispatch_get_main_queue(), ^{
            WKSnapshotConfiguration *config = [WKSnapshotConfiguration new];
            config.rect = frame;
            config.afterScreenUpdates = YES;
            [self.view takeSnapshotWithConfiguration:config completionHandler:^(NSImage *image, NSError *snapError) {
                if (image == nil) { [self failWith:snapError]; return; }
                CGImageRef cg = [image CGImageForProposedRect:NULL context:nil hints:nil];
                NSBitmapImageRep *rep = [[NSBitmapImageRep alloc] initWithCGImage:cg];
                NSData *png = [rep representationUsingType:NSBitmapImageFileTypePNG properties:@{}];
                [self finish:[png writeToURL:self.output atomically:YES] ? 0 : 1];
            }];
        });
    }];
}

- (void)finish:(int)result
{
    self.result = result;
    [NSApp stop:nil];
    // stop: takes effect after the next event; post one so the run loop notices now.
    [NSApp postEvent:[NSEvent otherEventWithType:NSEventTypeApplicationDefined location:NSZeroPoint modifierFlags:0
                                       timestamp:0 windowNumber:0 context:nil subtype:0 data1:0 data2:0] atStart:NO];
}

@end

int main(int argc, const char **argv)
{
    @autoreleasepool
    {
        if (argc < 3) return 2;
        NSString *mode = @(argv[1]);
        BOOL print = [mode isEqualToString:@"print"];
        if (!print && !([mode isEqualToString:@"pdf"] || [mode isEqualToString:@"png"])) return 2;
        if (!print && argc < 4) return 2;
        NSString *html = @(argv[2]);
        if (![[NSFileManager defaultManager] fileExistsAtPath:html]) return 1;

        NSApplication *app = [NSApplication sharedApplication];
        // No Dock icon for an export; the print panel needs a regular app to come to the front.
        app.activationPolicy = print ? NSApplicationActivationPolicyRegular : NSApplicationActivationPolicyProhibited;

        Exporter *exporter = [Exporter new];
        exporter.mode = mode;
        exporter.result = 1;
        int extra = print ? 3 : 4;
        double number = argc > extra ? atof(argv[extra]) : 0;
        exporter.margin = (number > 0 && ![mode isEqualToString:@"png"] ? number : 12) / MillimetresPerPoint;
        exporter.width = [mode isEqualToString:@"png"] && number > 0 ? number : 1000;
        if (!print) exporter.output = [NSURL fileURLWithPath:@(argv[3])];
        [exporter startWithHtml:[NSURL fileURLWithPath:html]];
        [app run];
        return exporter.result;
    }
}
