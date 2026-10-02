#!/bin/bash
# reveal: "change" scrolls a change made off screen into view; "document" leaves the page where it was. The page code is
# the Windows editor bundle (checked there by E2E RV01, which reads the page's scroll position); this checks that the
# Uno host hands the request to the page, by what the screen shows: with the page at the top of a long document, a
# "document" write at the end leaves the screen as it was, a "change" write to the same place changes it.
#
# Runs the app under Xvfb + xfwm4 in an isolated profile through docs/automation-examples/typedown_client.py. Needs
# Xvfb, xfwm4, ImageMagick (import, compare), python3.
#
#   Tools/reveal-change-check.sh [path to Typedown.Uno] [display number]
set -u
HERE=$(cd "$(dirname "$0")/.." && pwd)
UNO=${1:-$HERE/Typedown.Uno/bin/Debug/net9.0-desktop/Typedown.Uno}
D=${2:-66}
T=/tmp/typedown-reveal-change-check
pkill -f "[X]vfb :$D " ; sleep 0.5
rm -rf $T && mkdir -p $T/home $T/data/Typedown.Uno $T/run $T/docs && chmod 700 $T/run
printf '{ "AllowLocalAutomation": true, "FileStartupAction": 2, "Language": "en" }' > $T/data/Typedown.Uno/settings.json
python3 -c "
print('# Reveal\n')
for i in range(1, 121): print(f'Paragraph {i} of the long document.\n')
print('Last paragraph.')" > $T/docs/long.md
Xvfb :$D -screen 0 1400x900x24 >/dev/null 2>&1 &
sleep 1
DISPLAY=:$D xfwm4 >/dev/null 2>&1 &
sleep 1
export DOTNET_ROOT=/root/.dotnet HOME=$T/home XDG_DATA_HOME=$T/data XDG_RUNTIME_DIR=$T/run XDG_CONFIG_HOME=$T/home/.config DISPLAY=:$D
"$UNO" $T/docs/long.md > $T/app.log 2>&1 &
APP=$!
SOCK=$T/run/typedown/automation.v1.sock
for i in $(seq 1 40); do [ -S $SOCK ] && break; sleep 0.5; done
sleep 5

shot() { import -display :$D -window root $T/$1.png; }
# One replaceText through the API, then the highlight (2 s) and the title's "... edited ..." notice (about 4 s) run out
# before the next picture.
write() {
  python3 - "$HERE/docs/automation-examples" "$SOCK" "$@" <<'PY'
import sys, time
sys.path.insert(0, sys.argv[1])
from typedown_client import Client
c = Client.connect(socket_path=sys.argv[2])
c.initialize(["document.read", "document.write", "window.focus"], name="reveal-change-check")
doc = c.call("document.list")["documents"][0]["documentId"]
revision = c.call("document.get", {"documentId": doc, "consistency": "latest"})["revision"]
c.call("document.replaceText", {"documentId": doc, "baseRevision": revision, "find": sys.argv[3],
                                "replacement": sys.argv[4], "expectedCount": 1, "reveal": sys.argv[5]})
time.sleep(5)
PY
}
shot before
write "Last paragraph." "Last paragraph, A." document
shot document
write "Last paragraph, A." "Last paragraph, B." change
shot change

diff() { compare -metric AE -fuzz 5% $T/$1.png $T/$2.png null: 2>&1 | cut -d' ' -f1; }
SAME=$(diff before document)
MOVED=$(diff document change)
echo "pixels changed: by the \"document\" write $SAME, by the \"change\" write $MOVED (pictures in $T)"
kill $APP 2>/dev/null; sleep 1; pkill -f "[X]vfb :$D "
FAIL=0
[ "${SAME%.*}" -lt 5000 ] || { echo "FAIL a \"document\" write at the end moved what is on screen"; FAIL=1; }
[ "${MOVED%.*}" -gt 20000 ] || { echo "FAIL a \"change\" write at the end did not bring it on screen"; FAIL=1; }
[ $FAIL = 0 ] && echo "PASS reveal: \"change\" scrolls to the change, \"document\" does not"
exit $FAIL
