//
//  NemotronLocalAPIEngineTests.swift
//  hyperwhisperTests
//
//  Issue #376: Nemotron 3.5 was implemented end to end — a real
//  `NemotronProvider` is injected into `TranscriptionProviderRouter` and
//  `selectLocalProvider` already routes `nemotron-asr-3.5-*` to it — but the
//  Local API could not name it. `engine=nemotron` fell through the
//  `default:` arm of both hand-maintained engine switches, and a Mode whose
//  `model` was already a Nemotron id fell through `engineLabel`'s final
//  `return "whisperLocal"`.
//
//  So there are five properties worth pinning here, and they are different:
//
//  1. The engine ALIASES have to keep landing a canonical Nemotron id on the
//     Mode. `resolveProvider` and `applyEngineModel` were two hand-copied
//     switches over the same spellings; they now match the one
//     `NemotronModelManager.Constants.engineAliases` set, so they cannot
//     drift, and `theSharedEngineAliasSetIsPinned` guards the set itself.
//  2. `engineLabel(forMode:)` has to keep saying "nemotron", including on the
//     `mode_id` path that never goes near an `engine=` string. That path is
//     the issue's first comment, where a saved Nemotron mode reported
//     `"engine": "whisperLocal"`.
//  3. The mixed `mode_id` + `engine` form must not swap the variant the
//     caller saved — review round 1's regression, where re-asserting
//     `engine=nemotron` on a Latin mode silently selected multilingual.
//  4. …but it must not KEEP that variant for a language the variant cannot
//     transcribe — review round 2, where the round-1 inherit rule sent
//     `language=ja` to the Latin model's pruned vocabulary and returned 200.
//  5. `engine=nemotron` must never leave a non-Nemotron id on the Mode —
//     review round 2, where an explicit `model=base` routed the request to
//     LibWhisper and answered `"engine": "whisperLocal"`.
//
//  Properties 4 and 5 both matter because they are only reachable on the mixed
//  `mode_id` + `engine` path, which resolves through
//  `selectProvider(for: transient)` and never calls `resolveProvider` — so the
//  router's own guards do not run.
//
//  `resolveProvider` and `HealthEndpoint.handle` are NOT tested here: both are
//  `@MainActor` and want live providers / `Bundle.main`, and no test in this
//  target stands up that fixture. The router arm's model handling is covered
//  by inspection against the `parakeet` arm it copies; its alias handling is
//  covered structurally, by reading the same constant this file pins.
//

import Foundation
import Testing
@testable import HyperWhisper

@MainActor
struct NemotronLocalAPIEngineTests {

    // MARK: - Constants.canonicalModelId(for:)

    @Test func canonicalIdentifiersRoundTripBothVariants() {
        #expect(
            NemotronModelManager.Constants.canonicalModelId(
                for: NemotronModelManager.Constants.latinModelId
            ) == NemotronModelManager.Constants.latinModelId
        )
        #expect(
            NemotronModelManager.Constants.canonicalModelId(
                for: NemotronModelManager.Constants.multilingualModelId
            ) == NemotronModelManager.Constants.multilingualModelId
        )
        // The literals, so a rename of either constant cannot quietly rewrite
        // the wire values `Mode.model` already holds on disk.
        #expect(NemotronModelManager.Constants.latinModelId == "nemotron-asr-3.5-latin")
        #expect(NemotronModelManager.Constants.multilingualModelId == "nemotron-asr-3.5-multilingual")
    }

    @Test func canonicalIdentifiersToleratePersistedCaseAndWhitespace() {
        #expect(
            NemotronModelManager.Constants.canonicalModelId(for: "  NEMOTRON-ASR-3.5-LATIN  ")
                == NemotronModelManager.Constants.latinModelId
        )
        #expect(
            NemotronModelManager.Constants.canonicalModelId(for: " Nemotron-ASR-3.5-Multilingual ")
                == NemotronModelManager.Constants.multilingualModelId
        )
    }

    /// The anti-coercion property Parakeet's equivalent documents: an unknown
    /// id must NOT resolve to a real variant, or a typo becomes a silent run
    /// against the wrong model.
    @Test func unknownAndTypoIdentifiersAreNotCoercedToARealVariant() {
        let unknown = [
            "nemotron-3.5-latin",     // the spelling in the issue's own curl; exists nowhere
            "nemotron-3.5-ml-560ms",  // the Windows/Linux/.NET id for a different model
            "nemotron-asr-3.5-",      // the bare prefix the router matches on
            "nemotron",               // the engine alias, not a model id
            "typo",
            "",
        ]
        for id in unknown {
            #expect(
                NemotronModelManager.Constants.canonicalModelId(for: id) == nil,
                "'\(id)' must not resolve to an installable Nemotron variant"
            )
        }
    }

    /// Review round 1. `canonicalModelId(for:)` used to re-implement the
    /// id → variant switch that `NemotronModelManager.variant(forModelId:)`
    /// already owned, with looser normalisation, so a third variant added to
    /// one and not the other would let the Local API accept an id the
    /// provider then rejects. They share one list now; this pins that they
    /// still agree, in both directions, over every declared variant.
    @Test func canonicalModelIdAgreesWithTheVariantLookup() {
        for variant in NemotronModelManager.Variant.allCases {
            let id: String
            switch variant {
            case .latin: id = NemotronModelManager.Constants.latinModelId
            case .multilingual: id = NemotronModelManager.Constants.multilingualModelId
            }

            #expect(NemotronModelManager.variant(forModelId: id) == variant)
            #expect(NemotronModelManager.Constants.canonicalModelId(for: id) == id)

            // What canonicalModelId hands the router must be an id the
            // provider path's own lookup resolves to the same variant.
            let canonical = NemotronModelManager.Constants.canonicalModelId(for: "  \(id.uppercased())  ")
            #expect(canonical == id)
            #expect(NemotronModelManager.variant(forModelId: canonical ?? "") == variant)
        }

        // Neither side may claim an id the other rejects.
        for id in ["nemotron-3.5-latin", "nemotron-asr-3.5-", "parakeet-tdt-0.6b-v3", "base", ""] {
            #expect(NemotronModelManager.variant(forModelId: id) == nil)
            #expect(NemotronModelManager.Constants.canonicalModelId(for: id) == nil)
        }
    }

    // MARK: - Constants.modelIdForSelection(_:)

    @Test func missingAndBlankSelectionModelIdsDefaultToMultilingual() {
        #expect(
            NemotronModelManager.Constants.modelIdForSelection(nil)
                == NemotronModelManager.Constants.multilingualModelId
        )
        #expect(
            NemotronModelManager.Constants.modelIdForSelection("")
                == NemotronModelManager.Constants.multilingualModelId
        )
        #expect(
            NemotronModelManager.Constants.modelIdForSelection("   ")
                == NemotronModelManager.Constants.multilingualModelId
        )
    }

    @Test func knownSelectionIdentifiersNormalizeToTheCanonicalId() {
        #expect(
            NemotronModelManager.Constants.modelIdForSelection(
                NemotronModelManager.Constants.latinModelId
            ) == NemotronModelManager.Constants.latinModelId
        )
        #expect(
            NemotronModelManager.Constants.modelIdForSelection("  NEMOTRON-ASR-3.5-LATIN ")
                == NemotronModelManager.Constants.latinModelId
        )
        #expect(
            NemotronModelManager.Constants.modelIdForSelection(
                NemotronModelManager.Constants.multilingualModelId
            ) == NemotronModelManager.Constants.multilingualModelId
        )
    }

    /// An unknown explicit value survives unchanged rather than being replaced
    /// by the default, so the router can reject the id the caller actually
    /// sent instead of transcribing with a model nobody asked for.
    ///
    /// Review round 2: `TranscriptionProviderRouter.resolveProvider` is now the
    /// only caller that passes a value here — `applyEngineModel` stopped
    /// forwarding unknown ids, because on the mixed `mode_id` path no router
    /// guard ever ran on them. This pass-through is still what puts the
    /// caller's own spelling into `resolveProvider`'s error message.
    @Test func unknownSelectionIdentifiersSurviveUnchanged() {
        #expect(
            NemotronModelManager.Constants.modelIdForSelection("nemotron-3.5-latin")
                == "nemotron-3.5-latin"
        )
        #expect(NemotronModelManager.Constants.modelIdForSelection("  typo  ") == "typo")
    }

    // MARK: - Constants.engineAliases

    /// Review round 1. `resolveProvider` and `applyEngineModel` used to hold
    /// literal copies of these four spellings, and only `applyEngineModel` is
    /// reachable from this target — deleting `"nemotron-asr"` from the router
    /// left every test green while `POST /transcribe {"engine":"nemotron-asr"}`
    /// started answering `Unknown engine`. Both switches now match this one
    /// set, so they cannot drift; this test pins the set itself, which is the
    /// only thing left that can change silently.
    @Test func theSharedEngineAliasSetIsPinned() {
        #expect(
            NemotronModelManager.Constants.engineAliases == [
                "nemotron",
                "nemotronlocal",
                "nemotron-local",
                "nemotron-asr",
            ],
            "an engine spelling was added or removed — the OpenAPI contract's `engine` description must match"
        )

        // Both switches lowercase before matching, so an entry that is not
        // already lowercase is dead and would silently accept nothing.
        for alias in NemotronModelManager.Constants.engineAliases {
            #expect(alias == alias.lowercased(), "'\(alias)' can never be matched")
        }

        // Issue #356 moved the matching itself into `hw-localapi`, so the two
        // switches no longer read this set — they read the shared table, which
        // Windows and the portable head read too. This set is now the PIN that
        // the two agree: a spelling added here and not there (or the reverse)
        // is a Nemotron request that works on one head and not another, which
        // is exactly the drift #356 exists to stop.
        for alias in NemotronModelManager.Constants.engineAliases {
            #expect(
                localApiResolveEngineAlias(alias: alias) == .nemotron,
                "'\(alias)' is a macOS Nemotron spelling the shared engine table does not resolve"
            )
        }

        // The label `/transcribe` reports back must itself be an accepted
        // spelling, so a client can feed a response's `engine` straight into
        // the next request.
        let persistence = PersistenceController(inMemory: true)
        let mode = Mode(context: persistence.container.viewContext)
        mode.model = NemotronModelManager.Constants.multilingualModelId
        let label = TranscribeEndpoint.engineLabel(forMode: mode)
        #expect(
            NemotronModelManager.Constants.engineAliases.contains(label.lowercased()),
            "the response label '\(label)' is not an engine spelling the API accepts back"
        )
    }

    // MARK: - TranscribeEndpoint.applyEngineModel(to:engine:model:)

    /// Every accepted spelling of `engine=`, including the mixed case the
    /// switch normalizes away. `"nemotron"` and `"nemotronLocal"` are the two
    /// the issue's repro actually sent. Driven off `Constants.engineAliases`
    /// so a spelling added to the shared list is exercised here without
    /// anyone remembering to widen this array.
    @Test func everyNemotronEngineAliasSelectsTheDefaultVariant() {
        let persistence = PersistenceController(inMemory: true)
        let aliases = Array(NemotronModelManager.Constants.engineAliases) + [
            "Nemotron", "NEMOTRON",
            "nemotronLocal",
            "Nemotron-Local",
            "NEMOTRON-ASR",
        ]

        for alias in aliases {
            let mode = Mode(context: persistence.container.viewContext)
            TranscribeEndpoint.applyEngineModel(to: mode, engine: alias, model: nil)
            #expect(
                mode.model == NemotronModelManager.Constants.multilingualModelId,
                """
                engine '\(alias)' left mode.model as '\(mode.model ?? "nil")'. An alias that \
                misses the switch falls through `default:`, which with no `model` leaves the \
                Mode untouched and reports the wrong engine on the response.
                """
            )
            #expect(TranscribeEndpoint.engineLabel(forMode: mode) == "nemotron")
        }
    }

    /// An explicit `model=` beats the default, in either direction, and is
    /// canonicalised on the way in.
    @Test func anExplicitModelBeatsTheDefaultVariant() {
        let persistence = PersistenceController(inMemory: true)

        let latin = Mode(context: persistence.container.viewContext)
        TranscribeEndpoint.applyEngineModel(
            to: latin,
            engine: "nemotron",
            model: NemotronModelManager.Constants.latinModelId
        )
        #expect(latin.model == NemotronModelManager.Constants.latinModelId)
        #expect(TranscribeEndpoint.engineLabel(forMode: latin) == "nemotron")

        let multilingual = Mode(context: persistence.container.viewContext)
        TranscribeEndpoint.applyEngineModel(
            to: multilingual,
            engine: "nemotron-asr",
            model: "  NEMOTRON-ASR-3.5-MULTILINGUAL  "
        )
        #expect(multilingual.model == NemotronModelManager.Constants.multilingualModelId)
        #expect(TranscribeEndpoint.engineLabel(forMode: multilingual) == "nemotron")

        // A blank model is not an override — it falls back to the default.
        let blank = Mode(context: persistence.container.viewContext)
        TranscribeEndpoint.applyEngineModel(to: blank, engine: "nemotron", model: "   ")
        #expect(blank.model == NemotronModelManager.Constants.multilingualModelId)
    }

    /// Review round 1, the mixed `mode_id` + `engine` form. `makeTransientMode`
    /// copies `baseline.model` onto the transient Mode BEFORE calling
    /// `applyEngineModel`, so on that path the Mode already carries the
    /// variant the user saved. Re-asserting `engine=nemotron` with no `model`
    /// must not swap it: a caller with only the Latin variant installed
    /// otherwise gets MODEL_NOT_INSTALLED for the multilingual default they
    /// never asked for.
    @Test func anInheritedNemotronVariantSurvivesAnEngineOnlyOverride() {
        let persistence = PersistenceController(inMemory: true)
        let aliases = ["nemotron", "nemotronLocal", "nemotron-local", "nemotron-asr"]

        for alias in aliases {
            for inherited in [
                NemotronModelManager.Constants.latinModelId,
                NemotronModelManager.Constants.multilingualModelId,
            ] {
                let mode = Mode(context: persistence.container.viewContext)
                mode.model = inherited
                // Explicit, because inheriting is now conditional on the
                // language too (see the round-2 test below) and "en" is the
                // one code BOTH variants serve. Left implicit this would ride
                // on the Core Data default and break for a confusing reason if
                // that default ever moved.
                mode.language = "en"
                TranscribeEndpoint.applyEngineModel(to: mode, engine: alias, model: nil)
                #expect(
                    mode.model == inherited,
                    "engine '\(alias)' with no model replaced the inherited '\(inherited)' with '\(mode.model ?? "nil")'"
                )

                // A blank/whitespace model is not an override either, so it
                // must inherit on exactly the same terms as nil.
                let blank = Mode(context: persistence.container.viewContext)
                blank.model = inherited
                blank.language = "en"
                TranscribeEndpoint.applyEngineModel(to: blank, engine: alias, model: "   ")
                #expect(blank.model == inherited)
            }
        }

        // An inherited id written by an older build in a different case is
        // still recognised, and is normalised to the canonical spelling.
        let shouted = Mode(context: persistence.container.viewContext)
        shouted.model = NemotronModelManager.Constants.latinModelId.uppercased()
        shouted.language = "en"
        TranscribeEndpoint.applyEngineModel(to: shouted, engine: "nemotron", model: nil)
        #expect(shouted.model == NemotronModelManager.Constants.latinModelId)

        // An explicit model still beats the inherited one — inheriting is only
        // the no-model fallback, not a veto on the request.
        let overridden = Mode(context: persistence.container.viewContext)
        overridden.model = NemotronModelManager.Constants.latinModelId
        TranscribeEndpoint.applyEngineModel(
            to: overridden,
            engine: "nemotron",
            model: NemotronModelManager.Constants.multilingualModelId
        )
        #expect(overridden.model == NemotronModelManager.Constants.multilingualModelId)
    }

    /// The other half of the inherit rule: only a REAL Nemotron variant is
    /// inheritable. Anything else on the baseline Mode — the Core Data default
    /// "base", another engine's id, or a prefix-alike that names no variant —
    /// is not a Nemotron selection, so `engine=nemotron` falls back to the
    /// engine default rather than carrying a foreign id into the router.
    @Test func aNonNemotronInheritedModelFallsBackToTheDefaultVariant() {
        let persistence = PersistenceController(inMemory: true)
        let foreign = [
            "base",                       // the Core Data default on a fresh Mode
            "large-v3-turbo",
            ParakeetModelManager.Constants.v2ModelId,
            Qwen3AsrModelManager.Constants.modelId,
            "apple-speech-analyzer",
            "cloud",
            "nemotron-asr-3.5-bogus",     // prefix-alike, names no real variant
            "",
        ]

        for inherited in foreign {
            let mode = Mode(context: persistence.container.viewContext)
            mode.model = inherited
            TranscribeEndpoint.applyEngineModel(to: mode, engine: "nemotron", model: nil)
            #expect(
                mode.model == NemotronModelManager.Constants.multilingualModelId,
                "inherited '\(inherited)' should not have been kept as a Nemotron selection"
            )
            #expect(TranscribeEndpoint.engineLabel(forMode: mode) == "nemotron")
        }
    }

    /// Review round 2, the regression the round-1 inherit rule introduced.
    ///
    /// Round 1 taught the arm to keep a Mode's saved Nemotron variant, and
    /// looked only at the model id. Nothing downstream re-checks the variant
    /// against the language: `NemotronProvider.prepareIfNeeded(language:modelId:)`
    /// resolves the variant from `modelId` alone and never reads its `language`
    /// argument, and `transcribe` then passes `mode.language` straight to
    /// `setLanguage`. So `{mode_id: <a Latin mode>, engine: "nemotron",
    /// language: "ja"}` inherited Latin and returned HTTP 200 full of
    /// Latin-script garbage — a request that WORKED before the round-1 fix,
    /// because it used to get multilingual.
    ///
    /// The rule now: inherit only a variant that can serve the requested
    /// language, otherwise take the multilingual default, whose unlisted codes
    /// degrade to the model's own auto-detect prompt rather than to the wrong
    /// alphabet.
    @Test func anInheritedVariantIsDroppedWhenItCannotServeTheRequestedLanguage() {
        let persistence = PersistenceController(inMemory: true)

        func inheritedModel(savedVariant: String, language: String?) -> String? {
            let mode = Mode(context: persistence.container.viewContext)
            mode.model = savedVariant
            mode.language = language
            TranscribeEndpoint.applyEngineModel(to: mode, engine: "nemotron", model: nil)
            return mode.model
        }

        let latin = NemotronModelManager.Constants.latinModelId
        let multilingual = NemotronModelManager.Constants.multilingualModelId

        // The bug, and its neighbours: every language the multilingual variant
        // lists and the Latin one does not.
        for language in ["ja", "zh", "ko", "ar", "ru", "hi", "th", "he"] {
            #expect(
                NemotronModelManager.latinLanguages[language] == nil,
                "'\(language)' is in latinLanguages — this case no longer tests anything"
            )
            #expect(
                inheritedModel(savedVariant: latin, language: language) == multilingual,
                """
                a Latin-variant Mode plus language '\(language)' kept Latin. The Latin \
                variant's vocabulary is pruned to \
                \(NemotronModelManager.latinLanguages.keys.sorted().joined(separator: "/")), so \
                that transcribes to Latin-script garbage with a 200 status.
                """
            )
        }

        // A language no variant lists is still safer on multilingual.
        #expect(inheritedModel(savedVariant: latin, language: "cy") == multilingual)

        // Round 1's fix must NOT regress: a Latin Mode keeps Latin for every
        // language the Latin variant does serve, region subtag or not…
        for language in NemotronModelManager.latinLanguages.keys {
            #expect(inheritedModel(savedVariant: latin, language: language) == latin)
        }
        #expect(inheritedModel(savedVariant: latin, language: "pt-BR") == latin)
        #expect(inheritedModel(savedVariant: latin, language: "EN-us") == latin)
        #expect(inheritedModel(savedVariant: latin, language: "  de  ") == latin)

        // …and for "no language asked", in every spelling the rest of the
        // Local API treats as auto-detect. This is the exact case round 1
        // repaired, so it must survive unchanged.
        for language in [nil, "", "   ", "auto", "AUTO"] {
            #expect(
                inheritedModel(savedVariant: latin, language: language) == latin,
                "auto-detect ('\(language ?? "nil")') must still inherit the saved Latin variant"
            )
        }

        // The multilingual variant serves everything it lists, and inheriting
        // it is never downgraded.
        for language in ["ja", "zh", "en", "auto", nil, "cy"] {
            #expect(inheritedModel(savedVariant: multilingual, language: language) == multilingual)
        }
    }

    /// Review round 2. `engine=nemotron` must never be able to run a different
    /// engine.
    ///
    /// The arm used to pass an unknown explicit `model` through unchanged, on
    /// the reasoning that `resolveProvider` rejects it first. It does — but only
    /// on the engine-only path. The mixed `mode_id` + `engine` form resolves
    /// through `selectProvider(for: transient)` and never reaches
    /// `resolveProvider`, so `{mode_id: X, engine: "nemotron", model: "base"}`
    /// left "base" on the Mode, `selectLocalProvider`'s
    /// `mapModelIdToWhisperModel` matched it (`lower.contains("base")`), and
    /// LibWhisper answered `ok: true` with `"engine": "whisperLocal"`.
    /// `"parakeet-tdt-0.6b-v3"` reached ParakeetProvider by the same route.
    ///
    /// The property is about the Mode, not about which error is raised: after
    /// this call `mode.model` must always name a real Nemotron variant, so no
    /// other engine's provider can be selected from it.
    @Test func anExplicitNonNemotronModelNeverSurvivesOnTheMode() {
        let persistence = PersistenceController(inMemory: true)

        let foreign = [
            "base",                                  // routed to LibWhisper by substring
            "large-v3-turbo",
            "small.en",
            "ggml-medium",
            ParakeetModelManager.Constants.v2ModelId,
            ParakeetModelManager.Constants.v3ModelId,
            Qwen3AsrModelManager.Constants.modelId,
            "apple-speech-analyzer",
            "cloud",
            "nemotron-3.5-latin",                    // the spelling in the issue's own curl
            "nemotron-3.5-ml-560ms",                 // the Windows/Linux id for another model
            "nemotron-asr-3.5-",                     // the bare router prefix
            "nemotron-asr-3.5-bogus",                // prefix-alike, names no variant
            "typo",
        ]

        // Both request shapes: no baseline Mode (engine-only) and a baseline
        // Mode carrying each of the things `mode.model` can actually hold.
        let baselines: [String?] = [
            nil,
            "base",
            NemotronModelManager.Constants.latinModelId,
            NemotronModelManager.Constants.multilingualModelId,
        ]

        for requested in foreign {
            for baseline in baselines {
                let mode = Mode(context: persistence.container.viewContext)
                if let baseline { mode.model = baseline }
                mode.language = "en"
                TranscribeEndpoint.applyEngineModel(to: mode, engine: "nemotron", model: requested)

                let landed = mode.model ?? ""
                #expect(
                    NemotronModelManager.variant(forModelId: landed) != nil,
                    """
                    engine 'nemotron' with model '\(requested)' (baseline '\(baseline ?? "nil")') \
                    left '\(landed)' on the Mode, which is not a Nemotron variant — \
                    selectLocalProvider will hand this request to another engine's provider.
                    """
                )
                #expect(
                    TranscribeEndpoint.engineLabel(forMode: mode) == "nemotron",
                    "the response would report an engine the caller did not ask for"
                )
            }
        }

        // The coercion is "treat an unusable model as absent", so it lands on
        // exactly what the no-model form would have chosen: the saved variant
        // when there is one, the default otherwise.
        func landedModel(baseline: String?, requested: String?) -> String? {
            let mode = Mode(context: persistence.container.viewContext)
            if let baseline { mode.model = baseline }
            mode.language = "en"
            TranscribeEndpoint.applyEngineModel(to: mode, engine: "nemotron", model: requested)
            return mode.model
        }
        #expect(
            landedModel(baseline: NemotronModelManager.Constants.latinModelId, requested: "base")
                == NemotronModelManager.Constants.latinModelId
        )
        #expect(
            landedModel(baseline: "base", requested: "typo")
                == NemotronModelManager.Constants.multilingualModelId
        )
        // …and a real variant is still honoured over the baseline, which is the
        // whole point of sending `model` at all.
        #expect(
            landedModel(
                baseline: NemotronModelManager.Constants.latinModelId,
                requested: NemotronModelManager.Constants.multilingualModelId
            ) == NemotronModelManager.Constants.multilingualModelId
        )
    }

    // MARK: - TranscribeEndpoint.engineLabel(forMode:)

    /// The direct regression test for the issue's first comment: a saved Mode
    /// whose `model` is a Nemotron id reported `"engine": "whisperLocal"`,
    /// because Nemotron had no branch and fell to the chain's fallback. Both
    /// variants must report `"nemotron"` — the label the alias list also
    /// accepts back, so a client can round-trip it.
    @Test func bothNemotronVariantsLabelAsNemotron() {
        let persistence = PersistenceController(inMemory: true)
        let ids = [
            NemotronModelManager.Constants.latinModelId,
            NemotronModelManager.Constants.multilingualModelId,
        ]

        for id in ids {
            let mode = Mode(context: persistence.container.viewContext)
            mode.model = id
            let label = TranscribeEndpoint.engineLabel(forMode: mode)
            #expect(
                label == "nemotron",
                "a Mode saved on '\(id)' reported engine '\(label)'"
            )
            // Case is normalized before the prefix test, so a Mode written by
            // an older build with a shouted id still labels correctly.
            let shouted = Mode(context: persistence.container.viewContext)
            shouted.model = id.uppercased()
            #expect(TranscribeEndpoint.engineLabel(forMode: shouted) == "nemotron")
        }
    }

    /// The guard on the four labels that were already there. The Nemotron
    /// branch was inserted into the middle of an if-chain whose last line is
    /// an unconditional `return "whisperLocal"`, so the fallback arm is the
    /// thing most at risk from a mis-ordered or over-broad prefix test.
    @Test func thePreExistingEngineLabelsAreUnchanged() {
        let persistence = PersistenceController(inMemory: true)

        func label(model: String, cloudProvider: String? = nil) -> String {
            let mode = Mode(context: persistence.container.viewContext)
            mode.model = model
            mode.cloudProvider = cloudProvider
            return TranscribeEndpoint.engineLabel(forMode: mode)
        }

        // Cloud: the provider name, or the literal when there is none.
        #expect(label(model: "cloud", cloudProvider: CloudProvider.openai.rawValue) == "openai")
        #expect(label(model: "cloud") == "cloud")
        #expect(label(model: "") == "cloud")

        #expect(label(model: ParakeetModelManager.Constants.v2ModelId) == "parakeet")
        #expect(label(model: ParakeetModelManager.Constants.v3ModelId) == "parakeet")
        #expect(label(model: "apple-speech-analyzer") == "appleSpeech")
        #expect(label(model: Qwen3AsrModelManager.Constants.modelId) == "qwen3Asr")

        // And the fallback still catches everything else, which on this path
        // means the whisper.cpp model names.
        #expect(label(model: "base") == "whisperLocal")
        #expect(label(model: "large-v3-turbo") == "whisperLocal")
        // Including a near-miss Nemotron spelling: the prefix test is exact,
        // so an unrecognised id must not be labelled as a Nemotron run.
        #expect(label(model: "nemotron-3.5-latin") == "whisperLocal")
    }
}
