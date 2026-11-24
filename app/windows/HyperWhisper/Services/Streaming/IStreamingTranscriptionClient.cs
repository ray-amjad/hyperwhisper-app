using System;
using System.Threading;
using System.Threading.Tasks;
using HyperWhisper.Models;

namespace HyperWhisper.Services.Streaming;

/// <summary>
/// The part of <see cref="StreamingTranscriptionClient"/> that MainViewModel's
/// streaming start/stop/cancel path uses. It exists so HyperWhisper.SmokeTests can
/// drive that path with a fake client and no network (#2242); the app only ever
/// uses <see cref="StreamingTranscriptionClient"/>.
/// </summary>
internal interface IStreamingTranscriptionClient : IAsyncDisposable
{
    StreamingConnectionState State { get; }
    int AudioSampleRate { get; }

    event Action<StreamingConnectionState>? StateChanged;
    event Action<string>? FinalTranscriptSegmentReceived;
    event Action<string>? WarningReceived;
    event Action<string>? ErrorReceived;
    event Action<double, double>? SessionCompleted;

    Task<bool> StartAsync(CancellationToken cancellationToken = default);
    Task<string> StopAsync(CancellationToken cancellationToken = default);
    Task SendAudioAsync(byte[] pcmData, CancellationToken cancellationToken = default);
}
