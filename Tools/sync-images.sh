#!/bin/bash
# Copy the platform-neutral image upload code from the Windows repository into Typedown.Uno/Services/Images, where
# the single project compiles it: MarkdownImages (a document's image addresses), S3Uploader (Signature V4 upload) and
# UploadHistory (a picture goes up once per configuration). Do not edit the copies here: change them in
# ../typedown/Dev/Typedown.Core, which has the tests, and sync again.
set -eu
FROM=${1:-../typedown}
HERE=$(cd "$(dirname "$0")/.." && pwd)
DEST=$HERE/Typedown.Uno/Services/Images

[ -f "$FROM/Dev/Typedown.Core/Services/S3Uploader.cs" ] || { echo "no image upload code under $FROM" >&2; exit 1; }
rm -rf "$DEST" && mkdir -p "$DEST"
cp "$FROM/Dev/Typedown.Core/Utilities/MarkdownImages.cs" "$FROM/Dev/Typedown.Core/Services/S3Uploader.cs" \
   "$FROM/Dev/Typedown.Core/Services/UploadHistory.cs" "$DEST/"
# UploadHistory writes through SafeFile, which this project has as Typedown.Uno.Services.SafeFile.
sed -i 's/^using Typedown.Core.Utilities;$/using Typedown.Uno.Services;/' "$DEST/UploadHistory.cs"
echo "image upload code synced from $FROM ($(git -C "$FROM" rev-parse --short HEAD))" | tee "$DEST/SYNCED_FROM.txt"
