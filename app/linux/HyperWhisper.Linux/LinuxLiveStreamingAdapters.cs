using System.Security.Cryptography;
using System.Text;
using HyperWhisper.LiveStreaming;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.SharedCore;

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

/// <summary>Forwards bounded provider transcript updates without logging or retaining them.</summary>
internal sealed class LinuxLiveTranscriptSink(ILiveTranscriptSink? preview = null) : ILiveTranscriptSink
{
    private readonly ILiveTranscriptSink? _preview = preview;
    public event EventHandler<LiveTranscriptUpdate>? TranscriptReceived;

    public void OnTranscript(LiveTranscriptUpdate update)
    {
        if (string.IsNullOrWhiteSpace(update.Text) || update.Text.Length > 512 * 1024) return;
        var safe = update with { Text = update.Text.Trim() };
        try { _preview?.OnTranscript(safe); } catch { }
        var handlers = TranscriptReceived;
        if (handlers is null) return;
        foreach (EventHandler<LiveTranscriptUpdate> handler in handlers.GetInvocationList())
            try { handler(this, safe); } catch { }
    }
}

internal sealed class LinuxRoutingLiveTranscriber(
    ILiveTranscriber cloud,
    ILiveTranscriber local) : ILiveTranscriber
{
    private readonly ILiveTranscriber _cloud = cloud ?? throw new ArgumentNullException(nameof(cloud));
    private readonly ILiveTranscriber _local = local ?? throw new ArgumentNullException(nameof(local));

    public Task<LiveTranscriptionResult> TranscribeAsync(
        LiveTranscriptionConfig config,
        IAsyncEnumerable<ReadOnlyMemory<byte>> audio,
        CancellationToken cancellationToken = default) =>
        (config.Provider is LiveTranscriptionProvider.ParakeetLocal or LiveTranscriptionProvider.NemotronLocal
            ? _local : _cloud).TranscribeAsync(config, audio, cancellationToken);
}

public static class LinuxLiveStreamingSettingsMapper
{
    public static string? ModelForProvider(string? provider, string? configuredModel)
    {
        var normalized = provider?.Trim().Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
        var configured = string.IsNullOrWhiteSpace(configuredModel) ? null : configuredModel.Trim();
        return normalized switch
        {
            "deepgram" => configured,
            "parakeetlocal" => configured is "parakeet-v2" or "parakeet-v3"
                ? configured : "parakeet-v3",
            "nemotronlocal" => "nemotron-3.5-ml-560ms",
            _ => null,
        };
    }
}
