#!/bin/bash
# File > Upload local images on Linux, with real keys: each local picture goes up once (a command of the person's own
# here; the S3 path is the Windows edition's code, tested there against a real bucket), every use of it changes to its
# address, a web image, a missing file and code stay; an undo and a second run take every address from the upload
# history without calling the command again.
#
# Runs the app under Xvfb + xfwm4 in an isolated profile, with a shortcut bound to Upload local images. Needs Xvfb,
# xfwm4, xdotool. Build first (dotnet build Typedown.Uno.sln).
set -u
HERE=$(cd "$(dirname "$0")/.." && pwd)
APP=${APP:-$HERE/Typedown.Uno/bin/Debug/net9.0-desktop/Typedown.Uno}
D=${DISPLAY_NUMBER:-71}
T=$(mktemp -d /tmp/typedown-upload-check.XXXXXX)
fail() { echo "FAIL: $*"; echo "--- app log:"; tail -15 "$T/app.log" 2>/dev/null; cleanup; exit 1; }
cleanup() { [ -n "${PID:-}" ] && kill $PID 2>/dev/null; sleep 1; pkill -f "[X]vfb :$D " 2>/dev/null; rm -rf "$T"; }

mkdir -p $T/home $T/data/Typedown.Uno $T/run $T/docs/pics $T/up && chmod 700 $T/run
printf '\x89PNG\r\n\x1a\n\x01' > "$T/docs/pics/a.png"
printf '\x89PNG\r\n\x1a\n\x02' > "$T/docs/pics/b b.png"
printf '\x89PNG\r\n\x1a\n\x01' > "$T/docs/pics/a-copy.png"   # a.png's picture under another name
cat > $T/docs/doc.md <<'MD'
# Upload

![a](pics/a.png)

![b](pics/b%20b.png)

![copy](pics/a-copy.png)

![web](https://example.com/x.png)

![missing](pics/missing.png)

`![code](pics/a.png)`
MD
ORIGINAL=$(cat $T/docs/doc.md)
COMMAND="echo \"\$1\" >> $T/up/calls.log; echo \"https://img.test/\$(basename \"\$1\" | tr ' ' '-')\""
python3 - "$T/data/Typedown.Uno/settings.json" "$COMMAND" <<'PY'
import json, sys
json.dump({"AllowLocalAutomation": False, "FileStartupAction": 2, "Language": "en", "ImageUploadMethod": 2,
           "ImageUploadCommand": sys.argv[2], "Shortcuts": {"Overrides": {"UploadLocalImages": "Ctrl+Alt+U"}}}, open(sys.argv[1], "w"))
PY

Xvfb :$D -screen 0 1200x800x24 >/dev/null 2>&1 & sleep 1
DISPLAY=:$D xfwm4 >/dev/null 2>&1 & sleep 1
# A debug build runs on the .NET that dotnet build used; the isolated HOME hides ~/.dotnet.
export DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.dotnet}
export HOME=$T/home XDG_DATA_HOME=$T/data XDG_RUNTIME_DIR=$T/run XDG_CONFIG_HOME=$T/home/.config DISPLAY=:$D
"$APP" $T/docs/doc.md > $T/app.log 2>&1 & PID=$!
sleep 14
W=$(xdotool search --name "Typedown" | head -1); [ -n "$W" ] || fail "no window"
xdotool windowactivate --sync $W; sleep 0.5; xdotool mousemove 400 300 click 1; sleep 0.5

run_upload() { xdotool key ctrl+alt+u; sleep 4; xdotool key Return; sleep 1; xdotool key ctrl+s; sleep 1.5; }

run_upload
calls=$(wc -l < $T/up/calls.log 2>/dev/null || echo 0)
TEXT=$(cat $T/docs/doc.md)
EXPECTED=$(printf '%s' "$ORIGINAL" | sed -e 's|](pics/a.png)|](https://img.test/a.png)|' -e 's|](pics/b%20b.png)|](https://img.test/b-b.png)|' -e 's|](pics/a-copy.png)|](https://img.test/a.png)|' -e 's|`!\[code\](https://img.test/a.png)`|`![code](pics/a.png)`|')
[ "$calls" = 2 ] || fail "the command ran $calls times, not once per picture (2)"
[ "$TEXT" = "$EXPECTED" ] || { diff <(echo "$EXPECTED") <(echo "$TEXT"); fail "the saved text is not the expected one"; }
echo "PASS: two pictures uploaded once each, every use replaced, web, missing and code left alone"

xdotool key ctrl+z; sleep 0.8
run_upload
calls=$(wc -l < $T/up/calls.log)
[ "$calls" = 2 ] || fail "after undo the command ran again ($calls calls): the history was not used"
[ "$(cat $T/docs/doc.md)" = "$EXPECTED" ] || fail "after undo and a second run the text differs"
echo "PASS: undone and run again, every address from the upload history, the command not called"
cleanup
