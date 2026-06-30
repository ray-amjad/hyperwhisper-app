// CLOUD TRANSCRIPTION MODEL REGISTRY
// Defines available models for each cloud transcription provider.
// This mirrors the macOS CloudTranscriptionModels.swift for cross-platform consistency.
//
// MODEL SELECTION:
// Each mode can specify a cloudTranscriptionModel (e.g., "whisper-1").
// The model ID is sent directly to the cloud API.
//
// PRICING (as of Dec 2024):
// - whisper-1: $0.006/min
// - gpt-4o-transcribe: ~$0.006/min (varies)
// - gpt-4o-mini-transcribe: ~$0.003/min (varies)

namespace HyperWhisper.Models;

/// <summary>
/// Represents a cloud transcription model with metadata.
/// </summary>
public record CloudTranscriptionModel
{
    /// <summary>API model ID (sent to the cloud API).</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable display name for UI.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Brief description of the model's characteristics.</summary>
    public required string Description { get; init; }

    /// <summary>Which provider offers this model.</summary>
    public required CloudTranscriptionProvider Provider { get; init; }

    /// <summary>Whether this model is currently available.</summary>
    public bool IsAvailable { get; init; } = true;

    /// <summary>Price per minute in USD (null if pricing is not publicly available).</summary>
    public decimal? PricePerMinute { get; init; }

    /// <summary>Whether this model should appear in the shortened default picker.</summary>
    public bool IsPopular { get; init; }
}

/// <summary>
/// Registry of all available cloud transcription models.
/// </summary>
public static class CloudTranscriptionModels
{
    // =========================================================================
    // OPENAI MODELS
    // =========================================================================

    /// <summary>
    /// OpenAI Whisper models.
    /// - whisper-1: Classic Whisper model, most cost-effective
    /// - gpt-4o-transcribe: GPT-4o based, most capable and accurate
    /// - gpt-4o-mini-transcribe: Balanced cost and capability
    /// </summary>
    public static readonly CloudTranscriptionModel[] OpenAI = new[]
    {
        new CloudTranscriptionModel
        {
            Id = "gpt-4o-mini-transcribe-2025-12-15",
            DisplayName = "GPT-4o Mini Transcribe (2025-12-15)",
            Description = "Latest dated snapshot of GPT-4o Mini Transcribe",
            Provider = CloudTranscriptionProvider.OpenAI,
            PricePerMinute = 0.003m
        },
        new CloudTranscriptionModel
        {
            Id = "gpt-4o-transcribe",
            DisplayName = "GPT-4o Transcribe",
            Description = "Most capable - best accuracy, handles complex audio",
            Provider = CloudTranscriptionProvider.OpenAI,
            PricePerMinute = 0.006m,
            IsPopular = true
        },
        new CloudTranscriptionModel
        {
            Id = "gpt-4o-mini-transcribe",
            DisplayName = "GPT-4o Mini Transcribe",
            Description = "Balanced - good accuracy at lower cost",
            Provider = CloudTranscriptionProvider.OpenAI,
            PricePerMinute = 0.003m,
            IsPopular = true
        },
        new CloudTranscriptionModel
        {
            Id = "whisper-1",
            DisplayName = "Whisper-1",
            Description = "Classic Whisper model - cost-effective, reliable",
            Provider = CloudTranscriptionProvider.OpenAI,
            PricePerMinute = 0.006m,
            IsPopular = true
        }
    };

    // =========================================================================
    // GROQ MODELS
    // Groq uses OpenAI-compatible API with Whisper Large V3 models.
    // Very fast inference due to Groq's LPU hardware.
    // =========================================================================

    /// <summary>
    /// Groq Whisper models.
    /// - whisper-large-v3-turbo: Faster, optimized for speed
    /// - whisper-large-v3: Full model, highest accuracy
    /// </summary>
    public static readonly CloudTranscriptionModel[] Groq = new[]
    {
        new CloudTranscriptionModel
        {
            Id = "whisper-large-v3-turbo",
            DisplayName = "Whisper Large V3 Turbo",
            Description = "Fastest - optimized for speed with good accuracy",
            Provider = CloudTranscriptionProvider.Groq,
            IsPopular = true
        },
        new CloudTranscriptionModel
        {
            Id = "whisper-large-v3",
            DisplayName = "Whisper Large V3",
            Description = "Full model - highest accuracy, slower",
            Provider = CloudTranscriptionProvider.Groq
        }
    };

    // =========================================================================
    // DEEPGRAM MODELS
    // Deepgram Nova models offer best-in-class accuracy for speech-to-text.
    // Also includes Whisper variants for compatibility.
    // =========================================================================

    /// <summary>
    /// Deepgram transcription models.
    /// Mirrors macOS domain-specific model IDs so modes and backups round-trip cleanly.
    /// </summary>
    public static readonly CloudTranscriptionModel[] Deepgram = new[]
    {
        new CloudTranscriptionModel
        {
            Id = "nova-3-general",
            DisplayName = "Nova 3 General",
            Description = "The leading model for general transcription from Deepgram",
            Provider = CloudTranscriptionProvider.Deepgram,
            PricePerMinute = 0.0043m,
            IsPopular = true
        },
        new CloudTranscriptionModel
        {
            Id = "nova-3-medical",
            DisplayName = "Nova 3 Medical",
            Description = "Optimized audio with medical oriented vocabulary",
            Provider = CloudTranscriptionProvider.Deepgram,
            PricePerMinute = 0.0043m
        },
        new CloudTranscriptionModel
        {
            Id = "nova-2-general",
            DisplayName = "Nova 2 General",
            Description = "General-purpose transcription with high accuracy for diverse audio sources",
            Provider = CloudTranscriptionProvider.Deepgram,
            PricePerMinute = 0.0043m
        },
        new CloudTranscriptionModel
        {
            Id = "nova-2-medical",
            DisplayName = "Nova-2 Medical",
            Description = "Medical domain vocabulary for clinical conversations and healthcare settings",
            Provider = CloudTranscriptionProvider.Deepgram,
            PricePerMinute = 0.0043m,
            IsPopular = true
        }
    };

    // =========================================================================
    // ASSEMBLYAI MODELS
    // AssemblyAI offers high-accuracy transcription with async processing.
    // =========================================================================

    /// <summary>
    /// AssemblyAI transcription models.
    /// - universal-2: Multi-language model supporting 99 languages (default, keyterms_prompt ≤ 200 terms)
    /// - universal-3-pro: Highest-accuracy model, 6 languages (EN/ES/DE/FR/PT/IT), keyterms_prompt ≤ 1000 terms
    /// Legacy IDs "universal" and "slam-1" are resolved to these via GetById for backward compatibility.
    /// </summary>
    public static readonly CloudTranscriptionModel[] AssemblyAI = new[]
    {
        new CloudTranscriptionModel
        {
            Id = "universal-2",
            DisplayName = "Universal-2",
            Description = "Multi-language model supporting 99 languages with automatic detection. Keyterms prompting up to 200 terms.",
            Provider = CloudTranscriptionProvider.AssemblyAI,
            PricePerMinute = 0.0025m,  // $0.15/hour
            IsPopular = true
        },
        new CloudTranscriptionModel
        {
            Id = "universal-3-pro",
            DisplayName = "Universal-3 Pro",
            Description = "Highest-accuracy model. English, Spanish, German, French, Portuguese, Italian. Keyterms prompting up to 1000 terms.",
            Provider = CloudTranscriptionProvider.AssemblyAI,
            PricePerMinute = 0.0035m,  // $0.21/hour
            IsPopular = true
        },
        new CloudTranscriptionModel
        {
            Id = "universal-2-medical",
            DisplayName = "Universal-2 (Medical)",
            Description = "Universal-2 with Medical Mode add-on for clinical vocabulary. EN/ES/DE/FR only. Medical Mode is billed as a separate add-on on top of Universal-2 pricing.",
            Provider = CloudTranscriptionProvider.AssemblyAI,
            IsPopular = true,
            PricePerMinute = 0.0025m  // $0.15/hour base — medical add-on billed separately
        },
        new CloudTranscriptionModel
        {
            Id = "universal-3-pro-medical",
            DisplayName = "Universal-3 Pro (Medical)",
            Description = "Universal-3 Pro with Medical Mode add-on for clinical vocabulary. EN/ES/DE/FR only. Medical Mode is billed as a separate add-on on top of Universal-3 Pro pricing.",
            Provider = CloudTranscriptionProvider.AssemblyAI,
            IsPopular = true,
            PricePerMinute = 0.0035m  // $0.21/hour base — medical add-on billed separately
        }
    };

    // =========================================================================
    // ELEVENLABS MODELS
    // ElevenLabs Scribe for speech-to-text.
    // Scribe V2 supports custom vocabulary via keyterms (up to 100 terms).
    // Scribe V1 does NOT support custom vocabulary.
    // =========================================================================

    /// <summary>
    /// ElevenLabs transcription models.
    /// - scribe_v2: Latest model with keyterm prompting support (default)
    /// - scribe_v1: Original flagship model (no vocabulary support)
    /// </summary>
    public static readonly CloudTranscriptionModel[] ElevenLabs = new[]
    {
        new CloudTranscriptionModel
        {
            Id = "scribe_v1",
            DisplayName = "Scribe V1",
            Description = "Original flagship model (no vocabulary support)",
            Provider = CloudTranscriptionProvider.ElevenLabs
        },
        new CloudTranscriptionModel
        {
            Id = "scribe_v2",
            DisplayName = "Scribe V2",
            Description = "Latest model with keyterm prompting (up to 100 terms)",
            Provider = CloudTranscriptionProvider.ElevenLabs,
            IsPopular = true
        }
    };

    // =========================================================================
    // MISTRAL MODELS
    // Mistral Voxtral for audio transcription.
    // NOTE: Does NOT support custom vocabulary.
    // =========================================================================

    /// <summary>
    /// Mistral transcription models.
    /// - voxtral-mini-latest: Latest Voxtral Mini model
    /// NOTE: Custom vocabulary is NOT supported.
    /// </summary>
    public static readonly CloudTranscriptionModel[] Mistral = new[]
    {
        new CloudTranscriptionModel
        {
            Id = "voxtral-mini-latest",
            DisplayName = "Voxtral Mini",
            Description = "Audio transcription (no vocabulary support)",
            Provider = CloudTranscriptionProvider.Mistral,
            IsPopular = true
        }
    };

    // =========================================================================
    // SONIOX MODELS
    // Async/file transcription only.
    // =========================================================================

    /// <summary>
    /// Soniox transcription models.
    /// - stt-async-v4: Current async transcription model
    /// </summary>
    public static readonly CloudTranscriptionModel[] Soniox = new[]
    {
        new CloudTranscriptionModel
        {
            Id = "stt-async-v4",
            DisplayName = "STT Async v4",
            Description = "Async batch transcription with 60+ supported languages",
            Provider = CloudTranscriptionProvider.Soniox,
            IsPopular = true
        }
    };

    // =========================================================================
    // GEMINI MODELS
    // Google Gemini multimodal models with native audio understanding.
    // Uses inline generateContent for small requests and Files API for larger audio.
    // NOTE: Pricing is token-based, not per-minute.
    // =========================================================================

    /// <summary>
    /// Google Gemini transcription models.
    /// - gemini-2.5-flash: Fast, cost-effective (default)
    /// - gemini-2.5-flash-lite: Cheapest option
    /// - gemini-2.5-pro: Highest quality
    /// - gemini-2.0-flash: Previous generation
    /// - gemini-3.x: Preview models
    /// </summary>
    public static readonly CloudTranscriptionModel[] Gemini = new[]
    {
        new CloudTranscriptionModel
        {
            Id = "gemini-2.5-flash",
            DisplayName = "Gemini 2.5 Flash",
            Description = "Fast and cost-effective with strong accuracy",
            Provider = CloudTranscriptionProvider.Gemini,
            IsPopular = true
        },
        new CloudTranscriptionModel
        {
            Id = "gemini-2.5-flash-lite",
            DisplayName = "Gemini 2.5 Flash Lite",
            Description = "Cheapest option - good for high-volume use",
            Provider = CloudTranscriptionProvider.Gemini,
            IsPopular = true
        },
        new CloudTranscriptionModel
        {
            Id = "gemini-2.5-pro",
            DisplayName = "Gemini 2.5 Pro",
            Description = "Highest quality - best accuracy for complex audio",
            Provider = CloudTranscriptionProvider.Gemini,
            IsPopular = true
        },
        new CloudTranscriptionModel
        {
            Id = "gemini-2.0-flash",
            DisplayName = "Gemini 2.0 Flash",
            Description = "Previous generation - stable and reliable",
            Provider = CloudTranscriptionProvider.Gemini
        },
        new CloudTranscriptionModel
        {
            Id = "gemini-3.1-flash-lite-preview",
            DisplayName = "Gemini 3.1 Flash Lite (Preview)",
            Description = "Next-gen lightweight model (preview)",
            Provider = CloudTranscriptionProvider.Gemini
        },
        new CloudTranscriptionModel
        {
            Id = "gemini-3-flash-preview",
            DisplayName = "Gemini 3 Flash (Preview)",
            Description = "Next-gen flash model (preview)",
            Provider = CloudTranscriptionProvider.Gemini
        },
        new CloudTranscriptionModel
        {
            Id = "gemini-3.1-pro-preview",
            DisplayName = "Gemini 3.1 Pro (Preview)",
            Description = "Next-gen pro model - highest quality (preview)",
            Provider = CloudTranscriptionProvider.Gemini
        }
    };

    // =========================================================================
    // HYPERWHISPER CLOUD
    // No model rows in the library — HyperWhisper Cloud is a routing service,
    // not a model. The sentinel below keeps GetById / GetDefault stable for
    // call sites that still resolve `(HyperWhisperCloud, "default")` from
    // persisted modes (see Mode.CloudTranscriptionModel).
    // =========================================================================

    private static readonly CloudTranscriptionModel HyperWhisperCloudSentinel = new()
    {
        Id = "default",
        DisplayName = "Default",
        Description = "HyperWhisper Cloud (Deepgram Nova-3 default tier)",
        Provider = CloudTranscriptionProvider.HyperWhisperCloud,
        IsPopular = true
    };

    // =========================================================================
    // GROK MODELS
    // xAI Grok speech-to-text. Single implicit model — no `model` parameter
    // is sent over the wire; the placeholder entry exists only so registry
    // lookups don't return null.
    // =========================================================================

    /// <summary>
    /// xAI Grok transcription. The API has no model parameter, so this is a
    /// single placeholder. The model dropdown is hidden in the UI for Grok.
    /// </summary>
    public static readonly CloudTranscriptionModel[] Grok = new[]
    {
        new CloudTranscriptionModel
        {
            Id = "",
            DisplayName = "Default",
            Description = "xAI Grok speech-to-text (single implicit model)",
            Provider = CloudTranscriptionProvider.Grok,
            PricePerMinute = 0.0016667m,
            IsPopular = true
        }
    };

    // =========================================================================
    // MICROSOFT AZURE SPEECH (HyperWhisper Cloud only)
    // =========================================================================

    /// <summary>
    /// Microsoft MAI-Transcribe 1.5 via Azure Speech / Foundry. Routed through
    /// the Fly /transcribe service with X-STT-Provider: azure-mai.
    /// </summary>
    public static readonly CloudTranscriptionModel[] MicrosoftAzureSpeech = new[]
    {
        new CloudTranscriptionModel
        {
            Id = "mai-transcribe-1.5",
            DisplayName = "MAI-Transcribe 1.5 (Preview)",
            Description = "Microsoft's 43-language transcription model with contextual biasing.",
            Provider = CloudTranscriptionProvider.MicrosoftAzureSpeech,
            PricePerMinute = 0.006m,
            IsPopular = true
        }
    };

    // =========================================================================
    // GOOGLE CLOUD SPEECH (HyperWhisper Cloud only)
    // =========================================================================

    /// <summary>
    /// Google Cloud Speech-to-Text V2 Chirp 3. Routed through the Fly
    /// /transcribe service with X-STT-Provider: google-chirp.
    /// </summary>
    public static readonly CloudTranscriptionModel[] GoogleSpeech = new[]
    {
        new CloudTranscriptionModel
        {
            Id = "chirp_3",
            DisplayName = "Chirp 3",
            Description = "Google's latest multilingual speech model with phrase adaptation.",
            Provider = CloudTranscriptionProvider.GoogleSpeech,
            PricePerMinute = 0.016m,
            IsPopular = true
        }
    };

    // =========================================================================
    // ALL MODELS
    // =========================================================================

    /// <summary>
    /// All available cloud transcription models across all providers.
    /// </summary>
    public static readonly CloudTranscriptionModel[] All =
        OpenAI
            .Concat(Groq)
            .Concat(Deepgram)
            .Concat(AssemblyAI)
            .Concat(ElevenLabs)
            .Concat(Mistral)
            .Concat(Soniox)
            .Concat(Gemini)
            .Concat(Grok)
            .Concat(MicrosoftAzureSpeech)
            .Concat(GoogleSpeech)
            .ToArray();

    /// <summary>
    /// Gets models for a specific provider.
    /// </summary>
    public static CloudTranscriptionModel[] GetModelsForProvider(CloudTranscriptionProvider provider) =>
        provider switch
        {
            CloudTranscriptionProvider.OpenAI => OpenAI,
            CloudTranscriptionProvider.Groq => Groq,
            CloudTranscriptionProvider.Deepgram => Deepgram,
            CloudTranscriptionProvider.AssemblyAI => AssemblyAI,
            CloudTranscriptionProvider.ElevenLabs => ElevenLabs,
            CloudTranscriptionProvider.Mistral => Mistral,
            CloudTranscriptionProvider.Soniox => Soniox,
            CloudTranscriptionProvider.Gemini => Gemini,
            CloudTranscriptionProvider.HyperWhisperCloud => new[] { HyperWhisperCloudSentinel },
            CloudTranscriptionProvider.Grok => Grok,
            CloudTranscriptionProvider.MicrosoftAzureSpeech => MicrosoftAzureSpeech,
            CloudTranscriptionProvider.GoogleSpeech => GoogleSpeech,
            _ => Array.Empty<CloudTranscriptionModel>()
        };

    /// <summary>
    /// Gets the curated default model list for a provider. Falls back to all provider models
    /// when no popular metadata exists so every provider remains selectable.
    /// </summary>
    public static CloudTranscriptionModel[] GetPopularModelsForProvider(CloudTranscriptionProvider provider)
    {
        var models = GetModelsForProvider(provider);
        var popular = models.Where(m => m.IsPopular).ToArray();
        return popular.Length > 0 ? popular : models;
    }

    /// <summary>
    /// Legacy AssemblyAI model IDs retired on 2026-05-11. Mapped transparently so existing
    /// Modes and imported backups keep working. "universal" → "universal-2" (same multilingual
    /// behavior) and "slam-1" → "universal-3-pro" (direct accuracy upgrade per AssemblyAI).
    /// </summary>
    private static readonly Dictionary<string, string> LegacyAssemblyAIAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "universal", "universal-2" },
            { "slam-1", "universal-3-pro" }
        };

    /// <summary>
    /// Legacy Windows Deepgram IDs used before the catalog mirrored macOS domain-specific IDs,
    /// plus the 25 IDs removed in the 2026-05 catalog cleanup. Removed IDs collapse to
    /// `nova-3-general` so existing modes, settings, and backups continue to resolve.
    /// </summary>
    private static readonly Dictionary<string, string> LegacyDeepgramAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Pre-cleanup short aliases. `enhanced` and `base` previously resolved to
            // their `-general` siblings, but those were removed in the cleanup, so they
            // now collapse straight to Nova 3 General.
            { "nova-3", "nova-3-general" },
            { "nova-2", "nova-2-general" },
            { "enhanced", "nova-3-general" },
            { "base", "nova-3-general" },
            // 2026-05 cleanup — every removed ID maps to Nova 3 General.
            { "nova-2-meeting", "nova-3-general" },
            { "nova-2-phonecall", "nova-3-general" },
            { "nova-2-voicemail", "nova-3-general" },
            { "nova-2-finance", "nova-3-general" },
            { "nova-2-conversationalai", "nova-3-general" },
            { "nova-2-automotive", "nova-3-general" },
            { "nova-2-video", "nova-3-general" },
            { "nova", "nova-3-general" },
            { "nova-phonecall", "nova-3-general" },
            { "enhanced-general", "nova-3-general" },
            { "enhanced-meeting", "nova-3-general" },
            { "enhanced-phonecall", "nova-3-general" },
            { "enhanced-finance", "nova-3-general" },
            { "base-general", "nova-3-general" },
            { "base-meeting", "nova-3-general" },
            { "base-phonecall", "nova-3-general" },
            { "base-voicemail", "nova-3-general" },
            { "base-finance", "nova-3-general" },
            { "base-conversationalai", "nova-3-general" },
            { "base-video", "nova-3-general" },
            { "whisper-tiny", "nova-3-general" },
            { "whisper-base", "nova-3-general" },
            { "whisper-small", "nova-3-general" },
            { "whisper-medium", "nova-3-general" },
            { "whisper-large", "nova-3-general" }
        };

    /// <summary>
    /// Resolve a legacy AssemblyAI model ID to its current equivalent. Non-AssemblyAI
    /// and already-current IDs pass through unchanged. AssemblyAI-scoped by design —
    /// do not add aliases for other providers here; give them their own resolver.
    /// </summary>
    public static string ResolveAssemblyAIModelAlias(string modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return modelId;
        return LegacyAssemblyAIAliases.TryGetValue(modelId, out var resolved) ? resolved : modelId;
    }

    /// <summary>
    /// Resolve a legacy Deepgram model ID to its current macOS-compatible equivalent.
    /// </summary>
    public static string ResolveDeepgramModelAlias(string modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return modelId;
        return LegacyDeepgramAliases.TryGetValue(modelId, out var resolved) ? resolved : modelId;
    }

    /// <summary>
    /// Resolve provider-specific model aliases before display, import, or request configuration.
    /// </summary>
    public static string ResolveModelAlias(string modelId, CloudTranscriptionProvider? provider = null)
    {
        if (string.IsNullOrEmpty(modelId)) return modelId;

        return provider switch
        {
            CloudTranscriptionProvider.AssemblyAI => ResolveAssemblyAIModelAlias(modelId),
            CloudTranscriptionProvider.Deepgram => ResolveDeepgramModelAlias(modelId),
            null => ResolveDeepgramModelAlias(ResolveAssemblyAIModelAlias(modelId)),
            _ => modelId
        };
    }

    /// <summary>
    /// Splits a (possibly medical) AssemblyAI model ID into the canonical
    /// <c>speech_model</c> value and a flag indicating whether Medical Mode
    /// is enabled. Medical Mode is encoded as a <c>-medical</c> suffix in
    /// the model ID — the suffix never goes over the wire; instead the
    /// caller adds <c>"domain": "medical-v1"</c> to the request body.
    /// Legacy aliases are resolved first.
    /// </summary>
    public static (string SpeechModel, bool Medical) GetAssemblyAIRequestParams(string modelId)
    {
        var resolved = ResolveAssemblyAIModelAlias(modelId);
        if (!string.IsNullOrEmpty(resolved) && resolved.EndsWith("-medical", StringComparison.Ordinal))
            return (resolved[..^"-medical".Length], true);
        return (resolved, false);
    }

    /// <summary>
    /// Gets a model by its ID, optionally scoped to a provider.
    /// </summary>
    public static CloudTranscriptionModel? GetById(string? modelId, CloudTranscriptionProvider? provider = null)
    {
        if (string.IsNullOrEmpty(modelId)) return null;

        var canonical = ResolveModelAlias(modelId, provider);

        var searchSet = provider.HasValue
            ? GetModelsForProvider(provider.Value)
            : All;

        return searchSet.FirstOrDefault(m => m.Id.Equals(canonical, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the default model for a provider.
    /// </summary>
    public static CloudTranscriptionModel? GetDefault(CloudTranscriptionProvider provider)
    {
        var defaultModelId = provider switch
        {
            CloudTranscriptionProvider.OpenAI => "whisper-1",
            CloudTranscriptionProvider.Groq => "whisper-large-v3-turbo",
            CloudTranscriptionProvider.Deepgram => "nova-3-general",
            CloudTranscriptionProvider.AssemblyAI => "universal-2",
            CloudTranscriptionProvider.ElevenLabs => "scribe_v2",
            CloudTranscriptionProvider.Mistral => "voxtral-mini-latest",
            CloudTranscriptionProvider.Soniox => "stt-async-v4",
            CloudTranscriptionProvider.Gemini => "gemini-2.5-flash",
            CloudTranscriptionProvider.HyperWhisperCloud => HyperWhisperCloudSentinel.Id,
            CloudTranscriptionProvider.Grok => "",
            CloudTranscriptionProvider.MicrosoftAzureSpeech => "mai-transcribe-1.5",
            CloudTranscriptionProvider.GoogleSpeech => "chirp_3",
            _ => null
        };

        return GetById(defaultModelId, provider) ?? GetModelsForProvider(provider).FirstOrDefault();
    }
}
