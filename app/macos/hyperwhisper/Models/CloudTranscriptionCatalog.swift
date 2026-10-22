import Foundation

extension CloudTranscriptionModels {
    /// Test seam for exercising a completed direct provider before the shared
    /// cross-platform catalog gate is activated. Production always reads the gate.
    #if DEBUG
    static var metaBYOKCatalogOverride: Bool?
    #endif

    static var isMetaBYOKCatalogEnabled: Bool {
        #if DEBUG
        if let override = metaBYOKCatalogOverride { return override }
        #endif
        return CloudSTTCatalog.shared.entry(byId: CloudAccuracyTier.metaMuse.rawValue)?
            .access?.byokEligible == true
    }

    /// All available cloud transcription models for speech-to-text
    /// Models are ordered by provider and capability
    static var availableModels: [CloudTranscriptionModel] {
        var models: [CloudTranscriptionModel] = [
            openAIModels(),
            groqModels(),
            deepgramModels(),
            assemblyAIModels(),
            elevenLabsModels(),
            mistralModels(),
            sonioxModels(),
            geminiModels(),
            geminiTranscribeModels(),
            microsoftAzureSpeechModels(),
            googleSpeechModels(),
            grokModels(),
        ].flatMap { $0 }
        if isMetaBYOKCatalogEnabled {
            let catalogModel = CloudSTTCatalog.shared.defaultModel(
                forEntryId: CloudAccuracyTier.metaMuse.rawValue
            )
            models.append(CloudTranscriptionModel(
                id: catalogModel?.id ?? MetaMuseProvider.modelID,
                isAvailable: true,
                description: "Meta Muse batch transcription using your Meta Model API key",
                provider: .meta,
                isPopular: true,
                pricePerSecond: nil
            ))
        }
        return models
    }

    private static func openAIModels() -> [CloudTranscriptionModel] {
        [
        // OpenAI Models
        CloudTranscriptionModel(
            id: "gpt-4o-mini-transcribe-2025-12-15",
            isAvailable: true,
            description: "Latest dated snapshot of GPT-4o Mini Transcribe. Lowest word error rate among OpenAI transcription models.",
            provider: .openai,
            pricePerSecond: 0.003 / 60.0
        ),
        // The three rows below were deprecated by OpenAI on 2026-08-26 and shut
        // down 2027-02-26. They still work, and a user who has one selected keeps
        // working, so the rows stay for this release — but they are no longer
        // promoted, and `resolve_openai_model_alias` now migrates them. Delete
        // the rows once a build carrying that alias table has shipped.
        CloudTranscriptionModel(
            id: "gpt-4o-transcribe",
            isAvailable: true,
            description: "Advanced speech-to-text powered by GPT-4o. Deprecated by OpenAI — retires 2027-02-26, use GPT Transcribe.",
            provider: .openai,
            pricePerSecond: 0.006 / 60.0
        ),
        CloudTranscriptionModel(
            id: "gpt-4o-mini-transcribe",
            isAvailable: true,
            description: "Fast speech-to-text powered by GPT-4o Mini. Deprecated by OpenAI — retires 2027-02-26, use the 2025-12-15 snapshot.",
            provider: .openai,
            pricePerSecond: 0.003 / 60.0
        ),
        CloudTranscriptionModel(
            id: "whisper-1",
            isAvailable: true,
            description: "General-purpose speech recognition model. Deprecated by OpenAI — retires 2027-02-26, use GPT Transcribe.",
            provider: .openai,
            pricePerSecond: 0.006 / 60.0
        ),
        CloudTranscriptionModel(
            id: "gpt-transcribe",
            isAvailable: true,
            description: "OpenAI's flat-rate transcription model billed per audio minute.",
            provider: .openai,
            isPopular: true,
            pricePerSecond: 0.0045 / 60.0
        ),
        CloudTranscriptionModel(
            id: "gpt-live-transcribe",
            isAvailable: false,  // Requires OpenAI's Realtime WebSocket API — no request path (this
            // app's cloud/BYOK OpenAI transcription is REST/batch-only) actually serves it yet.
            // Catalogued for pricing/metadata; flip once a Realtime session relay is wired.
            description: "OpenAI's realtime transcription model for the Realtime WebSocket API, billed per audio minute.",
            provider: .openai,
            pricePerSecond: 0.017 / 60.0
        ),
        ]
    }

    private static func groqModels() -> [CloudTranscriptionModel] {
        [
        // Groq Models
        CloudTranscriptionModel(
            id: "whisper-large-v3-turbo",
            isAvailable: true,
            description: "Groq's ultra-fast Whisper implementation. Optimized for speed with high accuracy. Excellent for real-time transcription.",
            provider: .groq,
            isPopular: true,
            // $0.04 per hour
            pricePerSecond: 0.04 / 3600.0
        ),
        CloudTranscriptionModel(
            id: "whisper-large-v3",
            isAvailable: true,
            description: "Groq's standard Whisper v3 model. High accuracy with good performance.",
            provider: .groq,
            // $0.111 per hour
            pricePerSecond: 0.111 / 3600.0
        ),
        ]
    }

    private static func deepgramModels() -> [CloudTranscriptionModel] {
        [
        // Deepgram
        CloudTranscriptionModel(
            id: "nova-3-general",
            isAvailable: true,
            description: "The leading model for general transcription from Deepgram.",
            provider: .deepgram,
            isPopular: true,
            pricePerSecond: 0.0043 / 60.0
        ),
        CloudTranscriptionModel(
            id: "nova-3-medical",
            isAvailable: true,
            description: "Optimized audio with medical oriented vocabulary.",
            provider: .deepgram,
            pricePerSecond: 0.0043 / 60.0
        ),
        CloudTranscriptionModel(
            id: "nova-2-general",
            isAvailable: true,
            description: "General-purpose transcription with high accuracy for diverse audio sources.",
            provider: .deepgram,
            pricePerSecond: 0.0043 / 60.0
        ),
        CloudTranscriptionModel(
            id: "nova-2-medical",
            isAvailable: true,
            description: "Medical domain vocabulary for clinical conversations and healthcare settings.",
            provider: .deepgram,
            isPopular: true,
            pricePerSecond: 0.0043 / 60.0
        ),
        ]
    }

    private static func assemblyAIModels() -> [CloudTranscriptionModel] {
        [
        // AssemblyAI Models
        CloudTranscriptionModel(
            id: "dictation",
            isAvailable: true,
            description: "",
            provider: .assemblyAI,
            isPopular: true,
            pricePerSecond: 0.62 / 3600.0
        ),
        CloudTranscriptionModel(
            id: "universal-2",
            isAvailable: true,
            description: "Multi-language model supporting 99 languages with automatic detection. Supports keyterms prompting (up to 200 terms).",
            provider: .assemblyAI,
            isPopular: true,
            pricePerSecond: 0.15 / 60.0 / 60.0 // $0.15 per hour
        ),
        CloudTranscriptionModel(
            id: "universal-3-5-pro",
            isAvailable: true,
            description: "AssemblyAI's most accurate model. Natively covers 18 languages. Keyterms prompting up to 1000 terms.",
            provider: .assemblyAI,
            isPopular: true,
            pricePerSecond: 0.21 / 60.0 / 60.0 // $0.21 per hour — same as Universal-3 Pro
        ),
        CloudTranscriptionModel(
            id: "universal-2-medical",
            isAvailable: true,
            description: "Universal-2 with Medical Mode add-on for clinical/medical vocabulary. Limited to English, Spanish, German, and French. Billed as a separate add-on on top of Universal-2 pricing.",
            provider: .assemblyAI,
            pricePerSecond: 0.15 / 60.0 / 60.0 // $0.15/hr base — medical add-on billed separately
        ),
        CloudTranscriptionModel(
            id: "universal-3-5-pro-medical",
            isAvailable: true,
            description: "Universal-3.5 Pro with Medical Mode add-on for clinical/medical vocabulary. Limited to English, Spanish, German, and French. Billed as a separate add-on on top of Universal-3.5 Pro pricing.",
            provider: .assemblyAI,
            pricePerSecond: 0.21 / 60.0 / 60.0 // $0.21/hr base — same as Universal-3 Pro; medical add-on billed separately
        ),
        ]
    }

    private static func elevenLabsModels() -> [CloudTranscriptionModel] {
        [
        // ElevenLabs Models
        CloudTranscriptionModel(
            id: "scribe_v1",
            isAvailable: false,  // Retired by ElevenLabs 2026-07-09 — resolved to scribe_v2 via legacyElevenLabsAliases
            description: "ElevenLabs Scribe model with multilingual coverage and word-level timestamps. Does not support custom vocabulary.",
            provider: .elevenLabs,
            pricePerSecond: nil
        ),
        CloudTranscriptionModel(
            id: "scribe_v2",
            isAvailable: true,
            description: "ElevenLabs' latest Scribe model with improved accuracy. Supports custom vocabulary with keyterm prompting.",
            provider: .elevenLabs,
            isPopular: true,
            pricePerSecond: nil
        ),
        CloudTranscriptionModel(
            id: "scribe_v2_medical",
            isAvailable: true,
            description: "Scribe v2 tuned for clinical audio. Keyterm prompting highlights drug names and medical phrases. Same rate as Scribe v2.",
            provider: .elevenLabs,
            pricePerSecond: nil
        ),
        ]
    }

    private static func mistralModels() -> [CloudTranscriptionModel] {
        [
        // Mistral Models
        CloudTranscriptionModel(
            id: "voxtral-mini-latest",
            isAvailable: true,
            description: "Mistral's state-of-the-art transcription model. Faster and more accurate than Whisper large-v3.",
            provider: .mistral,
            isPopular: true,
            pricePerSecond: 0.003 / 60.0  // $0.003 per minute
        ),
        ]
    }

    private static func sonioxModels() -> [CloudTranscriptionModel] {
        [
        // Soniox Models
        CloudTranscriptionModel(
            id: "stt-async-v5",
            isAvailable: true,
            description: "Soniox's latest async batch transcription model with 60+ supported languages.",
            provider: .soniox,
            isPopular: true,
            pricePerSecond: nil
        ),
        ]
    }

    private static func geminiModels() -> [CloudTranscriptionModel] {
        [
        // Google Gemini Models
        // gemini-3.8-flash is the catalog default since #1019: Google limits the
        // 2.5 models to accounts that used them before 2026-09-18, so a fresh key
        // may not reach 2.5 Flash. The 2.5 rows stay for the users who have them.
        CloudTranscriptionModel(
            id: "gemini-3.8-flash",
            isAvailable: true,
            description: "Google's current Flash model and the default for new keys. Supports custom vocabulary via prompting.",
            provider: .gemini,
            isPopular: true,
            pricePerSecond: nil  // Token-based pricing, not per-second
        ),
        CloudTranscriptionModel(
            id: "gemini-2.5-flash",
            isAvailable: true,
            description: "Fast and affordable multimodal transcription. Supports custom vocabulary via prompting.",
            provider: .gemini,
            isPopular: true,
            pricePerSecond: nil  // Token-based pricing, not per-second
        ),
        CloudTranscriptionModel(
            id: "gemini-2.5-flash-lite",
            isAvailable: true,
            description: "Cheapest Gemini option for high-volume transcription. Good accuracy at minimal cost.",
            provider: .gemini,
            isPopular: true,
            pricePerSecond: nil
        ),
        CloudTranscriptionModel(
            id: "gemini-2.5-pro",
            isAvailable: true,
            description: "Highest quality Gemini model. Best accuracy for complex audio with background noise.",
            provider: .gemini,
            isPopular: true,
            pricePerSecond: nil
        ),
        CloudTranscriptionModel(
            id: "gemini-3.1-flash-lite",
            isAvailable: true,
            description: "Latest generation lightweight Gemini model. Fast and cost-effective transcription.",
            provider: .gemini,
            pricePerSecond: nil
        ),
        CloudTranscriptionModel(
            id: "gemini-3.1-pro-preview",
            isAvailable: true,
            description: "Latest generation Gemini Pro model. Highest quality transcription with preview access.",
            provider: .gemini,
            pricePerSecond: nil
        ),
        ]
    }

    private static func geminiTranscribeModels() -> [CloudTranscriptionModel] {
        [
        // Gemini 3.5 Transcribe — Google's dedicated speech API
        // (POST /v1beta/interactions), a separate provider from the multimodal
        // `.gemini` rows above. Only the pre-recorded model belongs here; the
        // live variant (`gemini-3.5-transcribe-live`) is WebSocket-only and is
        // never a batch transcription model.
        CloudTranscriptionModel(
            id: "gemini-3.5-transcribe",
            isAvailable: true,
            description: "Google's dedicated speech-to-text model. Supports custom vocabulary for exact spellings of names and jargon.",
            provider: .geminiTranscribe,
            pricePerSecond: nil
        ),
        ]
    }

    private static func microsoftAzureSpeechModels() -> [CloudTranscriptionModel] {
        [
        // Microsoft Azure Speech (HyperWhisper Cloud only)
        CloudTranscriptionModel(
            id: "mai-transcribe-2",
            isAvailable: true,
            description: "Microsoft's 60-language transcription model with contextual biasing.",
            provider: .microsoftAzureSpeech,
            isPopular: true,
            // $0.10 per hour of audio — a limited-time offer Microsoft has
            // announced through the end of the year. Revisit alongside
            // `AZURE_MAI_COST_PER_AUDIO_MINUTE` in the Cloud backend.
            pricePerSecond: 0.10 / 3600.0
        ),
        CloudTranscriptionModel(
            id: "mai-transcribe-1.5",
            isAvailable: true,
            description: "Microsoft's 42-language transcription model with contextual biasing.",
            provider: .microsoftAzureSpeech,
            isPopular: true,
            pricePerSecond: 0.006 / 60.0
        ),
        ]
    }

    private static func googleSpeechModels() -> [CloudTranscriptionModel] {
        [
        // Google Cloud Speech (HyperWhisper Cloud only)
        CloudTranscriptionModel(
            id: "chirp_3",
            isAvailable: true,
            description: "Google's latest multilingual speech model with phrase adaptation.",
            provider: .googleSpeech,
            isPopular: true,
            pricePerSecond: 0.016 / 60.0
        ),
        ]
    }

    private static func grokModels() -> [CloudTranscriptionModel] {
        [
        // xAI Grok, sent as the `model` field. The id was the empty string until
        // 2026-09-19, when xAI gave `/v1/stt` a `model` parameter. xAI ended
        // `grok-voice-transcribe-1.0` on 2026-10-02 and routes it to 2.0 at the
        // same price, so its row is gone; `legacyGrokAliases` moves a stored
        // 1.0 id onto 2.0 (#1908).
        CloudTranscriptionModel(
            id: "grok-voice-transcribe-2.0",
            isAvailable: true,
            description: "SpaceXAI's speech-to-text model — auto-detects the language and follows a mid-recording switch.",
            provider: .grok,
            isPopular: true,
            pricePerSecond: nil
        ),
        ]
    }
}
