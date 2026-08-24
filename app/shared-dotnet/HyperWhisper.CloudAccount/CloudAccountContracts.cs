namespace HyperWhisper.CloudAccount;

public enum CloudAccountStatus
{
    Active,
    Expired,
    Invalid,
}

public enum CloudAccountFailureCode
{
    InvalidRequest,
    MissingAccountKey,
    CredentialReadFailed,
    CredentialWriteFailed,
    CredentialDeleteFailed,
    Rejected,
    RateLimited,
    ServerUnavailable,
    InvalidResponse,
    ResponseTooLarge,
    TimedOut,
    Cancelled,
    NetworkFailure,
}

public sealed record CloudAccountFailure(CloudAccountFailureCode Code, string Message);

public sealed record CloudAccountResult<T>(T? Value, CloudAccountFailure? Failure)
{
    public bool IsSuccess => Failure is null;
    public bool IsFailure => Failure is not null;

    public static CloudAccountResult<T> Success(T value) => new(value, null);

    public static CloudAccountResult<T> Failed(CloudAccountFailureCode code, string message) =>
        new(default, new CloudAccountFailure(code, message));
}

public sealed record CloudAccountActivation(
    string AccountKey,
    string DeviceId,
    string DeviceName);

public sealed record CloudAccountDetails(
    CloudAccountStatus Status,
    string? CustomerId,
    string? CustomerEmail,
    DateTimeOffset? ExpiresAt)
{
    public bool IsActive => Status == CloudAccountStatus.Active;
}

public sealed record CloudCreditBalance(
    double CreditsRemaining,
    int MinutesRemaining,
    double CreditsPerMinute,
    bool IsLicensed,
    bool IsAnonymous,
    DateTimeOffset? ResetsAt,
    string? CustomerId,
    string? Message);

/// <summary>
/// Acknowledgement from the compatibility deactivation endpoint. The current
/// server contract is local-only and does not revoke an account key remotely.
/// </summary>
public sealed record CloudAccountDeactivation(
    bool ServerAcknowledged,
    bool ServerRevocationSupported);

public static class CloudAccountLinks
{
    public static Uri Purchase => new("https://www.hyperwhisper.com/credits");
    public static Uri ManageAccount => new("https://www.hyperwhisper.com/user");

    public static Uri PurchaseFor(string identifier, bool isAccountKey)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException("A purchase identifier is required.", nameof(identifier));

        var parameter = isAccountKey ? "license_key" : "device_id";
        return new Uri($"{Purchase}?{parameter}={Uri.EscapeDataString(identifier.Trim())}");
    }
}
