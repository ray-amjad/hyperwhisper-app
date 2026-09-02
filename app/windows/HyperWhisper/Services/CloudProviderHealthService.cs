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

    /// <summary>
    /// How long a recorded transcription failure outranks the health probe
    /// (issue #379). Deliberately the same 60 s as <see cref="CacheTtlSeconds"/>:
    /// the failure record exists to beat exactly one cache generation, not to latch.
    /// </summary>
    private const int FailureOverrideTtlSeconds = 60;

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
        { CloudTranscriptionProvider.Grok, ("https://api.x.ai/v1/models", "Bearer") },
        // Same model-list endpoint as Gemini, but authenticated with the header
        // form the transcribe API uses.
        { CloudTranscriptionProvider.GeminiTranscribe, ("https://generativelanguage.googleapis.com/v1beta/models", "x-goog-api-key") }
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

    /// <summary>
    /// When each provider last returned a DEFINITIVE provider-down error from a
    /// real transcription (issue #379). See
    /// <see cref="RecordTranscriptionOutcome(CloudTranscriptionProvider, long, Exception?)"/>.
    /// Entries are never pruned on read — <see cref="GetHealthStatus(CloudTranscriptionProvider)"/>
    /// stays a pure read. An expired entry simply stops matching, and the
    /// dictionary is bounded by the provider enum.
    /// </summary>
    private readonly ConcurrentDictionary<CloudTranscriptionProvider, DateTime> _recentFailures = new();

    // Monotonic generations for transcription credentials, scoped by provider.
    // A request captures its provider's value before resolving the key. A later
    // edit to that key invalidates the old outcome without discarding valid
    // outcomes for unrelated providers.
    private readonly ConcurrentDictionary<CloudTranscriptionProvider, long> _transcriptionCredentialGenerations = new();

    /// <summary>
    /// Clock. Every timestamp this type takes — the two cache-hit gates, the
    /// <see cref="UpdateCache"/> stamp, and the failure-override window — reads
    /// through this one closure, so a test that moves it moves all of them
    /// together. Mirrors the macOS <c>CloudProviderHealthManager.now</c>.
    /// </summary>
    private readonly Func<DateTime> _now;

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

    private CloudProviderHealthService() : this(() => DateTime.UtcNow)
    {
    }

    /// <summary>
    /// Test seam (issue #379): builds a NON-singleton instance with a clock the
    /// caller drives by hand, so the 60 s cache TTL and the 60 s failure-override
    /// window can be crossed without a wall-clock wait. Visible to
    /// HyperWhisper.SmokeTests via InternalsVisibleTo (see HyperWhisper.csproj).
    /// Production always goes through <see cref="Instance"/>.
    /// </summary>
    internal CloudProviderHealthService(Func<DateTime> now)
    {
        _now = now;
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
        var status = ProviderHealth.Unknown;
        if (_cache.TryGetValue(key, out var cached))
        {
            if (_now() - cached.CachedAt < TimeSpan.FromSeconds(CacheTtlSeconds))
            {
                status = cached.Status;
            }
        }
        return status;
    }

    /// <summary>
    /// Gets the Local API <c>/health</c> verdict. This is the only read seam that
    /// folds a recent real transcription failure over the probe-derived cache.
    /// Generic app reads remain probe-derived through <see cref="GetStatus"/>.
    /// </summary>
    public ProviderHealth GetHealthStatus(CloudTranscriptionProvider provider)
    {
        return ApplyFailureOverride(GetStatus(provider), provider);
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
            if (_now() - cached.CachedAt < TimeSpan.FromSeconds(CacheTtlSeconds))
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
    /// Probes a CANDIDATE key without publishing, caching or persisting it.
    ///
    /// <see cref="RefreshAsync(CloudTranscriptionProvider, bool)"/> cannot serve
    /// this: it reads the key out of the credential store, so it can only grade a
    /// key that has already been saved. Onboarding has to grade the key the user
    /// just typed BEFORE writing it, precisely so a failed credential write cannot
    /// be masked by a health check that passed against a temporary in-memory
    /// credential. Mirrors macOS <c>CloudProviderHealthManager.probe</c>.
    ///
    /// The status cache is deliberately left alone: a probe of some other key must
    /// not overwrite what the app believes about the stored one.
    /// </summary>
    public async Task<ProviderHealth> ProbeAsync(
        CloudTranscriptionProvider provider,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        // Routed / HW-Cloud providers need no API key and are always reachable.
        if (provider is CloudTranscriptionProvider.HyperWhisperCloud
            or CloudTranscriptionProvider.MicrosoftAzureSpeech
            or CloudTranscriptionProvider.GoogleSpeech)
        {
            return ProviderHealth.Healthy;
        }

        var trimmed = apiKey?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return ProviderHealth.Unknown;
        }

        return await PerformTranscriptionHealthCheckAsync(provider, trimmed, cancellationToken);
    }

    /// <summary>
    /// Registers an API key change with debounced refresh.
    /// </summary>
    public void RegisterApiKeyChange(CloudTranscriptionProvider provider, string? newValue)
    {
        var key = $"transcription:{provider}";

        _transcriptionCredentialGenerations.AddOrUpdate(provider, 1, (_, generation) => generation + 1);

        // A key edit invalidates the transcription-failure override too (issue
        // #379). Without this, a user who reacts to an outage by re-pasting their
        // key would see Unreachable for the rest of the 60 s window and read it as
        // "the new key is bad".
        _recentFailures.TryRemove(provider, out _);

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
            if (_now() - cached.CachedAt < TimeSpan.FromSeconds(CacheTtlSeconds))
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
            if (_now() - cached.CachedAt < TimeSpan.FromSeconds(CacheTtlSeconds))
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

    private async Task<ProviderHealth> PerformTranscriptionHealthCheckAsync(
        CloudTranscriptionProvider provider,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        // Meta documents no content-free validation endpoint. A present key is
        // configured but remains unvalidated until the first transcription. The
        // predicate is on the enum so callers that must interpret this Unknown -
        // the onboarding Configure step - read the same fact rather than a second
        // hard-coded copy of it.
        if (!provider.SupportsKeyHealthProbe())
        {
            return ProviderHealth.Unknown;
        }
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

            var captured = await RustHttpExecutor.ExecuteAsync(request, _httpClient, cancellationToken);

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
            // Own key slot — Google, but NOT the Gemini post-processing key.
            CloudTranscriptionProvider.GeminiTranscribe => ApiKeyService.Instance.GetApiKey(TranscriptionApiKeyType.GeminiTranscribe),
            CloudTranscriptionProvider.Meta => ApiKeyService.Instance.GetApiKey(TranscriptionApiKeyType.Meta),

            // HyperWhisper Cloud doesn't need API key
            CloudTranscriptionProvider.HyperWhisperCloud => null,

            _ => null
        };
    }

    private void UpdateCache(string key, ProviderHealth status)
    {
        _cache[key] = (status, _now());
    }

    /// <summary>
    /// Invalidates the cache for a specific provider.
    /// </summary>
    public void InvalidateCache(CloudTranscriptionProvider provider)
    {
        _cache.TryRemove($"transcription:{provider}", out _);
        // A deliberate invalidation clears the transcription-failure override too
        // (issue #379) — otherwise the "give me a fresh verdict" entry point would
        // keep answering with the stale one.
        _recentFailures.TryRemove(provider, out _);
    }

    // =========================================================================
    // TRANSCRIPTION OUTCOME FEEDBACK (issue #379)
    // =========================================================================

    /// <summary>
    /// Records the outcome of a real cloud transcription attempt so the health
    /// verdict stops disagreeing with what the app just observed.
    /// </summary>
    /// <remarks>
    /// STEP-BY-STEP:
    /// 1. <paramref name="error"/> null means the attempt SUCCEEDED. Clear any
    ///    recorded failure and stamp Healthy at the current time. A real
    ///    transcription is stronger evidence than any probe, and its fresh
    ///    timestamp must not expire two seconds later with an older probe.
    /// 2. Otherwise classify. ONLY a definitive provider-down verdict counts (see
    ///    <see cref="IsDefinitiveProviderDownVerdict"/>); everything else is a no-op.
    /// 3. A definitive failure stamps only <c>_recentFailures</c>, and then
    ///    outranks the raw probe at
    ///    <see cref="GetHealthStatus(CloudTranscriptionProvider)"/> for
    ///    <see cref="FailureOverrideTtlSeconds"/> seconds.
    ///
    /// WHY: the health probe cannot see this failure. It hits the vendor's
    /// model-list endpoint, not the transcription endpoint, and for the
    /// HW-Cloud-routed providers it short-circuits without any network call at
    /// all. Either way it stays green while transcription is failing, which is
    /// why <c>/health</c> reported <c>"status":"healthy","reachable":true</c>
    /// throughout a reproducible <c>POST /transcribe</c> failure.
    ///
    /// ⚠️ DELIBERATE ASYMMETRY, mirroring macOS: the override is applied at the
    /// READ seam only. It is never written into the TTL cache.
    /// <see cref="RefreshAsync(CloudTranscriptionProvider, bool)"/> therefore
    /// keeps its normal cache age and still probes when that raw cache expires.
    /// Do not "tidy" the override into the cache or RefreshAsync's return value.
    ///
    /// This deliberately does NOT raise <see cref="TranscriptionProviderStatusChanged"/>.
    /// The only subscriber answers it with a blocking <c>Dispatcher.Invoke</c>, and
    /// this method runs on the transcription thread — raising it here would put a
    /// synchronous UI-thread rendezvous on the hot path, and deadlock outright if
    /// the UI thread were ever waiting on the transcription. The Local API
    /// <c>/health</c> reads <see cref="GetHealthStatus(CloudTranscriptionProvider)"/>
    /// directly, so the surface the issue was filed about is unaffected; the
    /// Settings badges pick the change up on their next rebuild.
    /// </remarks>
    /// <param name="provider">The provider the attempt actually ran against.</param>
    /// <param name="error">Null on success, otherwise the exception thrown.</param>
    public long CaptureTranscriptionCredentialGeneration(CloudTranscriptionProvider provider)
    {
        return _transcriptionCredentialGenerations.GetOrAdd(provider, 0);
    }

    public void RecordTranscriptionOutcome(
        CloudTranscriptionProvider provider,
        long credentialGeneration,
        Exception? error)
    {
        if (provider == CloudTranscriptionProvider.None) return;
        if (credentialGeneration != CaptureTranscriptionCredentialGeneration(provider)) return;

        if (error == null)
        {
            _recentFailures.TryRemove(provider, out _);
            // Always re-stamp. A healthy probe at t=0 followed by a successful
            // transcription at t=59 is fresh evidence at t=59, not at t=0.
            UpdateCache($"transcription:{provider}", ProviderHealth.Healthy);
            return;
        }

        if (!IsDefinitiveProviderDownVerdict(error)) return;

        _recentFailures[provider] = _now();
        LoggingService.Warn(
            $"{provider} transcription reported the provider down; marking /health unreachable for {FailureOverrideTtlSeconds}s: {error.Message}");
    }

    /// <summary>
    /// Whether <paramref name="error"/> is a DEFINITIVE verdict that the PROVIDER
    /// ITSELF is down, as opposed to a verdict about the request, the account or
    /// the local network.
    /// </summary>
    /// <remarks>
    /// YES: <see cref="TranscriptionErrorCode.ProviderUnavailable"/> carrying an
    /// actual HTTP 5xx status. <c>RustCoreMapping</c> produces this shape for
    /// <c>HwTranscriptionException.ProviderUnavailable</c> from HTTP 5xx — the exact counterpart of macOS's
    /// <c>.serverError(statusCode: 500...599, _)</c> plus <c>.providerNotAvailable</c>.
    ///
    /// NO, on purpose: Unauthorized, QuotaExceeded, RateLimited, NetworkError,
    /// NoSpeechDetected, FileTooLarge, InvalidRequest, Cancelled, CloudAccountRequired,
    /// ProviderUnavailable without a 5xx (including HTTP 408 and poll exhaustion),
    /// and anything that is not a <see cref="TranscriptionException"/> at all.
    /// Marking a provider unreachable because the user's Wi-Fi dropped, because
    /// their card expired, or because they recorded silence would be a worse bug
    /// than the stale verdict this exists to fix. The default is therefore the
    /// CONSERVATIVE answer, so a code added to the enum later cannot start marking
    /// providers unreachable until someone deliberately lists it here.
    /// </remarks>
    internal static bool IsDefinitiveProviderDownVerdict(Exception error)
    {
        return error is TranscriptionException
        {
            Code: TranscriptionErrorCode.ProviderUnavailable,
            HttpStatusCode: >= 500 and <= 599
        };
    }

    /// <summary>
    /// Folds a recorded transcription failure over a probe-derived status. Pure:
    /// mutates nothing, so an expired record is ignored rather than cleaned up.
    /// </summary>
    private ProviderHealth ApplyFailureOverride(ProviderHealth status, CloudTranscriptionProvider provider)
    {
        if (!_recentFailures.TryGetValue(provider, out var failedAt)) return status;
        if (_now() - failedAt >= TimeSpan.FromSeconds(FailureOverrideTtlSeconds)) return status;
        return ProviderHealth.Unreachable;
    }

    /// <summary>
    /// Test seam (issue #379): publishes a cached status directly, so a smoke test
    /// can put a provider in the "probe says Healthy" state the override has to
    /// beat without doing any network I/O. Visible to HyperWhisper.SmokeTests via
    /// InternalsVisibleTo (see HyperWhisper.csproj) — no other accessibility gives
    /// the test the state it needs while keeping <c>UpdateCache</c> private.
    /// </summary>
    internal void SetCachedTranscriptionStatusForTests(CloudTranscriptionProvider provider, ProviderHealth status)
    {
        UpdateCache($"transcription:{provider}", status);
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
