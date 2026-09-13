using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.ViewModels.Base;

namespace HyperWhisper.ViewModels.Onboarding;

public sealed partial class OnboardingFlowViewModel : ViewModelBase
{
    // =========================================================================
    // SEAM EVENTS
    // =========================================================================

    private void OnDownloadErrorsChanged(object? sender, OnboardingDownloadErrors errors)
    {
        _downloadErrors = errors;
        RefreshSetupError();
    }

    private void OnDownloadActivity(object? sender, EventArgs e)
    {
        // The catalog's download state is read through plain method calls, so this
        // tick is the only thing that tells the binding layer to re-read it.
        OnPropertyChanged(nameof(SelectedModelProgress));
        OnPropertyChanged(nameof(IsSelectedModelDownloading));
        OnPropertyChanged(nameof(IsSelectedModelInstalled));
        RaiseGateChanged();
    }

    private void OnShortcutChanged(object? sender, EventArgs e) => ApplyShortcutState();

    private void OnCreditsChanged(object? sender, EventArgs e) => ApplyCredits();

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        // Availability is step-independent: the Done step's summary reads it too.
        DeviceAvailability = _audio.Availability;

        // The list itself only belongs to the microphone step, exactly as on macOS.
        if (Step == OnboardingStep.Microphone)
            ApplyDeviceList(_audio.Devices);

        // Reconcile the meter with reality, in BOTH directions. Unplugging the
        // microphone mid-step used to leave the flag true and the bars frozen at
        // their last heights; plugging one in left them dead under a prompt that
        // had already gone back to asking for speech. After the list, so a
        // recovery meters the device the step is now showing.
        SyncLevelMeter();
    }

    private void OnIsRecordingChanged(object? sender, EventArgs e) => IsRecording = _audio.IsRecording;

    private void OnTranscriptChanged(object? sender, EventArgs e) => Transcript = _audio.Transcript;

    private void OnTranscriptWarningChanged(object? sender, EventArgs e) =>
        TranscriptWarning = _audio.TranscriptWarning;

    private void OnInputLevelChanged(object? sender, float level) => InputLevel = level;

}
