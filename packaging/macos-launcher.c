/*
 * Contents/MacOS/Typedown in Typedown.app: replaces itself with the .NET app host in Contents/Resources/app, keeping
 * the process (so LaunchServices, the Dock and the open-documents Apple Event all stay with it) and the arguments.
 *
 * The published app cannot live in Contents/MacOS itself: codesign treats everything there as code, and the .NET
 * runtime's managed assemblies, fonts and Assets are not, so the bundle could not be sealed, and an unsealed bundle
 * whose executable carries the SDK's ad-hoc signature is reported as damaged once downloaded.
 *
 *   clang -O2 -arch arm64 -mmacosx-version-min=12.0 -o Typedown packaging/macos-launcher.c
 */
#include <libgen.h>
#include <limits.h>
#include <mach-o/dyld.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>

int main(int argc, char **argv)
{
    char exe[PATH_MAX], real[PATH_MAX], target[PATH_MAX];
    uint32_t size = sizeof exe;
    (void)argc;
    if (_NSGetExecutablePath(exe, &size) != 0 || !realpath(exe, real)) return 127;
    snprintf(target, sizeof target, "%s/../Resources/app/Typedown.Uno", dirname(real));
    argv[0] = target;
    execv(target, argv);
    perror(target);
    return 127;
}
