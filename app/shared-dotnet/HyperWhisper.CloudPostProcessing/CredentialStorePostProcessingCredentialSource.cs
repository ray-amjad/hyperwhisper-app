using System.Security.Cryptography;
using System.Text;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.CloudPostProcessing;

/// <summary>Reads secrets only at request time and clears copied credential bytes.</summary>
public sealed class CredentialStorePostProcessingCredentialSource(
    ICredentialStore store,
    IDeviceIdentityProvider deviceIdentity,
    string resource = "HyperWhisper") : IPostProcessingCredentialSource
{
    private readonly ICredentialStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IDeviceIdentityProvider _deviceIdentity = deviceIdentity ?? throw new ArgumentNullException(nameof(deviceIdentity));
    private readonly string _resource = string.IsNullOrWhiteSpace(resource)
        ? throw new ArgumentException("A credential resource is required.", nameof(resource))
        : resource;

    public ValueTask<PostProcessingCredential?> GetCredentialAsync(
        CloudPostProcessingProvider provider,
        Guid? customEndpointId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var account = AccountFor(provider, customEndpointId);
        var secret = account is null ? null : ReadUtf8(account);
        if (provider == CloudPostProcessingProvider.HyperWhisperCloud)
        {
            var identity = _deviceIdentity.GetDeviceIdentity();
            return ValueTask.FromResult<PostProcessingCredential?>(new(
                LicenseKey: secret,
                DeviceId: identity.IsSuccess ? identity.Value!.Id : null));
        }

        return ValueTask.FromResult<PostProcessingCredential?>(new(ApiKey: secret));
    }

    public static string? AccountFor(CloudPostProcessingProvider provider, Guid? customEndpointId = null) => provider switch
    {
        CloudPostProcessingProvider.OpenAi => "OpenAIApiKey",
        CloudPostProcessingProvider.Anthropic => "AnthropicApiKey",
        CloudPostProcessingProvider.Groq => "GroqApiKey",
        CloudPostProcessingProvider.Grok => "GrokApiKey",
        CloudPostProcessingProvider.Gemini => "GeminiApiKey",
        CloudPostProcessingProvider.Cerebras => "CerebrasApiKey",
        CloudPostProcessingProvider.Mistral => "MistralApiKey",
        CloudPostProcessingProvider.HyperWhisperCloud => "LicenseKey",
        CloudPostProcessingProvider.Custom when customEndpointId is { } id => $"CustomEndpoint_{id:D}",
        _ => null,
    };

    private string? ReadUtf8(string account)
    {
        var result = _store.Read(_resource, account);
        if (result.IsFailure || result.Value is not { Length: > 0 } bytes) return null;
        try
        {
            var value = new UTF8Encoding(false, true).GetString(bytes).Trim();
            return value.Length == 0 ? null : value;
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
}
