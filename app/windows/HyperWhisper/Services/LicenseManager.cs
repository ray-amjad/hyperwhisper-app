// LICENSE MANAGER
// Coordinates license operations and manages UI state.
//
// COMPONENTS:
// - LicenseNetworkService: API calls and local caching
// - LicenseUsageTracker: Trial limits (daily time, model downloads)
// - DeviceIdService: Device identification
//
// USAGE:
// - Singleton accessible via LicenseManager.Instance
// - SettingsPage for license UI
// - HyperWhisperCloudService for transcription identifiers
//
// EVENTS:
// - LicenseStatusChanged: Fired when license status changes
// - PropertyChanged: Fired when any property changes
//
// METHODS:
// - ActivateLicenseAsync(): Validates and stores a license key
// - DeactivateLicense(): Clears stored license (returns to trial)
// - LoadStoredLicenseAsync(): Loads and validates stored license on startup
// - CanStartRecording(): Checks if recording is allowed
// - CanDownloadModel(): Checks if model download is allowed

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

/// <summary>
/// Orchestrates license operations and maintains application state.
/// </summary>
public sealed class LicenseManager : INotifyPropertyChanged
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    /// <summary>
    /// URL for purchase page.
    /// </summary>
    private const string PurchaseUrl = "https://www.hyperwhisper.com/checkout";

    /// <summary>
    /// URL for user portal (manage billing, credits).
    /// </summary>
    private const string UserPortalUrl = "https://www.hyperwhisper.com/user";

    // =========================================================================
    // SINGLETON INSTANCE
    // =========================================================================

    private static LicenseManager? _instance;
    private static readonly object _lock = new();

    /// <summary>
    /// Gets the singleton instance of LicenseManager.
    /// </summary>
    public static LicenseManager Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new LicenseManager();
                }
            }
            return _instance;
        }
    }

    // =========================================================================
    // STATE
    // =========================================================================

    private LicenseStatus _licenseStatus = LicenseStatus.Trial;
    private bool _isValidating;
    private bool _isDeactivating;
    private string? _lastError;
    private string? _customerEmail;

    // =========================================================================
    // EVENTS
    // =========================================================================

    /// <summary>
    /// Fired when any property changes (for UI binding).
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Fired when license status changes.
    /// </summary>
    public event EventHandler? LicenseStatusChanged;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    private LicenseManager()
    {
        // Subscribe to usage tracker changes
        LicenseUsageTracker.Instance.UsageChanged += OnUsageChanged;

        LoggingService.Info("LicenseManager: Initialized");
    }

    // =========================================================================
    // PUBLIC PROPERTIES
    // =========================================================================

    /// <summary>
    /// Current license status.
    /// </summary>
    public LicenseStatus LicenseStatus
    {
        get => _licenseStatus;
        private set
        {
            if (_licenseStatus != value)
            {
                _licenseStatus = value;
                OnPropertyChanged();
                LicenseStatusChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Whether license validation is in progress.
    /// </summary>
    public bool IsValidating
    {
        get => _isValidating;
        private set
        {
            if (_isValidating != value)
            {
                _isValidating = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Whether deactivation is in progress.
    /// </summary>
    public bool IsDeactivating
    {
        get => _isDeactivating;
        private set
        {
            if (_isDeactivating != value)
            {
                _isDeactivating = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Error message from last validation attempt.
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
            }
        }
    }

    /// <summary>
    /// Customer email associated with the license.
    /// </summary>
    public string? CustomerEmail
    {
        get => _customerEmail;
        private set
        {
            if (_customerEmail != value)
            {
                _customerEmail = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Whether the user has an active license.
    /// </summary>
    public bool IsLicensed => _licenseStatus == LicenseStatus.Active;

    // =========================================================================
    // USAGE TRACKER DELEGATED PROPERTIES
    // =========================================================================

    /// <summary>
    /// Daily transcription usage in seconds.
    /// </summary>
    public int DailyUsageSeconds => LicenseUsageTracker.Instance.DailyUsageSeconds;

    /// <summary>
    /// Number of models downloaded.
    /// </summary>
    public int ModelsDownloaded => LicenseUsageTracker.Instance.ModelsDownloaded;

    /// <summary>
    /// Whether daily limit is reached (trial users).
    /// </summary>
    public bool IsDailyLimitReached => LicenseUsageTracker.Instance.IsDailyLimitReached;

    /// <summary>
    /// Whether model limit is reached (trial users).
    /// </summary>
    public bool IsModelLimitReached => LicenseUsageTracker.Instance.IsModelLimitReached;

    /// <summary>
    /// Trial daily transcription limit (for UI display).
    /// </summary>
    public int TrialDailyTranscriptionLimit => LicenseUsageTracker.TrialDailyLimitSeconds;

    /// <summary>
    /// Trial model download limit (for UI display).
    /// </summary>
    public int TrialModelDownloadLimit => LicenseUsageTracker.TrialModelLimit;

    // =========================================================================
    // LICENSE OPERATIONS
    // =========================================================================

    /// <summary>
    /// Activates a license key by validating it with the server.
    /// </summary>
    /// <param name="licenseKey">The license key to activate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation result with status and error info.</returns>
    public async Task<LicenseValidationResult> ActivateLicenseAsync(
        string licenseKey,
        CancellationToken cancellationToken = default)
    {
        IsValidating = true;
        LastError = null;

        try
        {
            LoggingService.Info("LicenseManager: Activating license...");

            var result = await LicenseNetworkService.Instance.ValidateLicenseAsync(licenseKey, cancellationToken);
            ProcessValidationResult(result);

            return result;
        }
        finally
        {
            IsValidating = false;
        }
    }

    /// <summary>
    /// Deactivates the license locally (clears stored data).
    /// </summary>
    /// <returns>True if deactivation succeeded.</returns>
    public bool DeactivateLicense()
    {
        IsDeactivating = true;
        LastError = null;

        try
        {
            LoggingService.Info("LicenseManager: Deactivating license...");

            // Clear stored license data
            LicenseNetworkService.Instance.ClearStoredLicense();

            // Reset state
            LicenseStatus = LicenseStatus.Trial;
            CustomerEmail = null;
            LicenseUsageTracker.Instance.UpdateLicenseStatus(LicenseStatus.Trial);

            LoggingService.Info("LicenseManager: License deactivated, reverted to trial");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            LoggingService.Error($"LicenseManager: Deactivation failed: {ex.Message}");
            return false;
        }
        finally
        {
            IsDeactivating = false;
        }
    }

    /// <summary>
    /// Loads stored license from cache, revalidates if cache expired (24h).
    /// Also loads remote trial config.
    /// Call this on app startup.
    /// </summary>
    public async Task LoadStoredLicenseAsync(CancellationToken cancellationToken = default)
    {
        LoggingService.Info("LicenseManager: Loading stored license...");

        var storedKey = LicenseNetworkService.Instance.GetStoredLicenseKey();

        if (string.IsNullOrEmpty(storedKey))
        {
            LoggingService.Info("LicenseManager: No stored license, using trial mode");
            LicenseStatus = LicenseStatus.Trial;
            LicenseUsageTracker.Instance.UpdateLicenseStatus(LicenseStatus.Trial);
        }
        else if (LicenseNetworkService.Instance.ShouldRevalidate())
        {
            // Check if we should revalidate (cache older than 24h)
            LoggingService.Info("LicenseManager: Cache expired, revalidating license...");
            await ActivateLicenseAsync(storedKey, cancellationToken);
        }
        else
        {
            // Use cached status
            var cachedStatus = LicenseNetworkService.Instance.GetCachedStatus();
            if (cachedStatus.HasValue)
            {
                LoggingService.Info($"LicenseManager: Using cached license status: {cachedStatus.Value}");
                LicenseStatus = cachedStatus.Value;
                LicenseUsageTracker.Instance.UpdateLicenseStatus(cachedStatus.Value);
            }
            else
            {
                // Revalidate if no valid cache
                await ActivateLicenseAsync(storedKey, cancellationToken);
            }
        }

        // Load remote trial config (non-blocking for license flow)
        await LoadRemoteConfigAsync();
    }

    /// <summary>
    /// Loads remote trial config: applies cached values immediately, then fetches fresh values.
    /// Non-blocking — uses defaults if no cache and no network.
    /// </summary>
    private async Task LoadRemoteConfigAsync()
    {
        // Apply cached config immediately (if fresh)
        var cached = ConfigService.Instance.GetCachedConfig();
        if (cached != null)
        {
            LicenseUsageTracker.Instance.UpdateTrialLimits(
                cached.TrialDailyLimitSeconds, cached.TrialModelDownloadLimit);
            return; // Cache is fresh, no need to fetch
        }

        // Cache expired or missing — fetch from server
        var fetched = await ConfigService.Instance.FetchConfigAsync();
        if (fetched != null)
        {
            LicenseUsageTracker.Instance.UpdateTrialLimits(
                fetched.TrialDailyLimitSeconds, fetched.TrialModelDownloadLimit);
        }
        // On failure, hardcoded defaults remain in effect
    }

    /// <summary>
    /// Gets the stored license key if available.
    /// </summary>
    public string? GetStoredLicenseKey()
    {
        return LicenseNetworkService.Instance.GetStoredLicenseKey();
    }

    // =========================================================================
    // USAGE TRACKING (DELEGATED)
    // =========================================================================

    /// <summary>
    /// Checks if user can start recording based on daily limit.
    /// </summary>
    public bool CanStartRecording()
    {
        return LicenseUsageTracker.Instance.CanStartRecording();
    }

    /// <summary>
    /// Records transcription time and updates usage.
    /// </summary>
    public void RecordTranscriptionTime(int seconds)
    {
        LicenseUsageTracker.Instance.RecordTranscriptionTime(seconds);
    }

    /// <summary>
    /// Gets remaining daily transcription time in seconds.
    /// </summary>
    public int GetRemainingDailyTime()
    {
        return LicenseUsageTracker.Instance.GetRemainingDailyTime();
    }

    /// <summary>
    /// Gets remaining daily time as formatted string.
    /// </summary>
    public string GetRemainingDailyTimeFormatted()
    {
        return LicenseUsageTracker.Instance.GetRemainingDailyTimeFormatted();
    }

    /// <summary>
    /// Checks if user can download another model.
    /// </summary>
    public bool CanDownloadModel()
    {
        return LicenseUsageTracker.Instance.CanDownloadModel();
    }

    /// <summary>
    /// Increments the model download count.
    /// </summary>
    public void IncrementModelDownloadCount()
    {
        LicenseUsageTracker.Instance.IncrementModelDownloadCount();
    }

    /// <summary>
    /// Gets remaining model downloads.
    /// </summary>
    public int GetRemainingModelDownloads()
    {
        return LicenseUsageTracker.Instance.GetRemainingModelDownloads();
    }

    // =========================================================================
    // HYPERWHISPER CLOUD
    // =========================================================================

    /// <summary>
    /// Returns license key if active, otherwise device ID for credit tracking.
    /// Used by HyperWhisperCloudService for authentication.
    /// </summary>
    /// <returns>Tuple of (identifier, isLicensed).</returns>
    public (string Identifier, bool IsLicensed) GetTranscriptionIdentifier()
    {
        if (_licenseStatus == LicenseStatus.Active)
        {
            var key = LicenseNetworkService.Instance.GetStoredLicenseKey();
            if (!string.IsNullOrEmpty(key))
            {
                return (key, true);
            }
        }

        return (DeviceIdService.Instance.GetDeviceId(), false);
    }

    // =========================================================================
    // EXTERNAL LINKS
    // =========================================================================

    /// <summary>
    /// Opens the purchase page in the default browser.
    /// </summary>
    public void OpenPurchasePage()
        => OpenPurchasePage(out _);

    public bool OpenPurchasePage(out string? errorMessage)
        => TryOpenExternalUrl(PurchaseUrl, "purchase page", out errorMessage);

    /// <summary>
    /// Opens the user portal in the default browser.
    /// </summary>
    public void OpenUserPortal()
        => OpenUserPortal(out _);

    public bool OpenUserPortal(out string? errorMessage)
        => TryOpenExternalUrl(UserPortalUrl, "user portal", out errorMessage);

    private static bool TryOpenExternalUrl(string url, string label, out string? errorMessage)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            LoggingService.Error($"LicenseManager: Failed to open {label}: {ex.Message}");
            return false;
        }
    }

    // =========================================================================
    // PRIVATE METHODS
    // =========================================================================

    /// <summary>
    /// Processes a validation result and updates state.
    /// </summary>
    private void ProcessValidationResult(LicenseValidationResult result)
    {
        LicenseStatus = result.Status;
        CustomerEmail = result.CustomerEmail;

        if (!result.IsValid)
        {
            LastError = result.ErrorMessage;
        }
        else
        {
            LastError = null;
        }

        // Update usage tracker with new license status
        LicenseUsageTracker.Instance.UpdateLicenseStatus(result.Status);

        LoggingService.Info($"LicenseManager: License status updated to {result.Status}");
    }

    /// <summary>
    /// Handles usage tracker changes to notify property changed.
    /// </summary>
    private void OnUsageChanged(object? sender, EventArgs e)
    {
        // Notify that usage-related properties may have changed
        OnPropertyChanged(nameof(DailyUsageSeconds));
        OnPropertyChanged(nameof(ModelsDownloaded));
        OnPropertyChanged(nameof(IsDailyLimitReached));
        OnPropertyChanged(nameof(IsModelLimitReached));
    }

    /// <summary>
    /// Raises the PropertyChanged event.
    /// </summary>
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
