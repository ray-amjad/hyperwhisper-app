// LICENSE MANAGER
// Coordinates HyperWhisper Cloud license operations and UI state. The license key
// is the Cloud "wallet" (status drives the Cloud transcription identifier). Local
// transcription/model downloads are free & unlimited (open source) — no trial gate.
//
// COMPONENTS:
// - LicenseNetworkService: API calls and local caching
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
    /// URL for purchase page. `/credits` is the universal "go Cloud" path — a guest
    /// buy mints and emails an account key; the retired `/checkout` product is gone.
    /// </summary>
    private const string PurchaseUrl = "https://www.hyperwhisper.com/credits";

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
    /// Whether the user has an active Cloud license.
    /// </summary>
    public bool IsLicensed => _licenseStatus == LicenseStatus.Active;

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
            }
            else
            {
                // Revalidate if no valid cache
                await ActivateLicenseAsync(storedKey, cancellationToken);
            }
        }

        // No remote trial-config fetch — local limits are removed (open source).
    }

    /// <summary>
    /// Gets the stored license key if available.
    /// </summary>
    public string? GetStoredLicenseKey()
    {
        return LicenseNetworkService.Instance.GetStoredLicenseKey();
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

    /// <summary>
    /// Builds the identifier-aware credits purchase URL — the same `/credits`
    /// destination as <see cref="OpenPurchasePage()"/>, tagged with the caller's
    /// license key (licensed) or device ID (guest) so the buy is attributed to the
    /// right wallet. Single source of truth for the credits URL.
    /// </summary>
    public string GetCreditsPurchaseUrl()
    {
        var (identifier, isLicensed) = GetTranscriptionIdentifier();
        var paramName = isLicensed ? "license_key" : "device_id";
        return $"{PurchaseUrl}?{paramName}={Uri.EscapeDataString(identifier)}";
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

        LoggingService.Info($"LicenseManager: License status updated to {result.Status}");
    }

    /// <summary>
    /// Raises the PropertyChanged event.
    /// </summary>
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
