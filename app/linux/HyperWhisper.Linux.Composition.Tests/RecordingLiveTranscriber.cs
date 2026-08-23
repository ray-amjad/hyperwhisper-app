using HyperWhisper.LiveStreaming;
using HyperWhisper.SharedCore;

sealed class RecordingLiveTranscriber(string transcript) : ILiveTranscriber
{
    public List<LiveTranscriptionProvider> Providers { get; } = [];

    public Task<LiveTranscriptionResult> TranscribeAsync(
        LiveTranscriptionConfig config,
        IAsyncEnumerable<ReadOnlyMemory<byte>> audio,
        CancellationToken cancellationToken = default)
    {
        Providers.Add(config.Provider);
        return Task.FromResult(new LiveTranscriptionResult(transcript, null, 0, 0));
    }
}
