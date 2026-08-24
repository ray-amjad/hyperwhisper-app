using System.Security.Cryptography;
using System.Text;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.Linux.Platform.Files;
using HyperWhisper.Linux.Platform.Injection;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Security;

public sealed record LinuxCredentialCapabilities(string Backend, bool UsesSecretService, bool UsesPrivateFileFallback);
internal interface ILinuxCredentialBackend
{
    PlatformResult<byte[]?> Read(string resource, string account);
    PlatformResult Write(string resource, string account, ReadOnlySpan<byte> value);
    PlatformResult Delete(string resource, string account);
}

public sealed class LinuxCredentialStore : ICredentialStore
{
    private readonly ILinuxCredentialBackend _backend;
    public LinuxCredentialStore() : this(CreateDefault(out var capabilities), capabilities) { }
    internal LinuxCredentialStore(ILinuxCredentialBackend backend, LinuxCredentialCapabilities capabilities)
    { _backend = backend; Capabilities = capabilities; }
    public LinuxCredentialCapabilities Capabilities { get; }
    public PlatformResult<byte[]?> Read(string resource, string account) { Validate(resource, account); return _backend.Read(resource, account); }
    public PlatformResult Write(string resource, string account, ReadOnlySpan<byte> value) { Validate(resource, account); return _backend.Write(resource, account, value); }
    public PlatformResult Delete(string resource, string account) { Validate(resource, account); return _backend.Delete(resource, account); }
    private static ILinuxCredentialBackend CreateDefault(out LinuxCredentialCapabilities capabilities)
    {
        var tool = CommandClipboardBackend.FindExecutable("secret-tool");
        if (tool is not null && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
        { capabilities = new("secret-service", true, false); return new SecretToolCredentialBackend(new DesktopCommandRunner(), tool); }
        var paths = new LinuxAppPaths();
        capabilities = new("private-file-0600", false, true);
        return new PrivateFileCredentialBackend(new LinuxPrivateFileService(), Path.Combine(paths.ConfigDirectory, "credentials"));
    }
    private static void Validate(string resource, string account)
    {
        if (string.IsNullOrWhiteSpace(resource) || string.IsNullOrWhiteSpace(account)
            || resource.Contains('\0') || account.Contains('\0')) throw new ArgumentException("Credential keys must be non-empty and contain no NUL.");
    }
}

internal sealed class SecretToolCredentialBackend(IDesktopCommandRunner runner, string tool) : ILinuxCredentialBackend
{
    private const string Prefix = "hyperwhisper-bytes-v1:";
    public PlatformResult<byte[]?> Read(string resource, string account)
    {
        try
        {
            var result = runner.RunAsync(tool, ["lookup", "resource", resource, "account", account], null,
                CancellationToken.None, TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
            if (result.ExitCode != 0) return PlatformResult<byte[]?>.Success(null);
            var encoded = Encoding.UTF8.GetString(result.Output).TrimEnd('\r', '\n');
            if (!encoded.StartsWith(Prefix, StringComparison.Ordinal)) return PlatformResult<byte[]?>.Failure("credential.invalid_encoding", "The stored credential has an invalid encoding.");
            return PlatformResult<byte[]?>.Success(Convert.FromBase64String(encoded[Prefix.Length..]));
        }
        catch (FormatException) { return PlatformResult<byte[]?>.Failure("credential.invalid_encoding", "The stored credential has an invalid encoding."); }
        catch { return PlatformResult<byte[]?>.Failure("credential.read_failed", "Secret Service could not read the credential."); }
    }
    public PlatformResult Write(string resource, string account, ReadOnlySpan<byte> value)
    {
        byte[]? input = null;
        try
        {
            input = Encoding.UTF8.GetBytes(Prefix + Convert.ToBase64String(value) + "\n");
            var result = runner.RunAsync(tool, ["store", "--label=HyperWhisper", "resource", resource, "account", account], input,
                CancellationToken.None, TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            return result.ExitCode == 0 ? PlatformResult.Success() : PlatformResult.Failure("credential.write_failed", "Secret Service could not write the credential.");
        }
        catch { return PlatformResult.Failure("credential.write_failed", "Secret Service could not write the credential."); }
        finally { if (input is not null) CryptographicOperations.ZeroMemory(input); }
    }
    public PlatformResult Delete(string resource, string account)
    {
        try { var result = runner.RunAsync(tool, ["clear", "resource", resource, "account", account], null,
            CancellationToken.None, TimeSpan.FromSeconds(8)).GetAwaiter().GetResult(); return result.ExitCode == 0 ? PlatformResult.Success() : PlatformResult.Failure("credential.delete_failed", "Secret Service could not delete the credential."); }
        catch { return PlatformResult.Failure("credential.delete_failed", "Secret Service could not delete the credential."); }
    }
}

internal sealed class PrivateFileCredentialBackend(IPrivateFileService files, string directory) : ILinuxCredentialBackend
{
    public PlatformResult<byte[]?> Read(string resource, string account) => files.ReadAllBytes(PathFor(resource, account));
    public PlatformResult Write(string resource, string account, ReadOnlySpan<byte> value) => files.WriteAllBytesAtomically(PathFor(resource, account), value);
    public PlatformResult Delete(string resource, string account) => files.Delete(PathFor(resource, account));
    private string PathFor(string resource, string account)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(resource + "\0" + account));
        return Path.Combine(directory, Convert.ToHexStringLower(key) + ".credential");
    }
}
