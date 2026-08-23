using System.Threading.Channels;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.SharedCore;

namespace HyperWhisper.LiveStreaming;

public sealed record LiveStreamingSessionRequest(
    LiveTranscriptionConfig Config,
    string AudioDeviceId = "default",
    int ChannelCount = 1);

public sealed record LiveStreamingSessionOutcome(
    LiveTranscriptionResult Transcription,
    PlatformError? CaptureFailure,
    TimeSpan CaptureDuration)
{
    public bool IsSuccess => CaptureFailure is null && Transcription.IsSuccess;
}

public interface ILiveCloudTranscriber
{
    Task<LiveTranscriptionResult> TranscribeAsync(
        LiveTranscriptionConfig config,
        IAsyncEnumerable<ReadOnlyMemory<byte>> audio,
        CancellationToken cancellationToken = default);
}

public sealed class SharedCoreLiveCloudTranscriber(LiveCloudTranscriptionService service) : ILiveCloudTranscriber
{
    private readonly LiveCloudTranscriptionService _service =
        service ?? throw new ArgumentNullException(nameof(service));

    public Task<LiveTranscriptionResult> TranscribeAsync(
        LiveTranscriptionConfig config,
        IAsyncEnumerable<ReadOnlyMemory<byte>> audio,
        CancellationToken cancellationToken = default) =>
        _service.TranscribeAsync(config, audio, cancellationToken);
}

/// <summary>
/// Owns one live capture session. A normal Stop completes the PCM stream so the
/// provider receives its commit/final frame; Cancel aborts the provider session.
/// </summary>
public sealed class LiveStreamingSessionController : IAsyncDisposable
{
    private const int ChannelCapacity = 128;
    private readonly object _gate = new();
    private readonly IStreamingAudioCapture _capture;
    private readonly ILiveCloudTranscriber _transcriber;
    private Channel<ReadOnlyMemory<byte>>? _audio;
    private CancellationTokenSource? _sessionCancellation;
    private CancellationTokenRegistration _externalCancellation;
    private Task<LiveStreamingSessionOutcome>? _completion;
    private PlatformError? _captureFailure;
    private bool _starting;
    private bool _workerCompleted;
    private bool _disposed;

    public LiveStreamingSessionController(
        IStreamingAudioCapture capture,
        ILiveCloudTranscriber transcriber)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _transcriber = transcriber ?? throw new ArgumentNullException(nameof(transcriber));
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate) return _starting || _completion is { IsCompleted: false };
        }
    }

    public Task<LiveStreamingSessionOutcome>? Completion
    {
        get
        {
            lock (_gate) return _completion;
        }
    }

    public PlatformResult Start(
        LiveStreamingSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Channel<ReadOnlyMemory<byte>> channel;
        CancellationTokenSource sessionCancellation;
        TaskCompletionSource<LiveStreamingSessionOutcome> completionSource;
        CancellationTokenRegistration previousRegistration;
        CancellationTokenSource? previousCancellation;
        lock (_gate)
        {
            if (_disposed)
                return PlatformResult.Failure("streaming_disposed", "The live transcription controller is disposed.");
            if (_starting || _completion is { IsCompleted: false })
                return PlatformResult.Failure("streaming_already_active", "A live transcription session is already active.");

            previousRegistration = _externalCancellation;
            _externalCancellation = default;
            previousCancellation = _sessionCancellation;
            _captureFailure = null;
            _workerCompleted = false;
            channel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(ChannelCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
            sessionCancellation = new CancellationTokenSource();
            completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _audio = channel;
            _sessionCancellation = sessionCancellation;
            _completion = completionSource.Task;
            _starting = true;
        }
        previousRegistration.Dispose();
        previousCancellation?.Dispose();
        _capture.AudioChunkAvailable += OnAudioChunkAvailable;
        _capture.CaptureStopped += OnCaptureStopped;

        AudioRecordingOptions options;
        try
        {
            options = new AudioRecordingOptions(
                string.IsNullOrWhiteSpace(request.AudioDeviceId) ? "default" : request.AudioDeviceId,
                LiveCloudTranscriptionService.GetRequiredSampleRate(request.Config.Provider),
                BitsPerSample: 16,
                ChannelCount: request.ChannelCount);
        }
        catch (ArgumentOutOfRangeException)
        {
            return RollBackFailedStart(
                PlatformResult.Failure("streaming_provider_unsupported", "The live transcription provider is not supported."),
                request.Config.Provider, channel, sessionCancellation, completionSource);
        }

        PlatformResult started;
        try
        {
            started = _capture.Start(options);
        }
        catch (Exception)
        {
            try { _capture.Stop(); } catch { }
            return RollBackFailedStart(
                PlatformResult.Failure("streaming_capture_start_failed", "Live audio capture could not be started."),
                request.Config.Provider, channel, sessionCancellation, completionSource);
        }
        if (started.IsFailure)
        {
            return RollBackFailedStart(
                started, request.Config.Provider, channel, sessionCancellation, completionSource);
        }

        var worker = CompleteSessionAsync(
            request.Config,
            channel.Reader.ReadAllAsync(sessionCancellation.Token),
            sessionCancellation.Token);
        _ = RelayCompletionAsync(worker, completionSource);
        lock (_gate)
        {
            _starting = false;
        }
        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(
                static state => ((LiveStreamingSessionController)state!).RequestCancellation(), this);
            var disposeRegistration = false;
            lock (_gate)
            {
                if (_disposed || _workerCompleted) disposeRegistration = true;
                else _externalCancellation = registration;
            }
            if (disposeRegistration) registration.Dispose();
        }
        return PlatformResult.Success();
    }

    private PlatformResult RollBackFailedStart(
        PlatformResult failure,
        LiveTranscriptionProvider provider,
        Channel<ReadOnlyMemory<byte>> channel,
        CancellationTokenSource sessionCancellation,
        TaskCompletionSource<LiveStreamingSessionOutcome> completionSource)
    {
        channel.Writer.TryComplete();
        Unsubscribe();
        lock (_gate)
        {
            if (ReferenceEquals(_audio, channel)) _audio = null;
            if (ReferenceEquals(_sessionCancellation, sessionCancellation)) _sessionCancellation = null;
            if (ReferenceEquals(_completion, completionSource.Task)) _completion = null;
            _starting = false;
        }
        sessionCancellation.Dispose();
        completionSource.TrySetResult(new LiveStreamingSessionOutcome(
            new LiveTranscriptionResult(null, new LiveTranscriptionFailure(
                LiveTranscriptionFailureCode.InvalidRequest, failure.Error!.Message, provider), 0, 0),
            failure.Error,
            TimeSpan.Zero));
        return failure;
    }

    private static async Task RelayCompletionAsync(
        Task<LiveStreamingSessionOutcome> worker,
        TaskCompletionSource<LiveStreamingSessionOutcome> completionSource)
    {
        try
        {
            completionSource.TrySetResult(await worker.ConfigureAwait(false));
        }
        catch (OperationCanceledException cancellation)
        {
            completionSource.TrySetCanceled(cancellation.CancellationToken);
        }
        catch (Exception error)
        {
            completionSource.TrySetException(error);
        }
    }

    public async Task<LiveStreamingSessionOutcome> StopAsync(CancellationToken cancellationToken = default)
    {
        Task<LiveStreamingSessionOutcome> completion;
        lock (_gate)
        {
            completion = _completion ?? throw new InvalidOperationException("No live transcription session has been started.");
        }
        _capture.Stop();
        CompleteAudio();
        return await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<LiveStreamingSessionOutcome> CancelAsync(CancellationToken cancellationToken = default)
    {
        Task<LiveStreamingSessionOutcome> completion;
        lock (_gate)
        {
            completion = _completion ?? throw new InvalidOperationException("No live transcription session has been started.");
        }
        RequestCancellation();
        return await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<LiveStreamingSessionOutcome> CompleteSessionAsync(
        LiveTranscriptionConfig config,
        IAsyncEnumerable<ReadOnlyMemory<byte>> audio,
        CancellationToken cancellationToken)
    {
        LiveTranscriptionResult transcription;
        try
        {
            transcription = await _transcriber.TranscribeAsync(config, audio, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_capture.IsCapturing) _capture.Stop();
            CompleteAudio();
            Unsubscribe();
            CancellationTokenRegistration registration;
            lock (_gate)
            {
                _workerCompleted = true;
                registration = _externalCancellation;
                _externalCancellation = default;
            }
            await registration.DisposeAsync().ConfigureAwait(false);
        }

        PlatformError? failure;
        lock (_gate) failure = _captureFailure;
        return new LiveStreamingSessionOutcome(transcription, failure, _capture.Duration);
    }

    private void OnAudioChunkAvailable(object? sender, ReadOnlyMemory<byte> chunk)
    {
        if (chunk.IsEmpty) return;
        Channel<ReadOnlyMemory<byte>>? audio;
        lock (_gate) audio = _audio;
        if (audio is null) return;
        var owned = (ReadOnlyMemory<byte>)chunk.ToArray();
        if (!audio.Writer.TryWrite(owned))
        {
            lock (_gate)
            {
                _captureFailure ??= new PlatformError(
                    "streaming_audio_buffer_full", "Live audio could not be consumed quickly enough.");
            }
            CancelTransport();
        }
    }

    private void OnCaptureStopped(object? sender, PlatformError? error)
    {
        if (error is not null)
        {
            lock (_gate) _captureFailure ??= error;
        }
        CompleteAudio();
    }

    private void CompleteAudio()
    {
        Channel<ReadOnlyMemory<byte>>? audio;
        lock (_gate) audio = _audio;
        audio?.Writer.TryComplete();
    }

    private void RequestCancellation()
    {
        CancelTransport();
        if (_capture.IsCapturing) _capture.Stop();
    }

    private void CancelTransport()
    {
        CancellationTokenSource? cancellation;
        lock (_gate) cancellation = _sessionCancellation;
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
        CompleteAudio();
    }

    private void Unsubscribe()
    {
        _capture.AudioChunkAvailable -= OnAudioChunkAvailable;
        _capture.CaptureStopped -= OnCaptureStopped;
    }

    public async ValueTask DisposeAsync()
    {
        Task<LiveStreamingSessionOutcome>? completion;
        CancellationTokenRegistration registration;
        CancellationTokenSource? sessionCancellation;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            completion = _completion;
        }
        if (completion is { IsCompleted: false })
        {
            RequestCancellation();
            try { await completion.ConfigureAwait(false); } catch { }
        }
        Unsubscribe();
        lock (_gate)
        {
            registration = _externalCancellation;
            _externalCancellation = default;
            sessionCancellation = _sessionCancellation;
            _sessionCancellation = null;
            _audio = null;
        }
        registration.Dispose();
        sessionCancellation?.Dispose();
        _capture.Dispose();
    }
}
