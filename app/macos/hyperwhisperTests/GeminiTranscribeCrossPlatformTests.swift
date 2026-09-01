//
//  GeminiTranscribeCrossPlatformTests.swift
//  hyperwhisperTests
//
//  The macOS half of the cross-platform seams Gemini 3.5 Transcribe is the first
//  provider to actually stress:
//
//  - `geminiTranscribe` is the first `byokEligible` provider whose id is
//    camelCase on Windows/Linux and lowercase here, so a Windows→macOS backup is
//    the first one that can silently move a BYOK user onto paid credits.
//  - It ships the first model HyperWhisper Cloud serves ONLY over a WebSocket,
//    so it is the first that must not appear as a dictation model.
//  - It replaced Chirp 3 as Google's cloud tier, so it is the first tier rewrite
//    that can reach a row whose provider is no longer HyperWhisper Cloud.
//
//  Fixtures are literal ids and dummy key strings only: no real keys.
//

import Foundation
import Testing
@testable import HyperWhisper

// MARK: - F2 · a Windows backup must not downgrade a BYOK user onto credits

@Suite("Cloud provider ids survive a cross-platform import")
struct CloudProviderStorageParsingTests {

    /// Windows persists and exports this exact spelling
    /// (`CloudTranscriptionProvider.cs`). macOS's raw value is all-lowercase.
    private static let windowsSpelling = "geminiTranscribe"

    @Test("An imported Windows `geminiTranscribe` resolves to BYOK Gemini Transcribe, not HyperWhisper Cloud")
    func windowsSpellingResolvesToTheByokProvider() {
        // THE defect. `CloudProvider(rawValue:)` is case-SENSITIVE and every
        // resolution site falls back to `.hyperwhisper`, so the miss is silent:
        // the user's own Google key stops being used, they are billed credits,
        // no error is raised and nothing in the UI changes.
        #expect(CloudProvider(rawValue: Self.windowsSpelling) == nil, "precondition: the raw lookup misses")

        let resolved = CloudProvider.parse(Self.windowsSpelling) ?? .hyperwhisper
        #expect(
            resolved == .geminiTranscribe,
            """
            an imported `geminiTranscribe` mode resolved to .\(resolved.rawValue). It must land on \
            .geminiTranscribe — .hyperwhisper is every call site's silent fallback, and taking it \
            moves a BYOK user onto metered credits with no error and no UI change.
            """
        )
        // And the resolved provider really is the BYOK one: HyperWhisper Cloud
        // is the arm that needs no key.
        #expect(resolved.requiresAPIKey)
        #expect(resolved != .hyperwhisper)
    }

    @Test("Every provider id round-trips regardless of case or padding")
    func everyIdParsesCaseInsensitively() {
        for provider in CloudProvider.allCases {
            if provider == .meta {
                #expect(CloudProvider.parse(provider.rawValue) == .hyperwhisper)
                continue
            }
            #expect(CloudProvider.parse(provider.rawValue) == provider)
            #expect(CloudProvider.parse(provider.rawValue.uppercased()) == provider)
            #expect(CloudProvider.parse("  \(provider.rawValue)  ") == provider)
        }
    }

    @Test("Unknown, empty and nil values still fail to parse")
    func unrecognisedValuesAreNotCoerced() {
        // A provider belonging to another platform must NOT be silently coerced
        // onto one of ours — the caller's own fallback is the right answer.
        for value in [nil, "", "   ", "notAProvider"] as [String?] {
            #expect(CloudProvider.parse(value) == nil)
        }
    }

    @Test("The write path canonicalises a known id and preserves an unknown one")
    func canonicalStorageValueNormalisesOnlyWhatItRecognises() {
        #expect(CloudProvider.canonicalStorageValue(Self.windowsSpelling) == "geminitranscribe")
        #expect(CloudProvider.canonicalStorageValue("DEEPGRAM") == "deepgram")
        #expect(CloudProvider.canonicalStorageValue("hyperwhisper") == "hyperwhisper")
        // Untouched: it may belong to a platform this build does not model, and
        // rewriting or dropping it loses the setting on the next export.
        #expect(CloudProvider.canonicalStorageValue("someFutureProvider") == "someFutureProvider")
        #expect(CloudProvider.canonicalStorageValue(nil) == nil)
        // Idempotent, so it can sit on a write path that already ran once and on
        // top of the shared core's own lowercasing.
        let once = CloudProvider.canonicalStorageValue(Self.windowsSpelling)
        #expect(CloudProvider.canonicalStorageValue(once) == once)
    }

    @Test("Gemini 3.5 Transcribe stays a separate slot from the Gemini LLM")
    func theTwoGoogleProvidersDoNotCollapse() {
        // Same vendor, different API and different key. `:generateContent`
        // accepts `gemini-3.5-transcribe`, bills the audio and returns empty
        // text, so merging them fails silently and expensively.
        #expect(CloudProvider.parse("gemini") == .gemini)
        #expect(CloudProvider.parse("geminitranscribe") == .geminiTranscribe)
        #expect(CloudProvider.gemini != CloudProvider.geminiTranscribe)
        #expect(KeychainManager.APIKeyType.gemini != KeychainManager.APIKeyType.geminiTranscribe)
    }
}

// MARK: - F3 · the new key has to survive a backup round trip

@MainActor
@Suite("The Gemini 3.5 Transcribe key survives a backup round trip")
struct GeminiTranscribeBackupKeyTests {

    private static let transcribeKey = "AIza-transcribe-fixture"
    private static let llmKey = "AIza-llm-fixture"

    @Test("Universal (v2) export writes the key and import reads it back")
    func universalRoundTripPreservesTheKey() {
        // Repro: configure ONLY the Gemini 3.5 Transcribe key, export with
        // "API keys" selected, restore on a new machine. Before the fix the
        // exported object had no member for it at all, so the provider came back
        // unconfigured — and the unrelated legacy `gemini` key restoring fine is
        // exactly what masks it.
        let exported = BackupManager.universalAPIKeyMap(read: { slot in
            slot == .geminiTranscribe ? Self.transcribeKey : nil
        })

        #expect(exported[BackupManager.geminiTranscribeBackupKey] == Self.transcribeKey)
        #expect(exported.count == 1, "no other slot was configured, so nothing else may be written")

        let restored = BackupManager.universalAPIKeyAssignments(from: exported)
        #expect(restored.count == 1)
        #expect(restored.first?.provider == .geminiTranscribe)
        #expect(restored.first?.key == Self.transcribeKey)
    }

    @Test("The two Google keys round-trip independently")
    func theLegacyGeminiKeyDoesNotStandInForTheNewOne() {
        let exported = BackupManager.universalAPIKeyMap(read: { slot in
            switch slot {
            case .gemini: return Self.llmKey
            case .geminiTranscribe: return Self.transcribeKey
            default: return nil
            }
        })
        #expect(exported["gemini"] == Self.llmKey)
        #expect(exported[BackupManager.geminiTranscribeBackupKey] == Self.transcribeKey)

        var restored: [KeychainManager.APIKeyType: String] = [:]
        for assignment in BackupManager.universalAPIKeyAssignments(from: exported) {
            restored[assignment.provider] = assignment.key
        }
        #expect(restored[.gemini] == Self.llmKey)
        #expect(restored[.geminiTranscribe] == Self.transcribeKey)
    }

    @Test("The exported member name is the documented lowercase one")
    func exportedMemberNameMatchesTheKeychainSlot() {
        // The schema's ApiKeys object is documented as "API keys by lowercase
        // provider name", and every sibling member (assemblyai, elevenlabs)
        // follows it. Windows/Linux must write the same string.
        #expect(BackupManager.geminiTranscribeBackupKey == "geminitranscribe")
        #expect(
            BackupManager.geminiTranscribeBackupKey
                == KeychainManager.APIKeyType.geminiTranscribe.rawValue
        )
        #expect(
            BackupManager.universalAPIKeyMembers.contains(where: {
                $0.name == BackupManager.geminiTranscribeBackupKey && $0.provider == .geminiTranscribe
            })
        )
    }

    @Test("Import also accepts the camelCase spelling, and the documented one wins")
    func importAcceptsTheCamelCaseAlias() {
        // The mode-level `cloudProvider` id for this provider IS camelCase on
        // Windows and Linux, so a writer mirroring that spelling is a live risk
        // — and a silently dropped key is unrecoverable for the user.
        let aliasOnly = BackupManager.universalAPIKeyAssignments(from: ["geminiTranscribe": Self.transcribeKey])
        #expect(aliasOnly.count == 1)
        #expect(aliasOnly.first?.provider == .geminiTranscribe)
        #expect(aliasOnly.first?.key == Self.transcribeKey)

        // Both present: assignments are applied in order, so the LAST one wins
        // and that must be the documented member.
        let both = BackupManager.universalAPIKeyAssignments(from: [
            "geminiTranscribe": "from-alias",
            BackupManager.geminiTranscribeBackupKey: "from-canonical",
        ])
        #expect(both.last?.provider == .geminiTranscribe)
        #expect(both.last?.key == "from-canonical")
    }

    @Test("Empty values and unknown members are ignored")
    func blankAndUnknownMembersAreSkipped() {
        #expect(BackupManager.universalAPIKeyMap(read: { _ in "" }).isEmpty)
        let assignments = BackupManager.universalAPIKeyAssignments(from: [
            BackupManager.geminiTranscribeBackupKey: "",
            "someFutureProvider": "x",
        ])
        #expect(assignments.isEmpty)
    }

    @Test("Legacy (v1) backups carry the key too, and older files still decode")
    func legacyBackupFormatRoundTripsTheKey() throws {
        // The v1 format is still the DEFAULT export
        // (`backup.useUniversalV2Export` is off unless opted in), so the same
        // gap here would hit far more users than the v2 one.
        let keys = BackupAPIKeys(
            openai: nil, groq: nil, fireworks: nil, anthropic: nil, gemini: nil,
            deepgram: nil, assemblyai: nil, elevenlabs: nil, mistral: nil, grok: nil,
            geminitranscribe: Self.transcribeKey
        )
        #expect(keys.hasAnyKey, "a backup holding only this key is not an empty backup")

        let data = try JSONEncoder().encode(keys)
        let json = try #require(
            try JSONSerialization.jsonObject(with: data) as? [String: Any]
        )
        #expect(json["geminitranscribe"] as? String == Self.transcribeKey)

        let decoded = try JSONDecoder().decode(BackupAPIKeys.self, from: data)
        #expect(decoded.geminitranscribe == Self.transcribeKey)

        // A file written before the member existed must still decode, with the
        // new field simply absent.
        let legacy = Data(#"{"openai":"sk-fixture"}"#.utf8)
        let older = try JSONDecoder().decode(BackupAPIKeys.self, from: legacy)
        #expect(older.geminitranscribe == nil)
        #expect(older.openai == "sk-fixture")
    }
}

// MARK: - F3 (related) · the streaming provider id is the cross-platform one

@Suite("The streaming provider id matches Windows and Linux")
struct StreamingProviderIdParityTests {

    @Test("Gemini's streaming id is `geminiTranscribe`")
    func geminiStreamingIdIsTheCrossPlatformSpelling() {
        // The id travels in `settings.streaming.provider` of the cross-platform
        // backup, and Windows/Linux already write `geminiTranscribe` (see the
        // `Tag="geminiTranscribe"` ComboBoxItem in StreamingSettingsPage.xaml).
        // macOS pinned `streaming: None` on export, so nothing in the wild
        // carries the old macOS spelling — the format is the constraint, and it
        // is already decided.
        #expect(StreamingTranscriptionProvider.gemini.rawValue == "geminiTranscribe")

        // And `gemini` unqualified means the vendor's OTHER product everywhere
        // else in this app, which is why the id may not be it.
        #expect(CloudProvider.gemini.rawValue == "gemini")
        #expect(StreamingTranscriptionProvider.gemini.apiKeyType == .geminiTranscribe)
    }

    @Test("The pre-release `gemini` value still resolves")
    func legacyStorageValueStillResolves() {
        #expect(StreamingTranscriptionProvider.fromStorageValue("geminiTranscribe") == .gemini)
        #expect(StreamingTranscriptionProvider.fromStorageValue("gemini") == .gemini)
        #expect(StreamingTranscriptionProvider.fromStorageValue(" gemini ") == .gemini)
    }

    @Test("No other provider's id moved")
    func theOtherProviderIdsAreUnchanged() {
        // A rename here silently unsets the user's streaming provider, because
        // every read falls back to HyperWhisper Cloud.
        #expect(StreamingTranscriptionProvider.hyperwhisperCloud.rawValue == "hyperwhisperCloud")
        #expect(StreamingTranscriptionProvider.deepgram.rawValue == "deepgram")
        #expect(StreamingTranscriptionProvider.elevenLabs.rawValue == "elevenLabs")
        #expect(StreamingTranscriptionProvider.openAI.rawValue == "openAI")
        #expect(StreamingTranscriptionProvider.xai.rawValue == "xai")
        #expect(StreamingTranscriptionProvider.parakeetLocal.rawValue == "parakeetLocal")
        #expect(StreamingTranscriptionProvider.nemotronLocal.rawValue == "nemotronLocal")

        for provider in StreamingTranscriptionProvider.allCases {
            #expect(StreamingTranscriptionProvider.fromStorageValue(provider.rawValue) == provider)
        }
    }

    @Test("An unknown or blank value does not resolve")
    func unknownStreamingValuesDoNotResolve() {
        for value in [nil, "", "   ", "notAProvider"] as [String?] {
            #expect(StreamingTranscriptionProvider.fromStorageValue(value) == nil)
        }
    }
}

// MARK: - F8 · a WebSocket-only model must never be a dictation model

@Suite("Live-only cloud models are not selectable for dictation")
struct LiveOnlyCloudModelTests {

    private static let liveModelId = "gemini-3.5-transcribe-live"

    @Test("The mode editor's Model dropdown drops the live-only model")
    func dictationPickerExcludesTheLiveOnlyModel() {
        // `/transcribe` answers this id with a 400 ("WebSocket-only model and is
        // not served by /transcribe"), so every dictation in a mode carrying it
        // fails. The dropdown flat-mapped the whole vendor group with no filter.
        let tier = CloudAccuracyTier.geminiTranscribe
        #expect(
            tier.vendorGroupModels.contains(where: { $0.id == Self.liveModelId }),
            "precondition: the catalog does list the live model under Google's row"
        )
        #expect(!tier.vendorGroupDictationModels.contains(where: { $0.id == Self.liveModelId }))
        #expect(!tier.dictationModels.contains(where: { $0.id == Self.liveModelId }))

        // The row must not go empty — the batch model is still there.
        #expect(tier.vendorGroupDictationModels.contains(where: { $0.id == "gemini-3.5-transcribe" }))
    }

    @Test("REGRESSION GUARD: the per-model `streaming` flag is NOT the filter")
    func deepgramNova3StaysSelectableForDictation() {
        // `streaming: true` means "HyperWhisper Cloud routes this model live",
        // and BOTH Deepgram Nova 3 models carry it while remaining the DEFAULT
        // pre-recorded models. Filtering the dictation picker on that flag would
        // delete the default dictation model from it.
        let tier = CloudAccuracyTier.deepgramNova3
        #expect(
            tier.models.contains(where: { $0.id == "nova-3-general" && $0.streaming == true }),
            "precondition: nova-3-general is flagged streaming AND is a batch model"
        )
        #expect(tier.dictationModels.contains(where: { $0.id == "nova-3-general" }))
        #expect(tier.dictationModels.contains(where: { $0.id == "nova-3-medical" }))
        #expect(tier.dictationModels.count == tier.models.count)
    }

    @Test("The live-only test is trimmed and case-insensitive")
    func liveOnlyLookupMatchesTheRestOfTheCatalog() {
        #expect(CloudSTTCatalog.isLiveOnlyModel(Self.liveModelId))
        #expect(CloudSTTCatalog.isLiveOnlyModel("  GEMINI-3.5-TRANSCRIBE-LIVE  "))
        #expect(!CloudSTTCatalog.isLiveOnlyModel("gemini-3.5-transcribe"))
        #expect(!CloudSTTCatalog.isLiveOnlyModel("nova-3-general"))
        for value in [nil, "", "   "] as [String?] {
            #expect(!CloudSTTCatalog.isLiveOnlyModel(value))
        }
    }

    @Test("The send path rejects a stored live-only model instead of posting it")
    func sendPathValidationDropsTheLiveOnlyModel() {
        // Runs the PRODUCTION resolver — the one `transcribe(...)` calls to fill
        // `X-STT-Model` — rather than restating its ternary. A picker filter is
        // not a validation: a backup restore, a Local API PATCH or a mode saved
        // before the filter existed all still put a live-only id in this field,
        // and posting it 400s every dictation in that mode.
        let tier = CloudAccuracyTier.geminiTranscribe

        #expect(
            HyperWhisperCloudProvider.resolvedSTTModelId(tier: tier, storedModelId: Self.liveModelId)
                == "gemini-3.5-transcribe"
        )
        // Whitespace and the BYOK leftover take the same fallback.
        #expect(
            HyperWhisperCloudProvider.resolvedSTTModelId(
                tier: tier, storedModelId: "  \(Self.liveModelId)  "
            ) == "gemini-3.5-transcribe"
        )
        #expect(
            HyperWhisperCloudProvider.resolvedSTTModelId(tier: tier, storedModelId: "whisper-1")
                == "gemini-3.5-transcribe"
        )
        // A legitimate dictation model is passed through untouched, so the
        // rejection cannot be mistaken for "always send the default".
        #expect(
            HyperWhisperCloudProvider.resolvedSTTModelId(
                tier: tier, storedModelId: "gemini-3.5-transcribe"
            ) == "gemini-3.5-transcribe"
        )
        // ...including on a tier that has several, which a blanket default would
        // also break.
        #expect(
            HyperWhisperCloudProvider.resolvedSTTModelId(
                tier: .deepgramNova3, storedModelId: "nova-3-medical"
            ) == "nova-3-medical"
        )
        // The fallback must itself be a legal dictation model, or the rejection
        // just swaps one 400 for another.
        #expect(!CloudSTTCatalog.isLiveOnlyModel(tier.defaultModelId))
    }

    @Test("The editor's .onAppear clamp agrees with the model dropdown")
    func theEditorClampMatchesThePickerItOffers() {
        // The clamp and the picker read the same list. When they disagreed, a
        // mode carrying the live-only id passed the clamp (it is in
        // `tier.models`) and then matched no picker tag — a blank menu button on
        // a mode whose every dictation 400s.
        let tier = CloudAccuracyTier.geminiTranscribe
        #expect(
            ModeEditorView.clampedCloudTranscriptionModel(
                tier: tier, storedModelId: Self.liveModelId
            ) == "gemini-3.5-transcribe"
        )
        #expect(
            ModeEditorView.clampedCloudTranscriptionModel(
                tier: tier, storedModelId: "gemini-3.5-transcribe"
            ) == "gemini-3.5-transcribe"
        )
        // A sibling tier of the same company row is offered by the dropdown, so
        // the clamp must leave it alone rather than reset it to the tier default.
        let ownModelIds = Set(tier.dictationModels.map { $0.id })
        if let sibling = tier.vendorGroupDictationModels.first(where: {
            !ownModelIds.contains($0.id)
        }) {
            #expect(
                ModeEditorView.clampedCloudTranscriptionModel(
                    tier: tier, storedModelId: sibling.id
                ) == sibling.id
            )
        }
    }

    @Test("The live model is still reachable on the streaming path")
    func theLiveModelIsNotRemovedFromStreaming() {
        // The filter is dictation-only. Google's tier must stay in the live
        // picker, or the fix would delete the feature this PR adds.
        #expect(CloudAccuracyTier.streamingEligibleTiers.contains(.geminiTranscribe))
        #expect(CloudAccuracyTier.geminiTranscribe.models.contains(where: { $0.id == Self.liveModelId }))
    }
}

// MARK: - F11 · the Chirp 3 tier rewrite must not touch a BYOK mode

@Suite("The Chirp 3 tier migration is scoped to HyperWhisper Cloud modes")
struct GoogleChirp3TierMigrationScopeTests {

    private typealias Rewrite = PersistenceController.GoogleChirp3TierRewrite

    private func rewrite(
        provider: String?,
        tier: String?,
        model: String?
    ) -> Rewrite? {
        PersistenceController.googleChirp3TierRewrite(
            cloudProvider: provider,
            cloudAccuracyTier: tier,
            cloudTranscriptionModel: model
        )
    }

    @Test("A BYOK Grok mode carrying a stale Chirp tier is left completely alone")
    func byokModeIsUntouched() {
        // THE defect. A mode switched from HW-Cloud+Chirp3 to BYOK Grok keeps
        // the stale tier, and Grok's single model is catalogued under the EMPTY
        // id — so the `storedModel.isEmpty` branch fired and stamped Google's
        // `gemini-3.5-transcribe` onto it. The next Grok dictation then POSTs
        // that model id to xAI.
        #expect(rewrite(provider: "grok", tier: "googleChirp3", model: "") == nil)
        #expect(rewrite(provider: "grok", tier: "chirp_3", model: nil) == nil)
        #expect(rewrite(provider: "deepgram", tier: "googlechirp3", model: "") == nil)
        // Including when the stored provider is a cross-platform camelCase id,
        // which is exactly the value an imported Windows mode carries.
        #expect(rewrite(provider: "geminiTranscribe", tier: "googleChirp3", model: "") == nil)
    }

    @Test("A HyperWhisper Cloud mode still migrates, tier and model together")
    func hyperwhisperCloudModeStillMigrates() throws {
        let migrated = try #require(rewrite(provider: "hyperwhisper", tier: "googleChirp3", model: "chirp_3"))
        #expect(migrated.accuracyTier == CloudAccuracyTier.geminiTranscribe.rawValue)
        #expect(migrated.transcriptionModel == "gemini-3.5-transcribe")

        // Every retired alias, and casing/padding from a hand-edited row.
        for stale in ["googleChirp3", "googlechirp3", " GOOGLECHIRP3 ", "googlechirp",
                      "google-chirp", "chirp", "chirp_3", "googlespeech"] {
            #expect(
                rewrite(provider: "hyperwhisper", tier: stale, model: nil)?.accuracyTier
                    == CloudAccuracyTier.geminiTranscribe.rawValue,
                "stale tier '\(stale)' must still migrate on a HyperWhisper Cloud mode"
            )
        }
    }

    @Test("A nil or blank provider counts as HyperWhisper Cloud")
    func absentProviderIsTreatedAsCloud() {
        // Matches `createOrUpdateMode`'s `?? "hyperwhisper"` default, so the
        // guard does not accidentally strand the rows it exists to migrate.
        #expect(rewrite(provider: nil, tier: "googleChirp3", model: "")?.accuracyTier
                == CloudAccuracyTier.geminiTranscribe.rawValue)
        #expect(rewrite(provider: "", tier: "googleChirp3", model: "")?.accuracyTier
                == CloudAccuracyTier.geminiTranscribe.rawValue)
        #expect(rewrite(provider: "HYPERWHISPER", tier: "googleChirp3", model: "")?.accuracyTier
                == CloudAccuracyTier.geminiTranscribe.rawValue)
    }

    @Test("A deliberate non-Chirp model choice is carried forward, not overwritten")
    func aDeliberateModelChoiceSurvives() throws {
        let migrated = try #require(
            rewrite(provider: "hyperwhisper", tier: "googleChirp3", model: "gemini-3.5-transcribe")
        )
        #expect(migrated.accuracyTier == CloudAccuracyTier.geminiTranscribe.rawValue)
        #expect(migrated.transcriptionModel == nil, "nil means 'leave the stored model alone'")
    }

    @Test("A row on any other tier is not matched at all")
    func unrelatedTiersAreNotMatched() {
        for tier in [nil, "", "deepgramNova3", "geminiTranscribe", "grokStt"] as [String?] {
            #expect(rewrite(provider: "hyperwhisper", tier: tier, model: "") == nil)
        }
    }
}
