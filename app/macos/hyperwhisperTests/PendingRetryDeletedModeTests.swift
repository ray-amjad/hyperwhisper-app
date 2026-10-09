//
//  PendingRetryDeletedModeTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1617.
//
//  The recording dialog's pending-file Retry (audio that failed before
//  transcription started) looked its mode up with the default fallback ON. If
//  the mode that made the recording had been deleted, the retry silently used
//  the default mode, which on a fresh install is the Cloud mode "Hyper", so
//  audio an on-device mode recorded could go to HyperWhisper Cloud unasked.
//
//  Ray (2026-10-09, inbox ask #444): send nothing, open a mode picker, retry
//  with the picked mode. Dismissing the picker sends nothing and keeps the file.
//
//  `retryTranscriptionFromPendingPath` needs a live `RecordingLifecycle` and a
//  pipeline, so it cannot run here (see `PendingRetrySupersessionTests`). The
//  mode decision is lifted into `RecordingTranscriptionFlow.resolvePendingRetryMode`
//  and tested on an in-memory store; the source tests pin that the retry
//  routes through it and stops before any request when it returns nil.
//

import CoreData
import Foundation
import Testing

@testable import HyperWhisper

@Suite("Pending-file Retry asks for a mode when its mode was deleted (#1617)")
struct PendingRetryDeletedModeTests {

    // MARK: - Fixtures

    @MainActor
    private func makeMode(
        in persistence: PersistenceController,
        name: String,
        model: String,
        isDefault: Bool,
        postProcessing: PostProcessingMode = .off
    ) -> Mode {
        let mode = Mode(context: persistence.container.viewContext)
        mode.id = UUID()
        mode.name = name
        mode.model = model
        mode.isDefault = isDefault
        mode.postProcessingMode = postProcessing.rawValue
        // Mandatory with no default: without them `save()` only logs a
        // validation failure and nothing is written.
        mode.createdDate = Date()
        mode.modifiedDate = Date()
        return mode
    }

    /// The store is held by the TEST for its whole length (see
    /// `TranscriptionRetryDeletedModeTests`): a dropped store reads every
    /// attribute back as its zero value.
    ///
    /// A fresh install's default (the Cloud mode "Hyper") plus the on-device
    /// mode that made the recording, saved so the background lookup sees them.
    @MainActor
    private func makeStore() -> (PersistenceController, hyper: Mode, onDevice: Mode) {
        let persistence = PersistenceController(inMemory: true)
        let hyper = makeMode(in: persistence, name: "Hyper", model: "cloud", isDefault: true)
        let onDevice = makeMode(
            in: persistence, name: "LocNemo", model: "nemotron-asr-3.5-latin", isDefault: false
        )
        persistence.save()
        return (persistence, hyper, onDevice)
    }

    // MARK: - The decision

    /// The issue's own sequence: the session's mode is deleted, a Cloud
    /// default exists. The retry must get no mode (so it asks), never Hyper.
    @MainActor
    @Test func aDeletedSessionModeResolvesToNoModeNotTheCloudDefault() async {
        let (persistence, _, onDevice) = makeStore()
        let id = onDevice.id!.uuidString
        persistence.deleteMode(onDevice)

        let mode = await RecordingTranscriptionFlow.resolvePendingRetryMode(
            pickedMode: nil,
            sessionModeId: id,
            sessionModeName: "LocNemo",
            persistence: persistence
        )
        #expect(mode == nil, "fell back to \(mode?.name ?? "nil")")
    }

    /// Control: a session mode that still exists resolves to itself, as today.
    @MainActor
    @Test func aLiveSessionModeResolvesToItself() async {
        let (persistence, _, onDevice) = makeStore()

        let mode = await RecordingTranscriptionFlow.resolvePendingRetryMode(
            pickedMode: nil,
            sessionModeId: onDevice.id!.uuidString,
            sessionModeName: "LocNemo",
            persistence: persistence
        )
        #expect(mode?.objectID == onDevice.objectID)
    }

    /// Control: the by-name step of the old chain still finds a live mode.
    @MainActor
    @Test func aLiveSessionModeIsStillFoundByName() async {
        let (persistence, _, onDevice) = makeStore()

        let mode = await RecordingTranscriptionFlow.resolvePendingRetryMode(
            pickedMode: nil,
            sessionModeId: "",
            sessionModeName: "LocNemo",
            persistence: persistence
        )
        #expect(mode?.objectID == onDevice.objectID)
    }

    /// After the picker: the picked mode wins over the deleted session mode.
    @MainActor
    @Test func aPickedModeIsTheOneUsed() async {
        let (persistence, _, onDevice) = makeStore()
        let deletedId = onDevice.id!.uuidString
        persistence.deleteMode(onDevice)
        let picked = makeMode(in: persistence, name: "Local Whisper", model: "whisper-base", isDefault: false)
        persistence.save()

        let mode = await RecordingTranscriptionFlow.resolvePendingRetryMode(
            pickedMode: PendingRetryModeChoice(id: picked.id!.uuidString, name: "Local Whisper"),
            sessionModeId: deletedId,
            sessionModeName: "LocNemo",
            persistence: persistence
        )
        #expect(mode?.objectID == picked.objectID)
    }

    /// A pick deleted before the retry ran asks again; it never falls back.
    @MainActor
    @Test func aPickedModeDeletedMeanwhileResolvesToNoMode() async {
        let (persistence, _, onDevice) = makeStore()
        let picked = PendingRetryModeChoice(id: onDevice.id!.uuidString, name: "LocNemo")
        persistence.deleteMode(onDevice)

        let mode = await RecordingTranscriptionFlow.resolvePendingRetryMode(
            pickedMode: picked,
            sessionModeId: "",
            sessionModeName: "Default",
            persistence: persistence
        )
        #expect(mode == nil, "fell back to \(mode?.name ?? "nil")")
    }

    /// A pick deleted before the retry ran must not resolve to ANOTHER mode
    /// that shares its name (maybe a Cloud one): a pick resolves by id only.
    @MainActor
    @Test func aDeletedPickNeverResolvesToASameNamedMode() async {
        let (persistence, _, onDevice) = makeStore()
        let picked = PendingRetryModeChoice(id: onDevice.id!.uuidString, name: "LocNemo")
        persistence.deleteMode(onDevice)
        let sameName = makeMode(in: persistence, name: "LocNemo", model: "cloud", isDefault: false)
        persistence.save()

        let mode = await RecordingTranscriptionFlow.resolvePendingRetryMode(
            pickedMode: picked,
            sessionModeId: "",
            sessionModeName: "Default",
            persistence: persistence
        )
        withExtendedLifetime(sameName) {
            #expect(mode == nil, "resolved the pick by name to \(mode?.name ?? "nil")")
        }
    }

    // MARK: - The picker's state

    /// The picker belongs to one pending file. A new file, or none (a new
    /// dictation, a successful retry), drops it.
    @MainActor
    @Test func thePickerFlagResetsWhenThePendingFileChanges() {
        let appState = AppState()
        appState.pendingRetryAudioPath = "/tmp/a.caf"
        appState.pendingRetryNeedsModePick = true

        appState.pendingRetryAudioPath = "/tmp/a.caf"
        #expect(appState.pendingRetryNeedsModePick, "re-setting the same file must keep the picker")

        appState.pendingRetryAudioPath = "/tmp/b.caf"
        #expect(!appState.pendingRetryNeedsModePick)

        appState.pendingRetryNeedsModePick = true
        appState.pendingRetryAudioPath = nil
        #expect(!appState.pendingRetryNeedsModePick)
    }

    // MARK: - The picker's list matches History's "Retry with..."

    @MainActor
    @Test func onlineEveryModeIsAChoice() {
        let (persistence, hyper, onDevice) = makeStore()
        withExtendedLifetime(persistence) {
            let choices = RetryModeChoices.available(from: [hyper, onDevice], isOnline: true)
            #expect(choices.map(\.objectID) == [hyper.objectID, onDevice.objectID])
        }
    }

    @MainActor
    @Test func offlineModesThatNeedTheInternetAreLeftOut() {
        let (persistence, hyper, onDevice) = makeStore()
        let cloudPolish = makeMode(
            in: persistence, name: "Polish", model: "whisper-base", isDefault: false, postProcessing: .cloud
        )
        withExtendedLifetime(persistence) {
            let choices = RetryModeChoices.available(from: [hyper, onDevice, cloudPolish], isOnline: false)
            #expect(choices.map(\.objectID) == [onDevice.objectID])
        }
    }

    // MARK: - Call sites, read from the production source

    private static let errorHandlingPath =
        "app/macos/hyperwhisper/Managers/AudioRecording/RecordingFlow/RecordingTranscriptionFlow+ErrorHandling.swift"

    private static func retryBody() throws -> String {
        try ProductionSource.slice(
            of: errorHandlingPath,
            from: "private func retryTranscriptionFromPendingPath(",
            to: "func handleRecordingStartFailure("
        )
    }

    /// The retry resolves through the no-default resolver and nothing else.
    @Test func theRetryNeverResolvesWithTheDefaultFallback() throws {
        let body = try Self.retryBody()
        #expect(body.contains("resolvePendingRetryMode("), "\(body)")
        #expect(!body.contains("resolveTranscriptionModeInBackground("),
                "the retry looks its mode up directly again: \(body)")

        let resolver = try ProductionSource.slice(
            of: Self.errorHandlingPath,
            from: "static func resolvePendingRetryMode(",
            to: "private func retryTranscriptionFromPendingPath("
        )
        #expect(resolver.contains("allowDefaultFallback: false"), "\(resolver)")
        #expect(resolver.contains("allowNameFallback: false"), "a pick resolves by name again: \(resolver)")
    }

    /// No mode: ask for one, and stop before anything is sent or the pending
    /// file is dropped.
    @Test func noModeOpensThePickerBeforeAnyRequest() throws {
        let body = try Self.retryBody()
        let ask = try #require(body.range(of: "appState.pendingRetryNeedsModePick = true"),
                               "the retry no longer asks for a mode")
        let request = try #require(body.range(of: "transcribeWithDetails("), "the retry no longer transcribes")
        #expect(ask.lowerBound < request.lowerBound)

        let transcribing = try #require(body.range(of: "appState.recordingState = .transcribing"))
        let askArm = body[ask.lowerBound..<transcribing.lowerBound]
        #expect(askArm.contains("return"), "the no-mode branch falls through to the request: \(askArm)")
        #expect(!askArm.contains("pendingRetryAudioPath = nil"), "the no-mode branch drops the audio: \(askArm)")
    }

    /// The dialog and History list the same modes.
    @Test func theDialogAndHistoryShareTheModeList() throws {
        let dialog = try ProductionSource.code(of: "app/macos/hyperwhisper/Views/RecordingDialog.swift")
        let history = try ProductionSource.code(of: "app/macos/hyperwhisper/Views/HistoryView.swift")
        #expect(dialog.contains("RetryModeChoices.available("))
        #expect(history.contains("RetryModeChoices.available("))
        #expect(dialog.contains("retryTranscriptionFromPendingFile(with:"))
    }
}
