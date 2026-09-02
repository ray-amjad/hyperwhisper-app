using System.Security.Cryptography;
using System.Text;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.SharedCore;

namespace HyperWhisper.TranscriptionRouting;

/// <summary>Reads provider credentials only at request time from platform secure storage.</summary>
public sealed class CredentialStoreCloudCredentialSource(
    ICredentialStore store,
    IDeviceIdentityProvider deviceIdentity,
    string resource = "HyperWhisper") : ICloudCredentialSource
{
    private readonly ICredentialStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IDeviceIdentityProvider _deviceIdentity = deviceIdentity ?? throw new ArgumentNullException(nameof(deviceIdentity));
    private readonly string _resource = string.IsNullOrWhiteSpace(resource)
        ? throw new ArgumentException("A credential resource is required.", nameof(resource))
        : resource;

    public ValueTask<CloudCredential?> GetCredentialAsync(
        CloudTranscriptionProvider provider,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var account = AccountFor(provider);
        var secret = account is null ? null : ReadUtf8(account);
        var identity = _deviceIdentity.GetDeviceIdentity();
        var deviceId = identity.IsSuccess ? identity.Value!.Id : null;
        return ValueTask.FromResult<CloudCredential?>(UsesAccountKey(provider)
            ? new CloudCredential(LicenseKey: secret, DeviceId: deviceId)
            : new CloudCredential(ApiKey: secret, DeviceId: deviceId));
    }

    public static string? AccountFor(CloudTranscriptionProvider provider) => provider switch
    {
        CloudTranscriptionProvider.OpenAi => "OpenAIApiKey",
        CloudTranscriptionProvider.Groq => "GroqApiKey",
        CloudTranscriptionProvider.Deepgram => "DeepgramApiKey",
        CloudTranscriptionProvider.AssemblyAi => "AssemblyAIApiKey",
        CloudTranscriptionProvider.ElevenLabs => "ElevenLabsApiKey",
        CloudTranscriptionProvider.Mistral => "MistralApiKey",
        CloudTranscriptionProvider.Soniox => "SonioxApiKey",
        CloudTranscriptionProvider.Gemini => "GeminiApiKey",
        // Its own slot, not shared with Gemini: same vendor, different
        // eligibility, and sharing would mean deleting one key silently
        // disables the other.
        CloudTranscriptionProvider.GeminiTranscribe => "GeminiTranscribeApiKey",
        CloudTranscriptionProvider.Grok => "GrokApiKey",
        CloudTranscriptionProvider.Meta => "MetaApiKey",
        CloudTranscriptionProvider.AzureMai or CloudTranscriptionProvider.GoogleChirp
            or CloudTranscriptionProvider.HyperWhisperCloud => "LicenseKey",
        _ => null,
    };

    private static bool UsesAccountKey(CloudTranscriptionProvider provider) => provider is
        CloudTranscriptionProvider.AzureMai or CloudTranscriptionProvider.GoogleChirp
        or CloudTranscriptionProvider.HyperWhisperCloud;

    private string? ReadUtf8(string account)
    {
        var result = _store.Read(_resource, account);
        if (result.IsFailure || result.Value is not { Length: > 0 } bytes) return null;
        try
        {
            var value = new UTF8Encoding(false, true).GetString(bytes).Trim();
            return value.Length == 0 ? null : value;
        }
        catch (DecoderFallbackException) { return null; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
