import Foundation

/// Cloud Provider enum for extensible provider support
/// Supports HyperWhisper Cloud (built-in), OpenAI, Groq, Deepgram, AssemblyAI, and ElevenLabs for cloud transcription
enum CloudProvider: String, CaseIterable, Identifiable {
    case hyperwhisper = "hyperwhisper"  // FIRST: Built-in default provider
    case openai = "openai"
    case groq = "groq"
    case deepgram = "deepgram"
    case assemblyAI = "assemblyai"
    case elevenLabs = "elevenlabs"
    case mistral = "mistral"
    case soniox = "soniox"
    case gemini = "gemini"
    case grok = "grok"
    // Raw values are all-lowercase so they round-trip through
    // `engine.lowercased()` in the Local API and the in-app router. The
    // camelCase identifiers stay on the Swift case name for ergonomics.
    case microsoftAzureSpeech = "microsoftazurespeech"
    case googleSpeech = "googlespeech"

    var id: String { rawValue }

    /// Display name for the provider
    var displayName: String {
        switch self {
        case .hyperwhisper:
            return "HyperWhisper Cloud"
        case .openai:
            return "OpenAI"
        case .groq:
            return "Groq"
        case .deepgram:
            return "Deepgram"
        case .assemblyAI:
            return "AssemblyAI"
        case .elevenLabs:
            return "ElevenLabs"
        case .mistral:
            return "Mistral"
        case .soniox:
            return "Soniox"
        case .gemini:
            return "Google Gemini"
        case .grok:
            return "Grok"
        case .microsoftAzureSpeech:
            return "Microsoft Azure Speech"
        case .googleSpeech:
            return "Google Cloud Speech"
        }
    }

    /// Description for tooltips
    var description: String {
        switch self {
        case .hyperwhisper:
            return "Built-in cloud transcription with credit-based usage. No API key needed."
        case .openai:
            return "OpenAI's Whisper API for cloud-based transcription"
        case .groq:
            return "Groq Cloud's fast Whisper API with low latency"
        case .deepgram:
            return "Deepgram Nova family models with domain-specific tuning"
        case .assemblyAI:
            return "AssemblyAI's Universal-2 and Universal-3 Pro speech-to-text models"
        case .elevenLabs:
            return "ElevenLabs Scribe transcription with rich timestamps and diarization"
        case .mistral:
            return "Mistral's Voxtral speech-to-text with state-of-the-art accuracy"
        case .soniox:
            return "Soniox async speech-to-text with multilingual batch transcription"
        case .gemini:
            return "Google Gemini multimodal transcription with custom prompting support"
        case .grok:
            return "xAI Grok speech-to-text API"
        case .microsoftAzureSpeech:
            return "Microsoft MAI-Transcribe 1.5 via Azure Speech (43 languages with contextual biasing)"
        case .googleSpeech:
            return "Google Cloud Speech-to-Text V2 with Chirp 3 (multilingual + phrase adaptation)"
        }
    }

    /// API endpoint for transcription
    var transcriptionEndpoint: String {
        switch self {
        case .hyperwhisper:
            return NetworkConfig.hyperwhisperCloudURL
        case .openai:
            return "https://api.openai.com/v1/audio/transcriptions"
        case .groq:
            return "https://api.groq.com/openai/v1/audio/transcriptions"
        case .deepgram:
            return "https://api.deepgram.com/v1/listen"
        case .assemblyAI:
            return "https://api.assemblyai.com/v2/transcript"
        case .elevenLabs:
            return "https://api.elevenlabs.io/v1/speech-to-text"
        case .mistral:
            return "https://api.mistral.ai/v1/audio/transcriptions"
        case .soniox:
            return "https://api.soniox.com/v1/transcriptions"
        case .gemini:
            return "https://generativelanguage.googleapis.com/v1beta/models"
        case .grok:
            return "https://api.x.ai/v1/stt"
        case .microsoftAzureSpeech, .googleSpeech:
            // Routed through HyperWhisper Cloud — the upstream URL is not
            // user-facing. Returning the Fly endpoint keeps callers that
            // inspect this string consistent across HW-Cloud-only providers.
            return NetworkConfig.hyperwhisperCloudURL
        }
    }

    /// API key URL for getting keys
    var apiKeyURL: String {
        switch self {
        case .hyperwhisper:
            return "https://www.hyperwhisper.com"
        case .openai:
            return "https://platform.openai.com/api-keys"
        case .groq:
            return "https://console.groq.com/keys"
        case .deepgram:
            return "https://deepgram.com"
        case .assemblyAI:
            return "https://www.assemblyai.com"
        case .elevenLabs:
            return "https://elevenlabs.io/app/settings/api-keys"
        case .mistral:
            return "https://console.mistral.ai/api-keys"
        case .soniox:
            return "https://console.soniox.com"
        case .gemini:
            return "https://aistudio.google.com/apikey"
        case .grok:
            return "https://console.x.ai/"
        case .microsoftAzureSpeech, .googleSpeech:
            // HyperWhisper Cloud only — no BYOK in v1.
            return "https://www.hyperwhisper.com"
        }
    }

    /// Whether this provider requires an API key
    var requiresAPIKey: Bool {
        switch self {
        case .hyperwhisper, .microsoftAzureSpeech, .googleSpeech:
            return false  // Credit-based via HyperWhisper Cloud, no API key needed
        default:
            return true
        }
    }

    /// True when the provider's `/transcribe` traffic terminates at the
    /// HyperWhisper Cloud Fly backend (transcribe.hyperwhisper.com), regardless
    /// of which upstream STT engine the backend dispatches to. Used by the
    /// router to decide whether a connection prewarm is worthwhile and by the
    /// shared-URLSession plumbing so all three providers coalesce HTTP/2
    /// connections to the same host.
    var routesViaHyperWhisperCloud: Bool {
        switch self {
        case .hyperwhisper, .microsoftAzureSpeech, .googleSpeech:
            return true
        default:
            return false
        }
    }

    /// Maximum file size in bytes supported by this provider
    /// Used for validating imported audio files before transcription
    /// Local providers (LibWhisper, Parakeet) have no file size limit - only cloud providers are constrained
    var maxFileSizeBytes: Int64 {
        switch self {
        case .hyperwhisper:
            return 2 * 1024 * 1024 * 1024  // 2 GB (uses Deepgram backend)
        case .openai:
            return 25 * 1024 * 1024  // 25 MB
        case .groq:
            return 25 * 1024 * 1024  // 25 MB (free tier limit, dev tier is 100 MB)
        case .deepgram:
            return 2 * 1024 * 1024 * 1024  // 2 GB
        case .assemblyAI:
            return 5 * 1024 * 1024 * 1024  // 5 GB (matches AssemblyAI documented limit)
        case .elevenLabs:
            return 3 * 1024 * 1024 * 1024  // 3 GB
        case .mistral:
            return 100 * 1024 * 1024  // 100 MB (conservative estimate, no official limit)
        case .soniox:
            return 1 * 1024 * 1024 * 1024  // 1 GB (Soniox Files API upload limit)
        case .gemini:
            return 2 * 1024 * 1024 * 1024  // 2 GB (Files API upload limit)
        case .grok:
            return 500 * 1024 * 1024  // 500 MB per docs
        case .microsoftAzureSpeech:
            return 300 * 1024 * 1024  // 300 MB — Azure Foundry inline upload limit
        case .googleSpeech:
            // Inline V2 recognize caps near 10 MB (~1 min). GCS upload path is
            // out of scope for v1; cap matches the backend's 9.5 MB guard.
            return 9_500_000
        }
    }

    /// Human-readable file size limit string for error messages
    var maxFileSizeDisplay: String {
        let bytes = maxFileSizeBytes
        if bytes >= 1024 * 1024 * 1024 {
            let gb = Double(bytes) / (1024.0 * 1024.0 * 1024.0)
            return String(format: "%.1f GB", gb)
        } else {
            let mb = bytes / (1024 * 1024)
            return "\(mb) MB"
        }
    }

    /// Audio file extensions supported by this provider for file transcription
    ///
    /// **Purpose:**
    /// Used to validate imported audio files before transcription. Each provider has different
    /// format support - this prevents unhelpful API errors by catching unsupported formats early.
    ///
    /// **Format Coverage:**
    /// - OpenAI/Groq: Limited to mp3, mp4, mpeg, mpga, m4a, wav, webm
    /// - Deepgram/HyperWhisper Cloud: Broad support including flac, ogg, aac, opus, wma, amr
    /// - AssemblyAI/ElevenLabs/Mistral: Common formats with varying flac/ogg support
    ///
    /// **Note:**
    /// Extensions are lowercase and without the leading dot (e.g., "mp3" not ".mp3")
    var supportedAudioExtensions: Set<String> {
        switch self {
        case .openai, .groq:
            // OpenAI Whisper API and compatible providers
            // Ref: https://platform.openai.com/docs/api-reference/audio/createTranscription
            return ["mp3", "mp4", "mpeg", "mpga", "m4a", "wav", "webm"]

        case .hyperwhisper, .deepgram:
            // Deepgram supports most common audio formats
            // Ref: https://developers.deepgram.com/docs/supported-audio-formats
            return ["mp3", "mp4", "m4a", "wav", "flac", "ogg", "webm", "aac", "opus", "wma", "amr"]

        case .assemblyAI:
            // AssemblyAI accepts most common formats
            // Ref: https://www.assemblyai.com/docs/speech-to-text/pre-recorded
            return ["mp3", "mp4", "m4a", "wav", "flac", "ogg", "webm"]

        case .elevenLabs:
            // ElevenLabs Scribe supported formats
            // Ref: https://elevenlabs.io/docs/speech-to-text/scribe
            return ["mp3", "mp4", "m4a", "wav", "webm", "ogg", "flac"]

        case .mistral:
            // Mistral Voxtral supported formats
            // Ref: https://docs.mistral.ai/capabilities/audio/
            return ["mp3", "mp4", "m4a", "wav", "webm", "flac", "ogg"]

        case .soniox:
            // Soniox async transcription supported formats
            // Ref: https://soniox.com/docs/stt/async/async-transcription
            return ["aac", "aiff", "amr", "asf", "flac", "mp3", "ogg", "wav", "webm", "m4a", "mp4"]

        case .gemini:
            // Gemini multimodal audio support
            // Ref: https://ai.google.dev/gemini-api/docs/audio
            return ["mp3", "mp4", "m4a", "wav", "webm", "flac", "ogg", "aac", "opus"]

        case .grok:
            // xAI Grok STT supported containers (auto-detected)
            // Ref: https://docs.x.ai/docs/api-reference#speech-to-text
            return ["wav", "mp3", "ogg", "opus", "flac", "aac", "mp4", "m4a", "mkv"]

        case .microsoftAzureSpeech:
            // Azure Speech Foundry transcribe endpoint — common containers
            return ["wav", "mp3", "mp4", "m4a", "ogg", "flac", "webm"]

        case .googleSpeech:
            // Google Speech V2 autoDecodingConfig handles common containers
            return ["wav", "mp3", "mp4", "m4a", "ogg", "flac", "webm"]
        }
    }
}

/// Cloud Transcription Models Configuration
/// This file defines all available cloud transcription models from various providers for speech-to-text
/// Each model has an ID (used by API) and a display name (shown in UI)
struct CloudTranscriptionModel {
    /// The model identifier used by the API
    let id: String
    
    /// The user-friendly display name shown in the UI
    let displayName: String
    
    /// Whether this model is available for general use
    let isAvailable: Bool
    
    /// Model description for tooltips
    let description: String
    
    /// Which provider this model belongs to
    let provider: CloudProvider

    /// Whether this model should appear in the shortened default picker
    let isPopular: Bool

    /// Billing price per second in USD (nil if unknown)
    let pricePerSecond: Double?

    init(
        id: String,
        displayName: String,
        isAvailable: Bool,
        description: String,
        provider: CloudProvider,
        isPopular: Bool = false,
        pricePerSecond: Double?
    ) {
        self.id = id
        self.displayName = displayName
        self.isAvailable = isAvailable
        self.description = description
        self.provider = provider
        self.isPopular = isPopular
        self.pricePerSecond = pricePerSecond
    }
}

extension CloudTranscriptionModel {
    /// Convenience computed price per minute
    var pricePerMinute: Double? { pricePerSecond.map { $0 * 60.0 } }
}

/// Central registry of all available cloud transcription models
struct CloudTranscriptionModels {
    /// Default model to use when creating new modes with cloud transcription
    static let defaultModelId = "whisper-1"
    
    /// All available cloud transcription models for speech-to-text
    /// Models are ordered by provider and capability
    static let availableModels: [CloudTranscriptionModel] = [
        // OpenAI Models
        CloudTranscriptionModel(
            id: "gpt-4o-mini-transcribe-2025-12-15",
            displayName: "GPT-4o Mini Transcribe (2025-12-15)",
            isAvailable: true,
            description: "Latest dated snapshot of GPT-4o Mini Transcribe. Lowest word error rate among OpenAI transcription models.",
            provider: .openai,
            pricePerSecond: 0.003 / 60.0
        ),
        CloudTranscriptionModel(
            id: "gpt-4o-transcribe",
            displayName: "GPT-4o Transcribe",
            isAvailable: true,
            description: "Advanced speech-to-text powered by GPT-4o. Higher accuracy with better context understanding.",
            provider: .openai,
            isPopular: true,
            pricePerSecond: 0.006 / 60.0
        ),
        CloudTranscriptionModel(
            id: "gpt-4o-mini-transcribe",
            displayName: "GPT-4o Mini Transcribe",
            isAvailable: true,
            description: "Fast speech-to-text powered by GPT-4o Mini. Good balance of speed and accuracy.",
            provider: .openai,
            isPopular: true,
            pricePerSecond: 0.003 / 60.0
        ),
        CloudTranscriptionModel(
            id: "whisper-1",
            displayName: "Whisper-1",
            isAvailable: true,
            description: "General-purpose speech recognition model. Reliable and proven for all recording lengths.",
            provider: .openai,
            isPopular: true,
            pricePerSecond: 0.006 / 60.0
        ),
        
        // Groq Models
        CloudTranscriptionModel(
            id: "whisper-large-v3-turbo",
            displayName: "Whisper Large v3 Turbo",
            isAvailable: true,
            description: "Groq's ultra-fast Whisper implementation. Optimized for speed with high accuracy. Excellent for real-time transcription.",
            provider: .groq,
            isPopular: true,
            // $0.04 per hour
            pricePerSecond: 0.04 / 3600.0
        ),
        CloudTranscriptionModel(
            id: "whisper-large-v3",
            displayName: "Whisper Large v3",
            isAvailable: true,
            description: "Groq's standard Whisper v3 model. High accuracy with good performance.",
            provider: .groq,
            // $0.111 per hour
            pricePerSecond: 0.111 / 3600.0
        ),
        
        // Deepgram
        CloudTranscriptionModel(
            id: "nova-3-general",
            displayName: "Nova 3 General",
            isAvailable: true,
            description: "The leading model for general transcription from Deepgram.",
            provider: .deepgram,
            isPopular: true,
            pricePerSecond: 0.0043 / 60.0
        ),
        CloudTranscriptionModel(
            id: "nova-3-medical",
            displayName: "Nova 3 Medical",
            isAvailable: true,
            description: "Optimized audio with medical oriented vocabulary.",
            provider: .deepgram,
            pricePerSecond: 0.0043 / 60.0
        ),
        CloudTranscriptionModel(
            id: "nova-2-general",
            displayName: "Nova 2 General",
            isAvailable: true,
            description: "General-purpose transcription with high accuracy for diverse audio sources.",
            provider: .deepgram,
            pricePerSecond: 0.0043 / 60.0
        ),
        CloudTranscriptionModel(
            id: "nova-2-medical",
            displayName: "Nova-2 Medical",
            isAvailable: true,
            description: "Medical domain vocabulary for clinical conversations and healthcare settings.",
            provider: .deepgram,
            isPopular: true,
            pricePerSecond: 0.0043 / 60.0
        ),

        // AssemblyAI Models
        CloudTranscriptionModel(
            id: "universal-2",
            displayName: "Universal-2",
            isAvailable: true,
            description: "Multi-language model supporting 99 languages with automatic detection. Supports keyterms prompting (up to 200 terms).",
            provider: .assemblyAI,
            isPopular: true,
            pricePerSecond: 0.15 / 60.0 / 60.0 // $0.15 per hour
        ),
        CloudTranscriptionModel(
            id: "universal-3-pro",
            displayName: "Universal-3 Pro",
            isAvailable: true,
            description: "AssemblyAI's most accurate model. Supports English, Spanish, German, French, Portuguese, and Italian. Keyterms prompting up to 1000 terms.",
            provider: .assemblyAI,
            isPopular: true,
            pricePerSecond: 0.21 / 60.0 / 60.0 // $0.21 per hour
        ),
        CloudTranscriptionModel(
            id: "universal-2-medical",
            displayName: "Universal-2 (Medical)",
            isAvailable: true,
            description: "Universal-2 with Medical Mode add-on for clinical/medical vocabulary. Limited to English, Spanish, German, and French. Billed as a separate add-on on top of Universal-2 pricing.",
            provider: .assemblyAI,
            pricePerSecond: 0.15 / 60.0 / 60.0 // $0.15/hr base — medical add-on billed separately
        ),
        CloudTranscriptionModel(
            id: "universal-3-pro-medical",
            displayName: "Universal-3 Pro (Medical)",
            isAvailable: true,
            description: "Universal-3 Pro with Medical Mode add-on for clinical/medical vocabulary. Limited to English, Spanish, German, and French. Billed as a separate add-on on top of Universal-3 Pro pricing.",
            provider: .assemblyAI,
            pricePerSecond: 0.21 / 60.0 / 60.0 // $0.21/hr base — medical add-on billed separately
        ),

        // ElevenLabs Models
        CloudTranscriptionModel(
            id: "scribe_v1",
            displayName: "Scribe v1",
            isAvailable: true,
            description: "ElevenLabs Scribe model with multilingual coverage and word-level timestamps. Does not support custom vocabulary.",
            provider: .elevenLabs,
            pricePerSecond: nil
        ),
        CloudTranscriptionModel(
            id: "scribe_v2",
            displayName: "Scribe v2",
            isAvailable: true,
            description: "ElevenLabs' latest Scribe model with improved accuracy. Supports custom vocabulary with keyterm prompting.",
            provider: .elevenLabs,
            isPopular: true,
            pricePerSecond: nil
        ),

        // Mistral Models
        CloudTranscriptionModel(
            id: "voxtral-mini-latest",
            displayName: "Voxtral Mini",
            isAvailable: true,
            description: "Mistral's state-of-the-art transcription model. Faster and more accurate than Whisper large-v3.",
            provider: .mistral,
            isPopular: true,
            pricePerSecond: 0.002 / 60.0  // $0.002 per minute
        ),

        // Soniox Models
        CloudTranscriptionModel(
            id: "stt-async-v4",
            displayName: "STT Async v4",
            isAvailable: true,
            description: "Soniox async batch transcription model with 60+ supported languages.",
            provider: .soniox,
            isPopular: true,
            pricePerSecond: nil
        ),

        // Google Gemini Models
        CloudTranscriptionModel(
            id: "gemini-2.5-flash",
            displayName: "Gemini 2.5 Flash",
            isAvailable: true,
            description: "Fast and affordable multimodal transcription. Supports custom vocabulary via prompting.",
            provider: .gemini,
            isPopular: true,
            pricePerSecond: nil  // Token-based pricing, not per-second
        ),
        CloudTranscriptionModel(
            id: "gemini-2.5-flash-lite",
            displayName: "Gemini 2.5 Flash Lite",
            isAvailable: true,
            description: "Cheapest Gemini option for high-volume transcription. Good accuracy at minimal cost.",
            provider: .gemini,
            isPopular: true,
            pricePerSecond: nil
        ),
        CloudTranscriptionModel(
            id: "gemini-2.5-pro",
            displayName: "Gemini 2.5 Pro",
            isAvailable: true,
            description: "Highest quality Gemini model. Best accuracy for complex audio with background noise.",
            provider: .gemini,
            isPopular: true,
            pricePerSecond: nil
        ),
        CloudTranscriptionModel(
            id: "gemini-3.1-flash-lite-preview",
            displayName: "Gemini 3.1 Flash Lite (Preview)",
            isAvailable: true,
            description: "Latest generation lightweight Gemini model. Fast and cost-effective transcription.",
            provider: .gemini,
            pricePerSecond: nil
        ),
        CloudTranscriptionModel(
            id: "gemini-3-flash-preview",
            displayName: "Gemini 3 Flash (Preview)",
            isAvailable: true,
            description: "Next-gen Gemini Flash with improved accuracy and speed.",
            provider: .gemini,
            pricePerSecond: nil
        ),
        CloudTranscriptionModel(
            id: "gemini-3.1-pro-preview",
            displayName: "Gemini 3.1 Pro (Preview)",
            isAvailable: true,
            description: "Latest generation Gemini Pro model. Highest quality transcription with preview access.",
            provider: .gemini,
            pricePerSecond: nil
        ),

        // Microsoft Azure Speech (HyperWhisper Cloud only)
        CloudTranscriptionModel(
            id: "mai-transcribe-1.5",
            displayName: "MAI-Transcribe 1.5 (Preview)",
            isAvailable: true,
            description: "Microsoft's 43-language transcription model with contextual biasing.",
            provider: .microsoftAzureSpeech,
            isPopular: true,
            pricePerSecond: 0.006 / 60.0
        ),

        // Google Cloud Speech (HyperWhisper Cloud only)
        CloudTranscriptionModel(
            id: "chirp_3",
            displayName: "Chirp 3",
            isAvailable: true,
            description: "Google's latest multilingual speech model with phrase adaptation.",
            provider: .googleSpeech,
            isPopular: true,
            pricePerSecond: 0.016 / 60.0
        ),
    ]
    
    /// Legacy AssemblyAI model IDs that have been retired. Resolved transparently to their
    /// modern replacements so existing Modes and backups keep working after the 2026-05-11
    /// deprecation of `word_boost` and the `slam-1` / `universal` identifiers.
    private static let legacyAssemblyAIAliases: [String: String] = [
        "universal": "universal-2",
        "slam-1": "universal-3-pro"
    ]

    /// Resolve a legacy AssemblyAI model ID to its current equivalent. Non-AssemblyAI
    /// and already-current IDs pass through unchanged. AssemblyAI-scoped by design —
    /// do not add aliases for other providers here; give them their own resolver.
    static func resolveAssemblyAIModelAlias(_ id: String) -> String {
        legacyAssemblyAIAliases[id] ?? id
    }

    /// Deepgram model IDs removed in the 2026-05 catalog cleanup. Stored Modes,
    /// per-mode default-model overrides, and the streaming Deepgram setting all
    /// migrate any of these IDs to `nova-3-general` at launch.
    static let removedDeepgramModelIds: Set<String> = [
        "nova-2-meeting", "nova-2-phonecall", "nova-2-voicemail",
        "nova-2-finance", "nova-2-conversationalai", "nova-2-automotive",
        "nova-2-video", "nova", "nova-phonecall",
        "enhanced-general", "enhanced-meeting", "enhanced-phonecall", "enhanced-finance",
        "base-general", "base-meeting", "base-phonecall", "base-voicemail",
        "base-finance", "base-conversationalai", "base-video",
        "whisper-tiny", "whisper-base", "whisper-small", "whisper-medium", "whisper-large",
    ]

    /// Resolve a removed Deepgram model ID to `nova-3-general`. Non-removed IDs
    /// (and `nil`) pass through unchanged. Deepgram-scoped — do not add aliases
    /// for other providers here.
    static func resolveDeepgramModelAlias(_ id: String?) -> String? {
        guard let id else { return nil }
        return removedDeepgramModelIds.contains(id) ? "nova-3-general" : id
    }

    /// Splits a (possibly medical) model ID into the canonical AssemblyAI
    /// `speech_model` value and whether Medical Mode is enabled. Medical Mode
    /// is encoded as a `-medical` suffix on the model ID; the suffix never goes
    /// over the wire — instead `domain: "medical-v1"` is added to the request
    /// body. Legacy aliases are resolved first.
    static func assemblyAIRequestParams(for id: String) -> (speechModel: String, medical: Bool) {
        let resolved = resolveAssemblyAIModelAlias(id)
        if resolved.hasSuffix("-medical") {
            return (String(resolved.dropLast("-medical".count)), true)
        }
        return (resolved, false)
    }

    /// Get a model by its ID
    /// - Parameter id: The model ID to look up
    /// - Returns: The CloudTranscriptionModel if found, nil otherwise
    static func model(withId id: String) -> CloudTranscriptionModel? {
        let resolved = resolveAssemblyAIModelAlias(id)
        return availableModels.first { $0.id == resolved }
    }
    
    /// Get the display name for a model ID
    /// - Parameter id: The model ID to look up
    /// - Returns: The display name if found, or the ID itself as fallback
    static func displayName(for id: String) -> String {
        model(withId: id)?.displayName ?? id
    }
    
    /// Get all available model IDs
    /// - Returns: Array of model IDs that are marked as available
    static var availableModelIds: [String] {
        availableModels.filter { $0.isAvailable }.map { $0.id }
    }
    
    /// Get all available models for UI pickers
    /// - Returns: Array of available models
    static var availableModelsForPicker: [CloudTranscriptionModel] {
        availableModels.filter { $0.isAvailable }
    }
    
    /// Get models for a specific provider
    /// - Parameter provider: The cloud provider to filter by
    /// - Returns: Array of models for that provider
    static func models(for provider: CloudProvider) -> [CloudTranscriptionModel] {
        availableModels.filter { $0.provider == provider && $0.isAvailable }
    }

    /// Get the curated default model list for a provider. Falls back to all
    /// provider models when no popular metadata exists.
    static func popularModels(for provider: CloudProvider) -> [CloudTranscriptionModel] {
        let models = models(for: provider)
        let popular = models.filter { $0.isPopular }
        return popular.isEmpty ? models : popular
    }
    
    /// Get default model for a provider
    /// - Parameter provider: The cloud provider
    /// - Returns: The default model ID for that provider
    static func defaultModel(for provider: CloudProvider) -> String {
        switch provider {
        case .hyperwhisper:
            return ""  // HyperWhisper Cloud routes by accuracy tier; no client-side model parameter
        case .openai:
            return "whisper-1"
        case .groq:
            return "whisper-large-v3-turbo"
        case .deepgram:
            return "nova-3-general"  // Use latest Nova-3 as default
        case .assemblyAI:
            return "universal-2"  // Multi-language default; users can switch to universal-3-pro
        case .elevenLabs:
            return "scribe_v2"
        case .mistral:
            return "voxtral-mini-latest"
        case .soniox:
            return "stt-async-v5"  // Matches cloud-stt-catalog default + SonioxProvider fallback
        case .gemini:
            return "gemini-2.5-flash"
        case .grok:
            return ""  // No model parameter — single implicit model
        case .microsoftAzureSpeech:
            return "mai-transcribe-1.5"
        case .googleSpeech:
            return "chirp_3"
        }
    }
}
