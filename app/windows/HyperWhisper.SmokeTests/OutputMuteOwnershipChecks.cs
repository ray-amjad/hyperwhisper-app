// OUTPUT MUTE OWNERSHIP (issue #2243)
//
// Mute Audio mutes the default output while recording and must put back only
// what HyperWhisper itself changed: never unmute a device the user had muted,
// never touch a device the user unmuted by hand mid-recording, and never leave
// the output muted after a stop, a cancel or an exit. The deleted
// verify_media_control_audio_cleanup.ps1 asserted this with a source regex.
// These checks drive the real AudioEnvironmentService start/stop/cancel/exit
// methods over a fake output device (the IOutputMuteEndpoints seam), because a
// CI runner may have no audio endpoint.

using HyperWhisper.Services;

namespace HyperWhisper.SmokeTests;

internal static class OutputMuteOwnershipChecks
{
    public static void RunAll(Action<string, Action> run)
    {
        run("media control: Mute Audio unmutes the output after a stop (#2243)", () =>
        {
            var output = new FakeOutputDevices("speakers", muted: false);
            var service = NewService(output);

            var state = StartRecording(service);
            Assert(output["speakers"].Mute, "the output is muted while recording");
            Assert(state?.MutedByHyperWhisper == true, "the recording owns the mute it made");

            service.ScheduleRestoreAfterRecording(state);
            Assert(!output["speakers"].Mute, "a stop leaves the output muted");
        });

        run("media control: a cancel and an exit unmute the output too (#2243)", () =>
        {
            var output = new FakeOutputDevices("speakers", muted: false);
            var service = NewService(output);

            service.ScheduleRestoreAfterRecording(StartRecording(service), immediate: true);
            Assert(!output["speakers"].Mute, "a cancel leaves the output muted");

            service.RestoreAfterRecordingImmediatelyAsync(StartRecording(service)).Wait();
            Assert(!output["speakers"].Mute, "an exit mid-recording leaves the output muted");
        });

        run("media control: Mute Audio never unmutes an output the user had muted (#2243)", () =>
        {
            var output = new FakeOutputDevices("speakers", muted: true);
            var service = NewService(output);

            var state = StartRecording(service);
            Assert(state?.MutedByHyperWhisper != true, "a mute the user made is claimed by HyperWhisper");
            service.ScheduleRestoreAfterRecording(state);
            service.ScheduleRestoreAfterRecording(StartRecording(service), immediate: true);
            service.RestoreAfterRecordingImmediatelyAsync(StartRecording(service)).Wait();

            Assert(output["speakers"].Mute, "HyperWhisper unmuted an output the user had muted");
            Assert(output["speakers"].Writes == 0,
                $"HyperWhisper wrote the mute of an output it never muted ({output["speakers"].Writes} writes)");
        });

        run("media control: a hand unmute mid-recording is left alone (#2243)", () =>
        {
            var output = new FakeOutputDevices("speakers", muted: false);
            var service = NewService(output);

            var state = StartRecording(service);
            output["speakers"].UserSets(false);
            var writesBeforeStop = output["speakers"].Writes;
            service.ScheduleRestoreAfterRecording(state);

            Assert(!output["speakers"].Mute, "the output is muted again after the user unmuted it");
            Assert(output["speakers"].Writes == writesBeforeStop,
                "the stop wrote the mute of an output the user had already unmuted");
        });

        run("media control: the restore goes to the device it muted, not the new default (#2243)", () =>
        {
            var output = new FakeOutputDevices("speakers", muted: false);
            var service = NewService(output);

            var state = StartRecording(service);
            output.Add("headphones", muted: true);
            output.DefaultId = "headphones";
            service.ScheduleRestoreAfterRecording(state);

            Assert(!output["speakers"].Mute, "the device HyperWhisper muted stays muted after a default switch");
            Assert(output["headphones"].Mute && output["headphones"].Writes == 0,
                "the restore unmuted the new default device, which the user had muted");
        });
    }

    private static AudioEnvironmentService.AudioEnvironmentState? StartRecording(AudioEnvironmentService service) =>
        service.PrepareForRecording(service.ClaimRestoreOwnershipForRecording());

    private static AudioEnvironmentService NewService(FakeOutputDevices output) =>
        new(output, () => AudioEnvironmentService.MuteAudioMode);

    private sealed class FakeOutputDevices(string defaultId, bool muted) : IOutputMuteEndpoints
    {
        private readonly Dictionary<string, FakeDevice> _devices = new() { [defaultId] = new FakeDevice(defaultId, muted) };

        public string DefaultId { get; set; } = defaultId;

        public FakeDevice this[string id] => _devices[id];

        public void Add(string id, bool muted) => _devices[id] = new FakeDevice(id, muted);

        public IOutputMuteEndpoint GetDefault() => _devices[DefaultId];

        public IOutputMuteEndpoint GetOrDefault(string deviceId) =>
            _devices.TryGetValue(deviceId, out var device) ? device : GetDefault();
    }

    private sealed class FakeDevice(string id, bool muted) : IOutputMuteEndpoint
    {
        private bool _mute = muted;

        public string Id => id;
        public string Name => $"Fake {id}";

        /// <summary>Mute writes made through the seam, which only HyperWhisper uses.</summary>
        public int Writes { get; private set; }

        public bool Mute
        {
            get => _mute;
            set
            {
                Writes++;
                _mute = value;
            }
        }

        /// <summary>The user flips the mute in the Windows volume flyout, outside HyperWhisper.</summary>
        public void UserSets(bool mute) => _mute = mute;

        public void Dispose() { }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
