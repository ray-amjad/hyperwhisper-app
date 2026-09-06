using System.Collections.ObjectModel;
using System.Windows.Input;
using HyperWhisper.ModelManagement;
using HyperWhisper.ModelReadiness;

namespace HyperWhisper.PortableApplication.ViewModels;

public sealed class ManagedModelViewModel : ViewModelBase
{
    private bool _installed;
    private double _progress;
    private string _status;
    private ReadinessState _readiness;
    private string? _readinessDetail;

    public ManagedModelViewModel(ModelCapability capability, ManagedModel? model, bool installed)
    {
        Capability = capability ?? throw new ArgumentNullException(nameof(capability));
        Model = model;
        _installed = installed;
        _readiness = capability.Deployment == ModelDeployment.Local
            ? installed ? ReadinessState.Installed : ReadinessState.Downloadable
            : ReadinessState.Unknown;
        _status = ReadinessLabel(_readiness);
    }

    public ModelCapability Capability { get; }
    public ManagedModel? Model { get; }
    public string Id => Capability.Key;
    public string ModelId => Capability.ModelId;
    /// <summary>
    /// The model's own name, with the two things the row already says stripped off. Windows
    /// prints exactly the model name here and puts everything else on the sub-line or in a pill,
    /// and both of these had pushed Linux names past the column into an ellipsis.
    ///
    /// 1. The catalog's cloud-STT display names are built as "{provider} — {model}"
    ///    (UnifiedModelCatalog.cs:112), so the row read "Groq Whisper — Whisper Large v3 Tur..."
    ///    with "Groq" already printed underneath it. Split on the LAST separator, so a name that
    ///    contains more than one keeps its own tail.
    /// 2. The recommended local model is spelled "Gemma 4 E2B (Recommended)" while the row also
    ///    draws a "Recommended" pill, which said it twice and truncated to "Gemma 4 E2B (Recom...".
    ///    Windows strips the same suffix for the same reason (ModelLibraryManager.cs:210).
    ///
    /// Presentation only, and deliberately here rather than in the catalog: the catalog's fuller
    /// name is the right one for a picker with no second line to put the provider on.
    /// </summary>
    public string DisplayName
    {
        get
        {
            var name = Capability.DisplayName.Replace(" (Recommended)", "", StringComparison.Ordinal);
            // 3. The on-device rows are spelled "Nemotron 3.5 Streaming (Multilingual)" and
            //    "Parakeet v2 (English)". Windows prints only the name and puts the language in
            //    the pill beside it, and carrying it inline pushed the longest name (37 chars)
            //    past the column into an ellipsis, which Windows never shows here.
            if (LanguageTag is { } tag)
                name = name.Replace($" ({LanguageSuffix(tag)})", "", StringComparison.Ordinal);
            var separator = name.LastIndexOf(" — ", StringComparison.Ordinal);
            return separator >= 0 ? name[(separator + 3)..] : name;
        }
    }

    /// <summary>
    /// "EN" or "Multilingual" when the catalog spells the language into the model name, else
    /// null. Windows shows exactly these two pills on its on-device rows.
    /// </summary>
    public string? LanguageTag =>
        Capability.DisplayName.EndsWith(" (English)", StringComparison.Ordinal) ? "EN"
        : Capability.DisplayName.EndsWith(" (Multilingual)", StringComparison.Ordinal) ? "Multilingual"
        : null;

    private static string LanguageSuffix(string tag) => tag == "EN" ? "English" : tag;
    public string Kind => $"{Capability.Deployment} {Capability.Workload} · {Capability.Surface}";
    public string ProviderId => Capability.ProviderId;
    public string Deployment => Capability.Deployment.ToString();
    public string Workload => Capability.Workload.ToString();
    public string Surface => Capability.Surface.ToString();
    public string Size => Capability.ApproximateSizeBytes is { } bytes ? $"{bytes / 1_000_000d:F0} MB" : "Provider managed";
    public string RuntimeBadge => Capability.Deployment == ModelDeployment.Local
        ? $"Local · {Capability.Runtime ?? "runtime unknown"}"
        : "Cloud";
    public string CapabilitySummary => string.Join(" · ", new[]
    {
        Capability.SupportsStreaming ? "Streaming" : null,
        Capability.SupportsCustomVocabulary ? "Custom vocabulary" : null,
        Capability.SupportsAllLanguages ? "All languages"
            : Capability.IsEnglishOnly ? "English only"
            : Capability.SupportedLanguages.Count > 0 ? $"{Capability.SupportedLanguages.Count} languages/locales" : null,
        Capability.CloudTierEligible ? "Cloud tier" : null,
        Capability.ByokEligible ? "BYOK" : null,
    }.Where(value => value is not null));
    /// <summary>
    /// The Windows row prints the provider's display name under the model name; the raw catalog
    /// id ("openai", "geminiTranscribe") is an implementation detail and never appears there.
    /// The same map answers the provider filter, whose Windows menu groups every local runtime
    /// under one "Local" entry.
    /// </summary>
    private static readonly Dictionary<string, string> ProviderDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openai"] = "OpenAI",
        ["anthropic"] = "Anthropic",
        ["groq"] = "Groq",
        ["deepgram"] = "Deepgram",
        ["gemini"] = "Gemini",
        ["google"] = "Gemini",
        ["geminiTranscribe"] = "Gemini",
        ["elevenlabs"] = "ElevenLabs",
        ["mistral"] = "Mistral",
        ["soniox"] = "Soniox",
        ["xai"] = "xAI",
        ["grok"] = "xAI",
        ["hyperwhisper"] = "HyperWhisper Cloud",
        // The three ids UnifiedModelCatalog mints for on-device models. Without these the row
        // sub-line reads the raw id, "localLLM", instead of a provider a reader recognises.
        ["localWhisper"] = "Whisper",
        ["localLLM"] = "On-device",
        ["parakeet"] = "Parakeet",
        // A cloud row's provider id is the catalog's sttProvider, which is spelled differently
        // from the vendor key above it, so five vendors fell through to ProviderId and printed
        // "azure-mai" and "gemini-transcribe" under the model name. The strings come from
        // cloud-stt-catalog.json's vendorDisplayName; keep them in step with it. Two exceptions:
        // cerebras has no STT entry (it is post-processing only) and takes the Windows spelling
        // from TranscriptStatusConverters.cs:209, and gemini-transcribe reads "Gemini" rather
        // than the catalog's "Google" so it matches the gemini and google rows beside it.
        ["assemblyai"] = "AssemblyAI",
        ["azure-mai"] = "Microsoft",
        ["gemini-transcribe"] = "Gemini",
        ["cerebras"] = "Cerebras",
        ["meta"] = "Meta",
        // The two extra ids the streaming duplicates of the on-device models are minted under.
        ["nemotronLocal"] = "Nemotron",
        ["parakeetLocal"] = "Parakeet",
    };

    public string ProviderName =>
        // UnifiedModelCatalog files every sherpa-onnx model under "parakeet" because that is the
        // runtime, so the Nemotron rows read "Parakeet" on their sub-line. Windows names the
        // model family there, and its own streaming duplicate already says "Nemotron", so the
        // two rows for one model disagreed with each other as well as with Windows.
        Capability.Deployment == ModelDeployment.Local
            && Capability.ModelId.StartsWith("nemotron-", StringComparison.OrdinalIgnoreCase)
            ? "Nemotron"
        : ProviderDisplayNames.TryGetValue(Capability.ProviderId, out var name) ? name
        : Capability.ProviderId;

    /// <summary>
    /// The bare logo name for the row's 32px avatar tile, or null when no PNG ships for this
    /// provider. Null is the signal to draw <see cref="ProviderMonogram"/> instead; it must not
    /// be turned into an image path. Windows does the same at LibraryModel.cs:122-126.
    /// </summary>
    public string? ProviderAssetName
    {
        get
        {
            var name = ProviderAssets.AssetNameFor(Capability.ProviderId);
            return ProviderAssets.Exists(name) ? name : null;
        }
    }

    public bool HasProviderAsset => ProviderAssetName is not null;

    /// <summary>The letter Windows draws in place of a missing logo.</summary>
    public string ProviderMonogram => ProviderName.Length > 0
        ? ProviderName[..1].ToUpperInvariant()
        : "?";

    /// <summary>Which entry of the provider filter menu this row belongs to.</summary>
    public string ProviderGroup => IsLocal ? "Local" : ProviderName;

    /// <summary>The grey pill beside the model name. Null collapses it, as on Windows.</summary>
    /// The language pill comes before the surface pill so the plain on-device rows read exactly
    /// as they do on Windows ("Parakeet v2" + EN). The streaming duplicates, which Windows has no
    /// row for, keep "Streaming" so they stay tellable apart from the row they duplicate.
    public string? TagText => Model?.IsRecommended == true ? "Recommended"
        : Capability.Surface == ModelSurface.StreamingTranscription ? "Streaming"
        : LanguageTag is { } language ? language
        : Capability.Surface == ModelSurface.PostProcessing ? "Post-processing"
        : Capability.Surface == ModelSurface.CustomEndpoint ? "Endpoint"
        : null;
    public bool HasTag => TagText is not null;

    /// <summary>The second pill: the local runtime backend, collapsed on every cloud row.</summary>
    public bool HasRuntimeBadge => IsLocal && Capability.Runtime is { Length: > 0 };
    public string RuntimeBadgeText => Capability.Runtime ?? string.Empty;

    public bool IsVoiceModel => Capability.Workload == ModelWorkload.Voice;
    // The book outline collapses into a solid block at 14px, so a language model gets
    // the sparkle instead. A voice model keeps the microphone.
    public string KindGlyphKey => IsVoiceModel ? "HwIconStreaming" : "HwIconSparkle";
    public string KindToolTip => IsVoiceModel ? "Voice model" : "Language model";
    public string LocationText => IsLocal ? "Offline" : "Cloud";

    /// <summary>
    /// The two five-segment meters in the third column, 1-5 each. Clamped to 0-5 the way
    /// Windows clamps at render (LibraryModel.cs:176-177), so a bad catalog number cannot
    /// overrun the gauge.
    /// </summary>
    public int Speed => Math.Clamp(ModelRatings.For(Capability, Model).Speed, 0, 5);
    public int Accuracy => Math.Clamp(ModelRatings.For(Capability, Model).Accuracy, 0, 5);

    /// <summary>
    /// Which brush fills the lit gauge segments. Mirrors macOS gaugeColor and Windows
    /// LibraryModel.GaugeBrushKey: accent when the row is usable, secondary when it is locked
    /// or still has to be downloaded, warning when it is broken. Returned as a resource KEY,
    /// not a brush, so the row follows a live light/dark theme switch.
    ///
    /// Windows keys this off five LibraryModelStatusKind cases and the portable row carries the
    /// wider ReadinessState instead, so the extra failure states join Error rather than falling
    /// through to the accent and painting a broken row as if it were healthy.
    /// </summary>
    public string GaugeBrushKey => Readiness switch
    {
        ReadinessState.Unauthorized or ReadinessState.Unreachable or ReadinessState.Malformed
            or ReadinessState.Unsupported or ReadinessState.RateLimited => "HwWarningBrush",
        ReadinessState.MissingCredential or ReadinessState.Downloadable => "HwTextSecondaryBrush",
        ReadinessState.Installed or ReadinessState.Healthy => "HwAccentBrush",
        // Unknown and Checking. A cloud row starts Unknown and only becomes MissingCredential
        // once a readiness probe has run, so keying purely off the state painted every
        // unconfigured provider in the full accent on first paint, as if it were connected.
        // Windows has no such window: its rows are Locked from the start whenever there is no
        // key, which is why its gauges are grey on a fresh profile. NeedsCredential is the same
        // question asked without waiting for the probe.
        _ => NeedsCredential ? "HwTextSecondaryBrush" : "HwAccentBrush",
    };

    /// <summary>
    /// Windows labels the row button with the ACTION, not with the readiness: Connect for a
    /// cloud model, Download for one that has to come down first. The readiness column already
    /// carries the state, so repeating it in the button said the same thing twice.
    /// </summary>
    public string PrimaryActionText => IsLocal ? (Installed ? "Installed" : "Download") : "Connect";

    /// <summary>
    /// Windows right-aligns the download size in the last column and leaves it blank for a
    /// cloud model, which has nothing to download.
    /// </summary>
    public string DownloadSizeText => IsLocal && Capability.ApproximateSizeBytes is { } bytes
        ? $"{bytes / 1_000_000d:F0} MB"
        : string.Empty;
    public string DetailToolTip => string.Join(" · ", new[] { ProviderName, ModelId, Size, CapabilitySummary }
        .Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>
    /// The single row action Windows draws: one button whose glyph and label follow the row's
    /// own readiness, rather than three buttons that are mostly collapsed.
    /// </summary>
    public string StatusGlyphKey => Readiness switch
    {
        ReadinessState.Installed or ReadinessState.Healthy => "HwIconCheck",
        ReadinessState.MissingCredential => "HwIconLock",
        ReadinessState.Unauthorized or ReadinessState.Unreachable or ReadinessState.Unsupported => "HwIconWarning",
        ReadinessState.Downloadable => "HwIconDownload",
        // A cloud row starts at Unknown and only becomes MissingCredential once a readiness probe
        // has run, so the whole page drew an info circle beside "Connect" until then. Windows has
        // no such window: StatusForHealth returns Locked the moment there is no key
        // (ModelLibraryManager.cs:384-386), and its Locked glyph is the padlock . This is
        // the same NeedsCredential correction the rating gauge already makes above.
        _ when NeedsCredential => "HwIconLock",
        _ => "HwIconInfo",
    };

    public bool IsDownloading => _progress is > 0 and < 1;
    public bool CanCancelRow => IsDownloading;

    public string? CredentialAccount => Capability.CredentialAccount;
    public string? CredentialNavigationActionId => Capability.CredentialAccount is null ? null
        : string.Equals(Capability.CredentialAccount, "LicenseKey", StringComparison.Ordinal)
            ? "navigate.account"
            : $"navigate.credentials:{Capability.CredentialAccount}";
    public string? AccountNavigationActionId => Capability.CloudTierEligible ? "navigate.account" : null;
    // A table row draws one action button, so the row needs to know which action applies without
    // first selecting the model. Local rows download or delete; cloud rows only need a credential.
    public bool IsLocal => Capability.Deployment == ModelDeployment.Local;
    public bool CanDownload => IsLocal && Model is not null && !Installed;
    public bool CanDelete => IsLocal && Model is not null && Installed;
    public bool NeedsCredential => !IsLocal && Capability.CredentialAccount is not null;

    public bool Installed
    {
        get => _installed;
        set
        {
            if (!Set(ref _installed, value)) return;
            Notify(nameof(CanDownload));
            Notify(nameof(CanDelete));
            Notify(nameof(LocationText));
            Notify(nameof(PrimaryActionText));
        }
    }
    public double Progress
    {
        get => _progress;
        set
        {
            if (!Set(ref _progress, value)) return;
            Notify(nameof(IsDownloading));
            Notify(nameof(CanCancelRow));
        }
    }
    public string Status { get => _status; set => Set(ref _status, value); }
    public ReadinessState Readiness
    {
        get => _readiness;
        // GaugeBrushKey reads Readiness too, so the meters recolour with the glyph. Without
        // this the gauges keep the accent after a row turns unauthorized.
        private set
        {
            if (!Set(ref _readiness, value)) return;
            Notify(nameof(StatusGlyphKey));
            Notify(nameof(GaugeBrushKey));
        }
    }
    public string? ReadinessDetail { get => _readinessDetail; private set => Set(ref _readinessDetail, value); }

    internal void ApplyReadiness(HyperWhisper.ModelReadiness.ModelReadiness readiness)
    {
        Readiness = readiness.State;
        ReadinessDetail = readiness.Detail;
        if (readiness.State != ReadinessState.Checking || Model is null)
            Status = readiness.Detail is { Length: > 0 } detail
                ? $"{ReadinessLabel(readiness.State)} — {detail}"
                : ReadinessLabel(readiness.State);
        if (readiness.State == ReadinessState.Installed) Installed = true;
        else if (readiness.State == ReadinessState.Downloadable) Installed = false;
    }

    internal void InvalidateCredential()
        => ApplyReadiness(new(Id, ReadinessState.Unknown, "Credential changed; refresh readiness."));

    private static string ReadinessLabel(ReadinessState state) => state switch
    {
        ReadinessState.MissingCredential => "Credential required",
        ReadinessState.Checking => "Checking…",
        ReadinessState.Healthy => "Ready",
        ReadinessState.Unauthorized => "Credential rejected",
        ReadinessState.Unreachable => "Provider unavailable",
        ReadinessState.Installed => "Installed",
        ReadinessState.Downloadable => "Not installed",
        ReadinessState.Unsupported => "Unsupported",
        _ => "Not checked",
    };
}

public enum ModelLibrarySort { Recommended, Name, Provider, Readiness, Type, Location }

public sealed class ModelLibraryViewModel : ViewModelBase, IDisposable
{
    private readonly PortableModelManager _manager;
    private readonly ModelReadinessService? _readiness;
    private readonly SettingsViewModel? _streamingSettings;
    private readonly List<ManagedModelViewModel> _allItems = [];
    private readonly SynchronizationContext? _synchronizationContext = SynchronizationContext.Current;
    private CancellationTokenSource? _download;
    private CancellationTokenSource? _readinessRefresh;
    private ManagedModelViewModel? _selected;
    private string _searchText = string.Empty;
    private ModelDeployment? _deploymentFilter;
    private ModelWorkload? _workloadFilter;
    private ModelSurface? _surfaceFilter;
    private ModelLibrarySort _sort = ModelLibrarySort.Recommended;
    private string _providerFilter = AllProviders;
    private bool _vocabularyFilter;
    private bool _cloudAvailableFilter;
    private string? _languageFilter;
    private bool _sortDescending;
    private string _librarySummary = string.Empty;
    private string _customEndpointsSummary = string.Empty;
    private string _emptyStateText = string.Empty;
    private bool _hasActiveFilters;

    public const string AllProviders = "All providers";

    public ModelLibraryViewModel(
        PortableModelManager manager,
        ModelReadinessService? readiness = null,
        IReadOnlyList<ModelCapability>? capabilities = null,
        SettingsViewModel? streamingSettings = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _readiness = readiness;
        _streamingSettings = streamingSettings;
        var managed = PortableModelCatalog.All.ToDictionary(model => model.Id, StringComparer.Ordinal);
        foreach (var capability in capabilities ?? UnifiedModelCatalog.LoadBundled())
        {
            ManagedModel? model = null;
            if (capability.Deployment == ModelDeployment.Local)
                managed.TryGetValue(capability.ModelId, out model);
            if (capability.Deployment == ModelDeployment.Local && model is null) continue;
            _allItems.Add(new(capability, model, model is not null && manager.IsInstalled(model)));
        }
        ApplyView();
        DownloadCommand = new AsyncCommand(_ => DownloadAsync(), _ => Selected is { Model: not null, Installed: false });
        CancelCommand = new AsyncCommand(_ => { _download?.Cancel(); return Task.CompletedTask; }, _ => _download is not null);
        DeleteCommand = new AsyncCommand(_ => DeleteAsync(), _ => Selected is { Model: not null, Installed: true });
        RefreshReadinessCommand = new AsyncCommand(_ => RefreshSelectedReadinessAsync(), _ => Selected is not null && _readiness is not null);
        RefreshVisibleReadinessCommand = new AsyncCommand(_ => RefreshVisibleReadinessAsync(), _ => Items.Count > 0 && _readiness is not null);
        UseForLiveStreamingCommand = new AsyncCommand(_ => UseForLiveStreamingAsync(),
            _ => _streamingSettings is not null && Selected is
            {
                Installed: true,
                Capability.Deployment: ModelDeployment.Local,
                Capability.Surface: ModelSurface.StreamingTranscription,
            });
        if (_readiness is not null)
        {
            _readiness.ReadinessChanged += OnReadinessChanged;
            _readiness.CredentialInvalidated += OnCredentialInvalidated;
        }
    }
    public ObservableCollection<ManagedModelViewModel> Items { get; } = new();

    /// <summary>
    /// The streaming-surface capabilities, which are deliberately not rows in <see cref="Items"/>
    /// (see <c>IsLibraryRow</c>). They stay selectable so
    /// <see cref="UseForLiveStreamingCommand"/> has something to act on.
    /// </summary>
    public ObservableCollection<ManagedModelViewModel> StreamingItems { get; } = new();
    public ManagedModelViewModel? Selected { get => _selected; set { if (Set(ref _selected, value)) RaiseCommands(); } }
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value ?? string.Empty)) ApplyView(); } }
    public ModelDeployment? DeploymentFilter { get => _deploymentFilter; set { if (Set(ref _deploymentFilter, value)) ApplyView(); } }
    public ModelWorkload? WorkloadFilter { get => _workloadFilter; set { if (Set(ref _workloadFilter, value)) ApplyView(); } }
    public ModelSurface? SurfaceFilter { get => _surfaceFilter; set { if (Set(ref _surfaceFilter, value)) ApplyView(); } }
    public ModelLibrarySort Sort { get => _sort; set { if (Set(ref _sort, value)) ApplyView(); } }
    public IReadOnlyList<ModelDeployment?> DeploymentFilters { get; } = [null, ModelDeployment.Local, ModelDeployment.Cloud];
    public IReadOnlyList<ModelWorkload?> WorkloadFilters { get; } = [null, ModelWorkload.Voice, ModelWorkload.Text];
    public IReadOnlyList<ModelSurface?> SurfaceFilters { get; } = [null, .. Enum.GetValues<ModelSurface>().Cast<ModelSurface?>()];
    public IReadOnlyList<ModelLibrarySort> SortOptions { get; } = Enum.GetValues<ModelLibrarySort>();

    /// <summary>
    /// The Windows provider menu, in its order. Every local runtime collapses into "Local", so
    /// the list is fixed rather than derived from the catalog's provider ids.
    /// </summary>
    public IReadOnlyList<string> ProviderOptions { get; } =
        [AllProviders, "OpenAI", "Anthropic", "Groq", "Deepgram", "Gemini", "Local"];

    public string ProviderFilter
    {
        get => _providerFilter;
        set { if (Set(ref _providerFilter, string.IsNullOrWhiteSpace(value) ? AllProviders : value)) ApplyView(); }
    }

    /// <summary>Windows "Features" menu: supports custom vocabulary.</summary>
    public bool VocabularyFilter { get => _vocabularyFilter; set { if (Set(ref _vocabularyFilter, value)) ApplyView(); } }

    /// <summary>Windows "Features" menu: available on HyperWhisper Cloud.</summary>
    public bool CloudAvailableFilter { get => _cloudAvailableFilter; set { if (Set(ref _cloudAvailableFilter, value)) ApplyView(); } }

    /// <summary>A BCP-47 code, or null for "any language".</summary>
    public string? LanguageFilter
    {
        get => _languageFilter;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (!Set(ref _languageFilter, normalized)) return;
            Notify(nameof(HasLanguageFilter));
            Notify(nameof(LanguageFilterLabel));
            ApplyView();
        }
    }

    public bool HasLanguageFilter => _languageFilter is not null;
    public string LanguageFilterLabel => _languageFilter ?? "Language";

    /// <summary>"{n} of {m} support {language}", the count Windows shows beside the globe.</summary>
    public string LanguageCountText { get; private set; } = string.Empty;

    /// <summary>Sorting is driven by the column headers, so the direction is public too.</summary>
    public bool SortDescending { get => _sortDescending; set { if (Set(ref _sortDescending, value)) ApplyView(); } }

    public bool SortedByName => _sort == ModelLibrarySort.Name;
    public bool SortedByType => _sort == ModelLibrarySort.Type;
    public bool SortedByRating => _sort == ModelLibrarySort.Readiness;
    public bool SortedByLocation => _sort == ModelLibrarySort.Location;

    /// <summary>
    /// Clicking a header sorts by it; clicking the active header flips the direction. Same
    /// behaviour as the Windows SortHeader_Click.
    /// </summary>
    public void ToggleSort(ModelLibrarySort column)
    {
        if (_sort == column) _sortDescending = !_sortDescending;
        else { _sort = column; _sortDescending = false; }
        Notify(nameof(Sort));
        Notify(nameof(SortDescending));
        Notify(nameof(SortedByName));
        Notify(nameof(SortedByType));
        Notify(nameof(SortedByRating));
        Notify(nameof(SortedByLocation));
        ApplyView();
    }

    public void ClearFilters()
    {
        _searchText = string.Empty;
        _deploymentFilter = null;
        _workloadFilter = null;
        _surfaceFilter = null;
        _providerFilter = AllProviders;
        _vocabularyFilter = false;
        _cloudAvailableFilter = false;
        _languageFilter = null;
        Notify(nameof(SearchText));
        Notify(nameof(DeploymentFilter));
        Notify(nameof(WorkloadFilter));
        Notify(nameof(SurfaceFilter));
        Notify(nameof(ProviderFilter));
        Notify(nameof(VocabularyFilter));
        Notify(nameof(CloudAvailableFilter));
        Notify(nameof(LanguageFilter));
        Notify(nameof(HasLanguageFilter));
        Notify(nameof(LanguageFilterLabel));
        ApplyView();
    }

    /// <summary>"{N} models: {c} Cloud, {o} Offline, {i} Installed" — the Windows page subtitle.</summary>
    public string LibrarySummary { get => _librarySummary; private set => Set(ref _librarySummary, value); }
    public string CustomEndpointsSummary { get => _customEndpointsSummary; private set => Set(ref _customEndpointsSummary, value); }
    public string EmptyStateText { get => _emptyStateText; private set => Set(ref _emptyStateText, value); }
    public bool HasActiveFilters { get => _hasActiveFilters; private set => Set(ref _hasActiveFilters, value); }
    public bool IsEmpty => Items.Count == 0;

    public UiStatus Status { get; } = new();
    public ICommand DownloadCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand RefreshReadinessCommand { get; }
    public ICommand RefreshVisibleReadinessCommand { get; }
    public ICommand UseForLiveStreamingCommand { get; }
    public async Task DownloadAsync()
    {
        var target = Selected;
        if (target?.Model is not { } model) return;
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
            var result = await _manager.DownloadAsync(model, progress, download.Token);
            target.Installed = result.IsSuccess;
            target.Status = result.IsSuccess ? "Installed" : result.Failure!.Message;
            if (result.IsSuccess)
            {
                foreach (var row in RowsFor(model))
                    row.ApplyReadiness(new(row.Id, ReadinessState.Installed));
            }
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
        if (Selected?.Model is not { } model) return Task.CompletedTask;
        var result = _manager.Delete(model);
        if (result.IsSuccess)
        {
            foreach (var row in RowsFor(model))
            {
                row.Installed = false;
                row.Progress = 0;
                row.ApplyReadiness(new(row.Id, ReadinessState.Downloadable));
            }
            Status.Success("Model deleted");
        }
        else Status.Failure("models.delete_failed", result.Failure!.Message);
        RaiseCommands(); return Task.CompletedTask;
    }
    public async Task RefreshSelectedReadinessAsync(CancellationToken cancellationToken = default)
    {
        var target = Selected;
        if (target is null || _readiness is null) return;
        await RefreshAsync([target], cancellationToken);
    }

    public Task RefreshVisibleReadinessAsync(CancellationToken cancellationToken = default)
        => _readiness is null ? Task.CompletedTask : RefreshAsync(Items.ToArray(), cancellationToken);

    public Task UseForLiveStreamingAsync()
    {
        var target = Selected;
        if (_streamingSettings is null || target is not
            {
                Installed: true,
                Capability.Deployment: ModelDeployment.Local,
                Capability.Surface: ModelSurface.StreamingTranscription,
            }) return Task.CompletedTask;

        _streamingSettings.StreamingEnabled = true;
        _streamingSettings.StreamingProvider = target.ProviderId;
        _streamingSettings.StreamingModel = target.ModelId;
        if (!SupportsLanguage(target.Capability, _streamingSettings.StreamingLanguage))
            _streamingSettings.StreamingLanguage = "auto";
        _streamingSettings.Save();
        if (_streamingSettings.Status.HasError)
        {
            Status.Failure(_streamingSettings.Status.ErrorCode ?? "models.live_selection_failed",
                _streamingSettings.Status.Message);
            return Task.CompletedTask;
        }
        Status.Success($"{target.DisplayName} selected for live transcription");
        return Task.CompletedTask;
    }

    private IEnumerable<ManagedModelViewModel> RowsFor(ManagedModel model)
        => _allItems.Where(row => string.Equals(row.Model?.Id, model.Id, StringComparison.Ordinal));

    private static bool SupportsLanguage(ModelCapability capability, string? language)
    {
        if (capability.SupportsAllLanguages || string.IsNullOrWhiteSpace(language)
            || string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase)) return true;
        var normalized = language.Trim();
        return capability.SupportedLanguages.Any(supported =>
            string.Equals(supported, normalized, StringComparison.OrdinalIgnoreCase)
            || supported.StartsWith(normalized + "-", StringComparison.OrdinalIgnoreCase));
    }

    private async Task RefreshAsync(IReadOnlyList<ManagedModelViewModel> targets, CancellationToken cancellationToken)
    {
        _readinessRefresh?.Cancel();
        _readinessRefresh?.Dispose();
        var refresh = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _readinessRefresh = refresh;
        RaiseCommands();
        try
        {
            foreach (var target in targets)
                await _readiness!.CheckAsync(target.Capability, refresh.Token);
            Status.Success(targets.Count == 1 ? "Model readiness refreshed" : $"{targets.Count} model readiness rows refreshed");
        }
        catch (OperationCanceledException) when (refresh.IsCancellationRequested)
        {
            Status.Failure("models.readiness_cancelled", "Model readiness refresh cancelled.");
        }
        finally
        {
            refresh.Dispose();
            if (ReferenceEquals(_readinessRefresh, refresh)) _readinessRefresh = null;
            RaiseCommands();
        }
    }

    /// <summary>
    /// Which of Windows' four appended blocks a row belongs to: 0 voice, 1 cloud
    /// post-processing, 2 on-device LLM, 3 custom endpoint. Windows gets these for free by
    /// calling four builders in order; the portable catalog is one flat list, so the block has
    /// to be derived from the capability instead.
    ///
    /// Surface alone cannot separate 1 from 2 -- both are PostProcessing -- so Deployment does
    /// it. Workload alone would over-capture 0 on Linux, where the streaming rows are Voice too,
    /// but that is correct here: they belong with the other voice models.
    /// </summary>
    private static int GroupRank(ModelCapability capability) => capability.Surface switch
    {
        ModelSurface.CustomEndpoint => 3,
        _ when capability.Workload == ModelWorkload.Voice => 0,
        ModelSurface.PostProcessing when capability.Deployment == ModelDeployment.Local => 2,
        _ => 1,
    };

    /// <summary>
    /// Whether a row's block is one Windows sorts by rating. The other two keep catalog order,
    /// which is what puts the recommended Gemma at the head of the local-LLM block.
    /// </summary>
    private static bool IsRatedGroup(ModelCapability capability) => GroupRank(capability) <= 1;

    private void ApplyView()
    {
        var selectedKey = Selected?.Id;
        IEnumerable<ManagedModelViewModel> query = _allItems.Where(Matches);
        IOrderedEnumerable<ManagedModelViewModel> ordered = _sort switch
        {
            ModelLibrarySort.Name => query.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase),
            ModelLibrarySort.Provider => query.OrderBy(item => item.ProviderName, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase),
            // The third column's header sorts by the RATING now that it draws the two meters,
            // not by the readiness word it used to print. Same keys Windows sorts that column
            // with, so clicking the header lands on the same order on both heads.
            ModelLibrarySort.Readiness => query
                .OrderByDescending(item => item.Speed + item.Accuracy)
                .ThenByDescending(item => item.Accuracy)
                .ThenByDescending(item => item.Speed)
                .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase),
            ModelLibrarySort.Type => query.OrderBy(item => item.Capability.Workload).ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase),
            ModelLibrarySort.Location => query.OrderBy(item => item.Capability.Deployment).ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase),
            // Default order. Windows does not sort the whole table: it appends four blocks and
            // sorts only the first two (ModelLibraryManager.Rebuild, lines 30-47), so the shape
            // is group first, then rating within the rated groups. Sorting the local-LLM and
            // custom-endpoint blocks too would move Gemma 4 E2B off the top of its block, which
            // is where Windows shows it. OrderBy is stable in LINQ-to-Objects, so ranks 2 and 3
            // keep catalog order by returning a constant key for them.
            //
            // Windows' RecommendedSort has no final tiebreak, so equal-rated rows fall back to ITS
            // input order: cloud rows first, then Whisper, then Parakeet (Rebuild lines 33-35).
            // _allItems is local-first, so an alphabetical tiebreak here put equal-rated cloud and
            // local rows in a visibly different order from Windows. Deployment reproduces the
            // Windows fallback exactly -- Cloud before Local -- and LINQ's stable OrderBy then
            // keeps catalog order inside each, which is Whisper before Parakeet.
            _ => query.OrderBy(item => GroupRank(item.Capability))
                .ThenByDescending(item => IsRatedGroup(item.Capability) ? item.Speed + item.Accuracy : 0)
                .ThenByDescending(item => IsRatedGroup(item.Capability) ? item.Accuracy : 0)
                .ThenByDescending(item => IsRatedGroup(item.Capability) ? item.Speed : 0)
                .ThenBy(item => IsRatedGroup(item.Capability) && item.IsLocal ? 1 : 0),
        };
        var rows = ordered.ToList();
        if (_sortDescending && _sort != ModelLibrarySort.Recommended) rows.Reverse();

        Items.Clear();
        foreach (var item in rows) Items.Add(item);
        StreamingItems.Clear();
        foreach (var item in _allItems.Where(item => !IsLibraryRow(item.Capability))) StreamingItems.Add(item);
        if (selectedKey is not null) Selected = Items.FirstOrDefault(item => item.Id == selectedKey);
        UpdateSummaries();
        Notify(nameof(IsEmpty));
        RaiseCommands();
    }

    /// <summary>
    /// Whether a capability is a row in the library TABLE. Windows builds that table from four
    /// blocks — cloud voice, cloud post-processing, local LLM, custom endpoints
    /// (ModelLibraryManager.Rebuild, lines 30-47) — and none of them has a streaming surface, so
    /// Windows draws no streaming row at all.
    ///
    /// The Linux catalog mints one extra StreamingTranscription capability per streaming-capable
    /// model, on top of the batch capability for the same model. In the table that read as a
    /// duplicate: "Nemotron 3.5 Streaming" twice, "Parakeet v2" twice and "Parakeet v3" twice,
    /// told apart only by a Multilingual/Streaming badge, plus nine cloud rows Windows has no
    /// equivalent for. Those extras are what made the counts 86/63/23 against Windows' 85/65/20.
    ///
    /// The capabilities themselves are kept — see <see cref="StreamingItems"/> — because they are
    /// what <see cref="UseForLiveStreamingCommand"/> acts on. Nothing is lost by keeping them out
    /// of the table: a streaming provider is chosen on the Streaming settings page, which offers
    /// the same six cloud providers Windows offers (StreamingSettingsPage.xaml:221-226).
    /// </summary>
    private static bool IsLibraryRow(ModelCapability capability)
        => capability.Surface != ModelSurface.StreamingTranscription;

    private bool Matches(ManagedModelViewModel item)
    {
        if (!IsLibraryRow(item.Capability)) return false;
        if (_deploymentFilter is not null && item.Capability.Deployment != _deploymentFilter) return false;
        if (_workloadFilter is not null && item.Capability.Workload != _workloadFilter) return false;
        if (_surfaceFilter is not null && item.Capability.Surface != _surfaceFilter) return false;
        if (!string.Equals(_providerFilter, AllProviders, StringComparison.Ordinal)
            && !string.Equals(item.ProviderGroup, _providerFilter, StringComparison.OrdinalIgnoreCase)) return false;
        if (_vocabularyFilter && !item.Capability.SupportsCustomVocabulary) return false;
        if (_cloudAvailableFilter && !item.Capability.CloudTierEligible) return false;
        if (_languageFilter is not null && !SupportsLanguage(item.Capability, _languageFilter)) return false;
        if (string.IsNullOrWhiteSpace(_searchText)) return true;
        var needle = _searchText.Trim();
        return item.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || item.ProviderId.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || item.ProviderName.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || item.ModelId.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The Windows page subtitle, the language count beside the globe and the empty-state
    /// sentence are all one derived view of the same counts, so they are recomputed together.
    /// </summary>
    private void UpdateSummaries()
    {
        // Counted over the table's own rows, not every capability: the streaming duplicates are
        // not rows, so they must not be in "N models: c Cloud, o Offline" either.
        var rows = _allItems.Where(item => IsLibraryRow(item.Capability)).ToList();
        var total = rows.Count;
        var visible = Items.Count;
        var cloud = rows.Count(item => !item.IsLocal);
        var offline = rows.Count(item => item.IsLocal);
        var installed = rows.Count(item => item.Installed);
        var countText = visible == total ? $"{total} models" : $"{visible} of {total} models";
        LibrarySummary = $"{countText}: {cloud} Cloud, {offline} Offline, {installed} Installed";

        var endpoints = _allItems.Count(item => item.Capability.Surface == ModelSurface.CustomEndpoint);
        CustomEndpointsSummary = endpoints == 0
            ? "No custom endpoints configured. Add a local or hosted OpenAI-compatible chat completions endpoint for post-processing."
            : $"{endpoints} custom endpoint{(endpoints == 1 ? "" : "s")} available in the table. Use the row actions to edit, duplicate, or delete.";

        LanguageCountText = _languageFilter is null
            ? string.Empty
            : $"{_allItems.Count(item => SupportsLanguage(item.Capability, _languageFilter))} of {total} support {_languageFilter}";
        Notify(nameof(LanguageCountText));

        var descriptions = ActiveFilterDescriptions().ToList();
        HasActiveFilters = descriptions.Count > 0;
        EmptyStateText = descriptions.Count > 0
            ? $"No models match: {string.Join(", ", descriptions)}."
            : "No models are available.";
    }

    private IEnumerable<string> ActiveFilterDescriptions()
    {
        if (!string.IsNullOrWhiteSpace(_searchText)) yield return $"search \"{_searchText.Trim()}\"";
        if (!string.Equals(_providerFilter, AllProviders, StringComparison.Ordinal)) yield return _providerFilter;
        if (_workloadFilter is { } workload) yield return workload == ModelWorkload.Voice ? "Voice models" : "Language models";
        if (_deploymentFilter is { } deployment) yield return deployment == ModelDeployment.Cloud ? "Cloud" : "Offline";
        if (_surfaceFilter is { } surface) yield return surface.ToString();
        if (_vocabularyFilter) yield return "Custom vocabulary";
        if (_cloudAvailableFilter) yield return "On HyperWhisper Cloud";
        if (_languageFilter is { } language) yield return language;
    }

    private void OnReadinessChanged(object? sender, ModelReadinessChangedEventArgs e)
        => OnUi(() => _allItems.FirstOrDefault(item => item.Id == e.Readiness.CapabilityKey)?.ApplyReadiness(e.Readiness));

    private void OnCredentialInvalidated(object? sender, string account)
        => OnUi(() =>
        {
            foreach (var item in _allItems.Where(item => string.Equals(item.CredentialAccount, account, StringComparison.Ordinal)))
                item.InvalidateCredential();
        });

    private void OnUi(Action action)
    {
        if (_synchronizationContext is null || SynchronizationContext.Current == _synchronizationContext) action();
        else _synchronizationContext.Post(_ => action(), null);
    }

    public void Dispose()
    {
        if (_readiness is not null)
        {
            _readiness.ReadinessChanged -= OnReadinessChanged;
            _readiness.CredentialInvalidated -= OnCredentialInvalidated;
        }
        _readinessRefresh?.Cancel();
        _readinessRefresh?.Dispose();
        _download?.Cancel();
        _download?.Dispose();
    }
    private void RaiseCommands()
    {
        (DownloadCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (CancelCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (DeleteCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (RefreshReadinessCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (RefreshVisibleReadinessCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (UseForLiveStreamingCommand as AsyncCommand)?.RaiseCanExecuteChanged();
    }
}
