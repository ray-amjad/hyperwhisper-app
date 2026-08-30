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
    public string AccountState { get => _accountState; private set => Set(ref _accountState, value); }
    public string? CustomerEmail { get => _customerEmail; private set { if (Set(ref _customerEmail, value)) Notify(nameof(HasCustomerEmail)); } }
    public string? CustomerId { get => _customerId; private set { if (Set(ref _customerId, value)) Notify(nameof(HasCustomerId)); } }
    public string? ExpiresAt { get => _expiresAt; private set { if (Set(ref _expiresAt, value)) Notify(nameof(HasExpiry)); } }
    public string? Credits { get => _credits; private set { if (Set(ref _credits, value)) Notify(nameof(HasCredits)); } }
    public string? MinutesRemaining { get => _minutesRemaining; private set => Set(ref _minutesRemaining, value); }
    public bool HasAccount { get => _hasAccount; private set => Set(ref _hasAccount, value); }
    public bool HasCustomerEmail => !string.IsNullOrEmpty(CustomerEmail);
    public bool HasCustomerId => !string.IsNullOrEmpty(CustomerId);
    public bool HasExpiry => !string.IsNullOrEmpty(ExpiresAt);
    public bool HasCredits => !string.IsNullOrEmpty(Credits);
    public Uri PurchaseUrl => CloudAccountLinks.Purchase;
    public UiStatus Status { get; } = new();
    public ICommand ActivateCommand { get; }
    public ICommand RefreshStatusCommand { get; }
    public ICommand RefreshCreditsCommand { get; }
    public ICommand DeactivateCommand { get; }
    public ICommand PurchaseCommand { get; }
    public ICommand ManageCommand { get; }
    public bool CanRunOperation => Volatile.Read(ref _operationRunning) == 0;

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        if (!TryBeginOperation()) return;
        if (string.IsNullOrWhiteSpace(AccountKey))
        {
            Status.Failure("account.key_required", "Enter the account key from your purchase email.");
            EndOperation();
            return;
        }

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
                Status.Failure(Code(result.Failure!), result.Failure!.Message);
                return;
            }

            ApplyDetails(result.Value!);
            Status.Success("Account activated; the key is stored securely and is never displayed.");
        }
        catch (OperationCanceledException)
        {
            Status.Failure("account.cancelled", "Account activation was cancelled.");
        }
        catch (Exception)
        {
            Status.Failure("account.activation_failed", "The account could not be activated.");
        }
        finally
        {
            // Clear a value assigned by the UI while validation was in flight too.
            AccountKey = string.Empty;
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
        try
        {
            var result = await _service.RefreshCreditsAsync(cancellationToken);
            if (result.IsFailure)
            {
                Status.Failure(Code(result.Failure!), result.Failure!.Message);
                return;
            }

            HasAccount = true;
            Credits = result.Value!.CreditsRemaining.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            MinutesRemaining = result.Value.MinutesRemaining.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Status.Success("Cloud credits refreshed.");
        }
        catch (OperationCanceledException)
        {
            Status.Failure("account.cancelled", "Credit refresh was cancelled.");
        }
        catch (Exception)
        {
            Status.Failure("account.credits_failed", "Cloud credits could not be refreshed.");
        }
        finally { EndOperation(); }
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
        }
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
