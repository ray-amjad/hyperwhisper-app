#!/bin/bash
# AUTOMATIC UPLOAD - Runs during Xcode Archive builds
# Upload dSYM files to Sentry for crash symbolication
# This script runs automatically via Xcode Build Phase on Release builds

set -e

# Only upload for release builds (not debug)
if [ "${CONFIGURATION}" = "Release" ]; then
    echo "📤 Uploading dSYMs to Sentry..."

    # Get app version from Xcode build settings (MARKETING_VERSION)
    # Modern Xcode projects use build settings instead of Info.plist
    APP_VERSION="${MARKETING_VERSION:-unknown}"

    # Use .sentryclirc from project root for authentication
    export SENTRY_PROPERTIES="${SRCROOT}/../.sentryclirc"

    # Upload dSYMs using sentry-cli
    # The $DWARF_DSYM_FOLDER_PATH contains all dSYM bundles from the build
    if [ -n "${DWARF_DSYM_FOLDER_PATH}" ] && [ -d "${DWARF_DSYM_FOLDER_PATH}" ]; then
        /opt/homebrew/bin/sentry-cli upload-dif \
            --org ray-amjad \
            --project hyperwhisper \
            --include-sources \
            "${DWARF_DSYM_FOLDER_PATH}"

        echo "✅ dSYM upload complete for version ${APP_VERSION}"
    else
        echo "⚠️  Warning: DWARF_DSYM_FOLDER_PATH not found or empty"
        exit 0
    fi
else
    echo "⏭️  Skipping dSYM upload for ${CONFIGURATION} build"
fi
