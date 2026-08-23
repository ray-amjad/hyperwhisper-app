using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;

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
    private bool _disposed;

    public TranscriptionWorkflowViewModel(
        TranscriptionWorkflow workflow,
        Func<TranscriptionWorkflowRequest> requestFactory)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _requestFactory = requestFactory ?? throw new ArgumentNullException(nameof(requestFactory));
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
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand TranscribeFileCommand { get; }
    public ICommand RefreshDevicesCommand { get; }
    public event EventHandler? TranscriptionSaved;

    public void RefreshDevices() => _workflow.RefreshDevices();
    public Task StartAsync(CancellationToken cancellationToken = default) => _workflow.StartRecordingAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken = default) => _workflow.StopAndTranscribeAsync(_requestFactory(), cancellationToken);
    public Task CancelAsync() => _workflow.CancelAsync();
    public Task TranscribeFileAsync(CancellationToken cancellationToken = default) =>
        _workflow.TranscribeFileAsync(FilePath, _requestFactory(), cancellationToken);

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
        State = snapshot.State.ToString();
        Message = snapshot.Message;
        ErrorCode = snapshot.ErrorCode;
        CanStartRecording = snapshot.CanStartRecording;
        CanStop = snapshot.CanStop;
        CanCancel = snapshot.CanCancel;
        CanTranscribeFile = snapshot.CanTranscribeFile;
        ((AsyncCommand)StartCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)StopCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)CancelCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)TranscribeFileCommand).RaiseCanExecuteChanged();
        if (completedNow) TranscriptionSaved?.Invoke(this, EventArgs.Empty);
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
    private Transcript? _selected;
    public HistoryViewModel(HistoryRepository repository)
    {
        _repository = repository;
        RefreshCommand = new AsyncCommand(_ => RefreshAsync());
        DeleteCommand = new AsyncCommand(item => DeleteAsync(item as Transcript), item => item is Transcript);
    }
    public ObservableCollection<Transcript> Items { get; } = new();
    public Transcript? Selected { get => _selected; set => Set(ref _selected, value); }
    public UiStatus Status { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand DeleteCommand { get; }

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

    public async Task DeleteAsync(Transcript? transcript, CancellationToken cancellationToken = default)
    {
        if (transcript == null) { Status.Failure("history.no_selection", "Select a transcript to delete."); return; }
        try
        {
            if (!await _repository.DeleteAsync(transcript.Id, cancellationToken))
            { Status.Failure("history.not_found", "The transcript no longer exists."); return; }
            Items.Remove(transcript);
            Selected = Items.FirstOrDefault();
            Status.Success("Transcript deleted");
        }
        catch (OperationCanceledException) { Status.Failure("history.cancelled", "Delete cancelled"); }
        catch (Exception) { Status.Failure("history.delete_failed", "Could not delete the transcript."); }
    }
}

public sealed class VocabularyViewModel : ViewModelBase
{
    private readonly VocabularyRepository _repository;
    private string _word = string.Empty;
    private string _replacement = string.Empty;
    public VocabularyViewModel(VocabularyRepository repository)
    {
        _repository = repository;
        AddCommand = new AsyncCommand(_ => AddAsync());
        DeleteCommand = new AsyncCommand(item => DeleteAsync(item as VocabularyItem), item => item is VocabularyItem);
    }
    public ObservableCollection<VocabularyItem> Items { get; } = new();
    public string Word { get => _word; set => Set(ref _word, value); }
    public string Replacement { get => _replacement; set => Set(ref _replacement, value); }
    public UiStatus Status { get; } = new();
    public ICommand AddCommand { get; }
    public ICommand DeleteCommand { get; }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try { Items.Clear(); foreach (var item in await _repository.ListAsync(cancellationToken)) Items.Add(item); Status.Success($"{Items.Count} term(s)"); }
        catch (Exception) { Status.Failure("vocabulary.load_failed", "Could not load vocabulary."); }
    }
    public async Task AddAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Word)) { Status.Failure("vocabulary.word_required", "Enter a word or phrase."); return; }
        var item = new VocabularyItem { Word = Word.Trim(), Replacement = string.IsNullOrWhiteSpace(Replacement) ? null : Replacement.Trim(), SortOrder = Items.Count };
        try { await _repository.AddAsync(item, cancellationToken); Items.Add(item); Word = string.Empty; Replacement = string.Empty; Status.Success("Vocabulary added"); }
        catch (Exception) { Status.Failure("vocabulary.add_failed", "Could not add the vocabulary item."); }
    }
    public async Task DeleteAsync(VocabularyItem? item, CancellationToken cancellationToken = default)
    {
        if (item == null) { Status.Failure("vocabulary.no_selection", "Select a vocabulary item to delete."); return; }
        try { if (await _repository.DeleteAsync(item.Id, cancellationToken)) { Items.Remove(item); Status.Success("Vocabulary deleted"); } else Status.Failure("vocabulary.not_found", "The vocabulary item no longer exists."); }
        catch (Exception) { Status.Failure("vocabulary.delete_failed", "Could not delete the vocabulary item."); }
    }
}

public sealed class ModesViewModel : ViewModelBase
{
    private readonly ModeRepository _repository;
    private Mode? _selected;
    private string _name = string.Empty;
    private string _language = "en";
    private bool _localPostProcessingEnabled;
    private string _localPostProcessingModel = string.Empty;
    private string _userSystemPrompt = string.Empty;
    public ModesViewModel(ModeRepository repository)
    {
        _repository = repository;
        SaveCommand = new AsyncCommand(_ => SaveAsync());
        NewCommand = new AsyncCommand(_ =>
        {
            Selected = null;
            Name = string.Empty;
            Language = "en";
            LocalPostProcessingEnabled = false;
            LocalPostProcessingModel = string.Empty;
            UserSystemPrompt = string.Empty;
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
            Name = value.Name;
            Language = value.Language;
            LocalPostProcessingEnabled = value.PostProcessingMode == 2
                && string.Equals(value.PostProcessingProvider, "local_llm", StringComparison.OrdinalIgnoreCase);
            LocalPostProcessingModel = value.LocalPostProcessingModel ?? string.Empty;
            UserSystemPrompt = value.UserSystemPrompt ?? string.Empty;
        }
    }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Language { get => _language; set => Set(ref _language, value); }
    public bool LocalPostProcessingEnabled { get => _localPostProcessingEnabled; set => Set(ref _localPostProcessingEnabled, value); }
    public string LocalPostProcessingModel { get => _localPostProcessingModel; set => Set(ref _localPostProcessingModel, value); }
    public string UserSystemPrompt { get => _userSystemPrompt; set => Set(ref _userSystemPrompt, value); }
    public UiStatus Status { get; } = new();
    public ICommand SaveCommand { get; }
    public ICommand NewCommand { get; }
    public ICommand DeleteCommand { get; }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try { Items.Clear(); foreach (var item in await _repository.ListAsync(cancellationToken)) Items.Add(item); Selected = Items.FirstOrDefault(); Status.Success($"{Items.Count} mode(s)"); }
        catch (Exception) { Status.Failure("modes.load_failed", "Could not load modes."); }
    }
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Name)) { Status.Failure("modes.name_required", "Enter a mode name."); return; }
        if (LocalPostProcessingEnabled
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
        var mode = Selected ?? new Mode { SortOrder = Items.Count };
        mode.Name = Name.Trim();
        mode.Language = string.IsNullOrWhiteSpace(Language) ? "auto" : Language.Trim();
        mode.PostProcessingMode = LocalPostProcessingEnabled ? 2 : 0;
        if (LocalPostProcessingEnabled) mode.PostProcessingProvider = "local_llm";
        mode.LocalPostProcessingModel = string.IsNullOrWhiteSpace(LocalPostProcessingModel)
            ? null
            : LocalPostProcessingModel.Trim();
        mode.UserSystemPrompt = string.IsNullOrWhiteSpace(UserSystemPrompt) ? null : UserSystemPrompt.Trim();
        mode.ModifiedDate = DateTime.UtcNow;
        try
        {
            await _repository.UpsertAsync(mode, cancellationToken);
            var existingIndex = Items.IndexOf(mode);
            if (existingIndex < 0) Items.Add(mode);
            else Items[existingIndex] = mode;
            Selected = mode;
            Status.Success("Mode saved");
        }
        catch (Exception) { Status.Failure("modes.save_failed", "Could not save the mode."); }
    }
    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        if (Selected == null) { Status.Failure("modes.no_selection", "Select a mode to delete."); return; }
        var selected = Selected;
        try { if (await _repository.DeleteAsync(selected.Id, cancellationToken)) { Items.Remove(selected); Selected = Items.FirstOrDefault(); Status.Success("Mode deleted"); } else Status.Failure("modes.not_found", "The mode no longer exists."); }
        catch (Exception) { Status.Failure("modes.delete_failed", "Could not delete the mode."); }
    }
}

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly PortableSettingsService _settings;
    private string _language = "auto";
    private string _localLlmBackend = "cpu";
    private bool _allowLocalLlmCpuFallback = true;
    public SettingsViewModel(PortableSettingsService settings, string localLlmRuntimeStatus = "Local LLM runtime not connected")
    {
        _settings = settings;
        SaveCommand = new AsyncCommand(_ => { Save(); return Task.CompletedTask; });
        LocalLlmRuntimeStatus = localLlmRuntimeStatus;
    }
    public string Language { get => _language; set => Set(ref _language, value); }
    public string LocalLlmBackend { get => _localLlmBackend; set => Set(ref _localLlmBackend, NormalizeBackend(value)); }
    public bool AllowLocalLlmCpuFallback { get => _allowLocalLlmCpuFallback; set => Set(ref _allowLocalLlmCpuFallback, value); }
    public string LocalLlmRuntimeStatus { get; }
    public IReadOnlyList<string> LocalLlmBackends { get; } = ["cpu", "vulkan", "cuda"];
    public UiStatus Status { get; } = new();
    public ICommand SaveCommand { get; }
    public void Load()
    {
        var result = _settings.Load();
        if (result.IsFailure) { Status.Failure(result.Error!.Code, result.Error.Message); return; }
        Language = _settings.Get("language", "auto") ?? "auto";
        LocalLlmBackend = _settings.Get("localLlmBackend", "cpu") ?? "cpu";
        AllowLocalLlmCpuFallback = _settings.Get("allowLocalLlmCpuFallback", true);
        Status.Success("Settings loaded");
    }
    public void Save()
    {
        _settings.Set("language", Language);
        _settings.Set("localLlmBackend", NormalizeBackend(LocalLlmBackend));
        _settings.Set("allowLocalLlmCpuFallback", AllowLocalLlmCpuFallback);
        var result = _settings.Save();
        if (result.IsSuccess) Status.Success("Settings saved");
        else Status.Failure(result.Error!.Code, result.Error.Message);
    }

    private static string NormalizeBackend(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "vulkan" => "vulkan",
        "cuda" => "cuda",
        _ => "cpu",
    };
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
        string localLlmRuntimeStatus = "Local LLM runtime not connected")
    {
        _database = database;
        var historyRepository = new HistoryRepository(database);
        var vocabularyRepository = new VocabularyRepository(database);
        var modeRepository = new ModeRepository(database);
        History = new HistoryViewModel(historyRepository);
        Vocabulary = new VocabularyViewModel(vocabularyRepository);
        Modes = new ModesViewModel(modeRepository);
        Settings = new SettingsViewModel(settings, localLlmRuntimeStatus);
        Recording = transcriptionWorkflow is null ? null : new TranscriptionWorkflowViewModel(
            transcriptionWorkflow,
            () => new TranscriptionWorkflowRequest(
                Settings.Language,
                Modes.Selected?.Name,
                Modes.Selected?.Id,
                Modes.Selected));
        Home = new HomeViewModel(historyRepository, vocabularyRepository, modeRepository, Recording);
        if (Recording is not null) Recording.TranscriptionSaved += OnTranscriptionSaved;
        _currentPage = Home;
    }

    public HomeViewModel Home { get; }
    public HistoryViewModel History { get; }
    public VocabularyViewModel Vocabulary { get; }
    public ModesViewModel Modes { get; }
    public SettingsViewModel Settings { get; }
    public TranscriptionWorkflowViewModel? Recording { get; }
    public UiStatus Status { get; } = new();
    public object? CurrentPage { get => _currentPage; private set => Set(ref _currentPage, value); }
    public string PageTitle { get => _pageTitle; private set => Set(ref _pageTitle, value); }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        Status.Busy("Preparing local database…");
        try
        {
            await _database.MigrateAsync(_lifetime.Token);
            await new HistoryRepository(_database).FailOrphanedProcessingAsync(_lifetime.Token);
            Settings.Load();
            await Task.WhenAll(Home.RefreshAsync(_lifetime.Token), History.RefreshAsync(_lifetime.Token), Vocabulary.RefreshAsync(_lifetime.Token), Modes.RefreshAsync(_lifetime.Token));
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
        Recording?.Dispose();
        _lifetime.Dispose();
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
}
