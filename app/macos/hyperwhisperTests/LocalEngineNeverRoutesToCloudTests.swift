//
//  LocalEngineNeverRoutesToCloudTests.swift
//  hyperwhisperTests
//
//  Issue #1466: `POST /transcribe` with `mode_id` + `engine: "whisperLocal"`
//  + `model: ""` (or `"  "`) was routed to HyperWhisper Cloud. The mixed
//  mode_id+engine path builds a transient Mode and resolves it through
//  `TranscriptionProviderRouter.selectProvider(for:)` alone — it never calls
//  `resolveProvider`, which is the only place that refuses a blank Whisper
//  model. `applyEngineModel`'s whisper arm did `model ?? "base"`, so a blank
//  string survived, and `selectProvider` treats an empty model (and the
//  literal "cloud") as a Cloud mode. On-device audio would have been uploaded
//  and billed for any user with an account key.
//
//  The property pinned here is about the Mode, not about which error is
//  raised: after `applyEngineModel` with an engine that names a LOCAL engine,
//  `mode.model` must never be a value `selectProvider` sends to its Cloud
//  branch — whatever the request's `model` and whatever the baseline Mode
//  held. `selectProvider` itself is not called: it is `@MainActor`, wants live
//  providers, and no test in this target stands up that fixture (see
//  `NemotronLocalAPIEngineTests`). `modelRoutesToCloud` is the mirror of its
//  first three lines, and is itself pinned below against those literals.
//

import Foundation
import Testing
@testable import HyperWhisper

@MainActor
struct LocalEngineNeverRoutesToCloudTests {

    /// Every value a caller can send that `selectProvider` reads as Cloud once
    /// it lands on `mode.model`: empty, whitespace only, and "cloud" in any
    /// case or padding.
    private static let cloudRoutingModels = ["", " ", "  ", "\t\n", "cloud", "CLOUD", " Cloud "]

    /// What `mode.model` can already hold when the arm runs. On the mixed path
    /// `makeTransientMode` copies the saved Mode's model first, so a Cloud
    /// mode's "cloud" (or a legacy mode's "") is a real baseline.
    private static let baselines: [String?] = [nil, "base", "cloud", "", "large-v3-turbo"]

    /// The same normalisation `selectProvider` applies before it decides
    /// local vs cloud. Kept literal here so a drift in either is visible.
    private static func selectProviderWouldUseCloud(_ model: String?) -> Bool {
        let raw = (model ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        let modelString = raw.isEmpty ? "cloud" : raw
        return modelString.lowercased() == "cloud"
    }

    // MARK: - The issue's repro

    /// Steps 2 and 3 of the issue: `whisperLocal` and `whisper` with `""` and
    /// `"  "`. A blank model is treated exactly like a missing one, which the
    /// arm already defaults to "base".
    @Test func aBlankWhisperModelFallsBackToBaseLikeAMissingOne() {
        let persistence = PersistenceController(inMemory: true)

        for engine in ["whisperLocal", "whisper", "libwhisper", "WhisperLocal", " whisper "] {
            for blank in ["", "  ", "\t"] {
                for baseline in Self.baselines {
                    let mode = Mode(context: persistence.container.viewContext)
                    if let baseline { mode.model = baseline }
                    TranscribeEndpoint.applyEngineModel(to: mode, engine: engine, model: blank)
                    #expect(
                        mode.model == "base",
                        """
                        engine '\(engine)' with model '\(blank)' (baseline '\(baseline ?? "nil")') \
                        left '\(mode.model ?? "nil")' on the Mode; selectProvider maps a blank \
                        model to HyperWhisper Cloud.
                        """
                    )
                    #expect(TranscribeEndpoint.engineLabel(forMode: mode) == "whisperLocal")
                }
            }
        }

        // Same answer as omitting `model` entirely — the behaviour the fix
        // matches.
        let missing = Mode(context: persistence.container.viewContext)
        TranscribeEndpoint.applyEngineModel(to: missing, engine: "whisperLocal", model: nil)
        #expect(missing.model == "base")
    }

    /// The wider hole the issue names: `model: "cloud"` with a local engine
    /// wrote the literal onto the Mode for whisper and for parakeet (whose
    /// `modelIdForSelection` passes an unknown id through unchanged).
    @Test func aCloudModelOnALocalEngineFallsBackToThatEnginesDefault() {
        let persistence = PersistenceController(inMemory: true)

        for cloud in ["cloud", "CLOUD", " Cloud "] {
            let whisper = Mode(context: persistence.container.viewContext)
            whisper.model = "cloud"
            TranscribeEndpoint.applyEngineModel(to: whisper, engine: "whisperLocal", model: cloud)
            #expect(whisper.model == "base")

            let parakeet = Mode(context: persistence.container.viewContext)
            parakeet.model = "cloud"
            TranscribeEndpoint.applyEngineModel(to: parakeet, engine: "parakeet", model: cloud)
            #expect(parakeet.model == ParakeetModelManager.Constants.v3ModelId)
            #expect(TranscribeEndpoint.engineLabel(forMode: parakeet) == "parakeet")
        }

        // Parakeet's blank model already defaulted, through
        // `modelIdForSelection`; pin it so the guard cannot regress it.
        let blank = Mode(context: persistence.container.viewContext)
        TranscribeEndpoint.applyEngineModel(to: blank, engine: "parakeet", model: "  ")
        #expect(blank.model == ParakeetModelManager.Constants.v3ModelId)
    }

    // MARK: - The general property

    /// No LOCAL engine, by any accepted spelling, may leave a Cloud-routing
    /// model on the Mode. Driven off the shared engine table so an engine
    /// added there is covered here without anyone widening a list.
    @Test func noLocalEngineEverLeavesACloudModelOnTheMode() {
        let persistence = PersistenceController(inMemory: true)

        var engines = localApiAllEngineIds().map { localApiEngineWireLabel(id: $0) }
        engines += ["whisper", "libwhisper", "nemotron-asr", "qwen3", "qwen", "APPLESPEECH"]
        #expect(engines.count > 5, "the shared engine table answered no engines")

        for engine in engines {
            #expect(
                localApiResolveEngineAlias(alias: engine) != nil,
                "'\(engine)' is not a local engine spelling — this case tests nothing"
            )
            let requests: [String?] = Self.cloudRoutingModels.map { $0 } + [nil]
            for requested in requests {
                for baseline in Self.baselines {
                    let mode = Mode(context: persistence.container.viewContext)
                    if let baseline { mode.model = baseline }
                    mode.language = "en"
                    TranscribeEndpoint.applyEngineModel(to: mode, engine: engine, model: requested)
                    #expect(
                        !Self.selectProviderWouldUseCloud(mode.model),
                        """
                        engine '\(engine)' with model '\(requested ?? "nil")' (baseline \
                        '\(baseline ?? "nil")') left '\(mode.model ?? "nil")' on the Mode, \
                        which selectProvider routes to a Cloud provider.
                        """
                    )
                }
            }
        }
    }

    // MARK: - What must not change

    /// A real explicit model is still honoured: the guard only drops a value
    /// that would leave the Mac.
    @Test func aRealExplicitModelIsKept() {
        let persistence = PersistenceController(inMemory: true)

        for id in ["base", "small.en", "large-v3-turbo"] {
            let mode = Mode(context: persistence.container.viewContext)
            mode.model = "cloud"
            TranscribeEndpoint.applyEngineModel(to: mode, engine: "whisperLocal", model: id)
            #expect(mode.model == id)
        }

        let v2 = Mode(context: persistence.container.viewContext)
        TranscribeEndpoint.applyEngineModel(
            to: v2,
            engine: "parakeet",
            model: ParakeetModelManager.Constants.v2ModelId
        )
        #expect(v2.model == ParakeetModelManager.Constants.v2ModelId)

        // Parakeet's documented pass-through of an unknown id is untouched, so
        // `resolveProvider` can still name the caller's own spelling in its
        // error on the engine-only path.
        let typo = Mode(context: persistence.container.viewContext)
        TranscribeEndpoint.applyEngineModel(to: typo, engine: "parakeet", model: "typo")
        #expect(typo.model == "typo")
    }

    /// A CLOUD engine is unaffected: `engine=cloud` with a blank model still
    /// selects Cloud. The guard is scoped to the local arms.
    @Test func aCloudEngineStillSelectsCloud() {
        let persistence = PersistenceController(inMemory: true)
        let mode = Mode(context: persistence.container.viewContext)
        mode.model = "base"
        TranscribeEndpoint.applyEngineModel(to: mode, engine: "cloud", model: "")
        #expect(mode.model == "cloud")
        #expect(mode.cloudProvider == CloudProvider.hyperwhisper.rawValue)
    }

    /// `modelRoutesToCloud` must agree with `selectProvider`'s own test on
    /// every non-nil value; nil is "no override", not a Cloud model.
    @Test func modelRoutesToCloudMirrorsSelectProvider() {
        let samples = Self.cloudRoutingModels + ["base", "cloudy", "parakeet-tdt-0.6b-v3", " base "]
        for sample in samples {
            #expect(
                TranscribeEndpoint.modelRoutesToCloud(sample) == Self.selectProviderWouldUseCloud(sample),
                "'\(sample)' is judged differently by the guard and by selectProvider"
            )
        }
        #expect(TranscribeEndpoint.modelRoutesToCloud(nil) == false)
    }
}
