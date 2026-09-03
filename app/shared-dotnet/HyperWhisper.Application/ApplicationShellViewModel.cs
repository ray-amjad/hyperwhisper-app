using HyperWhisper.FileTranscription;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.ModelManagement;
using HyperWhisper.SpeechOutput;
using HyperWhisper.SharedCore;
using HyperWhisper.CloudAccount;
using HyperWhisper.ModelReadiness;
using HyperWhisper.Statistics;

namespace HyperWhisper.PortableApplication.ViewModels;

public sealed class ApplicationShellViewModel : ViewModelBase, IDisposable
{
    private readonly ApplicationDb _database;
    private readonly CancellationTokenSource _lifetime = new();
    private object? _currentPage;
    private string _pageTitle = "Home";
    private bool _initialized;
    private bool _disposed;
    private readonly Func<string, string>? _localize;

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
        Func<Uri, PlatformResult>? openAccountUri = null,
        ITextInjectionService? historyTextInjection = null,
        ModelReadinessService? modelReadiness = null,
        AboutViewModel? about = null,
        Func<string, string>? localize = null)
    {
        _database = database;
        _localize = localize;
        var historyRepository = new HistoryRepository(database, paths);
        var vocabularyRepository = new VocabularyRepository(database);
        var modeRepository = new ModeRepository(database);
        Vocabulary = new VocabularyViewModel(vocabularyRepository);
        Modes = new ModesViewModel(modeRepository, settings, credentials);
        Settings = new SettingsViewModel(settings, localLlmRuntimeStatus, localWhisperRuntimeStatus);
        Streaming = new StreamingSettingsViewModel(Settings);
        General = new GeneralSettingsViewModel(Settings);
        Sound = new SoundSettingsViewModel(Settings);
        Storage = new StorageSettingsViewModel(Settings);
        Output = new OutputSettingsViewModel(Settings);
        LocalApi = new LocalApiSettingsViewModel(Settings);
        Shortcuts = new ShortcutsSettingsViewModel(Settings);
        Appearance = new AppearanceSettingsViewModel(Settings);
        History = new HistoryViewModel(
            historyRepository,
            playback,
            retryWithMode: transcriptionWorkflow is null ? null : async (item, explicitMode, token) =>
        {
            var audioPath = item.AudioFilePath;
            if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath)) throw new FileNotFoundException();
            var retryMode = explicitMode
                ?? Modes.Items.FirstOrDefault(mode => item.ModeId is { } modeId && mode.Id == modeId)
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
                PasteResultText: Settings.PasteResultText,
                StoreWordTimestamps: Settings.StoreWordTimestamps), token);
        }, retryModes: Modes.Items, textInjection: historyTextInjection);
        Models = modelManager is null ? null : new ModelLibraryViewModel(
            modelManager, modelReadiness, streamingSettings: Settings);
        Backup = new BackupViewModel(new ApplicationBackupService(database, settings, credentials));
        Credentials = credentials is null ? null : new CredentialManagementViewModel(credentials);
        Account = cloudAccount is not null && deviceIdentity is not null && openAccountUri is not null
            ? new CloudAccountViewModel(cloudAccount, deviceIdentity, deviceName, openAccountUri)
            : null;
        About = about;
        Recording = transcriptionWorkflow is null ? null : new TranscriptionWorkflowViewModel(
            transcriptionWorkflow,
            () => CreateTranscriptionRequest(Modes.Selected), audioImport, filePreflight);
        Home = new HomeViewModel(
            historyRepository,
            vocabularyRepository,
            modeRepository,
            new HomeStatisticsService(new StatisticsTranscriptProvider(database)),
            settings,
            Recording);
        if (Recording is not null) Recording.TranscriptionSaved += OnTranscriptionSaved;
        Backup.Imported += OnBackupImported;
        _currentPage = Home;
        PageTitle = Text("sidebar.home", "Home");
    }

    public HomeViewModel Home { get; }
    public HistoryViewModel History { get; }
    public VocabularyViewModel Vocabulary { get; }
    public ModesViewModel Modes { get; }
    public SettingsViewModel Settings { get; }
    public StreamingSettingsViewModel Streaming { get; }
    public GeneralSettingsViewModel General { get; }
    public SoundSettingsViewModel Sound { get; }
    public StorageSettingsViewModel Storage { get; }
    public OutputSettingsViewModel Output { get; }
    public LocalApiSettingsViewModel LocalApi { get; }
    public ShortcutsSettingsViewModel Shortcuts { get; }
    public AppearanceSettingsViewModel Appearance { get; }
    public ModelLibraryViewModel? Models { get; }
    public BackupViewModel Backup { get; }
    public CredentialManagementViewModel? Credentials { get; }
    public CloudAccountViewModel? Account { get; }
    public AboutViewModel? About { get; }
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
        CursorContext: cursorContext,
        StoreWordTimestamps: Settings.StoreWordTimestamps);

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
            History.SetRetryModes(Modes.Items);
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
            "home" => (Text("sidebar.home", "Home"), (object)Home),
            "history" => (Text("sidebar.history", "History"), History),
            "vocabulary" => (Text("sidebar.vocabulary", "Vocabulary"), Vocabulary),
            "modes" => (Text("sidebar.modes", "Modes"), Modes),
            "settings" => (Text("settings.nav.general", "General"), General),
            "sound" => (Text("settings.nav.sound", "Sound"), Sound),
            "storage" => (Text("settings.nav.storage", "Storage"), Storage),
            "output" => (Text("settings.nav.output", "Output"), Output),
            "localapi" => (Text("settings.nav.localApi", "Local API"), LocalApi),
            "shortcuts" => (Text("settings.nav.shortcuts", "Shortcuts"), Shortcuts),
            "appearance" => (Text("settings.nav.appearance", "Appearance"), Appearance),
            "streaming" => (Text("settings.nav.streaming", "Streaming"), Streaming),
            "models" when Models is not null => (Text("settings.section.models", "Models"), Models),
            "backup" => (Text("settings.nav.backup", "Backup"), Backup),
            "credentials" when Credentials is not null => (Text("linux.ui.provider.credentials", "Credentials"), Credentials),
            "account" when Account is not null => (Text("linux.ui.cloud.account", "Cloud account"), Account),
            "about" when About is not null => (Text("settings.nav.about", "About"), About),
            _ => throw new ArgumentException("Unknown navigation page.", nameof(pageId))
        };
        PageTitle = selection.Title;
        CurrentPage = selection.Page;
        Status.Success($"{PageTitle} ready");
    }

    private string Text(string key, string fallback) => _localize?.Invoke(key) ?? fallback;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (Recording is not null) Recording.TranscriptionSaved -= OnTranscriptionSaved;
        Backup.Imported -= OnBackupImported;
        Models?.Dispose();
        History.Dispose();
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
