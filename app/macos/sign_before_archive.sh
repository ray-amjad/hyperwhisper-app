#!/bin/bash

# Pre-Archive Signing Script for Whisper Framework
# Run this script before archiving the app for distribution
# This ensures the whisper.xcframework is properly signed with your Developer ID

echo "Pre-signing whisper.xcframework for distribution..."

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Override with: export WHISPER_FRAMEWORK="/path/to/whisper.framework"
FRAMEWORK_PATH="${WHISPER_FRAMEWORK:-$SCRIPT_DIR/whisper.xcframework/macos-arm64_x86_64/whisper.framework}"
# Override with your own identity: export SIGNING_IDENTITY="Developer ID Application: Your Name (TEAMID)"
SIGNING_IDENTITY="${SIGNING_IDENTITY:-Developer ID Application}"

# Check if framework exists
if [ ! -d "$FRAMEWORK_PATH" ]; then
    echo "Error: Framework not found at $FRAMEWORK_PATH"
    exit 1
fi

echo "Signing dylib files..."

# Sign each dylib individually first
for dylib in "$FRAMEWORK_PATH"/*.dylib; do
    if [ -f "$dylib" ]; then
        echo "  Signing $(basename "$dylib")"
        codesign --force --deep --sign "$SIGNING_IDENTITY" \
                 --options runtime \
                 --timestamp \
                 --preserve-metadata=identifier,entitlements \
                 "$dylib"
    fi
done

# Sign the main whisper executable
echo "Signing whisper executable..."
codesign --force --deep --sign "$SIGNING_IDENTITY" \
         --options runtime \
         --timestamp \
         --preserve-metadata=identifier,entitlements \
         "$FRAMEWORK_PATH/whisper"

# Finally, sign the entire framework bundle
echo "Signing framework bundle..."
codesign --force --sign "$SIGNING_IDENTITY" \
         --options runtime \
         --timestamp \
         "$FRAMEWORK_PATH"

# Verify the signature
echo "Verifying signature..."
if codesign -vvv --deep --strict "$FRAMEWORK_PATH" 2>&1 | grep -q "valid on disk"; then
    echo "✅ Framework successfully signed and verified!"
    echo ""
    echo "You can now archive your app in Xcode."
else
    echo "❌ Signature verification failed!"
    exit 1
fi