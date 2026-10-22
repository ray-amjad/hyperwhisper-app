using HyperWhisper.Linux.Overlay;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.Platform.Abstractions;
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

    // The sandbox repro: the selected mic was an idle virtual input whose master is the default output,
    // so the start chime (sound effects are on by default) came back through it at -20 dBFS and disarmed
    // the watcher; the warning never showed. This drives the coordinator the Home button, the tray and
    // the hotkeys call, with sound effects ON and a chime that loops back into capture while it plays.
    public static async Task StartChimeLoopingIntoTheMicDoesNotHideTheWarning()
    {
        var clock = new ManualClock();
        using var watcher = new MicSilenceWatcher(clock.Now, startTimer: false);
        var chime = new LoopbackChime(watcher, clock);
        await RecordingStatusAfterStopTests.RunAsync(new FakeTextInjection(), async (shell, session, overlay) =>
        {
            shell.Settings.EnableSoundEffects = true;
            var pushToTalk = new FakePushToTalk();
            using var coordinator = new LinuxInteractionCoordinator(
                new FakeShortcuts(), pushToTalk, new FakeTextInjection(), session, new InlineDispatcher());

            // Home's "Start recording" (MainWindow.OnStartRecording) and the tray's Start call this.
            await coordinator.StartRecordingAsync();
            Check(session.IsActive, "Home's start did not start the recording");
            Check(watcher.IsArmed, "the app's own start chime disarmed the mic-silence watcher");
            Check(chime.Plays == 1, $"the start chime played {chime.Plays} times, not once");
            Check(overlay.MicSilence.Count == 0, "the chime's own blocks changed the warning");
            clock.Advance(TimeSpan.FromSeconds(1.68));
            watcher.Tick();
            Check(overlay.MicSilence.Count == 1 && overlay.MicSilence[0]?.Value == session.ActiveMicrophoneName,
                $"a silent mic behind the start chime did not warn at 2.0 s: [{Describe(overlay.MicSilence)}]");
            // The mic really hearing the user, after the chime, still hides it.
            watcher.ObserveBlockPeak(-30);
            Check(overlay.MicSilence.Count == 2 && overlay.MicSilence[1] is null, "sound after the chime did not hide the warning");
            await coordinator.StopRecordingAsync();

            // Push-to-talk reaches the same start, and the same chime.
            Check(coordinator.ConfigureAndStart(LinuxInteractionConfiguration.Default with
            {
                PushToTalk = new PushToTalkConfiguration(PushToTalkMode.Modifier, ModifierSide.LeftAlt)
            }).IsSuccess, "push-to-talk did not configure");
            pushToTalk.RaisePressed();
            for (var wait = 0; wait < 500 && !(session.IsActive && chime.Plays == 3); wait++) await Task.Delay(10);
            Check(session.IsActive && watcher.IsArmed && chime.Plays == 3, $"push-to-talk did not start a watched recording ({chime.Plays} chimes)");
            clock.Advance(TimeSpan.FromSeconds(2));
            watcher.Tick();
            Check(overlay.MicSilence.Count == 3 && overlay.MicSilence[2] is not null,
                $"push-to-talk's silent recording did not warn: [{Describe(overlay.MicSilence)}]");
            pushToTalk.RaiseReleased();
            for (var wait = 0; wait < 500 && session.IsRecordingOrFinishing; wait++) await Task.Delay(10);
            Check(overlay.MicSilence.Count == 4 && overlay.MicSilence[3] is null, "releasing push-to-talk did not hide the warning");
        }, watcher, chime);
    }

    /// <summary>The start chime as an idle loopback/virtual input hears it: loud blocks while it plays.</summary>
    private sealed class LoopbackChime(MicSilenceWatcher watcher, ManualClock clock) : ISoundEffectsService
    {
        private int _plays;
        public int Plays => Volatile.Read(ref _plays);
        public PlatformResult Play(SoundEffect effect)
        {
            Interlocked.Increment(ref _plays);
            if (effect != SoundEffect.RecordingStarted) return PlatformResult.Success();
            for (var block = 0; block < 16; block++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(20));
                watcher.ObserveBlockPeak(-20);
            }
            watcher.Tick();
            return PlatformResult.Success();
        }
        public void Dispose() { }
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
