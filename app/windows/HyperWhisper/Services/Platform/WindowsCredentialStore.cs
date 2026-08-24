using System.Runtime.InteropServices;
using Windows.Security.Credentials;
using PlatformContracts = HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Services.Platform;

internal interface IWindowsCredentialBackend
{
    bool TryRead(string resource, string account, out string? value);
    void Write(string resource, string account, string value);
    void Delete(string resource, string account);
}

internal sealed class PasswordVaultCredentialBackend : IWindowsCredentialBackend
{
    private const int ElementNotFound = unchecked((int)0x80070490);
    private readonly PasswordVault _vault = new();

    public bool TryRead(string resource, string account, out string? value)
    {
        try
        {
            var credential = _vault.Retrieve(resource, account);
            credential.RetrievePassword();
            value = credential.Password;
            return true;
        }
        catch (COMException ex) when (ex.HResult == ElementNotFound)
        {
            value = null;
            return false;
        }
    }

    public void Write(string resource, string account, string value)
    {
        Delete(resource, account);
        _vault.Add(new PasswordCredential(resource, account, value));
    }

    public void Delete(string resource, string account)
    {
        try { _vault.Remove(_vault.Retrieve(resource, account)); }
        catch (COMException ex) when (ex.HResult == ElementNotFound) { }
    }
}

public sealed class WindowsCredentialStore : PlatformContracts.ICredentialStore
{
    private const string EncodingPrefix = "hyperwhisper-bytes-v1:";
    private readonly IWindowsCredentialBackend _backend;

    public WindowsCredentialStore() : this(new PasswordVaultCredentialBackend()) { }
    internal WindowsCredentialStore(IWindowsCredentialBackend backend)
        => _backend = backend ?? throw new ArgumentNullException(nameof(backend));

    public PlatformContracts.PlatformResult<byte[]?> Read(string resource, string account)
    {
        ValidateKey(resource, account);
        try
        {
            if (!_backend.TryRead(resource, account, out var encoded))
                return PlatformContracts.PlatformResult<byte[]?>.Success(null);
            if (encoded == null || !encoded.StartsWith(EncodingPrefix, StringComparison.Ordinal))
                return PlatformContracts.PlatformResult<byte[]?>.Failure(
                    "credential.invalid_encoding", "The Windows credential is not in the expected byte encoding.");
            return PlatformContracts.PlatformResult<byte[]?>.Success(
                Convert.FromBase64String(encoded[EncodingPrefix.Length..]));
        }
        catch (FormatException)
        {
            return PlatformContracts.PlatformResult<byte[]?>.Failure(
                "credential.invalid_encoding", "The Windows credential is not in the expected byte encoding.");
        }
        catch (Exception ex)
        {
            LoggingService.Error("WindowsCredentialStore: credential read failed", ex);
            return PlatformContracts.PlatformResult<byte[]?>.Failure(
                "credential.read_failed", "Windows Credential Manager could not read the credential.");
        }
    }

    public PlatformContracts.PlatformResult Write(string resource, string account, ReadOnlySpan<byte> value)
    {
        ValidateKey(resource, account);
        try
        {
            _backend.Write(resource, account, EncodingPrefix + Convert.ToBase64String(value));
            return PlatformContracts.PlatformResult.Success();
        }
        catch (Exception ex)
        {
            LoggingService.Error("WindowsCredentialStore: credential write failed", ex);
            return PlatformContracts.PlatformResult.Failure(
                "credential.write_failed", "Windows Credential Manager could not write the credential.");
        }
    }

    public PlatformContracts.PlatformResult Delete(string resource, string account)
    {
        ValidateKey(resource, account);
        try
        {
            _backend.Delete(resource, account);
            return PlatformContracts.PlatformResult.Success();
        }
        catch (Exception ex)
        {
            LoggingService.Error("WindowsCredentialStore: credential deletion failed", ex);
            return PlatformContracts.PlatformResult.Failure(
                "credential.delete_failed", "Windows Credential Manager could not delete the credential.");
        }
    }

    private static void ValidateKey(string resource, string account)
    {
        if (string.IsNullOrWhiteSpace(resource)) throw new ArgumentException("A credential resource is required.", nameof(resource));
        if (string.IsNullOrWhiteSpace(account)) throw new ArgumentException("A credential account is required.", nameof(account));
    }
}
