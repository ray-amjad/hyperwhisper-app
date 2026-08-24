using System.Security.Cryptography;
using System.Text;
using HyperWhisper.Linux.Platform.Files;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Security;

internal interface IMachineIdentitySource { byte[]? ReadRaw(); }
internal sealed class LinuxMachineIdentitySource : IMachineIdentitySource
{
    public byte[]? ReadRaw()
    {
        foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
            try { if (File.Exists(path)) return File.ReadAllBytes(path); } catch { }
        return null;
    }
}

public sealed class LinuxDeviceIdentityProvider : IDeviceIdentityProvider
{
    private static readonly byte[] Domain = "hyperwhisper-device-id-v1\0"u8.ToArray();
    private readonly IMachineIdentitySource _machine;
    private readonly IPrivateFileService _files;
    private readonly string _fallbackPath;
    private readonly Func<byte[]> _random;
    public LinuxDeviceIdentityProvider() : this(new LinuxMachineIdentitySource(), new LinuxPrivateFileService(),
        Path.Combine(new LinuxAppPaths().StateDirectory, "device-identity"), () => RandomNumberGenerator.GetBytes(32)) { }
    internal LinuxDeviceIdentityProvider(IMachineIdentitySource machine, IPrivateFileService files,
        string fallbackPath, Func<byte[]> random)
    { _machine = machine; _files = files; _fallbackPath = fallbackPath; _random = random; }
    public PlatformResult<DeviceIdentity> GetDeviceIdentity()
    {
        try
        {
            var raw = _machine.ReadRaw();
            if (raw is { Length: >= 16 }) return PlatformResult<DeviceIdentity>.Success(
                new DeviceIdentity(Hash(raw), DeviceIdentitySource.PlatformMachineId));
            if (raw is not null) CryptographicOperations.ZeroMemory(raw);
            var stored = _files.ReadAllBytes(_fallbackPath);
            if (stored.IsFailure) return PlatformResult<DeviceIdentity>.Failure(stored.Error!.Code, stored.Error.Message);
            if (stored.Value is { Length: 32 } existing)
            {
                var id = Convert.ToHexStringLower(existing); CryptographicOperations.ZeroMemory(existing);
                return PlatformResult<DeviceIdentity>.Success(new DeviceIdentity(id, DeviceIdentitySource.StoredFallback));
            }
            var generated = _random();
            if (generated.Length != 32)
            {
                CryptographicOperations.ZeroMemory(generated);
                return PlatformResult<DeviceIdentity>.Failure("device_identity.generation_failed", "A secure device identity could not be generated.");
            }
            var write = _files.WriteAllBytesAtomically(_fallbackPath, generated);
            if (write.IsFailure) { CryptographicOperations.ZeroMemory(generated); return PlatformResult<DeviceIdentity>.Failure(write.Error!.Code, write.Error.Message); }
            var generatedId = Convert.ToHexStringLower(generated); CryptographicOperations.ZeroMemory(generated);
            return PlatformResult<DeviceIdentity>.Success(new DeviceIdentity(generatedId, DeviceIdentitySource.GeneratedFallback));
        }
        catch { return PlatformResult<DeviceIdentity>.Failure("device_identity.unavailable", "Linux could not provide a device identity."); }
    }
    private static string Hash(byte[] raw)
    {
        var normalized = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(raw).Trim());
        var combined = new byte[Domain.Length + normalized.Length]; Domain.CopyTo(combined, 0); normalized.CopyTo(combined, Domain.Length);
        var value = Convert.ToHexStringLower(SHA256.HashData(combined));
        CryptographicOperations.ZeroMemory(combined); CryptographicOperations.ZeroMemory(normalized);
        CryptographicOperations.ZeroMemory(raw); return value;
    }
}
