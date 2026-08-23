using System.Security.Cryptography;
using System.Text;
using HyperWhisper.LiveStreaming;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux;

internal sealed class LinuxLiveStreamingCredentialSource(ICredentialStore credentials)
    : ILiveStreamingCredentialSource
{
    private const string Resource = "HyperWhisper";
    private readonly ICredentialStore _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));

    public Task<string?> GetCredentialAsync(string account, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _credentials.Read(Resource, account);
        if (result.IsFailure || result.Value is not { Length: > 0 } bytes)
            return Task.FromResult<string?>(null);
        try
        {
            var value = new UTF8Encoding(false, true).GetString(bytes).Trim();
            return Task.FromResult<string?>(value.Length == 0 ? null : value);
        }
        catch (DecoderFallbackException) { return Task.FromResult<string?>(null); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

public static class LinuxLiveStreamingSettingsMapper
{
    public static string? ModelForProvider(string? provider, string? configuredModel) =>
        string.Equals(provider?.Trim(), "deepgram", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(configuredModel)
                ? configuredModel.Trim()
                : null;
}
