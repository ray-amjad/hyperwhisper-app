#!/bin/bash

echo "🔧 HyperWhisper Accessibility Permission Reset Script"
echo "=================================================="
echo ""

# Kill any running instances
echo "1. Stopping HyperWhisper..."
pkill -f hyperwhisper || true
pkill -f HyperWhisper || true

echo ""
echo "2. Resetting accessibility permissions..."
echo "   This will remove HyperWhisper from the accessibility list."
echo "   You'll need to re-add it after this."
echo ""

# Reset the TCC database entry for HyperWhisper
# Note: This requires admin privileges
echo "   Attempting to reset TCC database entries..."

# Try to reset for the bundle ID
tccutil reset Accessibility com.hyperwhisper.hyperwhisper 2>/dev/null || true

# Also try the executable name
tccutil reset Accessibility hyperwhisper 2>/dev/null || true

echo ""
echo "3. Instructions to fix:"
echo "   a) Open System Settings > Privacy & Security > Accessibility"
echo "   b) Look for any HyperWhisper entries and remove them (- button)"
echo "   c) Run HyperWhisper again"
echo "   d) When prompted, grant accessibility permission"
echo "   e) If HyperWhisper appears in the list but unchecked, check it"
echo "   f) You may need to restart HyperWhisper after granting permission"
echo ""
echo "4. Alternative fix if above doesn't work:"
echo "   a) Open Terminal"
echo "   b) Run: sudo sqlite3 ~/Library/Application\\ Support/com.apple.TCC/TCC.db"
echo "   c) Run: DELETE FROM access WHERE client LIKE '%hyperwhisper%';"
echo "   d) Run: .quit"
echo "   e) Restart your Mac"
echo ""
echo "✅ Reset complete. Now run HyperWhisper and grant permission when prompted."