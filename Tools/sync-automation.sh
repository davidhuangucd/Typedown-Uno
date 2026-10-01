#!/bin/bash
# Copy the platform-neutral automation library (protocol, errors, framing, registry, edit coordinator, Unix socket)
# from the Windows repository into Typedown.Uno/Automation/Protocol, where the single project compiles it. Do not
# edit the copies here: change them in ../typedown/Dev/Typedown.Automation, which has the tests, and sync again.
set -eu
FROM=${1:-../typedown}
HERE=$(cd "$(dirname "$0")/.." && pwd)
DEST=$HERE/Typedown.Uno/Automation/Protocol

[ -f "$FROM/Dev/Typedown.Automation/AutomationSession.cs" ] || { echo "no automation library under $FROM" >&2; exit 1; }
rm -rf "$DEST" && mkdir -p "$DEST/Unix"
cp "$FROM"/Dev/Typedown.Automation/*.cs "$DEST/"
cp "$FROM"/Dev/Typedown.Automation/Unix/*.cs "$DEST/Unix/"
mkdir -p "$HERE/docs/automation-fixtures"
cp "$FROM"/docs/automation-fixtures/*.json "$HERE/docs/automation-fixtures/"

# typedownctl (the automation CLI, and "typedownctl mcp", the MCP server): its sources, built here by Typedown.Cli,
# and the documents the packages carry beside it.
CLI=$HERE/Typedown.Cli
rm -rf "$CLI/Cli" "$CLI/Mcp" && mkdir -p "$CLI/Cli" "$CLI/Mcp"
cp "$FROM"/Tools/Typedown.Cli/*.cs "$CLI/Cli/"
cp "$FROM"/Tools/Typedown.Mcp/*.cs "$CLI/Mcp/"
rm -rf "$HERE/docs/automation-schema" "$HERE/docs/automation-examples"
mkdir -p "$HERE/docs/automation-schema" "$HERE/docs/automation-examples"
cp "$FROM"/docs/automation.md "$FROM"/docs/automation-mcp.md "$FROM"/docs/automation-api-spec.md "$HERE/docs/"
cp "$FROM"/docs/automation-schema/v1.json "$HERE/docs/automation-schema/"
cp "$FROM"/docs/automation-examples/*.py "$FROM"/docs/automation-examples/*.ps1 "$FROM"/docs/automation-examples/*.md "$HERE/docs/automation-examples/"
echo "automation library synced from $FROM ($(git -C "$FROM" rev-parse --short HEAD))" | tee "$DEST/SYNCED_FROM.txt"
