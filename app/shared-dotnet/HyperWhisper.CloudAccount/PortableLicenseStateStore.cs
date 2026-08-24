using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.SharedCore;

namespace HyperWhisper.CloudAccount;

/// <summary>
/// Persistence for the <c>hw-license</c> Rust core on a platform whose secret
/// storage is an <see cref="ICredentialStore"/>.
/// <para>
/// Two backing stores, routed by key, the same split Windows uses
/// (<c>RustCoreKeyValueStore</c>):
/// </para>
/// <list type="number">
/// <item>
/// The license key goes to the credential store, under the same resource and
/// account the account service already uses. The mapping is 1:1 with what was
/// stored before this store existed, so an already activated device keeps its
/// key with no migration.
/// </item>
/// <item>
/// Everything else — the cached verdict, its timestamp, the customer id, and the
/// remote trial-limit override — goes to one flat JSON file of string pairs,
/// written atomically at owner-only permissions.
/// </item>
/// </list>
/// <para>
/// No method throws. The core's store trait has no error channel, and an
/// exception escaping into a Rust callback frame would take the process down.
/// A write whose failure the user has to see stays on
/// <see cref="ICredentialStore"/>, which does report one.
/// </para>
/// </summary>
public sealed class PortableLicenseStateStore : IPortableKeyValueStore
{
    /// <summary>
    /// The core's key for the license key itself. Must match
    /// <c>K_LICENSE_KEY</c> in <c>shared-core-rs/crates/hw-license/src/cache.rs</c>;
    /// macOS and Windows route the same string to their own secret stores.
    /// </summary>
    private const string LicenseKeyStorageKey = "com.hyperwhisper.license.key";

    /// <summary>The file name under the state directory. See <see cref="For"/>.</summary>
    public const string StateFileName = "license-state.json";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly ICredentialStore _credentials;
    private readonly IPrivateFileService _files;
    private readonly string _statePath;
    private readonly string _credentialResource;
    private readonly string _credentialAccount;
    private readonly object _gate = new();

    private Dictionary<string, string>? _state;

    public PortableLicenseStateStore(
        ICredentialStore credentials,
        IPrivateFileService files,
        string statePath,
        string credentialResource = PortableCloudAccountService.CredentialResource,
        string credentialAccount = PortableCloudAccountService.CredentialAccount)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        if (string.IsNullOrWhiteSpace(statePath))
            throw new ArgumentException("A state file path is required.", nameof(statePath));
        if (string.IsNullOrWhiteSpace(credentialResource))
            throw new ArgumentException("A credential resource is required.", nameof(credentialResource));
        if (string.IsNullOrWhiteSpace(credentialAccount))
            throw new ArgumentException("A credential account is required.", nameof(credentialAccount));
        _statePath = statePath;
        _credentialResource = credentialResource;
        _credentialAccount = credentialAccount;
    }

    /// <summary>
    /// The store for an application's own directories: the cached verdict lives
    /// beside the other state, and the license key stays in the keyring.
    /// </summary>
    public static PortableLicenseStateStore For(
        ICredentialStore credentials,
        IPrivateFileService files,
        IAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new PortableLicenseStateStore(
            credentials, files, Path.Combine(paths.StateDirectory, StateFileName));
    }

    public string? Get(string key)
    {
        if (key is null) return null;
        if (key == LicenseKeyStorageKey) return ReadLicenseKey();

        lock (_gate)
        {
            return LoadedState().TryGetValue(key, out var value) ? value : null;
        }
    }

    public void Set(string key, string value)
    {
        if (key is null || value is null) return;
        if (key == LicenseKeyStorageKey)
        {
            WriteLicenseKey(value);
            return;
        }

        lock (_gate)
        {
            var state = LoadedState();
            if (state.TryGetValue(key, out var current) && current == value) return;
            state[key] = value;
            Persist(state);
        }
    }

    public void Delete(string key)
    {
        if (key is null) return;
        if (key == LicenseKeyStorageKey)
        {
            // The result is deliberately dropped. A caller that has to report a
            // failed removal calls ICredentialStore.Delete itself and checks it.
            TryDeleteLicenseKey();
            return;
        }

        lock (_gate)
        {
            var state = LoadedState();
            if (!state.Remove(key)) return;
            Persist(state);
        }
    }

    private string? ReadLicenseKey()
    {
        var read = _credentials.Read(_credentialResource, _credentialAccount);
        if (read.IsFailure || read.Value is not { Length: > 0 } bytes) return null;

        try
        {
            var key = StrictUtf8.GetString(bytes).Trim();
            return key.Length == 0 ? null : key;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private void WriteLicenseKey(string value)
    {
        var key = value.Trim();
        if (key.Length == 0) return;
        // The account service writes the key itself, with the result checked, and
        // the core then re-persists the same verdict. Comparing first keeps that
        // second write from reaching the keyring at all.
        if (string.Equals(ReadLicenseKey(), key, StringComparison.Ordinal)) return;

        byte[]? secret = null;
        try
        {
            secret = StrictUtf8.GetBytes(key);
            _credentials.Write(_credentialResource, _credentialAccount, secret);
        }
        catch (EncoderFallbackException)
        {
            // An unencodable key cannot have come from the server; drop it.
        }
        finally
        {
            if (secret is not null) CryptographicOperations.ZeroMemory(secret);
        }
    }

    private void TryDeleteLicenseKey()
    {
        try
        {
            // Deactivation deletes the key itself, with the result checked, and
            // then clears the core's state — which asks for this same delete a
            // second time. Reading first keeps that from reaching the keyring.
            if (ReadLicenseKey() is null) return;
            _credentials.Delete(_credentialResource, _credentialAccount);
        }
        catch (Exception)
        {
            // See the class remarks: this method must not throw into Rust.
        }
    }

    // Caller holds _gate.
    private Dictionary<string, string> LoadedState()
    {
        if (_state is not null) return _state;

        _state = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var read = _files.ReadAllText(_statePath);
            if (read.IsSuccess && read.Value is { Length: > 0 } text)
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(text);
                if (loaded is not null)
                {
                    foreach (var pair in loaded)
                    {
                        if (pair.Value is not null) _state[pair.Key] = pair.Value;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // A corrupt file is treated as no cached verdict: the next validation
            // rewrites it. Losing the cache is recoverable; refusing to start is not.
        }

        return _state;
    }

    // Caller holds _gate.
    private void Persist(Dictionary<string, string> state)
    {
        try
        {
            _files.WriteAllTextAtomically(_statePath, JsonSerializer.Serialize(state, WriteOptions));
        }
        catch (Exception)
        {
            // See the class remarks: this method must not throw into Rust. The
            // in-memory state stays correct for the rest of this session.
        }
    }
}
