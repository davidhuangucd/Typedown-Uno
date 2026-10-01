#!/bin/bash
# settingsRevision moves with the exposed settings only. Changing the view (mode, side pane, status bar, bounds),
# opening another file (the recent files) and moving the window change what the app keeps for itself, not a
# setting a client can set: the revision stays, and a settings.set made with the revision read before them goes
# through. It used to count every AppSettings change, and that set was refused as stale.
#
# Runs the app under Xvfb + xfwm4 in an isolated profile through typedownctl (build Typedown.Cli first). Needs Xvfb,
# xfwm4, xdotool, python3.
#
#   Tools/settings-revision-check.sh [path to Typedown.Uno] [display number]
set -u
HERE=$(cd "$(dirname "$0")/.." && pwd)
UNO=${1:-$HERE/Typedown.Uno/bin/Debug/net9.0-desktop/Typedown.Uno}
D=${2:-65}
T=/tmp/typedown-settings-revision-check
CTL="${DOTNET:-$HOME/.dotnet/dotnet} $HERE/Typedown.Cli/bin/Debug/net9.0/typedownctl.dll"
pkill -f "[X]vfb :$D " ; sleep 0.5
rm -rf $T && mkdir -p $T/home $T/data/Typedown.Uno $T/run $T/docs && chmod 700 $T/run
printf '{ "AllowLocalAutomation": true, "FileStartupAction": 2, "Language": "en" }' > $T/data/Typedown.Uno/settings.json
printf '# One\n\nText\n' > $T/docs/a.md
printf '# Two\n\nText\n' > $T/docs/b.md
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
ok=1; pass() { echo "PASS $1"; }; fail() { echo "FAIL $1"; ok=0; }
rev() { ctl settings get editor.fontSize | python3 -c 'import json,sys; print(json.load(sys.stdin)["settingsRevision"])'; }
W=$(ctl windows | python3 -c 'import json,sys; print(json.load(sys.stdin)["windows"][0]["windowId"])')
before=$(rev)
case "$before" in ''|*[!0-9]*) echo "FAIL no settingsRevision read (typedownctl built? the app up?)"; kill $APP; pkill -f "[X]vfb :$D "; echo "OVERALL FAIL"; exit 1;; esac
ctl view $W --mode source --side-pane outline --status-bar off >/dev/null
ctl view $W --bounds 60,40,1000,700 >/dev/null
ctl open $T/docs/b.md >/dev/null
X=$(xdotool search --onlyvisible --name " - Typedown" | head -1); xdotool windowmove $X 120 80; sleep 0.5; xdotool windowsize $X 900 650
ctl view $W --mode visual --side-pane closed --status-bar on >/dev/null
sleep 3
after=$(rev)
echo "   settingsRevision $before -> $after"
[ "$before" = "$after" ] && pass "view changes, another file and a moved window leave settingsRevision as it was" || fail "settingsRevision moved without a setting changing"
set=$(ctl settings set editor.fontSize 19 --base-revision $before)
echo "$set" | grep -q '"operationId"' && pass "a settings.set with the revision read before them goes through" || fail "the settings.set was refused: $set"
now=$(rev)
[ "$now" -gt "$before" ] && pass "and a setting changed advances it ($before -> $now)" || fail "a setting changed and the revision did not move ($before -> $now)"
kill $APP; for i in $(seq 1 30); do kill -0 $APP 2>/dev/null || break; sleep 0.5; done; pkill -f "[X]vfb :$D "
echo "OVERALL $([ $ok = 1 ] && echo PASS || echo FAIL)"
[ $ok = 1 ]
