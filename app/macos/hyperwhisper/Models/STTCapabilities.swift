//
//  STTCapabilities.swift
//  hyperwhisper
//
//  Provider-agnostic registry of cloud STT providers, models, and supported locales.
//
//  LANGUAGE TAG STANDARDS:
//  - All locales should use BCP-47 tags (e.g., "en-US", "es-419")
//  - Special case: "auto" for automatic language detection (handled by provider code)
//  - Do NOT use pseudo-locales like "multi" - they break Locale(identifier:) parsing

import Foundation

struct STTLanguageSpec: Identifiable, Hashable {
    let code: String
    let displayName: String

    var id: String { code }

    init(code: String, displayName: String? = nil) {
        self.code = code
        if let name = displayName, !name.isEmpty {
            self.displayName = name
        } else {
            self.displayName = LanguageData.displayName(for: code)
        }
    }
}

enum STTLanguageTemplates {
    static var whisperUniversal: [STTLanguageSpec] {
        codes(LanguageData.whisperUniversalCodes)
    }

    /// Nova-3 General supported languages (Deepgram, Jan 2026)
    /// Shared between deepgram.nova-3-general and hyperwhisper.nova-3
    static let nova3General: [STTLanguageSpec] = codes([
        "auto",
        "ar",
        "be", "bg", "bn", "bs",
        "ca", "cs",
        "da", "da-DK", "de", "de-CH",
        "el",
        "en", "en-US", "en-AU", "en-GB", "en-IN", "en-NZ",
        "es", "es-419", "et",
        "fa", "fi", "fr", "fr-CA",
        "he", "hi", "hr", "hu",
        "id", "it",
        "ja",
        "kn", "ko", "ko-KR",
        "lt", "lv",
        "mk", "mr", "ms",
        "nl", "nl-BE", "no",
        "pl", "pt", "pt-BR", "pt-PT",
        "ro", "ru",
        "sk", "sl", "sr", "sv", "sv-SE",
        "ta", "te", "tl", "tr",
        "uk", "ur",
        "vi"
    ])

    /// xAI Grok STT: the `language` parameter only enables ITN formatting —
    /// the model transcribes any language regardless. Picker is restricted to
    /// the supported formatting set so it mirrors the Windows filter
    /// (`GrokSttService.SupportedFormattingLanguages`). "tl" is the macOS
    /// LanguageData code that the provider aliases to xAI's "fil".
    static let grokFormattingLanguages: [STTLanguageSpec] = codes([
        "auto",
        "ar", "cs", "da", "de", "en", "es", "fa", "tl", "fr", "hi",
        "id", "it", "ja", "ko", "mk", "ms", "nl", "pl", "pt", "ro",
        "ru", "sv", "th", "tr", "vi"
    ], context: "xAI Grok formatting language filter")

    /// Soniox stt-async-v4 supported languages verified against official Soniox docs on 2026-03-21.
    static let sonioxAsyncV4: [STTLanguageSpec] = codes([
        "auto",
        "af", "sq", "ar", "az", "eu", "be", "bn", "bs", "bg", "ca",
        "zh", "hr", "cs", "da", "nl", "en", "et", "fi", "fr", "gl",
        "de", "el", "gu", "he", "hi", "hu", "id", "it", "ja", "kn",
        "kk", "ko", "lv", "lt", "mk", "ms", "ml", "mr", "no", "fa",
        "pl", "pt", "pa", "ro", "ru", "sr", "sk", "sl", "es", "sw",
        "sv", "tl", "ta", "te", "th", "tr", "uk", "ur", "vi", "cy"
    ], context: "Soniox stt-async-v4 language filter")

    /// Azure MAI-Transcribe 1.5 supported languages, normalized from the shared
    /// catalog (azureMaiTranscribe, ISO-639-1) to the macOS picker code space.
    /// Catalog "or" (Odia) is dropped — no LanguageData entry; "nb" → "no".
    /// Without this the HyperWhisper Cloud Azure MAI tier fell through to the full
    /// ~100-language list and let users pick unsupported languages.
    static let azureMaiTranscribe: [STTLanguageSpec] = codes([
        "auto",
        "ar", "as", "bg", "bn", "ca", "cs", "da", "de", "el", "en",
        "es", "et", "fi", "fr", "gu", "hi", "hu", "id", "it", "ja",
        "kn", "ko", "lt", "ml", "mr", "no", "nl", "pa", "pl", "pt",
        "ro", "ru", "sk", "sl", "sv", "ta", "te", "th", "tr", "uk",
        "vi"
    ], context: "Azure MAI-Transcribe language filter")

    /// Google Speech-to-Text Chirp 3 supported languages, normalized from the
    /// shared catalog (googleChirp3, BCP-47 locale) to the macOS picker code
    /// space: primary subtag, lowercased, deduped. "iw" → "he", "jv" → "jw",
    /// "cmn" → "zh" (Mandarin), Cantonese kept as "yue". Codes with no LanguageData
    /// entry are dropped (fil, nso, ast, ky, or, wo, xh, zu).
    static let googleChirp3: [STTLanguageSpec] = codes([
        "auto",
        "ca", "hr", "da", "nl", "en", "fi", "fr", "de", "el", "hi",
        "it", "ja", "ko", "pl", "pt", "ro", "ru", "es", "sv", "tr",
        "uk", "vi", "af", "sq", "am", "ar", "hy", "as", "az", "eu",
        "bn", "bg", "my", "cs", "et", "gl", "ka", "gu", "ha", "he",
        "hu", "is", "id", "jw", "kn", "kk", "km", "lo", "lv", "lt",
        "lb", "mk", "ms", "ml", "mt", "mi", "mr", "mn", "ne", "no",
        "fa", "pa", "sr", "sk", "sl", "sw", "ta", "te", "th", "uz",
        "cy", "yo", "zh", "yue"
    ], context: "Google Chirp 3 language filter")

    static let englishUS: [STTLanguageSpec] = codes(["en", "en-US"], context: "englishUS template")

    static func codes(_ codes: [String], context: String? = nil) -> [STTLanguageSpec] {
        LanguageData.languages(for: codes, context: context).map { info in
            STTLanguageSpec(code: info.code, displayName: info.displayName)
        }
    }
}

struct STTModelSpec: Identifiable, Hashable {
    let id: String
    let displayName: String
    var languages: [STTLanguageSpec]
    var notes: String?

    var locales: [String] { languages.map { $0.code } }
}

struct STTProviderSpec: Identifiable, Hashable {
    let id: String // e.g., "elevenlabs"
    let displayName: String // e.g., "ElevenLabs"
    let authKeyName: String // Settings/Keychain logical name
    var lastVerifiedAt: String? // ISO date string
    var models: [STTModelSpec]
}

struct STTCapabilitiesRegistry {
    let version: String
    let providers: [STTProviderSpec]
}

enum STTCapabilities {
    static let registry = STTCapabilitiesRegistry(
        version: "2025-09-22",
        providers: [
            STTProviderSpec(
                id: "openai",
                displayName: "OpenAI",
                authKeyName: "OpenAI",
                lastVerifiedAt: "2025-09-17",
                models: [
                    STTModelSpec(
                        id: "gpt-4o-transcribe",
                        displayName: "GPT-4o Transcribe",
                        languages: STTLanguageTemplates.whisperUniversal,
                        notes: "Advanced speech-to-text powered by GPT-4o with Whisper language coverage."
                    ),
                    STTModelSpec(
                        id: "gpt-4o-mini-transcribe",
                        displayName: "GPT-4o Mini Transcribe",
                        languages: STTLanguageTemplates.whisperUniversal,
                        notes: "Fast GPT-4o Mini transcription with full Whisper language support."
                    ),
                    STTModelSpec(
                        id: "whisper-1",
                        displayName: "Whisper-1",
                        languages: STTLanguageTemplates.whisperUniversal,
                        notes: "General-purpose Whisper model with multilingual support and auto-detection."
                    )
                ]
            ),
            STTProviderSpec(
                id: "groq",
                displayName: "Groq",
                authKeyName: "Groq",
                lastVerifiedAt: "2025-09-17",
                models: [
                    STTModelSpec(
                        id: "whisper-large-v3-turbo",
                        displayName: "Whisper Large v3 Turbo",
                        languages: STTLanguageTemplates.whisperUniversal,
                        notes: "Groq's ultra-fast Whisper implementation with full multilingual coverage."
                    ),
                    STTModelSpec(
                        id: "whisper-large-v3",
                        displayName: "Whisper Large v3",
                        languages: STTLanguageTemplates.whisperUniversal,
                        notes: "Groq's Whisper v3 model with multilingual support and auto-detection."
                    )
                ]
            ),
            STTProviderSpec(
                id: "deepgram",
                displayName: "Deepgram",
                authKeyName: "Deepgram",
                lastVerifiedAt: "2025-09-17",
                models: [
                    // Nova-3 Models
                    STTModelSpec(
                        id: "nova-3-general",
                        displayName: "Nova-3 General",
                        languages: STTLanguageTemplates.nova3General,
                        notes: "Highest performing model with multilingual support and auto-detection."
                    ),
                    STTModelSpec(
                        id: "nova-3-medical",
                        displayName: "Nova-3 Medical",
                        languages: STTLanguageTemplates.codes(["en", "en-US", "en-AU", "en-CA", "en-GB", "en-IE", "en-IN", "en-NZ"]),
                        notes: "Medical domain vocabulary."
                    ),

                    // Nova-2 Models
                    STTModelSpec(
                        id: "nova-2-general",
                        displayName: "Nova-2 General",
                        languages: STTLanguageTemplates.codes([
                            "auto",
                            "bg", "ca", "zh", "zh-CN", "zh-Hans", "zh-TW", "zh-Hant", "zh-HK",
                            "cs", "da", "da-DK", "nl", "nl-BE",
                            "en", "en-US", "en-AU", "en-GB", "en-NZ", "en-IN",
                            "et", "fi", "fr", "fr-CA",
                            "de", "de-CH",
                            "el", "hi", "hu", "id", "it", "ja",
                            "ko", "ko-KR",
                            "lv", "lt", "ms", "no", "pl",
                            "pt", "pt-BR", "pt-PT",
                            "ro", "ru", "sk",
                            "es", "es-419",
                            "sv", "sv-SE",
                            "th", "th-TH",
                            "tr", "uk", "vi"
                        ]),
                        notes: "General-purpose with wide language support and auto-detection."
                    ),
                    STTModelSpec(
                        id: "nova-2-medical",
                        displayName: "Nova-2 Medical",
                        languages: STTLanguageTemplates.englishUS,
                        notes: "Medical domain vocabulary."
                    )
                ]
            ),
            STTProviderSpec(
                id: "assemblyai",
                displayName: "AssemblyAI",
                authKeyName: "AssemblyAI",
                lastVerifiedAt: "2026-04-11",
                models: [
                    STTModelSpec(
                        id: "universal-2",
                        displayName: "Universal-2",
                        languages: STTLanguageTemplates.codes([
                            "auto",
                            "en", "es", "fr", "de", "it", "pt", "nl", "hi", "ja", "zh",
                            "fi", "ko", "pl", "ru", "tr", "uk", "vi",
                            "af", "sq", "am", "ar", "hy", "as", "az", "ba", "eu", "be",
                            "bn", "bs", "br", "bg", "my", "ca", "hr", "cs", "da", "et",
                            "fo", "gl", "ka", "el", "gu", "ht", "ha", "haw", "he", "hu",
                            "is", "id", "jw", "kn", "kk", "km", "lo", "la", "lv", "ln",
                            "lt", "lb", "mk", "mg", "ms", "ml", "mt", "mi", "mr", "mn",
                            "ne", "no", "nn", "oc", "pa", "ps", "fa", "ro", "sa", "sr",
                            "sn", "sd", "si", "sk", "sl", "so", "su", "sw", "sv", "tl",
                            "tg", "ta", "tt", "te", "th", "bo", "tk", "ur", "uz", "cy",
                            "yi", "yo"
                        ]),
                        notes: "Supports 99 languages with automatic language detection. Keyterms prompting up to 200 terms."
                    ),
                    STTModelSpec(
                        id: "universal-3-pro",
                        displayName: "Universal-3 Pro",
                        languages: STTLanguageTemplates.codes([
                            "auto", "en", "es", "de", "fr", "pt", "it"
                        ]),
                        notes: "Highest-accuracy model. Supports English, Spanish, German, French, Portuguese, and Italian. Keyterms prompting up to 1000 terms."
                    ),
                    STTModelSpec(
                        id: "universal-2-medical",
                        displayName: "Universal-2 (Medical)",
                        languages: STTLanguageTemplates.codes([
                            "auto", "en", "es", "de", "fr"
                        ]),
                        notes: "Universal-2 with the Medical Mode add-on. Domain correction is only applied for English, Spanish, German, and French — other languages fall back to plain transcription."
                    ),
                    STTModelSpec(
                        id: "universal-3-pro-medical",
                        displayName: "Universal-3 Pro (Medical)",
                        languages: STTLanguageTemplates.codes([
                            "auto", "en", "es", "de", "fr"
                        ]),
                        notes: "Universal-3 Pro with the Medical Mode add-on. Domain correction is only applied for English, Spanish, German, and French — other languages fall back to plain transcription."
                    )
                ]
            ),
            STTProviderSpec(
                id: "elevenlabs",
                displayName: "ElevenLabs",
                authKeyName: "ElevenLabs",
                lastVerifiedAt: "2025-09-22",
                models: [
                    STTModelSpec(
                        id: "scribe_v1",
                        displayName: "Scribe v1",
                        languages: STTLanguageTemplates.whisperUniversal,
                        notes: "Multilingual batch transcription with word-level timestamps and diarization support. Does not support custom vocabulary."
                    ),
                    STTModelSpec(
                        id: "scribe_v2",
                        displayName: "Scribe v2",
                        languages: STTLanguageTemplates.whisperUniversal,
                        notes: "Latest generation Scribe model with improved accuracy. Supports custom vocabulary with keyterm prompting (up to 100 terms)."
                    )
                ]
            ),
            STTProviderSpec(
                id: "mistral",
                displayName: "Mistral",
                authKeyName: "Mistral",
                lastVerifiedAt: "2025-11-27",
                models: [
                    STTModelSpec(
                        id: "voxtral-mini-latest",
                        displayName: "Voxtral Mini",
                        languages: STTLanguageTemplates.codes([
                            "auto",
                            "en",  // English
                            "zh",  // Chinese
                            "hi",  // Hindi
                            "es",  // Spanish
                            "ar",  // Arabic
                            "fr",  // French
                            "pt",  // Portuguese
                            "ru",  // Russian
                            "de",  // German
                            "ja",  // Japanese
                            "ko",  // Korean
                            "it",  // Italian
                            "nl"   // Dutch
                        ]),
                        notes: "Mistral's state-of-the-art transcription model. Supports 13 languages with automatic detection. Does not support custom vocabulary."
                    )
                ]
            ),
            STTProviderSpec(
                id: "soniox",
                displayName: "Soniox",
                authKeyName: "Soniox",
                lastVerifiedAt: "2026-03-21",
                models: [
                    STTModelSpec(
                        id: "stt-async-v4",
                        displayName: "STT Async v4",
                        languages: STTLanguageTemplates.sonioxAsyncV4,
                        notes: "Soniox async batch transcription model with optional language hints and plain-text transcript output."
                    ),
                    STTModelSpec(
                        id: "stt-async-v5",
                        displayName: "STT Async v5",
                        languages: STTLanguageTemplates.sonioxAsyncV4,
                        notes: "Soniox async batch transcription model with optional language hints and plain-text transcript output."
                    )
                ]
            ),
            STTProviderSpec(
                id: "hyperwhisper",
                displayName: "HyperWhisper Cloud",
                authKeyName: "",
                models: [
                    STTModelSpec(
                        id: "nova-3",
                        displayName: "Nova-3 Streaming",
                        languages: STTLanguageTemplates.nova3General,
                        notes: "HyperWhisper Cloud streaming (Deepgram Nova-3 backend)."
                    )
                ]
            ),
            STTProviderSpec(
                id: "grok",
                displayName: "Grok",
                authKeyName: "Grok",
                lastVerifiedAt: "2026-04-22",
                models: [
                    // Grok STT has no `model` parameter — the stored
                    // `CloudTranscriptionModel` for a Grok mode is "", so the
                    // lookup key here is the empty string.
                    STTModelSpec(
                        id: "",
                        displayName: "Default",
                        languages: STTLanguageTemplates.grokFormattingLanguages,
                        notes: "xAI Grok speech-to-text. The language setting only enables number/currency formatting — transcription works on any spoken language."
                    )
                ]
            ),
            // Routed HyperWhisper Cloud upstreams. These have no BYOK UI of their
            // own; they exist here purely so the cloud tier's language picker (keyed
            // on the tier's sttProvider + model id via languageFilterCloudProviderId /
            // languageFilterCloudModelId) resolves a real supported-language set
            // instead of falling through to the full list.
            STTProviderSpec(
                id: "azure-mai",
                displayName: "Azure MAI-Transcribe",
                authKeyName: "",
                lastVerifiedAt: "2026-06-18",
                models: [
                    STTModelSpec(
                        id: "mai-transcribe-1.5",
                        displayName: "MAI-Transcribe 1.5",
                        languages: STTLanguageTemplates.azureMaiTranscribe,
                        notes: "Microsoft Azure MAI-Transcribe 1.5. Multilingual mode with a restricted ~42-language set."
                    )
                ]
            ),
            STTProviderSpec(
                id: "google-chirp",
                displayName: "Google Chirp",
                authKeyName: "",
                lastVerifiedAt: "2026-06-18",
                models: [
                    STTModelSpec(
                        id: "chirp_3",
                        displayName: "Chirp 3",
                        languages: STTLanguageTemplates.googleChirp3,
                        notes: "Google Speech-to-Text Chirp 3. 29 GA locales plus preview languages; auto-detect supported."
                    )
                ]
            )
        ]
    )

    static func provider(id: String) -> STTProviderSpec? {
        registry.providers.first { $0.id == id }
    }

    static func model(providerId: String, modelId: String) -> STTModelSpec? {
        provider(id: providerId)?.models.first { $0.id == modelId }
    }

    static func languages(providerId: String, modelId: String) -> [STTLanguageSpec] {
        model(providerId: providerId, modelId: modelId)?.languages ?? []
    }

    static func locales(providerId: String, modelId: String) -> [String] {
        languages(providerId: providerId, modelId: modelId).map { $0.code }
    }
}
