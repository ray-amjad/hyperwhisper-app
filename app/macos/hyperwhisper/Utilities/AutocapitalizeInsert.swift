//
//  AutocapitalizeInsert.swift
//  hyperwhisper
//
//  Adjusts the first word of inserted transcript text to match the cursor's
//  surrounding context: lowercase mid-sentence, untouched at sentence start.
//
//  The pure case-adjustment logic now lives in the shared Rust core
//  (`hw-text`, exposed as `applyAutocapitalize`) so macOS and Windows stay in
//  lockstep. `CursorContext` is the UniFFI-generated enum (same cases as the
//  old native one). Only the Accessibility cursor probe stays native here.
//

import Foundation
import ApplicationServices

/// Sentence-terminal characters. If the last non-whitespace character before
/// the caret is one of these, the caret is treated as start-of-sentence.
fileprivate let autocapitalizeSentenceTerminators: Set<Character> = [
    ".", "!", "?", "…", "¡", "¿", ";", "\n", "\r"
]

enum AutocapitalizeInsert {

    /// Apply case adjustment to a transcript fragment based on cursor context.
    /// Thin wrapper over the shared core's `applyAutocapitalize`.
    static func apply(_ text: String, context: CursorContext) -> String {
        applyAutocapitalize(text: text, context: context)
    }
}

extension AccessibilityHelper {

    /// Probe the focused text element for cursor context.
    ///
    /// Returns `.unknown` if AX permission is missing, no element is focused,
    /// the element isn't a text input, or the AX read fails. Callers should
    /// treat `.unknown` as "leave the text alone".
    func cursorContextOfFocusedElement() -> CursorContext {
        guard AXIsProcessTrusted() else { return .unknown }

        let system = AXUIElementCreateSystemWide()
        var focused: CFTypeRef?
        let focusResult = AXUIElementCopyAttributeValue(
            system,
            kAXFocusedUIElementAttribute as CFString,
            &focused
        )
        guard focusResult == .success, let element = focused else { return .unknown }
        let axElement = element as! AXUIElement

        var rangeValue: CFTypeRef?
        let rangeResult = AXUIElementCopyAttributeValue(
            axElement,
            kAXSelectedTextRangeAttribute as CFString,
            &rangeValue
        )
        guard rangeResult == .success, let rangeRef = rangeValue else { return .unknown }
        var range = CFRange(location: 0, length: 0)
        guard AXValueGetValue(rangeRef as! AXValue, .cfRange, &range) else { return .unknown }

        // Caret at very start of field → sentence start.
        if range.location <= 0 { return .startOfSentence }

        // Read just the slice immediately preceding the caret. Cap at 64 chars
        // — we only need the last non-whitespace character.
        let probeStart = max(0, range.location - 64)
        let probeLen = range.location - probeStart
        var probeRange = CFRange(location: probeStart, length: probeLen)
        guard let probeAXValue = AXValueCreate(.cfRange, &probeRange) else { return .unknown }

        var preTextRef: CFTypeRef?
        let stringResult = AXUIElementCopyParameterizedAttributeValue(
            axElement,
            kAXStringForRangeParameterizedAttribute as CFString,
            probeAXValue,
            &preTextRef
        )

        let preceding: String
        if stringResult == .success, let s = preTextRef as? String {
            preceding = s
        } else {
            // Fallback: read whole value and slice. Some apps don't support the
            // parameterized string-for-range attribute.
            var valueRef: CFTypeRef?
            let valueResult = AXUIElementCopyAttributeValue(
                axElement,
                kAXValueAttribute as CFString,
                &valueRef
            )
            guard valueResult == .success, let full = valueRef as? String else { return .unknown }
            // Approximate slice: AX offsets are UTF-16 but for "what's the last
            // non-whitespace char before the caret" the exact alignment doesn't
            // matter — we only need a representative tail.
            let endOffset = max(0, min(range.location, full.count))
            let endIdx = full.index(full.startIndex, offsetBy: endOffset, limitedBy: full.endIndex) ?? full.endIndex
            preceding = String(full[..<endIdx])
        }

        return Self.classifyPreceding(preceding)
    }

    /// Pure helper exposed for unit testing.
    static func classifyPreceding(_ preceding: String) -> CursorContext {
        // Walk back over whitespace; first non-whitespace char decides.
        for char in preceding.reversed() {
            if char.isWhitespace { continue }
            if autocapitalizeSentenceTerminators.contains(char) {
                return .startOfSentence
            }
            return .midSentence
        }
        // Only whitespace before caret.
        return .startOfSentence
    }
}
