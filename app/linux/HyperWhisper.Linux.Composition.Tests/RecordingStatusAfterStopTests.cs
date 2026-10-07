using System.Text;
using HyperWhisper.Linux;
using HyperWhisper.Linux.Overlay;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.Platform.Abstractions.Audio;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.PortableApplication.ViewModels;

// Issue #958: a finished in-app dictation left the status bar reading "Recording… - Press Ctrl+Alt to
// record" until a page change. The real LinuxInteractionRecordingSession drives a real
// TranscriptionWorkflow and the real shell view model here; only the microphone, the transcriber and
// the injection target are stand-ins. Every stop path (button, hotkey, push-to-talk, tray, onboarding)
// reaches this StopAsync through LinuxInteractionCoordinator.StopCoreAsync.
static class RecordingStatusAfterStopTests
{
    public static async Task BatchStopLeavesReady()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hw-958-status-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        // LinuxDesktopServices builds LinuxAppPaths from XDG_*; keep it out of the real home directory.
        var saved = new Dictionary<string, string?>();
        foreach (var name in new[] { "XDG_DATA_HOME", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME" })
        {
            saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, Path.Combine(root, name));
        }
        try
        {
            var paths = new StaticPaths(root);
            var database = new ApplicationDb(paths);
            var history = new HistoryRepository(database, paths);
            using var recorder = new StatusTestRecorder(root);
            using var devices = new StatusTestDevices();
            using var workflow = new TranscriptionWorkflow(
                recorder, devices, new StatusTestTranscriber(), history, textInjection: new FakeTextInjection());
            var settings = new PortableSettingsService(new MissingPrivateFiles(), Path.Combine(root, "settings.json"));
            using var shell = new ApplicationShellViewModel(database, settings, workflow, paths: paths);
            await shell.InitializeAsync();
            shell.Settings.EnableSoundEffects = false;
            workflow.RefreshDevices();
            using var services = new LinuxDesktopServices();
            var overlay = new StatusTestOverlay();
            var session = new LinuxInteractionRecordingSession(
                shell, workflow, services,
                new LinuxContextCaptureCoordinator(new FakeContextProvider(), new FakeOcr()),
                new CapturingPostProcessor(), history, overlay);

            var started = await session.StartAsync(InteractionRecordingKind.Batch);
            Check(started.IsSuccess, $"the batch recording did not start: {started.Error?.Code} {started.Error?.Message}");
            Check(shell.Status.Message == "Recording…", $"start wrote '{shell.Status.Message}', not 'Recording…'");

            var stopped = await session.StopAsync();
            Check(stopped.Result.IsSuccess, $"the batch stop failed: {stopped.Result.Error?.Code} {stopped.Result.Error?.Message}");
            Check(overlay.Completed, "the overlay never showed the completed dictation");
            Check(shell.Status.Message == "Ready" && !shell.Status.HasError,
                $"after a completed dictation the status bar read '{shell.Status.Message}', not 'Ready' (#958)");

            // A cancel that follows a second start keeps its own line, and is not reset to Ready.
            Check((await session.StartAsync(InteractionRecordingKind.Batch)).IsSuccess, "the second start failed");
            await session.CancelAsync();
            Check(shell.Status.Message == "Recording cancelled",
                $"a cancel read '{shell.Status.Message}', not 'Recording cancelled'");
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

sealed class StatusTestRecorder(string root) : IAudioRecorder
{
    private string? _path;
    public event EventHandler<float>? AudioLevelChanged { add { } remove { } }
    public bool IsRecording { get; private set; }
    public TimeSpan Duration => TimeSpan.FromSeconds(1);
    public PlatformResult Start(AudioRecordingOptions options)
    {
        _path = Path.Combine(root, $"capture-{Guid.NewGuid():N}.wav");
        var pcm = new byte[32000];
        using (var stream = File.Create(_path))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + pcm.Length);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1);
            writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(pcm.Length); writer.Write(pcm);
        }
        IsRecording = true;
        return PlatformResult.Success();
    }
    public PlatformResult<string> Stop()
    {
        IsRecording = false;
        return _path is null
            ? PlatformResult<string>.Failure("test.not_started", "The recorder was not started.")
            : PlatformResult<string>.Success(_path);
    }
    public void Dispose() { }
}

sealed class StatusTestDevices : IAudioInputDeviceService
{
    public event EventHandler? DevicesChanged { add { } remove { } }
    public PlatformResult<IReadOnlyList<AudioInputDevice>> GetAvailableDevices() =>
        PlatformResult<IReadOnlyList<AudioInputDevice>>.Success([new AudioInputDevice("mic.status-test", "Test microphone", true)]);
    public void Dispose() { }
}

sealed class StatusTestTranscriber : IRecordedAudioTranscriber
{
    public TranscriptionBackendCapability Capability { get; } = new(true, "Status test transcriber");
    public Task<PortableTranscriptionResult> TranscribeAsync(string audioPath, string? language,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(PortableTranscriptionResult.Success("hello from the status test", Capability.DisplayName));
}

sealed class StatusTestOverlay : ILinuxRecordingOverlayFeedback
{
    public bool Completed { get; private set; }
    public void RecordingStarted(LinuxOverlayModeLabel mode) { }
    public void StreamingStarted(LinuxOverlayModeLabel mode) { }
    public void StreamingConnectionChanged(LinuxStreamingOverlayConnectionState state) { }
    public void AudioLevelChanged(float level) { }
    public void Transcribing() { }
    void ILinuxRecordingOverlayFeedback.Completed(LinuxRecordingOverlayCompletion completion) => Completed = true;
    public void CancelConfirmationRequested() { }
    public void CancelConfirmationDismissed() { }
    public void Cancelled() { }
    public void Failed(LinuxRecordingOverlayError error) { }
    public void ModeChanged(LinuxOverlayModeLabel mode) { }
    public void Dispose() { }
}
