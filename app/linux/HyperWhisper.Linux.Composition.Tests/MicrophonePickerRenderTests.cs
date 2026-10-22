using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HyperWhisper.Linux;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.PortableApplication.ViewModels;

// Spec: mic picker in Settings (2026-10-19). The Settings → Sound microphone row in the real
// MainWindow under the real App. With no sound server (CI) the assertions still hold: the saved
// device is one that never exists, so the page must show it "(not connected)" with the amber
// fallback line, an empty grey level bar and an idle Test button, and picking Default must clear
// the setting and reach the Home box.
//
// Optional live phase, for a box with a sound server the probe can reach (PULSE_SERVER):
//   HW_MIC_PROBE_DEVICE       a connected input's name, saved as the choice
//   HW_MIC_PROBE_UNPLUG       a shell command that removes that input
//   HW_MIC_PROBE_REPLUG       a shell command that brings it back
//   HW_MIC_PROBE_SCREENSHOT   a PNG path, written while the device is unplugged
// It proves the device watch moves both pickers with no "Refresh devices", and drives Test.
static class MicrophonePickerRenderTests
{
    public const string ProbeArgument = "--probe-microphone-picker";
    private const string NeverConnected = "HW Probe Microphone That Is Never Connected";

    public static Task RunAsync() =>
        XvfbProbe.RunAsync(ProbeArgument, "the Settings → Sound microphone picker probe", "en", "1440x900");

    public static int RunProbe()
    {
        try
        {
            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
            var services = new LinuxDesktopServices();
            var settingsPath = Path.Combine(services.Paths.ConfigDirectory, "settings.json");
            Directory.CreateDirectory(services.Paths.ConfigDirectory);
            // Through the private-file service: it refuses to read a settings file other users can
            // read, and a refused file is a fresh profile, which would hide the saved device.
            var seeded = services.PrivateFiles.WriteAllTextAtomically(settingsPath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["selectedModeId"] = "00000000-0000-0000-0000-000000000001",
                ["onboarding.skipped"] = true,
                ["onboarding.completed"] = true,
                [MicrophonePickerViewModel.SettingKey] = NeverConnected,
            }));
            if (seeded.IsFailure) throw new InvalidOperationException($"could not seed settings.json: {seeded.Error!.Code}");

            var window = new MainWindow(services) { Width = 1100, Height = 860 };
            var failures = new List<string>();
            var exit = 2;
            using var done = new CancellationTokenSource(TimeSpan.FromSeconds(55));
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    window.Show();
                    await Initialized(window);
                    var shell = (ApplicationShellViewModel)window.DataContext!;
                    await Run(window, shell, settingsPath, failures);
                    if (Environment.GetEnvironmentVariable("HW_MIC_PROBE_DEVICE") is { Length: > 0 } device)
                        await RunLive(window, shell, settingsPath, device, failures);
                    exit = failures.Count == 0 ? 0 : 1;
                }
                catch (Exception exception) { Console.Error.WriteLine(exception); exit = 2; }
                finally { done.Cancel(); }
            });
            Dispatcher.UIThread.MainLoop(done.Token);
            foreach (var failure in failures) Console.Error.WriteLine($"FAIL {failure}");
            return exit;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 2; }
    }

    private static async Task Run(MainWindow window, ApplicationShellViewModel shell, string settingsPath, List<string> failures)
    {
        var microphone = shell.Microphone ?? throw new InvalidOperationException("the shell has no microphone picker");
        shell.Navigate("sound");
        await Settle();

        var picker = Find<ComboBox>(window, "SettingsMicrophonePicker");
        var shown = (picker.SelectionBoxItem as MicrophoneChoice)?.Label ?? "<blank>";
        Console.WriteLine($"Settings picker on open: {shown}; rows: {string.Join(" | ", microphone.Choices.Select(choice => choice.Label))}");
        Expect(shown == $"{NeverConnected} (not connected)", $"the Settings picker draws {shown}, expected the saved device (not connected)", failures);
        Expect(microphone.Choices.FirstOrDefault()?.Kind == MicrophoneChoiceKind.SystemDefault
            && microphone.Choices[0].Label.StartsWith("Default", StringComparison.Ordinal),
            "the first row is not Default", failures);
        var fallback = Find<TextBlock>(window, "SettingsMicrophoneFallback");
        Console.WriteLine($"fallback line: visible {fallback.IsEffectivelyVisible}, \"{fallback.Text}\"");
        Expect(fallback.IsEffectivelyVisible && fallback.Text?.EndsWith($"until {NeverConnected} is reconnected.", StringComparison.Ordinal) == true,
            "the amber fallback line is not shown for an unplugged saved device", failures);

        // Visiting the page does not open the microphone.
        var level = Find<MicrophoneLevelBar>(window, "SettingsMicrophoneLevel");
        var test = Find<Button>(window, "SettingsMicrophoneTest");
        Console.WriteLine($"idle: testing {microphone.LevelTest.IsTesting}, level {level.Level}, button \"{test.Content}\"");
        Expect(!microphone.LevelTest.IsTesting, "visiting Sound started the level test", failures);
        Expect(MicrophoneLevelBar.LitSegments(level.Level) == 0, "the idle level bar has lit segments", failures);
        Expect(test.Content as string == "Test", $"the idle button reads {test.Content}, expected Test", failures);

        // Leaving and reopening the page must not let the torn-down binding write the choice away.
        shell.Navigate("home");
        await Settle();
        Expect(SavedName(settingsPath) == NeverConnected, "leaving Sound changed the saved device", failures);
        shell.Navigate("sound");
        await Settle();

        // Picking Default clears the setting, and Home shows the same row.
        picker = Find<ComboBox>(window, "SettingsMicrophonePicker");
        picker.SelectedItem = microphone.Choices[0];
        await Settle();
        Console.WriteLine($"after picking Default: saved {SavedName(settingsPath) ?? "<absent>"}, rows: {microphone.Choices.Count}");
        Expect(SavedName(settingsPath) is null, "picking Default left audio.inputDeviceName in settings.json", failures);
        Expect(!Find<TextBlock>(window, "SettingsMicrophoneFallback", visibleOnly: false).IsEffectivelyVisible,
            "the fallback line stayed after picking Default", failures);
        Expect(microphone.Choices.All(choice => choice.Kind != MicrophoneChoiceKind.Disconnected),
            "the (not connected) row stayed after picking Default", failures);
        shell.Navigate("home");
        await Settle();
        if (shell.Recording?.HasAudioDevices == true)
        {
            var home = Find<ComboBox>(window, "HomeAudioInputPicker");
            var homeShown = (home.SelectionBoxItem as MicrophoneChoice)?.Label ?? "<blank>";
            Console.WriteLine($"Home box: {homeShown}");
            Expect(home.SelectionBoxItem is MicrophoneChoice { Kind: MicrophoneChoiceKind.SystemDefault },
                $"the Home box draws {homeShown}, expected the Default row", failures);
        }
        else Console.WriteLine("Home box: hidden, no input devices on this box");
    }

    private static async Task RunLive(MainWindow window, ApplicationShellViewModel shell, string settingsPath, string device, List<string> failures)
    {
        var microphone = shell.Microphone!;
        var recording = shell.Recording!;
        var connected = recording.AudioDevices.FirstOrDefault(candidate => candidate.Name == device);
        if (connected is null) { failures.Add($"live: {device} is not among {string.Join(", ", recording.AudioDevices.Select(d => d.Name))}"); return; }

        // Pick it on Home; Settings must show it.
        var home = Find<ComboBox>(window, "HomeAudioInputPicker");
        home.SelectedItem = microphone.Choices.First(choice => choice.DeviceName == device);
        await Settle();
        Expect(SavedName(settingsPath) == device, $"live: picking {device} on Home did not save it", failures);
        shell.Navigate("sound");
        await Settle();
        var picker = Find<ComboBox>(window, "SettingsMicrophonePicker");
        Console.WriteLine($"live: Settings shows {(picker.SelectionBoxItem as MicrophoneChoice)?.Label}; recording opens {recording.SelectedAudioDevice?.Id}");
        Expect((picker.SelectionBoxItem as MicrophoneChoice)?.DeviceName == device, "live: Settings does not show the Home pick", failures);
        Expect(recording.SelectedAudioDevice?.Id == connected.Id, "live: the recording device is not the pick", failures);

        // Test opens the device; leaving the page stops it.
        Find<Button>(window, "SettingsMicrophoneTest").Command!.Execute(null);
        await Settle();
        Console.WriteLine($"live: after Test: testing {microphone.LevelTest.IsTesting}, button \"{microphone.LevelTest.ButtonText}\", error \"{microphone.LevelTest.ErrorMessage}\"");
        Expect(microphone.LevelTest.IsTesting && microphone.LevelTest.ButtonText == "Stop", "live: Test did not start", failures);
        shell.Navigate("home");
        await Settle();
        Expect(!microphone.LevelTest.IsTesting, "live: leaving Sound did not stop the test", failures);
        shell.Navigate("sound");
        await Settle();

        // Unplug: no Refresh click, the watcher alone.
        Shell(Environment.GetEnvironmentVariable("HW_MIC_PROBE_UNPLUG")!);
        await WaitFor(() => microphone.IsSavedDeviceMissing, TimeSpan.FromSeconds(10));
        await Settle();
        picker = Find<ComboBox>(window, "SettingsMicrophonePicker");
        var fallback = Find<TextBlock>(window, "SettingsMicrophoneFallback", visibleOnly: false);
        Console.WriteLine($"live: unplugged: Settings shows {(picker.SelectionBoxItem as MicrophoneChoice)?.Label}; \"{fallback.Text}\"; recording opens {recording.SelectedAudioDevice?.Id}");
        Expect((picker.SelectionBoxItem as MicrophoneChoice)?.Label == $"{device} (not connected)", "live: unplug did not show (not connected)", failures);
        Expect(fallback.IsEffectivelyVisible, "live: unplug did not show the fallback line", failures);
        Expect(recording.SelectedAudioDevice is { IsDefault: true }, "live: unplug did not fall back to the OS default", failures);
        Expect(SavedName(settingsPath) == device, "live: unplug forgot the choice", failures);
        if (Environment.GetEnvironmentVariable("HW_MIC_PROBE_SCREENSHOT") is { Length: > 0 } png)
        {
            var size = new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
            using var bitmap = new RenderTargetBitmap(size);
            bitmap.Render(window);
            bitmap.Save(png, new PngBitmapEncoderOptions());
            Console.WriteLine($"live: screenshot written ({size.Width}x{size.Height})");
        }

        // Replug: the next recording uses it again.
        Shell(Environment.GetEnvironmentVariable("HW_MIC_PROBE_REPLUG")!);
        await WaitFor(() => !microphone.IsSavedDeviceMissing, TimeSpan.FromSeconds(10));
        await Settle();
        picker = Find<ComboBox>(window, "SettingsMicrophonePicker");
        Console.WriteLine($"live: replugged: Settings shows {(picker.SelectionBoxItem as MicrophoneChoice)?.Label}; recording opens {recording.SelectedAudioDevice?.Name}");
        Expect((picker.SelectionBoxItem as MicrophoneChoice)?.DeviceName == device && recording.SelectedAudioDevice?.Name == device,
            "live: replug did not return to the saved device", failures);
    }

    private static string? SavedName(string settingsPath)
    {
        using var saved = JsonDocument.Parse(File.ReadAllText(settingsPath));
        return saved.RootElement.TryGetProperty(MicrophonePickerViewModel.SettingKey, out var value) ? value.GetString() : null;
    }

    private static void Shell(string command)
    {
        using var process = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } })!;
        process.WaitForExit(10_000);
    }

    private static async Task WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(100);
    }

    private static void Expect(bool condition, string failure, List<string> failures)
    {
        if (!condition) failures.Add(failure);
    }

    private static T Find<T>(Window window, string name, bool visibleOnly = true) where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name && (!visibleOnly || control.IsEffectivelyVisible))
        ?? throw new InvalidOperationException($"the window shows no {name}");

    private static Task Initialized(MainWindow window) =>
        (Task)typeof(MainWindow).GetMethod("EnsureInitializedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null)!;

    private static async Task Settle()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(900);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
