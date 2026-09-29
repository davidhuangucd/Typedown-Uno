#!/bin/bash
# Checks the authored assets before a build, or a published Assets directory passed as $1.
set -eu
HERE=$(cd "$(dirname "$0")/.." && pwd)
ASSETS=${1:-$HERE/Typedown.Uno/Assets}

bash "$HERE/packaging/check-editor-assets.sh" "$ASSETS/Editor"

designer="$ASSETS/Themes/theme-designer.html"
[ -f "$designer" ] || { echo "packaged theme designer is missing: $designer" >&2; exit 1; }
count=$(grep -o 'id="typedown-theme-catalog"' "$designer" | wc -l)
[ "$count" -eq 1 ] || { echo "theme designer must contain exactly one catalog placeholder" >&2; exit 1; }
echo "application assets look right: editor bridge and offline theme designer are packaged"
