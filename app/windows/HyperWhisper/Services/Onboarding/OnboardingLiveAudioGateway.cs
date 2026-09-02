// ONBOARDING LIVE AUDIO GATEWAY
//
// The audio seam's live adapter: device listing and availability, the idle level
// preview, the "give it a try" recording, and the bundled sample clip.
//
// It is its own file because Windows has to BUILD three things macOS gets for
// free, and each of them lives here:
//
//  1. AN IDLE LEVEL METER. On Windows the level is only computed inside
//     AudioRecorderService.OnDataAvailable while a real recording runs, and
//     MicrophoneKeepWarmService deliberately discards its buffers. The preview
//     below reuses the SAME RMS -> dB -> envelope maths on a short-lived
//     WaveInEvent, and suspends keep-warm around it so the two do not contend
//     for the device.
//
//  2. A FOUR-CASE AVAILABILITY. Windows really does have zero capture devices
//     (the dev box is in exactly that state) and really can fail to enumerate,
//     and AudioDeviceService already distinguishes an empty Result.Success from a
//     Result.Failure. Collapsing the two would tell a user with a broken audio
//     stack to go buy a microphone.
//
//  3. A SAMPLE-CLIP FALLBACK, so the Try It step is honest and completable on a
//     machine with no microphone instead of offering a Record button whose only
//     outcome is an error.
//
// DELIVERY. This adapter owns record and stop directly, so the onboarding
// transcript never touches a delivery sink at all. TextDeliveryGate is the
// belt-and-braces backstop for the global hotkey path, which stays live while the
// window is open.
//
// DEVICE IDENTITY. macOS keys devices on a string id. Windows keys capture on an
// int DeviceNumber, which shifts when a device is plugged in, and persists a
// device NAME. So the seam's string id is the device NAME, and "" is the System
// Default row, which resolves to DeviceNumber -1: the sentinel
// AudioRecorderService.ResolveCaptureDevice already understands.

using System.IO;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;
using HyperWhisper.ViewModels.Onboarding;
using NAudio.Wave;

namespace HyperWhisper.Services.Onboarding;

public sealed class LiveOnboardingAudioGateway : IOnboardingAudioGateway, IDisposable
{
    private readonly AudioDeviceService _devices;
    private readonly AudioRecorderService _recorder;
    private readonly SettingsService _settings;
    private readonly ModeService _modes;
    private readonly MicrophoneKeepWarmService _keepWarm;
    private readonly VocabularyService _vocabulary;

    /// <summary>
    /// The two on-device catalogs, so the Try It step can LOAD the engine the
    /// user just chose before asking the orchestrator to use it. See
    /// <see cref="EnsureLocalEngineReadyAsync"/>.
    /// </summary>
    private readonly WhisperModelService _whisperModels;
    private readonly ParakeetModelService _parakeetModels;

    /// <summary>
    /// Reads and writes whichever device the RUNNING APP has open.
    ///
    /// SettingsService.LastSelectedMicrophone is documented as "(future)" and is
    /// written by nothing and read by nothing in this head - the live selection is
    /// MainViewModel.SelectedAudioDevice, which is in-memory. So the preference
    /// and the open device are genuinely two different stores here, exactly as the
    /// seam's StoredDeviceId / SelectedDeviceId split assumes; this pair is the
    /// second one. OnboardingLiveDependencies supplies the MainViewModel-backed
    /// implementation; without one the adapter keeps its own field, which is what
    /// the smoke harness gets.
    /// </summary>
    private readonly Func<string?> _readOpenDevice;
    private readonly Action<string?> _writeOpenDevice;
    private string? _localOpenDevice;

    private IReadOnlyList<OnboardingInputDevice> _deviceList = Array.Empty<OnboardingInputDevice>();
    private OnboardingDeviceAvailability _availability = OnboardingDeviceAvailability.NoDevices;

    private WaveInEvent? _preview;
    private float _previewLevel;
    private readonly object _previewLock = new();

    /// <summary>
    /// The NAudio device index the preview suspended keep-warm on, so stopping can
    /// resume it on the SAME device. -1 is the system-default sentinel, not "none".
    /// </summary>
    private int _previewDeviceNumber = -1;

    private string _transcript = string.Empty;
    private string? _transcriptWarning;
    private bool _isRecording;
    private bool _disposed;

    /// <summary>
    /// The orchestrator's post-processing warning handler, held so it can be
    /// detached on Dispose. TranscriptionRuntime.Orchestrator is a process-lifetime
    /// singleton, so a subscription that outlived this gateway would keep the whole
    /// onboarding graph alive.
    /// </summary>
    private readonly EventHandler<ErrorToastEventArgs> _warningHandler;

    public LiveOnboardingAudioGateway(
        AudioDeviceService devices,
        AudioRecorderService recorder,
        SettingsService settings,
        ModeService modes,
        MicrophoneKeepWarmService keepWarm,
        VocabularyService vocabulary,
        WhisperModelService whisperModels,
        ParakeetModelService parakeetModels,
        Func<string?>? readOpenDevice = null,
        Action<string?>? writeOpenDevice = null)
    {
        _devices = devices;
        _recorder = recorder;
        _settings = settings;
        _modes = modes;
        _keepWarm = keepWarm;
        _vocabulary = vocabulary;
        _whisperModels = whisperModels;
        _parakeetModels = parakeetModels;

        _readOpenDevice = readOpenDevice ?? (() => _localOpenDevice);
        _writeOpenDevice = writeOpenDevice ?? (value => _localOpenDevice = value);

        _devices.DevicesChanged += OnHardwareDevicesChanged;

        // MainViewModel deliberately drops this event for the Onboarding call site
        // (a toast behind a modal is unreachable). Onboarding is therefore the only
        // thing that can surface it, and the CallSite tag makes the filter exact: a
        // concurrent Local API or GUI transcription is tagged differently and is
        // never attributed to the Try It panel.
        _warningHandler = (_, args) =>
        {
            if (args is OrchestratorPostProcessingWarningEventArgs tagged
                && tagged.CallSite == TranscriptionCallSite.Onboarding)
            {
                PublishWarning(tagged.Message);
            }
        };
        TranscriptionRuntime.Orchestrator.PostProcessingWarning += _warningHandler;

        RefreshDevices();
    }

    // =========================================================================
    // DEVICES AND AVAILABILITY
    // =========================================================================

    public IReadOnlyList<OnboardingInputDevice> Devices => _deviceList;

    public OnboardingDeviceAvailability Availability => _availability;

    public event EventHandler? DevicesChanged;

    public string? SelectedDeviceId
    {
        get
        {
            var open = _readOpenDevice();
            return string.IsNullOrEmpty(open) ? null : open;
        }
    }

    public string? StoredDeviceId
    {
        get
        {
            var stored = _settings.LastSelectedMicrophone;
            return string.IsNullOrEmpty(stored) ? null : stored;
        }
    }

    /// <summary>
    /// Re-enumerate and recompute. Raises DevicesChanged when EITHER the list or
    /// the availability moved, so plugging a microphone in while the step is open
    /// recovers live even though the availability alone changed first.
    /// </summary>
    public void RefreshDevices()
    {
        var previousAvailability = _availability;
        var previousList = _deviceList;

        var (list, availability) = Enumerate();
        _deviceList = list;
        _availability = availability;

        if (availability != previousAvailability || !SameDevices(previousList, list))
        {
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Re-read consent only. Availability can flip on that alone.</summary>
    public void RefreshMicrophoneAuthorization() => RefreshDevices();

    /// <summary>
    /// The ordering here is the whole point: a privacy block is the more
    /// actionable diagnosis, so it wins over an empty list, and an enumeration
    /// FAILURE is a broken audio stack rather than an absent microphone.
    /// </summary>
    private (IReadOnlyList<OnboardingInputDevice>, OnboardingDeviceAvailability) Enumerate()
    {
        // Consent first. With the toggle off, enumeration still SUCCEEDS and still
        // reports devices - the block only shows up at open time as WASAPI
        // E_ACCESSDENIED - so a list-first check would report Available for a
        // microphone that cannot be opened.
        if (MicrophonePrivacyService.IsBlocked())
        {
            return (Array.Empty<OnboardingInputDevice>(), OnboardingDeviceAvailability.Blocked);
        }

        var result = _devices.GetAvailableDevices();
        if (result.IsFailure)
        {
            return (Array.Empty<OnboardingInputDevice>(), OnboardingDeviceAvailability.EnumerationFailed);
        }

        var devices = result.Value ?? new List<AudioDeviceService.AudioDevice>();
        if (devices.Count == 0)
        {
            return (Array.Empty<OnboardingInputDevice>(), OnboardingDeviceAvailability.NoDevices);
        }

        var mapped = devices
            .Select(d => new OnboardingInputDevice(d.Name, d.Name))
            .ToList();

        return (mapped, OnboardingDeviceAvailability.Available);
    }

    private static bool SameDevices(
        IReadOnlyList<OnboardingInputDevice> left,
        IReadOnlyList<OnboardingInputDevice> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i]) return false;
        }
        return true;
    }

    private void OnHardwareDevicesChanged(object? sender, EventArgs e)
    {
        // AudioDeviceService debounces this to 250 ms and raises it from a
        // System.Timers.Timer, sourced from the MMDevice COM notification
        // client — so it arrives on a threadpool thread.
        //
        // THE ADAPTER MARSHALS. RefreshDevices raises DevicesChanged, which the
        // flow turns into writes of DeviceAvailability, IsLevelMeterActive and
        // the whole DeviceOptions list, racing the UI thread's own
        // BeginMicrophoneStep and SelectDevice over the same fields. An earlier
        // comment here asserted that the flow marshalled; it never did, and the
        // flow is deliberately Dispatcher-free so the smoke suite can drive it
        // with no WPF Application at all.
        OnboardingUiDispatch.Post(RefreshDevices);
    }

    public void SelectDevice(string? id)
    {
        // Never write a device preference on a machine that has none, so
        // "Set Up Later" has nothing to undo. The flow guards this too; the
        // adapter guards it again because it is the thing that owns the write.
        if (_availability != OnboardingDeviceAvailability.Available) return;

        var name = string.IsNullOrEmpty(id) ? null : id;

        _settings.LastSelectedMicrophone = name;
        _writeOpenDevice(name);

        // Follow the new device with the meter if one is already running.
        lock (_previewLock)
        {
            if (_preview != null)
            {
                StopPreviewLocked();
                StartPreviewLocked(ResolveDeviceNumber(name));
            }
        }
    }

    /// <summary>
    /// Put BOTH writes back. Deliberately not routed through
    /// <see cref="SelectDevice"/>: that would drop a preference naming a device
    /// that is not currently connected and silently reset the user to the system
    /// default, turning a deferral into a change.
    /// </summary>
    public void RestoreDevice(string? storedId, string? openId)
    {
        _settings.LastSelectedMicrophone = string.IsNullOrEmpty(storedId) ? null : storedId;
        _writeOpenDevice(string.IsNullOrEmpty(openId) ? null : openId);
    }

    /// <summary>
    /// Name -> NAudio WaveIn index. -1 is NAudio's "default device" sentinel and
    /// is what an unknown or absent name resolves to, so a preference naming a
    /// disconnected microphone degrades to the system default rather than throwing.
    /// </summary>
    internal int ResolveDeviceNumber(string? name)
    {
        if (string.IsNullOrEmpty(name)) return -1;

        var result = _devices.GetAvailableDevices();
        if (result.IsFailure || result.Value is null) return -1;

        var match = result.Value.FirstOrDefault(d => d.Name == name);
        return match?.DeviceNumber ?? -1;
    }

    // =========================================================================
    // IDLE LEVEL PREVIEW
    // =========================================================================

    public float InputLevel => _previewLevel;

    public event EventHandler<float>? InputLevelChanged;

    /// <summary>
    /// No-op unless a device is genuinely usable. On the blocked path an open
    /// would throw E_ACCESSDENIED; on the empty path there is nothing to open.
    /// </summary>
    /// <returns>
    /// True only when a capture stream is actually running afterwards. A device can
    /// enumerate and still refuse to open (exclusive-mode hold, consent flipped
    /// between the read and the open, driver fault), and the caller has to be able
    /// to tell a live meter from a frozen one.
    /// </returns>
    public bool StartInputLevelPreview()
    {
        if (_availability != OnboardingDeviceAvailability.Available) return false;
        if (_isRecording) return false;

        lock (_previewLock)
        {
            if (_preview != null) return true;
            return StartPreviewLocked(ResolveDeviceNumber(SelectedDeviceId));
        }
    }

    public void StopInputLevelPreview()
    {
        lock (_previewLock)
        {
            StopPreviewLocked();
        }

        PublishLevel(0f);
    }

    private bool StartPreviewLocked(int deviceNumber)
    {
        if (_disposed) return false;

        try
        {
            // The keep-warm stream holds the same endpoint open. Suspend it or the
            // two contend for the device.
            _keepWarm.SuspendForRecording();

            var preview = new WaveInEvent
            {
                DeviceNumber = deviceNumber,
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 100
            };
            preview.DataAvailable += OnPreviewData;
            preview.StartRecording();
            _preview = preview;
            _previewDeviceNumber = deviceNumber;
            LoggingService.Debug($"LiveOnboardingAudioGateway: level preview started on device #{deviceNumber}");
            return true;
        }
        catch (Exception ex)
        {
            // A device that enumerates but will not open (privacy flipped between
            // the read and the open, exclusive-mode hold, driver fault) must leave
            // the meter explicitly inactive, not a dead flat bar that reads as a
            // broken app. Reported through the return value, because a caught
            // exception the caller never hears about is exactly what produced the
            // frozen meter under a live "speak to see the level" hint.
            LoggingService.Warn($"LiveOnboardingAudioGateway: level preview unavailable: {ex.Message}");
            _preview = null;
            ResumeKeepWarmLocked(deviceNumber);
            return false;
        }
    }

    private void StopPreviewLocked()
    {
        if (_preview is null) return;

        var preview = _preview;
        var deviceNumber = _previewDeviceNumber;
        _preview = null;
        _previewDeviceNumber = -1;

        try
        {
            preview.DataAvailable -= OnPreviewData;
            preview.StopRecording();
            preview.Dispose();
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"LiveOnboardingAudioGateway: level preview stop failed: {ex.Message}");
        }

        ResumeKeepWarmLocked(deviceNumber);
    }

    /// <summary>
    /// Hand the endpoint back to MicrophoneKeepWarmService.
    ///
    /// The device number MATTERS. Configure(enabled, null) takes its
    /// !deviceNumber.HasValue branch and calls StopLocked, so resuming with null
    /// does not resume at all: it tears the app's warm capture stream down and
    /// leaves it down, and because MarkOnboardingCompleted is a no-op for a
    /// returning user, nothing ever re-Configures it. The very first dictation
    /// after setup then pays the full cold WASAPI activation the service exists to
    /// eliminate.
    ///
    /// -1 is passed THROUGH rather than mapped to null. It is NAudio's "system
    /// default device" sentinel, which WaveInEvent and therefore
    /// MicrophoneKeepWarmService.StartLocked both understand; null means "there is
    /// no device", which is a different thing and the only case that should stop
    /// the stream.
    /// </summary>
    private void ResumeKeepWarmLocked(int deviceNumber) =>
        _keepWarm.ResumeAfterRecording(deviceNumber);

    /// <summary>
    /// The identical RMS -> dB -> envelope maths AudioRecorderService.OnDataAvailable
    /// runs, so the onboarding meter and the recording meter move the same way for
    /// the same speech.
    /// </summary>
    private void OnPreviewData(object? sender, WaveInEventArgs e)
    {
        double sumSquares = 0;
        var sampleCount = 0;
        for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
        {
            short sample = BitConverter.ToInt16(e.Buffer, i);
            var norm = sample / 32768.0;
            sumSquares += norm * norm;
            sampleCount++;
        }

        var normalized = 0f;
        if (sampleCount > 0)
        {
            var rms = Math.Sqrt(sumSquares / sampleCount);
            var db = 20.0 * Math.Log10(Math.Max(rms, 1e-6));
            normalized = (float)Math.Clamp((db + 60.0) / 54.0, 0.0, 1.0);
        }

        // Fast attack, slow decay.
        var next = normalized > _previewLevel
            ? normalized
            : Math.Max(normalized, _previewLevel * 0.85f);

        PublishLevel(next);
    }

    private void PublishLevel(float level)
    {
        _previewLevel = level;
        InputLevelChanged?.Invoke(this, level);
    }

    // =========================================================================
    // TRY IT
    // =========================================================================

    public bool IsRecording => _isRecording;

    public event EventHandler? IsRecordingChanged;

    public string Transcript => _transcript;

    public event EventHandler? TranscriptChanged;

    public string? TranscriptWarning => _transcriptWarning;

    public event EventHandler? TranscriptWarningChanged;

    public bool StartTestRecording()
    {
        if (_isRecording) return true;

        if (_availability != OnboardingDeviceAvailability.Available)
        {
            PublishTranscript(Error(Loc.S("errors.noMicrophone")));
            return false;
        }

        // The meter and the recorder both open the endpoint; the recorder wins.
        StopInputLevelPreview();

        try
        {
            _recorder.StartRecording(ResolveDeviceNumber(SelectedDeviceId));
            PublishTranscript(string.Empty);
            SetRecording(true);
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Error($"LiveOnboardingAudioGateway: could not start recording: {ex.Message}", ex);
            SetRecording(false);
            PublishTranscript(Error(Loc.S("errors.recordingStartFailed")));
            return false;
        }
    }

    /// <summary>
    /// Privacy backstop on every exit path. Deliberately not gated on IsRecording:
    /// StopRecording is a no-op when idle, and the audio must not survive the
    /// window closing.
    /// </summary>
    public void StopRecordingForExit()
    {
        try
        {
            var result = _recorder.StopRecording();
            if (result.IsSuccess && !string.IsNullOrEmpty(result.Value))
            {
                TryDelete(result.Value);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"LiveOnboardingAudioGateway: stop-for-exit: {ex.Message}");
        }

        SetRecording(false);
        StopInputLevelPreview();
    }

    public void ClearTranscript()
    {
        PublishTranscript(string.Empty);
        PublishWarning(null);
    }

    /// <summary>
    /// Stop capture and transcribe what was captured. The returned Task is the
    /// whole point: the first cut fired this as a discarded task with
    /// CancellationToken.None, so the flow had no transcribing state (the step
    /// showed "Nothing here yet" and a live Record button for the whole of a local
    /// model's 20 s), no re-entrancy guard, and nothing for "Set Up Later" to
    /// cancel: it disposed the gateway and the recorder while an orchestrator call
    /// was still running and still billable.
    /// </summary>
    public async Task StopAndTranscribeAsync(CancellationToken cancellationToken)
    {
        SetRecording(false);

        Result<string> stopped;
        try
        {
            stopped = _recorder.StopRecording();
        }
        catch (Exception ex)
        {
            LoggingService.Error($"LiveOnboardingAudioGateway: stop failed: {ex.Message}", ex);
            PublishTranscript(Error(Loc.S("audio.error.stopRecordingFailed")));
            return;
        }

        if (stopped.IsFailure || string.IsNullOrEmpty(stopped.Value))
        {
            PublishTranscript(Error(stopped.Error ?? Loc.S("audio.error.stopRecordingFailed")));
            return;
        }

        await TranscribeAndPublishAsync(stopped.Value!, deleteWhenDone: true, cancellationToken);
    }

    // =========================================================================
    // SAMPLE CLIP
    // =========================================================================

    public bool HasSampleClip => OnboardingLiveDependencies.SampleClipExists();

    /// <summary>
    /// Runs the bundled clip through the SAME path as a recording: the same
    /// conversion, the same shared orchestrator, the same Mode, the same
    /// transcript channel. It differs from the microphone path in capture only,
    /// which is what makes it a real demonstration of the configured source on a
    /// machine that has no microphone.
    /// </summary>
    public async Task TranscribeSampleClipAsync(CancellationToken cancellationToken)
    {
        var extracted = OnboardingLiveDependencies.ExtractSampleClip();
        if (extracted is null)
        {
            PublishTranscript(Error(Loc.S("onboarding.tryIt.sample.missing")));
            return;
        }

        await TranscribeAndPublishAsync(extracted, deleteWhenDone: true, cancellationToken);
    }

    private async Task TranscribeAndPublishAsync(string audioPath, bool deleteWhenDone, CancellationToken cancellationToken)
    {
        var converted = audioPath;

        // A warning belongs to the transcript it was raised for, so the previous
        // attempt's must not survive into this one.
        PublishWarning(null);

        try
        {
            // .wav is in SupportedExtensions and an already-16 kHz mono file comes
            // straight back, so the bundled clip short-circuits this entirely.
            var conversion = await FileTranscriptionService.ConvertToWhisperFormatAsync(audioPath, cancellationToken);
            if (conversion.IsFailure)
            {
                PublishTranscript(Error(conversion.Error ?? Loc.S("app.unknown.error")));
                return;
            }

            converted = conversion.Value!;

            var mode = _modes.GetSelectedMode();
            if (mode is null)
            {
                // The reason is known here, so name it. "An unknown error occurred"
                // was a stand-in for a string that did not exist in any .resx.
                PublishTranscript(Error(Loc.S("errors.noModeSelected")));
                return;
            }

            var localProvider = LocalProviderFor(mode);

            // LOAD THE ENGINE FIRST. Every other transcription entry point does
            // (MainViewModel.StartRecordingAsync, EnsureLocalProviderReadyForFileAsync,
            // the Local API's /transcribe); this one did not, and the DEFAULT
            // first-run path went straight through it: the Source step
            // pre-selects Parakeet V2, the committer writes LocalEngine and
            // LocalParakeetModel but leaves ModelType null, so MainViewModel's
            // eager load never fires, ParakeetTranscriptionService.IsAvailable is
            // false, and the orchestrator threw ModelNotLoaded — rendered as
            // "Error: Local transcription model not loaded" on the very step that
            // is supposed to prove the product works.
            if (localProvider is not null)
            {
                var ready = await EnsureLocalEngineReadyAsync(mode, localProvider, cancellationToken);
                if (!ready)
                {
                    return;
                }
            }

            // The PROCESS-WIDE orchestrator and local provider, never a private
            // one: the GUI, the Local API server and this window must observe the
            // same loaded model.
            var result = await TranscriptionRuntime.Orchestrator.TranscribeAsync(
                converted,
                mode,
                // Same vocabulary budget the main recording flow uses. Empty on a
                // genuine first run; a returning user re-running setup gets their
                // own terms, which is what makes this a real demonstration.
                vocabulary: _vocabulary.GetVocabularyWords(100),
                localTranscriptionProvider: localProvider,
                cancellationToken: cancellationToken,
                callSite: TranscriptionCallSite.Onboarding);

            // NOT after the flow gave up on this run. A provider that returns
            // normally on a cancelled token (several do: they finish the request
            // they already sent) would otherwise publish into a step the user has
            // walked away from — and walking forward into Try It again resets the
            // "this came from the sample clip" flag, so a stale sample result
            // renders as the user's own recording.
            if (cancellationToken.IsCancellationRequested)
            {
                LoggingService.Debug("LiveOnboardingAudioGateway: dropping a transcript that landed after cancellation");
                return;
            }

            PublishTranscript(result.FinalText);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LoggingService.Error($"LiveOnboardingAudioGateway: transcription failed: {ex.Message}", ex);
            PublishTranscript(Error(ex.Message));
        }
        finally
        {
            if (!string.Equals(converted, audioPath, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(converted);
            }

            if (deleteWhenDone)
            {
                TryDelete(audioPath);
            }
        }
    }

    /// <summary>Mirrors MainViewModel.GetLocalProvider: cloud Modes take null.</summary>
    private static ITranscriptionProvider? LocalProviderFor(Mode mode)
    {
        if (mode.ProviderType == "cloud") return null;

        return mode.LocalEngine == "parakeet"
            ? TranscriptionRuntime.ParakeetProvider
            : TranscriptionRuntime.LocalProvider;
    }

    /// <summary>
    /// Bring the shared local engine up for <paramref name="mode"/>, publishing
    /// the failure on the transcript channel and answering false when it cannot.
    ///
    /// This is a FOURTH readiness check, and deliberately not a shared one. The
    /// three that already exist each speak a different vocabulary — the Local
    /// API's is wire error codes and English hints aimed at an MCP client,
    /// MainViewModel's two are toast strings with a settings deep link, and this
    /// one is an inline sentence inside a modal — and the three also differ in
    /// what they do around the load (the GUI unloads Whisper under 32 GB of RAM
    /// before a Parakeet spawn, the API does not). Unifying them is a real
    /// cleanup and is NOT this change: it would put /transcribe at risk to fix a
    /// defect that is entirely inside onboarding.
    ///
    /// Both branches are no-ops when the engine is already warm, which is the
    /// common case for a returning user re-running setup.
    /// </summary>
    private async Task<bool> EnsureLocalEngineReadyAsync(
        Mode mode,
        ITranscriptionProvider provider,
        CancellationToken cancellationToken)
    {
        try
        {
            if (provider is ParakeetTranscriptionService parakeet)
            {
                var modelId = string.IsNullOrWhiteSpace(mode.LocalParakeetModel)
                    ? mode.Model
                    : mode.LocalParakeetModel;

                var info = ParakeetModelInfo.AllModels.FirstOrDefault(m => m.Id == modelId);
                if (info is null || !_parakeetModels.IsModelDownloaded(info))
                {
                    PublishTranscript(Error(Loc.S("errors.modelNotDownloaded", modelId ?? "")));
                    return false;
                }

                if (!parakeet.NeedsReload(info.Id, mode.Language))
                {
                    return true;
                }

                LoggingService.Info($"LiveOnboardingAudioGateway: loading Parakeet-family model {info.DisplayName} for the Try It step");
                await parakeet.InitializeAsync(
                    _parakeetModels.GetModelDirectory(info),
                    mode.Language == "auto" ? null : mode.Language);
                return true;
            }

            if (provider is TranscriptionService whisper)
            {
                var modelType = string.IsNullOrWhiteSpace(mode.ModelType) ? mode.Model : mode.ModelType;

                var info = WhisperModelInfo.AllModels.FirstOrDefault(m => m.Type == modelType);
                if (info is null || !_whisperModels.IsModelDownloaded(info))
                {
                    PublishTranscript(Error(Loc.S("errors.modelNotDownloaded", modelType ?? "")));
                    return false;
                }

                var modelPath = _whisperModels.GetModelPath(info);
                if (whisper.IsInitialized
                    && string.Equals(whisper.LoadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                LoggingService.Info($"LiveOnboardingAudioGateway: loading Whisper model {info.DisplayName} for the Try It step");
                await whisper.InitializeAsync(modelPath, null, cancellationToken);
                return true;
            }

            // An unrecognised provider is not a reason to refuse: let the
            // orchestrator decide, exactly as it did before this check existed.
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LoggingService.Error($"LiveOnboardingAudioGateway: local model load failed: {ex.Message}", ex);
            PublishTranscript(Error(Loc.S("errors.modelLoadFailed")));
            return false;
        }
    }

    // =========================================================================
    // PLUMBING
    // =========================================================================

    /// <summary>
    /// The "Error:" sentinel the seam documents, so the view can render a failure
    /// differently from a transcript without a second channel.
    /// </summary>
    private static string Error(string message) => $"Error: {message}";

    private void PublishTranscript(string value)
    {
        if (_transcript == value) return;
        _transcript = value;
        TranscriptChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PublishWarning(string? value)
    {
        if (_transcriptWarning == value) return;
        _transcriptWarning = value;
        TranscriptWarningChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetRecording(bool value)
    {
        if (_isRecording == value) return;
        _isRecording = value;
        IsRecordingChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"LiveOnboardingAudioGateway: could not delete '{path}': {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _devices.DevicesChanged -= OnHardwareDevicesChanged;
        TranscriptionRuntime.Orchestrator.PostProcessingWarning -= _warningHandler;

        StopRecordingForExit();
        StopInputLevelPreview();
    }
}
