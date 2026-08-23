using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.Data.Entities;

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

    public HomeViewModel(HistoryRepository history, VocabularyRepository vocabulary, ModeRepository modes)
    {
        _history = history; _vocabulary = vocabulary; _modes = modes;
        RefreshCommand = new AsyncCommand(_ => RefreshAsync());
    }

    public UiStatus Status { get; } = new();
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
    public ModesViewModel(ModeRepository repository)
    {
        _repository = repository;
        SaveCommand = new AsyncCommand(_ => SaveAsync());
        NewCommand = new AsyncCommand(_ => { Selected = null; Name = string.Empty; Language = "en"; return Task.CompletedTask; });
        DeleteCommand = new AsyncCommand(_ => DeleteAsync());
    }
    public ObservableCollection<Mode> Items { get; } = new();
    public Mode? Selected { get => _selected; set { if (Set(ref _selected, value) && value != null) { Name = value.Name; Language = value.Language; } } }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Language { get => _language; set => Set(ref _language, value); }
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
        var mode = Selected ?? new Mode { SortOrder = Items.Count };
        mode.Name = Name.Trim(); mode.Language = string.IsNullOrWhiteSpace(Language) ? "auto" : Language.Trim(); mode.ModifiedDate = DateTime.UtcNow;
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
    public SettingsViewModel(PortableSettingsService settings)
    {
        _settings = settings;
        SaveCommand = new AsyncCommand(_ => { Save(); return Task.CompletedTask; });
    }
    public string Language { get => _language; set => Set(ref _language, value); }
    public UiStatus Status { get; } = new();
    public ICommand SaveCommand { get; }
    public void Load()
    {
        var result = _settings.Load();
        if (result.IsFailure) { Status.Failure(result.Error!.Code, result.Error.Message); return; }
        Language = _settings.Get("language", "auto") ?? "auto";
        Status.Success("Settings loaded");
    }
    public void Save()
    {
        _settings.Set("language", Language);
        var result = _settings.Save();
        if (result.IsSuccess) Status.Success("Settings saved");
        else Status.Failure(result.Error!.Code, result.Error.Message);
    }
}

public sealed class ApplicationShellViewModel : ViewModelBase, IDisposable
{
    private readonly ApplicationDb _database;
    private readonly CancellationTokenSource _lifetime = new();
    private object? _currentPage;
    private string _pageTitle = "Home";
    private bool _initialized;

    public ApplicationShellViewModel(ApplicationDb database, PortableSettingsService settings)
    {
        _database = database;
        var historyRepository = new HistoryRepository(database);
        var vocabularyRepository = new VocabularyRepository(database);
        var modeRepository = new ModeRepository(database);
        Home = new HomeViewModel(historyRepository, vocabularyRepository, modeRepository);
        History = new HistoryViewModel(historyRepository);
        Vocabulary = new VocabularyViewModel(vocabularyRepository);
        Modes = new ModesViewModel(modeRepository);
        Settings = new SettingsViewModel(settings);
        _currentPage = Home;
    }

    public HomeViewModel Home { get; }
    public HistoryViewModel History { get; }
    public VocabularyViewModel Vocabulary { get; }
    public ModesViewModel Modes { get; }
    public SettingsViewModel Settings { get; }
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
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
