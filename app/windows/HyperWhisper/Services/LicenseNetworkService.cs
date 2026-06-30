// LICENSE NETWORK SERVICE
// Handles license validation network operations.
//
// TODO-verify (Windows/CI): Rust shared-core swap. UNVERIFIED / compile-only.
//
// Wave 3 swap: the request build, response parse, error/offline mapping, and the
// validation cache are now owned by the `hw-license` Rust core. This service
// keeps ONLY the I/O it must own: the HttpClient POST, transient-failure
// classification (429/5xx -> offline fallback), and timeout/cancellation
// handling. License key + cache state live behind RustCoreKeyValueStore.
//
// FLOW:
// - ValidateLicenseAsync() -> LicenseBuildValidateRequest -> POST -> either
//   LicenseParseValidateResponse / LicenseHttpErrorOutcome on a verdict, or
//   LicenseOfflineFallbackOutcome on transient/network failure; then
//   LicenseUpdateValidationCache + LicenseStoreLicenseKey on a real verdict.
//
// CACHING (in the core, keyed off plain UTC `now`):
// - 24-hour validation cache; 7-day offline grace period.
// - license.customerId / cachedStatus / lastValidation in kvstore.json.
// - Raw license key in Windows Credential Manager (unchanged 1:1).

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HyperWhisper.Models;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

/// <summary>
/// Handles license validation network operations.
/// Stateless service - returns LicenseValidationResult for LicenseManager to process.
/// Falls back to cached status (via the Rust core) on network errors.
/// </summary>
public sealed class LicenseNetworkService : IDisposable
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    /// <summary>
    /// Timeout for license validation requests (10 seconds).
    /// </summary>
    private const int ValidationTimeoutSeconds = 10;

    // =========================================================================
    // SINGLETON INSTANCE
    // =========================================================================

    private static LicenseNetworkService? _instance;
    private static readonly object _lock = new();

    public static LicenseNetworkService Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new LicenseNetworkService();
                }
            }
            return _instance;
        }
    }

    // =========================================================================
    // STATE
    // =========================================================================

    private readonly HttpClient _httpClient;
    private bool _disposed;

    private LicenseNetworkService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(ValidationTimeoutSeconds)
        };

        // One-shot legacy migration runs in the store's constructor.
        _ = RustCoreKeyValueStore.Instance;

        LoggingService.Info("LicenseNetworkService: Initialized");
    }

    // =========================================================================
    // PUBLIC METHODS
    // =========================================================================

    /// <summary>
    /// Validates a license key with the server.
    /// Falls back to the core's cached/offline status if the network fails.
    /// </summary>
    public async Task<LicenseValidationResult> ValidateLicenseAsync(
        string licenseKey,
        CancellationToken cancellationToken = default)
    {
        var store = RustLicenseCore.Store;

        // STEP 1: Validate input (core owns the empty-key verdict).
        var trimmedKey = licenseKey?.Trim() ?? "";
        if (string.IsNullOrEmpty(trimmedKey))
        {
            LoggingService.Warn("LicenseNetworkService: Validation rejected - empty license key");
            // TODO-verify (Windows/CI): Rust shared-core swap.
            return RustLicenseCore.ToResult(HyperwhisperCoreMethods.LicenseEmptyKeyOutcome());
        }

        LoggingService.Info("LicenseNetworkService: Validating license key...");

        // STEP 2: Build request via the core (URL + content-type + JSON body bytes).
        var deviceId = DeviceIdService.Instance.GetDeviceId();
        var deviceName = Environment.MachineName;

        // TODO-verify (Windows/CI): Rust shared-core swap.
        ValidateRequest request = HyperwhisperCoreMethods.LicenseBuildValidateRequest(
            trimmedKey, deviceId, deviceName);

        // STEP 3: Send request (I/O stays native).
        try
        {
            LoggingService.Debug("LicenseNetworkService: POST /api/license/validate");

            using var content = new ByteArrayContent(request.body);
            content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(request.contentType);

            var response = await _httpClient.PostAsync(request.url, content, cancellationToken);
            var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                LoggingService.Warn($"LicenseNetworkService: Server returned {code}");

                // 429 + 5xx are transient, not a verdict: fall back to cached/offline
                // (do NOT cache an Invalid that would downgrade a paying user).
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    || code >= 500)
                {
                    LoggingService.Warn(
                        $"LicenseNetworkService: Transient {code} - using cached/offline status");
                    // TODO-verify (Windows/CI): Rust shared-core swap.
                    return OfflineFallbackForKey(store, trimmedKey);
                }

                // Hard non-200 = a real verdict -> core maps it to Invalid/Expired.
                // TODO-verify (Windows/CI): Rust shared-core swap.
                var httpOutcome = HyperwhisperCoreMethods.LicenseHttpErrorOutcome(
                    (ushort)code, responseBytes);
                HyperwhisperCoreMethods.LicenseUpdateValidationCache(
                    store, httpOutcome.status, RustLicenseCore.Now());
                HyperwhisperCoreMethods.LicenseStoreLicenseKey(store, trimmedKey);
                return RustLicenseCore.ToResult(httpOutcome);
            }

            // STEP 4: Parse the 200-OK body in the core.
            // TODO-verify (Windows/CI): Rust shared-core swap.
            var outcome = HyperwhisperCoreMethods.LicenseParseValidateResponse(responseBytes);

            // Persist verdict + key, then update the validation cache.
            HyperwhisperCoreMethods.LicenseStoreLicenseKey(store, trimmedKey);
            HyperwhisperCoreMethods.LicenseUpdateValidationCache(
                store, outcome.status, RustLicenseCore.Now());

            LoggingService.Info($"LicenseNetworkService: Validation complete (valid={outcome.isValid})");
            return RustLicenseCore.ToResult(outcome);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            LoggingService.Warn("LicenseNetworkService: Request timed out");
            // TODO-verify (Windows/CI): Rust shared-core swap.
            return OfflineFallbackForKey(store, trimmedKey);
        }
        catch (OperationCanceledException)
        {
            LoggingService.Info("LicenseNetworkService: Validation cancelled");
            return LicenseValidationResult.Failed("Validation cancelled", LicenseStatus.Invalid);
        }
        catch (HttpRequestException ex)
        {
            LoggingService.Warn($"LicenseNetworkService: Network error - {ex.Message}");
            // TODO-verify (Windows/CI): Rust shared-core swap.
            return OfflineFallbackForKey(store, trimmedKey);
        }
        catch (Exception ex)
        {
            LoggingService.Error($"LicenseNetworkService: Unexpected error - {ex.Message}");
            // TODO-verify (Windows/CI): Rust shared-core swap.
            return OfflineFallbackForKey(store, trimmedKey);
        }
    }

    /// <summary>
    /// Returns the core's offline fallback ONLY when the key being validated
    /// matches the key currently on file. The cached offline-grace verdict is tied
    /// to that stored key, so honoring it for a DIFFERENT (or first-time) key would
    /// wrongly report an unverified key as Active/offline. On a mismatch, returns an
    /// unverified failure instead. (G2 — parity with macOS LicenseNetworkService.)
    /// </summary>
    private static LicenseValidationResult OfflineFallbackForKey(KeyValueStore store, string trimmedKey)
    {
        var storedKey = HyperwhisperCoreMethods.LicenseStoredLicenseKey(store);
        if (!string.Equals(storedKey, trimmedKey, StringComparison.Ordinal))
        {
            LoggingService.Warn(
                "LicenseNetworkService: Offline and submitted key differs from stored - not honoring cached verdict");
            return LicenseValidationResult.Failed(
                "Unable to verify license while offline", LicenseStatus.Invalid);
        }
        return RustLicenseCore.ToResult(
            HyperwhisperCoreMethods.LicenseOfflineFallbackOutcome(store, RustLicenseCore.Now()));
    }

    /// <summary>
    /// Checks if the cached license should be revalidated (older than 24h).
    /// </summary>
    public bool ShouldRevalidate()
    {
        // TODO-verify (Windows/CI): Rust shared-core swap.
        return HyperwhisperCoreMethods.LicenseShouldRevalidate(RustLicenseCore.Store, RustLicenseCore.Now());
    }

    /// <summary>
    /// Gets the stored license key from Windows Credential Manager (via the core).
    /// </summary>
    public string? GetStoredLicenseKey()
    {
        // TODO-verify (Windows/CI): Rust shared-core swap.
        return HyperwhisperCoreMethods.LicenseStoredLicenseKey(RustLicenseCore.Store);
    }

    /// <summary>
    /// Gets the cached license status if within the 7-day grace period.
    /// </summary>
    public LicenseStatus? GetCachedStatus()
    {
        // TODO-verify (Windows/CI): Rust shared-core swap.
        HwLicenseStatus? cached = HyperwhisperCoreMethods.LicenseCachedStatusWithinGrace(
            RustLicenseCore.Store, RustLicenseCore.Now());
        return cached.HasValue ? RustLicenseCore.ToApp(cached.Value) : (LicenseStatus?)null;
    }

    /// <summary>
    /// Clears stored license data (local deactivation). Keeps the remote override.
    /// </summary>
    public void ClearStoredLicense()
    {
        // TODO-verify (Windows/CI): Rust shared-core swap.
        HyperwhisperCoreMethods.LicenseClearStoredLicense(RustLicenseCore.Store);
        LoggingService.Info("LicenseNetworkService: Cleared stored license data");
    }

    // =========================================================================
    // DISPOSAL
    // =========================================================================

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
    }
}
