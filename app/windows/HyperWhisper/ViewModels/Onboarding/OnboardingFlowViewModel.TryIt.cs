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
    // TRY IT STEP
    // =========================================================================

    private bool _isRecording;

    public bool IsRecording
    {
        get => _isRecording;
        private set => SetProperty(ref _isRecording, value);
    }

    private string _transcript = string.Empty;

    public string Transcript
    {
        get => _transcript;
        private set
        {
            if (!SetProperty(ref _transcript, value))
                return;

            OnPropertyChanged(nameof(TranscriptIsError));
            OnPropertyChanged(nameof(TranscriptBody));
            OnPropertyChanged(nameof(HasTranscript));
        }
    }

    public bool HasTranscript => Transcript.Length > 0;

    /// <summary>
    /// Recording failures arrive through the same channel as transcripts with an
    /// "Error:" sentinel, so the view can render them differently.
    /// </summary>
    public bool TranscriptIsError => Transcript.StartsWith("Error:", StringComparison.Ordinal);

    public string TranscriptBody =>
        TranscriptIsError ? Transcript["Error:".Length..].Trim() : Transcript;

    private OnboardingTryItMode _tryItMode = OnboardingTryItMode.Record;

    /// <summary>
    /// Which primary control the step offers. On a machine with no capture device the
    /// Record button would only ever produce an error, so the bundled sample clip
    /// takes its place.
    /// </summary>
    public OnboardingTryItMode TryItMode
    {
        get => _tryItMode;
        private set => SetProperty(ref _tryItMode, value);
    }

    private bool _transcriptCameFromSample;

    /// <summary>So the result line can say truthfully which of the two happened.</summary>
    public bool TranscriptCameFromSample
    {
        get => _transcriptCameFromSample;
        private set => SetProperty(ref _transcriptCameFromSample, value);
    }

    private bool _isTranscribingSample;

    public bool IsTranscribingSample
    {
        get => _isTranscribingSample;
        private set => SetProperty(ref _isTranscribingSample, value);
    }

    private bool _isTranscribingTestRecording;

    /// <summary>
    /// The microphone path's equivalent of <see cref="IsTranscribingSample"/>. Set
    /// the moment Stop is pressed and cleared when the transcript lands, so the
    /// step can say "transcribing" rather than showing "Nothing here yet" beside a
    /// live Record button for the whole of a local model's run.
    /// </summary>
    public bool IsTranscribingTestRecording
    {
        get => _isTranscribingTestRecording;
        private set => SetProperty(ref _isTranscribingTestRecording, value);
    }

    private string? _transcriptWarning;

    /// <summary>
    /// A non-fatal warning about the current transcript, or null. Post-processing
    /// that was SKIPPED (a 401, a timeout) still returns text, and the one seeded
    /// Mode post-processes through a cloud LLM, so without this the user
    /// reads a raw transcript under full success chrome and concludes the source
    /// works. The GUI's toast handler deliberately drops the Onboarding call site,
    /// because a toast behind a modal cannot be seen.
    /// </summary>
    public string? TranscriptWarning
    {
        get => _transcriptWarning;
        private set
        {
            if (SetProperty(ref _transcriptWarning, value))
                OnPropertyChanged(nameof(HasTranscriptWarning));
        }
    }

    public bool HasTranscriptWarning => !string.IsNullOrEmpty(TranscriptWarning);

    public bool HasSampleClip => _audio.HasSampleClip;

    public void BeginTryItStep()
    {
        if (!_isLive)
            return;

        TryItMode = DeviceAvailability != OnboardingDeviceAvailability.Available && _audio.HasSampleClip
            ? OnboardingTryItMode.Sample
            : OnboardingTryItMode.Record;
        TranscriptCameFromSample = false;
        _audio.ClearTranscript();
    }

    public void EndTryItStep()
    {
        // Cancel BOTH owned transcriptions before tearing the recorder down, so a
        // running orchestrator call is not left billing against a disposed gateway.
        //
        // The sample clip is a SEPARATE task-box key from the microphone
        // recording, and cancelling only the microphone one let a sample
        // transcription started here survive Back: it kept running with no
        // chrome, and because walking forward into Try It again resets
        // TranscriptCameFromSample to false, its result then rendered as the
        // user's own recording, complete with the device name and the "recorded"
        // pill. The defer and complete paths were already safe because Finish()
        // calls CancelAll(); only Back leaked.
        _taskBox.Cancel(OnboardingTaskKeys.TestRecording);
        _taskBox.Cancel(OnboardingTaskKeys.SampleClip);
        _audio.StopRecordingForExit();
        _audio.ClearTranscript();
        IsTranscribingSample = false;
        IsTranscribingTestRecording = false;
    }

    /// <summary>
    /// Start the capture, or stop it and transcribe.
    ///
    /// The stop half runs under the SAME task box as the sample-clip path. The
    /// first cut let the gateway fire it as a discarded task with
    /// CancellationToken.None, which cost three things at once: the step showed
    /// "Nothing here yet" beside a live Record button for the whole of a local
    /// model's run, a second press started an overlapping capture into the same
    /// transcript channel, and "Set Up Later" disposed the gateway and the recorder
    /// out from under a running, billable orchestrator call.
    /// </summary>
    [RelayCommand]
    public void ToggleTestRecording()
    {
        // Re-entrancy: no second capture while the last one is still transcribing.
        if (!_isLive || IsTranscribingTestRecording || IsTranscribingSample)
            return;

        TranscriptCameFromSample = false;

        if (!IsRecording)
        {
            _audio.StartTestRecording();
            return;
        }

        IsTranscribingTestRecording = true;
        RunTracked(OnboardingTaskKeys.TestRecording, StopAndTranscribeCoreAsync);
    }

    private async Task StopAndTranscribeCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _audio.StopAndTranscribeAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // The gateway publishes its own failures on the transcript channel with
            // the "Error:" sentinel. A throw that escapes it must still leave the
            // step usable rather than tearing the flow down.
        }

        if (cancellationToken.IsCancellationRequested || !_isLive)
            return;

        IsTranscribingTestRecording = false;
        _taskBox.Clear(OnboardingTaskKeys.TestRecording);
    }

    /// <summary>
    /// Run the bundled clip through the configured source. It exercises model load,
    /// provider routing, post-processing and the transcript render; only capture is
    /// different.
    /// </summary>
    [RelayCommand]
    public void TranscribeSampleClip()
    {
        if (!_isLive || !_audio.HasSampleClip || IsTranscribingSample || IsTranscribingTestRecording)
            return;

        IsTranscribingSample = true;
        TranscriptCameFromSample = true;
        RunTracked(OnboardingTaskKeys.SampleClip, TranscribeSampleClipCoreAsync);
    }

    private async Task TranscribeSampleClipCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _audio.TranscribeSampleClipAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // The gateway publishes its own failures on the transcript channel with
            // the "Error:" sentinel. A throw that escapes it must still leave the
            // step usable rather than tearing the flow down.
        }

        if (cancellationToken.IsCancellationRequested || !_isLive)
            return;

        IsTranscribingSample = false;
        _taskBox.Clear(OnboardingTaskKeys.SampleClip);
    }

}
