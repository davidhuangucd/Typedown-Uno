#!/bin/bash
# The theme changed while the app runs must look the way it does after a restart. Switched in the settings dialog
# (Light -> Sepia, by the drop-down, as a person does it), the open dialog kept the Light palette and the system blue,
# a dialog opened afterwards showed Sepia's accent until the pointer moved and the system blue after, and the selected
# tab kept the Light theme's colour - each element had looked its theme resources up once and did not again.
#
# Runs the app under Xvfb + xfwm4 in an isolated profile and reads colours at fixed points of the screenshots, so it
# depends on this layout (1400x900 screen, the window at the top left). Needs Xvfb, xfwm4, xdotool, ImageMagick.
#
#   Tools/theme-switch-check.sh [path to Typedown.Uno] [display number]
set -u
UNO=${1:-$(cd "$(dirname "$0")/.." && pwd)/Typedown.Uno/bin/Debug/net9.0-desktop/Typedown.Uno}
D=${2:-66}
T=/tmp/typedown-theme-switch-check
ACCENT=A1683A   # Sepia's accent (Assets/Themes/sepia.css)
BLUE=005A9E     # the system accent a theme without one keeps
pkill -f "[X]vfb :$D " ; sleep 0.5
rm -rf $T && mkdir -p $T/home $T/data/Typedown.Uno $T/run $T/docs && chmod 700 $T/run
printf '{ "FileStartupAction": 2, "Language": "en", "Theme": 1, "CustomTheme": "" }' > $T/data/Typedown.Uno/settings.json
printf '# One\n\nText.\n' > $T/docs/a.md
Xvfb :$D -screen 0 1400x900x24 >/dev/null 2>&1 &
sleep 1
DISPLAY=:$D xfwm4 >/dev/null 2>&1 &
sleep 1
export DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.dotnet} HOME=$T/home XDG_DATA_HOME=$T/data XDG_RUNTIME_DIR=$T/run XDG_CONFIG_HOME=$T/home/.config DISPLAY=:$D
"$UNO" $T/docs/a.md > $T/app.log 2>&1 &
APP=$!
sleep 16
W=$(xdotool search --name "Typedown" | tail -1); xdotool windowactivate --sync $W 2>/dev/null; sleep 0.5
shot() { import -window root $T/$1.png 2>/dev/null; }
px() { convert $T/$1.png -format "%[hex:p{$2,$3}]" info: 2>/dev/null; }
ok=1; check() { if [ "$2" = "$3" ]; then echo "PASS $1 ($2)"; else echo "FAIL $1: $2, expected $3"; ok=0; fi; }

xdotool key ctrl+n; sleep 2
# Settings > Theme: the drop-down, down from Light past Dark, Black, Example, Dracula, Gruvbox Dark, Nord to Sepia.
xdotool mousemove 700 600; sleep 0.3; xdotool key ctrl+comma; sleep 2.5; shot 0-dialog-light
check "the dialog opens in Light with the system accent" "$(px 0-dialog-light 190 113)" $BLUE
xdotool mousemove 818 153 click 1; sleep 1.2
for i in 1 2 3 4 5 6 7; do xdotool key Down; sleep 0.25; done
xdotool key Return; sleep 2.5; xdotool mousemove 700 700; sleep 0.8; shot 1-dialog-sepia
check "the open dialog takes Sepia's accent" "$(px 1-dialog-sepia 190 113)" $ACCENT
check "and Sepia's background" "$(px 1-dialog-sepia 190 155)" F4ECD8
xdotool mousemove 200 154 click 1; sleep 1; xdotool mousemove 700 700; sleep 0.8; shot 2-dialog-second
check "a category selected after the switch has Sepia's accent" "$(px 2-dialog-second 190 155)" $ACCENT
xdotool key Escape; sleep 1.5; xdotool mousemove 700 700; sleep 0.5; shot 3-main
check "the selected tab has Sepia's accent line" "$(px 3-main 300 78)" $ACCENT
check "the tab strip has Sepia's surface" "$(px 3-main 500 78)" ECE2C8
# Opened again after the switch: the accent must stay through a hover and a new selection.
xdotool mousemove 700 600 click 1; sleep 0.8; xdotool key ctrl+comma; sleep 2.5
if [ "$(compare -metric AE $T/3-main.png <(import -window root png:-) null: 2>&1 | cut -d' ' -f1)" -lt 2000 ]; then xdotool key ctrl+comma; sleep 2.5; fi
for y in 0 1 2 3; do xdotool mousemove 200 $((114 + y * 40)); sleep 0.6; done
xdotool mousemove 700 700; sleep 0.8; shot 4-reopened-hovered
check "reopened: the selection keeps Sepia's accent after the pointer passed over the list" "$(px 4-reopened-hovered 190 113)" $ACCENT
xdotool mousemove 200 154 click 1; sleep 1; xdotool mousemove 700 700; sleep 0.8; shot 5-reopened-second
check "reopened: a new selection has Sepia's accent" "$(px 5-reopened-second 190 155)" $ACCENT
kill $APP 2>/dev/null; sleep 1; pkill -f "[X]vfb :$D "
echo "OVERALL $([ $ok = 1 ] && echo PASS || echo FAIL)  (screenshots in $T)"
[ $ok = 1 ]
