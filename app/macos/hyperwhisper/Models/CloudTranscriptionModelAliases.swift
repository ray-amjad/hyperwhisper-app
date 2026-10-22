import Foundation

extension CloudTranscriptionModels {
    /// Legacy AssemblyAI model IDs that have been retired. Resolved transparently to their
    /// modern replacements so existing Modes and backups keep working.
    private static let legacyAssemblyAIAliases: [String: String] = [
        "universal": "universal-2",
        "slam-1": "universal-3-5-pro",
        "universal-3-pro": "universal-3-5-pro",
        "universal-3-pro-medical": "universal-3-5-pro-medical",
    ]

    /// Resolve a legacy AssemblyAI model ID to its current equivalent. Non-AssemblyAI
    /// and already-current IDs pass through unchanged. AssemblyAI-scoped by design —
    /// do not add aliases for other providers here; give them their own resolver.
    static func resolveAssemblyAIModelAlias(_ id: String) -> String {
        legacyAssemblyAIAliases[id] ?? id
    }

    /// Legacy ElevenLabs model IDs that have been retired. Resolved transparently to
    /// their modern replacements so existing Modes and backups keep working after the
    /// 2026-07-09 retirement of `scribe_v1`.
    private static let legacyElevenLabsAliases: [String: String] = [
        "scribe_v1": "scribe_v2"
    ]

    /// Resolve a legacy ElevenLabs model ID to its current equivalent. Non-ElevenLabs
    /// and already-current IDs pass through unchanged. ElevenLabs-scoped by design —
    /// do not add aliases for other providers here; give them their own resolver.
    static func resolveElevenLabsModelAlias(_ id: String) -> String {
        legacyElevenLabsAliases[id] ?? id
    }

    private static let legacySonioxAliases: [String: String] = [
        "stt-async-v4": "stt-async-v5",
    ]

    static func resolveSonioxModelAlias(_ id: String) -> String {
        legacySonioxAliases[id] ?? id
    }

    /// Every target must be a model the Cloud catalog's `gemini` entry sells, or the
    /// membership guard judges a migrated mode foreign and swaps its model (#796).
    private static let legacyGeminiAliases: [String: String] = [
        "gemini-3.1-flash-lite-preview": "gemini-2.5-flash-lite",
        "gemini-2.0-flash": "gemini-3.8-flash",
        // Left the picker 2026-10 for the GA model. PARITY: hw-catalog GEMINI_ALIASES.
        "gemini-3-flash-preview": "gemini-3.8-flash",
    ]

    static func resolveGeminiModelAlias(_ id: String) -> String {
        legacyGeminiAliases[id] ?? id
    }

    /// xAI ended `grok-voice-transcribe-1.0` on 2026-10-02 and routes it to 2.0
    /// at the same price. PARITY: hw-catalog GROK_ALIASES, hyperwhisper-cloud
    /// stt-models.ts `grok.aliases`.
    private static let legacyGrokAliases: [String: String] = [
        "grok-voice-transcribe-1.0": "grok-voice-transcribe-2.0",
    ]

    static func resolveGrokModelAlias(_ id: String) -> String {
        legacyGrokAliases[id] ?? id
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

    /// Single dispatcher for resolving a (possibly legacy) model ID to its current
    /// equivalent, scoped by provider. PARITY: Windows `ResolveModelAlias(modelId:provider:)`
    /// (`CloudTranscriptionModel.cs`). Every call site that reads a Mode's stored model ID
    /// — not just request-build time — should go through this so a Mode still holding a
    /// retired ID (e.g. ElevenLabs `scribe_v1`) gets migrated wherever it's loaded, not
    /// only where the wire request happens to be built.
    ///
    /// When `provider` is `nil` (the caller doesn't have it handy), all three per-provider
    /// resolvers run in sequence — safe because each one's aliases are keyed by IDs scoped
    /// to that provider and don't collide with another provider's model IDs.
    static func resolveModelAlias(_ id: String, provider: CloudProvider?) -> String {
        switch provider {
        case .assemblyAI:
            return resolveAssemblyAIModelAlias(id)
        case .elevenLabs:
            return resolveElevenLabsModelAlias(id)
        case .deepgram:
            return resolveDeepgramModelAlias(id) ?? id
        case .soniox:
            return resolveSonioxModelAlias(id)
        case .gemini:
            return resolveGeminiModelAlias(id)
        case .grok:
            return resolveGrokModelAlias(id)
        case nil:
            return resolveDeepgramModelAlias(
                resolveGrokModelAlias(
                    resolveGeminiModelAlias(
                        resolveSonioxModelAlias(
                            resolveElevenLabsModelAlias(resolveAssemblyAIModelAlias(id))
                        )
                    )
                )
            ) ?? id
        default:
            return id
        }
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
}
