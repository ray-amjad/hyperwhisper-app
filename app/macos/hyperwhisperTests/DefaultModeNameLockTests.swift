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

@testable import hyperwhisper

@Suite("Default mode name lock")
struct DefaultModeNameLockTests {

    private func makeMode(name: String, isDefault: Bool) -> Mode {
        let persistence = PersistenceController(inMemory: true)
        let mode = Mode(context: persistence.container.viewContext)
        mode.id = UUID()
        mode.name = name
        mode.isDefault = isDefault
        return mode
    }

    @Test func locksTheDefaultWhateverItIsCalled() {
        // The restored-backup shape: flagged, but not named "Default".
        let restored = makeMode(name: "Work", isDefault: true)
        #expect(ModeEditorView.isNameLocked(restored))
    }

    @Test func doesNotLockAnOrdinaryModeNamedDefault() {
        // The trap the old string compare fell into.
        let impostor = makeMode(name: "Default", isDefault: false)
        #expect(!ModeEditorView.isNameLocked(impostor))
    }

    @Test func locksTheSeededDefault() {
        // The ordinary case both predicates agreed on, kept so a change that
        // breaks it cannot hide behind the two above.
        let seeded = makeMode(name: "Default", isDefault: true)
        #expect(ModeEditorView.isNameLocked(seeded))
    }

    @Test func locksNothingWhenThereIsNoMode() {
        // The create dialog carries no mode, and a new mode is never the default.
        #expect(!ModeEditorView.isNameLocked(nil))
    }
}
