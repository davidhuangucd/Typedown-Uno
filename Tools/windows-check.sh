#!/bin/bash
# Two windows and dialogs, as the reader meets them:
#  - a second window opened: the first keeps its editor. Uno detached the first window's web view (unmapped, moved
#    to the root) when the second drew its own, and nothing attached it again: after the second window closed, keys
#    and clicks in the first went nowhere.
#  - back in the first window without a click: a letter reaches its document.
#  - the settings dialog open: a letter does not reach the document under it, and Escape closes the dialog. The
#    keyboard stayed in the web view's X window when the dialog opened: letters changed the document, Escape never
#    reached the dialog.
#
# Runs the app under Xvfb + xfwm4 in an isolated profile, reads the text through typedownctl (build Typedown.Cli
# first). Needs Xvfb, xfwm4, xdotool, xwininfo, python3.
#
#   Tools/windows-check.sh [path to Typedown.Uno] [display number]
set -u
HERE=$(cd "$(dirname "$0")/.." && pwd)
UNO=${1:-$HERE/Typedown.Uno/bin/Debug/net9.0-desktop/Typedown.Uno}
D=${2:-66}
T=/tmp/typedown-windows-check
CTL="${DOTNET:-$HOME/.dotnet/dotnet} $HERE/Typedown.Cli/bin/Debug/net9.0/typedownctl.dll"
pkill -f "[X]vfb :$D " ; sleep 0.5
rm -rf $T && mkdir -p $T/home $T/data/Typedown.Uno $T/run $T/docs && chmod 700 $T/run
printf '{ "AllowLocalAutomation": true, "FileStartupAction": 2, "Language": "en" }' > $T/data/Typedown.Uno/settings.json
printf '# Windows\n\nText\n' > $T/docs/a.md
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
ID=$(ctl documents | python3 -c 'import json,sys; print(json.load(sys.stdin)["documents"][0]["documentId"])')
text() { ctl get $ID --latest --text | python3 -c 'import json,sys; print(json.load(sys.stdin)["text"].replace("\n","|"))'; }
ok=1; pass() { echo "PASS $1"; }; fail() { echo "FAIL $1"; ok=0; }
wins() { xdotool search --onlyvisible --name " - Typedown" 2>/dev/null | sort; }
# The web views' own X windows, each with its map state.
views() { for x in $(xwininfo -root -tree | grep -o '0x[0-9a-f]* "Uno WebView' | cut -d' ' -f1); do xwininfo -id $x | grep -o 'IsViewable\|IsUnMapped\|IsUnviewable'; done | sort | uniq -c | tr -s ' ' | tr '\n' ','; }
# As the close button does: the window manager asks the window to close (WM_DELETE_WINDOW).
close() { python3 - "$1" <<'PY'
import ctypes, sys
x = ctypes.cdll.LoadLibrary("libX11.so.6")
x.XOpenDisplay.restype = ctypes.c_void_p
x.XInternAtom.restype = ctypes.c_ulong
x.XInternAtom.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int]
d = x.XOpenDisplay(None)
w = int(sys.argv[1], 0)
class Ev(ctypes.Structure):
    _fields_ = [("type", ctypes.c_int), ("serial", ctypes.c_ulong), ("send_event", ctypes.c_int), ("display", ctypes.c_void_p),
                ("window", ctypes.c_ulong), ("message_type", ctypes.c_ulong), ("format", ctypes.c_int), ("data", ctypes.c_long * 5), ("pad", ctypes.c_long * 12)]
e = Ev(type=33, window=w, message_type=x.XInternAtom(d, b"WM_PROTOCOLS", 0), format=32)
e.data[0] = x.XInternAtom(d, b"WM_DELETE_WINDOW", 0)
x.XSendEvent.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_int, ctypes.c_long, ctypes.c_void_p]
x.XSendEvent(d, w, 0, 0, ctypes.byref(e))
x.XFlush(d)
PY
}
W1=$(wins | head -1); xdotool windowactivate --sync $W1 2>/dev/null; sleep 0.5

echo "== a second window opened"
xdotool key ctrl+shift+n; sleep 8
W2=$(comm -13 <(echo "$W1") <(wins) | head -1)
v=$(views); echo "   web views: $v"
case "$v" in *2\ IsViewable*) pass "both windows show their editor";; *) fail "a window lost its editor";; esac
close $W2; sleep 3
xdotool windowactivate --sync $W1 2>/dev/null; sleep 1.5
xdotool type K; sleep 1
case "$(text)" in *K*) pass "back in the first window, a letter typed without a click reached the document";; *) fail "back in the first window, a letter went nowhere ($(text))";; esac

echo "== the settings dialog"
before=$(text)
xdotool key ctrl+comma; sleep 2.5
xdotool type Z; sleep 1
[ "$(text)" = "$before" ] && pass "a letter typed with the dialog open did not reach the document" || fail "a letter typed with the dialog open changed the document ($(text))"
xdotool key Escape; sleep 1.5
xdotool type E; sleep 1
case "$(text)" in *E*) pass "Escape closed the dialog, and the next letter reached the document";; *) fail "after Escape the letter went nowhere - the dialog still open? ($(text))";; esac

kill $APP; sleep 1; pkill -f "[X]vfb :$D "
echo "OVERALL $([ $ok = 1 ] && echo PASS || echo FAIL)"
[ $ok = 1 ]
