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
        return mode
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
        persistence.save()

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
        persistence.save()

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
        let localizations = ProductionSource.url("app/macos/hyperwhisper/Localizations")
        let locales = try FileManager.default.contentsOfDirectory(
            at: localizations,
            includingPropertiesForKeys: nil
        ).filter { $0.pathExtension == "lproj" }
        #expect(locales.count == 40)

        for locale in locales {
            let lines = try ProductionSource.text(of: locale.appendingPathComponent("Localizable.strings"))
                .components(separatedBy: .newlines)
            let matches = lines.filter { $0.hasPrefix("\"settings.models.inUse.localAPI\" = \"") }
            #expect(matches.count == 1, "\(locale.lastPathComponent) needs exactly one settings.models.inUse.localAPI")
            guard let line = matches.first else { continue }
            #expect(line.trimmingCharacters(in: .whitespaces).hasSuffix("\";"), "\(locale.lastPathComponent)")
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
        #expect(mixed.contains("transient.end()"))
        #expect(mixed.contains("throw error"))
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
        persistence.save()

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
        persistence.save()

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
}
