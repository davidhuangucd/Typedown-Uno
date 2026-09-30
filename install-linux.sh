#!/bin/bash
# System-wide install of the Typedown (Uno) Linux build. Run as root:
#   sudo ./install-linux.sh [Typedown-linux-x64.tar.gz]
# Installs to /opt/typedown-uno, adds /usr/local/bin/typedown and a desktop entry, and creates the unversioned
# library symlinks Uno's GTK web view needs (see below).
set -e
mkdir -p /opt/typedown-uno
tar -xzf "${1:-/tmp/Typedown-linux-x64.tar.gz}" -C /opt/typedown-uno
chmod +x /opt/typedown-uno/Typedown.Uno

# The launcher links the unversioned SONAMEs into a per-user directory, so no /usr/lib changes are needed.

cat > /usr/local/bin/typedown <<'EOF'
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
exec /opt/typedown-uno/Typedown.Uno "$@"
EOF
chmod +x /usr/local/bin/typedown
if [ -x /opt/typedown-uno/typedownctl ]; then
  printf '#!/bin/sh\nexec /opt/typedown-uno/typedownctl "$@"\n' > /usr/local/bin/typedownctl
  chmod +x /usr/local/bin/typedownctl
fi

# The plain artwork, the same file the deb and the AppImage install; the generated Assets/Icons set is only
# a fallback, and picking from it alphabetically can land on a 16px variant.
icon=/opt/typedown-uno/Assets/typedown.png
[ -f "$icon" ] || icon=$(ls /opt/typedown-uno/Assets/Icons/icon.scale-400.png /opt/typedown-uno/Assets/Icons/icon.png 2>/dev/null | head -1)
cat > /usr/share/applications/typedown-uno.desktop <<EOF
[Desktop Entry]
Type=Application
Name=Typedown
Comment=Markdown editor
Exec=/usr/local/bin/typedown %f
Icon=${icon:-accessories-text-editor}
Terminal=false
Categories=Office;TextEditor;
MimeType=text/markdown;text/x-markdown;
EOF
update-desktop-database /usr/share/applications 2>/dev/null || true

echo "installed: $(du -sh /opt/typedown-uno | cut -f1) in /opt/typedown-uno"
echo "run it with: typedown [file.md]"
