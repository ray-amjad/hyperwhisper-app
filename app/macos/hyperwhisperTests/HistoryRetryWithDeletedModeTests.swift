//
//  HistoryRetryWithDeletedModeTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1620.
//
//  History fetched its "Retry with..." modes once, when the screen opened. A
//  mode deleted while it stayed open (the Modes page, Local API
//  `DELETE /modes/<id>`) stayed in the list as a dead managed object whose
//  name reads nil, so the submenu showed "Unknown". Picking it would hand a
//  nil model to the router, which treats that as Cloud (the #1440 shape).
//
//  Now the list drops dead modes, History refetches it on any saved Mode
//  change, and "Retry with" refuses a dead mode before anything is sent.
//

import CoreData
import Foundation
import Testing

@testable import HyperWhisper

@Suite("History Retry with... never lists or uses a deleted mode (#1620)")
struct HistoryRetryWithDeletedModeTests {

    // MARK: - Fixtures

    @MainActor
    private func makeMode(in persistence: PersistenceController, name: String, model: String) -> Mode {
        let mode = Mode(context: persistence.container.viewContext)
        mode.id = UUID()
        mode.name = name
        mode.model = model
        mode.postProcessingMode = PostProcessingMode.off.rawValue
        mode.createdDate = Date()
        mode.modifiedDate = Date()
        return mode
    }

    /// The issue's store: three modes, then the test mode is deleted.
    @MainActor
    private func makeStore() -> (PersistenceController, kept: [Mode], doomed: Mode) {
        let persistence = PersistenceController(inMemory: true)
        let hyper = makeMode(in: persistence, name: "Hyper", model: "cloud")
        let nemotron = makeMode(in: persistence, name: "HW771 Nemotron", model: "nemotron-asr-3.5-latin")
        let doomed = makeMode(in: persistence, name: "V1440 Nemo", model: "nemotron-asr-3.5-latin")
        persistence.save()
        return (persistence, [hyper, nemotron], doomed)
    }

    // MARK: - The list

    /// The issue's sequence: the list was fetched, then a mode was deleted
    /// and saved. Only the remaining modes are choices, online or offline.
    @MainActor
    @Test func aModeDeletedAfterTheFetchIsNotAChoice() {
        let (persistence, kept, doomed) = makeStore()
        withExtendedLifetime(persistence) {
            let fetched = kept + [doomed]
            persistence.deleteMode(doomed)

            #expect(!RetryModeChoices.isLive(doomed))

            let online = RetryModeChoices.available(from: fetched, isOnline: true)
            #expect(online.map(\.objectID) == kept.map(\.objectID))

            let offline = RetryModeChoices.available(from: fetched, isOnline: false)
            #expect(offline.map(\.objectID) == [kept[1].objectID])
        }
    }

    /// A delete not saved yet is also not a choice.
    @MainActor
    @Test func anUnsavedDeleteIsNotAChoice() {
        let (persistence, kept, doomed) = makeStore()
        withExtendedLifetime(persistence) {
            persistence.container.viewContext.delete(doomed)
            let choices = RetryModeChoices.available(from: kept + [doomed], isOnline: true)
            #expect(choices.map(\.objectID) == kept.map(\.objectID))
        }
    }

    /// Control: live modes are all still choices.
    @MainActor
    @Test func liveModesStayChoices() {
        let (persistence, kept, doomed) = makeStore()
        withExtendedLifetime(persistence) {
            #expect(RetryModeChoices.isLive(doomed))
            let choices = RetryModeChoices.available(from: kept + [doomed], isOnline: true)
            #expect(choices.count == 3)
        }
    }

    // MARK: - Call sites, read from the production source

    private static let historyPath = "app/macos/hyperwhisper/Views/HistoryView.swift"
    private static let handlerPath =
        "app/macos/hyperwhisper/Managers/Transcription/Support/TranscriptActionHandler.swift"

    /// History refetches its mode list when a save touches a Mode, not only
    /// when the screen opens.
    @Test func historyRefreshesTheModeListOnAModeSave() throws {
        let observer = try ProductionSource.slice(
            of: Self.historyPath,
            from: "private func observeTranscriptChanges()",
            to: "private static func containsChanges("
        )
        let modeCheck = try #require(
            observer.range(of: "containsChanges(to: \"Mode\""),
            "the save observer no longer checks for Mode changes: \(observer)"
        )
        let refresh = try #require(observer.range(of: "refreshAvailableModes()"), "\(observer)")
        #expect(modeCheck.lowerBound < refresh.lowerBound)
    }

    /// "Retry with" refuses a dead mode before it copies audio, makes a row,
    /// or starts a transcription.
    @Test func retryWithRefusesADeadModeBeforeAnyWork() throws {
        let body = try ProductionSource.slice(
            of: Self.handlerPath,
            from: "func retryTranscription(_ transcript: Transcript, with mode: Mode) async -> Bool {",
            to: "Self.trackRetryTask("
        )
        let guardRange = try #require(body.range(of: "guard RetryModeChoices.isLive(mode) else"), "\(body)")
        for later in ["copyItem(", "createProcessingTranscript(", "transcribeWithDetails("] {
            let use = try #require(body.range(of: later), "missing \(later)")
            #expect(guardRange.lowerBound < use.lowerBound, "\(later) runs before the live check")
        }
    }
}
