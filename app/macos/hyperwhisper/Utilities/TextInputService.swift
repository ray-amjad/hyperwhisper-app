//
//  TextInputService.swift
//  hyperwhisper
//
//  Text input service for typing and pasting text into applications.
//  Extracted from AccessibilityHelper to separate text input concerns
//  from accessibility/focus detection.
//

import AppKit
import Carbon
import os

private actor TextInputCoordinator {
    private var tailTask: Task<Void, Never>?
    private var tailTicket = 0

    func run(_ operation: @escaping @Sendable () async -> Bool) async -> Bool {
        let previousTask = tailTask
        tailTicket += 1
        let ticket = tailTicket
        let currentTask = Task {
            await previousTask?.value
            return await operation()
        }

        tailTask = Task {
            _ = await currentTask.value
        }

        let result = await currentTask.value
        if tailTicket == ticket {
            tailTask = nil
        }
        return result
    }
}

private struct PasteboardItemSnapshot {
    let types: [NSPasteboard.PasteboardType]
    let data: [NSPasteboard.PasteboardType: Data]
}

/// Service for inputting text into applications via typing or pasting.
///
/// TEXT INPUT STRATEGIES:
/// This service provides multiple methods for inserting text into the focused application:
///
/// 1. **Character Typing** (`typeText`, `typeTextAsync`):
///    - Types each character individually using CGEvent keyboard simulation
///    - Preserves clipboard contents
///    - Works with any Unicode character including emoji
///    - ~5ms delay per character for reliability
///
/// 2. **Clipboard Paste** (`pasteTextForStreaming`):
///    - Sets clipboard content and simulates Cmd+V
///    - Instant for any text length
///    - Saves/restores original clipboard contents
///    - Used for CJK languages where character typing is slow
///
/// 3. **Hybrid/Smart** (`typeSegment`):
///    - Automatically chooses paste for CJK languages (ja, zh, ko)
///    - Falls back to character typing for Latin languages
///    - Auto-detects CJK content when language is unknown
///
/// USAGE:
/// ```swift
/// // For streaming transcription with language awareness
/// await TextInputService.shared.typeSegment(text, language: "ja")
///
/// // For simple async typing
/// await TextInputService.shared.typeTextAsync(text)
/// ```
final class TextInputService {

    // MARK: - Singleton

    static let shared = TextInputService()

    private let logger = Logger(subsystem: "com.hyperwhisper", category: "TextInputService")
    private let coordinator = TextInputCoordinator()

    private init() {}

    private func capturePasteboardSnapshot(from pasteboard: NSPasteboard) -> [PasteboardItemSnapshot]? {
        guard let items = pasteboard.pasteboardItems, !items.isEmpty else {
            return nil
        }

        return items.compactMap { item in
            var dataByType: [NSPasteboard.PasteboardType: Data] = [:]
            for type in item.types {
                if let data = item.data(forType: type) {
                    dataByType[type] = data
                }
            }
            guard !dataByType.isEmpty else { return nil }
            return PasteboardItemSnapshot(types: item.types, data: dataByType)
        }
    }

    private func restorePasteboardSnapshot(_ snapshot: [PasteboardItemSnapshot], to pasteboard: NSPasteboard) {
        pasteboard.clearContents()
        let items = snapshot.map { snapshotItem -> NSPasteboardItem in
            let item = NSPasteboardItem()
            for (type, data) in snapshotItem.data {
                item.setData(data, forType: type)
            }
            return item
        }
        _ = pasteboard.writeObjects(items)
    }

    // MARK: - Character Typing

    /// Types text character by character using CGEvent keyboard simulation.
    /// This is used for streaming transcription where we type as results arrive.
    ///
    /// Unlike paste (⌘V), this method:
    /// - Types each character individually using Unicode events
    /// - Works incrementally (can type more text as it arrives)
    /// - Doesn't use the clipboard (no restoration needed)
    /// - Handles arbitrary Unicode characters including emoji
    ///
    /// HOW IT WORKS:
    /// 1. Creates a CGEventSource for synthetic keyboard events
    /// 2. For each character in the text:
    ///    a. Converts the character to UTF-16 code units
    ///    b. Creates keyDown and keyUp CGEvents with virtualKey 0 (placeholder)
    ///    c. Sets the Unicode string on the events using keyboardSetUnicodeString
    ///    d. Posts events to .cghidEventTap (HID layer)
    ///    e. Adds a small delay between characters for reliability
    ///
    /// THREADING:
    /// This method uses Thread.sleep for timing, so it blocks the calling thread.
    /// For streaming transcription, call from a background task or use typeTextAsync.
    ///
    /// - Parameter text: The text to type
    /// - Returns: true if typing succeeded, false otherwise
    func typeText(_ text: String) -> Bool {
        guard !text.isEmpty else {
            return true // Nothing to type
        }

        guard AXIsProcessTrusted() else {
            logger.error("❌ No accessibility permission - cannot type")
            return false
        }

        // Use a private-state source so synthesised events do not inherit the user's
        // live hardware modifier flags. With a push-to-talk modifier (e.g. Control or
        // Option) held or latched, .hidSystemState would turn each streamed character
        // into a Ctrl/Option chord shortcut instead of inserting text.
        guard let src = CGEventSource(stateID: .privateState) else {
            logger.error("❌ Failed to create CGEventSource")
            return false
        }

        logger.debug("⌨️ Typing \(text.count, privacy: .public) characters...")

        // Type each character using Unicode events
        for char in text {
            // Convert character to UTF-16 code units
            let chars = Array(char.utf16)

            // Create keyDown and keyUp events with a placeholder virtual key (0)
            // The actual character is set via keyboardSetUnicodeString
            guard let keyDown = CGEvent(keyboardEventSource: src, virtualKey: 0, keyDown: true),
                  let keyUp = CGEvent(keyboardEventSource: src, virtualKey: 0, keyDown: false) else {
                logger.warning("⚠️ Failed to create CGEvent for character: \(String(char), privacy: .public)")
                continue
            }

            // Explicitly clear modifier flags so a held PTT modifier cannot turn the
            // character into a shortcut (e.g. Ctrl+W). Mirrors sendTab().
            keyDown.flags = []
            keyUp.flags = []

            // Set the Unicode characters to type
            // This tells macOS to insert this character regardless of keyboard layout
            keyDown.keyboardSetUnicodeString(stringLength: chars.count, unicodeString: chars)
            keyUp.keyboardSetUnicodeString(stringLength: chars.count, unicodeString: chars)

            // Post the events to the HID event tap
            keyDown.post(tap: .cghidEventTap)
            keyUp.post(tap: .cghidEventTap)

            // Small delay between characters for reliability
            // This prevents overwhelming the system and ensures each character is processed
            Thread.sleep(forTimeInterval: 0.005) // 5ms per character
        }

        logger.info("✅ Typed \(text.count, privacy: .public) characters")
        return true
    }

    /// Async version of typeText that doesn't block the main thread.
    /// Uses Task.sleep instead of Thread.sleep for non-blocking delays.
    ///
    /// - Parameter text: The text to type
    /// - Returns: true if typing succeeded, false otherwise
    func typeTextAsync(_ text: String) async -> Bool {
        await coordinator.run { [self] in
            await typeTextAsyncUnlocked(text)
        }
    }

    private func typeTextAsyncUnlocked(_ text: String) async -> Bool {
        guard !text.isEmpty else {
            return true
        }

        guard AXIsProcessTrusted() else {
            logger.error("❌ No accessibility permission - cannot type")
            return false
        }

        // Use a private-state source so synthesised events do not inherit the user's
        // live hardware modifier flags. With a push-to-talk modifier (e.g. Control or
        // Option) held or latched, .hidSystemState would turn each streamed character
        // into a Ctrl/Option chord shortcut instead of inserting text.
        guard let src = CGEventSource(stateID: .privateState) else {
            logger.error("❌ Failed to create CGEventSource")
            return false
        }

        logger.debug(
            "⌨️ Typing \(text.count, privacy: .public) characters (async), spaces=\(self.whitespaceCount(text), privacy: .public), text=\(self.diagnosticExcerpt(text), privacy: .public)"
        )

        for char in text {
            // Check for task cancellation
            if Task.isCancelled {
                logger.debug("❌ Typing cancelled")
                return false
            }

            let chars = Array(char.utf16)

            guard let keyDown = CGEvent(keyboardEventSource: src, virtualKey: 0, keyDown: true),
                  let keyUp = CGEvent(keyboardEventSource: src, virtualKey: 0, keyDown: false) else {
                continue
            }

            // Explicitly clear modifier flags so a held PTT modifier cannot turn the
            // character into a shortcut (e.g. Ctrl+W). Mirrors sendTab().
            keyDown.flags = []
            keyUp.flags = []

            keyDown.keyboardSetUnicodeString(stringLength: chars.count, unicodeString: chars)
            keyUp.keyboardSetUnicodeString(stringLength: chars.count, unicodeString: chars)

            keyDown.post(tap: .cghidEventTap)
            keyUp.post(tap: .cghidEventTap)

            // Non-blocking delay
            try? await Task.sleep(nanoseconds: 5_000_000) // 5ms
        }

        logger.info("✅ Typed \(text.count, privacy: .public) characters (async)")
        return true
    }

    // MARK: - CJK Streaming Paste

    /// Paste text via clipboard. Saves/restores existing clipboard contents.
    /// Used for CJK streaming where character-by-character typing is slow.
    ///
    /// CJK PASTE OPTIMIZATION:
    /// For CJK languages (Japanese, Chinese, Korean), typing character-by-character
    /// with 5ms delays causes noticeable lag. This method:
    /// 1. Saves current clipboard contents
    /// 2. Temporarily sets the text to paste
    /// 3. Simulates Cmd+V
    /// 4. Restores original clipboard contents
    ///
    /// - Parameter text: The text to paste
    /// - Returns: true if paste operation succeeded, false otherwise
    func pasteTextForStreaming(_ text: String, targetPID: pid_t? = nil) async -> Bool {
        await coordinator.run { [self] in
            await pasteTextForStreamingUnlocked(text, targetPID: targetPID)
        }
    }

    private func pasteTextForStreamingUnlocked(_ text: String, targetPID: pid_t? = nil) async -> Bool {
        guard !text.isEmpty else { return true }

        guard AXIsProcessTrusted() else {
            logger.error("❌ No accessibility permission - cannot paste")
            return false
        }

        var targetBundleId: String?
        if let targetPID, let targetApp = NSRunningApplication(processIdentifier: targetPID) {
            targetBundleId = targetApp.bundleIdentifier
            if NSWorkspace.shared.frontmostApplication?.processIdentifier != targetPID {
                targetApp.activate(options: [.activateIgnoringOtherApps])
                try? await Task.sleep(nanoseconds: 120_000_000)
            }
        }

        let frontmostBundleId: String?
        if let targetBundleId {
            frontmostBundleId = targetBundleId
        } else {
            frontmostBundleId = await AccessibilityHelper.shared.frontmostBundleId()
        }
        let isTerminalTarget: Bool
        if let bundleId = frontmostBundleId {
            isTerminalTarget = await AccessibilityHelper.shared.isTerminalBundleId(bundleId)
        } else {
            isTerminalTarget = false
        }

        // Save current clipboard
        let pasteboard = NSPasteboard.general
        let savedSnapshot = capturePasteboardSnapshot(from: pasteboard)

        // Set new content, optionally with concealed type to hide from clipboard history apps
        pasteboard.clearContents()
        let item = NSPasteboardItem()
        item.setString(text, forType: .string)
        // Mark as concealed to hide from clipboard history apps (Paste, Maccy, Alfred, etc.)
        if UserDefaults.standard.bool(forKey: "hideFromClipboardHistory") {
            item.setData(Data(), forType: NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType"))
        }
        pasteboard.writeObjects([item])

        logger.info(
            "📋 Streaming paste prepared for bundle=\(frontmostBundleId ?? "unknown", privacy: .public) targetPID=\(targetPID ?? -1, privacy: .public) terminal=\(isTerminalTarget, privacy: .public) spaces=\(self.whitespaceCount(text), privacy: .public) text=\(self.diagnosticExcerpt(text), privacy: .public)"
        )

        // Give the clipboard a brief moment to settle before posting Cmd+V.
        // Doing this unconditionally — terminals seem to behave
        // better with a small delay here.
        try? await Task.sleep(nanoseconds: 50_000_000)

        let pasteSucceeded = await AccessibilityHelper.shared.sendPasteCommand(allowBlindPaste: isTerminalTarget)

        guard pasteSucceeded else {
            if isTerminalTarget {
                logger.error(
                    "❌ Terminal paste failed for bundle=\(frontmostBundleId ?? "unknown", privacy: .public). Direct Cmd+V failed."
                )
            }
            if let savedSnapshot {
                restorePasteboardSnapshot(savedSnapshot, to: pasteboard)
            }
            return false
        }

        // Wait for paste to complete
        let restoreDelayNanoseconds: UInt64 = isTerminalTarget ? 350_000_000 : 50_000_000
        try? await Task.sleep(nanoseconds: restoreDelayNanoseconds)

        // Restore clipboard
        if let savedSnapshot {
            restorePasteboardSnapshot(savedSnapshot, to: pasteboard)
        }

        logger.info(
            "✅ Pasted \(text.count, privacy: .public) characters via clipboard, bundle=\(frontmostBundleId ?? "unknown", privacy: .public), restoreDelayMs=\(restoreDelayNanoseconds / 1_000_000, privacy: .public), spaces=\(self.whitespaceCount(text), privacy: .public), text=\(self.diagnosticExcerpt(text), privacy: .public)"
        )
        return true
    }

    // MARK: - Hybrid Text Input

    /// Type or paste text based on language.
    ///
    /// HYBRID STREAMING TEXT INPUT:
    /// - CJK languages (ja, zh, ko): Uses clipboard paste for instant insertion
    /// - Auto-detect mode: Analyzes text content for CJK characters
    /// - Other languages: Uses character-by-character typing to preserve clipboard
    ///
    /// Why separate approaches:
    /// - CJK typing with 5ms/char delay causes visible lag (250ms for 50 chars)
    /// - Paste is instant but disrupts clipboard (acceptable for CJK)
    /// - Non-CJK users expect clipboard to remain untouched
    ///
    /// - Parameters:
    ///   - text: The text to type or paste
    ///   - language: Language code (e.g., "ja", "en") or nil for auto-detect
    /// - Returns: true if operation succeeded, false otherwise
    func typeSegment(_ text: String, language: String?) async -> Bool {
        guard !text.isEmpty else { return true }

        return await coordinator.run { [self] in
            await typeSegmentUnlocked(text, language: language)
        }
    }

    private func typeSegmentUnlocked(_ text: String, language: String?) async -> Bool {
        let lang = language?.prefix(2).lowercased() ?? ""
        logger.debug(
            "⌨️ Queueing streaming segment: chars=\(text.count, privacy: .public) spaces=\(self.whitespaceCount(text), privacy: .public) language=\(lang, privacy: .public) text=\(self.diagnosticExcerpt(text), privacy: .public)"
        )

        // Use paste for CJK languages
        if ["ja", "zh", "ko"].contains(lang) {
            logger.debug("🇯🇵 CJK language '\(lang, privacy: .public)' - using paste")
            return await pasteTextForStreamingUnlocked(text)
        }

        // Auto-detect: check if text contains CJK characters
        if lang.isEmpty {
            if SmartSpacing.containsCJKCharacters(text) {
                logger.debug("🔍 Auto-detect found CJK characters - using paste")
                return await pasteTextForStreamingUnlocked(text)
            }
        }

        // Non-CJK: use character-by-character typing
        return await typeTextAsyncUnlocked(text)
    }

    private func whitespaceCount(_ text: String) -> Int {
        text.reduce(into: 0) { count, character in
            if character.isWhitespace {
                count += 1
            }
        }
    }

    private func diagnosticExcerpt(_ text: String, limit: Int = 120) -> String {
        let escaped = text
            .replacingOccurrences(of: "\n", with: "\\n")
            .replacingOccurrences(of: "\t", with: "\\t")
        let excerpt = String(escaped.prefix(limit))
        return "\"\(excerpt)\""
    }
}
