#!/bin/bash
# After the settings dialog is closed with Escape, the keyboard is the editor's again: a letter typed without a click
# reaches the document, and Ctrl+, opens the settings again. The dialog took the keyboard into the app's own window
# and nothing gave it back to the web view's (an override-redirect GTK window the window manager does not focus):
# until the reader clicked, keys went nowhere.
#
# Runs the app under Xvfb + xfwm4 in an isolated profile, reads the text through typedownctl (build Typedown.Cli
# first). Needs Xvfb, xfwm4, xdotool, ImageMagick.
#
#   Tools/focus-after-dialog-check.sh [path to Typedown.Uno] [display number]
set -u
HERE=$(cd "$(dirname "$0")/.." && pwd)
UNO=${1:-$HERE/Typedown.Uno/bin/Debug/net9.0-desktop/Typedown.Uno}
D=${2:-67}
T=/tmp/typedown-focus-check
CTL="${DOTNET:-$HOME/.dotnet/dotnet} $HERE/Typedown.Cli/bin/Debug/net9.0/typedownctl.dll"
pkill -f "[X]vfb :$D " ; sleep 0.5
rm -rf $T && mkdir -p $T/home $T/data/Typedown.Uno $T/run $T/docs && chmod 700 $T/run
printf '{ "AllowLocalAutomation": true, "FileStartupAction": 2, "Language": "en" }' > $T/data/Typedown.Uno/settings.json
printf '# Focus\n\nText\n' > $T/docs/a.md
Xvfb :$D -screen 0 1400x900x24 >/dev/null 2>&1 &
sleep 1
DISPLAY=:$D xfwm4 >/dev/null 2>&1 &
sleep 1
export DOTNET_ROOT=/root/.dotnet HOME=$T/home XDG_DATA_HOME=$T/data XDG_RUNTIME_DIR=$T/run XDG_CONFIG_HOME=$T/home/.config DISPLAY=:$D
"$UNO" $T/docs/a.md > $T/app.log 2>&1 &
APP=$!
SOCK=$T/run/typedown/automation.v1.sock
for i in $(seq 1 40); do [ -S $SOCK ] && break; sleep 0.5; done
sleep 6
ctl() { $CTL --json --endpoint $SOCK "$@"; }
if [ -S $SOCK ] && [ -f ${CTL##* } ]; then
  ID=$(ctl documents | python3 -c 'import json,sys; print(json.load(sys.stdin)["documents"][0]["documentId"])')
  text() { ctl get $ID --latest --text | python3 -c 'import json,sys; print(json.load(sys.stdin)["text"].replace("\n","|"))'; }
else
  # A build without the automation API (main): what the document holds is what Ctrl+S writes - which also needs the
  # keyboard, so a lost keyboard still shows as a missing letter.
  text() { xdotool key ctrl+s; sleep 1.5; tr '\n' '|' < $T/docs/a.md; }
fi
shot() { import -window root $T/$1.png 2>/dev/null; }
W=$(xdotool search --name "Typedown" | tail -1); xdotool windowactivate --sync $W 2>/dev/null; sleep 0.5
# The editor has the keyboard to begin with: a click at the end of the text, a letter.
xdotool mousemove 300 200 click 1; sleep 0.5; xdotool key ctrl+End; xdotool type A; sleep 1
echo "before: $(text)"
ok=1
# Another dialog: the file picker (Ctrl+O), closed with Escape; a letter without a click.
xdotool key ctrl+o; sleep 2.5; xdotool key Escape; sleep 1.5
xdotool type C; sleep 1
T2=$(text); echo "after the file picker, typed C: $T2"
case "$T2" in *C*) echo "PASS the letter reached the document after the file picker";; *) echo "FAIL the letter did not reach the document after the file picker"; ok=0;; esac
# Back in the document by a click, as the reader would be after the first dialog.
xdotool mousemove 300 200 click 1; sleep 0.5; xdotool key ctrl+End
xdotool key ctrl+comma; sleep 2.5; shot 1-open
xdotool key Escape; sleep 1.5; xdotool mousemove 700 650; sleep 0.8; shot 2-closed
# No click from here on.
xdotool type B; sleep 1
T1=$(text); echo "after Escape, typed B: $T1"
case "$T1" in *B*) echo "PASS the letter reached the document";; *) echo "FAIL the letter did not reach the document"; ok=0;; esac
xdotool key ctrl+comma; sleep 2.5; xdotool mousemove 690 640; sleep 0.8; shot 3-reopen
if [ "$(compare -metric AE $T/2-closed.png $T/3-reopen.png null: 2>&1 | cut -d' ' -f1)" -gt 20000 ]; then echo "PASS Ctrl+, opened the settings again"; else echo "FAIL Ctrl+, did nothing"; ok=0; fi
# The X server goes only after the app has exited. A SIGTERM can end in a segfault in Uno's render thread - Mesa's
# software GL still compiling a shader while exit() tears LLVM down - and an X server taken away mid-exit makes
# that far likelier.
kill $APP; for i in $(seq 1 30); do kill -0 $APP 2>/dev/null || break; sleep 0.5; done; pkill -f "[X]vfb :$D "
echo "OVERALL $([ $ok = 1 ] && echo PASS || echo FAIL)"
[ $ok = 1 ]
