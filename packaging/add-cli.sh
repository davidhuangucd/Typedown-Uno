#!/bin/bash
# Adds typedownctl (the automation CLI, and "typedownctl mcp", the MCP server) and the automation documents to a
# published app folder before it is packaged (the Windows repository's Tools/Installer/add-cli.ps1 does the same).
#
# typedownctl is published self-contained for the app's runtime identifier and .NET version, so every runtime file it
# brings is the app's own: a file that is already there must be byte for byte the same, or the build stops rather than
# replace the app's runtime. Then it runs from the app folder, and the documents go to <app>/docs.
#
#   packaging/add-cli.sh <published-app-dir> <rid>      e.g. packaging/add-cli.sh publish/Typedown-linux-x64 linux-x64
set -eu
APP=$(readlink -f "${1:?usage: add-cli.sh <published-app-dir> <rid>}")
RID=${2:?rid}
HERE=$(cd "$(dirname "$0")/.." && pwd)
[ -x "$APP/Typedown.Uno" ] || { echo "add-cli: no Typedown.Uno in $APP" >&2; exit 1; }

OUT=$(mktemp -d)
trap 'rm -rf "$OUT"' EXIT
echo "add-cli: publishing typedownctl for $RID"
dotnet publish "$HERE/Typedown.Cli/Typedown.Cli.csproj" -c Release -r "$RID" --self-contained -o "$OUT" -nologo -v q >/dev/null

added=0; same=0
while IFS= read -r -d '' file; do
  rel=${file#"$OUT"/}
  case "$rel" in *.pdb) continue ;; esac
  target="$APP/$rel"
  if [ ! -e "$target" ]; then
    mkdir -p "$(dirname "$target")"
    cp -p "$file" "$target"
    added=$((added + 1))
  elif cmp -s "$file" "$target"; then
    same=$((same + 1))
  else
    echo "add-cli: typedownctl would replace the app's $rel with a different file - build both with the same .NET SDK" >&2
    exit 1
  fi
done < <(find "$OUT" -type f -print0)
echo "add-cli: $added file(s) added, $same already there and identical"

"$APP/typedownctl" help | grep -q typedownctl || { echo "add-cli: typedownctl does not run from $APP" >&2; exit 1; }
echo "add-cli: typedownctl runs from the app folder"

mkdir -p "$APP/docs"
cp -r "$HERE/docs/automation.md" "$HERE/docs/automation-mcp.md" "$HERE/docs/automation-api-spec.md" \
      "$HERE/docs/automation-schema" "$HERE/docs/automation-examples" "$APP/docs/"
echo "add-cli: documents in $APP/docs"
