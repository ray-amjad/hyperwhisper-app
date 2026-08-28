using uniffi.hyperwhisper_core;

namespace HyperWhisper.SharedCore;

/// <summary>
/// Platform persistence for the <c>hw-license</c> Rust core. The core is pure:
/// it holds no clock and no storage, and reads and writes every piece of license
/// state through this store.
/// <para>
/// The core's key strings are a cross-platform contract — macOS reads the same
/// <c>com.hyperwhisper.license.*</c> UserDefaults entries and Windows reads the
/// same Credential Manager entry, so an implementation routes keys rather than
/// inventing its own names.
/// </para>
/// <para>
/// An implementation must not throw. The trait has no error channel
/// (<c>get -&gt; Option&lt;String&gt;</c>, <c>set -&gt; ()</c>), so a failure has
/// nowhere to go: report a missing value as <c>null</c> and swallow a failed
/// write. Where a write failure has to be surfaced to the user — a locked
/// keyring, for example — the caller keeps that write on the platform API that
/// does have an error channel and does not route it through here.
/// </para>
/// </summary>
public interface IPortableKeyValueStore
{
    /// <summary>The value for <paramref name="key"/>, or null when absent.</summary>
    string? Get(string key);

    void Set(string key, string value);

    void Delete(string key);
}

/// <summary>
/// The license state. Mirrors the core's <c>HwLicenseStatus</c>, which is the one
/// place the server's status strings are interpreted.
/// </summary>
public enum PortableLicenseStatus
{
    Trial,
    Active,
    Expired,
    Invalid,
}

/// <summary>
/// A POST <c>/api/license/validate</c> request built by the core: the endpoint,
/// the content type and the encoded body. The platform owns only the transport.
/// </summary>
public sealed record PortableValidateRequest(string Url, string ContentType, byte[] Body);

/// <summary>
/// The core's verdict for a validation attempt. <see cref="ExpiresAt"/> is the
/// server's string, verbatim and unparsed — the core does not reject a value it
/// cannot parse, and neither should a caller.
/// </summary>
public sealed record PortableValidationOutcome(
    bool IsValid,
    PortableLicenseStatus Status,
    string? CustomerId,
    string? CustomerEmail,
    string? ExpiresAt,
    string? ErrorMessage);

/// <summary>
/// Stable public surface over the <c>hw-license</c> functions of the generated
/// UniFFI binding, which is internal to this assembly.
/// <para>
/// The core owns the validate request, the response parse, the status map, the
/// 24-hour validation cache and the 7-day offline grace. A platform keeps only
/// the I/O it must own: the HTTP call, the transient-failure classification, and
/// any write whose failure the user has to see.
/// </para>
/// <para>
/// There is no clock here. Every time-dependent call takes
/// <c>nowUnixSeconds</c>, supplied by the caller, exactly as macOS and Windows
/// supply it.
/// </para>
/// </summary>
public static class PortableLicenseCore
{
    /// <summary>The production <c>/api/license/validate</c> endpoint.</summary>
    public static string ValidateUrl() => HyperwhisperCoreMethods.LicenseValidateUrl();

    /// <summary>
    /// Builds the validate request. The core trims the license key and escapes
    /// the device fields.
    /// </summary>
    public static PortableValidateRequest BuildValidateRequest(
        string licenseKey,
        string deviceId,
        string deviceName)
    {
        ArgumentNullException.ThrowIfNull(licenseKey);
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(deviceName);
        var native = HyperwhisperCoreMethods.LicenseBuildValidateRequest(licenseKey, deviceId, deviceName);
        return new PortableValidateRequest(native.url, native.contentType, native.body);
    }

    /// <summary>The verdict for an empty or whitespace-only license key.</summary>
    public static PortableValidationOutcome EmptyKeyOutcome() =>
        Convert(HyperwhisperCoreMethods.LicenseEmptyKeyOutcome());

    /// <summary>
    /// The verdict for a terminal non-200 validate response. The core prefers the
    /// server's own <c>error</c> field and falls back to naming the status code.
    /// </summary>
    public static PortableValidationOutcome HttpErrorOutcome(int statusCode, byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfNegative(statusCode);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(statusCode, ushort.MaxValue);
        return Convert(HyperwhisperCoreMethods.LicenseHttpErrorOutcome((ushort)statusCode, body));
    }

    /// <summary>
    /// The verdict for a 200-OK validate response. A body that is not JSON is an
    /// Invalid verdict, not an exception.
    /// </summary>
    public static PortableValidationOutcome ParseValidateResponse(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Convert(HyperwhisperCoreMethods.LicenseParseValidateResponse(body));
    }

    /// <summary>
    /// Whether the cached verdict is older than the 24-hour validation cache, so
    /// the server has to be asked again. True when nothing is cached, and true
    /// when the clock has run backwards.
    /// </summary>
    public static bool ShouldRevalidate(IPortableKeyValueStore store, long nowUnixSeconds)
    {
        ArgumentNullException.ThrowIfNull(store);
        return HyperwhisperCoreMethods.LicenseShouldRevalidate(Adapt(store), nowUnixSeconds);
    }

    /// <summary>
    /// The cached status while it is inside the 7-day offline grace, else null.
    /// </summary>
    public static PortableLicenseStatus? CachedStatusWithinGrace(
        IPortableKeyValueStore store,
        long nowUnixSeconds)
    {
        ArgumentNullException.ThrowIfNull(store);
        var cached = HyperwhisperCoreMethods.LicenseCachedStatusWithinGrace(Adapt(store), nowUnixSeconds);
        return cached.HasValue ? Convert(cached.Value) : null;
    }

    /// <summary>
    /// The verdict to use when the server cannot be reached: the cached status
    /// while it is inside the offline grace, else Invalid.
    /// </summary>
    public static PortableValidationOutcome OfflineFallbackOutcome(
        IPortableKeyValueStore store,
        long nowUnixSeconds)
    {
        ArgumentNullException.ThrowIfNull(store);
        return Convert(HyperwhisperCoreMethods.LicenseOfflineFallbackOutcome(Adapt(store), nowUnixSeconds));
    }

    /// <summary>
    /// The license key on file, or null when it is absent or whitespace-only.
    /// </summary>
    public static string? StoredLicenseKey(IPortableKeyValueStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return HyperwhisperCoreMethods.LicenseStoredLicenseKey(Adapt(store));
    }

    /// <summary>
    /// Persists a server verdict for the key that was attempted. An Active
    /// verdict stores the key and caches the status. Any other verdict caches the
    /// status only when the attempted key is the key on file, so a rejected
    /// replacement key cannot lock a valid user out for the 24-hour cache window.
    /// </summary>
    public static void PersistValidationVerdict(
        IPortableKeyValueStore store,
        PortableLicenseStatus status,
        string attemptedKey,
        long nowUnixSeconds)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(attemptedKey);
        HyperwhisperCoreMethods.LicensePersistValidationVerdict(
            Adapt(store), Convert(status), attemptedKey, nowUnixSeconds);
    }

    /// <summary>
    /// Clears the license key, the customer id, and the cached verdict. The
    /// remote trial-limit override is configuration, not the user's license, and
    /// survives.
    /// </summary>
    public static void ClearStoredLicense(IPortableKeyValueStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        HyperwhisperCoreMethods.LicenseClearStoredLicense(Adapt(store));
    }

    private static KeyValueStore Adapt(IPortableKeyValueStore store) =>
        new PortableKeyValueStoreAdapter(store);

    private static PortableValidationOutcome Convert(ValidationOutcome outcome) => new(
        outcome.isValid,
        Convert(outcome.status),
        outcome.customerId,
        outcome.customerEmail,
        outcome.expiresAt,
        outcome.errorMessage);

    private static PortableLicenseStatus Convert(HwLicenseStatus status) => status switch
    {
        HwLicenseStatus.Trial => PortableLicenseStatus.Trial,
        HwLicenseStatus.Active => PortableLicenseStatus.Active,
        HwLicenseStatus.Expired => PortableLicenseStatus.Expired,
        HwLicenseStatus.Invalid => PortableLicenseStatus.Invalid,
        _ => PortableLicenseStatus.Invalid,
    };

    private static HwLicenseStatus Convert(PortableLicenseStatus status) => status switch
    {
        PortableLicenseStatus.Trial => HwLicenseStatus.Trial,
        PortableLicenseStatus.Active => HwLicenseStatus.Active,
        PortableLicenseStatus.Expired => HwLicenseStatus.Expired,
        PortableLicenseStatus.Invalid => HwLicenseStatus.Invalid,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}

/// <summary>
/// Bridges a public <see cref="IPortableKeyValueStore"/> to the generated
/// binding's internal <c>KeyValueStore</c> callback interface.
/// <para>
/// Warning: do not let an exception leave one of these methods. Rust calls them
/// through a reverse P/Invoke and the generated dispatcher
/// (<c>UniffiCallbackInterfaceKeyValueStore</c>) has no try/catch, so a managed
/// exception unwinds into native frames and takes the process down. A store is
/// already required not to throw; this is the second guard, because the cost of
/// the first one being wrong is the whole application.
/// </para>
/// </summary>
internal sealed class PortableKeyValueStoreAdapter(IPortableKeyValueStore store) : KeyValueStore
{
    public string? Get(string key)
    {
        try
        {
            return store.Get(key);
        }
        catch (Exception)
        {
            // A missing value is the only thing the core can be told here.
            return null;
        }
    }

    public void Set(string key, string value)
    {
        try
        {
            store.Set(key, value);
        }
        catch (Exception)
        {
            // The trait has no error channel; see the class remarks.
        }
    }

    public void Delete(string key)
    {
        try
        {
            store.Delete(key);
        }
        catch (Exception)
        {
            // The trait has no error channel; see the class remarks.
        }
    }
}
