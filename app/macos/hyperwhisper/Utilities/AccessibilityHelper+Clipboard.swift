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
    ///
    /// **Off the main actor (#879):** `item.data(forType:)` is a synchronous IPC to the
    /// pasteboard daemon, and a lazy promise makes the owning app render the data on
    /// demand. It once froze the app for 10 s and more at the moment the user pressed
    /// the hotkey. The read now runs in `ClipboardSnapshotReader`, on its own serial
    /// queue, with a 1 s deadline. Past the deadline the snapshot is nil: the
    /// clipboard is not restored after the paste, and the reader logs why.
    func startRecordingSession() async {
        logger.info("🎙️ Starting recording session")

        // Any read still in flight from an earlier call is now stale (#879).
        Self.clipboardSnapshotGeneration &+= 1
        let generation = Self.clipboardSnapshotGeneration

        // Single use: whichever branch runs, the mark from the last exit is spent.
        let keptChangeCount = keptClipboardSnapshotChangeCount
        keptClipboardSnapshotChangeCount = nil

        if activeRestorationWorkItem?.isCancelled == false,
           originalClipboardData != nil {
            logger.info("📋 Preserving saved clipboard snapshot and pending restore across stacked recording session")
            isInRecordingSession = true
            return
        }

        // The last exit pasted nothing and left its transcript on the clipboard,
        // and nobody has written to the clipboard since: keep the user's older
        // snapshot, so the next restore writes it back, not the transcript (#1061).
        if let keptChangeCount,
           keptChangeCount == NSPasteboard.general.changeCount,
           originalClipboardData != nil {
            logger.info("📋 Clipboard still holds an unpasted transcript; keeping the older clipboard snapshot")
            cancelPendingClipboardRestoration()
            isInRecordingSession = true
            return
        }

        // Cancel any pending restoration from a previous recording
        cancelPendingClipboardRestoration()

        // The session opens and the last session's snapshot goes BEFORE the read
        // awaits (#879). A paste that lands while the read is in flight then
        // restores nothing, never an older clipboard, and an endRecordingSession()
        // in that window is not undone when the read returns.
        originalClipboardData = nil
        isInRecordingSession = true
        let changeCountBeforeRead = NSPasteboard.general.changeCount

        // ENHANCED: Extract DATA from all clipboard items, off the main actor.
        // We cannot store the NSPasteboardItem objects directly because they cannot be reused
        let snapshot = await readRecordingStartSnapshot()

        // A newer startRecordingSession() owns the state now: drop this result.
        guard generation == Self.clipboardSnapshotGeneration else {
            logger.info("📋 A newer recording session started while the clipboard was read; dropping this snapshot")
            return
        }

        // Something wrote to the clipboard while it was read (a paste, or the
        // user's own copy). The snapshot may mix the two and no longer shows the
        // clipboard from before this recording, so keep nothing.
        guard NSPasteboard.general.changeCount == changeCountBeforeRead else {
            logger.info("📋 The clipboard changed while it was read; nothing to save")
            return
        }

        originalClipboardData = snapshot

        // Log what types we captured for debugging
        if let data = snapshot {
            let types = data.flatMap { $0.types }.map { $0.rawValue }
            let uniqueTypes = Set(types)
            logger.info("📋 Saved clipboard with \(data.count, privacy: .public) item(s) containing types: \(uniqueTypes.prefix(5).joined(separator: ", "), privacy: .public)")
        } else {
            logger.info("📋 Clipboard is empty or was not read in time, nothing to save")
        }
    }

    /// Bumped by every `startRecordingSession()`, so a read that returns after a
    /// newer call began never writes the session state (#879).
    private static var clipboardSnapshotGeneration = 0

    /// The record-start read: the shared 1 s reader on `NSPasteboard.general`.
    /// A Debug test can stand in for it (`recordingStartSnapshotOverrideForTesting`).
    private func readRecordingStartSnapshot() async -> [ClipboardItemData]? {
        #if DEBUG
        if let override = recordingStartSnapshotOverrideForTesting {
            return await override()
        }
        #endif
        return await ClipboardSnapshotReader.shared.snapshot(caller: "recording start")
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
        restoreExpectedChangeCount = nil
    }

    /// The app itself wrote to the clipboard and then put back what was there
    /// (the streaming paste, #1591). `before` is the change count right before
    /// that first write, `after` the one right after the write-back. When the
    /// pending restore expected `before`, the clipboard holds what it held then,
    /// so the restore now expects `after` and still runs. Any other `before`
    /// means a write the app did not make came first: leave the expectation, so
    /// the restore keeps skipping.
    func clipboardRoundTripRestored(from before: Int, to after: Int) {
        guard activeRestorationWorkItem != nil, restoreExpectedChangeCount == before else { return }
        restoreExpectedChangeCount = after
    }

    /// Call at an exit that pasted nothing, left the transcript on the clipboard
    /// and armed no restore (#783, #1034). `transcriptChangeCount` is
    /// `NSPasteboard.general.changeCount` read right after the transcript was
    /// written, so a write in between reads as the user's own copy (#1061).
    func keepClipboardSnapshotForNextRecording(transcriptChangeCount: Int, settings: SettingsManager?) {
        guard settings?.restoreClipboardAfterPaste == true, originalClipboardData != nil else {
            keptClipboardSnapshotChangeCount = nil
            return
        }
        keptClipboardSnapshotChangeCount = transcriptChangeCount
    }

    /// Restore was turned off: nothing will write the older clipboard back, so
    /// stop keeping it for the next recording (#1061).
    func dropKeptClipboardSnapshot() {
        guard keptClipboardSnapshotChangeCount != nil else { return }
        keptClipboardSnapshotChangeCount = nil
        if !isInRecordingSession && activeRestorationWorkItem == nil {
            originalClipboardData = nil
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
    /// Use executePasteAsync() instead which properly manages clipboard restoration
    /// across multiple recordings. This method is only for manual copy operations.
    func copyToClipboard(_ text: String, respectSettings settings: SettingsManager?) {
        // For manual copy operations (not auto-paste), we still do simple restoration
        // This is used when user manually copies from history or when auto-paste fails
        if let settings = settings, settings.restoreClipboardAfterPaste {
            // Save current clipboard
            let previousContent = getClipboardContent()

            // Copy new text
            copyToClipboard(text)
            // Read now, so a later write (the user's own copy) breaks the match (#1591).
            let copiedChangeCount = NSPasteboard.general.changeCount

            // Schedule simple restoration (not tied to recording sessions)
            if let previous = previousContent {
                let delay = settings.clipboardRestoreDelaySeconds
                DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
                    guard let self else { return }
                    // Something else wrote to the clipboard since this copy: that
                    // is the user's clipboard now, so leave it (#1591).
                    guard NSPasteboard.general.changeCount == copiedChangeCount else {
                        self.logger.info("📋 Clipboard changed since the copy; skipped the restore (manual copy)")
                        return
                    }
                    self.copyToClipboard(previous)
                    self.logger.info("♻️ Restored previous clipboard content (manual copy)")
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
    ///
    /// **A copy made inside the restore window survives (#1591):**
    /// `transcriptChangeCount` is `NSPasteboard.general.changeCount` read right
    /// after the transcript was written. When the restore runs and the count has
    /// moved, something else wrote to the clipboard since (the user copied
    /// something new). The restore then writes nothing and drops the snapshot,
    /// so no later restore writes the stale clipboard back either. The one
    /// exception keeps the snapshot: the clipboard holds an unpasted transcript
    /// the app wrote itself (#1061 mark), so the next recording still keeps it.
    func scheduleClipboardRestoration(settings: SettingsManager?, transcriptChangeCount: Int) {
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
        restoreExpectedChangeCount = transcriptChangeCount

        // Create a new work item for restoration
        let workItem = DispatchWorkItem { [weak self] in
            guard let self = self else { return }

            // Check if this work item is still the active one (not cancelled)
            if self.activeRestorationWorkItem?.isCancelled == false {
                let pasteboard = NSPasteboard.general

                // #1591: put the snapshot back only over the app's own write.
                if let expected = self.restoreExpectedChangeCount,
                   pasteboard.changeCount != expected {
                    self.skipRestorationAfterForeignWrite()
                    return
                }
                self.restoreExpectedChangeCount = nil

                // ENHANCED: Restore ALL clipboard types (text, images, files, etc.)
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

    /// The restore found a clipboard the app did not write (#1591): write
    /// nothing, and disarm. The snapshot goes too, so no later restore writes
    /// the stale clipboard back over the user's copy, unless the clipboard holds
    /// an unpasted transcript of the app's own (the #1061 mark matches): then
    /// the next recording still keeps the user's older clipboard.
    private func skipRestorationAfterForeignWrite() {
        activeRestorationWorkItem = nil
        restoreExpectedChangeCount = nil

        if let kept = keptClipboardSnapshotChangeCount,
           kept == NSPasteboard.general.changeCount {
            logger.info("📋 Clipboard holds an unpasted transcript; skipped the restore and kept the clipboard snapshot for the next recording")
            return
        }

        originalClipboardData = nil
        logger.info("📋 Clipboard changed since the transcript was written; skipped the restore and dropped the clipboard snapshot")
    }
}

// MARK: - Clipboard Snapshot Off the Main Actor (#879)

/// What a pasteboard read got through, for the deadline log line. Counts only:
/// never pasteboard data, never type names.
final class ClipboardSnapshotProgress: @unchecked Sendable {
    private struct Counts {
        var itemCount = 0
        var typeCount = 0
    }

    private let counts = OSAllocatedUnfairLock(initialState: Counts())

    var itemCount: Int { counts.withLock { $0.itemCount } }
    var typeCount: Int { counts.withLock { $0.typeCount } }

    func setItemCount(_ count: Int) {
        counts.withLock { $0.itemCount = count }
    }

    func addType() {
        counts.withLock { $0.typeCount += 1 }
    }
}

/// Copies the general pasteboard's data off the main actor, and stops waiting
/// after a deadline (#879). The ONE implementation of that copy, shared by the
/// recording-start snapshot (`AccessibilityHelper.startRecordingSession()`) and
/// the streaming paste (`TextInputService`).
///
/// - The read runs on one dedicated serial queue (the `SimpleRecorder.recorderStartQueue`
///   pattern), never on the main actor, and never two at once.
/// - Past the deadline the caller gets nil, and a metadata-only line is logged.
/// - A read cannot be cancelled: `data(forType:)` has no timeout. A read that
///   returns after its caller gave up is thrown away, so it never reaches anyone's
///   state. While such a read is still blocked, `snapshot` returns nil at once
///   instead of queueing behind it, so a stuck pasteboard owner holds up no later
///   recording or paste, and the queue never holds more than the stuck read plus
///   reads that will skip their work when they reach the front.
///
/// The deadline protocol itself is `DeadlineGate`, shared with `RecorderStartGate`.
///
/// `@unchecked Sendable`: no mutable state of its own; `DeadlineGate` owns it.
final class ClipboardSnapshotReader: @unchecked Sendable {

    typealias Snapshot = [AccessibilityHelper.ClipboardItemData]

    /// Reads the pasteboard. Runs on the reader's queue. Test seam: a test passes
    /// a stub that blocks; production passes `readGeneralPasteboard`.
    typealias Provider = @Sendable (ClipboardSnapshotProgress) -> Snapshot?

    /// Ray, 2026-10-07 (#879, inbox ask #227): 1 s on both paths.
    static let productionDeadline: Duration = .seconds(1)

    static let shared = ClipboardSnapshotReader(
        queue: DispatchQueue(label: "com.hyperwhisper.clipboard.snapshot", qos: .userInitiated),
        deadline: productionDeadline
    )

    private let gate: DeadlineGate
    private let deadlineNanoseconds: Int
    private let logger = AppLogger.ui

    init(queue: DispatchQueue, deadline: Duration) {
        let deadlineNanoseconds = Self.nanoseconds(deadline)
        self.deadlineNanoseconds = deadlineNanoseconds
        // `.skip`: a read whose caller already gave up while it waited on the
        // queue never touches the pasteboard, so reads do not pile up there.
        self.gate = DeadlineGate(
            queue: queue,
            timeout: .nanoseconds(deadlineNanoseconds),
            queuedPastDeadline: .skip
        )
    }

    /// True while a read whose caller already gave up is still on the queue.
    var hasAbandonedRead: Bool {
        gate.hasAbandonedWork
    }

    /// The pasteboard's data, or nil when it is empty, when the read passed the
    /// deadline, or when an earlier read is still stuck.
    func snapshot(
        caller: String,
        provider: @escaping Provider = ClipboardSnapshotReader.generalPasteboardProvider
    ) async -> Snapshot? {
        let deadlineMs = deadlineNanoseconds / 1_000_000

        if hasAbandonedRead {
            logger.warning("📋 Clipboard snapshot skipped (\(caller, privacy: .public)): an earlier read is still blocked past its \(deadlineMs, privacy: .public) ms deadline; the clipboard will not be restored")
            return nil
        }

        let progress = ClipboardSnapshotProgress()
        let started = ContinuousClock.now

        // A late read's data is dropped on the queue: the caller already got nil.
        let outcome = await gate.run({ provider(progress) }, discardLate: { _ in })

        switch outcome {
        case .finished(let value):
            return value
        case .timedOut:
            let elapsedMs = Self.nanoseconds(started.duration(to: .now)) / 1_000_000
            let itemCount = progress.itemCount
            let typeCount = progress.typeCount
            logger.warning("📋 Clipboard snapshot passed its deadline (\(caller, privacy: .public)): items=\(itemCount, privacy: .public) types=\(typeCount, privacy: .public) elapsedMs=\(elapsedMs, privacy: .public) deadlineMs=\(deadlineMs, privacy: .public); the clipboard will not be restored")
            return nil
        }
    }

    /// The production provider, as a `@Sendable` value.
    static let generalPasteboardProvider: Provider = { progress in
        readGeneralPasteboard(progress)
    }

    /// The production read: every type of every item on the general pasteboard.
    /// Blocking. Runs only on the reader's queue.
    static func readGeneralPasteboard(_ progress: ClipboardSnapshotProgress) -> Snapshot? {
        guard let items = NSPasteboard.general.pasteboardItems, !items.isEmpty else {
            return nil
        }
        progress.setItemCount(items.count)

        return items.compactMap { item -> AccessibilityHelper.ClipboardItemData? in
            var dataByType: [NSPasteboard.PasteboardType: Data] = [:]

            // Extract data for each type this item supports
            for type in item.types {
                progress.addType()
                if let data = item.data(forType: type) {
                    dataByType[type] = data
                }
            }

            // Only include items that have at least one type with data
            guard !dataByType.isEmpty else { return nil }

            return AccessibilityHelper.ClipboardItemData(types: item.types, data: dataByType)
        }
    }

    private static func nanoseconds(_ duration: Duration) -> Int {
        let (seconds, attoseconds) = duration.components
        return Int(seconds) * 1_000_000_000 + Int(attoseconds / 1_000_000_000)
    }
}
