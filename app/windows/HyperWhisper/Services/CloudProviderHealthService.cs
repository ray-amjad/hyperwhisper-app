// CLOUD PROVIDER HEALTH SERVICE
// Manages health checks for cloud transcription and post-processing providers.
// Validates API keys by making lightweight requests to provider endpoints.
//
// FEATURES:
// - Cached health status with 60-second TTL
// - Debounced validation (500ms delay) to avoid rapid API calls during typing
// - Status events for UI badge updates
// - Retry logic with exponential backoff
//
// HEALTH CHECK ENDPOINTS:
// | Provider    | Endpoint                        | Auth           |
// |-------------|---------------------------------|----------------|
// | OpenAI      | GET /v1/models                  | Bearer         |
// | Groq        | GET /openai/v1/models           | Bearer         |
// | Deepgram    | GET /v1/projects                | Token          |
// | AssemblyAI  | GET /v2/transcript?limit=1      | Authorization  |
// | ElevenLabs  | GET /v1/models                  | xi-api-key     |
// | Mistral     | GET /v1/models                  | Bearer         |
// | Anthropic   | GET /v1/models                  | x-api-key      |
// | Gemini      | GET /v1beta/models?key={key}    | Query param    |
// | Grok/xAI    | GET /v1/models                  | Bearer         |

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Timers;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;
// Rust shared-core binding (BuildHealthRequest / ParseHealthResponse /
// HwProviderHealth). Used by the transcription health probe only.
using uniffi.hyperwhisper_core;
using HttpMethod = System.Net.Http.HttpMethod;

namespace HyperWhisper.Services;

/// <summary>
/// Manages health checks for cloud providers to validate API keys.
/// Thread-safe singleton with caching and debouncing.
/// </summary>
public class CloudProviderHealthService : IDisposable
{
    // =========================================================================
    // SINGLETON
    // =========================================================================

    private static CloudProviderHealthService? _instance;
    private static readonly object _lock = new();

    /// <summary>Thread-safe singleton instance.</summary>
    public static CloudProviderHealthService Instance
    {
        get
        {
            lock (_lock)
            {
                return _instance ??= new CloudProviderHealthService();
            }
        }
    }

    // =========================================================================
    // CONSTANTS
    // =========================================================================

    private const int CacheTtlSeconds = 60;
    private const int DebounceDelayMs = 500;
    private const int RequestTimeoutSeconds = 10;

    // Health check endpoints
    private static readonly Dictionary<CloudTranscriptionProvider, (string Url, string AuthScheme)> TranscriptionEndpoints = new()
    {
        { CloudTranscriptionProvider.OpenAI, ("https://api.openai.com/v1/models", "Bearer") },
        { CloudTranscriptionProvider.Groq, ("https://api.groq.com/openai/v1/models", "Bearer") },
        { CloudTranscriptionProvider.Deepgram, ("https://api.deepgram.com/v1/projects", "Token") },
        { CloudTranscriptionProvider.AssemblyAI, ("https://api.assemblyai.com/v2/transcript?limit=1", "Direct") },
        { CloudTranscriptionProvider.ElevenLabs, ("https://api.elevenlabs.io/v1/models", "xi-api-key") },
        { CloudTranscriptionProvider.Mistral, ("https://api.mistral.ai/v1/models", "Bearer") },
        { CloudTranscriptionProvider.Soniox, ("https://api.soniox.com/v1/models", "Bearer") },
        { CloudTranscriptionProvider.Gemini, ("https://generativelanguage.googleapis.com/v1beta/models", "Query") },
        { CloudTranscriptionProvider.Grok, ("https://api.x.ai/v1/models", "Bearer") }
    };

    private static readonly Dictionary<PostProcessingProvider, (string Url, string AuthScheme)> PostProcessingEndpoints = new()
    {
        { PostProcessingProvider.OpenAI, ("https://api.openai.com/v1/models", "Bearer") },
        { PostProcessingProvider.Anthropic, ("https://api.anthropic.com/v1/models", "x-api-key") },
        { PostProcessingProvider.Groq, ("https://api.groq.com/openai/v1/models", "Bearer") },
        { PostProcessingProvider.Grok, ("https://api.x.ai/v1/models", "Bearer") },
        { PostProcessingProvider.Gemini, ("https://generativelanguage.googleapis.com/v1beta/models", "Query") },
        { PostProcessingProvider.Cerebras, ("https://api.cerebras.ai/v1/models", "Bearer") },
        { PostProcessingProvider.Mistral, ("https://api.mistral.ai/v1/models", "Bearer") }
    };

    // =========================================================================
    // STATE
    // =========================================================================

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, (ProviderHealth Status, DateTime CachedAt)> _cache = new();
    private readonly ConcurrentDictionary<string, System.Timers.Timer> _debounceTimers = new();
    private bool _disposed;

    // =========================================================================
    // EVENTS
    // =========================================================================

    /// <summary>
    /// Fired when a transcription provider's health status changes.
    /// </summary>
    public event EventHandler<CloudTranscriptionProvider>? TranscriptionProviderStatusChanged;

    /// <summary>
    /// Fired when a post-processing provider's health status changes.
    /// </summary>
    public event EventHandler<PostProcessingProvider>? PostProcessingProviderStatusChanged;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    private CloudProviderHealthService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds)
        };
    }

    // =========================================================================
    // PUBLIC API - TRANSCRIPTION PROVIDERS
    // =========================================================================

    /// <summary>
    /// Gets the cached health status for a transcription provider.
    /// Returns Unknown if not cached.
    /// </summary>
    public ProviderHealth GetStatus(CloudTranscriptionProvider provider)
    {
        var key = $"transcription:{provider}";
        if (_cache.TryGetValue(key, out var cached))
        {
            if (DateTime.UtcNow - cached.CachedAt < TimeSpan.FromSeconds(CacheTtlSeconds))
            {
                return cached.Status;
            }
        }
        return ProviderHealth.Unknown;
    }

    /// <summary>
    /// Refreshes the health status for a transcription provider.
    /// </summary>
    /// <param name="provider">The provider to check.</param>
    /// <param name="force">If true, ignores cache and always makes a request.</param>
    public async Task<ProviderHealth> RefreshAsync(CloudTranscriptionProvider provider, bool force = false)
    {
        var key = $"transcription:{provider}";

        // Check cache unless forced
        if (!force && _cache.TryGetValue(key, out var cached))
        {
            if (DateTime.UtcNow - cached.CachedAt < TimeSpan.FromSeconds(CacheTtlSeconds))
            {
                return cached.Status;
            }
        }

        // Get API key
        string? apiKey = GetTranscriptionApiKey(provider);
        if (string.IsNullOrEmpty(apiKey))
        {
            UpdateCache(key, ProviderHealth.Unknown);
            return ProviderHealth.Unknown;
        }

        // Update to checking status
        var previousStatus = GetStatus(provider);
        UpdateCache(key, ProviderHealth.Checking);
        TranscriptionProviderStatusChanged?.Invoke(this, provider);

        // Perform health check
        var status = await PerformTranscriptionHealthCheckAsync(provider, apiKey);
        UpdateCache(key, status);

        // Notify if changed
        if (status != previousStatus)
        {
            TranscriptionProviderStatusChanged?.Invoke(this, provider);
        }

        return status;
    }

    /// <summary>
    /// Registers an API key change with debounced refresh.
    /// </summary>
    public void RegisterApiKeyChange(CloudTranscriptionProvider provider, string? newValue)
    {
        var key = $"transcription:{provider}";

        // Cancel existing debounce timer
        if (_debounceTimers.TryRemove(key, out var existingTimer))
        {
            existingTimer.Stop();
            existingTimer.Dispose();
        }

        // If empty, set to unknown immediately
        if (string.IsNullOrEmpty(newValue))
        {
            UpdateCache(key, ProviderHealth.Unknown);
            TranscriptionProviderStatusChanged?.Invoke(this, provider);
            return;
        }

        // Create debounce timer
        var timer = new System.Timers.Timer(DebounceDelayMs);
        timer.Elapsed += async (s, e) =>
        {
            timer.Stop();
            _debounceTimers.TryRemove(key, out _);
            await RefreshAsync(provider, force: true);
            timer.Dispose();
        };
        timer.AutoReset = false;
        timer.Start();

        _debounceTimers[key] = timer;
    }

    // =========================================================================
    // PUBLIC API - POST-PROCESSING PROVIDERS
    // =========================================================================

    /// <summary>
    /// Gets the cached health status for a post-processing provider.
    /// </summary>
    public ProviderHealth GetStatus(PostProcessingProvider provider)
    {
        if (provider == PostProcessingProvider.None) return ProviderHealth.Unknown;

        var key = $"postprocessing:{provider}";
        if (_cache.TryGetValue(key, out var cached))
        {
            if (DateTime.UtcNow - cached.CachedAt < TimeSpan.FromSeconds(CacheTtlSeconds))
            {
                return cached.Status;
            }
        }
        return ProviderHealth.Unknown;
    }

    /// <summary>
    /// Refreshes the health status for a post-processing provider.
    /// </summary>
    public async Task<ProviderHealth> RefreshAsync(PostProcessingProvider provider, bool force = false)
    {
        if (provider == PostProcessingProvider.None) return ProviderHealth.Unknown;

        var key = $"postprocessing:{provider}";

        // Check cache unless forced
        if (!force && _cache.TryGetValue(key, out var cached))
        {
            if (DateTime.UtcNow - cached.CachedAt < TimeSpan.FromSeconds(CacheTtlSeconds))
            {
                return cached.Status;
            }
        }

        // Get API key
        var apiKey = ApiKeyService.Instance.GetApiKey(provider);
        if (string.IsNullOrEmpty(apiKey))
        {
            UpdateCache(key, ProviderHealth.Unknown);
            return ProviderHealth.Unknown;
        }

        // Update to checking status
        var previousStatus = GetStatus(provider);
        UpdateCache(key, ProviderHealth.Checking);
        PostProcessingProviderStatusChanged?.Invoke(this, provider);

        // Perform health check
        var status = await PerformPostProcessingHealthCheckAsync(provider, apiKey);
        UpdateCache(key, status);

        // Notify if changed
        if (status != previousStatus)
        {
            PostProcessingProviderStatusChanged?.Invoke(this, provider);
        }

        return status;
    }

    /// <summary>
    /// Registers an API key change with debounced refresh.
    /// </summary>
    public void RegisterApiKeyChange(PostProcessingProvider provider, string? newValue)
    {
        if (provider == PostProcessingProvider.None) return;

        var key = $"postprocessing:{provider}";

        // Cancel existing debounce timer
        if (_debounceTimers.TryRemove(key, out var existingTimer))
        {
            existingTimer.Stop();
            existingTimer.Dispose();
        }

        // If empty, set to unknown immediately
        if (string.IsNullOrEmpty(newValue))
        {
            UpdateCache(key, ProviderHealth.Unknown);
            PostProcessingProviderStatusChanged?.Invoke(this, provider);
            return;
        }

        // Create debounce timer
        var timer = new System.Timers.Timer(DebounceDelayMs);
        timer.Elapsed += async (s, e) =>
        {
            timer.Stop();
            _debounceTimers.TryRemove(key, out _);
            await RefreshAsync(provider, force: true);
            timer.Dispose();
        };
        timer.AutoReset = false;
        timer.Start();

        _debounceTimers[key] = timer;
    }

    // =========================================================================
    // HEALTH CHECK IMPLEMENTATION
    // =========================================================================

    private async Task<ProviderHealth> PerformTranscriptionHealthCheckAsync(CloudTranscriptionProvider provider, string apiKey)
    {
        // WAVE 3 / Win-2: the health request + verdict now run through the Rust
        // shared core (BuildHealthRequest(WithBase) + ParseHealthResponse). The
        // core owns the per-provider endpoint/auth + the Gemini/Grok 400 fold and
        // the routed-always-reachable short-circuit. We preserve the routed
        // short-circuit and the native missing-key gate (the latter lives in
        // RefreshAsync, which returns Unknown before calling here). The
        // post-processing probe path below is untouched (out of FFI scope).
        // TODO-verify (Windows/CI): Rust shared-core swap.

        // Routed / HW-Cloud providers need no API key and are always reachable
        // (mirrors macOS M3-B.4 short-circuit + the core's None endpoint).
        if (provider is CloudTranscriptionProvider.HyperWhisperCloud
            or CloudTranscriptionProvider.MicrosoftAzureSpeech
            or CloudTranscriptionProvider.GoogleSpeech)
        {
            return ProviderHealth.Healthy;
        }

        var hwProvider = RustCoreMapping.HwProviderFor(provider);

        try
        {
            // Build the request via the core (URL + auth header / ?key= for Gemini).
            var request = HyperwhisperCoreMethods.BuildHealthRequest(hwProvider, apiKey);

            var captured = await RustHttpExecutor.ExecuteAsync(request, _httpClient, CancellationToken.None);

            // The core's verdict collapses healthy vs unauthorized into a bool +
            // raw status; expand it back into the app's 3-state enum, preserving
            // the Gemini/Grok 400 -> unauthorized special-case the core already
            // folds into healthy=false.
            var verdict = HyperwhisperCoreMethods.ParseHealthResponse(hwProvider, captured);
            return MapHealthVerdict(provider, verdict);
        }
        catch (TaskCanceledException)
        {
            return ProviderHealth.Unreachable;
        }
        catch (HttpRequestException)
        {
            return ProviderHealth.Unreachable;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"Health check failed for {provider}: {ex.Message}");
            return ProviderHealth.Unreachable;
        }
    }

    /// <summary>
    /// Expand the core's <see cref="HwProviderHealth"/> (healthy bool + raw status)
    /// into the app's 3-state <see cref="ProviderHealth"/>. Mirrors the deleted
    /// native status switch, including the Gemini/Grok 400 -> Unauthorized case.
    /// </summary>
    private static ProviderHealth MapHealthVerdict(CloudTranscriptionProvider provider, HwProviderHealth verdict)
    {
        if (verdict.@healthy)
        {
            return ProviderHealth.Healthy;
        }

        var status = verdict.@status;
        var isUnauthorized =
            status is 401 or 403
            || (provider == CloudTranscriptionProvider.Gemini && status == 400)
            || (provider == CloudTranscriptionProvider.Grok && status == 400);

        return isUnauthorized ? ProviderHealth.Unauthorized : ProviderHealth.Unreachable;
    }

    private async Task<ProviderHealth> PerformPostProcessingHealthCheckAsync(PostProcessingProvider provider, string apiKey)
    {
        if (!PostProcessingEndpoints.TryGetValue(provider, out var endpoint))
        {
            return ProviderHealth.Unknown;
        }

        try
        {
            var url = endpoint.Url;

            // Gemini uses query parameter for auth
            if (endpoint.AuthScheme == "Query")
            {
                url = $"{endpoint.Url}?key={apiKey}";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // Add authentication header based on scheme
            switch (endpoint.AuthScheme)
            {
                case "Bearer":
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    break;
                case "x-api-key":
                    request.Headers.Add("x-api-key", apiKey);
                    request.Headers.Add("anthropic-version", "2023-06-01");
                    break;
                // Query - already added to URL
            }

            var response = await _httpClient.SendAsync(request);

            // Gemini and xAI can return 400 for invalid API keys, unlike other providers
            // which return 401/403. Treat 400 as unauthorized for those providers.
            var isUnauthorized = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                || (provider == PostProcessingProvider.Gemini && response.StatusCode == HttpStatusCode.BadRequest)
                || (provider == PostProcessingProvider.Grok && response.StatusCode == HttpStatusCode.BadRequest);

            return response.StatusCode switch
            {
                HttpStatusCode.OK => ProviderHealth.Healthy,
                _ when isUnauthorized => ProviderHealth.Unauthorized,
                _ => ProviderHealth.Unreachable
            };
        }
        catch (TaskCanceledException)
        {
            return ProviderHealth.Unreachable;
        }
        catch (HttpRequestException)
        {
            return ProviderHealth.Unreachable;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"Health check failed for {provider}: {ex.Message}");
            return ProviderHealth.Unreachable;
        }
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    /// <summary>
    /// Gets the API key for a cloud transcription provider.
    /// </summary>
    /// <param name="provider">The cloud provider to get the key for.</param>
    /// <returns>
    /// The API key if configured, or null for:
    /// - HyperWhisperCloud (uses device credits, no API key needed)
    /// - Unconfigured providers
    /// </returns>
    private string? GetTranscriptionApiKey(CloudTranscriptionProvider provider)
    {
        return provider switch
        {
            // These share keys with post-processing providers
            CloudTranscriptionProvider.OpenAI => ApiKeyService.Instance.GetApiKey(PostProcessingProvider.OpenAI),
            CloudTranscriptionProvider.Groq => ApiKeyService.Instance.GetApiKey(PostProcessingProvider.Groq),
            CloudTranscriptionProvider.Gemini => ApiKeyService.Instance.GetApiKey(PostProcessingProvider.Gemini),
            CloudTranscriptionProvider.Grok => ApiKeyService.Instance.GetApiKey(PostProcessingProvider.Grok),

            // These have their own keys
            CloudTranscriptionProvider.Deepgram => ApiKeyService.Instance.GetApiKey(TranscriptionApiKeyType.Deepgram),
            CloudTranscriptionProvider.AssemblyAI => ApiKeyService.Instance.GetApiKey(TranscriptionApiKeyType.AssemblyAI),
            CloudTranscriptionProvider.ElevenLabs => ApiKeyService.Instance.GetApiKey(TranscriptionApiKeyType.ElevenLabs),
            CloudTranscriptionProvider.Mistral => ApiKeyService.Instance.GetApiKey(TranscriptionApiKeyType.Mistral),
            CloudTranscriptionProvider.Soniox => ApiKeyService.Instance.GetApiKey(TranscriptionApiKeyType.Soniox),

            // HyperWhisper Cloud doesn't need API key
            CloudTranscriptionProvider.HyperWhisperCloud => null,

            _ => null
        };
    }

    private void UpdateCache(string key, ProviderHealth status)
    {
        _cache[key] = (status, DateTime.UtcNow);
    }

    /// <summary>
    /// Invalidates the cache for a specific provider.
    /// </summary>
    public void InvalidateCache(CloudTranscriptionProvider provider)
    {
        _cache.TryRemove($"transcription:{provider}", out _);
    }

    /// <summary>
    /// Invalidates the cache for a specific provider.
    /// </summary>
    public void InvalidateCache(PostProcessingProvider provider)
    {
        _cache.TryRemove($"postprocessing:{provider}", out _);
    }

    // =========================================================================
    // DISPOSAL
    // =========================================================================

    public void Dispose()
    {
        if (!_disposed)
        {
            // Dispose all timers
            foreach (var timer in _debounceTimers.Values)
            {
                timer.Stop();
                timer.Dispose();
            }
            _debounceTimers.Clear();

            _httpClient.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
