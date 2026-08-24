using System.Buffers;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.SharedCore;

namespace HyperWhisper.CloudAccount;

/// <summary>
/// HyperWhisper Cloud account lifecycle.
/// <para>
/// The <c>hw-license</c> Rust core owns the account verdict: it builds the
/// validate request, parses the response, maps the server's status strings, and
/// keeps the 24-hour validation cache and the 7-day offline grace. macOS
/// (<c>LicenseNetworkService.swift</c>) and Windows
/// (<c>LicenseNetworkService.cs</c>) are wired to the same functions, so all
/// three platforms agree on what a given server reply means (issue #290).
/// </para>
/// <para>
/// This component keeps only what the core cannot own: bounded,
/// cancellation-aware HTTP, the transient-versus-terminal split for a non-200
/// reply, and every write whose failure the user has to see. The account key
/// itself is written and deleted through <see cref="ICredentialStore"/>, which
/// reports a locked or failing keyring; the core's store trait cannot.
/// </para>
/// </summary>
public sealed class PortableCloudAccountService : IDisposable
{
    public const string CredentialResource = "HyperWhisper";
    public const string CredentialAccount = "LicenseKey";

    private static readonly Uri UsageEndpoint =
        new("https://transcribe-prod-v2.hyperwhisper.com/usage");

    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly ICredentialStore _credentials;
    private readonly IPortableKeyValueStore _licenseState;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _time;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    public PortableCloudAccountService(
        ICredentialStore credentials,
        IPortableKeyValueStore licenseState)
        : this(credentials, licenseState, CreateHttpClient(), DefaultTimeout, TimeProvider.System, ownsHttpClient: true)
    {
    }

    public PortableCloudAccountService(
        ICredentialStore credentials,
        IPortableKeyValueStore licenseState,
        HttpClient httpClient,
        TimeSpan? timeout = null,
        TimeProvider? timeProvider = null)
        : this(credentials, licenseState, httpClient, timeout ?? DefaultTimeout,
            timeProvider ?? TimeProvider.System, ownsHttpClient: false)
    {
    }

    private PortableCloudAccountService(
        ICredentialStore credentials,
        IPortableKeyValueStore licenseState,
        HttpClient httpClient,
        TimeSpan timeout,
        TimeProvider timeProvider,
        bool ownsHttpClient)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _licenseState = licenseState ?? throw new ArgumentNullException(nameof(licenseState));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be between zero and one minute.");
        _timeout = timeout;
        _ownsHttpClient = ownsHttpClient;
    }

    /// <summary>
    /// Validates an account key and, when the server accepts it, stores it.
    /// <para>
    /// A transient failure falls back to the cached verdict only when the
    /// submitted key is the key already on file. The cached verdict belongs to
    /// that key, so honouring it for a different or first-time key would report
    /// an unverified key as active. macOS and Windows apply the same rule.
    /// </para>
    /// </summary>
    public async Task<CloudAccountResult<CloudAccountDetails>> ActivateAsync(
        CloudAccountActivation activation,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(activation);

        // The core rejects an empty key, but it has no length, NUL, or
        // device-identity checks. Those stay here.
        var validationFailure = ValidateActivation(activation);
        if (validationFailure is not null)
            return CloudAccountResult<CloudAccountDetails>.Failed(validationFailure.Code, validationFailure.Message);

        var key = activation.AccountKey.Trim();
        var attempt = await ValidateAsync(
            key, activation.DeviceId.Trim(), activation.DeviceName.Trim(), cancellationToken)
            .ConfigureAwait(false);
        var now = NowUnixSeconds();

        if (attempt.Kind == ValidateAttemptKind.Fatal)
            return CloudAccountResult<CloudAccountDetails>.Failed(attempt.Failure!.Code, attempt.Failure.Message);

        if (attempt.Kind == ValidateAttemptKind.Transient)
        {
            var cached = OfflineFallbackForKey(key, attempt.Failure!, now);
            if (cached.IsFailure) return cached;
            return cached.Value!.IsActive
                ? cached
                : CloudAccountResult<CloudAccountDetails>.Failed(
                    CloudAccountFailureCode.Rejected, "The account key was rejected.");
        }

        var outcome = attempt.Outcome!;
        if (outcome.Status != PortableLicenseStatus.Active)
        {
            // Guarded by the core: the key is not stored, and the cached verdict
            // for a different key on file is left alone.
            PortableLicenseCore.PersistValidationVerdict(_licenseState, outcome.Status, key, now);
            return CloudAccountResult<CloudAccountDetails>.Failed(
                CloudAccountFailureCode.Rejected, RejectionMessage(outcome, key));
        }

        byte[]? secret = null;
        try
        {
            // The checked write comes before the verdict is cached. A locked
            // keyring must not leave an Active verdict cached against a key that
            // was never stored — the offline grace would then report an account
            // this device cannot use.
            secret = Encoding.UTF8.GetBytes(key);
            var stored = _credentials.Write(CredentialResource, CredentialAccount, secret);
            if (stored.IsFailure)
                return CloudAccountResult<CloudAccountDetails>.Failed(
                    CloudAccountFailureCode.CredentialWriteFailed,
                    "The account key could not be stored securely.");
        }
        finally
        {
            if (secret is not null) CryptographicOperations.ZeroMemory(secret);
        }

        PortableLicenseCore.PersistValidationVerdict(_licenseState, outcome.Status, key, now);
        return CloudAccountResult<CloudAccountDetails>.Success(Details(outcome));
    }

    /// <summary>
    /// The account status for the key on file.
    /// <para>
    /// Inside the 24-hour validation cache this answers from the cached verdict
    /// and makes no network call, which is what macOS and Windows do at launch.
    /// Pass <paramref name="forceRevalidate"/> for an explicit, user-triggered
    /// refresh. When the server cannot be reached, the cached verdict is used for
    /// as long as the 7-day offline grace allows.
    /// </para>
    /// </summary>
    public async Task<CloudAccountResult<CloudAccountDetails>> GetStatusAsync(
        string deviceId,
        string deviceName,
        bool forceRevalidate = false,
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

        var now = NowUnixSeconds();
        if (!forceRevalidate
            && !PortableLicenseCore.ShouldRevalidate(_licenseState, now)
            && PortableLicenseCore.CachedStatusWithinGrace(_licenseState, now) is { } cached)
        {
            // The core caches the verdict, not the customer record, on every
            // platform. The customer email and the expiry come back on the next
            // revalidation, or immediately from an explicit refresh.
            return CloudAccountResult<CloudAccountDetails>.Success(
                new CloudAccountDetails(Map(cached), null, null, null));
        }

        var attempt = await ValidateAsync(
            key.Value!, deviceId.Trim(), deviceName.Trim(), cancellationToken).ConfigureAwait(false);

        if (attempt.Kind == ValidateAttemptKind.Fatal)
            return CloudAccountResult<CloudAccountDetails>.Failed(attempt.Failure!.Code, attempt.Failure.Message);

        if (attempt.Kind == ValidateAttemptKind.Transient)
            return OfflineFallbackForKey(key.Value!, attempt.Failure!, NowUnixSeconds());

        var outcome = attempt.Outcome!;
        PortableLicenseCore.PersistValidationVerdict(_licenseState, outcome.Status, key.Value!, NowUnixSeconds());
        return CloudAccountResult<CloudAccountDetails>.Success(Details(outcome));
    }

    public async Task<CloudAccountResult<CloudCreditBalance>> RefreshCreditsAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = ReadAccountKey();
        if (key.IsFailure)
            return CloudAccountResult<CloudCreditBalance>.Failed(key.Failure!.Code, key.Failure.Message);

        var uri = new Uri($"{UsageEndpoint}?identifier={Uri.EscapeDataString(key.Value!)}&force_refresh=true");
        var transport = await SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), cancellationToken)
            .ConfigureAwait(false);
        if (transport.IsFailure)
            return CloudAccountResult<CloudCreditBalance>.Failed(transport.Failure!.Code, transport.Failure.Message);

        var response = transport.Value;
        if (!response.IsSuccess)
        {
            var failure = MapHttpFailure(response.StatusCode);
            return CloudAccountResult<CloudCreditBalance>.Failed(failure.Code, failure.Message);
        }

        try
        {
            var wire = JsonSerializer.Deserialize<CreditsResponse>(response.Body);
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
        if (deleted.IsFailure)
            return Task.FromResult(CloudAccountResult<CloudAccountDeactivation>.Failed(
                CloudAccountFailureCode.CredentialDeleteFailed,
                "The local account key could not be removed securely."));

        // The key is gone; the cached verdict must go with it, or the offline
        // grace would keep reporting an account this device no longer holds.
        // Clearing the key again through the core is a no-op (it is already
        // absent), and the remote trial-limit override deliberately survives.
        PortableLicenseCore.ClearStoredLicense(_licenseState);
        return Task.FromResult(
            CloudAccountResult<CloudAccountDeactivation>.Success(new(false, false)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_ownsHttpClient) _httpClient.Dispose();
        _disposed = true;
    }

    // =========================================================================
    // Validation
    // =========================================================================

    private enum ValidateAttemptKind
    {
        /// <summary>The server stated a verdict; the core mapped it.</summary>
        Verdict,

        /// <summary>Nothing was decided. The cached verdict may stand in.</summary>
        Transient,

        /// <summary>The request cannot be answered and must not be retried here.</summary>
        Fatal,
    }

    private sealed record ValidateAttempt(
        ValidateAttemptKind Kind,
        PortableValidationOutcome? Outcome,
        CloudAccountFailure? Failure);

    private async Task<ValidateAttempt> ValidateAsync(
        string key,
        string deviceId,
        string deviceName,
        CancellationToken cancellationToken)
    {
        // Endpoint, content type and body all come from the core, so the request
        // shape cannot drift from macOS and Windows.
        var built = PortableLicenseCore.BuildValidateRequest(key, deviceId, deviceName);
        var request = new HttpRequestMessage(HttpMethod.Post, built.Url)
        {
            Content = new ByteArrayContent(built.Body),
        };
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue(built.ContentType);

        var transport = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (transport.IsFailure)
        {
            var failure = transport.Failure!;
            var transient = failure.Code is CloudAccountFailureCode.TimedOut
                or CloudAccountFailureCode.NetworkFailure;
            return new ValidateAttempt(
                transient ? ValidateAttemptKind.Transient : ValidateAttemptKind.Fatal, null, failure);
        }

        var response = transport.Value;
        if (response.IsSuccess)
            return new ValidateAttempt(
                ValidateAttemptKind.Verdict,
                PortableLicenseCore.ParseValidateResponse(response.Body),
                null);

        var code = (int)response.StatusCode;

        // 429 and 5xx are congestion, not an entitlement decision. Caching the
        // Invalid the core would map them to is what downgraded a paying Linux
        // user; macOS and Windows both treat them as transient.
        if (response.StatusCode == HttpStatusCode.TooManyRequests || code >= 500)
            return new ValidateAttempt(ValidateAttemptKind.Transient, null, MapHttpFailure(response.StatusCode));

        // A redirect is not a verdict: the client never follows it, so nothing
        // was validated. This is stricter than macOS and Windows, which hand any
        // non-200 to the core, and it keeps a redirect to a hostile host from
        // reading as a server decision about the account.
        if (code is >= 300 and < 400)
            return new ValidateAttempt(ValidateAttemptKind.Fatal, null, MapHttpFailure(response.StatusCode));

        return new ValidateAttempt(
            ValidateAttemptKind.Verdict,
            PortableLicenseCore.HttpErrorOutcome(code, response.Body),
            null);
    }

    /// <summary>
    /// The cached verdict for <paramref name="key"/>, or the transient failure
    /// when there is nothing usable to fall back to.
    /// <para>
    /// The cache is a single global entry tied to the key on file, so a
    /// submitted key that differs from it never inherits its verdict. macOS and
    /// Windows carry the identical guard.
    /// </para>
    /// </summary>
    private CloudAccountResult<CloudAccountDetails> OfflineFallbackForKey(
        string key,
        CloudAccountFailure transientFailure,
        long nowUnixSeconds)
    {
        if (!string.Equals(PortableLicenseCore.StoredLicenseKey(_licenseState), key, StringComparison.Ordinal)
            || PortableLicenseCore.CachedStatusWithinGrace(_licenseState, nowUnixSeconds) is null)
            return CloudAccountResult<CloudAccountDetails>.Failed(
                transientFailure.Code, transientFailure.Message);

        return CloudAccountResult<CloudAccountDetails>.Success(
            Details(PortableLicenseCore.OfflineFallbackOutcome(_licenseState, nowUnixSeconds)));
    }

    private long NowUnixSeconds() => _time.GetUtcNow().ToUnixTimeSeconds();

    /// <summary>
    /// The server's own explanation of a rejection, unless it echoes the account
    /// key back. A rejection message is shown in the UI and written to logs, so
    /// it never carries the key.
    /// </summary>
    private static string RejectionMessage(PortableValidationOutcome outcome, string key) =>
        outcome.ErrorMessage is { Length: > 0 } message
        && !message.Contains(key, StringComparison.Ordinal)
            ? message
            : "The account key was rejected.";

    private static CloudAccountDetails Details(PortableValidationOutcome outcome) => new(
        Map(outcome.Status),
        Bound(outcome.CustomerId, 256),
        Bound(outcome.CustomerEmail, 320),
        ParseExpiry(outcome.ExpiresAt));

    /// <summary>
    /// The core's status, mapped to the account contract. Trial is a local,
    /// unlicensed state that the validate endpoint never returns; there is no
    /// account to describe, so it reads as Invalid.
    /// </summary>
    private static CloudAccountStatus Map(PortableLicenseStatus status) => status switch
    {
        PortableLicenseStatus.Active => CloudAccountStatus.Active,
        PortableLicenseStatus.Expired => CloudAccountStatus.Expired,
        _ => CloudAccountStatus.Invalid,
    };

    /// <summary>
    /// The expiry the server stated, or null when it stated none or stated one
    /// this client cannot read. An unreadable expiry is not a reason to discard
    /// an otherwise good verdict — that turned a 200-OK active account into a
    /// failure on Linux alone (issue #290).
    /// </summary>
    private static DateTimeOffset? ParseExpiry(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    // =========================================================================
    // Transport
    // =========================================================================

    /// <summary>A reply that was received and read, whatever its status.</summary>
    private readonly record struct HttpReply(HttpStatusCode StatusCode, byte[] Body)
    {
        public bool IsSuccess => (int)StatusCode is >= 200 and < 300;
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

    private async Task<CloudAccountResult<HttpReply>> SendAsync(
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
                return body.IsFailure
                    ? CloudAccountResult<HttpReply>.Failed(body.Failure!.Code, body.Failure.Message)
                    : CloudAccountResult<HttpReply>.Success(new HttpReply(response.StatusCode, body.Value!));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CloudAccountResult<HttpReply>.Failed(
                    CloudAccountFailureCode.Cancelled, "The account request was cancelled.");
            }
            catch (OperationCanceledException)
            {
                return CloudAccountResult<HttpReply>.Failed(
                    CloudAccountFailureCode.TimedOut, "The account request timed out.");
            }
            catch (HttpRequestException)
            {
                return CloudAccountResult<HttpReply>.Failed(
                    CloudAccountFailureCode.NetworkFailure, "The account service could not be reached.");
            }
        }
    }

    private static CloudAccountFailure MapHttpFailure(HttpStatusCode status) => new(
        status switch
        {
            HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                CloudAccountFailureCode.Rejected,
            HttpStatusCode.TooManyRequests => CloudAccountFailureCode.RateLimited,
            _ when (int)status >= 500 => CloudAccountFailureCode.ServerUnavailable,
            _ => CloudAccountFailureCode.NetworkFailure,
        },
        status switch
        {
            HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "The account key was rejected.",
            HttpStatusCode.TooManyRequests => "The account service is rate limited. Try again later.",
            _ when (int)status >= 500 => "The account service is temporarily unavailable.",
            _ => $"The account request failed (HTTP {(int)status}).",
        });

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
