#!/bin/bash
# Copy the editor bundle from the Windows repository into Assets/Editor.
#
# Do this with the script, not by hand. The bundle's index.html carries one line that only exists here —
# the tag that loads uno-bridge.js, which is what the host posts messages through — and copying the
# Windows index.html over it drops that line. Nothing then reaches the editor and it stays blank, while
# the app still logs "editor page loaded". That shipped in five packages before anyone noticed.
set -eu
FROM=${1:-../typedown/Dev/Typedown/Resources/Statics}
HERE=$(cd "$(dirname "$0")/.." && pwd)
DEST=$HERE/Typedown.Uno/Assets/Editor

[ -f "$FROM/index.html" ] || { echo "no bundle at $FROM" >&2; exit 1; }

cp -a "$FROM/." "$DEST/"
rm -f "$DEST"/static/js/*.map "$DEST"/static/css/*.map

# Whatever the Windows index.html says, the bridge has to be loaded before the editor, and mermaid only when a
# document draws a diagram (uno-lazy-mermaid.js says why).
python3 - "$DEST/index.html" <<'PY'
import sys
p = sys.argv[1]
s = open(p, encoding='utf-8').read()
eager = '<script src="./mermaid.min.js"></script>'
lazy = '<script src="./uno-lazy-mermaid.js"></script>'
if 'uno-bridge.js' not in s:
    anchor = eager if eager in s else lazy
    if anchor not in s:
        raise SystemExit('index.html does not look like the editor page; add the bridge tag by hand')
    s = s.replace(anchor, anchor + '<script src="./uno-bridge.js"></script>', 1)
    print('  put the uno-bridge tag back')
if eager in s:
    s = s.replace(eager, lazy, 1)
    print('  mermaid loads on demand')
open(p, 'w', encoding='utf-8').write(s)
PY

# Drop bundles the page no longer references.
keep=$(grep -o 'main\.[a-f0-9]*\.\(js\|css\)' "$DEST/index.html" | sort -u)
for f in "$DEST"/static/js/main.*.js "$DEST"/static/css/main.*.css; do
  [ -e "$f" ] || continue
  base=$(basename "$f")
  echo "$keep" | grep -q "^$base$" || { rm -f "$f" "$f.LICENSE.txt"; echo "  removed superseded $base"; }
done

bash "$HERE/packaging/check-editor-assets.sh"
echo "editor assets synced from $FROM"
