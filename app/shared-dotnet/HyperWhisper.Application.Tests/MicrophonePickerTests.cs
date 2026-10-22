using System.Text;
using System.Text.Json.Nodes;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.PortableApplication.ViewModels;

// Spec "Microphone picker in Settings" (2026-10-19), the Linux head's half, driven through the
// shared view models end to end: one saved choice by device NAME, restored at launch, a fallback
// to the OS default while it is unplugged, its return picked up without a restart, Home and
// Settings showing the same choice, and the Test button's state machine on a fake capture and a
// manual clock. No view is involved: both pickers bind MicrophonePickerViewModel.
static class MicrophonePickerTests
{
    private static readonly AudioInputDevice BuiltIn = new("alsa_input.pci-0000_00_1f.3.analog-stereo", "Built-in Audio Analog Stereo", IsDefault: true);
    private static readonly AudioInputDevice Scarlett = new("alsa_input.usb-Focusrite_Scarlett_2i2_USB-00.analog-stereo", "Scarlett 2i2 USB");
    private static readonly AudioInputDevice Webcam = new("alsa_input.usb-046d_C920-02.analog-stereo", "HD Pro Webcam C920");

    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        await SavedChoiceSurvivesARelaunchAsync(Path.Combine(root, "relaunch"));
        await DefaultSavesNothingAndFollowsTheOsDefaultAsync(Path.Combine(root, "default"));
        await UnplugFallsBackAndReplugReturnsAsync(Path.Combine(root, "unplug"));
        await HomeAndSettingsShareOneChoiceAsync(Path.Combine(root, "shell"));
        await PickerIgnoresNullAndKeepsSelectionAcrossRefillsAsync(Path.Combine(root, "refill"));
        await SameNamedDevicesKeepTheExactPickAsync(Path.Combine(root, "same-name"));
        await LevelTestStateMachineAsync(Path.Combine(root, "level-test"));
        await BackupDoesNotCarryTheDeviceAsync(Path.Combine(root, "backup"));
        LevelTestSurvivesASwappedUiContext();
        Console.WriteLine("Microphone picker tests passed.");
    }

    private static async Task SavedChoiceSurvivesARelaunchAsync(string root)
    {
        var settingsPath = Path.Combine(root, "settings.json");
        await using (var app = await Harness.CreateAsync(root, [BuiltIn, Scarlett]))
        {
            Check(app.Picker.SelectedChoice?.Kind == MicrophoneChoiceKind.SystemDefault, "a fresh profile does not start on Default");
            Check(app.Recording.SelectedAudioDevice == BuiltIn, "a fresh profile does not record from the OS default");
            app.Picker.SelectedChoice = app.Picker.Choices.Single(choice => choice.DeviceId == Scarlett.Id);
            Check(app.Recording.SelectedAudioDevice == Scarlett, "picking the Scarlett did not move the recording device");
            Check(await app.RecordOnceAsync() == Scarlett.Id, "the next recording did not open the picked device");
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!;
            Check(saved[MicrophonePickerViewModel.SettingKey]?.GetValue<string>() == Scarlett.Name,
                $"the choice was not saved by device name: {saved.ToJsonString()}");
        }

        // A relaunch: a new settings service on the same file, new workflow, new view models.
        await using (var relaunched = await Harness.CreateAsync(root, [BuiltIn, Scarlett]))
        {
            Check(relaunched.Picker.SelectedChoice?.DeviceId == Scarlett.Id, "the saved choice was not restored after a relaunch");
            Check(relaunched.Recording.SelectedAudioDevice == Scarlett, "the relaunched workflow does not record from the saved device");
            Check(await relaunched.RecordOnceAsync() == Scarlett.Id, "the first recording after a relaunch did not use the saved device");
        }
    }

    private static async Task DefaultSavesNothingAndFollowsTheOsDefaultAsync(string root)
    {
        await using var app = await Harness.CreateAsync(root, [BuiltIn, Scarlett]);
        Check(app.Picker.Choices[0].Label == $"Default · {BuiltIn.Name}", $"the first row reads '{app.Picker.Choices[0].Label}'");
        Check(app.Picker.Choices.Skip(1).Select(choice => choice.Label).SequenceEqual([BuiltIn.Name, Scarlett.Name]),
            "the rows after Default are not the input devices in order");
        app.Picker.SelectedChoice = app.Picker.Choices.Single(choice => choice.DeviceId == Scarlett.Id);
        app.Picker.SelectedChoice = app.Picker.Choices[0];
        Check(app.Picker.SavedDeviceName is null, "picking Default kept a saved device");
        var file = await File.ReadAllTextAsync(Path.Combine(root, "settings.json"));
        Check(!file.Contains(MicrophonePickerViewModel.SettingKey, StringComparison.Ordinal), $"picking Default wrote the key: {file}");
        Check(app.Recording.SelectedAudioDevice == BuiltIn, "Default does not record from the OS default");

        // The OS default moves (the user switched it in the desktop's sound settings): Default follows.
        app.Devices.Available = [BuiltIn with { IsDefault = false }, Scarlett with { IsDefault = true }];
        app.Workflow.RefreshDevices();
        Check(app.Picker.Choices[0].Label == $"Default · {Scarlett.Name}", $"the Default row did not follow the OS: '{app.Picker.Choices[0].Label}'");
        Check(app.Picker.SelectedChoice?.Kind == MicrophoneChoiceKind.SystemDefault, "the Default pick was lost when the OS default moved");
        Check(await app.RecordOnceAsync() == Scarlett.Id, "following Default did not record from the new OS default");
    }

    private static async Task UnplugFallsBackAndReplugReturnsAsync(string root)
    {
        await using var app = await Harness.CreateAsync(root, [BuiltIn, Scarlett]);
        app.Picker.SelectedChoice = app.Picker.Choices.Single(choice => choice.DeviceId == Scarlett.Id);

        // Unplug. The watcher's refresh is what the workflow sees.
        app.Devices.Available = [BuiltIn];
        app.Workflow.RefreshDevices();
        var selected = app.Picker.SelectedChoice;
        Check(selected?.Kind == MicrophoneChoiceKind.Disconnected && selected.Label == "Scarlett 2i2 USB (not connected)",
            $"an unplugged saved device reads '{selected?.Label}'");
        Check(app.Picker.Choices.Contains(selected!), "the (not connected) row is not in the list the picker shows");
        Check(app.Picker.IsSavedDeviceMissing
            && app.Picker.FallbackNotice == $"Using {BuiltIn.Name} until Scarlett 2i2 USB is reconnected.",
            $"the amber line reads '{app.Picker.FallbackNotice}'");
        Check(app.Picker.SavedDeviceName == Scarlett.Name, "the choice was dropped while the device was unplugged");
        Check(await app.RecordOnceAsync() == BuiltIn.Id, "a recording while unplugged did not fall back to the OS default");

        // Picking the (not connected) row keeps the choice; it is not a pick of something else.
        app.Picker.SelectedChoice = selected;
        Check(app.Picker.SavedDeviceName == Scarlett.Name, "re-picking the (not connected) row changed the choice");

        // Replug, with a different pulse id (another USB port): the NAME is what matches.
        var replugged = Scarlett with { Id = "alsa_input.usb-Focusrite_Scarlett_2i2_USB-01.analog-stereo" };
        app.Devices.Available = [BuiltIn, replugged];
        app.Workflow.RefreshDevices();
        Check(app.Picker.SelectedChoice?.DeviceId == replugged.Id, "the replugged device was not picked again");
        Check(!app.Picker.IsSavedDeviceMissing && app.Picker.FallbackNotice is null, "the amber line outlived the replug");
        Check(app.Picker.Choices.All(choice => choice.Kind != MicrophoneChoiceKind.Disconnected), "the (not connected) row outlived the replug");
        Check(await app.RecordOnceAsync() == replugged.Id, "the next recording after the replug did not use the saved device");
    }

    private static async Task HomeAndSettingsShareOneChoiceAsync(string root)
    {
        await using var app = await Harness.CreateAsync(root, [BuiltIn, Scarlett, Webcam], withShell: true);
        var shell = app.Shell!;
        Check(ReferenceEquals(shell.Sound.Microphone, shell.Microphone) && shell.Microphone is not null,
            "Settings → Sound and Home do not share one picker view model");
        var picker = shell.Microphone!;
        var notified = 0;
        picker.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(MicrophonePickerViewModel.SelectedChoice)) notified++; };

        // Home's box writes the shared SelectedChoice; Settings' box reads it (and vice versa).
        picker.SelectedChoice = picker.Choices.Single(choice => choice.DeviceId == Webcam.Id);
        Check(notified > 0 && shell.Sound.Microphone!.SelectedChoice?.DeviceId == Webcam.Id, "a Home pick did not reach the Settings picker");

        // The tray and onboarding pick through ChooseDevice.
        picker.ChooseDevice(Scarlett);
        Check(picker.SelectedChoice?.DeviceId == Scarlett.Id && picker.SavedDeviceName == Scarlett.Name, "a tray/onboarding pick did not become the saved choice");
        picker.ChooseSystemDefault();
        Check(picker.SelectedChoice?.Kind == MicrophoneChoiceKind.SystemDefault && picker.SavedDeviceName is null, "the tray's Default did not clear the choice");

        // A writer that bypasses the picker cannot fork the choice: the saved one wins back.
        picker.ChooseDevice(Webcam);
        shell.Recording!.SelectedAudioDevice = BuiltIn;
        Check(shell.Recording.SelectedAudioDevice == Webcam && picker.SelectedChoice?.DeviceId == Webcam.Id,
            "a direct SelectedAudioDevice write forked the saved choice");
    }

    private static async Task PickerIgnoresNullAndKeepsSelectionAcrossRefillsAsync(string root)
    {
        await using var app = await Harness.CreateAsync(root, [BuiltIn, Scarlett]);
        app.Picker.SelectedChoice = app.Picker.Choices.Single(choice => choice.DeviceId == Scarlett.Id);
        // A torn-down TwoWay binding writes null; a refilling list writes null mid-refill.
        app.Picker.Choices.CollectionChanged += (_, _) => app.Picker.SelectedChoice = null;
        app.Picker.SelectedChoice = null;
        Check(app.Picker.SavedDeviceName == Scarlett.Name && app.Picker.SelectedChoice?.DeviceId == Scarlett.Id, "a null write cleared the choice");
        var published = new List<MicrophoneChoice?>();
        app.Picker.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MicrophonePickerViewModel.SelectedChoice)) published.Add(app.Picker.SelectedChoice);
        };
        app.Devices.Available = [Webcam, BuiltIn, Scarlett];
        app.Workflow.RefreshDevices();
        Check(app.Picker.SelectedChoice?.DeviceId == Scarlett.Id, "a refill lost the selection");
        Check(published.Count > 0 && published[^1]?.DeviceId == Scarlett.Id,
            "a refill did not re-publish the selection for a picker that dropped it");
        var before = published.Count;
        app.Workflow.RefreshDevices();
        Check(published.Count == before, "an unchanged device list re-published the selection");

        // A ComboBox catching up on a refill writes back a row the list no longer holds: the
        // "Default" row from before the OS default was known. That is not a pick of Default.
        var staleDefault = new MicrophoneChoice(MicrophoneChoiceKind.SystemDefault, "Default", null, null);
        Check(!app.Picker.Choices.Contains(staleDefault), "the stale Default row is still listed");
        app.Picker.SelectedChoice = staleDefault;
        Check(app.Picker.SavedDeviceName == Scarlett.Name && app.Picker.SelectedChoice?.DeviceId == Scarlett.Id,
            "writing back a row the list no longer holds cleared the saved choice");
        app.Picker.SelectedChoice = app.Picker.Choices[0];
        Check(app.Picker.SavedDeviceName is null, "picking the listed Default row did not clear the choice");
    }

    // Review r1: with two microphones of the same name, picking the second snapped back to the
    // first, so it could never be chosen. The NAME is still what is saved, but the exact device
    // picked stays selected and records for the session.
    private static async Task SameNamedDevicesKeepTheExactPickAsync(string root)
    {
        var left = new AudioInputDevice("alsa_input.usb-Generic_USB_Mic-00.mono-fallback", "USB Microphone");
        var right = new AudioInputDevice("alsa_input.usb-Generic_USB_Mic-01.mono-fallback", "USB Microphone");
        await using var app = await Harness.CreateAsync(root, [BuiltIn, left, right]);

        app.Picker.SelectedChoice = app.Picker.Choices.Single(choice => choice.DeviceId == right.Id);
        Check(app.Picker.SelectedChoice?.DeviceId == right.Id, $"picking the second same-named device snapped back to '{app.Picker.SelectedChoice?.DeviceId}'");
        Check(app.Recording.SelectedAudioDevice == right, "the second same-named device is not the recording device");
        Check(app.Picker.SavedDeviceName == right.Name, "the choice was not saved by name");
        app.Workflow.RefreshDevices();
        Check(app.Picker.SelectedChoice?.DeviceId == right.Id, "a refresh moved the pick to the first same-named device");
        Check(await app.RecordOnceAsync() == right.Id, "the recording did not open the exact device picked");

        // The tray and onboarding pick a device object: that exact one too, either way round.
        app.Picker.ChooseDevice(left);
        Check(app.Picker.SelectedChoice?.DeviceId == left.Id && app.Recording.SelectedAudioDevice == left, "ChooseDevice did not select the first same-named device");
        app.Picker.ChooseDevice(right);
        Check(app.Picker.SelectedChoice?.DeviceId == right.Id && app.Recording.SelectedAudioDevice == right, "ChooseDevice did not select the second same-named device");

        // With no pick this session (a relaunch), the name resolves to the first.
        Check(MicrophonePickerViewModel.Resolve([BuiltIn, left, right], right.Name) == left, "an unpicked same name did not resolve to the first");
        Check(MicrophonePickerViewModel.Resolve([BuiltIn, left, right], right.Name, null, right.Id) == right, "the current selection did not win among same-named devices");
    }

    private static async Task LevelTestStateMachineAsync(string root)
    {
        await using var app = await Harness.CreateAsync(root, [BuiltIn, Scarlett], withShell: true);
        var shell = app.Shell!;
        var test = shell.Microphone!.LevelTest;
        shell.Navigate("sound");
        Check(app.Captures.Count == 0, "visiting Settings → Sound opened the microphone");
        Check(!test.IsTesting && test.Level == 0 && test.ButtonText == "Test", "the idle test is not empty with a Test button");

        // Test opens the CHOSEN device, runs ~10 s, reads Stop meanwhile.
        shell.Microphone.ChooseDevice(Scarlett);
        test.ToggleCommand.Execute(null);
        var first = app.Captures.Single();
        Check(test.IsTesting && test.ButtonText == "Stop" && first.DeviceId == Scarlett.Id, $"Test opened '{first.DeviceId}'");
        first.RaiseLevel(0.1f);
        Check(test.Level > 0.5 && test.Level < 1, $"a -20 dBFS chunk drew {test.Level:F2} of the bar");
        var lit = test.Level;
        first.RaiseLevel(0f);
        Check(test.Level > 0.5 * lit && test.Level < lit, $"a silent chunk between words blanked the bar ({test.Level:F2} after {lit:F2})");
        app.Time.Advance(TimeSpan.FromSeconds(9.9));
        Check(test.IsTesting && !first.Stopped, "the test stopped before 10 s");
        app.Time.Advance(TimeSpan.FromSeconds(0.2));
        Check(!test.IsTesting && test.Level == 0 && first.Stopped && first.Disposed && test.ButtonText == "Test",
            "the test did not stop and empty the bar after 10 s");
        Check(!test.HasError, "the test's own timeout was reported as an error");
        first.RaiseLevel(0.5f);
        Check(test.Level == 0, "a late level from a stopped capture lit the bar");

        // A second click stops it.
        test.ToggleCommand.Execute(null);
        test.ToggleCommand.Execute(null);
        Check(!test.IsTesting && app.Captures[^1].Stopped, "a second click did not stop the test");

        // Picking another device while testing moves the test to it.
        test.Start();
        shell.Microphone.ChooseDevice(BuiltIn);
        Check(test.IsTesting && app.Captures[^1].DeviceId == BuiltIn.Id && app.Captures[^2].Stopped,
            "a test did not follow the newly chosen device");

        // Leaving the page stops it.
        shell.Navigate("home");
        Check(!test.IsTesting && app.Captures[^1].Stopped, "leaving the Sound page did not stop the test");

        // A recording starting stops it.
        shell.Navigate("sound");
        test.Start();
        await app.Workflow.StartRecordingAsync();
        Check(!test.IsTesting && app.Captures[^1].Stopped, "a recording starting did not stop the test");
        Check(app.Captures.Count(capture => !capture.Stopped) == 0, "a capture was left open");
        test.Start();
        Check(!test.IsTesting, "Test opened the microphone while a recording was running");
        await app.Workflow.CancelAsync();

        // The capture ending by itself (the device was unplugged) ends the test.
        test.Start();
        app.Captures[^1].RaiseStopped(new PlatformError("audio_capture_failed", "gone"));
        Check(!test.IsTesting && test.HasError, "a capture that died left the test running, or said nothing");

        // Review r1: parec on a stale source id exits cleanly (0 bytes, no error). An exit the test
        // did not ask for still says the microphone could not be opened, not nothing.
        test.Start();
        Check(test.IsTesting && !test.HasError, "a new test did not clear the previous error");
        app.Captures[^1].RaiseStopped(null);
        Check(!test.IsTesting && test.HasError, "a capture that ended by itself without an error showed nothing");

        // A capture that cannot start says so and does not claim to be testing.
        app.FailNextStart = true;
        test.Start();
        Check(!test.IsTesting && test.HasError && app.Captures[^1].Disposed, "a failed start claimed to be testing or leaked");

        Check(MicrophoneLevelTestViewModel.ToDisplayLevel(0) == 0 && MicrophoneLevelTestViewModel.ToDisplayLevel(1) == 1
            && MicrophoneLevelTestViewModel.ToDisplayLevel(0.0005f) == 0, "the -60..0 dBFS mapping is off at its ends");
    }

    private static async Task BackupDoesNotCarryTheDeviceAsync(string root)
    {
        await using var app = await Harness.CreateAsync(root, [BuiltIn, Scarlett]);
        app.Picker.ChooseDevice(Scarlett);
        var backup = await new ApplicationBackupService(app.Database, app.Settings).ExportAsync();
        Check(!backup.Contains(Scarlett.Name, StringComparison.Ordinal) && !backup.Contains(MicrophonePickerViewModel.SettingKey, StringComparison.Ordinal),
            "the backup export carries the machine-specific microphone");
    }

    // Found in the real window: Avalonia's UI thread can carry a different SynchronizationContext
    // instance from the one the view model captured, so Stop posted its idle reset, and the reset
    // Start's own leading Stop queued landed after the test began and switched it straight off.
    private static void LevelTestSurvivesASwappedUiContext()
    {
        var previous = SynchronizationContext.Current;
        var captured = new QueueContext();
        var current = new QueueContext();
        try
        {
            SynchronizationContext.SetSynchronizationContext(captured);
            var captures = new List<FakeCapture>();
            using var test = new MicrophoneLevelTestViewModel(
                () => { var capture = new FakeCapture(false); captures.Add(capture); return capture; },
                () => "usb_mic", new ManualTimeProvider());
            SynchronizationContext.SetSynchronizationContext(current);
            test.Start();
            captured.Drain();
            current.Drain();
            Check(test.IsTesting && test.ButtonText == "Stop", "a test started on a swapped UI context turned itself off");
            test.Start();   // a restart, as a device change does
            captured.Drain();
            current.Drain();
            Check(test.IsTesting && captures.Count == 2 && !captures[0].IsCapturing, "a restarted test did not keep running on the new capture");
            test.Stop();
            captured.Drain();
            current.Drain();
            Check(!test.IsTesting && test.Level == 0, "Stop on a swapped UI context did not return to idle");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class QueueContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));
        public void Drain()
        {
            while (_queue.TryDequeue(out var item)) item.Callback(item.State);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Microphone picker: " + message);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required ApplicationDb Database { get; init; }
        public required PortableSettingsService Settings { get; init; }
        public required MutableDevices Devices { get; init; }
        public required RecordingRecorder Recorder { get; init; }
        public required TranscriptionWorkflow Workflow { get; init; }
        public TranscriptionWorkflowViewModel Recording => Shell?.Recording ?? _recording!;
        public MicrophonePickerViewModel Picker => Shell?.Microphone ?? _picker!;
        public ApplicationShellViewModel? Shell => _shell;
        private ApplicationShellViewModel? _shell;
        public ManualTimeProvider Time { get; } = new();
        public List<FakeCapture> Captures { get; } = [];
        public bool FailNextStart { get; set; }
        private TranscriptionWorkflowViewModel? _recording;
        private MicrophonePickerViewModel? _picker;

        private Harness() { }

        public static async Task<Harness> CreateAsync(string root, IReadOnlyList<AudioInputDevice> devices, bool withShell = false)
        {
            Directory.CreateDirectory(root);
            var paths = new Paths(root);
            var database = new ApplicationDb(paths);
            await database.MigrateAsync();
            var settings = new PortableSettingsService(new DiskPrivateFiles(), paths);
            Check(settings.Load().IsSuccess, "the settings file did not load");
            var audio = Path.Combine(root, "take.wav");
            await File.WriteAllBytesAsync(audio, [1, 2, 3]);
            var deviceService = new MutableDevices { Available = devices };
            var recorder = new RecordingRecorder(audio);
            var workflow = new TranscriptionWorkflow(recorder, deviceService, new OkTranscriber(), new HistoryRepository(database, paths));
            var harness = new Harness
            {
                Database = database, Settings = settings, Devices = deviceService, Recorder = recorder, Workflow = workflow,
            };
            IStreamingAudioCapture Factory()
            {
                var capture = new FakeCapture(harness.FailNextStart);
                harness.FailNextStart = false;
                harness.Captures.Add(capture);
                return capture;
            }
            if (withShell)
                harness._shell = new ApplicationShellViewModel(database, settings, workflow, paths: paths,
                    microphoneTestCapture: Factory, timeProvider: harness.Time);
            else
            {
                harness._recording = new TranscriptionWorkflowViewModel(workflow, () => new TranscriptionWorkflowRequest());
                harness._picker = new MicrophonePickerViewModel(harness._recording, settings, Factory, harness.Time);
            }
            // What the shell's first Home refresh does at launch.
            workflow.RefreshDevices();
            return harness;
        }

        public async Task<string?> RecordOnceAsync()
        {
            var started = await Workflow.StartRecordingAsync();
            Check(started.IsSuccess, $"recording did not start: {started.Failure?.Message}");
            var device = Recorder.LastDeviceId;
            await Workflow.CancelAsync();
            return device;
        }

        public ValueTask DisposeAsync()
        {
            if (Shell is not null) Shell.Dispose();
            else { _picker?.Dispose(); _recording?.Dispose(); }
            Workflow.Dispose();

            return ValueTask.CompletedTask;
        }
    }

    internal sealed class Paths(string root) : IAppPaths
    {
        public string DataDirectory => root;
        public string ConfigDirectory => root;
        public string CacheDirectory => root;
        public string StateDirectory => root;
        public string LogsDirectory => root;
        public string ModelsDirectory => root;
        public string RecordingsDirectory => root;
        public string RuntimeDirectory => root;
        public string TemporaryDirectory => root;
    }

    private sealed class DiskPrivateFiles : IPrivateFileService
    {
        public PlatformResult WriteAllBytesAtomically(string path, ReadOnlySpan<byte> contents)
        {
            File.WriteAllBytes(path, contents.ToArray());
            return PlatformResult.Success();
        }
        public PlatformResult WriteAllTextAtomically(string path, string contents)
            => WriteAllBytesAtomically(path, Encoding.UTF8.GetBytes(contents));
        public PlatformResult<string?> ReadAllText(string path)
            => PlatformResult<string?>.Success(File.Exists(path) ? File.ReadAllText(path) : null);
        public PlatformResult<byte[]?> ReadAllBytes(string path)
            => PlatformResult<byte[]?>.Success(File.Exists(path) ? File.ReadAllBytes(path) : null);
        public PlatformResult Delete(string path) { File.Delete(path); return PlatformResult.Success(); }
        public PlatformResult<bool> IsRestrictedToCurrentUser(string path) => PlatformResult<bool>.Success(true);
    }

    internal sealed class MutableDevices : IAudioInputDeviceService
    {
        public event EventHandler? DevicesChanged { add { } remove { } }
        public IReadOnlyList<AudioInputDevice> Available { get; set; } = [];
        public PlatformResult<IReadOnlyList<AudioInputDevice>> GetAvailableDevices()
            => PlatformResult<IReadOnlyList<AudioInputDevice>>.Success(Available);
        public void Dispose() { }
    }

    internal sealed class RecordingRecorder(string outputPath) : IAudioRecorder
    {
        public event EventHandler<float>? AudioLevelChanged { add { } remove { } }
        public bool IsRecording { get; private set; }
        public string? LastDeviceId { get; private set; }
        public TimeSpan Duration => TimeSpan.FromSeconds(1);
        public PlatformResult Start(AudioRecordingOptions options)
        {
            LastDeviceId = options.DeviceId;
            IsRecording = true;
            return PlatformResult.Success();
        }
        public PlatformResult<string> Stop()
        {
            IsRecording = false;
            return PlatformResult<string>.Success(outputPath);
        }
        public void Dispose() { }
    }

    private sealed class OkTranscriber : IRecordedAudioTranscriber
    {
        public TranscriptionBackendCapability Capability { get; } = new(true, "Test Whisper");
        public Task<PortableTranscriptionResult> TranscribeAsync(string audioPath, string? language, CancellationToken cancellationToken = default)
            => Task.FromResult(PortableTranscriptionResult.Success("ok", "Test Whisper"));
    }

    internal sealed class FakeCapture(bool failStart) : IStreamingAudioCapture
    {
        public event EventHandler<ReadOnlyMemory<byte>>? AudioChunkAvailable { add { } remove { } }
        public event EventHandler<float>? AudioLevelChanged;
        public event EventHandler<PlatformError?>? CaptureStopped;
        public string? DeviceId { get; private set; }
        public bool Stopped { get; private set; }
        public bool Disposed { get; private set; }
        public bool IsCapturing { get; private set; }
        public TimeSpan Duration => TimeSpan.Zero;
        public PlatformResult Start(AudioRecordingOptions options)
        {
            DeviceId = options.DeviceId;
            if (failStart) return PlatformResult.Failure("audio_streaming_unavailable", "no parec");
            IsCapturing = true;
            return PlatformResult.Success();
        }
        public void Stop() { Stopped = true; IsCapturing = false; }
        public void RaiseLevel(float level) => AudioLevelChanged?.Invoke(this, level);
        public void RaiseStopped(PlatformError? error) { IsCapturing = false; CaptureStopped?.Invoke(this, error); }
        public void Dispose() { Disposed = true; Stopped = true; }
    }

    internal sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime);
            _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan by)
        {
            _now += by;
            foreach (var timer in _timers.ToArray()) timer.FireIfDue(_now);
        }

        internal sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            private DateTimeOffset? _due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            public void FireIfDue(DateTimeOffset now)
            {
                if (_due is not { } due || due > now) return;
                _due = null;
                callback(state);
            }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                return true;
            }
            public void Dispose() { _due = null; owner._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
