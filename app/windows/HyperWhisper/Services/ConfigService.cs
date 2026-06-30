// CONFIG SERVICE
// Fetches remote trial configuration from the server.
//
// TODO-verify (Windows/CI): Rust shared-core swap. UNVERIFIED / compile-only.
//
// Wave 3 swap: the remote trial-limit OVERRIDE (value + freshness TTL) is now
// owned by the `hw-license` Rust core, persisted via RustCoreKeyValueStore under
// com.hyperwhisper.config.*. This service keeps ONLY the HTTP GET of /api/config
// (the core has no config endpoint); the parsed values are handed to the core
// via LicenseStoreRemoteOverride, and GetCachedConfig reads them back via
// LicenseRemoteOverrideIfFresh.
//
// TTL: server-driven (B4). FetchConfigAsync writes the response's Cache-Control
// max-age into the store (key "com.hyperwhisper.config.maxAgeSecs"); the core's
// freshness check honors it, defaulting to 6h when absent and clamping to a 24h
// upper bound (REMOTE_OVERRIDE_TTL_SECS). Restores the pre-unification behavior.

using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using uniffi.hyperwhisper_core;
using HttpMethod = System.Net.Http.HttpMethod;

namespace HyperWhisper.Services;

/// <summary>
/// Fetches remote trial configuration; caches it through the Rust core.
/// </summary>
public sealed class ConfigService
{
    // =========================================================================
    // SINGLETON
    // =========================================================================

    private static ConfigService? _instance;
    private static readonly object _lock = new();

    public static ConfigService Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new ConfigService();
                }
            }
            return _instance;
        }
    }

    // =========================================================================
    // CONSTANTS
    // =========================================================================

    private const string ConfigUrl = "https://www.hyperwhisper.com/api/config";

    // =========================================================================
    // STATE
    // =========================================================================

    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private ConfigService() { }

    // =========================================================================
    // PUBLIC API
    // =========================================================================

    /// <summary>
    /// Returns the cached remote override if still fresh (24h TTL, in the core),
    /// otherwise null.
    /// </summary>
    public RemoteConfig? GetCachedConfig()
    {
        // TODO-verify (Windows/CI): Rust shared-core swap.
        TrialLimits? over = HyperwhisperCoreMethods.LicenseRemoteOverrideIfFresh(
            RustLicenseCore.Store, RustLicenseCore.Now());
        if (over == null)
        {
            return null;
        }
        return new RemoteConfig((int)over.dailySeconds, (int)over.modelDownloads);
    }

    /// <summary>
    /// Fetches config from the server, persists it through the core, and returns
    /// the values. Returns null on any error.
    /// </summary>
    public async Task<RemoteConfig?> FetchConfigAsync()
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, ConfigUrl);
            request.Headers.Add("User-Agent", "HyperWhisper/1.0");

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                LoggingService.Warn($"ConfigService: Fetch failed with status {response.StatusCode}");
                return null;
            }

            var body = await response.Content.ReadAsStringAsync();
            var parsed = JsonSerializer.Deserialize<ConfigResponse>(body);
            if (parsed == null)
            {
                LoggingService.Warn("ConfigService: Failed to parse response");
                return null;
            }

            // Persist the override through the core.
            // TODO-verify (Windows/CI): Rust shared-core swap.
            HyperwhisperCoreMethods.LicenseStoreRemoteOverride(
                RustLicenseCore.Store,
                new TrialLimits(
                    @dailySeconds: parsed.TrialDailyLimitSeconds,
                    @modelDownloads: parsed.TrialModelDownloadLimit),
                RustLicenseCore.Now());

            // Persist the server's Cache-Control max-age so the core's freshness
            // check honors it (B4). Key string must match hw_license::cache's
            // K_OVERRIDE_MAX_AGE ("com.hyperwhisper.config.maxAgeSecs"). Absent /
            // non-positive ⇒ the core falls back to its 6h default, clamped to 24h.
            var maxAge = response.Headers.CacheControl?.MaxAge;
            var maxAgeSecs = maxAge.HasValue ? (long)maxAge.Value.TotalSeconds : 0;
            if (maxAgeSecs > 0)
            {
                RustLicenseCore.Store.Set(
                    "com.hyperwhisper.config.maxAgeSecs",
                    maxAgeSecs.ToString());
            }
            else
            {
                // FetchConfigAsync is always a LIVE fetch. With no/≤0 max-age on
                // this response, CLEAR any previously stored value so the core's
                // effective_override_ttl reverts to its 6h default (it treats an
                // empty/unparseable value as "use default") instead of pinning a
                // stale TTL from an earlier response that carried max-age.
                RustLicenseCore.Store.Set("com.hyperwhisper.config.maxAgeSecs", "");
            }

            LoggingService.Info(
                $"ConfigService: Fetched config (dailyLimit={parsed.TrialDailyLimitSeconds}s, modelLimit={parsed.TrialModelDownloadLimit})");

            return new RemoteConfig(parsed.TrialDailyLimitSeconds, parsed.TrialModelDownloadLimit);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"ConfigService: Fetch error: {ex.Message}");
            return null;
        }
    }

    // =========================================================================
    // DATA MODELS
    // =========================================================================

    private class ConfigResponse
    {
        [JsonPropertyName("trial_daily_limit_seconds")]
        public int TrialDailyLimitSeconds { get; set; }

        [JsonPropertyName("trial_model_download_limit")]
        public int TrialModelDownloadLimit { get; set; }
    }
}

/// <summary>
/// Immutable remote config values.
/// </summary>
public sealed class RemoteConfig
{
    public int TrialDailyLimitSeconds { get; }
    public int TrialModelDownloadLimit { get; }

    public RemoteConfig(int trialDailyLimitSeconds, int trialModelDownloadLimit)
    {
        TrialDailyLimitSeconds = trialDailyLimitSeconds;
        TrialModelDownloadLimit = trialModelDownloadLimit;
    }
}
