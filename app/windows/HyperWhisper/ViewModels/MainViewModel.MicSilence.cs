// LIVE "MIC HEARS NOTHING" WARNING (#2411)
// While a dictation records (batch or streaming), the overlay warns
// "No sound from {mic}" once 2.0 s of capture have passed with no buffer
// peaking at -60 dBFS or more. The first heard buffer hides it for the rest of
// the recording, so pauses in speech never bring it back. The rule itself is
// the shared HyperWhisper.PortableApplication.Audio.MicSilenceWatcher.

using HyperWhisper.Localization;
using HyperWhisper.PortableApplication.Audio;
using HyperWhisper.Services;

namespace HyperWhisper.ViewModels;

public partial class MainViewModel
{
    private readonly MicSilenceWatcher _micSilenceWatcher = new();

    // The device the recording opened, snapshotted when capture started: a
    // picker change mid-recording does not move the capture, so it must not
    // move the name either.
    private string? _micSilenceDeviceName;

    /// <summary>
    /// The warning text to show alongside the live recording face, or null to
    /// hide it. Raised on a capture or timer thread: subscribers POST to the UI
    /// thread (BeginInvoke) and never block on it - the UI thread starts and
    /// stops the watcher, which publishes too.
    /// </summary>
    public event EventHandler<string?>? MicSilenceWarningChanged;

    /// <summary>"No sound from {mic}", with a generic name when the device has none.</summary>
    internal static string MicSilenceWarningText(string? deviceName) =>
        Loc.S("recording.warning.micSilent",
            string.IsNullOrWhiteSpace(deviceName) ? Loc.S("menu.microphone") : deviceName.Trim());

    private void AttachMicSilenceWatcher()
    {
        _recorderService.BlockPeakDbfs += _micSilenceWatcher.ObserveBlockPeak;
        _micSilenceWatcher.WarningChanged += OnMicSilenceWarningChanged;
    }

    private void DetachMicSilenceWatcher()
    {
        _recorderService.BlockPeakDbfs -= _micSilenceWatcher.ObserveBlockPeak;
        _micSilenceWatcher.WarningChanged -= OnMicSilenceWarningChanged;
        _micSilenceWatcher.Dispose();
    }

    /// <summary>
    /// Follows <see cref="IsRecording"/>: both start paths set it only once the
    /// capture device is running (streaming: after the connection opened and
    /// capture started), and every stop, cancel and failure path clears it.
    /// </summary>
    private void UpdateMicSilenceWatcher(bool recording)
    {
        if (recording)
        {
            _micSilenceDeviceName = SelectedAudioDevice?.Name;
            _micSilenceWatcher.Start();
        }
        else
        {
            _micSilenceWatcher.Stop();
        }
    }

    private void OnMicSilenceWarningChanged(bool warning)
    {
        if (warning)
            LoggingService.Warn("MainViewModel: No sound from the microphone after 2 s of recording; showing the warning");

        MicSilenceWarningChanged?.Invoke(this, warning ? MicSilenceWarningText(_micSilenceDeviceName) : null);
    }
}
