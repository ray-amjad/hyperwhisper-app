using HyperWhisper.Linux.Overlay;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.PortableApplication.Audio;

// Issue #2411: the live "No sound from {mic}" warning. The real LinuxInteractionRecordingSession and
// TranscriptionWorkflow run here with a manual-clock MicSilenceWatcher; the stand-in microphone never
// reports a block, which is the "device that delivers nothing" case the timer exists for.
static class MicSilenceWarningSessionTests
{
    public static async Task SilentBatchRecordingWarnsWithTheOpenedDevice()
    {
        var clock = new ManualClock();
        using var watcher = new MicSilenceWatcher(clock.Now, startTimer: false);
        // Awaited, not returned: the watcher must outlive the session it is handed to.
        await RecordingStatusAfterStopTests.RunAsync(new FakeTextInjection(), async (shell, session, overlay) =>
        {
            // Before any recording the watcher is idle: time passing shows nothing.
            clock.Advance(TimeSpan.FromSeconds(5));
            watcher.Tick();
            Check(overlay.MicSilence.Count == 0, "the warning showed with no recording running");

            Check((await session.StartAsync(InteractionRecordingKind.Batch)).IsSuccess, "the batch recording did not start");
            var device = shell.Recording?.SelectedAudioDevice?.Name;
            Check(session.ActiveMicrophoneName == device && !string.IsNullOrEmpty(device),
                $"the session snapshotted '{session.ActiveMicrophoneName}', not the opened device '{device}'");
            Check(watcher.IsArmed, "capture started but the watcher was not armed");

            clock.Advance(TimeSpan.FromSeconds(1.9));
            watcher.Tick();
            Check(overlay.MicSilence.Count == 0, "the warning showed before 2.0 s of capture");
            clock.Advance(TimeSpan.FromSeconds(0.1));
            watcher.Tick();
            Check(overlay.MicSilence.Count == 1 && overlay.MicSilence[0]?.Value == device,
                $"after 2.0 s of nothing the overlay got [{Describe(overlay.MicSilence)}], not '{device}'");

            await session.StopAsync();
            Check(overlay.MicSilence.Count == 2 && overlay.MicSilence[1] is null,
                $"stopping did not hide the warning: [{Describe(overlay.MicSilence)}]");
            Check(!watcher.IsArmed, "the watcher stayed armed after the stop");

            // A new recording starts the clock again; a heard block hides the warning for good,
            // and cancelling hides nothing twice.
            Check((await session.StartAsync(InteractionRecordingKind.Batch)).IsSuccess, "the second start failed");
            clock.Advance(TimeSpan.FromSeconds(2));
            watcher.Tick();
            Check(overlay.MicSilence.Count == 3 && overlay.MicSilence[2] is not null, "the second silent recording did not warn");
            watcher.ObserveBlockPeak(-35);
            Check(overlay.MicSilence.Count == 4 && overlay.MicSilence[3] is null, "a heard block did not hide the warning");
            clock.Advance(TimeSpan.FromSeconds(10));
            watcher.Tick();
            await session.CancelAsync();
            Check(overlay.MicSilence.Count == 4, $"the warning came back after sound: [{Describe(overlay.MicSilence)}]");

            // Cancel while the warning is up hides it.
            Check((await session.StartAsync(InteractionRecordingKind.Batch)).IsSuccess, "the third start failed");
            clock.Advance(TimeSpan.FromSeconds(3));
            watcher.Tick();
            await session.CancelAsync();
            Check(overlay.MicSilence.Count == 6 && overlay.MicSilence[4] is not null && overlay.MicSilence[5] is null,
                $"cancel did not hide the warning: [{Describe(overlay.MicSilence)}]");
        }, watcher);
    }

    private static string Describe(List<LinuxOverlayMicrophoneLabel?> changes) =>
        string.Join(",", changes.Select(change => change is { } label ? $"'{label.Value}'" : "hide"));

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ManualClock
    {
        private long _ticks;
        public TimeSpan Now() => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
