using System;

namespace HyperWhisper.Services.Streaming;

/// <summary>
/// The part of <see cref="StreamingAudioCapture"/> that MainViewModel's streaming
/// path uses, so HyperWhisper.SmokeTests can drive that path with no microphone
/// (#2242). Not <c>PlatformContracts.IStreamingAudioCapture</c>: that contract
/// raises ReadOnlyMemory chunks through a wrapping adapter, and the view model's
/// handlers take the capture's own byte[] events.
/// </summary>
internal interface IStreamingAudioSource : IDisposable
{
    TimeSpan Duration { get; }

    event Action<byte[]>? AudioChunkAvailable;
    event Action<float>? AudioLevelChanged;

    void Start(int deviceNumber, int sampleRate);
    void Stop();
}
