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
    /// Google's dedicated speech API (`POST /v1beta/interactions`). Same vendor
    /// as `.gemini` but a DIFFERENT API, key slot and eligibility — the
    /// `:generateContent` path accepts `gemini-3.5-transcribe`, bills the audio
    /// and returns empty text, so the two must never be merged.
    case geminiTranscribe = "geminitranscribe"
    /// Meta Muse batch transcription using the user's own Meta Model API key.
    /// Public discovery stays catalog-gated until every desktop head ships it.
    case meta = "meta"

    var id: String { rawValue }

    // MARK: - Parsing persisted / imported values

    /// Parse a `cloudProvider` value that crossed a persistence, backup or
    /// Local API boundary, tolerating whitespace and CASE.
    ///
    /// The raw values above are all lowercase, but the cross-platform backup
    /// format is not: Windows persists and exports camelCase ids
    /// (`CloudTranscriptionProvider.cs` writes `geminiTranscribe`). A plain
    /// `CloudProvider(rawValue:)` therefore returns nil for `"geminiTranscribe"`,
    /// and every call site's `?? .hyperwhisper` fallback turns that miss into a
    /// SILENT downgrade: a BYOK user restoring a Windows backup stops using
    /// their own Google key and is billed HyperWhisper credits instead, with no
    /// error and no visible change in the UI.
    ///
    /// The shared core's `normalize_cloud_provider` now lowercases the values it
    /// passes through, so the import path arrives here already canonical — but
    /// this parse is what covers every OTHER boundary (Core Data rows written by
    /// an older build, the Local API, a hand-edited backup) and what keeps the
    /// two halves independent.
    ///
    /// Use this instead of `init(rawValue:)` anywhere the string came from
    /// storage. Unrecognised values still return nil, so a provider belonging to
    /// another platform is not silently coerced onto one of ours.
    static func parse(_ value: String?) -> CloudProvider? {
        guard let trimmed = value?.trimmingCharacters(in: .whitespacesAndNewlines),
              !trimmed.isEmpty else {
            return nil
        }
        return CloudProvider(rawValue: trimmed.lowercased())
    }

    /// The canonical spelling to PERSIST for `value`: the matching case's raw
    /// value when `parse` recognises it, otherwise the input verbatim.
    ///
    /// Applied on the write path so an imported camelCase id is stored in the
    /// lowercase form every reader and every SwiftUI picker tag expects. An
    /// unrecognised value passes through untouched — it may belong to a platform
    /// this build does not model, and rewriting or dropping it would lose the
    /// user's setting on the next export.
    static func canonicalStorageValue(_ value: String?) -> String? {
        guard let value else { return nil }
        return parse(value)?.rawValue ?? value
    }

    /// The cloud provider a mode USES for transcription, as a stored string.
    ///
    /// A blank value (nil, empty or whitespace) takes the shared core's answer
    /// (`cloudSttEffectiveModeCloudProvider`), which is HyperWhisper Cloud on
    /// every head (#1709). A non-blank value is returned as stored: the core
    /// also folds legacy aliases, and this head keeps its own routing for those.
    static func effectiveModeStorageValue(_ stored: String?) -> String {
        guard let stored,
              !stored.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return cloudSttEffectiveModeCloudProvider(value: stored)
        }
        return stored
    }

    /// The parsed provider a mode uses for transcription. A blank value reads
    /// the shared rule; an id this build does not model still falls back to
    /// HyperWhisper Cloud, as before.
    static func forMode(_ stored: String?) -> CloudProvider {
        parse(effectiveModeStorageValue(stored)) ?? .hyperwhisper
    }

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
        case .geminiTranscribe:
            return "Gemini 3.5 Transcribe"
        case .meta:
            return "Meta"
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
            return "SpaceXAI Grok speech-to-text API"
        case .microsoftAzureSpeech:
            return "Microsoft MAI-Transcribe via Azure Speech (up to 60 languages with contextual biasing)"
        case .googleSpeech:
            return "Google Cloud Speech-to-Text V2 with Chirp 3 (multilingual + phrase adaptation)"
        case .geminiTranscribe:
            return "Google's dedicated Gemini 3.5 speech-to-text API with native custom vocabulary"
        case .meta:
            return "Meta Muse Voice Transcribe batch speech-to-text"
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
        case .gemini, .geminiTranscribe:
            // Same vendor, same AI Studio key page — the two BYOK slots are
            // separate but both keys are minted here.
            return "https://aistudio.google.com/apikey"
        case .grok:
            return "https://console.x.ai/"
        case .meta:
            return "https://dev.meta.ai/docs/speech-to-text/"
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
        case .geminiTranscribe:
            // `/v1beta/interactions` has no file-reference form — the audio goes
            // inline as base64, which inflates it by ~33%. 14 MB of raw audio
            // stays under the endpoint's request ceiling once encoded.
            return 14 * 1024 * 1024  // 14 MB
        case .meta:
            return 32 * 1024 * 1024
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

        case .gemini, .geminiTranscribe:
            // Gemini multimodal audio support. `/v1beta/interactions` takes the
            // same container set as `:generateContent` — one vendor, one decoder.
            // Ref: https://ai.google.dev/gemini-api/docs/audio
            return ["mp3", "mp4", "m4a", "wav", "webm", "flac", "ogg", "aac", "opus"]

        case .grok:
            // xAI Grok STT supported containers (auto-detected)
            // Ref: https://docs.x.ai/docs/api-reference#speech-to-text
            return ["wav", "mp3", "ogg", "opus", "flac", "aac", "mp4", "m4a", "mkv"]

        case .meta:
            // Imported containers are normalized to canonical Muse WAV before upload.
            return ["wav", "mp3", "mp4", "m4a", "webm", "flac", "ogg", "aac"]

        case .microsoftAzureSpeech:
            // Azure Speech Foundry transcribe endpoint — common containers
            return ["wav", "mp3", "mp4", "m4a", "ogg", "flac", "webm"]

        case .googleSpeech:
            // Google Speech V2 autoDecodingConfig handles common containers
            return ["wav", "mp3", "mp4", "m4a", "ogg", "flac", "webm"]
        }
    }
}
