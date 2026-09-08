//
//  DefaultModeNameLockTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #494.
//
//  The mode editor disables the Name field for the one mode whose name is fixed.
//  macOS used to decide that with `configuration.mode?.name == "Default"` — the
//  mode's LABEL — while Windows had always read `IsDefault`, the mode's IDENTITY.
//  The two heads therefore disagreed about which mode was locked, and the string
//  compare was wrong in both directions on its own platform:
//
//    * a mode the user named "Default" themselves was locked, though it is an
//      ordinary mode;
//    * the real default stopped being locked the moment it carried another name,
//      which a restored backup can produce — `PersistenceController` re-applies
//      `isDefault` to a restored mode without constraining what it is called.
//
//  Both cases below are unreachable through the old predicate, so this file goes
//  red if the string compare ever comes back.
//

import CoreData
import Testing

@testable import HyperWhisper

@Suite("Default mode name lock")
struct DefaultModeNameLockTests {

    // The controller is a PARAMETER, and every caller holds it for the length of
    // the test. A store created and dropped inside this helper deallocates on
    // return, the Mode faults, and every attribute reads back as its zero value -
    // which made both isDefault:true cases fail while both false cases "passed".
    @MainActor
    private func makeMode(
        in persistence: PersistenceController,
        name: String,
        isDefault: Bool
    ) -> Mode {
        let mode = Mode(context: persistence.container.viewContext)
        mode.id = UUID()
        mode.name = name
        mode.isDefault = isDefault
        return mode
    }

    @MainActor
    @Test func locksTheDefaultWhateverItIsCalled() {
        // The restored-backup shape: flagged, but not named "Default".
        let persistence = PersistenceController(inMemory: true)
        let restored = makeMode(in: persistence, name: "Work", isDefault: true)
        #expect(ModeEditorView.isNameLocked(restored))
    }

    @MainActor
    @Test func doesNotLockAnOrdinaryModeNamedDefault() {
        // The trap the old string compare fell into.
        let persistence = PersistenceController(inMemory: true)
        let impostor = makeMode(in: persistence, name: "Default", isDefault: false)
        #expect(!ModeEditorView.isNameLocked(impostor))
    }

    @MainActor
    @Test func locksTheSeededDefault() {
        // The ordinary case both predicates agreed on, kept so a change that
        // breaks it cannot hide behind the two above.
        let persistence = PersistenceController(inMemory: true)
        let seeded = makeMode(in: persistence, name: "Default", isDefault: true)
        #expect(ModeEditorView.isNameLocked(seeded))
    }

    @MainActor
    @Test func locksNothingWhenThereIsNoMode() {
        // The create dialog carries no mode, and a new mode is never the default.
        #expect(!ModeEditorView.isNameLocked(nil))
    }
}
