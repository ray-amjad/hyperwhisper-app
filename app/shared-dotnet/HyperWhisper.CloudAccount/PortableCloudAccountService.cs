using System.Buffers;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.CloudAccount;

/// <summary>
/// Portable HyperWhisper Cloud account lifecycle. The platform owns secure
/// credential persistence; this component owns bounded, cancellation-aware HTTP.
/// </summary>
public sealed class PortableCloudAccountService : IDisposable
{
    public const string CredentialResource = "HyperWhisper";
    public const string CredentialAccount = "LicenseKey";

    private static readonly Uri ValidateEndpoint =
        new("https://www.hyperwhisper.com/api/license/validate");
    private static readonly Uri UsageEndpoint =
        new("https://transcribe-prod-v2.hyperwhisper.com/usage");

    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly ICredentialStore _credentials;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    public PortableCloudAccountService(ICredentialStore credentials)
        : this(credentials, CreateHttpClient(), DefaultTimeout, ownsHttpClient: true)
    {
    }

    public PortableCloudAccountService(
        ICredentialStore credentials,
        HttpClient httpClient,
        TimeSpan? timeout = null)
        : this(credentials, httpClient, timeout ?? DefaultTimeout, ownsHttpClient: false)
    {
    }

    private PortableCloudAccountService(
        ICredentialStore credentials,
        HttpClient httpClient,
        TimeSpan timeout,
        bool ownsHttpClient)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be between zero and one minute.");
        _timeout = timeout;
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<CloudAccountResult<CloudAccountDetails>> ActivateAsync(
        CloudAccountActivation activation,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(activation);

        var validationFailure = ValidateActivation(activation);
        if (validationFailure is not null)
            return CloudAccountResult<CloudAccountDetails>.Failed(validationFailure.Code, validationFailure.Message);

        var key = activation.AccountKey.Trim();
        var request = new HttpRequestMessage(HttpMethod.Post, ValidateEndpoint)
        {
            Content = JsonContent.Create(new ValidateRequest(
                key,
                activation.DeviceId.Trim(),
                activation.DeviceName.Trim())),
        };
        var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsFailure)
            return CloudAccountResult<CloudAccountDetails>.Failed(response.Failure!.Code, response.Failure.Message);

        var parsed = ParseValidation(response.Value!);
        if (parsed.IsFailure)
            return parsed;
        if (parsed.Value is not { IsActive: true } details)
            return CloudAccountResult<CloudAccountDetails>.Failed(
                CloudAccountFailureCode.Rejected, "The account key was rejected.");

        byte[]? secret = null;
        try
        {
            secret = Encoding.UTF8.GetBytes(key);
            var stored = _credentials.Write(CredentialResource, CredentialAccount, secret);
            return stored.IsSuccess
                ? CloudAccountResult<CloudAccountDetails>.Success(details)
                : CloudAccountResult<CloudAccountDetails>.Failed(
                    CloudAccountFailureCode.CredentialWriteFailed,
                    "The account key could not be stored securely.");
        }
        finally
        {
            if (secret is not null) CryptographicOperations.ZeroMemory(secret);
        }
    }

    public async Task<CloudAccountResult<CloudAccountDetails>> GetStatusAsync(
        string deviceId,
        string deviceName,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(deviceName))
            return CloudAccountResult<CloudAccountDetails>.Failed(
                CloudAccountFailureCode.InvalidRequest,
                "Device identity and device name are required.");

        var key = ReadAccountKey();
        if (key.IsFailure)
            return CloudAccountResult<CloudAccountDetails>.Failed(key.Failure!.Code, key.Failure.Message);

        return await ValidateWithoutPersistingAsync(
            key.Value!, deviceId.Trim(), deviceName.Trim(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<CloudAccountResult<CloudCreditBalance>> RefreshCreditsAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = ReadAccountKey();
        if (key.IsFailure)
            return CloudAccountResult<CloudCreditBalance>.Failed(key.Failure!.Code, key.Failure.Message);

        var uri = new Uri($"{UsageEndpoint}?identifier={Uri.EscapeDataString(key.Value!)}&force_refresh=true");
        var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), cancellationToken)
            .ConfigureAwait(false);
        if (response.IsFailure)
            return CloudAccountResult<CloudCreditBalance>.Failed(response.Failure!.Code, response.Failure.Message);

        try
        {
            var wire = JsonSerializer.Deserialize<CreditsResponse>(response.Value!);
            if (wire?.CreditsRemaining is not { } credits
                || wire.MinutesRemaining is not { } minutes
                || wire.CreditsPerMinute is not { } creditsPerMinute
                || !double.IsFinite(credits)
                || !double.IsFinite(creditsPerMinute)
                || credits < 0 || creditsPerMinute < 0 || minutes < 0)
            {
                return CloudAccountResult<CloudCreditBalance>.Failed(
                    CloudAccountFailureCode.InvalidResponse,
                    "The credit service returned an invalid response.");
            }

            return CloudAccountResult<CloudCreditBalance>.Success(new(
                credits,
                minutes,
                creditsPerMinute,
                wire.IsLicensed,
                wire.IsAnonymous,
                wire.ResetsAt,
                Bound(wire.CustomerId, 256),
                Bound(wire.Message, 1024)));
        }
        catch (JsonException)
        {
            return CloudAccountResult<CloudCreditBalance>.Failed(
                CloudAccountFailureCode.InvalidResponse,
                "The credit service returned an invalid response.");
        }
    }

    /// <summary>
    /// Removes this device's account key. Local only — no network call.
    /// <para>
    /// Deactivation used to POST to /api/license/deactivate first and delete the
    /// stored key only when the server replied <c>{"success":true}</c>. That
    /// endpoint is an explicit no-op stub, so the round-trip bought nothing and
    /// cost an offline user the ability to remove their key at all (issue #290).
    /// macOS does the same thing this method now does: clear the local store and
    /// report success (`LicenseNetworkService.deactivateLicense`).
    /// </para>
    /// </summary>
    public Task<CloudAccountResult<CloudAccountDeactivation>> DeactivateAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        // Read first so "no key is stored" stays a distinct verdict rather than a
        // silent success. The key's value itself is no longer needed.
        var key = ReadAccountKey();
        if (key.IsFailure)
            return Task.FromResult(
                CloudAccountResult<CloudAccountDeactivation>.Failed(key.Failure!.Code, key.Failure.Message));

        // The delete must stay on ICredentialStore. The Linux key lives in the
        // platform keyring under (CredentialResource, CredentialAccount), and
        // Delete reports a locked or failing keyring — the shared core's
        // KeyValueStore trait has no error channel, so routing this through
        // license_clear_stored_license would swallow that failure and report a
        // removal that never happened.
        var deleted = _credentials.Delete(CredentialResource, CredentialAccount);
        return Task.FromResult(deleted.IsSuccess
            ? CloudAccountResult<CloudAccountDeactivation>.Success(new(false, false))
            : CloudAccountResult<CloudAccountDeactivation>.Failed(
                CloudAccountFailureCode.CredentialDeleteFailed,
                "The local account key could not be removed securely."));
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_ownsHttpClient) _httpClient.Dispose();
        _disposed = true;
    }

    private async Task<CloudAccountResult<CloudAccountDetails>> ValidateWithoutPersistingAsync(
        string key,
        string deviceId,
        string deviceName,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ValidateEndpoint)
        {
            Content = JsonContent.Create(new ValidateRequest(key, deviceId, deviceName)),
        };
        var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.IsSuccess
            ? ParseValidation(response.Value!)
            : CloudAccountResult<CloudAccountDetails>.Failed(response.Failure!.Code, response.Failure.Message);
    }

    private CloudAccountResult<string> ReadAccountKey()
    {
        var read = _credentials.Read(CredentialResource, CredentialAccount);
        if (read.IsFailure)
            return CloudAccountResult<string>.Failed(
                CloudAccountFailureCode.CredentialReadFailed,
                "The account key could not be read from secure storage.");
        if (read.Value is not { Length: > 0 } bytes)
            return CloudAccountResult<string>.Failed(
                CloudAccountFailureCode.MissingAccountKey,
                "No account key is stored.");

        try
        {
            var key = new UTF8Encoding(false, true).GetString(bytes).Trim();
            return key.Length == 0
                ? CloudAccountResult<string>.Failed(
                    CloudAccountFailureCode.MissingAccountKey,
                    "No account key is stored.")
                : CloudAccountResult<string>.Success(key);
        }
        catch (DecoderFallbackException)
        {
            return CloudAccountResult<string>.Failed(
                CloudAccountFailureCode.CredentialReadFailed,
                "The stored account key is invalid.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task<CloudAccountResult<byte[]>> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using (request)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(_timeout);
            try
            {
                using var response = await _httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                var body = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
                if (body.IsFailure) return body;
                if (response.IsSuccessStatusCode) return body;

                return CloudAccountResult<byte[]>.Failed(
                    response.StatusCode switch
                    {
                        HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                            CloudAccountFailureCode.Rejected,
                        HttpStatusCode.TooManyRequests => CloudAccountFailureCode.RateLimited,
                        _ when (int)response.StatusCode >= 500 => CloudAccountFailureCode.ServerUnavailable,
                        _ => CloudAccountFailureCode.NetworkFailure,
                    },
                    response.StatusCode switch
                    {
                        HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                            "The account key was rejected.",
                        HttpStatusCode.TooManyRequests => "The account service is rate limited. Try again later.",
                        _ when (int)response.StatusCode >= 500 => "The account service is temporarily unavailable.",
                        _ => $"The account request failed (HTTP {(int)response.StatusCode}).",
                    });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CloudAccountResult<byte[]>.Failed(
                    CloudAccountFailureCode.Cancelled, "The account request was cancelled.");
            }
            catch (OperationCanceledException)
            {
                return CloudAccountResult<byte[]>.Failed(
                    CloudAccountFailureCode.TimedOut, "The account request timed out.");
            }
            catch (HttpRequestException)
            {
                return CloudAccountResult<byte[]>.Failed(
                    CloudAccountFailureCode.NetworkFailure, "The account service could not be reached.");
            }
        }
    }

    private static async Task<CloudAccountResult<byte[]>> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
            return CloudAccountResult<byte[]>.Failed(
                CloudAccountFailureCode.ResponseTooLarge, "The account response was too large.");

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var rented = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(rented.AsMemory(0, rented.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > MaximumResponseBytes)
                    return CloudAccountResult<byte[]>.Failed(
                        CloudAccountFailureCode.ResponseTooLarge, "The account response was too large.");
                output.Write(rented, 0, read);
            }
            return CloudAccountResult<byte[]>.Success(output.ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rented);
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static CloudAccountResult<CloudAccountDetails> ParseValidation(byte[] body)
    {
        try
        {
            var wire = JsonSerializer.Deserialize<ValidateResponse>(body);
            if (wire is null)
                throw new JsonException();

            var status = wire.Status?.ToLowerInvariant() switch
            {
                "active" => CloudAccountStatus.Active,
                "expired" => CloudAccountStatus.Expired,
                "revoked" or "invalid" => CloudAccountStatus.Invalid,
                _ when wire.Valid => CloudAccountStatus.Active,
                _ when wire.Expired => CloudAccountStatus.Expired,
                _ => CloudAccountStatus.Invalid,
            };
            DateTimeOffset? expires = null;
            if (!string.IsNullOrWhiteSpace(wire.ExpiresAt)
                && !DateTimeOffset.TryParse(wire.ExpiresAt, out var parsedExpiry))
            {
                return CloudAccountResult<CloudAccountDetails>.Failed(
                    CloudAccountFailureCode.InvalidResponse,
                    "The account service returned an invalid response.");
            }
            else if (!string.IsNullOrWhiteSpace(wire.ExpiresAt)
                && DateTimeOffset.TryParse(wire.ExpiresAt, out parsedExpiry))
            {
                expires = parsedExpiry;
            }

            return CloudAccountResult<CloudAccountDetails>.Success(new(
                status,
                Bound(wire.CustomerId, 256),
                Bound(wire.CustomerEmail, 320),
                expires));
        }
        catch (JsonException)
        {
            return CloudAccountResult<CloudAccountDetails>.Failed(
                CloudAccountFailureCode.InvalidResponse,
                "The account service returned an invalid response.");
        }
    }

    private static CloudAccountFailure? ValidateActivation(CloudAccountActivation activation)
    {
        if (string.IsNullOrWhiteSpace(activation.AccountKey))
            return new(CloudAccountFailureCode.MissingAccountKey, "An account key is required.");
        if (string.IsNullOrWhiteSpace(activation.DeviceId)
            || string.IsNullOrWhiteSpace(activation.DeviceName))
            return new(CloudAccountFailureCode.InvalidRequest, "Device identity and device name are required.");
        if (activation.AccountKey.Length > 4096
            || activation.DeviceId.Length > 512
            || activation.DeviceName.Length > 512
            || activation.AccountKey.Contains('\0')
            || activation.DeviceId.Contains('\0')
            || activation.DeviceName.Contains('\0'))
            return new(CloudAccountFailureCode.InvalidRequest, "Account activation fields are invalid.");
        return null;
    }

    private static string? Bound(string? value, int maximumLength) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= maximumLength ? value : value[..maximumLength];

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed record ValidateRequest(
        [property: JsonPropertyName("license_key")] string LicenseKey,
        [property: JsonPropertyName("device_id")] string DeviceId,
        [property: JsonPropertyName("device_name")] string DeviceName);

    private sealed class ValidateResponse
    {
        [JsonPropertyName("valid")] public bool Valid { get; init; }
        [JsonPropertyName("expired")] public bool Expired { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("customer_id")] public string? CustomerId { get; init; }
        [JsonPropertyName("customer_email")] public string? CustomerEmail { get; init; }
        [JsonPropertyName("expires_at")] public string? ExpiresAt { get; init; }
    }

    private sealed class CreditsResponse
    {
        [JsonPropertyName("credits_remaining")] public double? CreditsRemaining { get; init; }
        [JsonPropertyName("minutes_remaining")] public int? MinutesRemaining { get; init; }
        [JsonPropertyName("credits_per_minute")] public double? CreditsPerMinute { get; init; }
        [JsonPropertyName("is_licensed")] public bool IsLicensed { get; init; }
        [JsonPropertyName("is_anonymous")] public bool IsAnonymous { get; init; }
        [JsonPropertyName("resets_at")] public DateTimeOffset? ResetsAt { get; init; }
        [JsonPropertyName("customer_id")] public string? CustomerId { get; init; }
        [JsonPropertyName("message")] public string? Message { get; init; }
    }
}
