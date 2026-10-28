using System.Reflection;
using HyperWhisper.Linux;
using HyperWhisper.Linux.Overlay;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.PortableApplication.ViewModels;

// Issue #2038, the start window. The streaming overlay appears (connecting) as soon as a live start
// begins, but live capture only turns on once the provider has answered, so Home read "Idle" for the
// whole connect phase. The real LinuxInteractionRecordingSession runs a live start that fails to
// connect (no provider key in this profile): Home must read Recording the moment the overlay shows,
// Failed once the overlay turns red, and the workflow's own state again once the start has returned,
// with an ActivityChanged after that last change so the caption is refreshed (no stuck Recording).
static class HomeLiveStartTests
{
    private const string Workflow = "Idle";

    public static async Task StartWindowFollowsTheOverlay()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hw-2038-start-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
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
            shell.Settings.StreamingEnabled = true;
            workflow.RefreshDevices();
            using var services = new LinuxDesktopServices();
            var overlay = new StartWindowOverlay();
            var session = new LinuxInteractionRecordingSession(
                shell, workflow, services,
                new LinuxContextCaptureCoordinator(new FakeContextProvider(), new FakeOcr()),
                new CapturingPostProcessor(), history, overlay);
            overlay.Read = () => session.HomeRecordingState(Workflow);
            var raised = new List<string?>();
            session.ActivityChanged += (_, _) => raised.Add(session.HomeRecordingState(Workflow));

            Check(session.HomeRecordingState(Workflow) == Workflow, "Home did not read the workflow state at rest");
            var started = await session.StartAsync(InteractionRecordingKind.Streaming);
            Console.WriteLine($"  [en] start: {(started.IsSuccess ? "succeeded" : started.Error?.Code)}; Home when the overlay showed "
                + $"\"{overlay.AtStreamingStarted}\", when it turned red \"{overlay.AtError}\", after the start "
                + $"\"{session.HomeRecordingState(Workflow)}\"; ActivityChanged saw [{string.Join(", ", raised)}]");
            Check(started.IsFailure, "the live start connected with no provider key in the profile");
            Check(overlay.AtStreamingStarted == nameof(TranscriptionWorkflowState.Recording),
                $"Home read '{overlay.AtStreamingStarted}' when the streaming overlay appeared, not Recording (#2038)");
            Check(overlay.AtError == nameof(TranscriptionWorkflowState.Failed),
                $"Home read '{overlay.AtError}' while the overlay was red, not Failed (#2038)");
            Check(session.HomeRecordingState(Workflow) == Workflow,
                $"Home read '{session.HomeRecordingState(Workflow)}' after the failed start, not the workflow state");
            Check(raised.Count > 0 && raised[^1] == Workflow,
                $"the last ActivityChanged read '{(raised.Count > 0 ? raised[^1] : "nothing")}', so the caption was not refreshed after the start");
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // The refused-connection run (#2038 verify): the overlay turned red, the coordinator's auto-stop
    // (#2278) ran StopAsync, and its first line cleared live capture, so Home read Idle beside the red
    // overlay until the stop finished and Failed never showed. A held stream whose connection failed is
    // stopped here: Home must read Failed through the whole stop and the workflow state once it ends.
    public static async Task StopOfAFailedStreamKeepsFailed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hw-2038-stop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
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
            using var services = new LinuxDesktopServices();
            var overlay = new StopOverlay();
            var session = new LinuxInteractionRecordingSession(
                shell, workflow, services,
                new LinuxContextCaptureCoordinator(new FakeContextProvider(), new FakeOcr()),
                new CapturingPostProcessor(), history, overlay);
            overlay.Read = () => session.HomeRecordingState(Workflow);
            var seen = new List<string?>();
            session.ActivityChanged += (_, _) => seen.Add(session.HomeRecordingState(Workflow));

            // A held stream: live capture on, its connection in the error state, the worker gone.
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(LinuxInteractionRecordingSession).GetField("_streaming", flags)!.SetValue(session, true);
            typeof(LinuxInteractionRecordingSession).GetField("_liveConnectionFailed", flags)!.SetValue(session, true);
            typeof(LinuxInteractionRecordingSession).GetMethod("SetLiveCapture", flags)!.Invoke(session, [true]);
            Check(session.HomeRecordingState(Workflow) == nameof(TranscriptionWorkflowState.Failed),
                $"Home read '{session.HomeRecordingState(Workflow)}' for a held failed stream, not Failed");
            seen.Clear();

            try { await session.StopAsync(); } catch { }
            Console.WriteLine($"  [en] Home at the stop's Transcribing \"{overlay.AtTranscribing}\", at its end "
                + $"\"{overlay.AtEnd}\", after \"{session.HomeRecordingState(Workflow)}\"; ActivityChanged saw [{string.Join(", ", seen)}]");
            Check(overlay.AtTranscribing == nameof(TranscriptionWorkflowState.Failed),
                $"Home read '{overlay.AtTranscribing}' while the stop of a failed stream began, not Failed (#2038)");
            Check(overlay.AtEnd == nameof(TranscriptionWorkflowState.Failed),
                $"Home read '{overlay.AtEnd}' when the stop of a failed stream showed its result, not Failed (#2038)");
            var idleBeforeEnd = seen.Take(Math.Max(0, seen.Count - 1)).Where(s => s != nameof(TranscriptionWorkflowState.Failed)).ToList();
            Check(idleBeforeEnd.Count == 0,
                $"an ActivityChanged during the stop read [{string.Join(", ", idleBeforeEnd)}], so Home left Failed early (#2038)");
            Check(session.HomeRecordingState(Workflow) == Workflow && seen.Count > 0 && seen[^1] == Workflow,
                $"Home read '{session.HomeRecordingState(Workflow)}' after the stop (last ActivityChanged "
                + $"'{(seen.Count > 0 ? seen[^1] : "nothing")}'), not the workflow state");
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

    private sealed class StartWindowOverlay : ILinuxRecordingOverlayFeedback
    {
        public Func<string?> Read { get; set; } = () => null;
        public string? AtStreamingStarted { get; private set; }
        public string? AtError { get; private set; }
        public void RecordingStarted(LinuxOverlayModeLabel mode) { }
        public void StreamingStarted(LinuxOverlayModeLabel mode) => AtStreamingStarted = Read();
        public void StreamingConnectionChanged(LinuxStreamingOverlayConnectionState state)
        {
            if (state == LinuxStreamingOverlayConnectionState.Error) AtError ??= Read();
        }
        public void AudioLevelChanged(float level) { }
        public void Transcribing() { }
        public void Completed(LinuxRecordingOverlayCompletion completion) { }
        public void CancelConfirmationRequested() { }
        public void CancelConfirmationDismissed() { }
        public void Cancelled() { }
        public void Failed(LinuxRecordingOverlayError error) { }
        public void ModeChanged(LinuxOverlayModeLabel mode) { }
        public void Dispose() { }
    }

    private sealed class StopOverlay : ILinuxRecordingOverlayFeedback
    {
        public Func<string?> Read { get; set; } = () => null;
        public string? AtTranscribing { get; private set; }
        public string? AtEnd { get; private set; }
        public void RecordingStarted(LinuxOverlayModeLabel mode) { }
        public void StreamingStarted(LinuxOverlayModeLabel mode) { }
        public void StreamingConnectionChanged(LinuxStreamingOverlayConnectionState state) { }
        public void AudioLevelChanged(float level) { }
        public void Transcribing() => AtTranscribing = Read();
        public void Completed(LinuxRecordingOverlayCompletion completion) => AtEnd = Read();
        public void CancelConfirmationRequested() { }
        public void CancelConfirmationDismissed() { }
        public void Cancelled() => AtEnd = Read();
        public void Failed(LinuxRecordingOverlayError error) => AtEnd = Read();
        public void ModeChanged(LinuxOverlayModeLabel mode) { }
        public void Dispose() { }
    }
}
