using HyperWhisper.LiveStreaming;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.SharedCore;
using System.Net.WebSockets;
using System.Text;

var tests = new (string Name, Func<Task> Run)[]
{
    ("mode router covers every shared-core provider", ModeRouterProviders),
    ("mode router rejects disabled unsupported and credentialless modes", ModeRouterFailures),
    ("normal stop completes audio and preserves provider final", NormalStopCommits),
    ("controller drives shared-core audio commit and final protocol", SharedCoreProtocol),
    ("OpenAI capture uses provider-required 24 kHz PCM", OpenAiSampleRate),
    ("capture chunks are copied before asynchronous consumption", AudioOwnership),
    ("external cancellation cancels rather than commits", ExternalCancellation),
    ("capture failures remain visible beside provider outcome", CaptureFailure),
    ("bounded audio backpressure fails closed", AudioBackpressure),
    ("synchronous capture callbacks do not deadlock start", SynchronousCaptureCallbacks),
    ("controller safely restarts after completed session", RestartAfterCompletion),
    ("immediate transcriber completion cannot run under state lock", ImmediateCompletion),
    ("throwing capture start rolls controller state back", ThrowingCaptureStart),
    ("unsupported provider rolls controller state back", UnsupportedProviderStart),
    ("completed session cancellation cannot stop a restarted session", OldCancellationIsolation),
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception error)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {error.Message}");
    }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed");
return failures == 0 ? 0 : 1;

static async Task ModeRouterProviders()
{
    var credentials = new FakeCredentials(new Dictionary<string, string>
    {
        ["DeepgramApiKey"] = "dg",
        ["ElevenLabsApiKey"] = "el",
        ["OpenAIApiKey"] = "oa",
        ["GrokApiKey"] = "xai",
        ["LicenseKey"] = "license",
    });
    var router = new LiveStreamingModeRouter(credentials);
    var cases = new (string Stored, LiveTranscriptionProvider Expected, string Account, bool License)[]
    {
        ("deepgram", LiveTranscriptionProvider.Deepgram, "DeepgramApiKey", false),
        ("elevenLabs", LiveTranscriptionProvider.ElevenLabs, "ElevenLabsApiKey", false),
        ("openAI", LiveTranscriptionProvider.OpenAi, "OpenAIApiKey", false),
        ("xai", LiveTranscriptionProvider.Grok, "GrokApiKey", false),
        ("hyperwhisperCloud", LiveTranscriptionProvider.HyperWhisperCloud, "LicenseKey", true),
    };
    foreach (var value in cases)
    {
        var result = await router.ResolveAsync(new(
            "mode", true, value.Stored, DeviceId: " mic ", Language: " en ",
            Vocabulary: ["Ray", " codex ", "ray"], Model: " model ", FastFormatting: true),
            ["HyperWhisper", "Codex"]);
        True(result.IsSuccess);
        Equal(value.Expected, result.Value!.Config.Provider);
        Equal("mic", result.Value.AudioDeviceId);
        Equal("en", result.Value.Config.Language);
        Equal("model", result.Value.Config.Model);
        Equal(3, result.Value.Config.Vocabulary!.Count);
        Equal(value.License ? "license" : null, result.Value.Config.LicenseKey);
        Equal(value.License ? null : credentials.Values[value.Account], result.Value.Config.ApiKey);
        True(result.Value.Config.FastFormatting);
    }
}

static async Task ModeRouterFailures()
{
    var router = new LiveStreamingModeRouter(
        new FakeCredentials(new Dictionary<string, string>()));
    var disabled = await router.ResolveAsync(new("m", false, "deepgram"));
    Equal("streaming_disabled", disabled.Error!.Code);
    var unknown = await router.ResolveAsync(new("m", true, "somewhere"));
    Equal("streaming_provider_unsupported", unknown.Error!.Code);
    var missing = await router.ResolveAsync(new("m", true, "openAI"));
    Equal("streaming_credential_missing", missing.Error!.Code);
    var deviceFallback = await router.ResolveAsync(new(
        "m", true, "hyperwhisperCloud", ClientDeviceId: "device-1"));
    True(deviceFallback.IsSuccess);
    Equal("device-1", deviceFallback.Value!.Config.DeviceId);
}

static async Task NormalStopCommits()
{
    using var capture = new FakeCapture();
    var transcriber = new CollectingTranscriber();
    await using var controller = new LiveStreamingSessionController(capture, transcriber);
    True(controller.Start(Request(LiveTranscriptionProvider.Deepgram)).IsSuccess);
    capture.Emit([1, 0, 2, 0]);
    var result = await controller.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
    True(result.IsSuccess);
    Equal("final transcript", result.Transcription.Transcript);
    True(transcriber.AudioCompleted);
    False(transcriber.WasCancelled);
    Equal(1, transcriber.Chunks.Count);
    Equal(1, capture.StopCalls);
}

static async Task OpenAiSampleRate()
{
    using var capture = new FakeCapture();
    await using var controller = new LiveStreamingSessionController(capture, new CollectingTranscriber());
    True(controller.Start(Request(LiveTranscriptionProvider.OpenAi)).IsSuccess);
    Equal(24000, capture.Options!.SampleRate);
    Equal(16, capture.Options.BitsPerSample);
    Equal(1, capture.Options.ChannelCount);
    await controller.StopAsync();
}

static async Task SharedCoreProtocol()
{
    using var capture = new FakeCapture();
    var socket = new CommitGateWebSocket();
    var sharedCore = new LiveCloudTranscriptionService(new SingleSocketFactory(socket));
    await using var controller = new LiveStreamingSessionController(
        capture, new SharedCoreLiveCloudTranscriber(sharedCore));
    True(controller.Start(Request(LiveTranscriptionProvider.Deepgram)).IsSuccess);
    capture.Emit([1, 0, 2, 0]);
    var result = await controller.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
    True(result.IsSuccess);
    Equal("verified final", result.Transcription.Transcript);
    True(socket.Sent.Any(value => value.Type == WebSocketMessageType.Binary && value.Data.SequenceEqual(new byte[] { 1, 0, 2, 0 })));
    True(socket.Sent.Any(value => value.Type == WebSocketMessageType.Text && Encoding.UTF8.GetString(value.Data).Contains("CloseStream", StringComparison.Ordinal)));
    Equal("api.deepgram.com", socket.Options!.Uri.Host);
}

static async Task AudioOwnership()
{
    using var capture = new FakeCapture();
    var transcriber = new CollectingTranscriber();
    await using var controller = new LiveStreamingSessionController(capture, transcriber);
    True(controller.Start(Request(LiveTranscriptionProvider.ElevenLabs)).IsSuccess);
    byte[] bytes = [4, 0, 8, 0];
    capture.Emit(bytes);
    bytes[0] = 99;
    await controller.StopAsync();
    Equal((byte)4, transcriber.Chunks.Single()[0]);
}

static async Task ExternalCancellation()
{
    using var capture = new FakeCapture();
    var transcriber = new CollectingTranscriber();
    await using var controller = new LiveStreamingSessionController(capture, transcriber);
    using var cancellation = new CancellationTokenSource();
    True(controller.Start(Request(LiveTranscriptionProvider.Grok), cancellation.Token).IsSuccess);
    cancellation.Cancel();
    var result = await controller.Completion!.WaitAsync(TimeSpan.FromSeconds(2));
    Equal(LiveTranscriptionFailureCode.Cancelled, result.Transcription.Failure!.Code);
    True(transcriber.WasCancelled);
}

static async Task CaptureFailure()
{
    using var capture = new FakeCapture();
    await using var controller = new LiveStreamingSessionController(capture, new CollectingTranscriber());
    True(controller.Start(Request(LiveTranscriptionProvider.HyperWhisperCloud)).IsSuccess);
    capture.Fail(new PlatformError("microphone_disconnected", "The microphone was disconnected."));
    var result = await controller.Completion!.WaitAsync(TimeSpan.FromSeconds(2));
    Equal("microphone_disconnected", result.CaptureFailure!.Code);
    False(result.IsSuccess);
}

static async Task AudioBackpressure()
{
    using var capture = new FakeCapture();
    var transcriber = new BlockingTranscriber();
    await using var controller = new LiveStreamingSessionController(capture, transcriber);
    True(controller.Start(Request(LiveTranscriptionProvider.Deepgram)).IsSuccess);
    for (var i = 0; i < 140; i++) capture.Emit([1, 0]);
    var result = await controller.Completion!.WaitAsync(TimeSpan.FromSeconds(2));
    Equal("streaming_audio_buffer_full", result.CaptureFailure!.Code);
    Equal(LiveTranscriptionFailureCode.Cancelled, result.Transcription.Failure!.Code);
}

static async Task SynchronousCaptureCallbacks()
{
    using var capture = new FakeCapture { EmitAndStopDuringStart = true };
    var transcriber = new CollectingTranscriber();
    await using var controller = new LiveStreamingSessionController(capture, transcriber);
    True(controller.Start(Request(LiveTranscriptionProvider.Deepgram)).IsSuccess);
    var result = await controller.Completion!.WaitAsync(TimeSpan.FromSeconds(2));
    True(result.IsSuccess);
    Equal((byte)7, transcriber.Chunks.Single()[0]);
}

static async Task RestartAfterCompletion()
{
    using var capture = new FakeCapture();
    var transcriber = new CollectingTranscriber();
    await using var controller = new LiveStreamingSessionController(capture, transcriber);
    True(controller.Start(Request(LiveTranscriptionProvider.Deepgram)).IsSuccess);
    capture.Emit([1, 0]);
    await controller.StopAsync();
    True(controller.Start(Request(LiveTranscriptionProvider.ElevenLabs)).IsSuccess);
    capture.Emit([2, 0]);
    var second = await controller.StopAsync();
    True(second.IsSuccess);
    Equal(2, transcriber.Chunks.Count);
}

static async Task ImmediateCompletion()
{
    using var capture = new FakeCapture { StopWaitsForCallback = true };
    await using var controller = new LiveStreamingSessionController(capture, new ImmediateTranscriber());
    True(controller.Start(Request(LiveTranscriptionProvider.Deepgram)).IsSuccess);
    var result = await controller.Completion!.WaitAsync(TimeSpan.FromSeconds(2));
    True(result.IsSuccess);
    False(controller.IsRunning);
}

static async Task ThrowingCaptureStart()
{
    using var capture = new ThrowingCapture();
    await using var controller = new LiveStreamingSessionController(capture, new ImmediateTranscriber());
    var result = controller.Start(Request(LiveTranscriptionProvider.Deepgram));
    Equal("streaming_capture_start_failed", result.Error!.Code);
    False(controller.IsRunning);
    Equal<Task<LiveStreamingSessionOutcome>?>(null, controller.Completion);
}

static async Task UnsupportedProviderStart()
{
    using var capture = new FakeCapture();
    await using var controller = new LiveStreamingSessionController(capture, new ImmediateTranscriber());
    var invalid = controller.Start(Request((LiveTranscriptionProvider)999));
    Equal("streaming_provider_unsupported", invalid.Error!.Code);
    False(controller.IsRunning);
    Equal<Task<LiveStreamingSessionOutcome>?>(null, controller.Completion);
    True(controller.Start(Request(LiveTranscriptionProvider.Deepgram)).IsSuccess);
    await controller.Completion!.WaitAsync(TimeSpan.FromSeconds(2));
}

static async Task OldCancellationIsolation()
{
    using var capture = new FakeCapture();
    await using var controller = new LiveStreamingSessionController(capture, new CollectingTranscriber());
    using var oldCancellation = new CancellationTokenSource();
    True(controller.Start(Request(LiveTranscriptionProvider.Deepgram), oldCancellation.Token).IsSuccess);
    capture.Emit([1, 0]);
    await controller.StopAsync();
    True(controller.Start(Request(LiveTranscriptionProvider.ElevenLabs)).IsSuccess);
    oldCancellation.Cancel();
    True(controller.IsRunning);
    capture.Emit([2, 0]);
    True((await controller.StopAsync()).IsSuccess);
}

static LiveStreamingSessionRequest Request(LiveTranscriptionProvider provider) =>
    new(new LiveTranscriptionConfig(provider, ApiKey: "secret", LicenseKey: "license"), "mic");

static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Expected true.");
}

static void False(bool value) => True(!value);

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, received {actual}.");
}

sealed class FakeCredentials(IReadOnlyDictionary<string, string> values) : ILiveStreamingCredentialSource
{
    public IReadOnlyDictionary<string, string> Values { get; } = values;
    public Task<string?> GetCredentialAsync(string account, CancellationToken cancellationToken = default) =>
        Task.FromResult(Values.TryGetValue(account, out var value) ? value : null);
}

sealed class FakeCapture : IStreamingAudioCapture
{
    public event EventHandler<ReadOnlyMemory<byte>>? AudioChunkAvailable;
    public event EventHandler<float>? AudioLevelChanged;
    public event EventHandler<PlatformError?>? CaptureStopped;
    public bool IsCapturing { get; private set; }
    public TimeSpan Duration { get; private set; }
    public AudioRecordingOptions? Options { get; private set; }
    public int StopCalls { get; private set; }
    public bool EmitAndStopDuringStart { get; init; }
    public bool StopWaitsForCallback { get; init; }

    public PlatformResult Start(AudioRecordingOptions options)
    {
        Options = options;
        IsCapturing = true;
        if (EmitAndStopDuringStart)
        {
            Emit([7, 0]);
            Stop();
        }
        return PlatformResult.Success();
    }

    public void Emit(byte[] value)
    {
        Duration += TimeSpan.FromMilliseconds(10);
        AudioChunkAvailable?.Invoke(this, value);
        AudioLevelChanged?.Invoke(this, 0.5f);
    }

    public void Fail(PlatformError error)
    {
        IsCapturing = false;
        CaptureStopped?.Invoke(this, error);
    }

    public void Stop()
    {
        if (!IsCapturing) return;
        StopCalls++;
        IsCapturing = false;
        if (StopWaitsForCallback)
        {
            var callback = Task.Run(() => CaptureStopped?.Invoke(this, null));
            if (!callback.Wait(TimeSpan.FromSeconds(1)))
                throw new InvalidOperationException("Capture callback was blocked by the controller state lock.");
        }
        else CaptureStopped?.Invoke(this, null);
    }

    public void Dispose() => Stop();
}

sealed class CollectingTranscriber : ILiveCloudTranscriber
{
    public List<byte[]> Chunks { get; } = [];
    public bool AudioCompleted { get; private set; }
    public bool WasCancelled { get; private set; }

    public async Task<LiveTranscriptionResult> TranscribeAsync(
        LiveTranscriptionConfig config,
        IAsyncEnumerable<ReadOnlyMemory<byte>> audio,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await foreach (var chunk in audio.WithCancellation(cancellationToken))
                Chunks.Add(chunk.ToArray());
            AudioCompleted = true;
            return new("final transcript", null, Chunks.Count, 1);
        }
        catch (OperationCanceledException)
        {
            WasCancelled = true;
            return new(null, new LiveTranscriptionFailure(
                LiveTranscriptionFailureCode.Cancelled, "cancelled", config.Provider), Chunks.Count, 0);
        }
    }
}

sealed class BlockingTranscriber : ILiveCloudTranscriber
{
    public async Task<LiveTranscriptionResult> TranscribeAsync(
        LiveTranscriptionConfig config,
        IAsyncEnumerable<ReadOnlyMemory<byte>> audio,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
        catch (OperationCanceledException)
        {
            return new(null, new LiveTranscriptionFailure(
                LiveTranscriptionFailureCode.Cancelled, "cancelled", config.Provider), 0, 0);
        }
    }
}

sealed class ImmediateTranscriber : ILiveCloudTranscriber
{
    public Task<LiveTranscriptionResult> TranscribeAsync(
        LiveTranscriptionConfig config,
        IAsyncEnumerable<ReadOnlyMemory<byte>> audio,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new LiveTranscriptionResult("immediate final", null, 0, 1));
}

sealed class ThrowingCapture : IStreamingAudioCapture
{
    public event EventHandler<ReadOnlyMemory<byte>>? AudioChunkAvailable { add { } remove { } }
    public event EventHandler<float>? AudioLevelChanged { add { } remove { } }
    public event EventHandler<PlatformError?>? CaptureStopped { add { } remove { } }
    public bool IsCapturing => false;
    public TimeSpan Duration => TimeSpan.Zero;
    public PlatformResult Start(AudioRecordingOptions options) =>
        throw new IOException("simulated capture adapter failure");
    public void Stop() { }
    public void Dispose() { }
}

sealed class SingleSocketFactory(CommitGateWebSocket socket) : IStreamingWebSocketFactory
{
    public IStreamingWebSocket Create() => socket;
}

sealed class CommitGateWebSocket : IStreamingWebSocket
{
    private readonly TaskCompletionSource _committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _receiveIndex;
    public StreamingWebSocketConnectOptions? Options { get; private set; }
    public List<(byte[] Data, WebSocketMessageType Type)> Sent { get; } = [];

    public Task ConnectAsync(StreamingWebSocketConnectOptions options, CancellationToken cancellationToken)
    {
        Options = options;
        return Task.CompletedTask;
    }

    public Task SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType messageType, CancellationToken cancellationToken)
    {
        var owned = data.ToArray();
        Sent.Add((owned, messageType));
        if (messageType == WebSocketMessageType.Text &&
            Encoding.UTF8.GetString(owned).Contains("CloseStream", StringComparison.Ordinal))
            _committed.TrySetResult();
        return Task.CompletedTask;
    }

    public async Task<StreamingWebSocketFrame> ReceiveAsync(CancellationToken cancellationToken)
    {
        await _committed.Task.WaitAsync(cancellationToken);
        return Interlocked.Increment(ref _receiveIndex) == 1
            ? new StreamingWebSocketFrame(
                Encoding.UTF8.GetBytes("{\"type\":\"Results\",\"is_final\":true,\"channel\":{\"alternatives\":[{\"transcript\":\"verified final\"}]}}"),
                WebSocketMessageType.Text)
            : new StreamingWebSocketFrame([], WebSocketMessageType.Close,
                CloseStatus: WebSocketCloseStatus.NormalClosure);
    }

    public Task CloseAsync(WebSocketCloseStatus status, CancellationToken cancellationToken) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
