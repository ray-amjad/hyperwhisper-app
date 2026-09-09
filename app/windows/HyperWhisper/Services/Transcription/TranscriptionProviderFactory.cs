// TRANSCRIPTION PROVIDER FACTORY
// Owns and lazily creates all cloud transcription provider instances.
// Centralizes API key retrieval and provider configuration.
//
// DESIGN:
// - Lazy<T> ensures providers only created when first needed
// - Single point of configuration (API key lookup)
// - Proper disposal of all HttpClient resources
// - EXCEPT the HW-Cloud-routed providers (Azure MAI, Google Chirp), which are
//   built fresh per request. They own no HttpClient, and their per-request
//   model id must not be shared mutable state — see GetConfiguredCloudProvider.
//
// NOTE: Does NOT own TranscriptionService (local) - that requires
// model loading and has different lifecycle management.

using HyperWhisper.Data.Entities;
using HyperWhisper.Models;

namespace HyperWhisper.Services.Transcription;

/// <summary>
/// Factory for creating and configuring transcription providers.
/// Uses lazy initialization to create providers only when first accessed.
/// </summary>
public class TranscriptionProviderFactory : IDisposable
{
    // =========================================================================
    // LAZY CLOUD PROVIDER INSTANCES
    // =========================================================================

    private readonly Lazy<OpenAIWhisperService> _openAI;
    private readonly Lazy<GroqWhisperService> _groq;
    private readonly Lazy<DeepgramService> _deepgram;
    private readonly Lazy<AssemblyAIService> _assemblyAI;
    private readonly Lazy<ElevenLabsService> _elevenLabs;
    private readonly Lazy<MistralService> _mistral;
    private readonly Lazy<SonioxService> _soniox;
    private readonly Lazy<GeminiTranscriptionService> _gemini;
    private readonly Lazy<GeminiTranscribeService> _geminiTranscribe;
    private readonly Lazy<MetaMuseService> _meta;
    private readonly Lazy<HyperWhisperCloudService> _hyperWhisperCloud;
    private readonly Lazy<GrokSttService> _grok;

    // NO Lazy<> for the HW-Cloud-routed services (Azure MAI, Google Chirp).
    // They are built per request instead — see GetConfiguredCloudProvider.

    private bool _disposed;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public TranscriptionProviderFactory()
    {
        // Lazy initialization - providers created only when first accessed
        _openAI = new Lazy<OpenAIWhisperService>(() => new OpenAIWhisperService());
        _groq = new Lazy<GroqWhisperService>(() => new GroqWhisperService());
        _deepgram = new Lazy<DeepgramService>(() => new DeepgramService());
        _assemblyAI = new Lazy<AssemblyAIService>(() => new AssemblyAIService());
        _elevenLabs = new Lazy<ElevenLabsService>(() => new ElevenLabsService());
        _mistral = new Lazy<MistralService>(() => new MistralService());
        _soniox = new Lazy<SonioxService>(() => new SonioxService());
        _gemini = new Lazy<GeminiTranscriptionService>(() => new GeminiTranscriptionService());
        _geminiTranscribe = new Lazy<GeminiTranscribeService>(() => new GeminiTranscribeService());
        _meta = new Lazy<MetaMuseService>(() => new MetaMuseService());
        _hyperWhisperCloud = new Lazy<HyperWhisperCloudService>(() => new HyperWhisperCloudService());
        _grok = new Lazy<GrokSttService>(() => new GrokSttService());

        LoggingService.Debug("TranscriptionProviderFactory: Initialized (providers will be created on first use)");
    }

    // =========================================================================
    // PUBLIC API
    // =========================================================================

    /// <summary>
    /// Gets a configured cloud provider ready for transcription.
    /// Provider is lazily created on first access and cached for reuse.
    /// </summary>
    /// <param name="providerType">The cloud provider to get.</param>
    /// <param name="modelId">Model ID to configure (uses provider default if null).</param>
    /// <returns>Configured ITranscriptionProvider ready to use.</returns>
    /// <exception cref="TranscriptionException">If API key is missing for providers that require it.</exception>
    public ITranscriptionProvider GetConfiguredCloudProvider(
        CloudTranscriptionProvider providerType,
        string? modelId = null)
    {
        // Get API key (validates for providers that require it)
        string? apiKey = GetApiKeyForProvider(providerType);

        if (providerType.RequiresApiKey() && string.IsNullOrEmpty(apiKey))
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.ApiKeyMissing,
                $"API key not configured for {providerType.GetDisplayName()}",
                providerType.GetDisplayName());
        }

        // Get default model if not specified
        var effectiveModelId = modelId
            ?? CloudTranscriptionModels.GetDefault(providerType)?.Id
            ?? GetFallbackModelId(providerType);

        // Configure and return the provider
        return providerType switch
        {
            CloudTranscriptionProvider.OpenAI => ConfigureAndReturn(_openAI.Value, apiKey!, effectiveModelId),
            CloudTranscriptionProvider.Groq => ConfigureAndReturn(_groq.Value, apiKey!, effectiveModelId),
            CloudTranscriptionProvider.Deepgram => ConfigureAndReturn(_deepgram.Value, apiKey!, effectiveModelId),
            CloudTranscriptionProvider.AssemblyAI => ConfigureAndReturn(_assemblyAI.Value, apiKey!, effectiveModelId),
            CloudTranscriptionProvider.ElevenLabs => ConfigureAndReturn(_elevenLabs.Value, apiKey!, effectiveModelId),
            CloudTranscriptionProvider.Mistral => ConfigureAndReturn(_mistral.Value, apiKey!, effectiveModelId),
            CloudTranscriptionProvider.Soniox => ConfigureAndReturn(_soniox.Value, apiKey!, effectiveModelId),
            CloudTranscriptionProvider.Gemini => ConfigureAndReturn(_gemini.Value, apiKey!, effectiveModelId),
            CloudTranscriptionProvider.GeminiTranscribe => ConfigureAndReturn(_geminiTranscribe.Value, apiKey!, effectiveModelId),
            CloudTranscriptionProvider.Meta => ConfigureAndReturn(_meta.Value, apiKey!, effectiveModelId),
            CloudTranscriptionProvider.HyperWhisperCloud => ConfigureHyperWhisperCloud(_hyperWhisperCloud.Value),
            CloudTranscriptionProvider.Grok => ConfigureAndReturn(_grok.Value, apiKey!, effectiveModelId),
            // HW-Cloud-routed providers — no API key, but the selected model
            // still has to reach the request: it travels as X-STT-Model, which
            // the routed client only sends when the service resolves one. Azure
            // MAI serves two models at two prices, so an unconfigured service
            // silently ran the backend default instead of the user's choice.
            //
            // BUILT PER REQUEST, NOT CACHED. Every other arm here hands back a
            // cached Lazy<T> instance and mutates it (Configure sets the key and
            // the model). Doing that for a routed provider is a billing bug: this
            // factory is owned by the process-wide TranscriptionRuntime.Orchestrator
            // singleton and nothing locks, so two overlapping transcriptions
            // overwrite each other's model between here and the send. These two
            // services hold no HttpClient (the routed client's is static and
            // shared) and no other state, so a fresh instance costs an allocation
            // and makes the model immutable per-call — the same property macOS
            // gets from AzureMAIProvider.routedModelId and shared .NET from
            // RoutedModelFor. The BYOK arms above have the same shared-mutable
            // shape; that is pre-existing and out of scope here.
            CloudTranscriptionProvider.MicrosoftAzureSpeech => new AzureMAITranscriptionService(effectiveModelId),
            CloudTranscriptionProvider.GoogleSpeech => new GoogleChirpTranscriptionService(effectiveModelId),
            _ => throw new ArgumentException($"Unknown cloud provider: {providerType}")
        };
    }

    /// <summary>
    /// Gets display name for cloud provider.
    /// </summary>
    public static string GetProviderDisplayName(CloudTranscriptionProvider provider, string? modelId)
    {
        // Return only provider name (consistent with macOS app)
        // Model name is configuration detail, not relevant for history display
        return provider.GetDisplayName();
    }

    /// <summary>
    /// Returns true if the given mode resolves to HyperWhisper Cloud at /transcribe time.
    /// </summary>
    public static bool IsHyperWhisperCloudActive(Mode? mode)
    {
        if (mode?.ProviderType?.Equals("cloud", StringComparison.OrdinalIgnoreCase) != true) return false;
        return CloudTranscriptionProviderExtensions.FromIdentifier(mode.CloudProvider)
            == CloudTranscriptionProvider.HyperWhisperCloud;
    }

    /// <summary>
    /// Pre-warms the HyperWhisper Cloud connection if — and only if — cloud is
    /// currently the active transcription provider for the given mode. Safe to
    /// call from any hotkey-down path. Fire-and-forget.
    /// </summary>
    public void PrewarmCloudConnectionIfActive(Mode? mode)
    {
        if (!IsHyperWhisperCloudActive(mode)) return;
        _hyperWhisperCloud.Value.PrewarmConnection();
    }

    /// <summary>
    /// Variant that bypasses the 60s warmup debounce. Used by the foreground
    /// keepalive ticker so its ~45s cadence isn't absorbed into the debounce.
    /// Hotkey paths must keep using <see cref="PrewarmCloudConnectionIfActive"/>.
    /// </summary>
    public void PrewarmCloudConnectionIfActiveForced(Mode? mode)
    {
        if (!IsHyperWhisperCloudActive(mode)) return;
        _hyperWhisperCloud.Value.PrewarmConnectionForced();
    }

    // =========================================================================
    // API KEY RETRIEVAL (Extracted from MainViewModel/HistoryViewModel)
    // =========================================================================

    /// <summary>
    /// Gets the API key for a cloud transcription provider.
    /// Routes to PostProcessingProvider for shared keys (OpenAI, Groq, Gemini, Grok)
    /// or TranscriptionApiKeyType for providers without post-processing support.
    /// </summary>
    public static string? GetApiKeyForProvider(CloudTranscriptionProvider provider)
    {
        return provider switch
        {
            // Shared keys with post-processing
            CloudTranscriptionProvider.OpenAI => ApiKeyService.Instance.GetApiKey(PostProcessingProvider.OpenAI),
            CloudTranscriptionProvider.Groq => ApiKeyService.Instance.GetApiKey(PostProcessingProvider.Groq),
            CloudTranscriptionProvider.Gemini => ApiKeyService.Instance.GetApiKey(PostProcessingProvider.Gemini),
            CloudTranscriptionProvider.Grok => ApiKeyService.Instance.GetApiKey(PostProcessingProvider.Grok),

            // Transcription-only providers
            CloudTranscriptionProvider.Deepgram => ApiKeyService.Instance.GetApiKey(TranscriptionApiKeyType.Deepgram),
            CloudTranscriptionProvider.AssemblyAI => ApiKeyService.Instance.GetApiKey(TranscriptionApiKeyType.AssemblyAI),
            CloudTranscriptionProvider.ElevenLabs => ApiKeyService.Instance.GetApiKey(TranscriptionApiKeyType.ElevenLabs),
            CloudTranscriptionProvider.Mistral => ApiKeyService.Instance.GetApiKey(TranscriptionApiKeyType.Mistral),
            CloudTranscriptionProvider.Soniox => ApiKeyService.Instance.GetApiKey(TranscriptionApiKeyType.Soniox),
            // Own key slot — deliberately NOT PostProcessingProvider.Gemini.
            CloudTranscriptionProvider.GeminiTranscribe => ApiKeyService.Instance.GetApiKey(TranscriptionApiKeyType.GeminiTranscribe),
            CloudTranscriptionProvider.Meta => ApiKeyService.Instance.GetApiKey(TranscriptionApiKeyType.Meta),

            // HyperWhisper-Cloud-routed providers don't need an API key
            CloudTranscriptionProvider.HyperWhisperCloud => null,
            CloudTranscriptionProvider.MicrosoftAzureSpeech => null,
            CloudTranscriptionProvider.GoogleSpeech => null,

            _ => null
        };
    }

    // =========================================================================
    // PRIVATE HELPERS
    // =========================================================================

    // One overload covers every API-key provider: they all derive from
    // ApiKeyTranscriptionServiceBase, which declares Configure.
    private static ITranscriptionProvider ConfigureAndReturn(ApiKeyTranscriptionServiceBase service, string apiKey, string modelId)
    {
        service.Configure(apiKey, modelId);
        return service;
    }

    private static ITranscriptionProvider ConfigureHyperWhisperCloud(HyperWhisperCloudService service)
    {
        // No configuration needed - service gets fresh credentials from LicenseManager
        // on each request. This ensures license deactivation is immediately reflected.
        return service;
    }

    private static string GetFallbackModelId(CloudTranscriptionProvider provider)
    {
        return provider switch
        {
            CloudTranscriptionProvider.OpenAI => "whisper-1",
            CloudTranscriptionProvider.Groq => "whisper-large-v3-turbo",
            CloudTranscriptionProvider.Deepgram => "nova-3-general",
            CloudTranscriptionProvider.AssemblyAI => "universal-3-5-pro",
            CloudTranscriptionProvider.ElevenLabs => "scribe_v2",
            CloudTranscriptionProvider.Mistral => "voxtral-mini-latest",
            CloudTranscriptionProvider.Soniox => "stt-async-v5",
            CloudTranscriptionProvider.Gemini => "gemini-2.5-flash",
            CloudTranscriptionProvider.GeminiTranscribe => "gemini-3.5-transcribe",
            CloudTranscriptionProvider.Meta => "muse-voice-transcribe-1.0",
            CloudTranscriptionProvider.HyperWhisperCloud => "",
            CloudTranscriptionProvider.Grok => "",
            CloudTranscriptionProvider.MicrosoftAzureSpeech => "mai-transcribe-2",
            CloudTranscriptionProvider.GoogleSpeech => "chirp_3",
            _ => "whisper-1"
        };
    }

    // =========================================================================
    // DISPOSAL
    // =========================================================================

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Only dispose providers that were actually created
        SafeDispose(_openAI);
        SafeDispose(_groq);
        SafeDispose(_deepgram);
        SafeDispose(_assemblyAI);
        SafeDispose(_elevenLabs);
        SafeDispose(_mistral);
        SafeDispose(_soniox);
        SafeDispose(_gemini);
        SafeDispose(_geminiTranscribe);
        SafeDispose(_meta);
        SafeDispose(_hyperWhisperCloud);
        SafeDispose(_grok);
        // No SafeDispose for the routed services: they are not cached here, and
        // their Dispose is a no-op (the routed HttpClient is process-wide).

        LoggingService.Debug("TranscriptionProviderFactory: Disposed");
        GC.SuppressFinalize(this);
    }

    private static void SafeDispose<T>(Lazy<T> lazy) where T : IDisposable
    {
        if (lazy.IsValueCreated)
        {
            try { lazy.Value.Dispose(); }
            catch (Exception ex) { LoggingService.Warn($"Dispose failed for {typeof(T).Name}: {ex.Message}"); }
        }
    }
}
