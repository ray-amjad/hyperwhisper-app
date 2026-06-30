#!/bin/bash

# Sign Whisper Framework Script
# This script signs the whisper.framework after it's been embedded in the app bundle
# This is necessary for notarization as the framework contains binaries that need proper signing

# Exit on error
set -e

echo "Signing whisper.framework..."

# Get the path to the embedded framework
FRAMEWORK_PATH="${BUILT_PRODUCTS_DIR}/${FRAMEWORKS_FOLDER_PATH}/whisper.framework"

# Check if the framework exists
if [ ! -d "$FRAMEWORK_PATH" ]; then
    echo "Warning: whisper.framework not found at $FRAMEWORK_PATH"
    exit 0
fi

# Sign the framework with Developer ID
# Using --deep to sign all nested code
# Using --force to replace any existing signature
# Using --options runtime for hardened runtime (required for notarization)
# Using --timestamp for secure timestamp (required for notarization)
codesign --force \
         --deep \
         --sign "${EXPANDED_CODE_SIGN_IDENTITY}" \
         --options runtime \
         --timestamp \
         --preserve-metadata=identifier,entitlements,requirements \
         "$FRAMEWORK_PATH"

echo "Successfully signed whisper.framework"