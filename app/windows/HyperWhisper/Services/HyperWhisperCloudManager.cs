// HYPERWHISPER CLOUD MANAGER
// Manages HyperWhisper Cloud credit balance fetching and caching.
//
// RESPONSIBILITIES:
// 1. Fetch credit balance from /usage endpoint
// 2. Cache balance for 60 seconds to reduce API calls
// 3. Invalidate cache on license status changes
// 4. Provide UI-bindable properties for credit display
//
// CACHING STRATEGY:
// - 60-second cache duration (matches macOS)
// - Cache invalidated on license status change (device ID ↔ license key)
// - Force refresh available for user-initiated refresh
//
// API ENDPOINT: GET /usage?identifier=<device_id_or_license_key>
//
// THREAD SAFETY:
// - Singleton instance with lock-based thread safety
// - All state mutations on main/UI thread via INotifyPropertyChanged
//
// USAGE:
//   var manager = HyperWhisperCloudManager.Instance;
//   await manager.FetchCreditsAsync();
//   var balance = manager.Credits?.FormattedBalance;
//
// EVENTS:
// - PropertyChanged: Fired when any property changes (for UI binding)
// - CreditsUpdated: Fired after successful credit fetch

using System;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HyperWhisper.Configuration;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

/// <summary>
/// Manages HyperWhisper Cloud credit balance with caching and license awareness.
/// </summary>
public sealed class HyperWhisperCloudManager : INotifyPropertyChanged, IDisposable
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    /// <summary>
    /// Cache duration in seconds (60 seconds, matching macOS).
    /// </summary>
    private const int CacheDurationSeconds = 60;

    /// <summary>
    /// Request timeout in seconds.
    /// </summary>
    private const int RequestTimeoutSeconds = 10;

    // =========================================================================
    // SINGLETON INSTANCE
    // =========================================================================

    private static HyperWhisperCloudManager? _instance;
    private static readonly object _lock = new();

    /// <summary>
    /// Gets the singleton instance of HyperWhisperCloudManager.
    /// </summary>
    public static HyperWhisperCloudManager Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new HyperWhisperCloudManager();
                }
            }
            return _instance;
        }
    }

    // =========================================================================
    // STATE
    // =========================================================================

    private readonly HttpClient _httpClient;
    private HyperWhisperCloudCredits? _credits;
    private bool _isFetchingCredits;
    private string? _lastError;
    private DateTime? _lastFetchTime;
    private bool _disposed;

    // =========================================================================
    // EVENTS
    // =========================================================================

    /// <summary>
    /// Fired when any property changes (for UI binding).
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Fired after successful credit fetch.
    /// </summary>
    public event EventHandler? CreditsUpdated;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    private HyperWhisperCloudManager()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds)
        };

        // Subscribe to license status changes to invalidate cache
        // When user switches between license key and device ID, we need fresh data
        LicenseManager.Instance.LicenseStatusChanged += OnLicenseStatusChanged;

        LoggingService.Info("HyperWhisperCloudManager: Initialized");
    }

    // =========================================================================
    // PUBLIC PROPERTIES
    // =========================================================================

    /// <summary>
    /// Current credit balance and account status.
    /// Null if never fetched or after error.
    /// </summary>
    public HyperWhisperCloudCredits? Credits
    {
        get => _credits;
        private set
        {
            if (_credits != value)
            {
                _credits = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasCredits));
                OnPropertyChanged(nameof(FormattedBalance));
                OnPropertyChanged(nameof(IsExhausted));
                OnPropertyChanged(nameof(IsLow));
            }
        }
    }

    /// <summary>
    /// Whether credits have been fetched successfully.
    /// </summary>
    public bool HasCredits => _credits != null;

    /// <summary>
    /// Whether credit fetch is in progress.
    /// </summary>
    public bool IsFetchingCredits
    {
        get => _isFetchingCredits;
        private set
        {
            if (_isFetchingCredits != value)
            {
                _isFetchingCredits = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Error message from last fetch attempt.
    /// Null if last fetch succeeded.
    /// </summary>
    public string? LastError
    {
        get => _lastError;
        private set
        {
            if (_lastError != value)
            {
                _lastError = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    /// <summary>
    /// Whether there's an error from the last fetch.
    /// </summary>
    public bool HasError => !string.IsNullOrEmpty(_lastError);

    /// <summary>
    /// Formatted balance string for UI display.
    /// Returns empty string if not available.
    /// </summary>
    public string FormattedBalance => _credits?.FormattedBalance ?? "";

    /// <summary>
    /// Whether credits are exhausted.
    /// </summary>
    public bool IsExhausted => _credits?.IsExhausted ?? false;

    /// <summary>
    /// Whether credits are running low.
    /// </summary>
    public bool IsLow => _credits?.IsLow ?? false;

    // =========================================================================
    // PUBLIC METHODS
    // =========================================================================

    /// <summary>
    /// Fetches credit balance from the server.
    /// Uses cached value if available and not expired.
    /// </summary>
    /// <param name="forceRefresh">If true, bypasses cache and fetches fresh data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Credit balance, or null on error.</returns>
    public async Task<HyperWhisperCloudCredits?> FetchCreditsAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        // STEP 1: Check cache (unless force refresh requested)
        if (!forceRefresh && IsCacheValid())
        {
            LoggingService.Debug("HyperWhisperCloudManager: Using cached credits");
            return _credits;
        }

        // STEP 2: Prevent concurrent fetches
        if (_isFetchingCredits)
        {
            LoggingService.Debug("HyperWhisperCloudManager: Fetch already in progress, skipping");
            return _credits;
        }

        IsFetchingCredits = true;
        LastError = null;

        try
        {
            LoggingService.Info("HyperWhisperCloudManager: Fetching credits...");

            // STEP 3: Build request URL with identifier
            var (identifier, isLicensed) = LicenseManager.Instance.GetTranscriptionIdentifier();
            var url = $"{NetworkConfig.UsageEndpoint}?identifier={Uri.EscapeDataString(identifier)}";

            // Add force_refresh parameter if requested (bypasses server-side cache)
            if (forceRefresh)
            {
                url += "&force_refresh=true";
            }

            LoggingService.Debug($"HyperWhisperCloudManager: URL: {MaskUrl(url)}");
            LoggingService.Debug($"HyperWhisperCloudManager: Using {(isLicensed ? "license key" : "device ID")}");

            // STEP 4: Send request
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", $"HyperWhisper-Windows/{GetAppVersion()}");

            var response = await _httpClient.SendAsync(request, cancellationToken);

            // STEP 5: Handle errors
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                var errorMessage = ParseErrorMessage(errorBody) ?? $"HTTP {(int)response.StatusCode}";
                throw new HttpRequestException(errorMessage);
            }

            // STEP 6: Parse response
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            var credits = JsonSerializer.Deserialize<HyperWhisperCloudCredits>(responseJson);

            if (credits == null)
            {
                throw new JsonException("Failed to parse credits response");
            }

            // STEP 7: Update state
            Credits = credits;
            _lastFetchTime = DateTime.UtcNow;

            LoggingService.Info($"HyperWhisperCloudManager: Credits fetched - {credits.FormattedBalance}");
            LoggingService.Debug($"  Account type: {credits.AccountType}");
            LoggingService.Debug($"  Credits: {credits.CreditsRemaining:F1}");
            LoggingService.Debug($"  Minutes: ~{credits.MinutesRemaining}");

            CreditsUpdated?.Invoke(this, EventArgs.Empty);
            return credits;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LastError = "Request timed out";
            LoggingService.Warn("HyperWhisperCloudManager: Request timed out");
            return null;
        }
        catch (HttpRequestException ex)
        {
            LastError = ex.Message;
            LoggingService.Error($"HyperWhisperCloudManager: Network error: {ex.Message}");
            return null;
        }
        catch (JsonException ex)
        {
            LastError = "Invalid response from server";
            LoggingService.Error($"HyperWhisperCloudManager: Parse error: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            LoggingService.Error($"HyperWhisperCloudManager: Unexpected error: {ex.Message}");
            return null;
        }
        finally
        {
            IsFetchingCredits = false;
        }
    }

    /// <summary>
    /// Refreshes credits, bypassing cache.
    /// Convenience method for UI refresh buttons.
    /// </summary>
    public async Task RefreshCreditsAsync(CancellationToken cancellationToken = default)
    {
        await FetchCreditsAsync(forceRefresh: true, cancellationToken);
    }

    /// <summary>
    /// Invalidates the cached credits.
    /// Call this after transcription to get fresh balance.
    /// </summary>
    public void InvalidateCache()
    {
        _lastFetchTime = null;
        LoggingService.Debug("HyperWhisperCloudManager: Cache invalidated");
    }

    /// <summary>
    /// Checks if user has sufficient credits for estimated transcription time.
    /// </summary>
    /// <param name="estimatedMinutes">Estimated audio duration in minutes.</param>
    /// <returns>True if sufficient credits available.</returns>
    public bool HasSufficientCredits(int estimatedMinutes)
    {
        if (_credits == null) return true; // Assume OK if unknown
        return _credits.MinutesRemaining >= estimatedMinutes;
    }

    /// <summary>
    /// Clears all cached data and resets state.
    /// </summary>
    public void Clear()
    {
        Credits = null;
        LastError = null;
        _lastFetchTime = null;
        LoggingService.Debug("HyperWhisperCloudManager: Cleared all data");
    }

    // =========================================================================
    // PRIVATE METHODS
    // =========================================================================

    /// <summary>
    /// Checks if cached credits are still valid.
    /// </summary>
    private bool IsCacheValid()
    {
        if (_credits == null || _lastFetchTime == null)
            return false;

        var elapsed = DateTime.UtcNow - _lastFetchTime.Value;
        return elapsed.TotalSeconds < CacheDurationSeconds;
    }

    /// <summary>
    /// Handles license status changes by invalidating cache.
    /// When user activates/deactivates license, the identifier changes,
    /// so we need to fetch fresh credit data.
    /// </summary>
    private void OnLicenseStatusChanged(object? sender, EventArgs e)
    {
        LoggingService.Info("HyperWhisperCloudManager: License status changed, invalidating cache");
        InvalidateCache();
        Credits = null; // Clear displayed credits to avoid showing stale data
    }

    /// <summary>
    /// Parses error message from API error response.
    /// </summary>
    private static string? ParseErrorMessage(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out var errorElement))
            {
                return errorElement.GetString();
            }
            if (doc.RootElement.TryGetProperty("message", out var msgElement))
            {
                return msgElement.GetString();
            }
        }
        catch
        {
            // Ignore parse errors
        }
        return null;
    }

    /// <summary>
    /// Gets the app version for User-Agent header.
    /// </summary>
    private static string GetAppVersion()
    {
        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            return version?.ToString(3) ?? "1.0.0";
        }
        catch
        {
            return "1.0.0";
        }
    }

    /// <summary>
    /// Masks the URL to hide the identifier for logging.
    /// </summary>
    private static string MaskUrl(string url)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            url,
            @"identifier=[^&]+",
            "identifier=***");
    }

    /// <summary>
    /// Raises the PropertyChanged event.
    /// </summary>
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    // =========================================================================
    // DISPOSAL
    // =========================================================================

    public void Dispose()
    {
        if (!_disposed)
        {
            LicenseManager.Instance.LicenseStatusChanged -= OnLicenseStatusChanged;
            _httpClient.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
