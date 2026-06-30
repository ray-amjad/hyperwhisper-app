#!/bin/bash

# Resign App for Notarization
# Use this script to manually resign an archived app with the correct certificate

if [ "$#" -ne 1 ]; then
    echo "Usage: $0 <path-to-app-bundle>"
    echo "Example: $0 ~/Library/Developer/Xcode/Archives/2025-09-04/hyperwhisper.xcarchive/Products/Applications/hyperwhisper.app"
    exit 1
fi

APP_PATH="$1"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Override with your own identity: export SIGNING_IDENTITY="Developer ID Application: Your Name (TEAMID)"
SIGNING_IDENTITY="${SIGNING_IDENTITY:-Developer ID Application}"
ENTITLEMENTS="${ENTITLEMENTS:-$SCRIPT_DIR/hyperwhisper/hyperwhisper-release.entitlements}"

if [ ! -d "$APP_PATH" ]; then
    echo "Error: App bundle not found at $APP_PATH"
    exit 1
fi

echo "Resigning app for notarization..."
echo "App: $APP_PATH"
echo "Identity: $SIGNING_IDENTITY"
echo "Entitlements: $ENTITLEMENTS"
echo ""

# Sign the app with proper settings for notarization
codesign --force \
         --deep \
         --sign "$SIGNING_IDENTITY" \
         --options runtime \
         --entitlements "$ENTITLEMENTS" \
         --timestamp \
         "$APP_PATH"

if [ $? -eq 0 ]; then
    echo "✅ Successfully resigned app"
    echo ""
    echo "Verifying signature..."
    codesign -vvv --deep --strict "$APP_PATH" 2>&1 | head -5
    
    echo ""
    echo "Checking signing certificate..."
    codesign -d -vv "$APP_PATH" 2>&1 | grep "Authority" | head -1
else
    echo "❌ Failed to resign app"
    exit 1
fi