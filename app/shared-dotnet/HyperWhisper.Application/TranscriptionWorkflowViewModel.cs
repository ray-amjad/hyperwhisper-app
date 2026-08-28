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
    public AudioInputDevice? SelectedAudioDevice
    {
        get => _selectedAudioDevice;
        set
        {
            if (!Set(ref _selectedAudioDevice, value)) return;
            _workflow.SelectDevice(value?.Id);
        }
    }
    public string FilePath { get => _filePath; set => Set(ref _filePath, value); }
    public string State { get => _state; private set => Set(ref _state, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string? ErrorCode { get => _errorCode; private set { if (Set(ref _errorCode, value)) Notify(nameof(HasError)); } }
    public bool HasError => ErrorCode != null;
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
                    imported = cloud
                        ? await _audioImport.ImportOriginalAsync(
                            path, preflight?.Constraints?.MaximumBytes ?? long.MaxValue, progress, import.Token)
                        : await _audioImport.ImportAsync(path, progress, import.Token);
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
            _ => default,
        };
        return value?.Trim().ToLowerInvariant() is
            "openai" or "groq" or "elevenlabs" or "mistral" or "grok" or "deepgram"
            or "assemblyai" or "soniox" or "gemini" or "geminitranscribe"
            or "gemini-transcribe" or "microsoftazurespeech" or "azure-mai"
            or "googlespeech" or "google-chirp" or "hyperwhisper";
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

    private void ApplySnapshot(TranscriptionWorkflowSnapshot snapshot)
    {
        var completedNow = snapshot.State == TranscriptionWorkflowState.Completed
            && !string.Equals(State, nameof(TranscriptionWorkflowState.Completed), StringComparison.Ordinal);
        AudioDevices.Clear();
        foreach (var device in snapshot.AudioDevices) AudioDevices.Add(device);
        _selectedAudioDevice = AudioDevices.FirstOrDefault(item => item.Id == snapshot.SelectedAudioDeviceId);
        Notify(nameof(SelectedAudioDevice));
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
