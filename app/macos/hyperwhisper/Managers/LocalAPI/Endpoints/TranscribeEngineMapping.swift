//
//  TranscribeEngineMapping.swift
//  hyperwhisper
//
//  The `/transcribe` engine + model alias mapping: how a wire `engine` /
//  `model` pair is written onto a Mode, the mixed mode_id + engine checks, and
//  the `engine` / `model` labels a response reports. Split out of
//  `TranscribeEndpoint` (issue #733) and moved unchanged; `TranscribeEndpoint`
//  keeps one-line forwarders under the old names.
//

import Foundation
import CoreData

enum TranscribeEngineMapping {

    typealias APIInputError = TranscribeEndpoint.APIInputError

    /// The error the mixed mode_id + engine form throws when
    /// `selectProvider(for: transient)` fails (issue #1919). With `engine`
    /// naming Nemotron, a `.modelNotDownloaded` becomes the same named
    /// MODEL_NOT_INSTALLED the engine-only form gives since #1493
    /// (`APIInputError.nemotronModelNotInstalled`), naming the variant the
    /// transient Mode tried. Every other error, and every other engine, passes
    /// through unchanged.
    @MainActor
    static func mixedPathSelectionError(
        _ error: Error,
        engine: String?,
        modeModel: String?,
        isInstalled: (String) -> Bool
    ) -> Error {
        guard let transcriptionError = error as? TranscriptionError,
              case .modelNotDownloaded = transcriptionError,
              let engine,
              case .nemotron? = localApiResolveEngineAlias(alias: engine),
              let triedModelId = NemotronModelManager.Constants.canonicalModelId(for: modeModel ?? "")
        else {
            return error
        }
        return APIInputError.nemotronModelNotInstalled(triedModelId, isInstalled: isInstalled)
    }

    /// Encode an engine + model override pair onto a Mode's `model` /
    /// `cloudProvider` / `cloudTranscriptionModel` fields.
    @MainActor
    static func applyEngineModel(to mode: Mode, engine: String, model: String?) {
        // TRIM FIRST, ON BOTH HALVES (issue #356 item 3, review round 1).
        // `localApiResolveEngineAlias` trims, but only the five LOCAL ids go
        // through it — the cloud half below matches literals against this
        // string. Untrimmed, `{"engine":" cloud"}` was not a `CloudProvider`
        // rawValue and not a `hw-catalog` alias either (`cloud` exists only as
        // the literal comparison a few lines down), so it fell to the `nil` arm,
        // silently patched only `model`, and transcribed on the baseline engine
        // with `ok: true`. `" meta"` skipped the meta case and left
        // `cloudAccuracyTier` uncleared. Windows and the portable head both
        // route the same body to Cloud, and the `openapi.yaml` line #356 adds
        // says the value is trimmed and case-insensitively matched on every
        // platform, with no carve-out.
        let trimmedEngine = engine.trimmingCharacters(in: .whitespacesAndNewlines)
        let normalizedEngine = trimmedEngine.lowercased()
        if normalizedEngine == "meta" {
            mode.model = "cloud"
            mode.cloudProvider = CloudProvider.meta.rawValue
            mode.cloudAccuracyTier = nil
            mode.cloudTranscriptionModel = model?.isEmpty == false
                ? model
                : CloudTranscriptionModels.defaultModel(for: .meta)
            return
        }
        let providerNormalization = CloudSTTCatalog.shared.normalizeCloudProvider(normalizedEngine)
        let cloudType: CloudProvider?
        if normalizedEngine == "cloud" {
            cloudType = .hyperwhisper
        } else {
            cloudType = CloudProvider.parse(providerNormalization.provider)
        }

        if let cloudType {
            // Capture BEFORE overwriting cloudProvider below — to decide whether
            // the inherited model is foreign we need the provider it came from.
            let priorProvider = mode.cloudProvider
            let priorModel = mode.cloudTranscriptionModel

            mode.model = "cloud"
            mode.cloudProvider = cloudType.rawValue
            if let inferredTier = providerNormalization.accuracyTier {
                mode.cloudAccuracyTier = inferredTier
                if let m = model, !m.isEmpty {
                    mode.cloudTranscriptionModel = m
                }
                // Otherwise leave the field alone. This used to overwrite it
                // with the tier default unconditionally, which silently dropped
                // a sub-model the caller had pinned inside the SAME tier — the
                // Azure MAI entry has two models at different rates, so
                // `engine: "microsoftazurespeech"` and `engine: "cloud"` gave
                // opposite outcomes for one Mode, changing what ran and what it
                // cost. Nothing stale can leak through: `modelLabel(forMode:)`
                // and the send path both run the surviving id through
                // `resolvedSTTModelId`, which validates it against the NEW tier
                // and falls back to that tier's default when it does not belong.
                // This arm is only ever reached for HyperWhisper Cloud —
                // `normalizeCloudProvider` returns a tier only for a legacy
                // standalone alias that is now one of its accuracy tiers, and
                // then reports `provider: "hyperwhisper"` (issues #500, #533).
                return
            }
            if let m = model, !m.isEmpty {
                mode.cloudTranscriptionModel = m
            } else {
                // No explicit model. Keep the inherited one only when it
                // legitimately belongs to THIS provider; otherwise fall back to
                // the provider's default. "Belongs" is true when EITHER the mode
                // was already on this provider (the caller is just (re)asserting
                // the engine — preserve their saved sub-model, including providers
                // like HyperWhisper Cloud / Grok whose sub-models aren't listed in
                // CloudTranscriptionModels.models(for:) — they live under
                // CloudAccuracyTier), OR the inherited id appears in this
                // provider's model list. This avoids two failures: a fresh
                // transient leaking the Core Data default ("whisper-1") to every
                // non-OpenAI engine, and the mixed mode_id+engine form clobbering
                // a saved HyperWhisper Cloud model with "".
                //
                // The membership half uses `model(withId:provider:)`, not a raw
                // scan of `models(for:)`, because that lookup applies
                // `resolveModelAlias`: a legacy-but-serviceable id such as
                // AssemblyAI `universal` or Gemini `gemini-2.0-flash` is not in
                // the curated list, so a raw scan judged it foreign and silently
                // upgraded it to a different-priced model — and for a BYOK
                // provider that changes the model that RUNS, not just the label
                // (issue #533, mirroring #528's `GetById` fix on Windows).
                let belongsToProvider: Bool = {
                    guard let priorModel, !priorModel.isEmpty else { return false }
                    if priorProvider == cloudType.rawValue { return true }
                    return CloudTranscriptionModels.model(withId: priorModel, provider: cloudType) != nil
                }()
                if !belongsToProvider {
                    mode.cloudTranscriptionModel = CloudTranscriptionModels.defaultModel(for: cloudType)
                }
            }
            return
        }

        // ONE ALIAS TABLE, SHARED WITH WINDOWS AND THE PORTABLE HEAD (issue
        // #356 item 3). This switch and `TranscriptionProviderRouter
        // .resolveProvider` were two hand-synced copies on this platform alone,
        // and two more lived on the .NET heads. `localApiResolveEngineAlias` is
        // the union of all four: it normalises trim-then-lowercase and answers
        // a canonical id. macOS gains `qwen3_asr` and `qwen` from it — the
        // spellings the .NET heads EMIT as their `engine` label and this head
        // did not accept back — and gains the trim.
        //
        // `nil` KEEPS THIS SWITCH'S OLD `default` ARM, VERBATIM. It silently
        // accepts an unrecognised engine and patches only `model`, where every
        // sibling — including this head's own `resolveProvider` — fails. That
        // asymmetry is a real open question, but closing it is a client-visible
        // behaviour change on the one head this change could not build locally,
        // so #356 routes the RESOLUTION and leaves the verdict alone.
        guard let resolvedEngine = localApiResolveEngineAlias(alias: trimmedEngine) else {
            if let m = model, !m.isEmpty { mode.model = m }
            return
        }

        // A LOCAL ENGINE MUST NEVER LEAVE A CLOUD MODEL ON THE MODE (issue
        // #1466). `TranscriptionProviderRouter.selectProvider` sends a Mode to
        // its CLOUD branch when `model` trims to "" or reads "cloud" (the
        // empty-means-cloud rule exists for legacy/imported modes, #1440), and
        // then picks `CloudProvider.parse(cloudProvider) ?? .hyperwhisper`. The
        // mixed mode_id+engine path resolves through `selectProvider(for:
        // transient)` alone and never reaches `resolveProvider`, which is the
        // only place that refuses a blank Whisper model — the same gap the
        // nemotron arm below documents. So `{mode_id: X, engine:
        // "whisperLocal", model: ""}` wrote "" here (`??` catches nil only),
        // and on-device audio went to HyperWhisper Cloud: uploaded and billed
        // for any user with an account key. `model: "cloud"` did the same for
        // whisper, and for parakeet, whose `modelIdForSelection` passes an
        // unknown id through unchanged. `engine=` naming a local engine is a
        // request to stay on the Mac, so a model that would route to Cloud is
        // treated as absent: each arm then applies its own no-model default,
        // exactly as if the caller had omitted `model`. Nemotron, qwen3Asr and
        // appleSpeech already write only their own ids, so they need nothing.
        //
        // For WHISPER and for a "cloud" PARAKEET model this is now the
        // backstop, not the answer: `resolve` calls
        // `validateMixedPathEngineModel` first, which REFUSES a blank or
        // "cloud" Whisper model (Ray's decision 2026-10-08) and a Parakeet
        // model that does not canonicalise (review round 2), so the defaults
        // below are reached for those values only by a caller that skips that
        // check. It stays so no caller can ever leave a Cloud-routing model on
        // the Mode. A blank Parakeet model still defaults to v3 by design.
        let localModel: String? = Self.modelRoutesToCloud(model) ? nil : model

        switch resolvedEngine {
        case .whisperLocal:
            mode.model = localModel ?? "base"
        case .parakeet:
            mode.model = ParakeetModelManager.Constants.modelIdForSelection(localModel)
        // The four `nemotron*` spellings are now `EngineId::Nemotron` in the
        // shared table; `NemotronModelManager.Constants.engineAliases` stays as
        // the pin that the two lists still agree
        // (`NemotronLocalAPIEngineTests`).
        case .nemotron:
            // `canonicalModelId` trims, lowercases, and answers nil for blank,
            // so this one test covers "no model", "  ", and "not a variant".
            if let explicitVariant = NemotronModelManager.Constants.canonicalModelId(for: model ?? "") {
                // An explicit model naming a real variant wins outright: the
                // caller is changing variant, not re-asserting the engine, so
                // no inherit and no language snap.
                //
                // Review round 2: this arm now accepts ONLY a real variant. It
                // used to pass an unknown id through unchanged
                // (`modelIdForSelection` preserves it), on the reasoning that
                // the router would reject it and name it in the error. True on
                // the engine-only path, where `resolve` calls `resolveProvider`
                // BEFORE `makeTransientMode` — but the mixed mode_id+engine
                // path only calls `selectProvider(for: transient)` and never
                // reaches `resolveProvider`, so nothing rejected it. There
                // `{mode_id: X, engine: "nemotron", model: "base"}` wrote
                // "base" onto the Mode, `selectLocalProvider`'s
                // `mapModelIdToWhisperModel` matched it, and LibWhisper
                // transcribed — `ok: true`, `"engine": "whisperLocal"`, for a
                // caller who asked for Nemotron (`"parakeet-tdt-0.6b-v3"`
                // reached ParakeetProvider the same way). `engine=` must never
                // select another engine's provider, so an unusable model is
                // treated as absent and falls through to the inherit-or-default
                // rule below; the response's `model` then reports what ran.
                mode.model = explicitVariant
            } else {
                // No usable explicit model. Same reasoning as the cloud branch's
                // `belongsToProvider` guard above: on the mixed mode_id+engine
                // form the caller is re-asserting the engine, not asking to
                // change variant, and `mode` is already a copy of their saved
                // Mode — so keep an inherited id that really is a Nemotron
                // variant. Defaulting unconditionally would swap a saved Latin
                // mode to multilingual and fail with MODEL_NOT_INSTALLED for a
                // caller who only installed Latin. Anything else inherited
                // (the Core Data default "base", a Parakeet id, a prefix-alike
                // that names no real variant) is not usable here, so it falls
                // through to the engine's default.
                //
                // Review round 2: inherit only when the saved variant can also
                // serve the language THIS request asked for. `makeTransientMode`
                // has already written the request's `language` onto the Mode by
                // the time we get here, and nothing downstream re-checks the
                // pair: `NemotronProvider.prepareIfNeeded` takes the variant
                // from `modelId` alone and never reads its `language` argument,
                // then `transcribe` hands `mode.language` straight to
                // `setLanguage`. So `{mode_id: <a saved Latin mode>,
                // engine: "nemotron", language: "ja"}` would run "ja" against a
                // vocabulary pruned to `latinLanguages` (en/es/fr/it/pt/de) and
                // answer HTTP 200 with Latin-script garbage. Falling back to
                // multilingual is the safe direction: a code it does not list
                // degrades to the model's own auto-detect prompt (see
                // `multilingualLanguages`' note), not to the wrong alphabet.
                let inherited = NemotronModelManager.Constants.canonicalModelId(for: mode.model ?? "")
                if let inherited, Self.nemotronVariant(inherited, canServe: mode.language) {
                    mode.model = inherited
                } else {
                    mode.model = NemotronModelManager.Constants.modelIdForSelection(nil)
                }
            }
        case .qwen3Asr:
            mode.model = Qwen3AsrModelManager.Constants.modelId
        case .appleSpeech:
            mode.model = "apple-speech-analyzer"
        }
    }

    /// Throw, on the mixed mode_id+engine path, the error the engine-only path
    /// (`TranscriptionProviderRouter.resolveProvider`) throws for the same
    /// Whisper `engine` + `model` pair, where that error comes from the model
    /// string itself (issue #1466).
    ///
    /// - A blank model (`""`, whitespace) throws `resolveProvider`'s own
    ///   `APIInputError.whisperModelRequired` (INVALID_REQUEST, "A Whisper
    ///   model is required.", issue #1493). Ray's decision 2026-10-08: refuse
    ///   it, like Windows (`ApplyEngineModel`) and Linux
    ///   (`ApplyTranscriptionOverrides`) do on both paths, rather than default
    ///   it to "base".
    /// - `"cloud"` (any case, padded) throws "Unknown local model: <model>".
    ///   The engine-only path already refuses it that way: `resolveProvider`
    ///   passes it to `selectLocalProvider`, which matches no local model and
    ///   throws exactly this. On the mixed path the same value would instead
    ///   land on `mode.model`, where `selectProvider` reads it as Cloud.
    /// - `nil` (no `model` key) is NOT refused: it keeps the mixed path's
    ///   long-standing `base` default. Ray's question was about a blank value,
    ///   and a client that omits `model` with a saved mode is a shape callers
    ///   already rely on.
    ///
    /// Any other Whisper id is left to `selectProvider`, which already throws
    /// "Unknown local model" for one it cannot map.
    ///
    /// PARAKEET mirrors `resolveProvider`'s parakeet arm (#1466 review round
    /// 2): a non-blank model that does not canonicalise to a Parakeet id throws
    /// `APIInputError.unknownParakeetModel` (MODEL_NOT_FOUND "Unknown Parakeet
    /// model '<id>'", issue #1919). Without this the mixed path ran
    /// `{engine: "parakeet", model: "cloud"}` on v3 with `ok: true` (the
    /// backstop below dropped "cloud" to nil), and `model: "base"` swapped to
    /// local Whisper, where the engine-only path refuses both. A nil or blank
    /// Parakeet model still defaults to v3 — `modelIdForSelection` answers v3
    /// for both, on both paths.
    ///
    /// nemotron / qwen3Asr / appleSpeech write only their own ids, so they need
    /// nothing. A cloud engine is skipped exactly as `applyEngineModel` and
    /// `resolveProvider` check the cloud half first.
    @MainActor
    static func validateMixedPathEngineModel(engine: String, model: String?) throws {
        let trimmedEngine = engine.trimmingCharacters(in: .whitespacesAndNewlines)
        let normalizedEngine = trimmedEngine.lowercased()
        if normalizedEngine == "cloud" || normalizedEngine == "meta" {
            return
        }
        let providerNormalization = CloudSTTCatalog.shared.normalizeCloudProvider(normalizedEngine)
        if CloudProvider.parse(providerNormalization.provider) != nil {
            return
        }
        switch localApiResolveEngineAlias(alias: trimmedEngine) {
        case .whisperLocal?:
            guard let model else { return }
            let trimmedModel = model.trimmingCharacters(in: .whitespacesAndNewlines)
            if trimmedModel.isEmpty {
                throw APIInputError.whisperModelRequired
            }
            if trimmedModel.lowercased() == "cloud" {
                throw TranscriptionError.providerNotAvailable(provider: "Local", reason: "Unknown local model: \(trimmedModel)")
            }
        case .parakeet?:
            // The same two calls, in the same order, as `resolveProvider`.
            let requestedModelId = ParakeetModelManager.Constants.modelIdForSelection(model)
            if ParakeetModelManager.Constants.canonicalModelId(for: requestedModelId) == nil {
                throw APIInputError.unknownParakeetModel(requestedModelId)
            }
        default:
            return
        }
    }

    /// Whether a Mode whose `model` is `model` would take the CLOUD branch of
    /// `TranscriptionProviderRouter.selectProvider`. Mirrors that function's
    /// own test (trim, empty counts as cloud, case-insensitive "cloud") so the
    /// two cannot disagree about what "routes to Cloud" means. nil is not a
    /// cloud model here: it means "no override", which each local arm defaults.
    static func modelRoutesToCloud(_ model: String?) -> Bool {
        guard let model else { return false }
        let trimmed = model.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty || trimmed.lowercased() == "cloud"
    }

    /// Whether the Nemotron variant named by `modelId` can transcribe
    /// `language`. Used by `applyEngineModel` to decide if a variant inherited
    /// off a saved Mode is still appropriate for the language the request asked
    /// for; `RecordingTranscriptionFlow+Streaming` answers the same question the
    /// same way, off the same `supportedLanguages(forModelId:)` table, when it
    /// falls back between variants.
    ///
    /// A nil / empty / `"auto"` language means auto-detect, which every variant
    /// serves — the same normalisation `effectiveLanguage(for:request:)`,
    /// `resolveProvider` and `NemotronProvider.transcribe` all apply. Region and
    /// script subtags are stripped because `latinLanguages` /
    /// `multilingualLanguages` are keyed by bare ISO codes while a Mode may hold
    /// a BCP-47 form such as `"en-US"`. A non-Nemotron id serves nothing here.
    private static func nemotronVariant(_ modelId: String, canServe language: String?) -> Bool {
        let normalized = (language ?? "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased()
        guard !normalized.isEmpty, normalized != "auto" else { return true }
        guard let supported = NemotronModelManager.supportedLanguages(forModelId: modelId) else {
            return false
        }
        let baseCode = String(normalized.prefix { $0 != "-" && $0 != "_" })
        return supported[baseCode] != nil
    }

    @MainActor
    static func engineLabel(forMode mode: Mode) -> String {
        let modelString = (mode.model ?? "").lowercased()
        if modelString == "cloud" || modelString.isEmpty {
            return mode.cloudProvider ?? "cloud"
        }
        if modelString.hasPrefix("parakeet-tdt-") { return "parakeet" }
        if modelString.hasPrefix("nemotron-asr-3.5-") { return "nemotron" }
        if modelString == "apple-speech-analyzer" { return "appleSpeech" }
        if modelString == Qwen3AsrModelManager.Constants.modelId { return "qwen3Asr" }
        return "whisperLocal"
    }

    /// The `model` a response carries: the model that ACTUALLY RAN, not the one
    /// the request named (issue #533).
    ///
    /// The cloud arm used to be a bare `mode.cloudTranscriptionModel ?? ""`. For
    /// HyperWhisper Cloud that field is legitimately unset — the provider picks
    /// by accuracy tier, and `CloudTranscriptionModels.defaultModel(for:
    /// .hyperwhisper)` is literally `""` because there is no client-side model
    /// parameter — so `/transcribe` answered `model: ""` for a run that really
    /// did dispatch Scribe v2 (or whatever the tier resolves to).
    ///
    /// Resolution happens HERE, at the projection, and not only in
    /// `applyEngineModel`, because that function runs only when the request
    /// carries an `engine`. A plain `mode_id` request never reaches it, so a fix
    /// planted there would leave the two request forms disagreeing about the
    /// same Mode — the opposite of the point. Windows records the same reasoning
    /// in `TranscribeEndpoints.ModelLabel` (#528).
    ///
    /// HyperWhisper Cloud goes through
    /// `HyperWhisperCloudProvider.resolvedSTTModelId` — the SAME function the
    /// send path uses to choose the `X-STT-Model` header, reached through the
    /// same `CloudAccuracyTier.fromStorageValue` normalisation. The label is
    /// therefore the dispatched model by construction: a blank, foreign,
    /// live-only, legacy-alias or out-of-tier id heals to the tier's default in
    /// both places at once, and the two cannot drift.
    ///
    /// `internal` so `LocalAPIContractTests` can assert the wire label directly.
    @MainActor
    static func modelLabel(forMode mode: Mode) -> String {
        let modelString = mode.model ?? ""
        guard modelString.lowercased() == "cloud" else { return modelString }

        let provider = CloudProvider.parse(mode.cloudProvider)
        if provider == .hyperwhisper {
            return HyperWhisperCloudProvider.resolvedSTTModelId(
                tier: CloudAccuracyTier.fromStorageValue(mode.cloudAccuracyTier),
                storedModelId: mode.cloudTranscriptionModel
            )
        }
        // Azure MAI is BYOK-NAMED but routed: `TranscriptionProviderRouter`
        // sends `.microsoftAzureSpeech` to `AzureMAIProvider`, which terminates
        // at our own proxy and validates the stored id against its tier before
        // putting it in `X-STT-Model`. Reading the same function it uses keeps
        // the label honest for the one case where a BYOK-looking provider does
        // NOT forward the field verbatim — a stale `whisper-1` in the shared
        // column runs as `mai-transcribe-2` and used to be reported as
        // `whisper-1`. `nil` means "no header, backend default", which is the
        // same "no client-side model id" the empty string reports elsewhere.
        if provider == .microsoftAzureSpeech {
            return AzureMAIProvider.routedModelId(storedModelId: mode.cloudTranscriptionModel) ?? ""
        }
        // Every other BYOK provider sends this field verbatim, so it is already the model
        // that runs — except when it is unset, where the provider applies its
        // own default. An unrecognised `cloudProvider` string (any value can
        // reach the field through a Local API mode write or a backup restore)
        // has no default to offer, so the stored id stands rather than being
        // forced onto some other vendor's model.
        let stored = mode.cloudTranscriptionModel ?? ""
        if !stored.isEmpty { return stored }
        guard let provider else { return "" }
        return CloudTranscriptionModels.defaultModel(for: provider)
    }
}
