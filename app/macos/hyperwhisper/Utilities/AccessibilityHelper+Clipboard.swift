//
//  AccessibilityHelper+Clipboard.swift
//  hyperwhisper
//
//  Created by Assistant on 16/08/2025.
//

import Foundation
import AppKit
import os

extension AccessibilityHelper {

    // MARK: - Clipboard Restoration Lifecycle

    /// Start a new recording session
    /// This saves the current clipboard so it can be restored after pasting the transcription
    ///
    /// **ENHANCED CLIPBOARD CAPTURE:**
    /// Captures ALL pasteboard types (text, images, files, rich text, URLs, etc.)
    /// instead of just plain text. This prevents data loss when users have
    /// non-text content copied (e.g., screenshots, PDFs, code with formatting).
    ///
    /// **How it works:**
    /// 1. Preserves an existing snapshot and pending restore when a previous clipboard restore is still pending
    /// 2. Cancels any pending restoration from previous recording if no restore snapshot needs to survive
    /// 3. Extracts DATA from all NSPasteboardItem objects (not the objects themselves)
    /// 4. Each item's data is stored for all its types (public.utf8-plain-text, public.png, etc.)
    /// 5. On restoration, NEW pasteboard items are created from the stored data
    ///
    /// **Why extract data instead of storing items?**
    /// NSPasteboardItem objects are tied to the pasteboard they came from and cannot be reused.
    /// Attempting to write them to a pasteboard after clearContents() causes a crash:
    /// "Cannot write pasteboard item. It is already associated with another pasteboard."
    func startRecordingSession() {
        logger.info("🎙️ Starting recording session")

        if activeRestorationWorkItem?.isCancelled == false,
           originalClipboardData != nil {
            logger.info("📋 Preserving saved clipboard snapshot and pending restore across stacked recording session")
            isInRecordingSession = true
            return
        }

        // Cancel any pending restoration from a previous recording
        cancelPendingClipboardRestoration()

        // ENHANCED: Extract DATA from all clipboard items
        // We cannot store the NSPasteboardItem objects directly because they cannot be reused
        let pasteboard = NSPasteboard.general
        if let items = pasteboard.pasteboardItems, !items.isEmpty {
            // Extract data from each pasteboard item
            originalClipboardData = items.compactMap { item in
                var dataDict: [NSPasteboard.PasteboardType: Data] = [:]

                // Extract data for each type this item supports
                for type in item.types {
                    if let data = item.data(forType: type) {
                        dataDict[type] = data
                    }
                }

                // Only include items that have at least one type with data
                guard !dataDict.isEmpty else { return nil }

                return ClipboardItemData(types: item.types, data: dataDict)
            }

            // Log what types we captured for debugging
            if let data = originalClipboardData {
                let types = data.flatMap { $0.types }.map { $0.rawValue }
                let uniqueTypes = Set(types)
                logger.info("📋 Saved clipboard with \(data.count, privacy: .public) item(s) containing types: \(uniqueTypes.prefix(5).joined(separator: ", "), privacy: .public)")
            }
        } else {
            originalClipboardData = nil
            logger.info("📋 Clipboard is empty, nothing to save")
        }

        isInRecordingSession = true
    }

    /// End the recording session
    /// This should be called when the recording dialog is closed or the app becomes inactive
    func endRecordingSession() {
        logger.info("🛑 Ending recording session")
        isInRecordingSession = false
        // Note: We don't clear originalClipboardContent here in case there's a pending restoration
    }

    /// Cancel any pending clipboard restoration
    /// This should be called when starting a new recording
    func cancelPendingClipboardRestoration() {
        if let workItem = activeRestorationWorkItem {
            workItem.cancel()
            activeRestorationWorkItem = nil
            logger.debug("❌ Cancelled pending clipboard restoration")
        }
    }

    // MARK: - Clipboard Methods

    /// Copy text to the system clipboard
    /// - Parameters:
    ///   - text: The text to copy
    ///   - skipConcealedType: When true, omits the ConcealedType marker even if the setting is enabled.
    ///     Used for remote desktop apps where clipboard forwarding may skip concealed items.
    func copyToClipboard(_ text: String, skipConcealedType: Bool = false) {
        let pb = NSPasteboard.general
        pb.clearContents()

        let item = NSPasteboardItem()
        item.setString(text, forType: .string)
        // Mark as concealed to hide from clipboard history apps (if setting is enabled)
        // Skip for remote desktop targets where concealed items may not sync to the remote machine
        if !skipConcealedType && UserDefaults.standard.bool(forKey: "hideFromClipboardHistory") {
            item.setData(Data(), forType: NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType"))
        }

        pb.writeObjects([item])
    }

    /// Get current clipboard content
    /// - Returns: Current clipboard text, or nil if empty/non-text
    func getClipboardContent() -> String? {
        return NSPasteboard.general.string(forType: .string)
    }

    /// Copy text to clipboard with settings awareness
    /// - Parameters:
    ///   - text: Text to copy
    ///   - respectSettings: Optional SettingsManager to respect clipboard restoration settings
    ///
    /// NOTE: This method is deprecated for auto-paste operations.
    /// Use performSmartPaste() instead which properly manages clipboard restoration
    /// across multiple recordings. This method is only for manual copy operations.
    func copyToClipboard(_ text: String, respectSettings settings: SettingsManager?) {
        // For manual copy operations (not auto-paste), we still do simple restoration
        // This is used when user manually copies from history or when auto-paste fails
        if let settings = settings, settings.restoreClipboardAfterPaste {
            // Save current clipboard
            let previousContent = getClipboardContent()

            // Copy new text
            copyToClipboard(text)

            // Schedule simple restoration (not tied to recording sessions)
            if let previous = previousContent {
                let delay = settings.clipboardRestoreDelaySeconds
                DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
                    self?.copyToClipboard(previous)
                    self?.logger.info("♻️ Restored previous clipboard content (manual copy)")
                }
            }
        } else {
            copyToClipboard(text)
        }
    }

    /// Schedule clipboard restoration based on settings
    /// Uses the original clipboard content saved at the start of the recording session
    ///
    /// **ENHANCED CLIPBOARD RESTORATION:**
    /// Restores ALL pasteboard types (text, images, files, rich text, URLs, etc.)
    /// that were captured at the start of the recording session.
    ///
    /// **How it works:**
    /// 1. Checks if restoration is enabled in settings
    /// 2. Verifies we have original clipboard data to restore
    /// 3. Schedules restoration after configured delay (default 15 seconds)
    /// 4. Creates NEW pasteboard items from the stored data
    /// 5. Restores ALL types to the pasteboard
    /// 6. Clears the saved data to free memory
    ///
    /// **Why create new items?**
    /// NSPasteboardItem objects cannot be reused across pasteboards or after clearContents().
    /// We must create fresh items from the stored data.
    func scheduleClipboardRestoration(settings: SettingsManager?) {
        // Check if restoration is enabled and we have original content
        guard let settings = settings,
              settings.restoreClipboardAfterPaste,
              let dataToRestore = originalClipboardData else {
            return
        }

        let delay = settings.clipboardRestoreDelaySeconds
        logger.info("⏰ Scheduling clipboard restoration in \(delay, privacy: .public) seconds (\(dataToRestore.count, privacy: .public) item(s))")

        // Cancel any existing restoration timer
        cancelPendingClipboardRestoration()

        // Create a new work item for restoration
        let workItem = DispatchWorkItem { [weak self] in
            guard let self = self else { return }

            // Check if this work item is still the active one (not cancelled)
            if self.activeRestorationWorkItem?.isCancelled == false {
                // ENHANCED: Restore ALL clipboard types (text, images, files, etc.)
                let pasteboard = NSPasteboard.general
                pasteboard.clearContents()

                // Create NEW pasteboard items from the stored data
                // We cannot reuse the original NSPasteboardItem objects
                let newItems = dataToRestore.map { itemData -> NSPasteboardItem in
                    let item = NSPasteboardItem()

                    // Set data for each type this item had
                    for (type, data) in itemData.data {
                        item.setData(data, forType: type)
                    }

                    return item
                }

                // Write the new items to the pasteboard
                let success = pasteboard.writeObjects(newItems)

                if success {
                    let types = dataToRestore.flatMap { $0.types }.map { $0.rawValue }
                    let uniqueTypes = Set(types)
                    self.logger.info("♻️ Restored original clipboard content (\(dataToRestore.count, privacy: .public) item(s) with types: \(uniqueTypes.prefix(5).joined(separator: ", "), privacy: .public))")
                } else {
                    self.logger.warning("⚠️ Failed to restore clipboard content")
                }

                self.activeRestorationWorkItem = nil

                // Clear the original content if we're not in a session
                if !self.isInRecordingSession {
                    self.originalClipboardData = nil
                }
            }
        }

        // Store the work item so it can be cancelled if needed
        activeRestorationWorkItem = workItem

        // Schedule the restoration
        DispatchQueue.main.asyncAfter(deadline: .now() + delay, execute: workItem)
    }
}
