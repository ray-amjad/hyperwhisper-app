//
//  AccessibilityHelper+KeyboardLayout.swift
//  hyperwhisper
//
//  Keyboard layout utilities for generating keyboard-independent key codes.
//
//  KEYBOARD LAYOUT INDEPENDENCE:
//  Virtual key codes represent physical key positions, not characters.
//  For example, key code 0x09 is the physical key that produces 'v' on QWERTY,
//  but on Dvorak that same physical position produces 'k'.
//
//  This causes problems when simulating keyboard shortcuts like Command+V:
//  - On QWERTY: CGEvent with virtualKey 0x09 + Command = Command+V (paste)
//  - On Dvorak: CGEvent with virtualKey 0x09 + Command = Command+K (clear screen in iTerm!)
//
//  SOLUTION:
//  Use UCKeyTranslate from the Carbon framework to dynamically look up which
//  physical key produces a given character on the current keyboard layout.
//  This is a reverse lookup: character -> key code (instead of key code -> character).
//

import Carbon
import CoreGraphics
import os

extension AccessibilityHelper {

    // MARK: - Keyboard Layout Key Code Lookup

    /// Get the virtual key code for a character on the current keyboard layout.
    ///
    /// KEYBOARD LAYOUT INDEPENDENCE:
    /// Virtual key codes represent physical key positions, not characters.
    /// - On QWERTY: 'v' is key code 0x09
    /// - On Dvorak: 'v' is key code 0x2F (physical position where QWERTY has '.')
    ///
    /// This function performs a reverse lookup: given a character, find which
    /// physical key produces it on the current keyboard layout.
    ///
    /// HOW IT WORKS:
    /// 1. Get the current keyboard layout from TISCopyCurrentKeyboardLayoutInputSource()
    /// 2. Extract the UCKeyboardLayout data from the input source
    /// 3. Loop through all 128 possible virtual key codes (0-127)
    /// 4. For each key code, use UCKeyTranslate() to determine what character it produces
    /// 5. When we find a match, return that key code
    ///
    /// PERFORMANCE:
    /// The loop through 128 keys is fast (microseconds) since UCKeyTranslate is a
    /// simple table lookup. This function is called infrequently (only for paste
    /// operations), so caching is unnecessary.
    ///
    /// - Parameter character: The character to look up (e.g., "v")
    /// - Returns: The virtual key code that produces this character, or nil if not found
    func keyCodeForCharacter(_ character: String) -> CGKeyCode? {
        // Get the current keyboard layout input source
        let inputSource = TISCopyCurrentKeyboardLayoutInputSource().takeRetainedValue()

        // Get the Unicode key layout data from the input source
        // This contains the mapping from key codes to characters
        guard let layoutData = TISGetInputSourceProperty(inputSource, kTISPropertyUnicodeKeyLayoutData) else {
            logger.warning("Could not get keyboard layout data - falling back to QWERTY")
            return nil
        }

        let layout = unsafeBitCast(layoutData, to: CFData.self)
        let keyboardLayout = unsafeBitCast(CFDataGetBytePtr(layout), to: UnsafePointer<UCKeyboardLayout>.self)

        var deadKeyState: UInt32 = 0
        let maxStringLength = 4
        var actualStringLength = 0
        var unicodeString = [UniChar](repeating: 0, count: maxStringLength)

        // Try each virtual key code to find which produces our character
        // Virtual key codes 0-127 cover all standard keyboard keys
        for keyCode in 0..<128 {
            deadKeyState = 0  // Reset dead key state for each key

            let status = UCKeyTranslate(
                keyboardLayout,
                UInt16(keyCode),
                UInt16(kUCKeyActionDisplay),  // Get the character for display (not input)
                0,                             // No modifier keys
                UInt32(LMGetKbdType()),        // Current keyboard type
                UInt32(kUCKeyTranslateNoDeadKeysMask),  // Ignore dead keys
                &deadKeyState,
                maxStringLength,
                &actualStringLength,
                &unicodeString
            )

            if status == noErr && actualStringLength > 0 {
                let result = String(utf16CodeUnits: unicodeString, count: actualStringLength)
                if result.lowercased() == character.lowercased() {
                    logger.debug("Key code for '\(character)' on current layout: \(keyCode) (0x\(String(keyCode, radix: 16)))")
                    return CGKeyCode(keyCode)
                }
            }
        }

        logger.warning("Could not find key code for '\(character)' on current keyboard layout")
        return nil
    }

    /// Get the key code for 'v' with QWERTY fallback.
    ///
    /// This is a convenience method specifically for paste operations (Command+V).
    /// If the keyboard layout lookup fails, falls back to the QWERTY key code 0x09.
    ///
    /// - Returns: The key code for 'v' on the current layout, or 0x09 as fallback
    func keyCodeForV() -> CGKeyCode {
        if let keyCode = keyCodeForCharacter("v") {
            return keyCode
        }
        // Fallback to QWERTY key code for 'v'
        logger.warning("Using QWERTY fallback key code 0x09 for 'v'")
        return 0x09
    }
}
