//
//  DefaultModeInvariantTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #536.
//
//  #535 made the three mode editors agree that the default mode's Name field is
//  locked, and added a sentence promising it. Nothing below the UI held either
//  half up: `PATCH /modes` renamed the default (`ModesEndpoint.applyPatch` wrote
//  `mode.name` with no `isDefault` check), a second mode could take the flag
//  with no clear-others pass, and `deleteMode` was a bare `context.delete` that
//  left no default at all. The app then merely BEHAVED as if the first mode were
//  the default, and that acting-default had an editable name and no hint.
//
//  These exercise the write paths, not the editor — the editor is #494's test
//  file next door. The DECISION they check comes from the shared Rust core
//  (`hw-modes`), so a Swift-side answer that drifted from Windows or Linux would
//  mean the same restored backup landed on a different default per machine.
//

import CoreData
import Testing

@testable import HyperWhisper

@Suite("Default mode invariant")
struct DefaultModeInvariantTests {

    /// The controller is a PARAMETER and every caller holds it for the length of
    /// the test: a store created and dropped inside a helper deallocates on
    /// return, the `Mode` faults, and every attribute reads back as its zero
    /// value — which would make an `isDefault: true` case pass for the wrong
    /// reason. Same trap as `DefaultModeNameLockTests`.
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
    @Test func leavesAHealthySetAlone() {
        // The repair runs on every launch, so it must write nothing when there
        // is nothing to repair.
        let persistence = PersistenceController(inMemory: true)
        let first = makeMode(in: persistence, name: "Default", isDefault: true, sortOrder: 0)
        let second = makeMode(in: persistence, name: "Email", isDefault: false, sortOrder: 1)
        #expect(DefaultModePolicy.apply(to: [first, second]) == false)
        #expect(first.isDefault)
        #expect(second.isDefault == false)
    }

    @MainActor
    @Test func clearsAStraySecondDefault() {
        // What a restored backup from another machine produces. Both modes then
        // showed "This is the default mode…", which is false for both, and
        // NEITHER could be renamed — so the user could not rename a mode they
        // had made themselves.
        let persistence = PersistenceController(inMemory: true)
        let local = makeMode(in: persistence, name: "Default", isDefault: true, sortOrder: 0)
        let imported = makeMode(in: persistence, name: "Mine", isDefault: true, sortOrder: 1)
        #expect(DefaultModePolicy.apply(to: [local, imported]))
        #expect(local.isDefault)
        #expect(imported.isDefault == false)
    }

    @MainActor
    @Test func promotesTheFirstModeWhenNothingCarriesTheFlag() {
        // A backup whose mode DTOs omit the field restores as
        // `isDefault: dto.isDefault ?? false`, and `initializeDefaultModes()`
        // returns early whenever any mode exists, so nothing repaired it.
        let persistence = PersistenceController(inMemory: true)
        let late = makeMode(in: persistence, name: "Late", isDefault: false, sortOrder: 7)
        let early = makeMode(in: persistence, name: "Early", isDefault: false, sortOrder: 2)
        #expect(DefaultModePolicy.apply(to: [late, early]))
        #expect(early.isDefault)
        #expect(late.isDefault == false)
    }

    @MainActor
    @Test func movesTheFlagRatherThanAddingASecond() {
        // The `PATCH /modes/{id} {"isDefault": true}` path.
        let persistence = PersistenceController(inMemory: true)
        let old = makeMode(in: persistence, name: "Default", isDefault: true, sortOrder: 0)
        let new = makeMode(in: persistence, name: "Mine", isDefault: false, sortOrder: 1)
        #expect(DefaultModePolicy.apply(to: [old, new], preferred: new.id))
        #expect(new.isDefault)
        #expect(old.isDefault == false)
    }

    @MainActor
    @Test func reassignsTheFlagWhenTheDefaultIsDeleted() {
        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext
        let theDefault = makeMode(in: persistence, name: "Default", isDefault: true, sortOrder: 0)
        _ = makeMode(in: persistence, name: "Second", isDefault: false, sortOrder: 1)
        try? context.save()

        persistence.deleteMode(theDefault)

        let remaining = persistence.fetchAllModes()
        #expect(remaining.count == 1)
        #expect(remaining.first?.isDefault == true)
    }

    @MainActor
    @Test func fixesTheDefaultModesName() {
        let persistence = PersistenceController(inMemory: true)
        let theDefault = makeMode(in: persistence, name: "Default", isDefault: true, sortOrder: 0)
        #expect(DefaultModePolicy.canRename(theDefault, to: "Zebra") == false)
        // Re-sending its own name is not a rename — every "save the whole
        // object" client does that on every save.
        #expect(DefaultModePolicy.canRename(theDefault, to: "Default"))
    }

    @MainActor
    @Test func leavesEveryOtherModeRenameable() {
        // A lock that caught every mode would be as wrong as no lock at all.
        let persistence = PersistenceController(inMemory: true)
        let ordinary = makeMode(in: persistence, name: "Email", isDefault: false, sortOrder: 1)
        #expect(DefaultModePolicy.canRename(ordinary, to: "Zebra"))
    }

    @MainActor
    @Test func refusesToClearTheLastDefaultFlag() {
        let persistence = PersistenceController(inMemory: true)
        let theDefault = makeMode(in: persistence, name: "Default", isDefault: true, sortOrder: 0)
        let ordinary = makeMode(in: persistence, name: "Email", isDefault: false, sortOrder: 1)
        let modes = [theDefault, ordinary]

        #expect(DefaultModePolicy.canWriteDefaultFlag(modes, id: theDefault.id!, requested: false) == false)
        // Setting it is always allowed: it moves, and that clears the other.
        #expect(DefaultModePolicy.canWriteDefaultFlag(modes, id: ordinary.id!, requested: true))
        // Clearing a flag this mode does not carry takes nothing away.
        #expect(DefaultModePolicy.canWriteDefaultFlag(modes, id: ordinary.id!, requested: false))
    }

    @MainActor
    @Test func repairsAStoreThatArrivedBroken() {
        // The whole-store entry point, which runs at launch and after a backup
        // import — the two places a set nothing here wrote can appear.
        let persistence = PersistenceController(inMemory: true)
        _ = makeMode(in: persistence, name: "One", isDefault: true, sortOrder: 0)
        _ = makeMode(in: persistence, name: "Two", isDefault: true, sortOrder: 1)
        try? persistence.container.viewContext.save()

        #expect(persistence.enforceDefaultModeInvariant())
        #expect(persistence.fetchAllModes().filter(\.isDefault).count == 1)
        // And it is idempotent: a second launch must not touch a row again.
        #expect(persistence.enforceDefaultModeInvariant() == false)
    }
}
