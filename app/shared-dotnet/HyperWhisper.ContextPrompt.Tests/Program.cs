using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;

var audioPath = Path.Combine(Path.GetTempPath(), $"hyperwhisper-context-{Guid.NewGuid():N}.wav");
await File.WriteAllBytesAsync(audioPath, [0]);
try
{
    var expected = new ApplicationContextSnapshot
    {
        ProcessName = "Firefox",
        WindowTitle = "Inbox",
        AppType = "browser",
        ScreenOcrText = "Visible screen text",
    };
    var processor = new ContextRecordingProcessor();
    using var workflow = new TranscriptionWorkflow(
        new UnusedRecorder(),
        new EmptyDevices(),
        new SuccessfulTranscriber(),
        new MemoryHistory(),
        processor);
    var mode = new Mode
    {
        Name = "Context",
        PostProcessingMode = 2,
        PostProcessingProvider = "local_llm",
    };

    var result = await workflow.TranscribeFileAsync(
        audioPath,
        new TranscriptionWorkflowRequest(SelectedMode: mode, ApplicationContext: expected));
    Assert.True(result.IsSuccess, DescribeFailure(result));
    Assert.Same(expected, processor.Context);

    var legacy = new LegacyProcessor();
    using var legacyWorkflow = new TranscriptionWorkflow(
        new UnusedRecorder(),
        new EmptyDevices(),
        new SuccessfulTranscriber(),
        new MemoryHistory(),
        legacy);
    result = await legacyWorkflow.TranscribeFileAsync(
        audioPath,
        new TranscriptionWorkflowRequest(SelectedMode: mode, ApplicationContext: expected));
    Assert.True(result.IsSuccess, DescribeFailure(result));
    Assert.True(legacy.WasCalled);

    Console.WriteLine("PASS workflow carries application context and preserves legacy processors");
    return 0;
}
finally
{
    File.Delete(audioPath);
}

static string DescribeFailure(PortableTranscriptionResult result) => result.Failure is null
    ? $"Expected a non-empty successful result, but received text '{result.Text ?? "<null>"}'."
    : $"Expected success, but received {result.Failure.Code}: {result.Failure.Message}";

sealed class ContextRecordingProcessor : ITranscriptionPostProcessor
{
    public ApplicationContextSnapshot? Context { get; private set; }

    public Task<PortablePostProcessingResult> ProcessAsync(
        string transcript,
        Mode mode,
        ApplicationContextSnapshot? applicationContext,
        CancellationToken cancellationToken = default)
    {
        Context = applicationContext;
        return Task.FromResult(PortablePostProcessingResult.Applied(transcript, "context-test"));
    }

    public Task<PortablePostProcessingResult> ProcessAsync(
        string transcript,
        Mode mode,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The context-aware overload must be used.");
}

sealed class LegacyProcessor : ITranscriptionPostProcessor
{
    public bool WasCalled { get; private set; }

    public Task<PortablePostProcessingResult> ProcessAsync(
        string transcript,
        Mode mode,
        CancellationToken cancellationToken = default)
    {
        WasCalled = true;
        return Task.FromResult(PortablePostProcessingResult.Applied(transcript, "legacy-test"));
    }
}

sealed class SuccessfulTranscriber : IRecordedAudioTranscriber
{
    public TranscriptionBackendCapability Capability { get; } = new(true, "test");

    public Task<PortableTranscriptionResult> TranscribeAsync(
        string audioPath,
        string? language,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(PortableTranscriptionResult.Success("hello", "test"));
}

sealed class MemoryHistory : ITranscriptionHistoryStore
{
    private readonly Dictionary<Guid, Transcript> _items = [];

    public Task<Transcript?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_items.GetValueOrDefault(id));

    public Task AddAsync(Transcript transcript, CancellationToken cancellationToken = default)
    {
        _items.Add(transcript.Id, transcript);
        return Task.CompletedTask;
    }

    public Task<bool> UpdateAsync(Transcript transcript, CancellationToken cancellationToken = default)
    {
        if (!_items.ContainsKey(transcript.Id)) return Task.FromResult(false);
        _items[transcript.Id] = transcript;
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_items.Remove(id));
}

sealed class EmptyDevices : IAudioInputDeviceService
{
    public event EventHandler? DevicesChanged { add { } remove { } }
    public PlatformResult<IReadOnlyList<AudioInputDevice>> GetAvailableDevices() =>
        PlatformResult<IReadOnlyList<AudioInputDevice>>.Success([]);
    public void Dispose() { }
}

sealed class UnusedRecorder : IAudioRecorder
{
    public event EventHandler<float>? AudioLevelChanged { add { } remove { } }
    public bool IsRecording => false;
    public TimeSpan Duration => TimeSpan.Zero;
    public PlatformResult Start(AudioRecordingOptions options) =>
        PlatformResult.Failure("unused", "unused");
    public PlatformResult<string> Stop() =>
        PlatformResult<string>.Failure("unused", "unused");
    public void Dispose() { }
}

static class Assert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition) throw new InvalidOperationException(message ?? "Expected true.");
    }

    public static void Same(object expected, object? actual)
    {
        if (!ReferenceEquals(expected, actual)) throw new InvalidOperationException("Expected the same instance.");
    }
}
