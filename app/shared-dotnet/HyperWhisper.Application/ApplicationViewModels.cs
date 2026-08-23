using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;
using HyperWhisper.FileTranscription;
using System.Windows.Input;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.ModelManagement;
using HyperWhisper.AudioNormalization;
using HyperWhisper.SpeechOutput;
using HyperWhisper.SharedCore;
using HyperWhisper.CloudAccount;

namespace HyperWhisper.PortableApplication.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void Notify([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class AsyncCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    private readonly Func<object?, Task> _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    private readonly Func<object?, bool>? _canExecute = canExecute;
    private bool _running;

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await _execute(parameter); }
        finally
        {
            _running = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

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
            "microsoftazurespeech" or "azure-mai" => CloudTranscriptionProvider.AzureMai,
            "googlespeech" or "google-chirp" => CloudTranscriptionProvider.GoogleChirp,
            "hyperwhisper" => CloudTranscriptionProvider.HyperWhisperCloud,
            _ => default,
        };
        return value?.Trim().ToLowerInvariant() is
            "openai" or "groq" or "elevenlabs" or "mistral" or "grok" or "deepgram"
            or "assemblyai" or "soniox" or "gemini" or "microsoftazurespeech" or "azure-mai"
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

public sealed class UiStatus : ViewModelBase
{
    private bool _isBusy;
    private string _message = "Ready";
    private string? _errorCode;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string? ErrorCode { get => _errorCode; private set { if (Set(ref _errorCode, value)) Notify(nameof(HasError)); } }
    public bool HasError => ErrorCode != null;

    public void Busy(string message) { IsBusy = true; ErrorCode = null; Message = message; }
    public void Success(string message) { IsBusy = false; ErrorCode = null; Message = message; }
    public void Failure(string code, string message) { IsBusy = false; ErrorCode = code; Message = message; }
}

public sealed class HomeViewModel : ViewModelBase
{
    private readonly HistoryRepository _history;
    private readonly VocabularyRepository _vocabulary;
    private readonly ModeRepository _modes;
    private int _historyCount;
    private int _vocabularyCount;
    private int _modeCount;

    public HomeViewModel(HistoryRepository history, VocabularyRepository vocabulary, ModeRepository modes, TranscriptionWorkflowViewModel? recording = null)
    {
        _history = history; _vocabulary = vocabulary; _modes = modes;
        Recording = recording;
        RefreshCommand = new AsyncCommand(_ => RefreshAsync());
    }

    public UiStatus Status { get; } = new();
    public TranscriptionWorkflowViewModel? Recording { get; }
    public int HistoryCount { get => _historyCount; private set => Set(ref _historyCount, value); }
    public int VocabularyCount { get => _vocabularyCount; private set => Set(ref _vocabularyCount, value); }
    public int ModeCount { get => _modeCount; private set => Set(ref _modeCount, value); }
    public ICommand RefreshCommand { get; }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Status.Busy("Refreshing library…");
        try
        {
            HistoryCount = (await _history.ListAsync(cancellationToken)).Count;
            VocabularyCount = (await _vocabulary.ListAsync(cancellationToken)).Count;
            ModeCount = (await _modes.ListAsync(cancellationToken)).Count;
            Recording?.RefreshDevices();
            Status.Success("Library ready");
        }
        catch (OperationCanceledException) { Status.Failure("home.cancelled", "Refresh cancelled"); }
        catch (Exception) { Status.Failure("home.refresh_failed", "Could not load the local library."); }
    }
}

public sealed class HistoryViewModel : ViewModelBase
{
    private readonly HistoryRepository _repository;
    private readonly IAudioPlaybackService? _playback;
    private readonly Func<Transcript, CancellationToken, Task<PortableTranscriptionResult>>? _retry;
    private Transcript? _selected;
    private string _searchText = string.Empty;
    private DateTimeOffset? _startDate;
    private DateTimeOffset? _endDate;
    private bool _showRawTranscript;
    private bool _deleteAudio;
    private bool _isRetrying;
    public HistoryViewModel(HistoryRepository repository, IAudioPlaybackService? playback = null, Func<Transcript, CancellationToken, Task<PortableTranscriptionResult>>? retry = null)
    {
        _repository = repository;
        _playback = playback;
        _retry = retry;
        RefreshCommand = new AsyncCommand(_ => RefreshAsync());
        DeleteCommand = new AsyncCommand(item => DeleteAsync(item as Transcript), item => item is Transcript);
        SearchCommand = new AsyncCommand(_ => SearchAsync());
        ClearFiltersCommand = new AsyncCommand(_ => ClearFiltersAsync());
        PlayCommand = new AsyncCommand(_ => PlayAsync(), _ => Selected?.AudioFilePath is not null);
        RetryCommand = new AsyncCommand(_ => RetryAsync(), _ => CanRetry);
    }
    public ObservableCollection<Transcript> Items { get; } = new();
    public Transcript? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            if (!HasRawTranscript) _showRawTranscript = false;
            Notify(nameof(ShowRawTranscript));
            NotifyDetail();
            Notify(nameof(CanRetry));
            ((AsyncCommand)PlayCommand).RaiseCanExecuteChanged();
            ((AsyncCommand)RetryCommand).RaiseCanExecuteChanged();
        }
    }
    public string SearchText { get => _searchText; set => Set(ref _searchText, value); }
    public DateTimeOffset? StartDate { get => _startDate; set => Set(ref _startDate, value); }
    public DateTimeOffset? EndDate { get => _endDate; set => Set(ref _endDate, value); }
    public bool ShowRawTranscript
    {
        get => _showRawTranscript;
        set
        {
            var next = value && HasRawTranscript;
            if (Set(ref _showRawTranscript, next)) NotifyDetail();
        }
    }
    public bool HasRawTranscript => !string.IsNullOrWhiteSpace(Selected?.TranscribedText);
    public string DetailText => ShowRawTranscript && HasRawTranscript
        ? Selected!.TranscribedText!
        : !string.IsNullOrWhiteSpace(Selected?.PostProcessedText)
            ? Selected!.PostProcessedText!
            : Selected?.Text ?? string.Empty;
    public string DetailLabel => ShowRawTranscript && HasRawTranscript
        ? "Raw transcription"
        : !string.IsNullOrWhiteSpace(Selected?.PostProcessedText)
            ? "Post-processed transcript"
            : "Final transcript";
    public bool DeleteAudio { get => _deleteAudio; set => Set(ref _deleteAudio, value); }
    public bool IsRetrying
    {
        get => _isRetrying;
        private set
        {
            if (!Set(ref _isRetrying, value)) return;
            Notify(nameof(CanRetry));
            ((AsyncCommand)RetryCommand).RaiseCanExecuteChanged();
        }
    }
    public bool CanRetry => !IsRetrying && _retry is not null && Selected is
        { Status: TranscriptStatus.Failed, AudioFilePath: not null };
    public UiStatus Status { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand ClearFiltersCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand RetryCommand { get; }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Status.Busy("Loading history…");
        try
        {
            Items.Clear();
            foreach (var item in await _repository.ListAsync(cancellationToken)) Items.Add(item);
            Selected = Items.FirstOrDefault();
            Status.Success(Items.Count == 0 ? "No transcripts yet" : $"{Items.Count} transcript(s)");
        }
        catch (OperationCanceledException) { Status.Failure("history.cancelled", "History load cancelled"); }
        catch (Exception) { Status.Failure("history.load_failed", "Could not load transcript history."); }
    }

    public async Task SearchAsync(CancellationToken cancellationToken = default)
    {
        if (StartDate is { } start && EndDate is { } end && start.Date > end.Date)
        {
            Status.Failure("history.date_range_invalid", "The start date must not be after the end date.");
            return;
        }
        try
        {
            Items.Clear();
            var fromUtc = StartDate is { } from ? LocalDateStartUtc(from) : (DateTime?)null;
            var toUtcExclusive = EndDate is { } to ? LocalDateStartUtc(to, dayOffset: 1) : (DateTime?)null;
            foreach (var item in await _repository.SearchAsync(
                SearchText, fromUtc, toUtcExclusive, cancellationToken)) Items.Add(item);
            Selected = Items.FirstOrDefault();
            Status.Success($"{Items.Count} matching transcript(s)");
        }
        catch (Exception) { Status.Failure("history.search_failed", "Could not search transcript history."); }
    }

    public async Task ClearFiltersAsync(CancellationToken cancellationToken = default)
    {
        SearchText = string.Empty;
        StartDate = null;
        EndDate = null;
        await RefreshAsync(cancellationToken);
    }

    public async Task DeleteAsync(Transcript? transcript, CancellationToken cancellationToken = default)
    {
        if (transcript == null) { Status.Failure("history.no_selection", "Select a transcript to delete."); return; }
        try
        {
            var deletion = await _repository.DeleteAsync(transcript.Id, DeleteAudio, cancellationToken);
            if (!deletion.TranscriptDeleted)
            { Status.Failure("history.not_found", "The transcript no longer exists."); return; }
            Items.Remove(transcript);
            Selected = Items.FirstOrDefault();
            if (deletion.Warning is null) Status.Success(DeleteAudio ? "Transcript and audio deleted" : "Transcript deleted; audio retained");
            else Status.Failure("history.audio_delete_failed", deletion.Warning);
        }
        catch (OperationCanceledException) { Status.Failure("history.cancelled", "Delete cancelled"); }
        catch (Exception) { Status.Failure("history.delete_failed", "Could not delete the transcript."); }
    }

    public Task PlayAsync()
    {
        if (_playback is null || Selected?.AudioFilePath is not { } path) { Status.Failure("history.audio_unavailable", "This transcript has no playable audio."); return Task.CompletedTask; }
        var loaded = _playback.Load(path);
        if (loaded.IsFailure) Status.Failure(loaded.Error!.Code, loaded.Error.Message);
        else { _playback.Play(); Status.Success("Playing recording"); }
        return Task.CompletedTask;
    }

    public async Task RetryAsync(CancellationToken cancellationToken = default)
    {
        if (_retry is null || Selected is not { Status: TranscriptStatus.Failed } selected)
        {
            Status.Failure("history.retry_unavailable", "Only a failed transcription with retained audio can be retried.");
            return;
        }

        var selectedId = selected.Id;
        IsRetrying = true;
        try
        {
            var result = await _retry(selected, cancellationToken);
            await RefreshAsync(CancellationToken.None);
            Selected = Items.FirstOrDefault(item => item.Id == selectedId) ?? Items.FirstOrDefault();
            if (result.IsSuccess) Status.Success("Transcription retry completed");
            else if (result.Failure?.Code == PortableTranscriptionErrorCode.Cancelled)
                Status.Failure("history.retry_cancelled", "Transcription retry cancelled");
            else Status.Failure("history.retry_failed", result.Failure?.Message ?? "The transcription retry failed.");
        }
        catch (OperationCanceledException)
        {
            await RefreshAsync(CancellationToken.None);
            Selected = Items.FirstOrDefault(item => item.Id == selectedId) ?? Items.FirstOrDefault();
            Status.Failure("history.retry_cancelled", "Transcription retry cancelled");
        }
        catch (Exception) { Status.Failure("history.retry_failed", "The transcription retry failed."); }
        finally { IsRetrying = false; }
    }

    private void NotifyDetail()
    {
        Notify(nameof(HasRawTranscript));
        Notify(nameof(DetailText));
        Notify(nameof(DetailLabel));
    }

    private static DateTime LocalDateStartUtc(DateTimeOffset value, int dayOffset = 0)
    {
        var localDate = new DateTime(value.Year, value.Month, value.Day).AddDays(dayOffset);
        return new DateTimeOffset(localDate, TimeZoneInfo.Local.GetUtcOffset(localDate)).UtcDateTime;
    }
}

public sealed class VocabularyViewModel : ViewModelBase
{
    private readonly VocabularyRepository _repository;
    private string _word = string.Empty;
    private string _replacement = string.Empty;
    private VocabularyItem? _selected;
    private string _transferPath = string.Empty;
    public VocabularyViewModel(VocabularyRepository repository)
    {
        _repository = repository;
        AddCommand = new AsyncCommand(_ => SaveAsync());
        DeleteCommand = new AsyncCommand(item => DeleteAsync(item as VocabularyItem), item => item is VocabularyItem);
        ImportCommand = new AsyncCommand(_ => ImportAsync());
        ExportCommand = new AsyncCommand(_ => ExportAsync());
    }
    public ObservableCollection<VocabularyItem> Items { get; } = new();
    public string Word { get => _word; set => Set(ref _word, value); }
    public string Replacement { get => _replacement; set => Set(ref _replacement, value); }
    public VocabularyItem? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value) && value is not null) { Word = value.Word; Replacement = value.Replacement ?? string.Empty; } }
    }
    public string TransferPath { get => _transferPath; set => Set(ref _transferPath, value); }
    public UiStatus Status { get; } = new();
    public ICommand AddCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try { Items.Clear(); foreach (var item in await _repository.ListAsync(cancellationToken)) Items.Add(item); Status.Success($"{Items.Count} term(s)"); }
        catch (Exception) { Status.Failure("vocabulary.load_failed", "Could not load vocabulary."); }
    }
    public Task AddAsync(CancellationToken cancellationToken = default) => SaveAsync(cancellationToken);
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Word)) { Status.Failure("vocabulary.word_required", "Enter a word or phrase."); return; }
        var item = Selected ?? new VocabularyItem { SortOrder = Items.Count };
        item.Word = Word.Trim(); item.Replacement = string.IsNullOrWhiteSpace(Replacement) ? null : Replacement.Trim();
        try { _ = await _repository.UpsertAsync(item, cancellationToken); await RefreshAsync(cancellationToken); Selected = null; Word = string.Empty; Replacement = string.Empty; Status.Success("Vocabulary saved"); }
        catch (Exception) { Status.Failure("vocabulary.add_failed", "Could not add the vocabulary item."); }
    }
    public async Task DeleteAsync(VocabularyItem? item, CancellationToken cancellationToken = default)
    {
        if (item == null) { Status.Failure("vocabulary.no_selection", "Select a vocabulary item to delete."); return; }
        try { if (await _repository.DeleteAsync(item.Id, cancellationToken)) { Items.Remove(item); Status.Success("Vocabulary deleted"); } else Status.Failure("vocabulary.not_found", "The vocabulary item no longer exists."); }
        catch (Exception) { Status.Failure("vocabulary.delete_failed", "Could not delete the vocabulary item."); }
    }

    public async Task ImportAsync(CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(TransferPath)) { Status.Failure("vocabulary.path_required", "Choose a vocabulary file."); return; }
        try
        {
            var lines = await File.ReadAllLinesAsync(TransferPath, cancellationToken);
            var items = lines.Select((line, index) => line.Split('\t', 2) switch
            {
                var fields => new VocabularyItem { Word = fields[0].Trim(), Replacement = fields.Length > 1 ? fields[1].Trim() : null, SortOrder = index }
            }).Where(item => item.Word.Length > 0);
            var count = await _repository.MergeAsync(items, cancellationToken);
            await RefreshAsync(cancellationToken);
            Status.Success($"Imported {count} term(s); duplicates merged");
        }
        catch (Exception) { Status.Failure("vocabulary.import_failed", "Could not import vocabulary."); }
    }

    public async Task ExportAsync(CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(TransferPath)) { Status.Failure("vocabulary.path_required", "Choose an export file."); return; }
        try
        {
            var lines = (await _repository.ListAsync(cancellationToken)).Select(item => $"{item.Word}\t{item.Replacement ?? string.Empty}");
            await File.WriteAllLinesAsync(TransferPath, lines, cancellationToken);
            Status.Success("Vocabulary exported");
        }
        catch (Exception) { Status.Failure("vocabulary.export_failed", "Could not export vocabulary."); }
    }
}

public sealed class ModesViewModel : ViewModelBase
{
    private readonly ModeRepository _repository;
    private readonly PortableSettingsService? _settings;
    private readonly ICredentialStore? _credentials;
    private Mode? _selected;
    private string _name = string.Empty;
    private string _language = "en";
    private bool _localPostProcessingEnabled;
    private string _localPostProcessingModel = string.Empty;
    private string _userSystemPrompt = string.Empty;
    private string _providerType = "local";
    private string _localEngine = "whisper";
    private string _transcriptionModel = "base";
    private string _cloudProvider = "elevenlabs";
    private string _cloudAccuracyTier = "elevenLabsScribeV2";
    private string _cloudDomain = string.Empty;
    private string _geminiPrompt = string.Empty;
    private string _customVocabulary = string.Empty;
    private bool _enableScreenOcr;
    private string _postProcessingMode = "off";
    private string _postProcessingProvider = "openai";
    private string _postProcessingModel = "gpt-4.1-mini";
    private string _hyperWhisperCloudModel = "anthropic:claude-haiku-4-5";
    private string _customInstructions = string.Empty;
    private bool _punctuation = true;
    private bool _capitalization = true;
    private bool _profanityFilter;
    private bool _removeTrailingPeriod;
    private string _englishSpelling = string.Empty;
    private string _customEndpointName = string.Empty;
    private string _customEndpointUrl = string.Empty;
    private string _customEndpointModel = string.Empty;
    private string _customEndpointApiKey = string.Empty;
    private Guid? _customEndpointId;
    public ModesViewModel(
        ModeRepository repository,
        PortableSettingsService? settings = null,
        ICredentialStore? credentials = null)
    {
        _repository = repository;
        _settings = settings;
        _credentials = credentials;
        SaveCommand = new AsyncCommand(_ => SaveAsync());
        NewCommand = new AsyncCommand(_ =>
        {
            Selected = null;
            Name = string.Empty;
            Language = "en";
            LocalPostProcessingEnabled = false;
            LocalPostProcessingModel = string.Empty;
            UserSystemPrompt = string.Empty;
            ProviderType = "local"; LocalEngine = "whisper"; TranscriptionModel = "base";
            CloudProvider = "elevenlabs"; CloudAccuracyTier = "elevenLabsScribeV2"; CloudDomain = string.Empty;
            GeminiPrompt = string.Empty; CustomVocabulary = string.Empty; EnableScreenOcr = false;
            PostProcessingMode = "off"; PostProcessingProvider = "openai";
            PostProcessingModel = "gpt-4.1-mini"; HyperWhisperCloudModel = "anthropic:claude-haiku-4-5";
            CustomInstructions = string.Empty; Punctuation = true; Capitalization = true;
            ProfanityFilter = false; RemoveTrailingPeriod = false; EnglishSpelling = string.Empty;
            ClearCustomEndpointEditor();
            return Task.CompletedTask;
        });
        DeleteCommand = new AsyncCommand(_ => DeleteAsync());
    }
    public ObservableCollection<Mode> Items { get; } = new();
    public Mode? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value) || value is null) return;
            PersistSelectedMode(value.Id);
            Name = value.Name;
            Language = value.Language;
            LocalPostProcessingEnabled = value.PostProcessingMode == 2
                && string.Equals(value.PostProcessingProvider, "local_llm", StringComparison.OrdinalIgnoreCase);
            LocalPostProcessingModel = value.LocalPostProcessingModel ?? string.Empty;
            UserSystemPrompt = value.UserSystemPrompt ?? string.Empty;
            ProviderType = value.ProviderType ?? "local";
            LocalEngine = value.LocalEngine;
            TranscriptionModel = value.ProviderType == "cloud" ? value.CloudTranscriptionModel ?? string.Empty : value.LocalEngine == "parakeet" ? value.LocalParakeetModel ?? "parakeet-v3" : value.ModelType ?? value.Model ?? "base";
            CloudProvider = value.CloudProvider ?? "elevenlabs";
            CloudAccuracyTier = value.CloudAccuracyTier;
            CloudDomain = value.CloudTranscriptionDomain ?? string.Empty;
            GeminiPrompt = value.GeminiCustomPrompt ?? string.Empty;
            CustomVocabulary = string.Join(", ", value.CustomVocabulary ?? []);
            EnableScreenOcr = value.EnableScreenOCR;
            PostProcessingMode = value.PostProcessingMode switch { 1 => "cloud", 2 => "local", _ => "off" };
            PostProcessingProvider = value.PostProcessingProvider?.StartsWith("custom:", StringComparison.OrdinalIgnoreCase) == true
                ? "custom" : value.PostProcessingProvider ?? "openai";
            PostProcessingModel = value.LanguageModel ?? string.Empty;
            HyperWhisperCloudModel = value.CloudPostProcessingModel;
            CustomInstructions = value.CustomInstructions ?? string.Empty;
            Punctuation = value.Punctuation; Capitalization = value.Capitalization;
            ProfanityFilter = value.ProfanityFilter; RemoveTrailingPeriod = value.RemoveTrailingPeriod;
            EnglishSpelling = value.EnglishSpelling ?? string.Empty;
            LoadCustomEndpoint(value.PostProcessingProvider);
        }
    }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Language { get => _language; set => Set(ref _language, value); }
    public bool LocalPostProcessingEnabled
    {
        get => _localPostProcessingEnabled;
        set
        {
            if (!Set(ref _localPostProcessingEnabled, value)) return;
            var desired = value ? "local" : _postProcessingMode == "local" ? "off" : _postProcessingMode;
            if (_postProcessingMode != desired)
            {
                _postProcessingMode = desired;
                Notify(nameof(PostProcessingMode));
            }
        }
    }
    public string LocalPostProcessingModel { get => _localPostProcessingModel; set => Set(ref _localPostProcessingModel, value); }
    public string UserSystemPrompt { get => _userSystemPrompt; set => Set(ref _userSystemPrompt, value); }
    public string ProviderType
    {
        get => _providerType;
        set
        {
            if (!Set(ref _providerType, value)) return;
            if (value == "cloud" && PortableModelCatalog.All.Any(model => model.Kind is ManagedModelKind.Whisper or ManagedModelKind.Parakeet
                && string.Equals(model.Id, TranscriptionModel, StringComparison.Ordinal))) TranscriptionModel = string.Empty;
        }
    }
    public string LocalEngine { get => _localEngine; set => Set(ref _localEngine, value); }
    public string TranscriptionModel { get => _transcriptionModel; set => Set(ref _transcriptionModel, value); }
    public IReadOnlyList<string> ProviderTypes { get; } = ["local", "cloud"];
    public IReadOnlyList<string> LocalEngines { get; } = ["whisper", "parakeet"];
    public string CloudProvider { get => _cloudProvider; set => Set(ref _cloudProvider, value); }
    public string CloudAccuracyTier { get => _cloudAccuracyTier; set => Set(ref _cloudAccuracyTier, value); }
    public string CloudDomain { get => _cloudDomain; set => Set(ref _cloudDomain, value); }
    public string GeminiPrompt { get => _geminiPrompt; set => Set(ref _geminiPrompt, value); }
    public string CustomVocabulary { get => _customVocabulary; set => Set(ref _customVocabulary, value); }
    public bool EnableScreenOcr { get => _enableScreenOcr; set => Set(ref _enableScreenOcr, value); }
    public string PostProcessingMode
    {
        get => _postProcessingMode;
        set
        {
            var normalized = value is "cloud" or "local" ? value : "off";
            if (Set(ref _postProcessingMode, normalized) && _localPostProcessingEnabled != (normalized == "local"))
            {
                _localPostProcessingEnabled = normalized == "local";
                Notify(nameof(LocalPostProcessingEnabled));
            }
        }
    }
    public string PostProcessingProvider { get => _postProcessingProvider; set => Set(ref _postProcessingProvider, value ?? "openai"); }
    public string PostProcessingModel { get => _postProcessingModel; set => Set(ref _postProcessingModel, value ?? string.Empty); }
    public string HyperWhisperCloudModel { get => _hyperWhisperCloudModel; set => Set(ref _hyperWhisperCloudModel, value ?? string.Empty); }
    public string CustomInstructions { get => _customInstructions; set => Set(ref _customInstructions, value ?? string.Empty); }
    public bool Punctuation { get => _punctuation; set => Set(ref _punctuation, value); }
    public bool Capitalization { get => _capitalization; set => Set(ref _capitalization, value); }
    public bool ProfanityFilter { get => _profanityFilter; set => Set(ref _profanityFilter, value); }
    public bool RemoveTrailingPeriod { get => _removeTrailingPeriod; set => Set(ref _removeTrailingPeriod, value); }
    public string EnglishSpelling { get => _englishSpelling; set => Set(ref _englishSpelling, value ?? string.Empty); }
    public string CustomEndpointName { get => _customEndpointName; set => Set(ref _customEndpointName, value ?? string.Empty); }
    public string CustomEndpointUrl { get => _customEndpointUrl; set => Set(ref _customEndpointUrl, value ?? string.Empty); }
    public string CustomEndpointModel { get => _customEndpointModel; set => Set(ref _customEndpointModel, value ?? string.Empty); }
    public string CustomEndpointApiKey { get => _customEndpointApiKey; set => Set(ref _customEndpointApiKey, value ?? string.Empty); }
    public IReadOnlyList<string> PostProcessingModes { get; } = ["off", "cloud", "local"];
    public IReadOnlyList<string> PostProcessingProviders { get; } = ["hyperwhispercloud", "openai", "anthropic", "groq", "grok", "gemini", "cerebras", "mistral", "custom"];
    public IReadOnlyList<string> CloudProviders { get; } = ["openai", "groq", "elevenlabs", "mistral", "grok", "deepgram", "assemblyai", "soniox", "gemini", "microsoftazurespeech", "googlespeech", "hyperwhisper"];
    public IReadOnlyList<string> CloudAccuracyTiers { get; } = ["groqWhisper", "deepgramNova3", "grokStt", "azureMaiTranscribe", "googleChirp3", "elevenLabsScribeV2", "openaiWhisper", "gemini", "mistralVoxtral", "assemblyAI", "soniox"];
    public IReadOnlyList<string> CloudDomains { get; } = ["", "medical"];
    public UiStatus Status { get; } = new();
    public ICommand SaveCommand { get; }
    public ICommand NewCommand { get; }
    public ICommand DeleteCommand { get; }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Items.Clear();
            foreach (var item in await _repository.ListAsync(cancellationToken)) Items.Add(item);
            var selectedId = _settings?.Get<string>("selectedModeId");
            Selected = Guid.TryParse(selectedId, out var id)
                ? Items.FirstOrDefault(item => item.Id == id) ?? FallbackSelection()
                : FallbackSelection();
            Status.Success($"{Items.Count} mode(s)");
        }
        catch (Exception) { Status.Failure("modes.load_failed", "Could not load modes."); }
    }
    private Mode? FallbackSelection() => Items.FirstOrDefault(item => item.IsDefault)
        ?? Items.OrderBy(item => item.SortOrder).FirstOrDefault();
    private void PersistSelectedMode(Guid id)
    {
        if (_settings is null) return;
        _settings.Set("selectedModeId", id.ToString("D"));
        var saved = _settings.Save();
        if (saved.IsFailure) Status.Failure(saved.Error!.Code, "The selected mode could not be remembered on this device.");
    }
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Name)) { Status.Failure("modes.name_required", "Enter a mode name."); return; }
        if (PostProcessingMode == "local"
            && (string.IsNullOrWhiteSpace(LocalPostProcessingModel)
                || LocalPostProcessingModel != Path.GetFileName(LocalPostProcessingModel)
                || LocalPostProcessingModel.Contains('\\')
                || !LocalPostProcessingModel.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)))
        {
            Status.Failure("modes.local_llm_model_required", "Enter a GGUF model filename from the local LLM models directory.");
            return;
        }
        if (UserSystemPrompt.Length > 2000)
        {
            Status.Failure("modes.prompt_too_long", "The system prompt cannot exceed 2000 characters.");
            return;
        }
        if (CustomInstructions.Length > 4000)
        { Status.Failure("modes.instructions_too_long", "Custom instructions cannot exceed 4000 characters."); return; }
        if (PostProcessingMode == "cloud" && !PostProcessingProviders.Contains(PostProcessingProvider, StringComparer.Ordinal))
        { Status.Failure("modes.postprocessing_provider_invalid", "Select a supported post-processing provider."); return; }
        if (PostProcessingMode == "cloud" && PostProcessingProvider == "custom" && !ValidateCustomEndpoint()) return;
        if (GeminiPrompt.Length > 2000) { Status.Failure("modes.gemini_prompt_too_long", "The Gemini prompt cannot exceed 2000 characters."); return; }
        if (ProviderType == "cloud" && !CloudProviders.Contains(CloudProvider, StringComparer.Ordinal))
        { Status.Failure("modes.cloud_provider_required", "Select a supported cloud provider."); return; }
        if (ProviderType != "cloud")
        {
            var kind = LocalEngine == "parakeet" ? ManagedModelKind.Parakeet : ManagedModelKind.Whisper;
            if (!PortableModelCatalog.All.Any(model => model.Kind == kind && string.Equals(model.Id, TranscriptionModel, StringComparison.Ordinal)))
            { Status.Failure("modes.local_model_invalid", "Select a model ID from the local model catalog."); return; }
        }
        var customEndpointId = PostProcessingMode == "cloud" && PostProcessingProvider == "custom"
            ? _customEndpointId ?? Guid.NewGuid()
            : (Guid?)null;
        var mode = Selected ?? new Mode { SortOrder = Items.Count };
        mode.Name = Name.Trim();
        mode.Language = string.IsNullOrWhiteSpace(Language) ? "auto" : Language.Trim();
        mode.ProviderType = ProviderType == "cloud" ? "cloud" : "local";
        mode.LocalEngine = LocalEngine == "parakeet" ? "parakeet" : "whisper";
        if (mode.ProviderType == "cloud")
        {
            mode.CloudProvider = CloudProvider;
            mode.CloudTranscriptionModel = string.IsNullOrWhiteSpace(TranscriptionModel) ? null : TranscriptionModel.Trim();
            mode.CloudAccuracyTier = CloudAccuracyTiers.Contains(CloudAccuracyTier, StringComparer.Ordinal) ? CloudAccuracyTier : "elevenLabsScribeV2";
            mode.CloudTranscriptionDomain = CloudDomain == "medical" ? "medical" : null;
            mode.GeminiCustomPrompt = string.IsNullOrWhiteSpace(GeminiPrompt) ? null : GeminiPrompt.Trim();
        }
        else if (mode.LocalEngine == "parakeet") { mode.LocalParakeetModel = TranscriptionModel.Trim(); mode.Model = mode.LocalParakeetModel; }
        else { mode.Model = string.IsNullOrWhiteSpace(TranscriptionModel) ? "base" : TranscriptionModel.Trim(); mode.ModelType = mode.Model; }
        mode.PostProcessingMode = PostProcessingMode switch { "cloud" => 1, "local" => 2, _ => 0 };
        mode.PostProcessingProvider = mode.PostProcessingMode switch
        {
            2 => "local_llm",
            1 when customEndpointId is { } id => $"custom:{id:D}",
            1 => PostProcessingProvider,
            _ => "none",
        };
        mode.LocalPostProcessingModel = string.IsNullOrWhiteSpace(LocalPostProcessingModel)
            ? null
            : LocalPostProcessingModel.Trim();
        mode.LanguageModel = string.IsNullOrWhiteSpace(PostProcessingModel) ? null : PostProcessingModel.Trim();
        mode.CloudPostProcessingModel = string.IsNullOrWhiteSpace(HyperWhisperCloudModel)
            ? "anthropic:claude-haiku-4-5" : HyperWhisperCloudModel.Trim();
        mode.UserSystemPrompt = string.IsNullOrWhiteSpace(UserSystemPrompt) ? null : UserSystemPrompt.Trim();
        mode.CustomInstructions = string.IsNullOrWhiteSpace(CustomInstructions) ? null : CustomInstructions.Trim();
        mode.Punctuation = Punctuation; mode.Capitalization = Capitalization;
        mode.ProfanityFilter = ProfanityFilter; mode.RemoveTrailingPeriod = RemoveTrailingPeriod;
        mode.EnglishSpelling = string.IsNullOrWhiteSpace(EnglishSpelling) ? null : EnglishSpelling.Trim();
        mode.CustomVocabulary = CustomVocabulary.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(1000).ToList();
        mode.EnableScreenOCR = EnableScreenOcr;
        mode.ModifiedDate = DateTime.UtcNow;
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? settingsSnapshot = null;
        byte[]? previousCredential = null;
        var credentialTouched = customEndpointId is not null && !string.IsNullOrWhiteSpace(CustomEndpointApiKey);
        try
        {
            if (customEndpointId is { } id)
            {
                settingsSnapshot = _settings!.Snapshot();
                if (credentialTouched)
                {
                    var previous = _credentials!.Read("HyperWhisper", $"CustomEndpoint_{id:D}");
                    if (previous.IsFailure) throw new InvalidOperationException(previous.Error!.Message);
                    previousCredential = previous.Value;
                }
                PersistCustomEndpoint(id);
            }
            await _repository.UpsertSafelyAsync(mode, cancellationToken);
            var existingIndex = Items.IndexOf(mode);
            if (existingIndex < 0) Items.Add(mode);
            else Items[existingIndex] = mode;
            Selected = mode;
            if (customEndpointId is { } savedId)
            {
                _customEndpointId = savedId;
                CustomEndpointApiKey = string.Empty;
            }
            Status.Success("Mode saved");
        }
        catch (Exception)
        {
            if (settingsSnapshot is not null)
            {
                _settings!.Replace(settingsSnapshot);
                _ = _settings.Save();
            }
            if (customEndpointId is { } id && credentialTouched)
            {
                if (previousCredential is { Length: > 0 })
                    _ = _credentials!.Write("HyperWhisper", $"CustomEndpoint_{id:D}", previousCredential);
                else
                    _ = _credentials!.Delete("HyperWhisper", $"CustomEndpoint_{id:D}");
            }
            Status.Failure("modes.save_failed", "Could not save the mode or its secure endpoint configuration.");
        }
        finally
        {
            if (previousCredential is not null) CryptographicOperations.ZeroMemory(previousCredential);
        }
    }
    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        if (Selected == null) { Status.Failure("modes.no_selection", "Select a mode to delete."); return; }
        var selected = Selected;
        try { if (await _repository.DeleteSafelyAsync(selected.Id, cancellationToken)) { Items.Remove(selected); Selected = FallbackSelection(); Status.Success("Mode deleted"); } else Status.Failure("modes.not_found", "The mode no longer exists."); }
        catch (InvalidOperationException exception) { Status.Failure("modes.last_mode", exception.Message); }
        catch (Exception) { Status.Failure("modes.delete_failed", "Could not delete the mode."); }
    }

    private bool ValidateCustomEndpoint()
    {
        if (string.IsNullOrWhiteSpace(CustomEndpointName)
            || string.IsNullOrWhiteSpace(CustomEndpointModel)
            || !Uri.TryCreate(CustomEndpointUrl.Trim(), UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            Status.Failure("modes.custom_endpoint_invalid", "Enter a name, HTTP(S) endpoint URL without embedded credentials, and model.");
            return false;
        }
        if (_settings is null || _credentials is null)
        {
            Status.Failure("modes.custom_endpoint_unavailable", "Secure custom endpoint storage is unavailable.");
            return false;
        }
        return true;
    }

    private void PersistCustomEndpoint(Guid id)
    {
        var endpoints = (_settings!.Get<PortableCustomPostProcessingEndpoint[]>("customEndpoints", []) ?? []).ToList();
        var configured = new PortableCustomPostProcessingEndpoint(
            id, CustomEndpointName.Trim(), CustomEndpointUrl.Trim(), CustomEndpointModel.Trim());
        var index = endpoints.FindIndex(item => item.Id == id);
        if (index < 0) endpoints.Add(configured); else endpoints[index] = configured;
        _settings.Set("customEndpoints", endpoints);
        if (!string.IsNullOrWhiteSpace(CustomEndpointApiKey))
        {
            var bytes = Encoding.UTF8.GetBytes(CustomEndpointApiKey.Trim());
            try
            {
                var saved = _credentials!.Write("HyperWhisper", $"CustomEndpoint_{id:D}", bytes);
                if (saved.IsFailure) throw new InvalidOperationException(saved.Error!.Message);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        var persisted = _settings.Save();
        if (persisted.IsFailure) throw new InvalidOperationException(persisted.Error!.Message);
    }

    private void LoadCustomEndpoint(string? provider)
    {
        ClearCustomEndpointEditor();
        if (_settings is null || provider?.StartsWith("custom:", StringComparison.OrdinalIgnoreCase) != true
            || !Guid.TryParse(provider[7..], out var id)) return;
        var configured = (_settings.Get<PortableCustomPostProcessingEndpoint[]>("customEndpoints", []) ?? [])
            .FirstOrDefault(item => item.Id == id);
        if (configured is null) return;
        _customEndpointId = id;
        CustomEndpointName = configured.Name;
        CustomEndpointUrl = configured.EndpointUrl;
        CustomEndpointModel = configured.ModelName;
    }

    private void ClearCustomEndpointEditor()
    {
        _customEndpointId = null;
        CustomEndpointName = CustomEndpointUrl = CustomEndpointModel = CustomEndpointApiKey = string.Empty;
    }
}

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly PortableSettingsService _settings;
    private string _language = "auto";
    private string _localLlmBackend = "cpu";
    private bool _allowLocalLlmCpuFallback = true;
    private bool _localApiEnabled;
    private int _localApiPort = 51671;
    private string _toggleShortcutModifiers = "Control, Alt";
    private string _toggleShortcutKey = string.Empty;
    private string _cancelShortcutModifiers = "None";
    private string _cancelShortcutKey = "Escape";
    private string _changeModeShortcutModifiers = "Control, Shift";
    private string _changeModeShortcutKey = "Period";
    private string _streamingShortcutModifiers = "Control, Shift";
    private string _streamingShortcutKey = "Space";
    private string _pushToTalkMode = "Disabled";
    private string _pushToTalkModifier = "LeftAlt";
    private string _pushToTalkShortcutModifiers = "None";
    private string _pushToTalkShortcutKey = string.Empty;
    private bool _pushToTalkDoublePressLock;
    private bool _pasteResultText = true;
    private bool _removeFillerWords = true;
    private bool _autocapitalizeInsert = true;
    private bool _restoreClipboardAfterPaste = true;
    private double _clipboardRestoreDelaySeconds = 10;
    private bool _streamingEnabled;
    private string _streamingProvider = "deepgram";
    private string _streamingLanguage = "auto";
    private string _streamingModel = "nova-3-general";
    private bool _streamingFastFormatting;
    private bool _autostartEnabled;
    private bool _enableSoundEffects = true;
    private bool _autoIncreaseMicVolume;
    private bool _keepMicrophoneWarm;
    private bool _keepAudioFiles = true;
    private bool _autoDeleteEnabled;
    private int _autoDeleteDaysOld = 30;
    private string _audioEnvironmentPolicy = "unchanged";
    private string _desktopContextStatus = "Desktop context capability not checked";
    private bool _enableErrorLogging = true;
    private string _localWhisperBackend = "auto";
    private bool _allowLocalWhisperCpuFallback = true;
    private string _processWhisperBackend = "auto";
    private bool _processWhisperCpuFallback = true;
    private bool _whisperBaselineCaptured;
    private readonly string _currentWhisperRuntimeStatus;
    public SettingsViewModel(
        PortableSettingsService settings,
        string localLlmRuntimeStatus = "Local LLM runtime not connected",
        string localWhisperRuntimeStatus = "Local Whisper runtime not connected")
    {
        _settings = settings;
        SaveCommand = new AsyncCommand(_ => { Save(); return Task.CompletedTask; });
        ResetShortcutsCommand = new AsyncCommand(_ => { ResetShortcuts(); return Task.CompletedTask; });
        LocalLlmRuntimeStatus = localLlmRuntimeStatus;
        _currentWhisperRuntimeStatus = localWhisperRuntimeStatus;
    }
    public string Language { get => _language; set => Set(ref _language, value); }
    public string LocalLlmBackend { get => _localLlmBackend; set => Set(ref _localLlmBackend, NormalizeBackend(value)); }
    public bool AllowLocalLlmCpuFallback { get => _allowLocalLlmCpuFallback; set => Set(ref _allowLocalLlmCpuFallback, value); }
    public bool LocalApiEnabled { get => _localApiEnabled; set => Set(ref _localApiEnabled, value); }
    public int LocalApiPort { get => _localApiPort; set => Set(ref _localApiPort, Math.Clamp(value, 0, 65535)); }
    public string ToggleShortcutModifiers { get => _toggleShortcutModifiers; set => Set(ref _toggleShortcutModifiers, value ?? string.Empty); }
    public string ToggleShortcutKey { get => _toggleShortcutKey; set => Set(ref _toggleShortcutKey, value ?? string.Empty); }
    public string CancelShortcutModifiers { get => _cancelShortcutModifiers; set => Set(ref _cancelShortcutModifiers, value ?? string.Empty); }
    public string CancelShortcutKey { get => _cancelShortcutKey; set => Set(ref _cancelShortcutKey, value ?? string.Empty); }
    public string ChangeModeShortcutModifiers { get => _changeModeShortcutModifiers; set => Set(ref _changeModeShortcutModifiers, value ?? string.Empty); }
    public string ChangeModeShortcutKey { get => _changeModeShortcutKey; set => Set(ref _changeModeShortcutKey, value ?? string.Empty); }
    public string StreamingShortcutModifiers { get => _streamingShortcutModifiers; set => Set(ref _streamingShortcutModifiers, value ?? string.Empty); }
    public string StreamingShortcutKey { get => _streamingShortcutKey; set => Set(ref _streamingShortcutKey, value ?? string.Empty); }
    public string PushToTalkMode { get => _pushToTalkMode; set => Set(ref _pushToTalkMode, value ?? "Disabled"); }
    public string PushToTalkModifier { get => _pushToTalkModifier; set => Set(ref _pushToTalkModifier, value ?? "LeftAlt"); }
    public string PushToTalkShortcutModifiers { get => _pushToTalkShortcutModifiers; set => Set(ref _pushToTalkShortcutModifiers, value ?? "None"); }
    public string PushToTalkShortcutKey { get => _pushToTalkShortcutKey; set => Set(ref _pushToTalkShortcutKey, value ?? string.Empty); }
    public bool PushToTalkDoublePressLock { get => _pushToTalkDoublePressLock; set => Set(ref _pushToTalkDoublePressLock, value); }
    public bool PasteResultText { get => _pasteResultText; set => Set(ref _pasteResultText, value); }
    public bool RemoveFillerWords { get => _removeFillerWords; set => Set(ref _removeFillerWords, value); }
    public bool AutocapitalizeInsert { get => _autocapitalizeInsert; set => Set(ref _autocapitalizeInsert, value); }
    public bool RestoreClipboardAfterPaste { get => _restoreClipboardAfterPaste; set => Set(ref _restoreClipboardAfterPaste, value); }
    public double ClipboardRestoreDelaySeconds { get => _clipboardRestoreDelaySeconds; set => Set(ref _clipboardRestoreDelaySeconds, Math.Clamp(value, 0, 60)); }
    public bool StreamingEnabled { get => _streamingEnabled; set => Set(ref _streamingEnabled, value); }
    public string StreamingProvider { get => _streamingProvider; set => Set(ref _streamingProvider, NormalizeStreamingProvider(value)); }
    public string StreamingLanguage { get => _streamingLanguage; set => Set(ref _streamingLanguage, string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim()); }
    public string StreamingModel { get => _streamingModel; set => Set(ref _streamingModel, value?.Trim() ?? string.Empty); }
    public bool StreamingFastFormatting { get => _streamingFastFormatting; set => Set(ref _streamingFastFormatting, value); }
    public bool AutostartEnabled { get => _autostartEnabled; set => Set(ref _autostartEnabled, value); }
    public bool EnableSoundEffects { get => _enableSoundEffects; set => Set(ref _enableSoundEffects, value); }
    public bool AutoIncreaseMicVolume { get => _autoIncreaseMicVolume; set => Set(ref _autoIncreaseMicVolume, value); }
    public bool KeepMicrophoneWarm { get => _keepMicrophoneWarm; set => Set(ref _keepMicrophoneWarm, value); }
    public bool KeepAudioFiles { get => _keepAudioFiles; set => Set(ref _keepAudioFiles, value); }
    public bool AutoDeleteEnabled { get => _autoDeleteEnabled; set => Set(ref _autoDeleteEnabled, value); }
    public int AutoDeleteDaysOld { get => _autoDeleteDaysOld; set => Set(ref _autoDeleteDaysOld, Math.Clamp(value, 1, 365)); }
    public string AudioEnvironmentPolicy { get => _audioEnvironmentPolicy; set => Set(ref _audioEnvironmentPolicy, NormalizeAudioPolicy(value)); }
    public string DesktopContextStatus { get => _desktopContextStatus; set => Set(ref _desktopContextStatus, value ?? string.Empty); }
    public bool EnableErrorLogging { get => _enableErrorLogging; set => Set(ref _enableErrorLogging, value); }
    public string LocalWhisperBackend
    {
        get => _localWhisperBackend;
        set
        {
            if (Set(ref _localWhisperBackend, NormalizeWhisperBackend(value)))
            {
                Notify(nameof(WhisperRestartRequired));
                Notify(nameof(LocalWhisperRuntimeStatus));
            }
        }
    }
    public bool AllowLocalWhisperCpuFallback
    {
        get => _allowLocalWhisperCpuFallback;
        set
        {
            if (Set(ref _allowLocalWhisperCpuFallback, value))
            {
                Notify(nameof(WhisperRestartRequired));
                Notify(nameof(LocalWhisperRuntimeStatus));
            }
        }
    }
    public string LocalLlmRuntimeStatus { get; }
    public bool WhisperRestartRequired => _whisperBaselineCaptured
        && (LocalWhisperBackend != _processWhisperBackend
            || AllowLocalWhisperCpuFallback != _processWhisperCpuFallback);
    public string LocalWhisperRuntimeStatus => WhisperRestartRequired
        ? $"Current process capability: {_currentWhisperRuntimeStatus}. Restart HyperWhisper to activate this Whisper backend change."
        : $"Current process capability: {_currentWhisperRuntimeStatus}.";
    public IReadOnlyList<string> LocalWhisperBackends { get; } = ["auto", "cpu", "vulkan", "cuda12"];
    public IReadOnlyList<string> LocalLlmBackends { get; } = ["cpu", "vulkan", "cuda"];
    public IReadOnlyList<string> PushToTalkModes { get; } = ["Disabled", "Modifier", "CustomShortcut"];
    public IReadOnlyList<string> PushToTalkModifiers { get; } = Enum.GetNames<ModifierSide>();
    public IReadOnlyList<string> StreamingProviders { get; } = ["deepgram", "elevenlabs", "openai", "grok", "hyperwhisper"];
    public IReadOnlyList<string> AudioEnvironmentPolicies { get; } = ["unchanged", "duck", "mute"];
    public UiStatus Status { get; } = new();
    public ICommand SaveCommand { get; }
    public ICommand ResetShortcutsCommand { get; }
    public event EventHandler? LocalApiSettingsChanged;
    public event EventHandler? DesktopSettingsChanged;
    public event EventHandler? TelemetrySettingsChanged;
    public event EventHandler? StorageSettingsChanged;
    public void Load()
    {
        var result = _settings.Load();
        if (result.IsFailure) { Status.Failure(result.Error!.Code, result.Error.Message); return; }
        Language = _settings.Get("language", "auto") ?? "auto";
        LocalLlmBackend = _settings.Get("localLlmBackend", "cpu") ?? "cpu";
        AllowLocalLlmCpuFallback = _settings.Get("allowLocalLlmCpuFallback", true);
        LocalApiEnabled = _settings.Get("localApiEnabled", false);
        LocalApiPort = _settings.Get("localApiPort", 51671);
        ToggleShortcutModifiers = _settings.Get("toggleShortcutModifiers", "Control, Alt") ?? "Control, Alt";
        ToggleShortcutKey = _settings.Get("toggleShortcutKey", string.Empty) ?? string.Empty;
        CancelShortcutModifiers = _settings.Get("cancelShortcutModifiers", "None") ?? "None";
        CancelShortcutKey = _settings.Get("cancelShortcutKey", "Escape") ?? "Escape";
        ChangeModeShortcutModifiers = _settings.Get("changeModeShortcutModifiers", "Control, Shift") ?? "Control, Shift";
        ChangeModeShortcutKey = _settings.Get("changeModeShortcutKey", "Period") ?? "Period";
        StreamingShortcutModifiers = _settings.Get("streamingShortcutModifiers", "Control, Shift") ?? "Control, Shift";
        StreamingShortcutKey = _settings.Get("streamingShortcutKey", "Space") ?? "Space";
        PushToTalkMode = _settings.Get("pushToTalkMode", "Disabled") ?? "Disabled";
        PushToTalkModifier = _settings.Get("pushToTalkModifier", "LeftAlt") ?? "LeftAlt";
        PushToTalkShortcutModifiers = _settings.Get("pushToTalkShortcutModifiers", "None") ?? "None";
        PushToTalkShortcutKey = _settings.Get("pushToTalkShortcutKey", string.Empty) ?? string.Empty;
        PushToTalkDoublePressLock = _settings.Get("pushToTalkDoublePressLock", false);
        PasteResultText = _settings.Get("textOutput.pasteResultText", true);
        RemoveFillerWords = _settings.Get("textOutput.removeFillerWords", true);
        AutocapitalizeInsert = _settings.Get("textOutput.autocapitalizeInsert", true);
        RestoreClipboardAfterPaste = _settings.Get("textOutput.restoreClipboardAfterPaste", true);
        ClipboardRestoreDelaySeconds = _settings.Get("textOutput.clipboardRestoreDelaySeconds", 10d);
        StreamingEnabled = _settings.Get("streaming.enabled", false);
        StreamingProvider = _settings.Get("streaming.provider", "deepgram") ?? "deepgram";
        StreamingLanguage = _settings.Get("streaming.language", "auto") ?? "auto";
        StreamingModel = _settings.Get("streaming.deepgramModel", "nova-3-general") ?? "nova-3-general";
        StreamingFastFormatting = _settings.Get("streaming.fastFormatting", false);
        AutostartEnabled = _settings.Get("autostartEnabled", false);
        EnableSoundEffects = _settings.Get("general.enableSoundEffects", true);
        AutoIncreaseMicVolume = _settings.Get("autoIncreaseMicVolume", false);
        KeepMicrophoneWarm = _settings.Get("keepMicrophoneWarm", false);
        KeepAudioFiles = _settings.Get("storage.keepAudioFiles", true);
        AutoDeleteEnabled = _settings.Get("autoDeleteEnabled", false);
        AutoDeleteDaysOld = _settings.Get("autoDeleteDaysOld", 30);
        AudioEnvironmentPolicy = _settings.Get("audioEnvironmentPolicy", "unchanged") ?? "unchanged";
        EnableErrorLogging = _settings.Get("general.enableErrorLogging", true);
        LocalWhisperBackend = _settings.Get("localWhisperBackend", "auto") ?? "auto";
        AllowLocalWhisperCpuFallback = _settings.Get("allowLocalWhisperCpuFallback", true);
        if (!_whisperBaselineCaptured)
        {
            _processWhisperBackend = LocalWhisperBackend;
            _processWhisperCpuFallback = AllowLocalWhisperCpuFallback;
            _whisperBaselineCaptured = true;
            Notify(nameof(WhisperRestartRequired));
            Notify(nameof(LocalWhisperRuntimeStatus));
        }
        Status.Success("Settings loaded");
    }
    public void Save()
    {
        var shortcutValidation = ValidateShortcuts();
        if (shortcutValidation.IsFailure)
        {
            Status.Failure(shortcutValidation.Error!.Code, shortcutValidation.Error.Message);
            return;
        }
        _settings.Set("language", Language);
        _settings.Set("localLlmBackend", NormalizeBackend(LocalLlmBackend));
        _settings.Set("allowLocalLlmCpuFallback", AllowLocalLlmCpuFallback);
        _settings.Set("localApiEnabled", LocalApiEnabled);
        _settings.Set("localApiPort", LocalApiPort);
        _settings.Set("toggleShortcutModifiers", ToggleShortcutModifiers);
        _settings.Set("toggleShortcutKey", ToggleShortcutKey);
        _settings.Set("cancelShortcutModifiers", CancelShortcutModifiers);
        _settings.Set("cancelShortcutKey", CancelShortcutKey);
        _settings.Set("changeModeShortcutModifiers", ChangeModeShortcutModifiers);
        _settings.Set("changeModeShortcutKey", ChangeModeShortcutKey);
        _settings.Set("streamingShortcutModifiers", StreamingShortcutModifiers);
        _settings.Set("streamingShortcutKey", StreamingShortcutKey);
        _settings.Set("pushToTalkMode", PushToTalkMode);
        _settings.Set("pushToTalkModifier", PushToTalkModifier);
        _settings.Set("pushToTalkShortcutModifiers", PushToTalkShortcutModifiers);
        _settings.Set("pushToTalkShortcutKey", PushToTalkShortcutKey);
        _settings.Set("pushToTalkDoublePressLock", PushToTalkDoublePressLock);
        _settings.Set("textOutput.pasteResultText", PasteResultText);
        _settings.Set("textOutput.removeFillerWords", RemoveFillerWords);
        _settings.Set("textOutput.autocapitalizeInsert", AutocapitalizeInsert);
        _settings.Set("textOutput.restoreClipboardAfterPaste", RestoreClipboardAfterPaste);
        _settings.Set("textOutput.clipboardRestoreDelaySeconds", ClipboardRestoreDelaySeconds);
        _settings.Set("streaming.enabled", StreamingEnabled);
        _settings.Set("streaming.provider", NormalizeStreamingProvider(StreamingProvider));
        _settings.Set("streaming.language", StreamingLanguage);
        _settings.Set("streaming.deepgramModel", StreamingModel);
        _settings.Set("streaming.fastFormatting", StreamingFastFormatting);
        _settings.Set("autostartEnabled", AutostartEnabled);
        _settings.Set("general.enableSoundEffects", EnableSoundEffects);
        _settings.Set("autoIncreaseMicVolume", AutoIncreaseMicVolume);
        _settings.Set("keepMicrophoneWarm", KeepMicrophoneWarm);
        _settings.Set("storage.keepAudioFiles", KeepAudioFiles);
        _settings.Set("autoDeleteEnabled", AutoDeleteEnabled);
        _settings.Set("autoDeleteDaysOld", Math.Clamp(AutoDeleteDaysOld, 1, 365));
        _settings.Set("audioEnvironmentPolicy", NormalizeAudioPolicy(AudioEnvironmentPolicy));
        _settings.Set("general.enableErrorLogging", EnableErrorLogging);
        _settings.Set("localWhisperBackend", NormalizeWhisperBackend(LocalWhisperBackend));
        _settings.Set("allowLocalWhisperCpuFallback", AllowLocalWhisperCpuFallback);
        var result = _settings.Save();
        if (result.IsSuccess) { Status.Success("Settings saved"); LocalApiSettingsChanged?.Invoke(this, EventArgs.Empty); DesktopSettingsChanged?.Invoke(this, EventArgs.Empty); TelemetrySettingsChanged?.Invoke(this, EventArgs.Empty); StorageSettingsChanged?.Invoke(this, EventArgs.Empty); }
        else Status.Failure(result.Error!.Code, result.Error.Message);
    }

    public void ResetShortcuts()
    {
        ToggleShortcutModifiers = "Control, Alt";
        ToggleShortcutKey = string.Empty;
        CancelShortcutModifiers = "None";
        CancelShortcutKey = "Escape";
        ChangeModeShortcutModifiers = "Control, Shift";
        ChangeModeShortcutKey = "Period";
        StreamingShortcutModifiers = "Control, Shift";
        StreamingShortcutKey = "Space";
        PushToTalkMode = "Disabled";
        PushToTalkModifier = "LeftAlt";
        PushToTalkShortcutModifiers = "None";
        PushToTalkShortcutKey = string.Empty;
        PushToTalkDoublePressLock = false;
        Status.Success("Shortcut defaults restored; save settings to apply them");
    }

    private PlatformResult ValidateShortcuts()
    {
        if (PushToTalkMode is not ("Disabled" or "Modifier" or "CustomShortcut"))
            return PlatformResult.Failure("settings.push_to_talk_mode_invalid", "Select a valid push-to-talk mode.");
        var configured = new List<(string Name, GlobalShortcut Shortcut)>();
        foreach (var item in new[]
        {
            ("toggle", ToggleShortcutModifiers, ToggleShortcutKey),
            ("cancel", CancelShortcutModifiers, CancelShortcutKey),
            ("change mode", ChangeModeShortcutModifiers, ChangeModeShortcutKey),
            ("streaming", StreamingShortcutModifiers, StreamingShortcutKey),
        })
        {
            var parsed = ParseShortcut(item.Item2, item.Item3);
            if (parsed.IsFailure) return PlatformResult.Failure(parsed.Error!.Code, $"{item.Item1}: {parsed.Error.Message}");
            if (parsed.Value is { } shortcut) configured.Add((item.Item1, shortcut));
        }

        if (PushToTalkMode == "CustomShortcut")
        {
            var parsed = ParseShortcut(PushToTalkShortcutModifiers, PushToTalkShortcutKey);
            if (parsed.IsFailure || parsed.Value is null)
                return PlatformResult.Failure("settings.shortcut_invalid", "push-to-talk: enter a valid assigned shortcut.");
            configured.Add(("push-to-talk", parsed.Value));
        }
        else if (PushToTalkMode == "Modifier")
        {
            if (!Enum.TryParse<ModifierSide>(PushToTalkModifier, true, out var modifier))
                return PlatformResult.Failure("settings.push_to_talk_modifier_invalid", "Select a valid push-to-talk modifier.");
            configured.Add(("push-to-talk", modifier switch
            {
                ModifierSide.Control => new GlobalShortcut(ShortcutModifiers.Control),
                ModifierSide.Alt => new GlobalShortcut(ShortcutModifiers.Alt),
                ModifierSide.Shift => new GlobalShortcut(ShortcutModifiers.Shift),
                ModifierSide.Meta => new GlobalShortcut(ShortcutModifiers.Meta),
                _ => new GlobalShortcut(ShortcutModifiers.None, new ShortcutKeyCode(modifier.ToString())),
            }));
        }

        for (var left = 0; left < configured.Count; left++)
        for (var right = left + 1; right < configured.Count; right++)
            if (configured[left].Shortcut.Modifiers == configured[right].Shortcut.Modifiers
                && string.Equals(configured[left].Shortcut.Key.Value, configured[right].Shortcut.Key.Value, StringComparison.OrdinalIgnoreCase))
                return PlatformResult.Failure("settings.shortcut_conflict",
                    $"{configured[left].Name} and {configured[right].Name} must use different shortcuts.");
        return PlatformResult.Success();
    }

    private static PlatformResult<GlobalShortcut?> ParseShortcut(string? modifiersText, string? keyText)
    {
        var text = string.IsNullOrWhiteSpace(modifiersText) ? "None" : modifiersText.Trim();
        if (!Enum.TryParse<ShortcutModifiers>(text, true, out var modifiers)
            || (modifiers & ~(ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Shift | ShortcutModifiers.Meta)) != 0)
            return PlatformResult<GlobalShortcut?>.Failure("settings.shortcut_modifiers_invalid", "Use only Control, Alt, Shift, or Meta modifiers.");
        var key = keyText?.Trim() ?? string.Empty;
        if (modifiers == ShortcutModifiers.None && key.Length == 0)
            return PlatformResult<GlobalShortcut?>.Success(null);
        if (key.Length == 0 && CountModifiers(modifiers) == 1)
            return PlatformResult<GlobalShortcut?>.Failure("settings.shortcut_bare_modifier", "A modifier-only shortcut needs at least two modifiers.");
        return PlatformResult<GlobalShortcut?>.Success(new GlobalShortcut(modifiers, new ShortcutKeyCode(key)));
    }

    private static int CountModifiers(ShortcutModifiers modifiers)
    {
        var value = (uint)modifiers;
        var count = 0;
        while (value != 0) { count += (int)(value & 1); value >>= 1; }
        return count;
    }

    private static string NormalizeBackend(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "vulkan" => "vulkan",
        "cuda" => "cuda",
        _ => "cpu",
    };

    private static string NormalizeWhisperBackend(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "cpu" => "cpu",
        "vulkan" => "vulkan",
        "cuda" or "cuda12" => "cuda12",
        _ => "auto",
    };

    private static string NormalizeStreamingProvider(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "elevenlabs" => "elevenlabs", "openai" => "openai", "grok" or "xai" => "grok",
        "hyperwhisper" or "hyperwhispercloud" => "hyperwhisper", _ => "deepgram",
    };

    private static string NormalizeAudioPolicy(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "duck" => "duck", "mute" => "mute", _ => "unchanged",
    };
}

public sealed class ManagedModelViewModel : ViewModelBase
{
    private bool _installed;
    private double _progress;
    private string _status = "Not installed";
    public ManagedModelViewModel(ManagedModel model, bool installed) { Model = model; _installed = installed; _status = installed ? "Installed" : "Not installed"; }
    public ManagedModel Model { get; }
    public string Id => Model.Id;
    public string DisplayName => Model.DisplayName;
    public string Kind => Model.Kind.ToString();
    public string Size => $"{Model.ApproximateSizeBytes / 1_000_000d:F0} MB";
    public bool Installed { get => _installed; set => Set(ref _installed, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
}

public sealed class ModelLibraryViewModel : ViewModelBase, IDisposable
{
    private readonly PortableModelManager _manager;
    private CancellationTokenSource? _download;
    private ManagedModelViewModel? _selected;
    public ModelLibraryViewModel(PortableModelManager manager)
    {
        _manager = manager;
        foreach (var model in PortableModelCatalog.All) Items.Add(new(model, manager.IsInstalled(model)));
        DownloadCommand = new AsyncCommand(_ => DownloadAsync(), _ => Selected is not null && !Selected.Installed);
        CancelCommand = new AsyncCommand(_ => { _download?.Cancel(); return Task.CompletedTask; }, _ => _download is not null);
        DeleteCommand = new AsyncCommand(_ => DeleteAsync(), _ => Selected?.Installed == true);
        Selected = Items.FirstOrDefault(item => item.Model.IsRecommended) ?? Items.FirstOrDefault();
    }
    public ObservableCollection<ManagedModelViewModel> Items { get; } = new();
    public ManagedModelViewModel? Selected { get => _selected; set { if (Set(ref _selected, value)) RaiseCommands(); } }
    public UiStatus Status { get; } = new();
    public ICommand DownloadCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DeleteCommand { get; }
    public async Task DownloadAsync()
    {
        var target = Selected;
        if (target is null) return;
        _download?.Dispose();
        var download = new CancellationTokenSource();
        _download = download;
        RaiseCommands();
        target.Status = "Downloading…";
        var progress = new Progress<ModelDownloadProgress>(value =>
        {
            target.Progress = value.Fraction ?? 0;
            target.Status = value.TotalBytes is { } total ? $"{value.BytesReceived / 1_000_000d:F0} / {total / 1_000_000d:F0} MB" : $"{value.BytesReceived / 1_000_000d:F0} MB";
        });
        try
        {
            var result = await _manager.DownloadAsync(target.Model, progress, download.Token);
            target.Installed = result.IsSuccess;
            target.Status = result.IsSuccess ? "Installed" : result.Failure!.Message;
            if (result.IsSuccess) Status.Success("Model installed");
            else Status.Failure($"models.{result.Failure!.Code.ToString().ToLowerInvariant()}", result.Failure.Message);
        }
        catch (OperationCanceledException) { target.Status = "Download cancelled"; Status.Failure("models.cancelled", "Model download cancelled."); }
        catch (HttpRequestException) { target.Status = "Network download failed"; Status.Failure("models.network_failed", "The model download could not reach its source."); }
        catch (Exception) { target.Status = "Download failed"; Status.Failure("models.download_failed", "The model download failed unexpectedly."); }
        finally
        {
            download.Dispose();
            if (ReferenceEquals(_download, download)) _download = null;
            RaiseCommands();
        }
    }
    public Task DeleteAsync()
    {
        if (Selected is null) return Task.CompletedTask;
        var result = _manager.Delete(Selected.Model);
        if (result.IsSuccess) { Selected.Installed = false; Selected.Progress = 0; Selected.Status = "Not installed"; Status.Success("Model deleted"); }
        else Status.Failure("models.delete_failed", result.Failure!.Message);
        RaiseCommands(); return Task.CompletedTask;
    }
    public void Dispose() { _download?.Cancel(); _download?.Dispose(); }
    private void RaiseCommands() { ((AsyncCommand)DownloadCommand).RaiseCanExecuteChanged(); ((AsyncCommand)CancelCommand).RaiseCanExecuteChanged(); ((AsyncCommand)DeleteCommand).RaiseCanExecuteChanged(); }
}

public sealed class BackupViewModel : ViewModelBase
{
    private readonly ApplicationBackupService _service;
    private string _path = string.Empty;
    public BackupViewModel(ApplicationBackupService service)
    {
        _service = service;
        ExportCommand = new AsyncCommand(_ => ExportAsync());
        ImportCommand = new AsyncCommand(_ => ImportAsync());
    }
    public string Path { get => _path; set => Set(ref _path, value); }
    public UiStatus Status { get; } = new();
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }
    public event EventHandler? Imported;

    public async Task ExportAsync(CancellationToken cancellationToken = default)
    {
        if (!System.IO.Path.IsPathFullyQualified(Path)) { Status.Failure("backup.path_required", "Choose a backup destination."); return; }
        try { await File.WriteAllTextAsync(Path, await _service.ExportAsync(cancellationToken), cancellationToken); Status.Success("Universal backup exported"); }
        catch (Exception) { Status.Failure("backup.export_failed", "Could not export the universal backup."); }
    }
    public async Task ImportAsync(CancellationToken cancellationToken = default)
    {
        if (!System.IO.Path.IsPathFullyQualified(Path)) { Status.Failure("backup.path_required", "Choose a backup file."); return; }
        try
        {
            var result = await _service.ImportAsync(await File.ReadAllTextAsync(Path, cancellationToken), cancellationToken);
            if (result.IsFailure) Status.Failure(result.Error!.Code, result.Error.Message);
            else { Status.Success("Universal backup imported"); Imported?.Invoke(this, EventArgs.Empty); }
        }
        catch (Exception) { Status.Failure("backup.import_failed", "Could not import the universal backup."); }
    }
}

public sealed record CredentialAccount(string Account, string DisplayName, bool IsPresent);

public sealed class CredentialManagementViewModel : ViewModelBase
{
    private const string Resource = "HyperWhisper";
    private readonly ICredentialStore _store;
    private CredentialAccount? _selected;
    private string _secret = string.Empty;
    public CredentialManagementViewModel(ICredentialStore store)
    {
        _store = store;
        SaveCommand = new AsyncCommand(_ => SaveAsync());
        DeleteCommand = new AsyncCommand(_ => DeleteAsync());
        Refresh();
    }
    public ObservableCollection<CredentialAccount> Items { get; } = new();
    public CredentialAccount? Selected { get => _selected; set => Set(ref _selected, value); }
    public string Secret { get => _secret; set => Set(ref _secret, value); }
    public UiStatus Status { get; } = new();
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
    public void Refresh()
    {
        Items.Clear();
        foreach (var pair in Accounts)
        {
            var read = _store.Read(Resource, pair.Account);
            var present = read.IsSuccess && read.Value is { Length: > 0 } bytes;
            if (read.Value is { } sensitive) CryptographicOperations.ZeroMemory(sensitive);
            Items.Add(new(pair.Account, pair.Name, present));
        }
        Selected = Items.FirstOrDefault();
    }
    public Task SaveAsync()
    {
        if (Selected is null || string.IsNullOrWhiteSpace(Secret)) { Status.Failure("credentials.value_required", "Select a credential and enter its value."); return Task.CompletedTask; }
        var bytes = Encoding.UTF8.GetBytes(Secret.Trim());
        try
        {
            var result = _store.Write(Resource, Selected.Account, bytes);
            Secret = string.Empty;
            if (result.IsFailure) Status.Failure(result.Error!.Code, result.Error.Message);
            else { Refresh(); Status.Success("Credential saved securely"); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        return Task.CompletedTask;
    }
    public Task DeleteAsync()
    {
        if (Selected is null) { Status.Failure("credentials.selection_required", "Select a credential."); return Task.CompletedTask; }
        var result = _store.Delete(Resource, Selected.Account);
        Secret = string.Empty;
        if (result.IsFailure) Status.Failure(result.Error!.Code, result.Error.Message);
        else { Refresh(); Status.Success("Credential deleted"); }
        return Task.CompletedTask;
    }
    private static readonly (string Account, string Name)[] Accounts =
    [
        ("OpenAIApiKey", "OpenAI API key"), ("AnthropicApiKey", "Anthropic API key"),
        ("CerebrasApiKey", "Cerebras API key"), ("GroqApiKey", "Groq API key"),
        ("DeepgramApiKey", "Deepgram API key"), ("AssemblyAIApiKey", "AssemblyAI API key"),
        ("ElevenLabsApiKey", "ElevenLabs API key"), ("MistralApiKey", "Mistral API key"),
        ("SonioxApiKey", "Soniox API key"), ("GeminiApiKey", "Gemini API key"),
        ("GrokApiKey", "xAI/Grok API key")
    ];
}

public sealed class ApplicationShellViewModel : ViewModelBase, IDisposable
{
    private readonly ApplicationDb _database;
    private readonly CancellationTokenSource _lifetime = new();
    private object? _currentPage;
    private string _pageTitle = "Home";
    private bool _initialized;
    private bool _disposed;

    public ApplicationShellViewModel(
        ApplicationDb database,
        PortableSettingsService settings,
        TranscriptionWorkflow? transcriptionWorkflow = null,
        string localLlmRuntimeStatus = "Local LLM runtime not connected",
        PortableModelManager? modelManager = null,
        IAudioPlaybackService? playback = null,
        DurableAudioImportService? audioImport = null,
        IAppPaths? paths = null,
        ICredentialStore? credentials = null,
        string localWhisperRuntimeStatus = "Local Whisper runtime not connected",
        PortableFileTranscriptionPreflight? filePreflight = null,
        PortableCloudAccountService? cloudAccount = null,
        IDeviceIdentityProvider? deviceIdentity = null,
        string deviceName = "Linux device",
        Func<Uri, PlatformResult>? openAccountUri = null)
    {
        _database = database;
        var historyRepository = new HistoryRepository(database, paths);
        var vocabularyRepository = new VocabularyRepository(database);
        var modeRepository = new ModeRepository(database);
        Vocabulary = new VocabularyViewModel(vocabularyRepository);
        Modes = new ModesViewModel(modeRepository, settings, credentials);
        Settings = new SettingsViewModel(settings, localLlmRuntimeStatus, localWhisperRuntimeStatus);
        History = new HistoryViewModel(historyRepository, playback, transcriptionWorkflow is null ? null : async (item, token) =>
        {
            var audioPath = item.AudioFilePath;
            if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath)) throw new FileNotFoundException();
            var retryMode = Modes.Items.FirstOrDefault(mode => item.ModeId is { } modeId && mode.Id == modeId)
                ?? Modes.Items.FirstOrDefault(mode => string.Equals(mode.Name, item.Mode, StringComparison.OrdinalIgnoreCase))
                ?? Modes.Items.FirstOrDefault(mode => mode.IsDefault)
                ?? Modes.Selected;
            return await transcriptionWorkflow!.RetryTranscriptAsync(item.Id, new(
                Language: Settings.Language,
                ModeName: retryMode?.Name ?? item.Mode,
                ModeId: retryMode?.Id ?? item.ModeId,
                SelectedMode: retryMode,
                Vocabulary: Vocabulary.Items.Select(term => term.Word).ToArray(),
                VocabularyReplacements: BuildVocabularyReplacements(),
                OutputOptions: BuildOutputOptions(retryMode),
                PasteResultText: Settings.PasteResultText), token);
        });
        Models = modelManager is null ? null : new ModelLibraryViewModel(modelManager);
        Backup = new BackupViewModel(new ApplicationBackupService(database, settings));
        Credentials = credentials is null ? null : new CredentialManagementViewModel(credentials);
        Account = cloudAccount is not null && deviceIdentity is not null && openAccountUri is not null
            ? new CloudAccountViewModel(cloudAccount, deviceIdentity, deviceName, openAccountUri)
            : null;
        Recording = transcriptionWorkflow is null ? null : new TranscriptionWorkflowViewModel(
            transcriptionWorkflow,
            () => CreateTranscriptionRequest(Modes.Selected), audioImport, filePreflight);
        Home = new HomeViewModel(historyRepository, vocabularyRepository, modeRepository, Recording);
        if (Recording is not null) Recording.TranscriptionSaved += OnTranscriptionSaved;
        Backup.Imported += OnBackupImported;
        _currentPage = Home;
    }

    public HomeViewModel Home { get; }
    public HistoryViewModel History { get; }
    public VocabularyViewModel Vocabulary { get; }
    public ModesViewModel Modes { get; }
    public SettingsViewModel Settings { get; }
    public ModelLibraryViewModel? Models { get; }
    public BackupViewModel Backup { get; }
    public CredentialManagementViewModel? Credentials { get; }
    public CloudAccountViewModel? Account { get; }
    public TranscriptionWorkflowViewModel? Recording { get; }
    public UiStatus Status { get; } = new();
    public object? CurrentPage { get => _currentPage; private set => Set(ref _currentPage, value); }
    public string PageTitle { get => _pageTitle; private set => Set(ref _pageTitle, value); }

    public TranscriptionWorkflowRequest CreateTranscriptionRequest(
        Mode? mode,
        ApplicationContextSnapshot? applicationContext = null,
        PortableCursorContext cursorContext = PortableCursorContext.Unknown) => new(
        Language: Settings.Language,
        ModeName: mode?.Name,
        ModeId: mode?.Id,
        SelectedMode: mode,
        Vocabulary: Vocabulary.Items.Select(item => item.Word).ToArray(),
        ApplicationContext: applicationContext,
        VocabularyReplacements: BuildVocabularyReplacements(),
        // Mode.CustomVocabulary contains prompt hints only. Linux does not yet
        // have a persisted mode-level word/replacement pair model.
        ModeVocabularyReplacements: [],
        OutputOptions: BuildOutputOptions(mode),
        PasteResultText: Settings.PasteResultText,
        CursorContext: cursorContext);

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        Status.Busy("Preparing local database…");
        try
        {
            await _database.InitializeAsync(_lifetime.Token);
            await new HistoryRepository(_database).FailOrphanedProcessingAsync(_lifetime.Token);
            Settings.Load();
            await Task.WhenAll(Home.RefreshAsync(_lifetime.Token), History.RefreshAsync(_lifetime.Token), Vocabulary.RefreshAsync(_lifetime.Token), Modes.RefreshAsync(_lifetime.Token));
            if (Account is not null) await Account.LoadAsync(_lifetime.Token);
            if (Settings.Status.HasError || Home.Status.HasError || History.Status.HasError
                || Vocabulary.Status.HasError || Modes.Status.HasError)
            {
                Status.Failure("app.data_load_failed", "Some local application data could not be loaded.");
                return;
            }
            _initialized = true;
            Status.Success("Ready");
        }
        catch (OperationCanceledException) { Status.Failure("app.cancelled", "Startup cancelled"); }
        catch (Exception) { Status.Failure("app.startup_failed", "Could not prepare local application data."); }
    }

    public void Navigate(string pageId)
    {
        (string Title, object Page) selection = pageId switch
        {
            "home" => ("Home", (object)Home),
            "history" => ("History", History),
            "vocabulary" => ("Vocabulary", Vocabulary),
            "modes" => ("Modes", Modes),
            "settings" => ("Settings", Settings),
            "models" when Models is not null => ("Models", Models),
            "backup" => ("Backup", Backup),
            "credentials" when Credentials is not null => ("Credentials", Credentials),
            "account" when Account is not null => ("Cloud account", Account),
            _ => throw new ArgumentException("Unknown navigation page.", nameof(pageId))
        };
        PageTitle = selection.Title;
        CurrentPage = selection.Page;
        Status.Success($"{PageTitle} ready");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (Recording is not null) Recording.TranscriptionSaved -= OnTranscriptionSaved;
        Backup.Imported -= OnBackupImported;
        Models?.Dispose();
        Recording?.Dispose();
        _lifetime.Dispose();
    }

    private async void OnBackupImported(object? sender, EventArgs e)
    {
        try
        {
            Settings.Load();
            await Task.WhenAll(Home.RefreshAsync(_lifetime.Token), History.RefreshAsync(_lifetime.Token), Vocabulary.RefreshAsync(_lifetime.Token), Modes.RefreshAsync(_lifetime.Token));
        }
        catch (Exception) { Status.Failure("backup.refresh_failed", "Backup imported, but the views could not be refreshed."); }
    }

    private async void OnTranscriptionSaved(object? sender, EventArgs e)
    {
        try
        {
            if (_disposed) return;
            var cancellationToken = _lifetime.Token;
            await Task.WhenAll(History.RefreshAsync(cancellationToken), Home.RefreshAsync(cancellationToken));
            if (History.Status.HasError || Home.Status.HasError)
                Status.Failure("app.history_refresh_failed", "The transcription was saved, but the library view could not be refreshed.");
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (ObjectDisposedException) when (_disposed) { }
        catch (Exception)
        {
            if (!_disposed) Status.Failure("app.history_refresh_failed", "The transcription was saved, but the library view could not be refreshed.");
        }
    }

    private PortableVocabularyReplacement[] BuildVocabularyReplacements() => Vocabulary.Items
        .Where(item => !string.IsNullOrWhiteSpace(item.Replacement))
        .Select(item => new PortableVocabularyReplacement(item.Word, item.Replacement!))
        .ToArray();

    private SpeechOutputProcessingOptions BuildOutputOptions(Mode? mode) => new(
        RemoveFillerWords: Settings.RemoveFillerWords,
        RemoveTrailingPeriod: mode?.RemoveTrailingPeriod == true,
        AppendTrailingSpace: true,
        AutocapitalizeInsert: Settings.AutocapitalizeInsert,
        Punctuation: mode?.Punctuation ?? true,
        Capitalization: mode?.Capitalization ?? true,
        ProfanityFilter: mode?.ProfanityFilter ?? false);
}
