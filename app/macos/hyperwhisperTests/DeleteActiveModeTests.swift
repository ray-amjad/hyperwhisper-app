//
//  DeleteActiveModeTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1439.
//
//  Deleting the ACTIVE mode on the Modes page left the app on the deleted
//  mode: the status bar kept its name, no card was highlighted, Select Mode had
//  no checkmark, and `currentModeId` / `currentMode` still held it until a
//  relaunch fell back to the default (Cloud) mode. `ModesView.deleteMode(_:)`
//  read `mode.id` only AFTER `PersistenceController.deleteMode(_:)` had deleted
//  and saved the object, when Core Data no longer gives its attribute values,
//  so the "was it selected?" test never matched. Local API
//  `DELETE /modes/:id` did no selection repair at all.
//
//  Both now go through `PersistenceController.deleteModeAndReconcileSelection`.
//  The behaviour tests call it on an in-memory store with a bare `AppState`
//  (its `settingsManager` is nil, so `persist: true` writes no UserDefaults).
//  The wiring tests pin that both callers use it, because the Modes page's
//  `deleteMode` is a `private` view method and the endpoint needs a live
//  FlyingFox request — neither can be called from here.
//

import CoreData
import Testing

@testable import HyperWhisper

@Suite("Deleting the active mode (#1439)")
struct DeleteActiveModeTests {

    /// The controller is a PARAMETER and every test holds it for its whole
    /// length: a store created and dropped inside a helper deallocates on
    /// return and every `Mode` attribute reads back as its zero value. Same
    /// trap as `DefaultModeInvariantTests`.
    @MainActor
    private func makeMode(
        in persistence: PersistenceController,
        name: String,
        isDefault: Bool,
        sortOrder: Int16
    ) -> Mode {
        let mode = Mode(context: persistence.container.viewContext)
        mode.id = UUID()
        mode.name = name
        mode.isDefault = isDefault
        mode.sortOrder = sortOrder
        return mode
    }

    @MainActor
    @Test func deletingTheSelectedModeSelectsTheFirstRemainingModeAtOnce() {
        let persistence = PersistenceController(inMemory: true)
        let first = makeMode(in: persistence, name: "Hyper", isDefault: true, sortOrder: 0)
        let active = makeMode(in: persistence, name: "ActiveRen", isDefault: false, sortOrder: 1)
        _ = makeMode(in: persistence, name: "Later", isDefault: false, sortOrder: 2)
        persistence.save()
        let firstId = first.id!.uuidString
        let activeId = active.id!.uuidString

        let appState = AppState()
        appState.selectMode(active, persist: false)
        #expect(appState.selectedModeId == activeId)

        persistence.deleteModeAndReconcileSelection(active, appState: appState, settingsManager: nil)

        #expect(persistence.fetchMode(withId: activeId) == nil)
        // The first remaining mode by sort order, not "any" mode, and every
        // field the status bar and Select Mode read moved with it.
        #expect(appState.selectedModeId == firstId)
        #expect(appState.selectedModeName == "Hyper")
        #expect(appState.selectedModeSnapshot?.id.uuidString == firstId)
    }

    @MainActor
    @Test func theFirstRemainingModeFollowsSortOrderNotTheDefaultFlag() {
        // The rule ModesView always promised is "first remaining by sort
        // order". Pin it so it cannot quietly become "the default mode".
        let persistence = PersistenceController(inMemory: true)
        let active = makeMode(in: persistence, name: "ActiveRen", isDefault: false, sortOrder: 0)
        let onDevice = makeMode(in: persistence, name: "LocNemo", isDefault: false, sortOrder: 1)
        _ = makeMode(in: persistence, name: "Hyper", isDefault: true, sortOrder: 5)
        persistence.save()
        let onDeviceId = onDevice.id!.uuidString

        let appState = AppState()
        appState.selectMode(active, persist: false)

        persistence.deleteModeAndReconcileSelection(active, appState: appState, settingsManager: nil)

        #expect(appState.selectedModeId == onDeviceId)
        #expect(appState.selectedModeName == "LocNemo")
    }

    @MainActor
    @Test func deletingAModeThatIsNotSelectedLeavesTheSelectionAlone() {
        let persistence = PersistenceController(inMemory: true)
        let selected = makeMode(in: persistence, name: "Hyper", isDefault: true, sortOrder: 0)
        let other = makeMode(in: persistence, name: "Email", isDefault: false, sortOrder: 1)
        persistence.save()
        let selectedId = selected.id!.uuidString

        let appState = AppState()
        appState.selectMode(selected, persist: false)

        persistence.deleteModeAndReconcileSelection(other, appState: appState, settingsManager: nil)

        #expect(appState.selectedModeId == selectedId)
        #expect(appState.selectedModeName == "Hyper")
    }

    @MainActor
    @Test func deletingTheOnlyModeClearsTheSelection() {
        // Neither caller lets this happen (both refuse to delete the last
        // mode), but the helper must not leave a dangling id if one ever does.
        let persistence = PersistenceController(inMemory: true)
        let only = makeMode(in: persistence, name: "Only", isDefault: true, sortOrder: 0)
        persistence.save()

        let appState = AppState()
        appState.selectMode(only, persist: false)

        persistence.deleteModeAndReconcileSelection(only, appState: appState, settingsManager: nil)

        #expect(appState.selectedModeId.isEmpty)
        #expect(appState.selectedModeSnapshot == nil)
    }

    @MainActor
    @Test func reconcileMatchesOnlyTheDeletedId() {
        let persistence = PersistenceController(inMemory: true)
        let selected = makeMode(in: persistence, name: "Hyper", isDefault: true, sortOrder: 0)
        let other = makeMode(in: persistence, name: "Email", isDefault: false, sortOrder: 1)
        persistence.save()

        let appState = AppState()
        appState.selectMode(selected, persist: false)

        // An unrelated id, and an empty one (what a nil `mode.id` became in the
        // bug), both leave the selection where it is.
        #expect(appState.reconcileSelectionAfterDeletingMode(id: UUID().uuidString, remainingModes: [other]) == false)
        #expect(appState.reconcileSelectionAfterDeletingMode(id: "", remainingModes: [other]) == false)
        #expect(appState.selectedModeId == selected.id!.uuidString)
    }

    // MARK: - Wiring: both callers use the shared delete

    @Test func theModesPageDeletesThroughTheSharedHelper() throws {
        let body = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Views/Modes/ModesView.swift",
            from: "private func deleteMode(_ mode: Mode) {",
            to: "#Preview {"
        )
        #expect(body.contains("deleteModeAndReconcileSelection("))
        #expect(body.contains("appState: appState"))
        // The bare delete is what read `mode.id` too late.
        #expect(!body.contains(".deleteMode(mode)"))
    }

    @Test func theLocalApiDeleteRepairsTheSelectionToo() throws {
        let body = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/LocalAPI/Endpoints/ModesEndpoint.swift",
            from: "static func delete(",
            to: "private static func idParameter("
        )
        #expect(body.contains("deleteModeAndReconcileSelection("))
        #expect(!body.contains(".deleteMode(mode)"))

        let trampoline = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/LocalAPI/LocalAPIServer.swift",
            from: "private func handleModeDelete(request: HTTPRequest) async -> HTTPResponse {",
            to: "private func handleTranscribe("
        )
        // Without the app's AppState the endpoint deletes and repairs nothing.
        #expect(trampoline.contains("appState: transcriptionPipeline?.appState"))
    }
}
