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
    public static Task BatchStopLeavesReady() => RunAsync(new FakeTextInjection(), async (shell, session, overlay) =>
    {
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
    });

    // Issue #1703: a transcript whose paste and clipboard write both failed showed "Copied!" on the overlay.
    public static Task FailedCopyShowsCopyFailed() => RunAsync(new FailingTextInjection(), async (shell, session, overlay) =>
    {
        Check((await session.StartAsync(InteractionRecordingKind.Batch)).IsSuccess, "the batch recording did not start");
        var stopped = await session.StopAsync();
        Check(stopped.Result.IsSuccess, $"the batch stop failed: {stopped.Result.Error?.Code} {stopped.Result.Error?.Message}");
        Check(overlay.Completion == LinuxRecordingOverlayCompletion.CopyFailed,
            $"a failed clipboard write showed {overlay.Completion?.ToString() ?? "nothing"} on the overlay, not CopyFailed (#1703)");
        // Issue #1402: the status bar read "Ready" with no word about the failed paste.
        Check(shell.Status.Message == LinuxInteractionRecordingSession.BatchInjectionFailedStatus,
            $"after a batch dictation whose paste failed the status bar read '{shell.Status.Message}', not the paste-failed line (#1402)");

        Check(LinuxInteractionRecordingSession.MapCompletion(TextInjectionOutcome.Failed) == LinuxRecordingOverlayCompletion.CopyFailed
            && LinuxInteractionRecordingSession.MapCompletion(TextInjectionOutcome.CopiedToClipboard) == LinuxRecordingOverlayCompletion.Copied
            && LinuxInteractionRecordingSession.MapCompletion(TextInjectionOutcome.Pasted) == LinuxRecordingOverlayCompletion.Pasted
            && LinuxInteractionRecordingSession.MapCompletion(TextInjectionOutcome.SecureFieldSkipped) == LinuxRecordingOverlayCompletion.SecureField,
            "the overlay completion mapping changed");
    });

    // Issue #2599: the status bar appended "Press Ctrl+Alt to record" to every line, so a recording read
    // "Recording… - Press Ctrl+Alt to record" while Ctrl+Alt would stop it. The rule now (the issue's
    // Proposed fix): no hint while anything is in flight or on an error line; every other line keeps it.
    // The line is composed by MainWindow.StatusBarLine from the real session and the real Home recorder
    // of a real batch dictation, and re-composed through MainWindow.WatchStatusBarInputs, the same watch
    // the window uses, so the line read here is the one the window would show after each change.
    public static Task StatusHintDroppedWhileInFlightOrOnError() => RunAsync(new FakeTextInjection(), async (shell, session, _) =>
    {
        var line = "";
        using var watch = WatchLine(shell, session, value => line = value);

        shell.ShowReadyStatus();
        Check(line == "Ready - Press Ctrl+Alt to record", $"at rest the status bar read '{line}'");

        Check((await session.StartAsync(InteractionRecordingKind.Batch)).IsSuccess, "the batch recording did not start");
        Check(line == "Recording…", $"while recording the status bar read '{line}', not 'Recording…' (#2599)");

        var stopped = await session.StopAsync();
        Check(stopped.Result.IsSuccess, $"the batch stop failed: {stopped.Result.Error?.Code} {stopped.Result.Error?.Message}");
        Check(line == "Ready - Press Ctrl+Alt to record", $"after the recording ended the status bar read '{line}'");

        // A shell Busy line is in flight too: "Importing universal backup…" must not invite a recording.
        shell.Status.Busy("Importing universal backup…");
        Check(line == "Importing universal backup…", $"a Busy line read '{line}' (#2599)");
        shell.Status.Success("Backup imported");
        Check(line == "Backup imported - Press Ctrl+Alt to record", $"after the Busy line ended the status bar read '{line}'");

        // Windows' status.failed carries no hint.
        shell.Status.Failure("provider.credential_rejected", "The provider rejected the supplied credential.");
        Check(line == "The provider rejected the supplied credential.", $"after a failure the status bar read '{line}' (#2599)");
        // A failure with no message still names its code, and still no hint.
        Check(MainWindow.ComposeStatusLine(null, true, "workflow.start_failed", "Ctrl+Alt", Hint, withHint: false) == "workflow.start_failed",
            "an error with no message lost its code");
        // No shortcut set: the line is the message alone.
        Check(MainWindow.ComposeStatusLine("Ready", false, null, "", Hint, withHint: true) == "Ready", "an empty shortcut still added a hint");
    });

    // #2599: a stop that writes its own line while it is still finishing ("Transcription saved, but text
    // injection failed", or a live stream's "… pasted and saved") changes nothing after it, so only the
    // session's ActivityChanged at the end of StopAsync brings the hint back with that resting line.
    public static Task HintReturnsAfterAStopThatWroteItsOwnLine() => RunAsync(new FailingTextInjection(), async (shell, session, _) =>
    {
        var line = "";
        var lines = new List<string>();
        using var watch = WatchLine(shell, session, value => { line = value; lines.Add(value); });

        Check((await session.StartAsync(InteractionRecordingKind.Batch)).IsSuccess, "the batch recording did not start");
        var stopped = await session.StopAsync();
        Check(stopped.Result.IsSuccess, $"the batch stop failed: {stopped.Result.Error?.Code} {stopped.Result.Error?.Message}");
        var expected = $"{LinuxInteractionRecordingSession.BatchInjectionFailedStatus} - Press Ctrl+Alt to record";
        Check(lines.Contains(LinuxInteractionRecordingSession.BatchInjectionFailedStatus),
            $"the stop's own line was never shown without the hint while it finished: {string.Join(" | ", lines)}");
        Check(line == expected, $"after the stop finished the status bar read '{line}', not '{expected}' (#2599)");
    });

    private const string Hint = "Press {0} to record";

    private static IDisposable WatchLine(ApplicationShellViewModel shell, LinuxInteractionRecordingSession session, Action<string> show)
    {
        void Compose() => show(MainWindow.StatusBarLine(shell.Status, "Ctrl+Alt", Hint, session, shell.Recording));
        Compose();
        return MainWindow.WatchStatusBarInputs(shell.Status, shell.Recording, session, Compose);
    }

    private static async Task RunAsync(ITextInjectionService textInjection,
        Func<ApplicationShellViewModel, LinuxInteractionRecordingSession, StatusTestOverlay, Task> body)
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
                recorder, devices, new StatusTestTranscriber(), history, textInjection: textInjection);
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
            await body(shell, session, overlay);
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
    public bool Completed => Completion is not null;
    public LinuxRecordingOverlayCompletion? Completion { get; private set; }
    public void RecordingStarted(LinuxOverlayModeLabel mode) { }
    public void StreamingStarted(LinuxOverlayModeLabel mode) { }
    public void StreamingConnectionChanged(LinuxStreamingOverlayConnectionState state) { }
    public void AudioLevelChanged(float level) { }
    public void Transcribing() { }
    void ILinuxRecordingOverlayFeedback.Completed(LinuxRecordingOverlayCompletion completion) => Completion = completion;
    public void CancelConfirmationRequested() { }
    public void CancelConfirmationDismissed() { }
    public void Cancelled() { }
    public void Failed(LinuxRecordingOverlayError error) { }
    public void ModeChanged(LinuxOverlayModeLabel mode) { }
    public void Dispose() { }
}

/// <summary>The clipboard helper is missing or times out: neither the paste nor the copy lands.</summary>
sealed class FailingTextInjection : ITextInjectionService
{
    public bool IsCapturedTargetAvailable => true;
    public void CaptureTarget() { }
    public void StartSession() { }
    public void EndSession() { }
    public void CancelPendingClipboardRestore() { }
    public void ScheduleClipboardRestore(TimeSpan delay) { }
    public ValueTask<PlatformResult> RestoreClipboardImmediatelyAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(PlatformResult.Success());
    public ValueTask<PlatformResult> CopyToClipboardAsync(string text, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(PlatformResult.Failure("clipboard.write_failed", "The clipboard helper failed."));
    public ValueTask<TextInjectionOutcome> InjectTranscriptAsync(string text, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(TextInjectionOutcome.Failed);
    public void Dispose() { }
}
