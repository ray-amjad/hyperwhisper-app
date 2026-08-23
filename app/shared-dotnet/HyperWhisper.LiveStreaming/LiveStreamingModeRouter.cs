using HyperWhisper.Platform.Abstractions;
using HyperWhisper.SharedCore;

namespace HyperWhisper.LiveStreaming;

public sealed record LiveStreamingModeSettings(
    string ModeId,
    bool Enabled,
    string Provider,
    string? DeviceId = null,
    string? Language = null,
    IReadOnlyList<string>? Vocabulary = null,
    string? Model = null,
    bool FastFormatting = false,
    string? ClientDeviceId = null);

public sealed record ResolvedLiveStreamingMode(
    LiveTranscriptionConfig Config,
    string AudioDeviceId);

public interface ILiveStreamingCredentialSource
{
    Task<string?> GetCredentialAsync(string account, CancellationToken cancellationToken = default);
}

/// <summary>
/// Converts persisted, platform-neutral mode values to the exact shared-core
/// streaming protocol configuration. Credentials remain outside mode storage.
/// </summary>
public sealed class LiveStreamingModeRouter(ILiveStreamingCredentialSource credentials)
{
    private readonly ILiveStreamingCredentialSource _credentials =
        credentials ?? throw new ArgumentNullException(nameof(credentials));

    public async Task<PlatformResult<ResolvedLiveStreamingMode>> ResolveAsync(
        LiveStreamingModeSettings mode,
        IReadOnlyList<string>? globalVocabulary = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mode);
        if (!mode.Enabled)
        {
            return PlatformResult<ResolvedLiveStreamingMode>.Failure(
                "streaming_disabled", "Live transcription is disabled for this mode.");
        }

        if (!TryProvider(mode.Provider, out var provider, out var credentialAccount, out var usesLicense))
        {
            return PlatformResult<ResolvedLiveStreamingMode>.Failure(
                "streaming_provider_unsupported", "The selected live transcription provider is not supported.");
        }

        var credential = await _credentials.GetCredentialAsync(credentialAccount, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(credential) &&
            (!usesLicense || string.IsNullOrWhiteSpace(mode.ClientDeviceId)))
        {
            return PlatformResult<ResolvedLiveStreamingMode>.Failure(
                "streaming_credential_missing", "The selected live transcription provider requires a credential.");
        }

        var vocabulary = MergeVocabulary(globalVocabulary, mode.Vocabulary);
        var config = new LiveTranscriptionConfig(
            provider,
            ApiKey: usesLicense ? null : credential,
            LicenseKey: usesLicense ? credential : null,
            DeviceId: usesLicense ? Normalize(mode.ClientDeviceId) : null,
            Language: Normalize(mode.Language),
            Vocabulary: vocabulary,
            Model: Normalize(mode.Model),
            FastFormatting: mode.FastFormatting);
        return PlatformResult<ResolvedLiveStreamingMode>.Success(
            new ResolvedLiveStreamingMode(config, Normalize(mode.DeviceId) ?? "default"));
    }

    public static bool TryProvider(
        string? storageValue,
        out LiveTranscriptionProvider provider,
        out string credentialAccount,
        out bool usesLicense)
    {
        usesLicense = false;
        switch (storageValue?.Trim().ToLowerInvariant())
        {
            case "deepgram":
                provider = LiveTranscriptionProvider.Deepgram;
                credentialAccount = "DeepgramApiKey";
                return true;
            case "elevenlabs":
            case "eleven_labs":
                provider = LiveTranscriptionProvider.ElevenLabs;
                credentialAccount = "ElevenLabsApiKey";
                return true;
            case "openai":
            case "open_ai":
                provider = LiveTranscriptionProvider.OpenAi;
                credentialAccount = "OpenAIApiKey";
                return true;
            case "xai":
            case "grok":
                provider = LiveTranscriptionProvider.Grok;
                credentialAccount = "GrokApiKey";
                return true;
            case "hyperwhisper":
            case "hyperwhispercloud":
            case "hyperwhisper_cloud":
                provider = LiveTranscriptionProvider.HyperWhisperCloud;
                credentialAccount = "LicenseKey";
                usesLicense = true;
                return true;
            default:
                provider = default;
                credentialAccount = string.Empty;
                return false;
        }
    }

    private static IReadOnlyList<string> MergeVocabulary(
        IReadOnlyList<string>? global,
        IReadOnlyList<string>? mode)
    {
        return (global ?? [])
            .Concat(mode ?? [])
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
