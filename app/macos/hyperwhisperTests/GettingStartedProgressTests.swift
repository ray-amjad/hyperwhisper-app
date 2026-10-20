//
//  GettingStartedProgressTests.swift
//  hyperwhisperTests
//
//  Issue #652: every Home "Get started" row used to mark itself Completed on a
//  click, from a toggle stored in `gettingStartedCompletedSteps`. A step is now
//  done only when the app's own state says so. These tests drive the real
//  loader against a real in-memory Core Data store, and the real shortcut
//  check, for each of the four steps:
//
//  - empty state: the step is not completed;
//  - the real state present: the step is completed;
//  - the old stored toggle value has no effect.
//

import CoreData
import Foundation
import KeyboardShortcuts
import Testing
@testable import HyperWhisper

@MainActor
struct GettingStartedProgressTests {

    // MARK: - Fixtures

    private func save(
        into persistence: PersistenceController,
        _ build: (NSManagedObjectContext) -> Void
    ) throws {
        let context = persistence.container.newBackgroundContext()
        var saveError: Error?
        context.performAndWait {
            build(context)
            do { try context.save() } catch { saveError = error }
        }
        if let saveError { throw saveError }
    }

    private func insertTranscript(status: String, into persistence: PersistenceController) throws {
        try save(into: persistence) { context in
            let transcript = Transcript(context: context)
            transcript.id = UUID()
            transcript.text = "hello world"
            transcript.date = Date()
            transcript.duration = 2
            transcript.status = status
        }
    }

    private func insertMode(
        id: UUID = UUID(),
        name: String,
        sortOrder: Int16 = 1,
        isDefault: Bool = false,
        isSystemProvided: Bool = false,
        into persistence: PersistenceController
    ) throws {
        try save(into: persistence) { context in
            let mode = Mode(context: context)
            mode.id = id
            mode.name = name
            mode.createdDate = Date()
            mode.modifiedDate = Date()
            mode.sortOrder = sortOrder
            mode.isDefault = isDefault
            mode.isSystemProvided = isSystemProvided
        }
    }

    private func load(_ persistence: PersistenceController, hasCustomShortcut: Bool = false) throws -> GettingStartedProgress {
        try GettingStartedLoader.load(container: persistence.container, hasCustomShortcut: hasCustomShortcut)
    }

    /// Every recording shortcut at its default.
    private func defaults(_ name: KeyboardShortcuts.Name) -> KeyboardShortcuts.Shortcut? {
        name.defaultShortcut
    }

    // MARK: - Empty state

    @Test("a fresh install has no step completed, and the section shows")
    func freshInstallHasNothingCompleted() throws {
        let persistence = PersistenceController(inMemory: true)
        persistence.seedDefaultMode()

        let progress = try load(
            persistence,
            hasCustomShortcut: GettingStartedProgress.hasCustomShortcut(pushToTalkMode: .disabled, shortcut: defaults)
        )

        for step in GettingStartedStep.allCases {
            #expect(!progress.isCompleted(step), "\(step) is completed on a fresh install")
        }
        #expect(!progress.isAllDone)
    }

    // MARK: - Recording

    @Test("recording: only a finished transcription completes it")
    func recordingNeedsAFinishedTranscription() throws {
        let persistence = PersistenceController(inMemory: true)
        #expect(!(try load(persistence)).isCompleted(.recording))

        try insertTranscript(status: "failed", into: persistence)
        try insertTranscript(status: "processing", into: persistence)
        #expect(!(try load(persistence)).isCompleted(.recording), "a failed or unfinished recording counts")

        try insertTranscript(status: "completed", into: persistence)
        #expect(try load(persistence).isCompleted(.recording))
    }

    // MARK: - Shortcuts

    @Test("shortcuts: defaults leave it incomplete, a changed one completes it")
    func shortcutsNeedAChange() {
        #expect(!GettingStartedProgress.hasCustomShortcut(pushToTalkMode: .disabled, shortcut: defaults))

        // A recording shortcut moved off its default.
        let changed = GettingStartedProgress.hasCustomShortcut(pushToTalkMode: .disabled) { name in
            name == .toggleRecordingWithTranscription
                ? KeyboardShortcuts.Shortcut(.r, modifiers: [.command, .option])
                : name.defaultShortcut
        }
        #expect(changed)

        // A recording shortcut cleared.
        let cleared = GettingStartedProgress.hasCustomShortcut(pushToTalkMode: .disabled) { name in
            name == .cancelRecording ? nil : name.defaultShortcut
        }
        #expect(cleared)

        // Push to Talk turned on.
        #expect(GettingStartedProgress.hasCustomShortcut(pushToTalkMode: .fn, shortcut: defaults))

        // A shortcut that does not record (the mode switcher) is not this step.
        let modeSwitcherOnly = GettingStartedProgress.hasCustomShortcut(pushToTalkMode: .disabled) { name in
            name == .changeMode ? KeyboardShortcuts.Shortcut(.m, modifiers: [.command]) : name.defaultShortcut
        }
        #expect(!modeSwitcherOnly)
    }

    @Test("shortcuts: the loader reports the shortcut check it was given")
    func loaderCarriesTheShortcutCheck() throws {
        let persistence = PersistenceController(inMemory: true)
        #expect(!(try load(persistence, hasCustomShortcut: false)).isCompleted(.shortcuts))
        #expect(try load(persistence, hasCustomShortcut: true).isCompleted(.shortcuts))
    }

    // MARK: - Mode

    @Test("mode: the seeded default, a restored default and a leaked Local API row do not count")
    func modeNeedsOneTheUserMade() throws {
        let persistence = PersistenceController(inMemory: true)
        persistence.seedDefaultMode()
        #expect(!(try load(persistence)).isCompleted(.mode), "the seeded mode counts")

        // A backup restore brings the seeded mode back without its flag.
        let restoreTarget = PersistenceController(inMemory: true)
        try insertMode(id: SeededModeValues.seededID, name: "Hyper", sortOrder: 0, isDefault: true, into: restoreTarget)
        #expect(!(try load(restoreTarget)).isCompleted(.mode), "the restored seeded mode counts")

        try insertMode(
            name: LocalAPITransientModeMarker.transcribeName,
            sortOrder: LocalAPITransientModeMarker.sortOrder,
            into: persistence
        )
        #expect(!(try load(persistence)).isCompleted(.mode), "a leaked Local API row counts")

        try insertMode(name: "Email", into: persistence)
        #expect(try load(persistence).isCompleted(.mode))
    }

    // MARK: - Vocabulary

    @Test("vocabulary: one word completes it")
    func vocabularyNeedsAWord() throws {
        let persistence = PersistenceController(inMemory: true)
        #expect(!(try load(persistence)).isCompleted(.vocabulary))

        try save(into: persistence) { context in
            let row = Vocabulary(context: context)
            row.id = UUID()
            row.word = "HyperWhisper"
            row.createdDate = Date()
        }
        #expect(try load(persistence).isCompleted(.vocabulary))
    }

    // MARK: - All done

    @Test("the section hides only when all four real steps are done")
    func allDoneNeedsAllFour() {
        var progress = GettingStartedProgress(
            hasFinishedRecording: true,
            hasCustomShortcut: true,
            hasUserMode: true,
            hasVocabularyWord: false
        )
        #expect(!progress.isAllDone)
        progress.hasVocabularyWord = true
        #expect(progress.isAllDone)
    }

    // MARK: - The old toggle

    @Test("the old click-toggled gettingStartedCompletedSteps value has no effect")
    func oldStoredToggleIsIgnored() throws {
        let key = "gettingStartedCompletedSteps"
        let defaults = UserDefaults.standard
        let previous = defaults.object(forKey: key)
        defer {
            if let previous { defaults.set(previous, forKey: key) } else { defaults.removeObject(forKey: key) }
        }
        defaults.set("mode,recording,shortcuts,vocabulary", forKey: key)

        let persistence = PersistenceController(inMemory: true)
        persistence.seedDefaultMode()
        let progress = try load(
            persistence,
            hasCustomShortcut: GettingStartedProgress.hasCustomShortcut(pushToTalkMode: .disabled, shortcut: self.defaults)
        )

        for step in GettingStartedStep.allCases {
            #expect(!progress.isCompleted(step), "\(step) is completed from the old stored toggle")
        }
        #expect(!progress.isAllDone)
    }
}
