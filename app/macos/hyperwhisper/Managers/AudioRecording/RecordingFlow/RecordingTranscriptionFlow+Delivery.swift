//
//  RecordingTranscriptionFlow+Delivery.swift
//  hyperwhisper
//
//  How a finished batch transcript reaches the user and History. Shared by a
//  dictation's stop flow and the recording pill's pending-file Retry (#1636),
//  so the two cannot drift: before #1636 the Retry showed "Pasted!" but
//  delivered nothing and saved nothing.
//

import AVFoundation
import CoreData
import Foundation

extension RecordingTranscriptionFlow {

    // MARK: - Delivery

    /// Delivers a finished batch transcript the way a dictation does: Quick
    /// Capture to Notes, else auto-paste into the previously focused app (which
    /// falls back to the clipboard when it cannot paste), else the pill keeps
    /// the text with a Copy button. Sets `transcriptionPasteFailed` and
    /// `showRecordingDialog` so the pill says "Pasted!" only after a real paste.
    ///
    /// The caller sets `lastTranscription` and `recordingState` first, in the
    /// same main-actor turn, and owns any session cleanup.
    ///
    /// - Parameters:
    ///   - sessionStartedSuppressed: whether delivery was suppressed when the
    ///     recording began (onboarding).
    ///   - isQuickCaptureRouting: route to Notes instead of pasting.
    ///   - pasteStart: when delivery began, for the paste-latency log line.
    func deliverBatchTranscript(
        _ transcriptionResult: TranscriptionResult,
        transcriptionMode: Mode?,
        sessionStartedSuppressed: Bool,
        trigger: RecordingTriggerSource,
        isQuickCaptureRouting: Bool,
        pasteStart: Date
    ) {
        // ONBOARDING: the transcript is surfaced inline in the onboarding
        // window only and must NEVER paste into another app, regardless of
        // the user's global `pasteResultText` setting. The delivery primitives
        // themselves refuse to emit while the gate is suppressed, but we ALSO
        // skip at the caller here: if we let `handleAutoPaste` reach the guarded
        // `sendPasteCommand`, it returns false and the failure branch would pop
        // the recording dialog *behind* the onboarding sheet. So the batch
        // caller must not enter delivery at all. `TextDeliveryGate.isSuppressed`
        // tracks the onboarding sheet's lifetime; the explicit `.onboarding`
        // trigger term is belt-and-suspenders. `lastTranscription` was already
        // set by the caller, which the onboarding view observes to render
        // "You said …".
        let suppressForOnboarding = RecordingTextDeliveryPolicy.shouldSuppress(
            sessionStartedSuppressed: sessionStartedSuppressed,
            currentlySuppressed: TextDeliveryGate.isSuppressed,
            trigger: trigger
        )
        let shouldDeliverText = !suppressForOnboarding
            && (isQuickCaptureRouting
                || (settingsManager?.pasteResultText ?? false))

        if suppressForOnboarding {
            // The onboarding view owns this result. Do not misclassify
            // intentional suppression as a paste failure or leave the
            // floating recording dialog open behind the onboarding sheet.
            appState?.transcriptionPasteFailed = false
            appState?.showRecordingDialog = false
            appState?.isStreamingShortcutTriggered = false
        } else if shouldDeliverText, let settings = settingsManager {
            var processedText = transcriptionResult.text

            // REMOVE TRAILING PERIOD:
            // When enabled, strip the final period from transcriptions (but preserve ellipsis).
            // Applied after post-processing but before smart spacing and auto-paste.
            if transcriptionMode?.removeTrailingPeriod == true {
                processedText = TranscriptionTextProcessing.removeTrailingPeriod(processedText)
            }

            // Snapshot for the Quick Capture path: Notes wants a fresh-note
            // transcript before any paste-target adjustments below mutate it.
            let notesText = processedText

            // AUTOCAPITALIZE INSERT:
            // Lowercase the first letter when the caret is mid-sentence
            // in the focused text field. Sentence-start / unknown context
            // pass through unchanged. Any AX failure returns .unknown so
            // the text is left alone.
            if settings.autocapitalizeInsert {
                let context = AccessibilityHelper.shared.cursorContextOfFocusedElement()
                processedText = AutocapitalizeInsert.apply(processedText, context: context)
            }

            // SMART SPACING FOR CONSECUTIVE TRANSCRIPTIONS:
            // Adds trailing space based on language to enable seamless consecutive dictation.
            // - Space-delimited languages (English, Danish, German, etc.): adds trailing space
            // - CJK languages (Japanese, Chinese, Korean): no trailing space (words aren't separated by spaces)
            // - Auto-detect mode: analyzes text content for CJK characters
            //
            // This solves the issue where consecutive recordings would paste without spacing:
            // "Hello world.How are you?" → "Hello world. How are you?"
            let modeLanguage = transcriptionMode?.language ?? "en"
            let spacedText = SmartSpacing.appendTrailingSpace(processedText, modeLanguage: modeLanguage)

            // Drives the success toast: "Saved to Notes!" vs "Pasted!".
            // Set synchronously before the delivery await so RecordingDialog
            // sees the correct value when `lastTranscription` changes — the
            // Notes await can block 0.5–2s on cold launch, long enough for
            // the dialog to render "Pasted!" first if we set this later.
            appState?.lastDeliveryWasQuickCapture = isQuickCaptureRouting

            // Quick Capture sessions go to Notes; everything else uses the
            // accessibility-driven paste into the previously focused app.
            Task { @MainActor in
                let delivered: Bool
                if isQuickCaptureRouting {
                    // Notes gets the un-paste-adjusted transcript:
                    // AutocapitalizeInsert reads the *previously focused*
                    // app's caret context (Slack/Safari/etc) and would
                    // demote a brand-new note's first letter; SmartSpacing's
                    // trailing space is for seamless paste, not a fresh note.
                    delivered = await NotesDestination.send(text: notesText)
                } else {
                    delivered = await autoPasteHandler.handleAutoPaste(spacedText)
                }

                // Paste runs concurrently with the Core Data write, so its
                // latency is logged here rather than in the flow-completion line.
                let pasteElapsedMs = Int(Date().timeIntervalSince(pasteStart) * 1000)
                if delivered {
                    appState?.transcriptionPasteFailed = false
                    appState?.showRecordingDialog = false
                    appState?.isStreamingShortcutTriggered = false
                    if isQuickCaptureRouting {
                        AppLogger.audio.info("✅ Quick Capture: saved to Notes — closing dialog · pasteMs=\(pasteElapsedMs)")
                    } else {
                        AppLogger.audio.info("✅ Auto-paste succeeded - closing dialog · pasteMs=\(pasteElapsedMs)")
                    }
                } else {
                    // Paste path: text is on the clipboard.
                    // Quick Capture path: NotesDestination has surfaced the banner.
                    appState?.transcriptionPasteFailed = true
                    appState?.showRecordingDialog = true
                    AppLogger.audio.info("📋 Text delivery failed - keeping dialog open · pasteMs=\(pasteElapsedMs)")
                }
            }
        } else {
            // AUTO-PASTE DISABLED: Keep dialog open
            AppLogger.audio.info("📋 Auto-paste disabled - transcription in dialog only")
            appState?.transcriptionPasteFailed = true
            appState?.showRecordingDialog = true
        }

        // PRIVACY: Don't log actual transcription text - users export diagnostic logs
        let wordCount = transcriptionResult.text.split(separator: " ").count
        AppLogger.audio.info("✅ Transcription complete: \(transcriptionResult.text.count) chars, \(wordCount) words")
    }

    // MARK: - History

    /// Completes a History row with a finished batch transcript, on the serial
    /// background writer. The one place the result's fields map onto the row,
    /// for the stop flow and the pending-file Retry alike (#1636).
    ///
    /// Saves the same text either way: whatever the stop flow saves, a retry
    /// saves too.
    ///
    /// - Parameter clearFailedReason: the row was saved as FAILED and a retry
    ///   is completing it (see `updateTranscriptWithTranscriptionInBackground`).
    /// - Returns: whether the row was found and the write was saved.
    @discardableResult
    static func saveBatchTranscript(
        _ transcriptionResult: TranscriptionResult,
        to transcriptID: NSManagedObjectID,
        clearFailedReason: Bool = false,
        persistence: PersistenceController = .shared
    ) async -> Bool {
        await persistence.updateTranscriptWithTranscriptionInBackground(
            transcriptID: transcriptID,
            transcribedText: transcriptionResult.rawText,
            postProcessedText: transcriptionResult.wasPostProcessed ? transcriptionResult.text : nil,
            transcriptionProvider: transcriptionResult.provider,
            postProcessingProvider: transcriptionResult.postProcessingProvider,
            wordTimestampsJSON: transcriptionResult.timestamps?.wordTimestampsJSON(),
            clearFailedReason: clearFailedReason
        )
    }

    /// Saves a successful pending-file Retry's transcript to History (#1636).
    ///
    /// Completes the failed "Audio file could not be read" row in place, as
    /// History's own Retry does. Awaits that row's write first: a Retry can
    /// finish before it lands, and saving then would leave two rows, the
    /// failed one and the retry's. A new completed row is the fallback, only
    /// when there is no such row: its write failed, or the user deleted it
    /// meanwhile.
    ///
    /// - Parameter failedRowWrite: the failed row's write
    ///   (`AppState.pendingRetryFailedRowWrite`), landed or not.
    /// - Returns: the id of the row that now holds the transcript, or `nil`
    ///   when no write could be saved.
    @discardableResult
    static func savePendingRetryTranscript(
        _ transcriptionResult: TranscriptionResult,
        failedRowWrite: Task<NSManagedObjectID?, Never>?,
        audioURL: URL,
        modeName: String?,
        persistence: PersistenceController = .shared
    ) async -> NSManagedObjectID? {
        let failedTranscriptID = await failedRowWrite?.value
        if let failedTranscriptID,
           await saveBatchTranscript(
               transcriptionResult,
               to: failedTranscriptID,
               clearFailedReason: true,
               persistence: persistence
           ) {
            return failedTranscriptID
        }

        AppLogger.audio.warning("Pending-file retry: no failed History row to complete; saving the transcript as a new row")
        let duration = await audioDurationSeconds(of: audioURL)
        guard let newID = await persistence.createProcessingTranscriptInBackground(
            duration: duration,
            mode: modeName,
            audioFilePath: audioURL.path,
            trimmedAudioPath: nil
        ) else {
            AppLogger.audio.error("Pending-file retry: could not save the transcript to History")
            return nil
        }
        guard await saveBatchTranscript(transcriptionResult, to: newID, persistence: persistence) else {
            AppLogger.audio.error("Pending-file retry: could not complete the new History row")
            return nil
        }
        return newID
    }

    /// The length of the audio file in seconds, or 0 when it cannot be read.
    /// Off the main actor: `AVAudioFile(forReading:)` does synchronous file I/O.
    nonisolated static func audioDurationSeconds(of url: URL) async -> TimeInterval {
        await Task.detached(priority: .userInitiated) {
            guard let file = try? AVAudioFile(forReading: url) else { return 0 }
            let sampleRate = file.processingFormat.sampleRate
            guard sampleRate > 0 else { return 0 }
            return Double(file.length) / sampleRate
        }.value
    }
}
