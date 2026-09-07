using System.Windows.Input;
using HyperWhisper.CloudAccount;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.PortableApplication.ViewModels;

/// <summary>
/// Account-specific UI state. Account keys are accepted only for online
/// validation and the input is cleared before any result is displayed.
/// </summary>
public sealed class CloudAccountViewModel : ViewModelBase
{
    private const int MaximumDeviceNameLength = 128;

    private readonly PortableCloudAccountService _service;
    private readonly IDeviceIdentityProvider _deviceIdentity;
    private readonly Func<Uri, PlatformResult> _openUri;
    private readonly string _deviceName;
    private string _accountKey = string.Empty;
    private string _accountState = "Not activated";
    private string? _customerEmail;
    private string? _customerId;
    private string? _expiresAt;
    private string? _credits;
    private string? _minutesRemaining;
    private bool _hasAccount;
    private int _operationRunning;
    private CloudCreditBalance? _balance;
    private bool _isLoadingCredits;
    private string? _creditsError;
    private string? _activationError;
    private string? _storedAccountKey;
    private bool _isAccountKeyRevealed;
    private bool _isActivating;

    public CloudAccountViewModel(
        PortableCloudAccountService service,
        IDeviceIdentityProvider deviceIdentity,
        string deviceName,
        Func<Uri, PlatformResult> openUri)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _deviceIdentity = deviceIdentity ?? throw new ArgumentNullException(nameof(deviceIdentity));
        _openUri = openUri ?? throw new ArgumentNullException(nameof(openUri));
        _deviceName = NormalizeDeviceName(deviceName);
        ActivateCommand = new AsyncCommand(_ => ActivateAsync(), _ => CanRunOperation);
        RefreshStatusCommand = new AsyncCommand(_ => RefreshStatusAsync(), _ => CanRunOperation);
        RefreshCreditsCommand = new AsyncCommand(_ => RefreshCreditsAsync(), _ => CanRunOperation);
        DeactivateCommand = new AsyncCommand(_ => DeactivateAsync(), _ => CanRunOperation);
        PurchaseCommand = new AsyncCommand(_ => OpenPurchaseAsync(), _ => CanRunOperation);
        ManageCommand = new AsyncCommand(_ => OpenManageAsync(), _ => CanRunOperation);
    }

    public string AccountKey { get => _accountKey; set => Set(ref _accountKey, value); }
    public string AccountState { get => _accountState; private set { if (Set(ref _accountState, value)) NotifyLicenseState(); } }
    public string? CustomerEmail { get => _customerEmail; private set { if (Set(ref _customerEmail, value)) Notify(nameof(HasCustomerEmail)); } }
    public string? CustomerId { get => _customerId; private set { if (Set(ref _customerId, value)) Notify(nameof(HasCustomerId)); } }
    public string? ExpiresAt { get => _expiresAt; private set { if (Set(ref _expiresAt, value)) Notify(nameof(HasExpiry)); } }
    public string? Credits { get => _credits; private set { if (Set(ref _credits, value)) Notify(nameof(HasCredits)); } }
    public string? MinutesRemaining { get => _minutesRemaining; private set => Set(ref _minutesRemaining, value); }
    public bool HasAccount { get => _hasAccount; private set { if (Set(ref _hasAccount, value)) NotifyLicenseState(); } }
    public bool HasCustomerEmail => !string.IsNullOrEmpty(CustomerEmail);
    public bool HasCustomerId => !string.IsNullOrEmpty(CustomerId);
    public bool HasExpiry => !string.IsNullOrEmpty(ExpiresAt);
    public bool HasCredits => !string.IsNullOrEmpty(Credits);

    // -----------------------------------------------------------------------
    // BALANCE DETAIL
    // The Windows balance card shows a dollar figure under the minutes, a cost per minute, an
    // account type, an anonymous daily reset, and three panel states (loading, error, low or
    // exhausted). Every one of those is derivable from the CloudCreditBalance the shared service
    // already returns, so the whole card can be data bound rather than driven from code behind.
    // The dollar figure follows the Windows model: one credit is a tenth of a cent.
    // -----------------------------------------------------------------------

    /// <summary>The account only counts as licensed while the server says the key is active.</summary>
    public bool IsActiveAccount => HasAccount && AccountState == "Active";
    /// <summary>Windows shows the activation card and the Get Credits call to action only here.</summary>
    public bool IsUnlicensed => !IsActiveAccount;
    /// <summary>A key was found but it is expired or invalid, which earns the warning banner.</summary>
    public bool HasLicenseProblem => HasAccount && !IsActiveAccount;
    public string DollarBalance => $"(${(_balance?.CreditsRemaining ?? 0) / 1000.0:F2})";
    public string CostPerMinute => $"~{_balance?.CreditsPerMinute ?? 0:F1}";
    public string AccountTypeLabel => _balance is null
        ? AccountState
        : _balance.IsLicensed ? "Licensed" : _balance.IsAnonymous ? "Anonymous" : "Trial";
    public string? DailyReset => _balance is { IsAnonymous: true, ResetsAt: { } resets }
        ? resets.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture)
        : null;
    public bool HasDailyReset => !string.IsNullOrEmpty(DailyReset);
    public bool IsCreditsExhausted => _balance is not null && _balance.CreditsRemaining <= 0;
    public bool IsCreditsLow => _balance is { MinutesRemaining: > 0 and < 10 };
    public bool IsLoadingCredits { get => _isLoadingCredits; private set { if (Set(ref _isLoadingCredits, value)) Notify(nameof(ShowCreditsLoading)); } }
    public bool ShowCreditsLoading => IsLoadingCredits && !HasCredits;
    public string? CreditsError { get => _creditsError; private set { if (Set(ref _creditsError, value)) Notify(nameof(HasCreditsError)); } }
    public bool HasCreditsError => !string.IsNullOrEmpty(CreditsError) && !HasCredits;
    /// <summary>The message under the activation field after a rejected key.</summary>
    public string? ActivationError { get => _activationError; private set { if (Set(ref _activationError, value)) Notify(nameof(HasActivationError)); } }
    public bool HasActivationError => !string.IsNullOrEmpty(ActivationError);

    // -----------------------------------------------------------------------
    // ACCOUNT KEY ROW
    // The Windows account card ends with the wallet itself: the stored key, masked, with a
    // reveal button and a copy button. The key is read back from secure storage rather than
    // remembered from the activation field, because a relaunched app never saw that field.
    // -----------------------------------------------------------------------

    /// <summary>The stored key in full. Only the copy action and a deliberate reveal use it.</summary>
    public string? StoredAccountKey { get => _storedAccountKey; private set
        {
            if (!Set(ref _storedAccountKey, value)) return;
            Notify(nameof(HasStoredAccountKey));
            Notify(nameof(AccountKeyDisplay));
        }
    }

    public bool HasStoredAccountKey => !string.IsNullOrEmpty(StoredAccountKey);

    public bool IsAccountKeyRevealed { get => _isAccountKeyRevealed; set
        {
            if (Set(ref _isAccountKeyRevealed, value)) Notify(nameof(AccountKeyDisplay));
        }
    }

    /// <summary>
    /// Masks every character with a bullet and keeps the dashes, which is exactly what the
    /// Windows page does: <c>HW-7F3K-9QXM</c> reads as <c>••-••••-••••</c>.
    /// </summary>
    public string AccountKeyDisplay => StoredAccountKey is not { Length: > 0 } key
        ? string.Empty
        : IsAccountKeyRevealed
            ? key
            : new string(key.Select(character => character == '-' ? '-' : '•').ToArray());

    /// <summary>Re-reads the stored key. Safe to call on every status change.</summary>
    public void RefreshStoredAccountKey()
    {
        IsAccountKeyRevealed = false;
        StoredAccountKey = _service.TryReadStoredAccountKey();
    }
    public UiStatus Status { get; } = new();
    public ICommand ActivateCommand { get; }
    public ICommand RefreshStatusCommand { get; }
    public ICommand RefreshCreditsCommand { get; }
    public ICommand DeactivateCommand { get; }
    public ICommand PurchaseCommand { get; }
    public ICommand ManageCommand { get; }
    public bool CanRunOperation => Volatile.Read(ref _operationRunning) == 0;

    /// <summary>
    /// True only while <see cref="ActivateAsync"/> is in flight.
    ///
    /// Windows relabels the Activate button to "Activating..." inside a try/finally in the page's
    /// code-behind rather than through a flag. A view model cannot reach into a page like that, so
    /// the same state is published here and the button binds to it. It is deliberately narrower
    /// than <see cref="CanRunOperation"/>, which is also false during a status or credits refresh:
    /// only activation may claim the activation label.
    /// </summary>
    public bool IsActivating { get => _isActivating; private set => Set(ref _isActivating, value); }

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        if (!TryBeginOperation()) return;
        IsActivating = true;
        if (string.IsNullOrWhiteSpace(AccountKey))
        {
            ActivationError = "Enter the account key from your purchase email.";
            Status.Failure("account.key_required", "Enter the account key from your purchase email.");
            IsActivating = false;
            EndOperation();
            return;
        }

        ActivationError = null;
        Status.Busy("Validating account…");
        var submittedKey = AccountKey;
        AccountKey = string.Empty;
        try
        {
            var identity = _deviceIdentity.GetDeviceIdentity();
            if (identity.IsFailure)
            {
                Status.Failure(identity.Error!.Code, identity.Error.Message);
                return;
            }

            var result = await _service.ActivateAsync(new(
                submittedKey,
                identity.Value!.Id,
                _deviceName), cancellationToken);
            if (result.IsFailure)
            {
                ActivationError = result.Failure!.Message;
                Status.Failure(Code(result.Failure!), result.Failure!.Message);
                return;
            }

            ApplyDetails(result.Value!);
            Status.Success("Account activated; the key is stored securely and is never displayed.");
        }
        catch (OperationCanceledException)
        {
            ActivationError = "Account activation was cancelled.";
            Status.Failure("account.cancelled", "Account activation was cancelled.");
        }
        catch (Exception)
        {
            ActivationError = "The account could not be activated.";
            Status.Failure("account.activation_failed", "The account could not be activated.");
        }
        finally
        {
            // Clear a value assigned by the UI while validation was in flight too.
            AccountKey = string.Empty;
            IsActivating = false;
            EndOperation();
        }
    }

    /// <summary>
    /// Loads the account status at startup. Inside the 24-hour validation cache
    /// this answers from the cached verdict and makes no network call, which is
    /// what macOS and Windows do at launch.
    /// </summary>
    public Task LoadAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(forceRevalidate: false, cancellationToken);

    /// <summary>
    /// Asks the server again, whatever the validation cache says. This is the
    /// explicit refresh the user pressed, so it also brings back the customer
    /// email and the expiry, which the cached verdict does not hold.
    /// </summary>
    public Task RefreshStatusAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(forceRevalidate: true, cancellationToken);

    private async Task LoadAsync(bool forceRevalidate, CancellationToken cancellationToken)
    {
        if (!TryBeginOperation()) return;
        Status.Busy("Loading account status…");
        try
        {
            var identity = _deviceIdentity.GetDeviceIdentity();
            if (identity.IsFailure)
            {
                Status.Failure(identity.Error!.Code, identity.Error.Message);
                return;
            }

            var result = await _service.GetStatusAsync(
                identity.Value!.Id, _deviceName, forceRevalidate, cancellationToken);
            if (result.Failure?.Code == CloudAccountFailureCode.MissingAccountKey)
            {
                ClearDetails();
                Status.Success("No HyperWhisper Cloud account is activated on this device.");
                return;
            }
            if (result.IsFailure)
            {
                Status.Failure(Code(result.Failure!), result.Failure!.Message);
                return;
            }

            ApplyDetails(result.Value!);
            Status.Success($"Account status refreshed: {AccountState}.");
        }
        catch (OperationCanceledException)
        {
            Status.Failure("account.cancelled", "Account status refresh was cancelled.");
        }
        catch (Exception)
        {
            Status.Failure("account.status_failed", "Account status could not be refreshed.");
        }
        finally { EndOperation(); }
    }

    public async Task RefreshCreditsAsync(CancellationToken cancellationToken = default)
    {
        if (!TryBeginOperation()) return;
        Status.Busy("Refreshing Cloud credits…");
        IsLoadingCredits = true;
        try
        {
            var result = await _service.RefreshCreditsAsync(cancellationToken);
            if (result.IsFailure)
            {
                CreditsError = result.Failure!.Message;
                Status.Failure(Code(result.Failure!), result.Failure!.Message);
                return;
            }

            HasAccount = true;
            CreditsError = null;
            _balance = result.Value!;
            Credits = result.Value!.CreditsRemaining.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            MinutesRemaining = result.Value.MinutesRemaining.ToString(System.Globalization.CultureInfo.InvariantCulture);
            NotifyBalance();
            Status.Success("Cloud credits refreshed.");
        }
        catch (OperationCanceledException)
        {
            CreditsError = "Credit refresh was cancelled.";
            Status.Failure("account.cancelled", "Credit refresh was cancelled.");
        }
        catch (Exception)
        {
            CreditsError = "Cloud credits could not be refreshed.";
            Status.Failure("account.credits_failed", "Cloud credits could not be refreshed.");
        }
        finally { IsLoadingCredits = false; NotifyBalance(); EndOperation(); }
    }

    public async Task DeactivateAsync(CancellationToken cancellationToken = default)
    {
        if (!TryBeginOperation()) return;
        Status.Busy("Removing this device's account key…");
        try
        {
            var result = await _service.DeactivateAsync(cancellationToken);
            if (result.IsFailure)
            {
                Status.Failure(Code(result.Failure!), result.Failure!.Message);
                return;
            }

            ClearDetails();
            Status.Success(result.Value!.ServerRevocationSupported
                ? "Account deactivated and the local key was removed."
                : "Account removed from this device. Removal is local and works offline; the server does not support remote key revocation, so manage the account online if needed.");
        }
        catch (OperationCanceledException)
        {
            Status.Failure("account.cancelled", "Account removal was cancelled.");
        }
        catch (Exception)
        {
            Status.Failure("account.deactivation_failed", "The local account key could not be removed.");
        }
        finally { EndOperation(); }
    }

    public Task OpenPurchaseAsync() => OpenAsync(CloudAccountLinks.Purchase, "purchase");
    public Task OpenManageAsync() => OpenAsync(CloudAccountLinks.ManageAccount, "account portal");

    private Task OpenAsync(Uri uri, string label)
    {
        if (!TryBeginOperation()) return Task.CompletedTask;
        try
        {
            var result = _openUri(uri);
            if (result.IsFailure) Status.Failure(result.Error!.Code, result.Error.Message);
            else Status.Success($"Opened the {label}.");
        }
        catch
        {
            Status.Failure("account.link_failed", "The account page could not be opened.");
        }
        finally { EndOperation(); }
        return Task.CompletedTask;
    }

    private bool TryBeginOperation()
    {
        if (Interlocked.CompareExchange(ref _operationRunning, 1, 0) != 0) return false;
        Notify(nameof(CanRunOperation));
        RaiseCommandStates();
        return true;
    }

    private void EndOperation()
    {
        if (Interlocked.Exchange(ref _operationRunning, 0) == 0) return;
        Notify(nameof(CanRunOperation));
        RaiseCommandStates();
    }

    private void NotifyLicenseState()
    {
        Notify(nameof(IsActiveAccount));
        Notify(nameof(IsUnlicensed));
        Notify(nameof(HasLicenseProblem));
        Notify(nameof(AccountTypeLabel));
    }

    private void NotifyBalance()
    {
        Notify(nameof(DollarBalance));
        Notify(nameof(CostPerMinute));
        Notify(nameof(AccountTypeLabel));
        Notify(nameof(DailyReset));
        Notify(nameof(HasDailyReset));
        Notify(nameof(IsCreditsExhausted));
        Notify(nameof(IsCreditsLow));
        Notify(nameof(HasCredits));
        Notify(nameof(HasCreditsError));
        Notify(nameof(ShowCreditsLoading));
    }

    private void RaiseCommandStates()
    {
        ((AsyncCommand)ActivateCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)RefreshStatusCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)RefreshCreditsCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)DeactivateCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)PurchaseCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)ManageCommand).RaiseCanExecuteChanged();
    }

    private void ApplyDetails(CloudAccountDetails details)
    {
        HasAccount = true;
        AccountState = details.Status switch
        {
            CloudAccountStatus.Active => "Active",
            CloudAccountStatus.Expired => "Expired",
            _ => "Invalid",
        };
        CustomerEmail = details.CustomerEmail;
        CustomerId = details.CustomerId;
        ExpiresAt = details.ExpiresAt?.ToString("u", System.Globalization.CultureInfo.InvariantCulture);
        if (!details.IsActive)
        {
            Credits = null;
            MinutesRemaining = null;
            _balance = null;
        }
        ActivationError = null;
        RefreshStoredAccountKey();
        NotifyBalance();
    }

    private void ClearDetails()
    {
        HasAccount = false;
        AccountState = "Not activated";
        CustomerEmail = null;
        CustomerId = null;
        ExpiresAt = null;
        Credits = null;
        MinutesRemaining = null;
        AccountKey = string.Empty;
        _balance = null;
        CreditsError = null;
        ActivationError = null;
        StoredAccountKey = null;
        IsAccountKeyRevealed = false;
        NotifyBalance();
    }

    private static string NormalizeDeviceName(string? value)
    {
        var normalized = new string((value ?? string.Empty)
            .Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (normalized.Length > MaximumDeviceNameLength)
            normalized = normalized[..MaximumDeviceNameLength];
        return normalized.Length == 0 ? "Linux device" : normalized;
    }

    private static string Code(CloudAccountFailure failure) =>
        $"account.{failure.Code.ToString().ToLowerInvariant()}";
}
