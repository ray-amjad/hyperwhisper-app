//
//  LibraryModel.swift
//  hyperwhisper
//

import Foundation

enum LibraryProviderKey: Hashable {
    case cloud(CloudProvider)
    case postProcessing(PostProcessingProvider)
    case appleSpeech
    case localWhisper
    case parakeet
    case qwen3ASR
    case nemotron

    var displayName: String {
        switch self {
        case .cloud(let provider):
            return provider.displayName
        case .postProcessing(let provider):
            return provider.displayName
        case .appleSpeech:
            return "Apple Speech"
        case .localWhisper:
            return "Whisper"
        case .parakeet:
            return "NVIDIA"
        case .qwen3ASR:
            return "Qwen3 ASR"
        case .nemotron:
            return "NVIDIA"
        }
    }

    /// `nil` falls back to `fallbackSymbol`. Hyperwhisper has no brand
    /// mark — both its cloud and post-processing rows use the symbol.
    var brandAssetName: String? {
        switch self {
        case .cloud(let provider):
            switch provider {
            case .hyperwhisper: return nil
            case .openai:       return "providerOpenAI"
            case .groq:         return "providerGroq"
            case .deepgram:     return "providerDeepgram"
            case .elevenLabs:   return "providerElevenLabs"
            case .mistral:      return "providerMistral"
            case .gemini:       return "providerGemini"
            case .assemblyAI:   return "providerAssemblyAI"
            case .soniox:       return "providerSoniox"
            case .grok:         return "providerGrok"
            case .microsoftAzureSpeech: return "providerMicrosoft"
            case .googleSpeech:        return "providerGoogle"
            }
        case .postProcessing(let provider):
            switch provider {
            case .hyperwhisper: return nil
            case .openai:       return "providerOpenAI"
            case .anthropic:    return "providerAnthropic"
            case .groq:         return "providerGroq"
            case .gemini:       return "providerGemini"
            case .grok:         return "providerGrok"
            case .cerebras:     return "providerCerebras"
            case .mistral:      return "providerMistral"
            case .localLLM:     return "providerLocalLLM"
            }
        case .appleSpeech:   return "providerApple"
        case .localWhisper:  return "providerLocalWhisper"
        case .parakeet:      return "providerNvidia"
        case .qwen3ASR:      return nil
        case .nemotron:      return "providerNvidia"
        }
    }

    var brandAssetIsMulticolor: Bool {
        switch self {
        case .cloud(.gemini), .postProcessing(.gemini): return true
        default: return false
        }
    }

    var fallbackSymbol: String {
        switch self {
        case .cloud(let provider):
            switch provider {
            case .hyperwhisper: return "sparkles"
            case .openai: return "circle.hexagongrid.fill"
            case .groq: return "bolt.fill"
            case .deepgram: return "waveform"
            case .assemblyAI: return "text.bubble.fill"
            case .elevenLabs: return "11.circle.fill"
            case .mistral: return "wind"
            case .soniox: return "antenna.radiowaves.left.and.right"
            case .gemini: return "sparkle"
            case .grok: return "x.circle.fill"
            case .microsoftAzureSpeech: return "m.square.fill"
            case .googleSpeech: return "globe"
            }
        case .postProcessing(let provider):
            switch provider {
            case .hyperwhisper: return "sparkles"
            case .openai: return "circle.hexagongrid.fill"
            case .anthropic: return "a.circle.fill"
            case .gemini: return "sparkle"
            case .groq: return "bolt.fill"
            case .grok: return "x.circle.fill"
            case .cerebras: return "cpu.fill"
            case .mistral: return "wind"
            case .localLLM: return "internaldrive.fill"
            }
        case .appleSpeech:
            return "applelogo"
        case .localWhisper:
            return "waveform.path"
        case .parakeet:
            return "bird.fill"
        case .qwen3ASR:
            return "waveform.badge.magnifyingglass"
        case .nemotron:
            return "sparkles.rectangle.stack.fill"
        }
    }
}

enum LibraryModelKind: String, Hashable {
    case voice
    case text
}

/// Canonical option set + reduction logic for the Model Library language filter.
///
/// The dropdown is one entry per *base* language (region/script collapsed) drawn
/// from the Whisper universal set, so its codes line up exactly with the
/// `supportsAllLanguages` reference used in the shared catalog and the macOS
/// resolver. Matching is always base-to-base: picking "Spanish" (`es`) keeps any
/// model whose set contains `es` (which already subsumes `es-419` etc. after
/// normalization).
enum LibraryLanguageFilter {
    /// Sentinel for "Any language" (no filtering). Empty string so it round-trips
    /// cleanly through `@AppStorage`.
    static let anyCode = ""

    /// One entry per base language, popular-first.
    static let languages: [LanguageData.LanguageInfo] = {
        var seen = Set<String>()
        var out: [LanguageData.LanguageInfo] = []
        for code in LanguageData.whisperUniversalCodes {
            let base = LanguageData.normalizeLanguageCode(code)
            if base == LanguageData.automaticCode { continue }
            if seen.contains(base) { continue }
            seen.insert(base)
            out.append(
                LanguageData.info(for: base)
                    ?? LanguageData.LanguageInfo(code: base, displayName: LanguageData.displayName(for: base))
            )
        }
        return out
    }()

    /// The base codes that count as "covers every language".
    static let allCodes: Set<String> = Set(languages.map { $0.code })

    /// Reduce a resolver's language infos to a base-code set + an "all languages"
    /// flag. A set that covers every filter option collapses to `(all: true)` so
    /// the model passes every filter without storing ~100 codes.
    static func reduce(_ infos: [LanguageData.LanguageInfo]) -> (codes: Set<String>, all: Bool) {
        var base = Set(infos.map { LanguageData.normalizeLanguageCode($0.code) })
        base.remove(LanguageData.automaticCode)
        if allCodes.isSubset(of: base) { return ([], true) }
        return (base, false)
    }
}

enum LibraryModelLocation: Hashable {
    case cloud
    case offline(sizeDescription: String?, installed: Bool, downloadProgress: Double?)
}

enum LibraryModelStatus: Hashable {
    case enabled
    case locked
    case error(String)
    case downloadable(progress: Double?)
}

struct LibraryModel: Identifiable, Hashable {
    let id: String
    let displayName: String
    let providerKey: LibraryProviderKey
    let kind: LibraryModelKind
    let location: LibraryModelLocation
    /// 1...5
    let speed: Int
    /// 1...5
    let accuracy: Int
    let tag: String?
    let status: LibraryModelStatus
    /// Sourced from shared-models/models-catalog.json. See SharedModelsCatalog.swift.
    let supportsCustomVocabulary: Bool
    /// Sourced from shared-models/models-catalog.json.
    let availableViaHyperWhisperCloud: Bool
    /// Base ISO language codes (region/script stripped, e.g. "en", "es", "zh")
    /// this voice model can transcribe, for the Model Library language filter.
    /// Empty when `supportsAllLanguages` is true and for text models. Cloud
    /// values come from the shared catalog; local values are resolved in-code in
    /// `ModelLibraryManager`. Defaulted so non-voice rows need not set it.
    var supportedLanguages: Set<String> = []
    /// When true the model passes every language filter — text models,
    /// Whisper-family, Gemini, Google Chirp, etc.
    var supportsAllLanguages: Bool = true

    /// Whether this model should remain visible when the library is filtered to
    /// `baseCode` (already region-stripped). Voice-only concept; callers gate on
    /// `kind == .voice` before applying it.
    func supportsLanguage(_ baseCode: String) -> Bool {
        supportsAllLanguages || supportedLanguages.contains(baseCode)
    }

    var allowsOfflineRemoval: Bool {
        providerKey != .appleSpeech
    }

    /// The model id without the LibraryModel-style provider prefix. Each row
    /// constructs `id` as `"<providerPrefix>-<canonical>"` so the prefix has to
    /// come off cleanly before talking to the manager that owns the underlying
    /// model. Stripping with `replacingOccurrences(of:)` would mishandle ids
    /// that re-include the prefix (e.g. `nemotron-asr-3.5-latin`), so this is
    /// anchored at the start.
    var canonicalModelId: String {
        let prefix: String?
        switch providerKey {
        case .localWhisper: prefix = "whisper-"
        case .parakeet:     prefix = "parakeet-"
        case .nemotron:     prefix = "nemotron-"
        case .qwen3ASR:     prefix = nil
        case .appleSpeech:  prefix = nil
        case .postProcessing(.localLLM): prefix = "local-llm-"
        default:            prefix = nil
        }
        guard let prefix, id.hasPrefix(prefix) else { return id }
        return String(id.dropFirst(prefix.count))
    }
}

// MARK: - Shared API key pairing
//
// OpenAI, Gemini, Groq and Grok each expose both a transcription API and a
// chat-completions API. They share one key per provider, so the API-keys flow
// writes through both stores at once.

extension CloudProvider {
    var pairedPostProcessing: PostProcessingProvider? {
        switch self {
        case .openai: return .openai
        case .gemini: return .gemini
        case .groq: return .groq
        case .grok: return .grok
        case .mistral: return .mistral
        default: return nil
        }
    }

    var apiKeyPlaceholder: String {
        switch self {
        case .openai:     return "sk-..."
        case .groq:       return "gsk_..."
        case .deepgram:   return "Token ..."
        case .gemini:     return "AIza..."
        case .grok:       return "xai-..."
        case .assemblyAI, .elevenLabs, .mistral, .soniox, .hyperwhisper, .microsoftAzureSpeech, .googleSpeech:
            return "Paste API key"
        }
    }
}

extension PostProcessingProvider {
    var pairedCloud: CloudProvider? {
        switch self {
        case .openai: return .openai
        case .gemini: return .gemini
        case .groq: return .groq
        case .grok: return .grok
        case .mistral: return .mistral
        default: return nil
        }
    }

    var apiKeyPlaceholder: String {
        switch self {
        case .openai:     return "sk-..."
        case .anthropic:  return "sk-ant-..."
        case .groq:       return "gsk_..."
        case .gemini:     return "AIza..."
        case .grok:       return "xai-..."
        case .cerebras:   return "csk-..."
        case .mistral:    return "Paste API key"
        case .hyperwhisper, .localLLM:
            return "Paste API key"
        }
    }
}
