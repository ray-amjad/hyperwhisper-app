using System.Collections.ObjectModel;
using HyperWhisper.FileTranscription;
using System.Windows.Input;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.AudioNormalization;
using HyperWhisper.SharedCore;

namespace HyperWhisper.PortableApplication.ViewModels;

public sealed class TranscriptionWorkflowViewModel : ViewModelBase, IDisposable
{
    private readonly TranscriptionWorkflow _workflow;
    private readonly Func<TranscriptionWorkflowRequest> _requestFactory;
    private readonly DurableAudioImportService? _audioImport;
    private readonly PortableFileTranscriptionPreflight? _filePreflight;
    private readonly SynchronizationContext? _synchronizationContext = SynchronizationContext.Current;
    private AudioInputDevice? _selectedAudioDevice;
    private string _filePath = string.Empty;
    private string _state = "Idle";
    private string _message = "Preparing audio…";
    private string? _errorCode;
    private bool _canStartRecording;
    private bool _canStop;
    private bool _canCancel;
    private bool _canTranscribeFile;
    private bool _isImporting;
    private bool _applyingSnapshot;
    private double _importProgress;
    private CancellationTokenSource? _importCancellation;
    private bool _disposed;

    public TranscriptionWorkflowViewModel(
        TranscriptionWorkflow workflow,
        Func<TranscriptionWorkflowRequest> requestFactory,
        DurableAudioImportService? audioImport = null,
        PortableFileTranscriptionPreflight? filePreflight = null)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _requestFactory = requestFactory ?? throw new ArgumentNullException(nameof(requestFactory));
        _audioImport = audioImport;
        _filePreflight = filePreflight;
        StartCommand = new AsyncCommand(_ => StartAsync(), _ => CanStartRecording);
        StopCommand = new AsyncCommand(_ => StopAsync(), _ => CanStop);
        CancelCommand = new AsyncCommand(_ => CancelAsync(), _ => CanCancel);
        TranscribeFileCommand = new AsyncCommand(_ => TranscribeFileAsync(), _ => CanTranscribeFile);
        RefreshDevicesCommand = new AsyncCommand(_ => { RefreshDevices(); return Task.CompletedTask; });
        _workflow.Changed += OnWorkflowChanged;
        ApplySnapshot(_workflow.Snapshot);
    }

    public ObservableCollection<AudioInputDevice> AudioDevices { get; } = new();

    /// <summary>
    /// Whether the input service offers at least one microphone.
    /// </summary>
    /// <remarks>
    /// Warning: never bind a view to <see cref="AudioDevices"/> through a value converter.
    ///
    /// <see cref="AudioDevices"/> is a get-only property over one collection instance that is
    /// refilled in place, so it never raises <c>PropertyChanged</c>. A converter binding reads it
    /// exactly once, at bind time, when the list is still empty — and never again. A list binding
    /// is fine, because that subscribes to <c>CollectionChanged</c>.
    ///
    /// The Linux head hid its whole "Audio input" row behind such a converter binding. Every
    /// microphone was enumerated and the row stayed hidden for the life of the process, so the app
    /// offered no microphone at all and "Refresh devices" could not recover it (issue #626). This
    /// property is the binding target instead, and <see cref="ApplySnapshot"/> notifies it.
    /// </remarks>
    public bool HasAudioDevices => AudioDevices.Count > 0;

    /// <summary>
    /// Raised once after <see cref="AudioDevices"/> has been refilled, on the UI context.
    /// </summary>
    /// <remarks>
    /// A view that copies the device list — the Linux onboarding step does, because it owns its own
    /// picker — needs one signal per refill. <c>CollectionChanged</c> is not that signal: the refill
    /// clears the collection first, so a copy driven by it sees an empty list and then one partial
    /// list per device.
    /// </remarks>
    public event EventHandler? DevicesChanged;

    /// <summary>
    /// The microphone the next recording will use.
    /// </summary>
    /// <remarks>
    /// Warning: a null written here is discarded while <see cref="AudioDevices"/> is not empty.
    ///
    /// A picker bound two-way to this property writes null when the view that holds it is torn
    /// down — on the Linux head, every time the user leaves the Home page. The user chose nothing,
    /// but the null reached the workflow, so the next recording refused with "No audio input device
    /// is available." until the microphone was picked again by hand. The picker cannot offer "no
    /// microphone" while it has entries, so a null from it is never a choice.
    ///
    /// The picker writes null a second way, and the guard above cannot see it: a refill clears the
    /// collection first, so the list IS empty at that instant. <see cref="ApplySnapshot"/> raises
    /// <c>_applyingSnapshot</c> for exactly that window.
    ///
    /// A genuinely empty device list still clears the selection: <see cref="ApplySnapshot"/>
    /// assigns the backing field, not this property.
    /// </remarks>
    public AudioInputDevice? SelectedAudioDevice
    {
        get => _selectedAudioDevice;
        set
        {
            if (_applyingSnapshot) return;
            if (value is null && AudioDevices.Count > 0) return;
            if (!Set(ref _selectedAudioDevice, value)) return;
            _workflow.SelectDevice(value?.Id);
        }
    }
    public string FilePath { get => _filePath; set => Set(ref _filePath, value); }
    public string State { get => _state; private set => Set(ref _state, value); }
    public string Message { get => _message; private set { if (Set(ref _message, value)) Notify(nameof(ShowErrorCode)); } }
    public string? ErrorCode
    {
        get => _errorCode;
        private set { if (Set(ref _errorCode, value)) { Notify(nameof(HasError)); Notify(nameof(ShowErrorCode)); } }
    }
    public bool HasError => ErrorCode != null;

    /// <summary>
    /// An error CODE is an internal identifier: `workflow.no_audio_device` beside "No audio input
    /// device is available." adds nothing for the user and reads as a crash. Show it only when
    /// there is no human message to show instead, so a failure is never silent. It is always in
    /// the diagnostic log either way.
    /// </summary>
    public bool ShowErrorCode => HasError && string.IsNullOrWhiteSpace(Message);
    public bool CanStartRecording { get => _canStartRecording; private set => Set(ref _canStartRecording, value); }
    public bool CanStop { get => _canStop; private set => Set(ref _canStop, value); }
    public bool CanCancel { get => _canCancel; private set => Set(ref _canCancel, value); }
    public bool CanTranscribeFile { get => _canTranscribeFile; private set => Set(ref _canTranscribeFile, value); }
    public bool IsImporting { get => _isImporting; private set => Set(ref _isImporting, value); }
    public double ImportProgress { get => _importProgress; private set => Set(ref _importProgress, value); }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand TranscribeFileCommand { get; }
    public ICommand RefreshDevicesCommand { get; }
    public event EventHandler? TranscriptionSaved;

    public void RefreshDevices() => _workflow.RefreshDevices();
    public Task StartAsync(CancellationToken cancellationToken = default) => _workflow.StartRecordingAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken = default) => _workflow.StopAndTranscribeAsync(_requestFactory(), cancellationToken);
    public Task CancelAsync()
    {
        if (_importCancellation is { } import)
        {
            import.Cancel();
            return Task.CompletedTask;
        }
        return _workflow.CancelAsync();
    }
    public async Task TranscribeFileAsync(CancellationToken cancellationToken = default)
    {
        if (_importCancellation is not null)
        {
            ReportInputFailure("audio_import.in_progress", "Another audio import is already running.");
            return;
        }
        var request = _requestFactory().Snapshot();
        var path = FilePath;
        var ownsImportedAudio = false;
        if (_audioImport is not null)
        {
            using var import = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _importCancellation = import;
            BeginImport();
            PlatformResult<string>? imported = null;
            var cancelled = false;
            try
            {
                var progress = new Progress<AudioNormalizationProgress>(value =>
                {
                    if (!IsImporting) return;
                    ImportProgress = value.Fraction;
                    Message = value.Phase == "staging"
                        ? $"Preparing audio… {value.Fraction:P0}"
                        : $"Converting audio… {value.Fraction:P0}";
                });
                FileTranscriptionPreflightResult? preflight = null;
                if (_filePreflight is not null)
                {
                    var target = CreateFileTarget(request.SelectedMode);
                    if (target is null)
                    {
                        imported = PlatformResult<string>.Failure(
                            "file_preflight.request_invalid", "Choose a valid transcription mode.");
                    }
                    else
                    {
                        preflight = await _filePreflight.ValidateAsync(path, target, import.Token);
                        if (!preflight.IsSuccess)
                            imported = PlatformResult<string>.Failure(
                                preflight.Failure!.Code, preflight.Failure.Message);
                    }
                }
                if (imported is null)
                {
                    var cloud = string.Equals(
                        request.SelectedMode?.ProviderType, "cloud", StringComparison.OrdinalIgnoreCase);
                    imported = cloud && preflight?.RequiresNormalization != true
                        ? await _audioImport.ImportOriginalAsync(
                            path, preflight?.Constraints?.MaximumBytes ?? long.MaxValue, progress, import.Token)
                        : await _audioImport.ImportAsync(path, progress, import.Token);
                }
                if (imported?.IsSuccess == true && preflight?.RequiresNormalization == true
                    && _filePreflight is not null)
                {
                    var target = CreateFileTarget(request.SelectedMode);
                    var normalizedPath = imported.Value!;
                    var normalized = target is null ? null
                        : await _filePreflight.ValidateAsync(normalizedPath, target, import.Token);
                    if (normalized?.IsSuccess != true || normalized.RequiresNormalization)
                    {
                        try { File.Delete(normalizedPath); }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                        imported = PlatformResult<string>.Failure(
                            normalized?.Failure?.Code ?? "audio_normalization.invalid_output",
                            normalized?.Failure?.Message ?? "The normalized audio file was invalid.");
                    }
                }
            }
            catch (OperationCanceledException) when (import.IsCancellationRequested)
            {
                cancelled = true;
            }
            finally
            {
                if (ReferenceEquals(_importCancellation, import)) _importCancellation = null;
                EndImport();
            }
            if (cancelled)
            {
                ReportInputFailure("audio_import.cancelled", "Audio import cancelled.");
                return;
            }
            if (imported is null) return;
            if (imported.IsFailure) { ReportInputFailure(imported.Error!.Code, imported.Error.Message); return; }
            path = imported.Value!;
            ownsImportedAudio = true;
            FilePath = path;
        }
        _ = ownsImportedAudio
            ? await _workflow.TranscribeOwnedFileAsync(path, request, cancellationToken)
            : await _workflow.TranscribeFileAsync(path, request, cancellationToken);
    }

    private static FileTranscriptionTarget? CreateFileTarget(Mode? mode)
    {
        if (mode is null) return null;
        if (!string.Equals(mode.ProviderType, "cloud", StringComparison.OrdinalIgnoreCase))
        {
            var parakeet = string.Equals(mode.LocalEngine, "parakeet", StringComparison.OrdinalIgnoreCase);
            return new(
                FileTranscriptionRoute.Local,
                parakeet ? mode.LocalParakeetModel ?? mode.Model ?? string.Empty : mode.ModelType ?? mode.Model ?? string.Empty,
                parakeet ? LocalTranscriptionEngine.Parakeet : LocalTranscriptionEngine.Whisper);
        }
        if (!TryMapCloudProvider(mode.CloudProvider, out var provider)) return null;
        return new(
            FileTranscriptionRoute.Cloud,
            mode.CloudTranscriptionModel ?? string.Empty,
            CloudProvider: provider,
            CloudCatalogTier: provider == CloudTranscriptionProvider.HyperWhisperCloud
                ? mode.CloudAccuracyTier : null);
    }

    private static bool TryMapCloudProvider(string? value, out CloudTranscriptionProvider provider)
    {
        provider = value?.Trim().ToLowerInvariant() switch
        {
            "openai" => CloudTranscriptionProvider.OpenAi,
            "groq" => CloudTranscriptionProvider.Groq,
            "elevenlabs" => CloudTranscriptionProvider.ElevenLabs,
            "mistral" => CloudTranscriptionProvider.Mistral,
            "grok" => CloudTranscriptionProvider.Grok,
            "deepgram" => CloudTranscriptionProvider.Deepgram,
            "assemblyai" => CloudTranscriptionProvider.AssemblyAi,
            "soniox" => CloudTranscriptionProvider.Soniox,
            "gemini" => CloudTranscriptionProvider.Gemini,
            "geminitranscribe" or "gemini-transcribe" => CloudTranscriptionProvider.GeminiTranscribe,
            "microsoftazurespeech" or "azure-mai" => CloudTranscriptionProvider.AzureMai,
            "googlespeech" or "google-chirp" => CloudTranscriptionProvider.GoogleChirp,
            "hyperwhisper" => CloudTranscriptionProvider.HyperWhisperCloud,
            "meta" => CloudTranscriptionProvider.Meta,
            _ => default,
        };
        return value?.Trim().ToLowerInvariant() is
            "openai" or "groq" or "elevenlabs" or "mistral" or "grok" or "deepgram"
            or "assemblyai" or "soniox" or "gemini" or "geminitranscribe"
            or "gemini-transcribe" or "microsoftazurespeech" or "azure-mai"
            or "googlespeech" or "google-chirp" or "hyperwhisper" or "meta";
    }

    public void ReportInputFailure(string code, string message)
    {
        ErrorCode = code;
        Message = message;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workflow.Changed -= OnWorkflowChanged;
        _importCancellation?.Cancel();
        _workflow.Dispose();
    }

    private void OnWorkflowChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        var snapshot = _workflow.Snapshot;
        if (_synchronizationContext is not null && SynchronizationContext.Current != _synchronizationContext)
        {
            _synchronizationContext.Post(_ =>
            {
                if (!_disposed) ApplySnapshot(snapshot);
            }, null);
            return;
        }
        ApplySnapshot(snapshot);
    }

    /// <summary>
    /// Brings <see cref="AudioDevices"/> in line with <paramref name="incoming"/> in place, and
    /// reports whether anything moved.
    /// </summary>
    /// <remarks>
    /// Warning: do not replace this with Clear-then-refill.
    ///
    /// Clearing makes every bound picker drop its selection, on every snapshot — and a snapshot
    /// arrives for each step of a recording or a file transcription. The device list is the same
    /// list almost every time, so the common case must not touch the collection at all.
    /// <c>AudioInputDevice</c> is a record, which is what makes the comparison below cheap.
    /// </remarks>
    private bool SyncAudioDevices(IReadOnlyList<AudioInputDevice> incoming)
    {
        var changed = false;
        for (var index = 0; index < incoming.Count; index++)
        {
            if (index >= AudioDevices.Count) { AudioDevices.Add(incoming[index]); changed = true; }
            else if (!Equals(AudioDevices[index], incoming[index])) { AudioDevices[index] = incoming[index]; changed = true; }
        }
        while (AudioDevices.Count > incoming.Count)
        {
            AudioDevices.RemoveAt(AudioDevices.Count - 1);
            changed = true;
        }
        return changed;
    }

    private void ApplySnapshot(TranscriptionWorkflowSnapshot snapshot)
    {
        var completedNow = snapshot.State == TranscriptionWorkflowState.Completed
            && !string.Equals(State, nameof(TranscriptionWorkflowState.Completed), StringComparison.Ordinal);
        // Warning: a bound picker writes null back when the collection it lists is emptied. The
        // null-while-not-empty guard on SelectedAudioDevice cannot catch that one, because the
        // list IS empty at that instant. Without the flag the workflow loses its device on every
        // refresh — and every file transcription raises several.
        _applyingSnapshot = true;
        try
        {
            var listChanged = SyncAudioDevices(snapshot.AudioDevices);
            if (listChanged) Notify(nameof(HasAudioDevices));
            var selected = AudioDevices.FirstOrDefault(item => item.Id == snapshot.SelectedAudioDeviceId);
            if (listChanged && Equals(selected, _selectedAudioDevice))
            {
                // The picker dropped its selection when the collection moved under it, and this
                // view model's value did not change — so notifying it again publishes a value the
                // binding has already sent, and the picker stays blank. Publish null first, so the
                // notification that follows carries a value the binding has to push.
                _selectedAudioDevice = null;
                Notify(nameof(SelectedAudioDevice));
            }
            _selectedAudioDevice = selected;
            Notify(nameof(SelectedAudioDevice));
        }
        finally { _applyingSnapshot = false; }
        DevicesChanged?.Invoke(this, EventArgs.Empty);
        if (!_isImporting)
        {
            State = snapshot.State.ToString();
            Message = snapshot.Message;
            ErrorCode = snapshot.ErrorCode;
            CanStartRecording = snapshot.CanStartRecording;
            CanStop = snapshot.CanStop;
            CanCancel = snapshot.CanCancel;
            CanTranscribeFile = snapshot.CanTranscribeFile;
        }
        ((AsyncCommand)StartCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)StopCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)CancelCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)TranscribeFileCommand).RaiseCanExecuteChanged();
        if (completedNow) TranscriptionSaved?.Invoke(this, EventArgs.Empty);
    }

    private void BeginImport()
    {
        IsImporting = true;
        ImportProgress = 0;
        State = "Importing";
        Message = "Preparing audio…";
        ErrorCode = null;
        CanStartRecording = false;
        CanStop = false;
        CanCancel = true;
        CanTranscribeFile = false;
        RaiseWorkflowCommands();
    }

    private void EndImport()
    {
        IsImporting = false;
        ImportProgress = 0;
        ApplySnapshot(_workflow.Snapshot);
    }

    private void RaiseWorkflowCommands()
    {
        ((AsyncCommand)StartCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)StopCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)CancelCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)TranscribeFileCommand).RaiseCanExecuteChanged();
    }
}
