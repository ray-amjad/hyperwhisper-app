//
//  LocalAPITransientModeTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1509, and the #1446 protection it must keep.
//
//  `/transcribe` and `/post-process` built their per-request Mode with
//  `Mode(context: viewContext)` and never saved it. Any other save of the
//  `viewContext` during a request (the fuzz used a Transcribe File transcript
//  save) committed it as a real row, which showed in Select Mode and
//  Transcribe File and survived relaunch. On main, the first two tests below
//  are exactly that sequence and find the row in the store.
//
//  The Model Library refused to delete a model a request used only because it
//  saw that unsaved Mode in its `viewContext` fetch (#1446). The registry tests
//  and the wiring tests pin that the refusal survives the move.
//

import CoreData
import Foundation
import Testing

@testable import HyperWhisper

@MainActor
@Suite("Local API transient Mode never reaches the store (#1509)", .serialized)
struct LocalAPITransientModeTests {

    private static let modelLibraryPath = "app/macos/hyperwhisper/Views/ModelLibrary/ModelLibraryView.swift"
    private static let transcribePath = "app/macos/hyperwhisper/Managers/LocalAPI/Endpoints/TranscribeEndpoint.swift"
    private static let postProcessPath = "app/macos/hyperwhisper/Managers/LocalAPI/Endpoints/PostProcessEndpoint.swift"

    // MARK: - Fixtures

    /// The controller is a PARAMETER and every test holds it for its whole
    /// length: a store dropped inside a helper deallocates on return (the
    /// `DeleteActiveModeTests` trap).
    ///
    /// `createdDate` and `modifiedDate` are mandatory with no default. Without
    /// them `persistence.save()` fails validation, logs instead of throwing,
    /// and leaves every insert pending, so the store these tests read is empty.
    private func makeSavedMode(
        in persistence: PersistenceController,
        name: String,
        sortOrder: Int16,
        isDefault: Bool = false,
        isSystemProvided: Bool = false
    ) -> Mode {
        let mode = Mode(context: persistence.container.viewContext)
        mode.id = UUID()
        mode.name = name
        mode.sortOrder = sortOrder
        mode.isDefault = isDefault
        mode.isSystemProvided = isSystemProvided
        mode.createdDate = Date()
        mode.modifiedDate = Date()
        return mode
    }

    /// `persistence.save()` swallows a failed save, so check that it committed.
    private func saveFixtures(_ persistence: PersistenceController) throws {
        persistence.save()
        try #require(!persistence.container.viewContext.hasChanges, "the fixture save failed")
    }

    /// Every Mode name in the STORE, read through a fresh background context so
    /// nothing pending on the `viewContext` can stand in for a committed row.
    private func storedModeNames(in persistence: PersistenceController) throws -> [String] {
        let probe = persistence.container.newBackgroundContext()
        return try probe.performAndWait {
            let request: NSFetchRequest<Mode> = Mode.fetchRequest()
            return try probe.fetch(request).compactMap { $0.name }
        }
    }

    // MARK: - #1509: another save during a request

    @Test func anotherViewContextSaveDoesNotCommitTheTranscribeMode() throws {
        let persistence = PersistenceController(inMemory: true)
        let baseline = makeSavedMode(in: persistence, name: "S3SmallEN", sortOrder: 0)
        baseline.model = "small.en"
        baseline.language = "en"
        try saveFixtures(persistence)

        // What a `{mode_id, language}` request builds.
        let transient = TranscribeEndpoint.makeTransientMode(
            baseline: baseline,
            engine: nil,
            model: nil,
            language: "de",
            parent: persistence.container.viewContext
        )
        defer { transient.end() }

        // The fields still come off the baseline, with the override on top.
        #expect(transient.mode.name == LocalAPITransientModeMarker.transcribeName)
        #expect(transient.mode.model == "small.en")
        #expect(transient.mode.language == "de")
        #expect(transient.mode.sortOrder == Int16.max)
        #expect(transient.mode.id != nil)
        #expect(transient.mode.managedObjectContext != nil)
        #expect(transient.mode.managedObjectContext !== persistence.container.viewContext)

        // The concurrent save: the user saves something else mid-request.
        _ = makeSavedMode(in: persistence, name: "Other", sortOrder: 1)
        persistence.save()

        let stored = try storedModeNames(in: persistence)
        #expect(stored.sorted() == ["Other", "S3SmallEN"])
        #expect(!stored.contains(LocalAPITransientModeMarker.transcribeName))
        // Nor is it visible to a `viewContext` fetch (menus, GET /modes, backups).
        #expect(!persistence.fetchAllModes().contains { $0 === transient.mode })
        #expect(!persistence.container.viewContext.insertedObjects.contains(transient.mode))
        #expect(!persistence.container.viewContext.hasChanges)
    }

    @Test func anotherViewContextSaveDoesNotCommitThePostProcessMode() throws {
        let persistence = PersistenceController(inMemory: true)
        _ = makeSavedMode(in: persistence, name: "Hyper", sortOrder: 0, isDefault: true)
        try saveFixtures(persistence)

        // What a `{text, preset}` request with no mode_id builds.
        let request = PostProcessRequest(
            text: "hello",
            mode_id: nil,
            preset: "email",
            prompt: nil,
            provider: nil,
            model: nil
        )
        let working = try PostProcessEndpoint.buildWorkingMode(
            req: request,
            parent: persistence.container.viewContext
        )
        let transient = try #require(working.transient)
        defer { transient.end() }
        #expect(working.mode === transient.mode)
        #expect(working.mode.name == LocalAPITransientModeMarker.postProcessName)
        #expect(working.mode.preset == "email")
        #expect(working.mode.postProcessingMode != 0)

        _ = makeSavedMode(in: persistence, name: "Other", sortOrder: 1)
        persistence.save()

        let stored = try storedModeNames(in: persistence)
        #expect(stored.sorted() == ["Hyper", "Other"])
        #expect(!persistence.fetchAllModes().contains { $0 === working.mode })
        #expect(!persistence.container.viewContext.hasChanges)
    }

    // MARK: - #1446: the in-flight registry

    @Test func aRequestModeIsInFlightUntilItEnds() {
        let persistence = PersistenceController(inMemory: true)
        let transient = TranscribeEndpoint.makeTransientMode(
            baseline: nil,
            engine: nil,
            model: "tiny",
            language: nil,
            parent: persistence.container.viewContext
        )
        #expect(transient.mode.model == "tiny")
        #expect(LocalAPITransientMode.isInFlight(transient.mode))
        // The Model Library reads this list; it must carry the model id.
        #expect(LocalAPITransientMode.inFlightModes.contains { $0 === transient.mode && $0.model == "tiny" })

        transient.end()
        #expect(!LocalAPITransientMode.isInFlight(transient.mode))
        #expect(!LocalAPITransientMode.inFlightModes.contains { $0 === transient.mode })

        // A second end() is harmless.
        transient.end()
        #expect(!LocalAPITransientMode.isInFlight(transient.mode))
        #expect(!persistence.container.viewContext.hasChanges)
    }

    @Test func aSavedModeIsNeverInFlight() {
        let persistence = PersistenceController(inMemory: true)
        let saved = makeSavedMode(in: persistence, name: "Saved", sortOrder: 0)
        #expect(!LocalAPITransientMode.isInFlight(saved))
    }

    @Test func theModelLibraryDeleteCheckStillSeesARunningRequest() throws {
        let both = try ProductionSource.slice(
            of: Self.modelLibraryPath,
            from: "private func modesUsingVoiceModel(_ modelId: String) -> [Mode] {",
            to: "private func modesThatCanUseAModel() -> [Mode] {"
        )
        // Both delete checks, voice and local LLM, read the widened list.
        #expect(both.components(separatedBy: "modesThatCanUseAModel()").count - 1 == 2)
        #expect(!both.contains("fetchAllModes()"))

        let source = try ProductionSource.slice(
            of: Self.modelLibraryPath,
            from: "private func modesThatCanUseAModel() -> [Mode] {",
            to: "@discardableResult"
        )
        #expect(source.contains("PersistenceController.shared.fetchAllModes()"))
        #expect(source.contains("LocalAPITransientMode.inFlightModes"))
    }

    @Test func theAlertNamesNoInternalModeForARunningRequest() throws {
        let alert = try ProductionSource.slice(
            of: Self.modelLibraryPath,
            from: "private func showCannotDeleteAlertIfNeeded(",
            to: "showingModelInUseAlert = true"
        )
        #expect(alert.contains("LocalAPITransientMode.isInFlight("))
        #expect(alert.contains("\"settings.models.inUse.localAPI\".localized"))
        // Bullets come from the saved Modes only.
        #expect(alert.contains("savedModes.map"))
        #expect(!alert.contains("modes.map"))
    }

    @Test func everyLocaleHasTheLocalAPIInUseKey() throws {
        let locales = try ProductionSource.localeDirectories()
        #expect(locales.count == 40)

        for locale in locales {
            let lines = try ProductionSource.localizableLines(of: locale)
            guard let line = ProductionSource.onlyLine(
                forKey: "settings.models.inUse.localAPI",
                in: lines,
                locale: locale
            ) else { continue }
            // Shown as-is, never through String(format:).
            #expect(!line.contains("%"), "\(locale.lastPathComponent)")
        }
    }

    // MARK: - Wiring: neither endpoint builds a Mode in the viewContext

    @Test func neitherEndpointInsertsAModeIntoASharedContext() throws {
        for path in [Self.transcribePath, Self.postProcessPath] {
            let code = try ProductionSource.code(of: path)
            #expect(!code.contains("Mode(context:"), "\(path) builds a Mode in a context it does not own")
            #expect(code.contains("LocalAPITransientMode("), "\(path)")
            #expect(!code.contains("context.delete("), "\(path)")
        }
        let transcribe = try ProductionSource.code(of: Self.transcribePath)
        #expect(transcribe.contains("defer { resolution.transientMode?.end() }"))
        let postProcess = try ProductionSource.code(of: Self.postProcessPath)
        #expect(postProcess.contains("defer { working.transient?.end() }"))
    }

    /// The mixed `mode_id` + override path builds the Mode BEFORE
    /// `selectProvider`, which can throw. `handle` then never gets a
    /// resolution to end, so the resolver must end it itself.
    @Test func theMixedPathEndsItsModeWhenProviderSelectionThrows() throws {
        let mixed = try ProductionSource.slice(
            of: Self.transcribePath,
            from: "let transient = makeTransientMode(baseline: stored,",
            to: "return ProviderResolution("
        )
        #expect(mixed.contains("try await router.selectProvider(for: transient.mode"))
        // #1919: the catch rethrows through `mixedPathSelectionError`, which
        // only renames a Nemotron not-installed error. The Mode must still be
        // ended before anything is thrown.
        let end = try #require(mixed.range(of: "transient.end()"))
        let rethrow = try #require(mixed.range(of: "throw mixedPathSelectionError("))
        #expect(end.lowerBound < rethrow.lowerBound)
    }

    // MARK: - Leaked rows from earlier builds

    @Test func theLeakedRowMatcherNeedsEveryMarker() {
        let t = LocalAPITransientModeMarker.transcribeName
        let p = LocalAPITransientModeMarker.postProcessName
        func leaked(_ name: String?, _ sortOrder: Int16 = Int16.max, isDefault: Bool = false, seeded: Bool = false) -> Bool {
            LocalAPITransientModeMarker.isLeakedRow(
                name: name,
                sortOrder: sortOrder,
                isDefault: isDefault,
                isSystemProvided: seeded
            )
        }

        #expect(leaked(t))
        #expect(leaked(p))
        // The suffix `repairModeNames()` gave the issue's 12 copies.
        #expect(leaked("\(t) 2"))
        #expect(leaked("\(t) 12"))
        #expect(leaked("\(p) 3"))

        #expect(!leaked(t, 5), "a user Mode with this name keeps an ordinary sort order")
        #expect(!leaked(t, isDefault: true))
        #expect(!leaked(t, seeded: true))
        #expect(!leaked(nil))
        #expect(!leaked("Meeting"))
        #expect(!leaked("\(t) "))
        #expect(!leaked("\(t) 2b"))
        #expect(!leaked("\(t)2"))
        #expect(!leaked("My \(t)"))
        #expect(!leaked(t.uppercased()))
    }

    @Test func theLaunchPurgeDeletesOnlyLeakedRows() throws {
        let persistence = PersistenceController(inMemory: true)
        let top = Int16.max
        let t = LocalAPITransientModeMarker.transcribeName
        let p = LocalAPITransientModeMarker.postProcessName

        // Leaked: what earlier builds left on disk.
        _ = makeSavedMode(in: persistence, name: t, sortOrder: top)
        _ = makeSavedMode(in: persistence, name: "\(t) 12", sortOrder: top)
        _ = makeSavedMode(in: persistence, name: p, sortOrder: top)
        // Kept: real Modes, including near misses.
        _ = makeSavedMode(in: persistence, name: "Hyper", sortOrder: 0, isDefault: true, isSystemProvided: true)
        _ = makeSavedMode(in: persistence, name: t, sortOrder: 3)
        // createOrUpdateMode gives a new Mode Int16.max once a leaked row holds it.
        _ = makeSavedMode(in: persistence, name: "Created After The Leak", sortOrder: top)
        _ = makeSavedMode(in: persistence, name: "\(t) 2b", sortOrder: top)
        try saveFixtures(persistence)

        #expect(persistence.purgeLeakedLocalAPITransientModes() == 3)

        let stored = try storedModeNames(in: persistence)
        #expect(stored.sorted() == ["Created After The Leak", "Hyper", t, "\(t) 2b"].sorted())
        #expect(!persistence.container.viewContext.hasChanges)

        // Once clean, it deletes nothing.
        #expect(persistence.purgeLeakedLocalAPITransientModes() == 0)
    }

    @Test func theLaunchPurgeLeavesARunningRequestAlone() throws {
        let persistence = PersistenceController(inMemory: true)
        _ = makeSavedMode(in: persistence, name: LocalAPITransientModeMarker.transcribeName, sortOrder: Int16.max)
        try saveFixtures(persistence)

        let transient = TranscribeEndpoint.makeTransientMode(
            baseline: nil,
            engine: nil,
            model: "tiny",
            language: nil,
            parent: persistence.container.viewContext
        )
        defer { transient.end() }

        #expect(persistence.purgeLeakedLocalAPITransientModes() == 1)
        #expect(!transient.mode.isDeleted)
        #expect(transient.mode.model == "tiny")
        #expect(LocalAPITransientMode.isInFlight(transient.mode))
    }

    // MARK: - #1546: two paths that kept a leaked row

    private struct StoredMode: Equatable {
        let name: String
        let sortOrder: Int16
        let isDefault: Bool
    }

    /// Every Mode in the STORE, sorted by name, read like `storedModeNames`.
    private func storedModes(in persistence: PersistenceController) throws -> [StoredMode] {
        let probe = persistence.container.newBackgroundContext()
        return try probe.performAndWait {
            let request: NSFetchRequest<Mode> = Mode.fetchRequest()
            return try probe.fetch(request)
                .map { StoredMode(name: $0.name ?? "", sortOrder: $0.sortOrder, isDefault: $0.isDefault) }
                .sorted { $0.name < $1.name }
        }
    }

    /// Any row carrying an endpoint name, whatever its other fields.
    private func internalNamedRows(in persistence: PersistenceController) throws -> [StoredMode] {
        try storedModes(in: persistence).filter { $0.name.hasPrefix("__local_api_") }
    }

    /// What a pre-#1509 build exports: the real `BackupMode(from:)` projection
    /// of every stored Mode, through the v1 Codable round trip.
    private func exportedModes(of persistence: PersistenceController) throws -> [BackupMode] {
        let data = try JSONEncoder().encode(persistence.fetchAllModes().map { BackupMode(from: $0) })
        return try JSONDecoder().decode([BackupMode].self, from: data)
    }

    /// Path 1. The user deleted every real Mode, so leaked rows are all that
    /// is left. The default-mode repair used to run first and make one of them
    /// the default, and the purge then kept it for good.
    @Test func aLaunchWithOnlyLeakedRowsEndsWithTheSeededDefaultMode() throws {
        let persistence = PersistenceController(inMemory: true)
        let t = LocalAPITransientModeMarker.transcribeName
        let p = LocalAPITransientModeMarker.postProcessName
        _ = makeSavedMode(in: persistence, name: t, sortOrder: Int16.max)
        _ = makeSavedMode(in: persistence, name: "\(t) 2", sortOrder: Int16.max)
        _ = makeSavedMode(in: persistence, name: p, sortOrder: Int16.max)
        try saveFixtures(persistence)

        persistence.prepareModesOnLaunch()

        let stored = try storedModes(in: persistence)
        #expect(try internalNamedRows(in: persistence).isEmpty, "\(stored)")
        // The store was empty after the purge, so the launch seeded the one
        // real default mode, exactly as on a fresh install.
        #expect(stored.count == 1, "\(stored)")
        #expect(stored.first?.isDefault == true)
        #expect(persistence.didSeedDefaultModesOnLaunch)
        #expect(!persistence.container.viewContext.hasChanges)

        // The next launch changes nothing.
        persistence.prepareModesOnLaunch()
        #expect(try storedModes(in: persistence) == stored)
    }

    /// A default Mode with an endpoint name and sort order 32767 stays. No
    /// field tells it apart from one a user made through the Local API
    /// (`POST /modes` + `PATCH` set all three), so the purge keeps #1540's
    /// rule and never deletes the default. The non-default leaked row beside
    /// it goes, and a user Mode with the same name and an ordinary sort order
    /// stays.
    @Test func aLaunchKeepsADefaultModeWithAnEndpointName() throws {
        let persistence = PersistenceController(inMemory: true)
        let t = LocalAPITransientModeMarker.transcribeName
        _ = makeSavedMode(in: persistence, name: t, sortOrder: Int16.max, isDefault: true)
        _ = makeSavedMode(in: persistence, name: "\(t) 2", sortOrder: Int16.max)
        _ = makeSavedMode(in: persistence, name: "Meeting", sortOrder: 0)
        _ = makeSavedMode(in: persistence, name: "\(t) 3", sortOrder: 3)
        try saveFixtures(persistence)

        persistence.prepareModesOnLaunch()

        #expect(try storedModes(in: persistence) == [
            StoredMode(name: "Meeting", sortOrder: 0, isDefault: false),
            StoredMode(name: t, sortOrder: Int16.max, isDefault: true),
            StoredMode(name: "\(t) 3", sortOrder: 3, isDefault: false),
        ])
        // Not a fresh install: nothing was seeded.
        #expect(!persistence.didSeedDefaultModesOnLaunch)
        #expect(!persistence.container.viewContext.hasChanges)
    }

    /// Path 2. A backup exported on a pre-#1509 build carries leaked rows.
    /// The restore used to recreate each one with `maxSortOrder + 1`, so the
    /// launch purge never matched it again. A user Mode named like the
    /// internal one, with an ordinary sort order, is still restored.
    @Test func aRestoredPreFixBackupLeavesNoLeakedRowAfterALaunch() throws {
        let t = LocalAPITransientModeMarker.transcribeName
        let p = LocalAPITransientModeMarker.postProcessName
        let source = PersistenceController(inMemory: true)
        _ = makeSavedMode(in: source, name: "Meeting", sortOrder: 0, isDefault: true)
        _ = makeSavedMode(in: source, name: t, sortOrder: 4)
        _ = makeSavedMode(in: source, name: "\(t) 2", sortOrder: Int16.max)
        _ = makeSavedMode(in: source, name: p, sortOrder: Int16.max)
        try saveFixtures(source)
        let backup = try exportedModes(of: source)
        try #require(backup.filter { $0.sortOrder == Int16.max }.count == 2)

        let persistence = PersistenceController(inMemory: true)
        let result = try persistence.importBackupStore(
            modes: backup,
            modeResolution: .skip,
            vocabulary: nil,
            vocabularyResolution: .skip
        )
        persistence.prepareModesOnLaunch()

        let stored = try storedModes(in: persistence)
        #expect(stored.map(\.name) == ["Meeting", t], "\(stored)")
        #expect(stored.first { $0.name == "Meeting" }?.isDefault == true)
        // The two left-out rows are reported as skipped.
        #expect(result.modesImported == 2)
        #expect(result.modesSkipped == 2)
        #expect(!persistence.container.viewContext.hasChanges)
    }

    /// The restore drops no more than the launch purge would: a DEFAULT backup
    /// row with an endpoint name and sort order 32767 is restored, because the
    /// purge would have kept it on the machine that made the backup.
    @Test func aRestoreKeepsADefaultBackupModeWithAnEndpointName() throws {
        let t = LocalAPITransientModeMarker.transcribeName
        let source = PersistenceController(inMemory: true)
        _ = makeSavedMode(in: source, name: "Meeting", sortOrder: 0)
        _ = makeSavedMode(in: source, name: t, sortOrder: Int16.max, isDefault: true)
        try saveFixtures(source)
        let backup = try exportedModes(of: source)
        try #require(backup.contains { $0.name == t && $0.isDefault && $0.sortOrder == Int16.max })

        let persistence = PersistenceController(inMemory: true)
        let result = try persistence.importBackupStore(
            modes: backup,
            modeResolution: .skip,
            vocabulary: nil,
            vocabularyResolution: .skip
        )
        persistence.prepareModesOnLaunch()

        let stored = try storedModes(in: persistence)
        #expect(stored.map(\.name) == ["Meeting", t], "\(stored)")
        #expect(stored.first { $0.name == t }?.isDefault == true)
        #expect(result.modesImported == 2)
        #expect(result.modesSkipped == 0)
    }

    /// The restore tests the RAW backup name, as the launch purge would read
    /// it on the machine that made the backup. A name with a trailing space
    /// does not match there, so the row is restored (trimmed on the way in,
    /// as every restored name is).
    @Test func aRestoreTestsTheRawBackupName() throws {
        let t = LocalAPITransientModeMarker.transcribeName
        let source = PersistenceController(inMemory: true)
        _ = makeSavedMode(in: source, name: "Meeting", sortOrder: 0, isDefault: true)
        _ = makeSavedMode(in: source, name: "\(t) ", sortOrder: Int16.max)
        try saveFixtures(source)
        let backup = try exportedModes(of: source)
        try #require(backup.contains { $0.name == "\(t) " && $0.sortOrder == Int16.max })

        let persistence = PersistenceController(inMemory: true)
        let result = try persistence.importBackupStore(
            modes: backup,
            modeResolution: .skip,
            vocabulary: nil,
            vocabularyResolution: .skip
        )
        persistence.prepareModesOnLaunch()

        let stored = try storedModes(in: persistence)
        #expect(stored.map(\.name) == ["Meeting", t], "\(stored)")
        #expect(result.modesImported == 2)
        #expect(result.modesSkipped == 0)
    }

    /// A restored row the check keeps must not be stored as one the launch
    /// purge deletes. In a store that already holds `Int16.max` (here a user
    /// mode created after a leak) `createOrUpdateMode` caps the new row's sort
    /// order there and trims its name, so both rows below would match.
    @Test func aRowRestoredIntoAStoreAtTheMarkerSortOrderSurvivesALaunch() throws {
        let t = LocalAPITransientModeMarker.transcribeName
        let p = LocalAPITransientModeMarker.postProcessName
        let source = PersistenceController(inMemory: true)
        _ = makeSavedMode(in: source, name: "\(t) ", sortOrder: Int16.max)
        _ = makeSavedMode(in: source, name: p, sortOrder: 7)
        try saveFixtures(source)
        let backup = try exportedModes(of: source)

        let persistence = PersistenceController(inMemory: true)
        _ = makeSavedMode(in: persistence, name: "Meeting", sortOrder: 0, isDefault: true)
        _ = makeSavedMode(in: persistence, name: "Created After The Leak", sortOrder: Int16.max)
        try saveFixtures(persistence)

        let result = try persistence.importBackupStore(
            modes: backup,
            modeResolution: .skip,
            vocabulary: nil,
            vocabularyResolution: .skip
        )
        persistence.prepareModesOnLaunch()

        #expect(result.modesImported == 2)
        #expect(try storedModes(in: persistence) == [
            StoredMode(name: "Created After The Leak", sortOrder: Int16.max, isDefault: false),
            StoredMode(name: "Meeting", sortOrder: 0, isDefault: true),
            StoredMode(name: p, sortOrder: Int16.max - 1, isDefault: false),
            StoredMode(name: t, sortOrder: Int16.max - 1, isDefault: false),
        ].sorted { $0.name < $1.name })
    }

    /// Path 2 under "Replace": a leaked backup row used to delete the local
    /// Mode with the same name before it was recreated in its place.
    @Test func aReplaceRestoreOfALeakedRowKeepsTheLocalModeWithThatName() throws {
        let t = LocalAPITransientModeMarker.transcribeName
        let source = PersistenceController(inMemory: true)
        _ = makeSavedMode(in: source, name: "Meeting", sortOrder: 0, isDefault: true)
        _ = makeSavedMode(in: source, name: t, sortOrder: Int16.max)
        try saveFixtures(source)
        let backup = try exportedModes(of: source)

        let persistence = PersistenceController(inMemory: true)
        let local = makeSavedMode(in: persistence, name: t, sortOrder: 3, isDefault: true)
        local.model = "small.en"
        let localId = try #require(local.id)
        try saveFixtures(persistence)

        _ = try persistence.importBackupStore(
            modes: backup,
            modeResolution: .replace,
            vocabulary: nil,
            vocabularyResolution: .skip
        )
        persistence.prepareModesOnLaunch()

        let stored = try storedModes(in: persistence)
        #expect(stored.map(\.name) == ["Meeting", t], "\(stored)")
        let kept = try #require(persistence.fetchAllModes().first { $0.name == t })
        #expect(kept.id == localId)
        #expect(kept.sortOrder == 3)
        #expect(kept.model == "small.en")
    }

    /// `init` runs the launch steps only for an on-disk store, so the tests
    /// above drive `prepareModesOnLaunch()` by hand. Pin that `init` runs it
    /// first, and that it purges before it seeds.
    @Test func launchRunsThePurgeBeforeTheDefaultModeSeed() throws {
        let path = "app/macos/hyperwhisper/CoreData/PersistenceController.swift"
        let launch = try ProductionSource.slice(
            of: path,
            from: "if !inMemory {",
            to: "normalizeCloudRouteIdentifiersIfNeeded()"
        )
        #expect(launch.trimmingCharacters(in: .whitespacesAndNewlines) == "prepareModesOnLaunch()")
        let afterInit = try ProductionSource.slice(
            of: path,
            from: "normalizeCloudRouteIdentifiersIfNeeded()",
            to: "func prepareModesOnLaunch() {"
        )
        #expect(!afterInit.contains("initializeDefaultModes()"))
        #expect(!afterInit.contains("purgeLeakedLocalAPITransientModesOnLaunch()"))

        let prepare = try ProductionSource.slice(
            of: path,
            from: "func prepareModesOnLaunch() {",
            to: "private func purgeLeakedLocalAPITransientModesOnLaunch() {"
        )
        let purge = try #require(prepare.range(of: "purgeLeakedLocalAPITransientModesOnLaunch()"))
        let seed = try #require(prepare.range(of: "initializeDefaultModes()"))
        #expect(purge.lowerBound < seed.lowerBound)
    }
}
