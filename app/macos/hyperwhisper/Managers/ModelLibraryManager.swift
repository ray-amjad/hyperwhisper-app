//
//  ModelLibraryManager.swift
//  hyperwhisper
//

import Foundation
import Combine
import SwiftUI
#if canImport(Speech)
import Speech
#endif

@MainActor
final class ModelLibraryManager: ObservableObject {

    @Published private(set) var models: [LibraryModel] = []

    private weak var cloudHealth: CloudProviderHealthManager?
    private weak var apiKeys: APIKeySettingsManager?
    private weak var whisperManager: WhisperModelManager?
    private weak var parakeetManager: ParakeetModelManager?
    private weak var qwen3AsrManager: Qwen3AsrModelManager?
    private weak var nemotronManager: NemotronModelManager?
    private weak var localLLMManager: LocalModelManager?

    private var cancellables: Set<AnyCancellable> = []

    init() {}

    func configure(
        cloudHealth: CloudProviderHealthManager,
        apiKeys: APIKeySettingsManager,
        whisperManager: WhisperModelManager,
        parakeetManager: ParakeetModelManager,
        qwen3AsrManager: Qwen3AsrModelManager,
        nemotronManager: NemotronModelManager?,
        localLLMManager: LocalModelManager
    ) {
        self.cloudHealth = cloudHealth
        self.apiKeys = apiKeys
        self.whisperManager = whisperManager
        self.parakeetManager = parakeetManager
        self.qwen3AsrManager = qwen3AsrManager
        self.nemotronManager = nemotronManager
        self.localLLMManager = localLLMManager

        cancellables.removeAll()

        // One coalesced rebuild stream: every source publishes Void; progress
        // streams are pre-throttled so a single download tick triggers at
        // most one rebuild per 200ms instead of three.
        var immediate: [AnyPublisher<Void, Never>] = [
            cloudHealth.$statuses.map { _ in () }.eraseToAnyPublisher(),
            cloudHealth.$postProcessingStatuses.map { _ in () }.eraseToAnyPublisher(),
            whisperManager.$downloadedModels.map { _ in () }.eraseToAnyPublisher(),
            whisperManager.$downloadingModels.map { _ in () }.eraseToAnyPublisher(),
            parakeetManager.$availableModels.map { _ in () }.eraseToAnyPublisher(),
            qwen3AsrManager.$isDownloaded.map { _ in () }.eraseToAnyPublisher(),
            qwen3AsrManager.downloads.$downloading.map { _ in () }.eraseToAnyPublisher(),
            localLLMManager.$availableModels.map { _ in () }.eraseToAnyPublisher(),
            localLLMManager.$downloadingModels.map { _ in () }.eraseToAnyPublisher(),
        ]
        if #available(macOS 14.0, *), let nemotronManager {
            immediate.append(nemotronManager.$availableModels.map { _ in () }.eraseToAnyPublisher())
            immediate.append(nemotronManager.$downloadingModels.map { _ in () }.eraseToAnyPublisher())
            immediate.append(nemotronManager.$brokenVariants.map { _ in () }.eraseToAnyPublisher())
        }
        var throttledSources: [AnyPublisher<Void, Never>] = [
            whisperManager.$downloadProgress.map { _ in () }.eraseToAnyPublisher(),
            parakeetManager.downloads.$downloading.map { _ in () }.eraseToAnyPublisher(),
            parakeetManager.downloads.$progress.map { _ in () }.eraseToAnyPublisher(),
            qwen3AsrManager.downloads.$progress.map { _ in () }.eraseToAnyPublisher(),
            localLLMManager.$downloadProgress.map { _ in () }.eraseToAnyPublisher(),
        ]
        if #available(macOS 14.0, *), let nemotronManager {
            throttledSources.append(
                nemotronManager.$downloadProgress.map { _ in () }.eraseToAnyPublisher()
            )
        }
        let throttled: [AnyPublisher<Void, Never>] = throttledSources.map {
            $0.throttle(for: .milliseconds(200), scheduler: DispatchQueue.main, latest: true)
                .eraseToAnyPublisher()
        }

        Publishers.MergeMany(immediate + throttled)
            .sink { [weak self] in self?.rebuild() }
            .store(in: &cancellables)

        rebuild()
    }

    private func rebuild() {
        var voiceRows: [LibraryModel] = []
        if let speechRow = appleSpeechRow() { voiceRows.append(speechRow) }
        voiceRows.append(contentsOf: cloudTranscriptionRows())
        voiceRows.append(contentsOf: whisperRows())
        voiceRows.append(contentsOf: parakeetRows())
        voiceRows.append(contentsOf: qwen3AsrRows())
        if #available(macOS 14.0, *) {
            voiceRows.append(contentsOf: nemotronRows())
        }

        // Recommended ordering: rank by (accuracy + speed), tie-break on
        // accuracy then speed. Source numbers come from the empirical
        // benchmark in benchmarks/results/ (avg WER + p50 latency), so this
        // surfaces models that are actually balanced — not the historical
        // catalog order or substring guesses.
        let recommendedSort: (LibraryModel, LibraryModel) -> Bool = { a, b in
            let sa = a.speed + a.accuracy
            let sb = b.speed + b.accuracy
            if sa != sb { return sa > sb }
            if a.accuracy != b.accuracy { return a.accuracy > b.accuracy }
            return a.speed > b.speed
        }
        voiceRows.sort(by: recommendedSort)

        var postRows = postProcessingRows()
        postRows.sort(by: recommendedSort)

        var rows: [LibraryModel] = voiceRows
        rows.append(contentsOf: postRows)
        rows.append(contentsOf: localLLMRows())

        if rows != models {
            models = rows
        }
    }

    // MARK: - Apple Speech

    private func appleSpeechRow() -> LibraryModel? {
        #if canImport(Speech)
        if #available(macOS 26.0, *) {
            let isAvailable = SpeechTranscriber.isAvailable
            let id = "apple-speech-analyzer"
            let lang = localLanguageSupport(modelId: id)
            return LibraryModel(
                id: id,
                displayName: "Apple Speech",
                providerKey: .appleSpeech,
                kind: .voice,
                location: .offline(sizeDescription: "Built-in", installed: true, downloadProgress: nil),
                speed: 5,
                accuracy: 3,
                tag: "Built-in",
                status: isAvailable ? .enabled : .error("Not available on this device"),
                supportsCustomVocabulary: SharedModelsCatalog.supportsCustomVocabulary(provider: "appleSpeech", kind: .voice, id: id),
                availableViaHyperWhisperCloud: SharedModelsCatalog.availableViaHyperWhisperCloud(provider: "appleSpeech", kind: .voice, id: id),
                supportedLanguages: lang.codes,
                supportsAllLanguages: lang.all
            )
        }
        #endif
        return nil
    }

    // MARK: - Language support resolution
    //
    // Cloud rows read base language sets from the shared catalog
    // (models-catalog.json). Local rows reuse the exact Mode Editor resolver
    // (`LanguageSelectionView.allowedLanguageInfos`) so the library filter and
    // the per-mode picker can never disagree; English-only models (Whisper `.en`,
    // Parakeet v2) are pinned to {en} since the resolver hides — rather than
    // restricts — their picker.

    private func cloudLanguageSupport(catalogProvider: String, modelId: String) -> (codes: Set<String>, all: Bool) {
        let support = SharedModelsCatalog.languageSupport(provider: catalogProvider, kind: .voice, id: modelId)
        return (support.codes, support.supportsAll)
    }

    private func localLanguageSupport(modelId: String) -> (codes: Set<String>, all: Bool) {
        if isEnglishOnlyModel(provider: .local, model: modelId) {
            return (["en"], false)
        }
        let infos = LanguageSelectionView.allowedLanguageInfos(
            provider: .local,
            model: modelId,
            cloudProviderId: nil,
            cloudModelId: nil
        )
        return LibraryLanguageFilter.reduce(infos)
    }

    // MARK: - Cloud Transcription

    private func cloudTranscriptionRows() -> [LibraryModel] {
        let healthByProvider: [CloudProvider: ProviderHealth] = cloudHealth?.statuses ?? [:]
        return CloudTranscriptionModels.availableModels.map { model -> LibraryModel in
            let providerKey = SharedModelsCatalog.providerKey(model.provider)
            let status = libraryStatus(forCloud: model.provider, health: healthByProvider[model.provider] ?? .unknown)
            let speed = cloudSpeed(for: model)
            let accuracy = cloudAccuracy(for: model)
            let lang = cloudLanguageSupport(catalogProvider: providerKey, modelId: model.id)
            return LibraryModel(
                id: "cloud-tx-\(model.provider.rawValue)-\(model.id)",
                displayName: model.displayName,
                providerKey: .cloud(model.provider),
                kind: .voice,
                location: .cloud,
                speed: speed,
                accuracy: accuracy,
                tag: nil,
                status: status,
                supportsCustomVocabulary: SharedModelsCatalog.supportsCustomVocabulary(provider: providerKey, kind: .voice, id: model.id),
                availableViaHyperWhisperCloud: SharedModelsCatalog.availableViaHyperWhisperCloud(provider: providerKey, kind: .voice, id: model.id),
                supportedLanguages: lang.codes,
                supportsAllLanguages: lang.all
            )
        }
    }

    private func libraryStatus(forCloud provider: CloudProvider, health: ProviderHealth) -> LibraryModelStatus {
        guard provider.requiresAPIKey else { return .enabled }

        let hasKey = apiKeys?.hasAPIKey(for: provider) ?? false

        switch health {
        case .healthy:
            return .enabled
        case .unauthorized:
            return .error("Key invalid")
        case .unreachable:
            return .error("Provider unreachable")
        case .checking:
            return hasKey ? .enabled : .locked
        case .unknown:
            return hasKey ? .enabled : .locked
        case .notInstalled:
            return .locked
        }
    }

    // MARK: - Empirical ratings (cloud transcription)
    //
    // Speed and accuracy bars are sourced from the benchmark in
    // `benchmarks/` (avg WER vs hand-corrected Scribe v2 reference, and
    // p50 wall-clock latency over a 12-sample corpus spanning <5s up to
    // 84s clips). Buckets:
    //
    //   Speed (p50 latency):  5 <700ms · 4 700-2000ms · 3 2000-3500ms ·
    //                         2 3500-5500ms · 1 >5500ms
    //   Accuracy (avg WER):   5 <5% · 4 5-8% · 3 8-12% ·
    //                         2 12-18% · 1 >18%
    //
    // Update via `benchmarks/propose_ratings.py` after re-running the
    // sweep. Unknown IDs default to (3, 3) — "average, unmeasured".
    private static let cloudRatings: [String: (speed: Int, accuracy: Int)] = [
        // OpenAI
        "gpt-4o-mini-transcribe-2025-12-15": (4, 3),
        "gpt-4o-transcribe":                 (4, 2),
        "gpt-4o-mini-transcribe":            (4, 3),
        "whisper-1":                         (3, 3),
        // Groq
        "whisper-large-v3-turbo":            (5, 4),
        "whisper-large-v3":                  (5, 3),
        // Deepgram
        "nova-3-general":                    (3, 3),
        "nova-3-medical":                    (3, 4),
        "nova-2-general":                    (3, 2),
        "nova-2-medical":                    (3, 2),
        // AssemblyAI
        "universal-2":                       (3, 4),
        "universal-3-pro":                   (2, 5),
        "universal-2-medical":               (2, 4),
        "universal-3-pro-medical":           (2, 5),
        // ElevenLabs
        "scribe_v1":                         (3, 5),
        "scribe_v2":                         (3, 5),
        // Mistral
        "voxtral-mini-latest":               (4, 2),
        // Soniox
        "stt-async-v4":                      (1, 4),
        // Gemini
        "gemini-2.5-flash":                  (2, 4),
        "gemini-2.5-flash-lite":             (3, 3),
        "gemini-2.5-pro":                    (1, 5),
        "gemini-3.1-flash-lite-preview":     (3, 4),
        "gemini-3-flash-preview":            (2, 1),
        "gemini-3.1-pro-preview":            (1, 5),
        // HyperWhisper Cloud routed tiers (Azure MAI / Google Chirp). Chirp 3
        // is a high-accuracy but slow model: inline sync recognize runs ~3.5s
        // and anything over ~55s falls onto a real-time GCS+batchRecognize
        // path, so it earns the lowest speed bar. MAI-Transcribe is comparably
        // accurate but returns in a single multipart round-trip (~5s).
        "mai-transcribe-1.5":                (4, 5),
        "chirp_3":                           (1, 5),
    ]

    private func cloudSpeed(for model: CloudTranscriptionModel) -> Int {
        Self.cloudRatings[model.id]?.speed ?? 3
    }

    private func cloudAccuracy(for model: CloudTranscriptionModel) -> Int {
        Self.cloudRatings[model.id]?.accuracy ?? 3
    }

    // MARK: - Whisper (local)

    private func whisperRows() -> [LibraryModel] {
        guard let manager = whisperManager else { return [] }
        let downloadedNames = Set(manager.downloadedModels.map { $0.name })
        let vocab = SharedModelsCatalog.supportsCustomVocabulary(provider: "localWhisper", kind: .voice, id: "*")
        let cloud = SharedModelsCatalog.availableViaHyperWhisperCloud(provider: "localWhisper", kind: .voice, id: "*")
        return manager.availableModels.map { item in
            let progress = manager.downloadProgress[item.name]
            let isDownloading = manager.downloadingModels.contains(item.name)
            let installed = downloadedNames.contains(item.name)
            let status: LibraryModelStatus = installed
                ? .enabled
                : .downloadable(progress: isDownloading ? (progress ?? 0.01) : nil)
            let lang = localLanguageSupport(modelId: item.name)
            return LibraryModel(
                id: "whisper-\(item.name)",
                displayName: item.displayName.replacingOccurrences(of: " (English)", with: ""),
                providerKey: .localWhisper,
                kind: .voice,
                location: .offline(sizeDescription: item.size, installed: installed, downloadProgress: progress),
                speed: whisperSpeed(forName: item.name),
                accuracy: whisperAccuracy(forName: item.name),
                tag: item.isEnglishOnly ? "EN" : nil,
                status: status,
                supportsCustomVocabulary: vocab,
                availableViaHyperWhisperCloud: cloud,
                supportedLanguages: lang.codes,
                supportsAllLanguages: lang.all
            )
        }
    }

    // MARK: - Empirical ratings (local Whisper)
    // See `cloudRatings` header for methodology + buckets.
    private static let whisperRatings: [String: (speed: Int, accuracy: Int)] = [
        "tiny":           (5, 1),
        "tiny.en":        (5, 1),
        "base":           (5, 1),
        "base.en":        (5, 2),
        "small":          (4, 2),
        "small.en":       (5, 2),
        "medium":         (4, 3),
        "medium.en":      (4, 2),
        "large-v2":       (3, 3),
        "large-v3":       (3, 3),
        "large-v3_turbo": (4, 3),
    ]

    private func whisperSpeed(forName name: String) -> Int {
        Self.whisperRatings[name]?.speed ?? 3
    }

    private func whisperAccuracy(forName name: String) -> Int {
        Self.whisperRatings[name]?.accuracy ?? 3
    }

    // MARK: - Parakeet (local)

    // MARK: - Empirical ratings (local Parakeet)
    private static let parakeetRatings: [String: (speed: Int, accuracy: Int)] = [
        "parakeet-tdt-0.6b-v2": (5, 3),
        "parakeet-tdt-0.6b-v3": (5, 3),
    ]

    private func parakeetRows() -> [LibraryModel] {
        guard let manager = parakeetManager else { return [] }
        let vocab = SharedModelsCatalog.supportsCustomVocabulary(provider: "parakeet", kind: .voice, id: "*")
        let cloud = SharedModelsCatalog.availableViaHyperWhisperCloud(provider: "parakeet", kind: .voice, id: "*")
        return manager.availableModels.map { item in
            let isDownloading = manager.downloads.isDownloading(item.id)
            let progress = manager.downloads.progress[item.id]
            let downloadProgress: Double? = isDownloading ? (progress ?? 0.01) : nil
            let status: LibraryModelStatus = item.isDownloaded
                ? .enabled
                : .downloadable(progress: downloadProgress)
            let rating = Self.parakeetRatings[item.id] ?? (5, 3)
            let lang = localLanguageSupport(modelId: item.id)
            return LibraryModel(
                id: "parakeet-\(item.id)",
                displayName: item.displayName
                    .replacingOccurrences(of: " (English)", with: "")
                    .replacingOccurrences(of: " (Multilingual)", with: ""),
                providerKey: .parakeet,
                kind: .voice,
                location: .offline(sizeDescription: item.size, installed: item.isDownloaded, downloadProgress: downloadProgress),
                speed: rating.speed,
                accuracy: rating.accuracy,
                tag: item.isMultilingual ? "Multilingual" : "EN",
                status: status,
                supportsCustomVocabulary: vocab,
                availableViaHyperWhisperCloud: cloud,
                supportedLanguages: lang.codes,
                supportsAllLanguages: lang.all
            )
        }
    }

    // MARK: - Qwen3 ASR (local)

    private func qwen3AsrRows() -> [LibraryModel] {
        guard let manager = qwen3AsrManager else { return [] }
        guard #available(macOS 15.0, *) else { return [] }

        let progress: Double? = manager.isDownloading ? (manager.downloadProgress ?? 0.01) : nil
        let status: LibraryModelStatus = manager.isDownloaded
            ? .enabled
            : .downloadable(progress: progress)

        let lang = localLanguageSupport(modelId: Qwen3AsrModelManager.Constants.modelId)
        return [
            LibraryModel(
                id: "qwen3-asr-\(Qwen3AsrModelManager.Constants.modelId)",
                displayName: Qwen3AsrModelManager.Constants.displayName,
                providerKey: .qwen3ASR,
                kind: .voice,
                location: .offline(
                    sizeDescription: Qwen3AsrModelManager.Constants.sizeDescription,
                    installed: manager.isDownloaded,
                    downloadProgress: progress
                ),
                speed: 4,
                accuracy: 1,
                tag: nil,
                status: status,
                supportsCustomVocabulary: SharedModelsCatalog.supportsCustomVocabulary(provider: "qwen3ASR", kind: .voice, id: "*"),
                availableViaHyperWhisperCloud: SharedModelsCatalog.availableViaHyperWhisperCloud(provider: "qwen3ASR", kind: .voice, id: "*"),
                supportedLanguages: lang.codes,
                supportsAllLanguages: lang.all
            )
        ]
    }

    // MARK: - Nemotron (local)

    // Speed/accuracy ratings for the two variants. Source: FluidAudio docs
    // ("~76 RTFx multilingual, ~124 RTFx latin" on M5 Pro; WER 3.2% / 3.6% on FLEURS-7).
    // Both beat Parakeet on coverage; the multilingual variant edges out V3 on WER.
    private static let nemotronRatings: [String: (speed: Int, accuracy: Int)] = [
        "nemotron-asr-3.5-latin":        (5, 4),
        "nemotron-asr-3.5-multilingual": (5, 4),
    ]

    @available(macOS 14.0, *)
    private func nemotronRows() -> [LibraryModel] {
        guard let manager = nemotronManager else { return [] }
        let vocab = SharedModelsCatalog.supportsCustomVocabulary(provider: "nemotron", kind: .voice, id: "*")
        let cloud = SharedModelsCatalog.availableViaHyperWhisperCloud(provider: "nemotron", kind: .voice, id: "*")
        return manager.availableModels.map { item in
            let isDownloading = manager.downloadingModels.contains(item.id)
            let progress = manager.downloadProgress[item.id]
            // Broken variants live in `brokenVariants` after a load failed on
            // an install the metadata-only probe accepted; surface a
            // "Re-download" affordance instead of green-checking an unusable model.
            let isBroken = manager.isVariantBroken(item.id)
            let isUsable = item.isDownloaded && !isBroken
            let status: LibraryModelStatus = isUsable
                ? .enabled
                : .downloadable(progress: isDownloading ? (progress ?? 0.01) : nil)
            let rating = Self.nemotronRatings[item.id] ?? (4, 4)
            // Tag carries the variant scope (Latin vs Multilingual) so users can see at
            // a glance what each card buys them.
            let tag = item.variant == .latin ? "Latin" : "Multilingual"
            let lang = localLanguageSupport(modelId: item.id)
            return LibraryModel(
                id: "nemotron-\(item.id)",
                displayName: item.displayName,
                providerKey: .nemotron,
                kind: .voice,
                location: .offline(sizeDescription: item.size, installed: item.isDownloaded, downloadProgress: isDownloading ? (progress ?? 0.01) : nil),
                speed: rating.speed,
                accuracy: rating.accuracy,
                tag: tag,
                status: status,
                supportsCustomVocabulary: vocab,
                availableViaHyperWhisperCloud: cloud,
                supportedLanguages: lang.codes,
                supportsAllLanguages: lang.all
            )
        }
    }

    // MARK: - Post-processing models

    private func postProcessingRows() -> [LibraryModel] {
        let statusesByProvider: [PostProcessingProvider: ProviderHealth] = cloudHealth?.postProcessingStatuses ?? [:]
        return PostProcessingModels.availableModels.compactMap { model -> LibraryModel? in
            if model.provider == .localLLM { return nil }
            let providerKey = SharedModelsCatalog.providerKey(model.provider)
            let providerStatus = statusesByProvider[model.provider] ?? .unknown
            let status = libraryStatus(forPost: model.provider, health: providerStatus)
            return LibraryModel(
                id: "pp-\(model.provider.rawValue)-\(model.id)",
                displayName: model.displayName,
                providerKey: .postProcessing(model.provider),
                kind: .text,
                location: .cloud,
                speed: postSpeed(for: model.id),
                accuracy: postAccuracy(for: model.id),
                tag: nil,
                status: status,
                supportsCustomVocabulary: SharedModelsCatalog.supportsCustomVocabulary(provider: providerKey, kind: .text, id: model.id),
                availableViaHyperWhisperCloud: SharedModelsCatalog.availableViaHyperWhisperCloud(provider: providerKey, kind: .text, id: model.id)
            )
        }
    }

    private func libraryStatus(forPost provider: PostProcessingProvider, health: ProviderHealth) -> LibraryModelStatus {
        guard provider.requiresAPIKey else { return .enabled }
        let hasKey = apiKeys?.hasPostProcessingAPIKey(for: provider) ?? false

        switch health {
        case .healthy:
            return .enabled
        case .unauthorized:
            return .error("Key invalid")
        case .unreachable:
            return .error("Provider unreachable")
        case .checking, .unknown:
            return hasKey ? .enabled : .locked
        case .notInstalled:
            return .locked
        }
    }

    // MARK: - Empirical ratings (post-processing)
    //
    // Speed and accuracy bars come from the post-processing benchmark in
    // `benchmarks/` — driving /post-process with the `hyper` preset over
    // the same input corpus used for transcription scoring, scoring each
    // model's output by WER against Claude's reference application of
    // hyper (pp_references.json). Buckets:
    //
    //   Speed (p50 latency):  5 <700ms · 4 700-2000ms · 3 2000-3500ms ·
    //                         2 3500-5500ms · 1 >5500ms
    //   Accuracy (WER vs hyper-ref):
    //                         5 <8% · 4 8-15% · 3 15-25% ·
    //                         2 25-40% · 1 >40%
    //
    // Update via `benchmarks/propose_pp_ratings.py` after re-running the
    // sweep. Unknown model IDs default to (3, 3) — "average, unmeasured".
    private static let postProcessingRatings: [String: (speed: Int, accuracy: Int)] = [
        "gpt-4.1":                                       (4, 5),
        "gpt-5.1":                                       (4, 5),
        "gpt-4.1-mini":                                  (4, 5),
        "gpt-5.2":                                       (4, 5),
        "gpt-5.4":                                       (3, 5),
        "gpt-5.4-nano":                                  (4, 4),
        "gpt-5.4-mini":                                  (4, 4),
        "gpt-5-mini":                                    (1, 4),
        "gpt-5":                                         (1, 4),
        "gpt-5-nano":                                    (1, 4),
        "gpt-4.1-nano":                                  (4, 4),
        "claude-sonnet-4-6":                             (4, 5),
        "claude-sonnet-4-5":                             (3, 5),
        "claude-sonnet-4-0":                             (3, 5),
        "claude-haiku-4-5":                              (4, 4),
        "gemini-2.5-flash":                              (2, 5),
        "gemini-3.5-flash":                              (2, 5),
        "gemini-2.5-flash-lite":                         (4, 5),
        "gemini-2.5-pro":                                (2, 4),
        "gemini-3-flash-preview":                        (1, 4),
        "gemini-3-pro-preview":                          (2, 3),
        "gemini-3.1-flash-lite-preview":                 (4, 3),
        "openai/gpt-oss-120b":                           (4, 4),
        "openai/gpt-oss-20b":                            (4, 4),
        "meta-llama/llama-4-maverick-17b-128e-instruct": (2, 3),
        "moonshotai/kimi-k2-instruct":                   (2, 3),
        "grok-4.3":                                      (2, 5),
        "mistral-small-latest":                          (2, 3),
        "open-mistral-nemo":                             (2, 2),
        "zai-glm-4.7":                                   (4, 5),
        "gpt-oss-120b":                                  (5, 3),
        "llama3.1-8b":                                   (2, 3),
        "qwen-3-235b-a22b-instruct-2507":                (2, 3),
        "gemma-4-E2B-it-Q4_K_M.gguf":                    (5, 1),
        "gemma-4-E4B-it-Q4_K_M.gguf":                    (4, 2),
        "gemma-4-12b-it-Q4_K_M.gguf":                    (3, 3),
        "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf":             (2, 4),
        "gemma-4-31B-it-Q4_K_M.gguf":                    (1, 5),
    ]

    private func postSpeed(for id: String) -> Int {
        Self.postProcessingRatings[id]?.speed ?? 3
    }

    private func postAccuracy(for id: String) -> Int {
        Self.postProcessingRatings[id]?.accuracy ?? 3
    }

    // MARK: - Local LLM rows

    private func localLLMRows() -> [LibraryModel] {
        guard let manager = localLLMManager else { return [] }
        let vocab = SharedModelsCatalog.supportsCustomVocabulary(provider: "localLLM", kind: .text, id: "*")
        let cloud = SharedModelsCatalog.availableViaHyperWhisperCloud(provider: "localLLM", kind: .text, id: "*")
        // Capability gate: on Intel the local runtime can never run, and under
        // Rosetta the arm64 runtime can't load — so don't offer downloads of models
        // that can't run here. Surface the reason on the row instead.
        let capability = SystemCapability.current
        return manager.availableModels.map { item in
            let progress = manager.downloadProgress[item.id]
            let isDownloading = manager.downloadingModels.contains(item.id)
            let status: LibraryModelStatus
            switch capability {
            case .unsupported:
                // Already-downloaded weights still show a "remove to reclaim disk"
                // action (driven by location.installed); they just can't be enabled.
                status = .error("modelLibrary.local.requiresAppleSilicon".localized)
            case .needsNativeRelaunch:
                status = item.isDownloaded
                    ? .enabled
                    : .error("modelLibrary.local.needsNativeRelaunch".localized)
            case .supported:
                status = item.isDownloaded
                    ? .enabled
                    : .downloadable(progress: isDownloading ? (progress ?? 0.01) : nil)
            }
            return LibraryModel(
                id: "local-llm-\(item.id)",
                displayName: item.displayName.replacingOccurrences(of: " (Recommended)", with: ""),
                providerKey: .postProcessing(.localLLM),
                kind: .text,
                location: .offline(sizeDescription: item.sizeDescription, installed: item.isDownloaded, downloadProgress: progress),
                speed: 3,
                accuracy: item.isRecommended ? 4 : 3,
                tag: item.isRecommended ? "Recommended" : nil,
                status: status,
                supportsCustomVocabulary: vocab,
                availableViaHyperWhisperCloud: cloud
            )
        }
    }
}
