#!/bin/bash
# Builds the Linux distribution formats from a published folder:
#   tar.gz   portable, extract and run
#   .deb     Debian/Ubuntu package (declares the two runtime dependencies)
#   AppImage single executable (needs appimagetool; skipped when it is missing)
#
#   ./packaging/build-linux-packages.sh <published-dir> <version> [output-dir]
set -e

PUBLISH=${1:?usage: build-linux-packages.sh <published-dir> <version> [out]}
VERSION=${2:?version}
OUT=${3:-dist}
NAME=typedown
# Check the files that will actually be archived, rather than only their source copies.
bash "$(dirname "$0")/check-app-assets.sh" "$PUBLISH/Assets"
mkdir -p "$OUT"
PUBLISH=$(readlink -f "$PUBLISH")
OUT=$(readlink -f "$OUT")

echo "==> tar.gz"
tar -czf "$OUT/Typedown-linux-x64-$VERSION.tar.gz" -C "$PUBLISH" .

# ---- .deb -------------------------------------------------------------------------------------------------
echo "==> deb"
ROOT=$(mktemp -d)
install -d "$ROOT/DEBIAN" "$ROOT/opt/$NAME" "$ROOT/usr/bin" "$ROOT/usr/share/applications" "$ROOT/usr/share/icons/hicolor/256x256/apps" "$ROOT/usr/share/doc/$NAME"
cp -r "$PUBLISH"/. "$ROOT/opt/$NAME/"
chmod +x "$ROOT/opt/$NAME/Typedown.Uno"
[ -f "$PUBLISH/Assets/typedown.png" ] && cp "$PUBLISH/Assets/typedown.png" "$ROOT/usr/share/icons/hicolor/256x256/apps/$NAME.png"
# The theme document belongs where a Debian user looks for documentation as well as next to the themes.
[ -f "$PUBLISH/Assets/Themes/custom-theme.md" ] && cp "$PUBLISH/Assets/Themes/custom-theme.md" "$ROOT/usr/share/doc/$NAME/custom-theme.md"
[ -f "$PUBLISH/LICENSE" ] && cp "$PUBLISH/LICENSE" "$ROOT/usr/share/doc/$NAME/LICENSE"
[ -f "$PUBLISH/THIRD-PARTY-NOTICES.md" ] && cp "$PUBLISH/THIRD-PARTY-NOTICES.md" "$ROOT/usr/share/doc/$NAME/THIRD-PARTY-NOTICES.md"
[ -f "$PUBLISH/PRIVACY.md" ] && cp "$PUBLISH/PRIVACY.md" "$ROOT/usr/share/doc/$NAME/PRIVACY.md"

cat > "$ROOT/usr/bin/$NAME" <<'LAUNCH'
#!/bin/sh
# Uno's GTK web view binds unversioned SONAMEs (libgdk-3.so, libsoup-3.0.so, libwebkit2gtk-4.1.so…), which only
# the -dev packages provide. Rather than require those, link the versioned libraries into a per-user directory
# and put it on the loader path — no root, works the same from the deb, the AppImage and the tarball.
libdir="${XDG_CACHE_HOME:-$HOME/.cache}/typedown/lib"
mkdir -p "$libdir" 2>/dev/null
for base in libwebkit2gtk-4.1 libjavascriptcoregtk-4.1 libgdk-3 libgtk-3 libsoup-3.0 libcairo libpango-1.0 libpangocairo-1.0 libgdk_pixbuf-2.0 libatk-1.0 libgio-2.0 libglib-2.0 libgobject-2.0; do
  [ -e "$libdir/$base.so" ] && continue
  target=$(ldconfig -p 2>/dev/null | awk -v pat="^$base\\.so\\.[0-9]+$" '$1 ~ pat { print $NF; exit }')
  [ -n "$target" ] && ln -sf "$target" "$libdir/$base.so" 2>/dev/null
done
export LD_LIBRARY_PATH="$libdir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
# The GTK web view needs X11 even in a Wayland session.
export GDK_BACKEND=x11
exec /opt/typedown/Typedown.Uno "$@"
LAUNCH
chmod +x "$ROOT/usr/bin/$NAME"

cat > "$ROOT/usr/share/applications/$NAME.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Typedown
Comment=Markdown editor
Exec=/usr/bin/$NAME %f
Icon=$NAME
Terminal=false
Categories=Office;TextEditor;
MimeType=text/markdown;text/x-markdown;
DESKTOP

cat > "$ROOT/DEBIAN/control" <<CONTROL
Package: $NAME
Version: $VERSION
Section: editors
Priority: optional
Architecture: amd64
Depends: libgtk-3-0 | libgtk-3-0t64, libwebkit2gtk-4.1-0, libx11-6
Maintainer: Typedown Community
Description: Typedown (Uno Platform edition)
 Cross-platform Markdown editor with the Typedown/MarkText editing engine.
 The .NET runtime is bundled; only GTK 3 and WebKitGTK 4.1 come from the system.
CONTROL

fakeroot dpkg-deb --build "$ROOT" "$OUT/typedown_${VERSION}_amd64.deb" >/dev/null
rm -rf "$ROOT"

# ---- AppImage ---------------------------------------------------------------------------------------------
if command -v appimagetool >/dev/null; then
  echo "==> AppImage"
  APPDIR=$(mktemp -d)/Typedown.AppDir
  install -d "$APPDIR/usr/bin" "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/256x256/apps"
  cp -r "$PUBLISH"/. "$APPDIR/usr/bin/"
  chmod +x "$APPDIR/usr/bin/Typedown.Uno"
  [ -f "$PUBLISH/Assets/typedown.png" ] && cp "$PUBLISH/Assets/typedown.png" "$APPDIR/$NAME.png" && cp "$PUBLISH/Assets/typedown.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/$NAME.png"
  cat > "$APPDIR/AppRun" <<'APPRUN'
#!/bin/sh
# Uno's GTK web view binds unversioned SONAMEs (libgdk-3.so, libsoup-3.0.so, libwebkit2gtk-4.1.so…), which only
# the -dev packages provide. Rather than require those, link the versioned libraries into a per-user directory
# and put it on the loader path — no root, works the same from the deb, the AppImage and the tarball.
libdir="${XDG_CACHE_HOME:-$HOME/.cache}/typedown/lib"
mkdir -p "$libdir" 2>/dev/null
for base in libwebkit2gtk-4.1 libjavascriptcoregtk-4.1 libgdk-3 libgtk-3 libsoup-3.0 libcairo libpango-1.0 libpangocairo-1.0 libgdk_pixbuf-2.0 libatk-1.0 libgio-2.0 libglib-2.0 libgobject-2.0; do
  [ -e "$libdir/$base.so" ] && continue
  target=$(ldconfig -p 2>/dev/null | awk -v pat="^$base\\.so\\.[0-9]+$" '$1 ~ pat { print $NF; exit }')
  [ -n "$target" ] && ln -sf "$target" "$libdir/$base.so" 2>/dev/null
done
export LD_LIBRARY_PATH="$libdir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
# The GTK web view needs X11 even in a Wayland session.
export GDK_BACKEND=x11
HERE=$(dirname "$(readlink -f "$0")")
exec "$HERE/usr/bin/Typedown.Uno" "$@"
APPRUN
  chmod +x "$APPDIR/AppRun"
  cat > "$APPDIR/$NAME.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Typedown
Exec=Typedown.Uno %f
Icon=$NAME
Categories=Office;TextEditor;
Terminal=false
MimeType=text/markdown;text/x-markdown;
DESKTOP
  cp "$APPDIR/$NAME.desktop" "$APPDIR/usr/share/applications/"
  # A failing appimagetool (no libfuse2, no network…) must not take the other packages down with it.
  if ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 appimagetool "$APPDIR" "$OUT/Typedown-$VERSION-x86_64.AppImage" >/dev/null 2>&1; then
    echo "    AppImage built"
  else
    echo "    AppImage build failed, continuing without it" >&2
  fi
  rm -rf "$(dirname "$APPDIR")"
else
  echo "==> AppImage skipped (appimagetool not installed)"
fi

ls -la "$OUT"
