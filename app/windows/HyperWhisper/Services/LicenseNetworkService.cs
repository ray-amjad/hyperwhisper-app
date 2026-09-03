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
//   LicensePersistValidationVerdict on a real verdict (stores the key only when
//   valid; caches only for the stored key, so a rejected replacement key can't
//   clobber a valid user's cached status).
//
// CACHING (in the core, keyed off plain UTC `now`):
// - 24-hour validation cache; 7-day offline grace period.
// - license.customerId / cachedStatus / lastValidation in kvstore.json.
// - Raw license key in Windows Credential Manager (unchanged 1:1).
//
// ERROR REPORTING (HYPERWHISPER-SP / HYPERWHISPER-FM parity with macOS):
// - A non-200 that is a genuine backend incident (unexpected status, an
//   unrecognised/absent verdict reason, or an undecodable body) is captured to
//   Sentry. An ordinary "this license isn't entitled" reply (400 + a `reason`
//   of "not_entitled" - lapsed, revoked, or a mistyped/non-existent key) is
//   only logged - see LicenseVerdictReason.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
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
                // The core persists the verdict guardedly: the key is stored only
                // when valid, and the (global) validation cache is written only
                // when the attempted key is the stored key — a rejected
                // replacement key must not clobber the stored key or its cached
                // status (a 24h lockout for a valid user).
                // TODO-verify (Windows/CI): Rust shared-core swap.
                var httpOutcome = HyperwhisperCoreMethods.LicenseHttpErrorOutcome(
                    (ushort)code, responseBytes);

                // The server's own classification of this rejection, or nil if it
                // stated none. Decoded once and used for BOTH the verdict decision
                // and the Sentry tag below, so the two can never disagree about
                // what the server said. Mirrors macOS LicenseNetworkService.swift
                // (HYPERWHISPER-SP / HYPERWHISPER-FM).
                var verdictReason = LicenseVerdictReason(code, responseBytes);

                if (verdictReason == NotEntitledReason)
                {
                    // An ordinary "this license isn't entitled" reply (lapsed,
                    // revoked, or a mistyped/non-existent key) — log it, never
                    // report it. Never log the license key itself.
                    LoggingService.Warn(
                        $"LicenseNetworkService: Validation rejected by server - status={code}, reason={verdictReason}, message={httpOutcome.errorMessage ?? "no message"}");
                }
                else
                {
                    // Everything else is an unexpected non-200: a different
                    // status, an unrecognised/absent reason, or an undecodable
                    // body. That is a genuine backend incident, not an ordinary
                    // verdict — report it. `license_reason` as a TAG (not just an
                    // extra) so lookup_failed / bad_request / unstated stay
                    // separable in triage.
                    SentryService.CaptureDiagnosticEvent(
                        "License validation server error",
                        extras: new Dictionary<string, object>
                        {
                            ["endpoint"] = request.url,
                            ["status"] = code,
                            ["license_reason"] = verdictReason ?? UnstatedReason
                        },
                        tags: new Dictionary<string, string>
                        {
                            ["component"] = "license",
                            ["license_reason"] = verdictReason ?? UnstatedReason
                        });
                }

                HyperwhisperCoreMethods.LicensePersistValidationVerdict(
                    store, httpOutcome.status, trimmedKey, RustLicenseCore.Now());
                return RustLicenseCore.ToResult(httpOutcome);
            }

            // STEP 4: Parse the 200-OK body in the core.
            // TODO-verify (Windows/CI): Rust shared-core swap.
            var outcome = HyperwhisperCoreMethods.LicenseParseValidateResponse(responseBytes);

            // Persist the verdict guardedly (see the non-200 branch above): key
            // stored only on a valid verdict, cache written only for the stored key.
            HyperwhisperCoreMethods.LicensePersistValidationVerdict(
                store, outcome.status, trimmedKey, RustLicenseCore.Now());

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
    /// Checks a key WITHOUT activating it on this PC. This is what the
    /// onboarding "Test access key" button runs, and it is the Windows port of
    /// macOS <c>LicenseNetworkService.probeLicense</c>
    /// (<c>performValidation(mode: .probe)</c>).
    ///
    /// Three things separate it from <see cref="ValidateLicenseAsync"/>, and all
    /// three are the point:
    /// <list type="bullet">
    /// <item>the body carries <c>probe_only</c> and NO device identity, so the
    /// backend records no device validation against the key;</item>
    /// <item><c>LicensePersistValidationVerdict</c> is never called, so a valid
    /// verdict does not store the key or write the validation cache;</item>
    /// <item>there is no offline fallback, because a cached verdict belongs to
    /// the STORED key and reporting it here would pass an unverified key.</item>
    /// </list>
    ///
    /// The core has no probe request builder, so the body is built here (as macOS
    /// builds its own) and only the endpoint is taken from the core's validate
    /// request, which keeps the two calls pointed at the same URL.
    /// </summary>
    public async Task<LicenseValidationResult> ProbeLicenseAsync(
        string licenseKey,
        CancellationToken cancellationToken = default)
    {
        var trimmedKey = licenseKey?.Trim() ?? "";
        if (string.IsNullOrEmpty(trimmedKey))
        {
            LoggingService.Warn("LicenseNetworkService: Probe rejected - empty license key");
            return RustLicenseCore.ToResult(HyperwhisperCoreMethods.LicenseEmptyKeyOutcome());
        }

        // Endpoint only. The device-tracking body this builds is deliberately
        // discarded and replaced with the probe body below.
        var endpoint = HyperwhisperCoreMethods
            .LicenseBuildValidateRequest(trimmedKey, deviceId: "", deviceName: "")
            .url;

        var body = JsonSerializer.SerializeToUtf8Bytes(new ProbeRequestBody(trimmedKey));

        try
        {
            LoggingService.Debug("LicenseNetworkService: POST /api/license/validate (probe)");

            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            var response = await _httpClient.PostAsync(endpoint, content, cancellationToken);
            var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;

                // 429 and 5xx are not a verdict about this key. With no cached
                // fallback available to a probe, say so plainly rather than
                // reporting the key as invalid.
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests || code >= 500)
                {
                    LoggingService.Warn($"LicenseNetworkService: Probe hit transient {code}");
                    return LicenseValidationResult.Failed(ProbeUnavailableMessage);
                }

                LoggingService.Warn($"LicenseNetworkService: Probe rejected by server - status={code}");
                return RustLicenseCore.ToResult(
                    HyperwhisperCoreMethods.LicenseHttpErrorOutcome((ushort)code, responseBytes));
            }

            var outcome = HyperwhisperCoreMethods.LicenseParseValidateResponse(responseBytes);
            LoggingService.Info($"LicenseNetworkService: Probe complete (valid={outcome.isValid})");
            return RustLicenseCore.ToResult(outcome);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LoggingService.Info("LicenseNetworkService: Probe cancelled");
            return LicenseValidationResult.Failed("Validation cancelled");
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LicenseNetworkService: Probe failed - {ex.Message}");
            return LicenseValidationResult.Failed(ProbeUnavailableMessage);
        }
    }

    private const string ProbeUnavailableMessage = "Unable to verify license while offline";

    /// <summary>
    /// The probe request body. <c>device_id</c> is omitted on purpose: the backend
    /// treats it as optional and only records a device validation when it is
    /// present, which is what keeps Test a lookup.
    /// </summary>
    private sealed record ProbeRequestBody(
        [property: JsonPropertyName("license_key")] string LicenseKey)
    {
        [JsonPropertyName("probe_only")]
        public bool ProbeOnly => true;
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

    // =========================================================================
    // VERDICT CLASSIFICATION (HYPERWHISPER-SP / HYPERWHISPER-FM parity)
    // =========================================================================

    /// <summary>
    /// The one `reason` value that means "ordinary verdict - log it, don't
    /// report it". Named rather than spelled out at each use so the predicate,
    /// the call site's branch, and its log line cannot drift apart.
    /// internal (not private): test seam for HyperWhisper.SmokeTests via
    /// InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    /// change is intended.
    /// </summary>
    internal const string NotEntitledReason = "not_entitled";

    /// <summary>
    /// Stand-in used in Sentry tags/extras when the server stated no usable
    /// reason, so "the backend didn't classify this" is searchable instead of
    /// being an absent tag.
    /// </summary>
    private const string UnstatedReason = "unstated";

    /// <summary>
    /// The one field of the backend's invalid-license reply this classifier reads.
    /// Everything else in that body (valid/error/status) is the core's job -
    /// this is decoded independently, native-side, purely to read `reason`.
    /// </summary>
    private sealed class LicenseVerdictBody
    {
        [JsonPropertyName("reason")]
        public string? Reason { get; set; }
    }

    /// <summary>
    /// The backend's machine-readable classification of a non-200 invalid-license
    /// reply - "not_entitled", "lookup_failed", "bad_request", or whatever else
    /// it may add later - returned verbatim. Null when the status is not 400,
    /// the body does not decode, or it states no non-empty reason.
    ///
    /// Mirrors macOS <c>LicenseNetworkService.licenseVerdictReason(statusCode:body:)</c>
    /// exactly: the response SHAPE cannot decide this, because the backend
    /// answers 400 with the same <c>{"valid":false,"error":"..."}</c> for an
    /// ordinary lapsed license, for a key that doesn't exist, AND for genuine
    /// infrastructure faults - only the server knows which branch it took, so
    /// it says so via `reason`. See <c>nextjs/src/lib/license-validation-probe.ts</c>
    /// (`LicenseInvalidReason`) for the backend side.
    ///
    /// Callers treat exactly one value - <see cref="NotEntitledReason"/> - as an
    /// ordinary verdict to log rather than capture. A different status, an HTML
    /// captive-portal page, an empty body, a missing/blank reason, or an
    /// unrecognised reason all fall through to "report it" - nil-means-report is
    /// the safe default, and it is accurate: one backend serves every client, so
    /// `reason` is present on every invalid reply from the moment it deploys.
    /// </summary>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static string? LicenseVerdictReason(int statusCode, byte[] body)
    {
        if (statusCode != 400)
        {
            return null;
        }

        LicenseVerdictBody? decoded;
        try
        {
            decoded = JsonSerializer.Deserialize<LicenseVerdictBody>(body);
        }
        catch (JsonException)
        {
            return null;
        }

        var reason = decoded?.Reason;
        return string.IsNullOrEmpty(reason) ? null : reason;
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
