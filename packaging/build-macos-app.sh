#!/bin/bash
# Wraps a published macOS folder in Typedown.app, so that Finder, Launchpad and Spotlight know the program, it shows
# up under "Open With" for Markdown files and can be made their default app. The published files go to
# Contents/Resources/app unchanged and Contents/MacOS/Typedown is a small launcher that execs the app host there
# (packaging/macos-launcher.c says why). The bundle declares the Markdown document types (the app takes files opened
# from Finder through Services/MacOpenDocuments) and is sealed with an ad-hoc signature; without a Developer ID,
# Gatekeeper still asks on first run (right-click -> Open), as it does for the plain folder.
# Needs clang (Xcode command line tools), sips, iconutil and codesign.
#
#   ./packaging/build-macos-app.sh <published-dir> <version> [output-dir]   -> <output-dir>/Typedown.app
set -eu

PUBLISH=${1:?usage: build-macos-app.sh <published-dir> <version> [out]}
VERSION=${2:?version}
OUT=${3:-dist}
[ -x "$PUBLISH/Typedown.Uno" ] || { echo "build-macos-app: no Typedown.Uno in $PUBLISH" >&2; exit 1; }
bash "$(dirname "$0")/check-app-assets.sh" "$PUBLISH/Assets"
mkdir -p "$OUT"
APP="$(cd "$OUT" && pwd)/Typedown.app"
# CFBundleShortVersionString takes numbers only; a CI build without a tag passes 0.0.0-<date>
SHORT=$(printf '%s' "$VERSION" | sed -E 's/^([0-9]+(\.[0-9]+){0,2}).*/\1/')
[[ "$SHORT" =~ ^[0-9] ]] || SHORT=0.0.0

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources/app"
cp -R "$PUBLISH/." "$APP/Contents/Resources/app/"
# Inside a bundle Uno looks ms-appx:/// files up under <app folder>/Resources (MacSkiaHost sets
# StorageFile.ResourcePathBase to InstalledPath + "/Resources"), where nothing is: the Fluent symbol font and
# Open Sans did not load, and every icon drew as "?". Links to the app folder's own directories put them there.
[ -e "$APP/Contents/Resources/app/Resources" ] && { echo "build-macos-app: the published folder has a Resources entry" >&2; exit 1; }
mkdir "$APP/Contents/Resources/app/Resources"
for dir in "$APP/Contents/Resources/app"/*/; do
  name=$(basename "$dir")
  [ "$name" = Resources ] || ln -s "../$name" "$APP/Contents/Resources/app/Resources/$name"
done

echo "==> launcher"
clang -O2 -Wall -arch arm64 -mmacosx-version-min=12.0 -o "$APP/Contents/MacOS/Typedown" "$(dirname "$0")/macos-launcher.c"
echo "==> print and export helper"
clang -O2 -Wall -fobjc-arc -arch arm64 -mmacosx-version-min=12.0 -framework AppKit -framework WebKit \
  -o "$APP/Contents/MacOS/typedown-webkit-export" "$(dirname "$0")/macos-webkit-export.m"

echo "==> icon"
ICONSET=$(mktemp -d)/Typedown.iconset
mkdir -p "$ICONSET"
for size in 16 32 128 256; do
  sips -z $size $size "$PUBLISH/Assets/typedown.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  [ $size -lt 256 ] && sips -z $((size * 2)) $((size * 2)) "$PUBLISH/Assets/typedown.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/Typedown.icns"
rm -rf "$(dirname "$ICONSET")"

echo "==> Info.plist"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Typedown</string>
  <key>CFBundleDisplayName</key><string>Typedown</string>
  <key>CFBundleIdentifier</key><string>uk.mingdan.typedown.uno</string>
  <key>CFBundleExecutable</key><string>Typedown</string>
  <key>CFBundleIconFile</key><string>Typedown</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$SHORT</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.productivity</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>CFBundleDocumentTypes</key>
  <array>
    <dict>
      <key>CFBundleTypeName</key><string>Markdown document</string>
      <key>CFBundleTypeRole</key><string>Editor</string>
      <key>LSHandlerRank</key><string>Default</string>
      <key>LSItemContentTypes</key><array><string>net.daringfireball.markdown</string></array>
    </dict>
  </array>
  <!-- Declares the Markdown type itself, for Macs where no other installed app does. -->
  <key>UTImportedTypeDeclarations</key>
  <array>
    <dict>
      <key>UTTypeIdentifier</key><string>net.daringfireball.markdown</string>
      <key>UTTypeDescription</key><string>Markdown document</string>
      <key>UTTypeConformsTo</key><array><string>public.plain-text</string></array>
      <key>UTTypeTagSpecification</key>
      <dict>
        <key>public.filename-extension</key>
        <array><string>md</string><string>markdown</string><string>mdown</string><string>mkd</string><string>mkdn</string></array>
        <key>public.mime-type</key><array><string>text/markdown</string></array>
      </dict>
    </dict>
  </array>
</dict>
</plist>
PLIST
plutil -lint "$APP/Contents/Info.plist" >/dev/null

echo "==> ad-hoc signature"
codesign --force --sign - "$APP"
codesign --verify --strict "$APP"

echo "built $APP ($VERSION)"
