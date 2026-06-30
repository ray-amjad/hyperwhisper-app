using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.Services;
using HyperWhisper.Utilities;
using HyperWhisper.ViewModels;
using HyperWhisper.Views.Windows;
using Button = System.Windows.Controls.Button;
using MenuItem = System.Windows.Controls.MenuItem;
using PropertyChangedEventArgs = System.ComponentModel.PropertyChangedEventArgs;
using WpfBrush = System.Windows.Media.Brush;

namespace HyperWhisper.Views.Pages.Settings;

public partial class ModelsSettingsPage : Page
{
    private readonly WhisperModelService _whisperService = new();
    private readonly ParakeetModelService _parakeetService = new();
    private readonly LocalLlmModelService _localLlmService = new();
    private readonly ModelLibraryManager _libraryManager;
    private readonly ObservableCollection<LibraryModelViewModel> _allRows = new();
    private MainViewModel? _mainViewModel;

    private enum SortColumn { Name, Type, Rating, Location }

    private SortColumn? _sortColumn;
    private bool _sortAscending;
    private string _providerFilter = "All providers";
    private string _typeFilter = "All types";
    private string _locationFilter = "Cloud & Offline";
    private bool _vocabFilterEnabled;
    private bool _cloudAvailableFilterEnabled;
    private string _languageFilter = LibraryLanguageFilter.AnyCode;

    public ObservableCollection<LibraryModelViewModel> LibraryRows { get; } = new();

    public ModelsSettingsPage()
    {
        _libraryManager = new ModelLibraryManager(
            _whisperService,
            _parakeetService,
            _localLlmService,
            ApiKeyService.Instance,
            CloudProviderHealthService.Instance);

        InitializeComponent();
        DataContext = this;
        InitializeLanguageFilter();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Restores the persisted language selection and populates the language
    /// dropdown ("Any language" + one entry per base language).
    /// </summary>
    private void InitializeLanguageFilter()
    {
        _languageFilter = SettingsService.Instance.ModelLibraryLanguageFilter ?? LibraryLanguageFilter.AnyCode;

        LanguageMenu.Items.Clear();

        var anyItem = new MenuItem
        {
            Header = "Any language",
            IsCheckable = true,
            IsChecked = string.IsNullOrEmpty(_languageFilter),
            Tag = LibraryLanguageFilter.AnyCode
        };
        anyItem.Click += LanguageFilter_Changed;
        LanguageMenu.Items.Add(anyItem);
        LanguageMenu.Items.Add(new Separator());

        foreach (var lang in LibraryLanguageFilter.Languages)
        {
            var item = new MenuItem
            {
                Header = lang.DisplayName,
                IsCheckable = true,
                IsChecked = lang.Code == _languageFilter,
                Tag = lang.Code
            };
            item.Click += LanguageFilter_Changed;
            LanguageMenu.Items.Add(item);
        }

        UpdateLanguageLabel();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CustomEndpointManager.Instance.EndpointsChanged += OnEndpointsChanged;
        ApiKeyService.Instance.ApiKeysChanged += OnApiKeysChanged;
        CloudProviderHealthService.Instance.TranscriptionProviderStatusChanged += OnTranscriptionProviderHealthChanged;
        CloudProviderHealthService.Instance.PostProcessingProviderStatusChanged += OnPostProcessingProviderHealthChanged;
        ModelDownloadService.Instance.DownloadChanged += OnModelDownloadChanged;

        RebuildLibrary();
        _ = RefreshProviderHealthAsync();

        // Observe the request flag from MainViewModel that fires when a credential-error
        // toast asks us to auto-open the API keys manager (mirrors macOS shouldOpenModelLibraryAPIKeys).
        _mainViewModel = Window.GetWindow(this)?.DataContext as MainViewModel;
        if (_mainViewModel != null)
        {
            _mainViewModel.PropertyChanged += OnMainViewModelPropertyChanged;
            ConsumeApiKeysManagerRequestIfPending();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CustomEndpointManager.Instance.EndpointsChanged -= OnEndpointsChanged;
        ApiKeyService.Instance.ApiKeysChanged -= OnApiKeysChanged;
        CloudProviderHealthService.Instance.TranscriptionProviderStatusChanged -= OnTranscriptionProviderHealthChanged;
        CloudProviderHealthService.Instance.PostProcessingProviderStatusChanged -= OnPostProcessingProviderHealthChanged;
        ModelDownloadService.Instance.DownloadChanged -= OnModelDownloadChanged;

        if (_mainViewModel != null)
        {
            _mainViewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
            _mainViewModel = null;
        }

    }

    private void RebuildLibrary()
    {
        var downloadingRows = ModelDownloadService.Instance.GetActiveDownloads()
            .ToDictionary(d => d.ModelId, d => d.Progress);

        _allRows.Clear();
        foreach (var model in _libraryManager.Rebuild())
        {
            _allRows.Add(new LibraryModelViewModel(
                downloadingRows.TryGetValue(model.Id, out var progress)
                    ? WithDownloadState(model, progress)
                    : model));
        }

        ApplyFilters();
    }

    private async Task RefreshProviderHealthAsync()
    {
        var cloudProviders = CloudTranscriptionModels.All
            .Select(m => m.Provider)
            .Where(p => p.RequiresApiKey())
            .Distinct()
            .ToArray();

        var postProviders = LanguageModelInfo.AvailableModels
            .Select(m => m.Provider)
            .Where(p => p.RequiresApiKey())
            .Distinct()
            .ToArray();

        var tasks = cloudProviders.Select(p => CloudProviderHealthService.Instance.RefreshAsync(p))
            .Concat(postProviders.Select(p => CloudProviderHealthService.Instance.RefreshAsync(p)));

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"ModelsSettingsPage: Provider health refresh failed: {ex.Message}");
        }

        Dispatcher.Invoke(RebuildLibrary);
    }

    private void Filter_Changed(object sender, EventArgs e) => ApplyFilters();

    private void FilterButton_Click(object sender, RoutedEventArgs e) => OpenContextMenu(sender);

    private void ProviderButton_Click(object sender, RoutedEventArgs e) => OpenContextMenu(sender);

    private static void OpenContextMenu(object sender)
    {
        if (sender is Button button && button.ContextMenu != null)
        {
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.IsOpen = true;
        }
    }

    // Type / Location are single-select toggles: checking one clears its sibling, and
    // re-checking the active one falls back to "all" (mirrors the macOS filter menu).
    private void TypeFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.IsChecked)
        {
            if (ReferenceEquals(item, TypeVoiceMenuItem)) TypeLanguageMenuItem.IsChecked = false;
            else TypeVoiceMenuItem.IsChecked = false;
        }

        _typeFilter = TypeVoiceMenuItem.IsChecked ? "Voice models"
            : TypeLanguageMenuItem.IsChecked ? "Language models"
            : "All types";
        UpdateFilterChips();
        ApplyFilters();
    }

    private void LocationFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.IsChecked)
        {
            if (ReferenceEquals(item, LocationCloudMenuItem)) LocationOfflineMenuItem.IsChecked = false;
            else LocationCloudMenuItem.IsChecked = false;
        }

        _locationFilter = LocationCloudMenuItem.IsChecked ? "Cloud Only"
            : LocationOfflineMenuItem.IsChecked ? "Offline Only"
            : "Cloud & Offline";
        UpdateFilterChips();
        ApplyFilters();
    }

    private void ProviderFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem clicked) return;

        _providerFilter = clicked.Tag?.ToString() ?? "All providers";
        foreach (var mi in ProviderMenu.Items.OfType<MenuItem>())
        {
            mi.IsChecked = ReferenceEquals(mi, clicked);
        }

        ProviderFilterLabel.Text = _providerFilter;
        ApplyFilters();
    }

    private void FeatureFilter_Changed(object sender, RoutedEventArgs e)
    {
        _vocabFilterEnabled = VocabFilterMenuItem.IsChecked;
        _cloudAvailableFilterEnabled = CloudAvailableFilterMenuItem.IsChecked;
        UpdateFilterChips();
        ApplyFilters();
    }

    private void TypeFilterChip_Clear(object sender, RoutedEventArgs e)
    {
        _typeFilter = "All types";
        TypeVoiceMenuItem.IsChecked = false;
        TypeLanguageMenuItem.IsChecked = false;
        UpdateFilterChips();
        ApplyFilters();
    }

    private void LocationFilterChip_Clear(object sender, RoutedEventArgs e)
    {
        _locationFilter = "Cloud & Offline";
        LocationCloudMenuItem.IsChecked = false;
        LocationOfflineMenuItem.IsChecked = false;
        UpdateFilterChips();
        ApplyFilters();
    }

    private void VocabFilterChip_Clear(object sender, RoutedEventArgs e)
    {
        _vocabFilterEnabled = false;
        VocabFilterMenuItem.IsChecked = false;
        UpdateFilterChips();
        ApplyFilters();
    }

    private void CloudAvailableFilterChip_Clear(object sender, RoutedEventArgs e)
    {
        _cloudAvailableFilterEnabled = false;
        CloudAvailableFilterMenuItem.IsChecked = false;
        UpdateFilterChips();
        ApplyFilters();
    }

    private void LanguageButton_Click(object sender, RoutedEventArgs e) => OpenContextMenu(sender);

    private void LanguageFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem clicked) return;
        SetLanguageFilter(clicked.Tag?.ToString() ?? LibraryLanguageFilter.AnyCode);
    }

    private void LanguageShowAll_Click(object sender, RoutedEventArgs e)
        => SetLanguageFilter(LibraryLanguageFilter.AnyCode);

    private void SetLanguageFilter(string code)
    {
        _languageFilter = code ?? LibraryLanguageFilter.AnyCode;
        SettingsService.Instance.ModelLibraryLanguageFilter = _languageFilter;

        foreach (var mi in LanguageMenu.Items.OfType<MenuItem>())
        {
            mi.IsChecked = (mi.Tag?.ToString() ?? "") == _languageFilter;
        }

        UpdateLanguageLabel();
        ApplyFilters();
    }

    private void UpdateLanguageLabel()
    {
        LanguageFilterLabel.Text = string.IsNullOrEmpty(_languageFilter)
            ? "Language"
            : LibraryLanguageFilter.DisplayName(_languageFilter);
    }

    private void OpenApiKeys_Click(object sender, RoutedEventArgs e) => OpenApiKeyManager();

    private void UpdateFilterChips()
    {
        var typeChip = _typeFilter switch
        {
            "Voice models" => "Voice models",
            "Language models" => "Language models",
            _ => null
        };
        TypeFilterChip.Visibility = typeChip != null ? Visibility.Visible : Visibility.Collapsed;
        if (typeChip != null) TypeFilterChipText.Text = typeChip;

        var locationChip = _locationFilter switch
        {
            "Cloud Only" => "Cloud",
            "Offline Only" => "Offline",
            _ => null
        };
        LocationFilterChip.Visibility = locationChip != null ? Visibility.Visible : Visibility.Collapsed;
        if (locationChip != null) LocationFilterChipText.Text = locationChip;

        VocabFilterChip.Visibility = _vocabFilterEnabled ? Visibility.Visible : Visibility.Collapsed;
        CloudAvailableFilterChip.Visibility = _cloudAvailableFilterEnabled ? Visibility.Visible : Visibility.Collapsed;

        var anyChip = typeChip != null || locationChip != null || _vocabFilterEnabled || _cloudAvailableFilterEnabled;
        ActiveFilterChipsPanel.Visibility = anyChip ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyFilters()
    {
        if (LibraryRows == null) return;

        var search = SearchBox?.Text?.Trim() ?? "";
        var provider = _providerFilter;
        var type = _typeFilter;
        var location = _locationFilter;

        IEnumerable<LibraryModelViewModel> filtered = _allRows.Where(row =>
            MatchesSearch(row.Model, search)
            && MatchesProvider(row.Model, provider)
            && MatchesType(row.Model, type)
            && MatchesLocation(row.Model, location)
            && (!_vocabFilterEnabled || row.Model.SupportsCustomVocabulary)
            && (!_cloudAvailableFilterEnabled || row.Model.AvailableViaHyperWhisperCloud)
            && MatchesLanguage(row.Model, _languageFilter));

        var rows = ApplySort(filtered).ToList();

        LibraryRows.Clear();
        foreach (var row in rows)
        {
            LibraryRows.Add(row);
        }

        UpdateEmptyState(rows.Count);

        UpdateSummary(rows);

        UpdateLanguageCount();
    }

    /// <summary>
    /// "N of M support X" where M = voice models matching every other filter and
    /// N = those that also support the chosen language. Hidden when no language
    /// is selected.
    /// </summary>
    private void UpdateLanguageCount()
    {
        if (LanguageCountText == null || LanguageShowAllButton == null) return;

        if (string.IsNullOrEmpty(_languageFilter))
        {
            LanguageCountText.Visibility = Visibility.Collapsed;
            LanguageShowAllButton.Visibility = Visibility.Collapsed;
            return;
        }

        var search = SearchBox?.Text?.Trim() ?? "";
        var voice = _allRows.Where(r =>
            r.Model.IsVoice
            && MatchesSearch(r.Model, search)
            && MatchesProvider(r.Model, _providerFilter)
            && MatchesType(r.Model, _typeFilter)
            && MatchesLocation(r.Model, _locationFilter)
            && (!_vocabFilterEnabled || r.Model.SupportsCustomVocabulary)
            && (!_cloudAvailableFilterEnabled || r.Model.AvailableViaHyperWhisperCloud))
            .ToList();
        var supported = voice.Count(r => r.Model.SupportsLanguage(_languageFilter));
        var name = LibraryLanguageFilter.DisplayName(_languageFilter);

        LanguageCountText.Text = $"{supported} of {voice.Count} support {name}";
        LanguageCountText.Visibility = Visibility.Visible;
        LanguageShowAllButton.Visibility = Visibility.Visible;
    }

    private void UpdateEmptyState(int visibleCount)
    {
        if (EmptyStatePanel == null) return;

        EmptyStatePanel.Visibility = visibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (visibleCount != 0) return;

        var activeFilters = GetActiveFilterDescriptions().ToList();
        var hasActiveFilters = activeFilters.Count > 0;
        EmptyStateText.Text = hasActiveFilters
            ? Loc.S("settings.models.empty.filtered", string.Join(", ", activeFilters))
            : Loc.S("settings.models.empty.noneAvailable");
        ClearFiltersButton.Visibility = hasActiveFilters ? Visibility.Visible : Visibility.Collapsed;
    }

    private IEnumerable<string> GetActiveFilterDescriptions()
    {
        var search = SearchBox?.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            yield return Loc.S("settings.models.empty.filter.search", search);
        }

        if (_providerFilter != "All providers")
        {
            yield return _providerFilter;
        }

        if (_typeFilter != "All types")
        {
            yield return _typeFilter;
        }

        if (_locationFilter != "Cloud & Offline")
        {
            yield return _locationFilter;
        }

        if (_vocabFilterEnabled)
        {
            yield return Loc.S("settings.models.feature.vocabulary");
        }

        if (_cloudAvailableFilterEnabled)
        {
            yield return Loc.S("provider.hyperwhisper");
        }

        if (!string.IsNullOrEmpty(_languageFilter))
        {
            yield return LibraryLanguageFilter.DisplayName(_languageFilter);
        }
    }

    private static bool MatchesSearch(LibraryModel model, string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        return model.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || model.ProviderName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || (model.Detail?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
            || (model.Tag?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static bool MatchesProvider(LibraryModel model, string provider) => provider switch
    {
        "OpenAI" => model.ProviderName.Contains("OpenAI", StringComparison.OrdinalIgnoreCase),
        "Anthropic" => model.ProviderName.Contains("Anthropic", StringComparison.OrdinalIgnoreCase),
        "Groq" => model.ProviderName.Contains("Groq", StringComparison.OrdinalIgnoreCase),
        "Deepgram" => model.ProviderName.Contains("Deepgram", StringComparison.OrdinalIgnoreCase),
        "Gemini" => model.ProviderName.Contains("Gemini", StringComparison.OrdinalIgnoreCase),
        "Local" => model.LocationKind == LibraryModelLocationKind.Offline || model.ProviderName.Contains("Local", StringComparison.OrdinalIgnoreCase),
        _ => true
    };

    private static bool MatchesType(LibraryModel model, string type) => type switch
    {
        "Voice models" => model.Kind == LibraryModelKind.Voice,
        "Language models" => model.Kind == LibraryModelKind.Text,
        _ => true
    };

    private static bool MatchesLocation(LibraryModel model, string location) => location switch
    {
        "Cloud Only" => model.LocationKind == LibraryModelLocationKind.Cloud,
        "Offline Only" => model.LocationKind == LibraryModelLocationKind.Offline,
        "Installed Only" => model.IsInstalled,
        _ => true
    };

    // Only voice models are language-filtered; text (post-processing) models
    // always pass. Empty filter = Any language.
    private static bool MatchesLanguage(LibraryModel model, string languageCode)
        => string.IsNullOrEmpty(languageCode)
           || model.Kind != LibraryModelKind.Voice
           || model.SupportsLanguage(languageCode);

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        SearchBox?.Clear();

        _providerFilter = "All providers";
        _typeFilter = "All types";
        _locationFilter = "Cloud & Offline";
        _vocabFilterEnabled = false;
        _cloudAvailableFilterEnabled = false;

        ProviderFilterLabel.Text = "All providers";
        foreach (var mi in ProviderMenu.Items.OfType<MenuItem>())
        {
            mi.IsChecked = mi.Tag?.ToString() == "All providers";
        }

        TypeVoiceMenuItem.IsChecked = false;
        TypeLanguageMenuItem.IsChecked = false;
        LocationCloudMenuItem.IsChecked = false;
        LocationOfflineMenuItem.IsChecked = false;
        VocabFilterMenuItem.IsChecked = false;
        CloudAvailableFilterMenuItem.IsChecked = false;

        // Reset the language filter too (SetLanguageFilter persists + re-applies).
        SetLanguageFilter(LibraryLanguageFilter.AnyCode);

        UpdateFilterChips();
        ApplyFilters();
    }

    private IEnumerable<LibraryModelViewModel> ApplySort(IEnumerable<LibraryModelViewModel> rows)
    {
        if (_sortColumn == null)
        {
            return rows;
        }

        var asc = _sortAscending;
        return _sortColumn switch
        {
            SortColumn.Name => asc
                ? rows.OrderBy(r => r.Model.DisplayName, StringComparer.OrdinalIgnoreCase)
                : rows.OrderByDescending(r => r.Model.DisplayName, StringComparer.OrdinalIgnoreCase),
            SortColumn.Type => asc
                ? rows.OrderBy(r => r.Model.Kind).ThenBy(r => r.Model.DisplayName, StringComparer.OrdinalIgnoreCase)
                : rows.OrderByDescending(r => r.Model.Kind).ThenBy(r => r.Model.DisplayName, StringComparer.OrdinalIgnoreCase),
            SortColumn.Rating => asc
                ? rows.OrderBy(r => r.Model.Speed + r.Model.Accuracy)
                      .ThenBy(r => r.Model.Accuracy)
                      .ThenBy(r => r.Model.Speed)
                : rows.OrderByDescending(r => r.Model.Speed + r.Model.Accuracy)
                      .ThenByDescending(r => r.Model.Accuracy)
                      .ThenByDescending(r => r.Model.Speed),
            SortColumn.Location => asc
                ? rows.OrderBy(r => r.Model.LocationKind).ThenBy(r => r.Model.DisplayName, StringComparer.OrdinalIgnoreCase)
                : rows.OrderByDescending(r => r.Model.LocationKind).ThenBy(r => r.Model.DisplayName, StringComparer.OrdinalIgnoreCase),
            _ => rows
        };
    }

    private void SortHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        if (!Enum.TryParse<SortColumn>(tag, out var column)) return;
        ToggleSort(column);
    }

    private void ToggleSort(SortColumn column)
    {
        if (_sortColumn == column)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortColumn = column;
            _sortAscending = false;
        }

        UpdateSortIndicators();
        ApplyFilters();
    }

    private void UpdateSortIndicators()
    {
        SetSortGlyph(NameSortGlyph, SortColumn.Name);
        SetSortGlyph(TypeSortGlyph, SortColumn.Type);
        SetSortGlyph(RatingSortGlyph, SortColumn.Rating);
        SetSortGlyph(LocationSortGlyph, SortColumn.Location);
    }

    private void SetSortGlyph(TextBlock glyph, SortColumn column)
    {
        if (_sortColumn != column)
        {
            glyph.Visibility = Visibility.Collapsed;
            return;
        }

        glyph.Visibility = Visibility.Visible;
        // E70D = chevron down (descending), E70E = chevron up (ascending)
        glyph.Text = _sortAscending ? "" : "";
    }

    private void UpdateSummary(IReadOnlyCollection<LibraryModelViewModel>? visibleRows = null)
    {
        var total = _allRows.Count;
        var rows = visibleRows ?? _allRows;
        var visible = rows.Count;
        var cloud = rows.Count(r => r.Model.LocationKind == LibraryModelLocationKind.Cloud);
        var offline = rows.Count(r => r.Model.LocationKind == LibraryModelLocationKind.Offline);
        var installed = rows.Count(r => r.Model.IsInstalled);
        var endpoints = _allRows.Count(r => r.Model.Source == LibraryModelSource.CustomEndpoint);
        var modelCountText = visible == total ? $"{total} models" : $"{visible} of {total} models";
        LibrarySummaryText.Text = $"{modelCountText}: {cloud} Cloud, {offline} Offline, {installed} Installed";
        CustomEndpointsSummaryText.Text = endpoints == 0
            ? "No custom endpoints configured. Add a local or hosted OpenAI-compatible chat completions endpoint for post-processing."
            : $"{endpoints} custom endpoint{(endpoints == 1 ? "" : "s")} available in the table. Use the row actions to edit, duplicate, or delete.";
    }

    private void LibraryRowPrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LibraryModelViewModel row }) return;

        if (row.Model.Source == LibraryModelSource.CustomEndpoint
            && row.Model.Payload is CustomPostProcessingEndpoint endpoint)
        {
            EditEndpoint(endpoint);
            return;
        }

        switch (row.Model.StatusKind)
        {
            case LibraryModelStatusKind.Locked:
            case LibraryModelStatusKind.Error:
            case LibraryModelStatusKind.Enabled when row.Model.IsCloud:
                OpenProviderApiKeyModal(row);
                break;
            case LibraryModelStatusKind.Downloadable:
                DownloadModel(row);
                break;
            case LibraryModelStatusKind.Downloading:
                CancelDownload(row.Model.Id);
                break;
        }
    }

    private void LibraryRowDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LibraryModelViewModel { Model.Payload: CustomPostProcessingEndpoint endpoint } })
        {
            return;
        }

        CustomEndpointManager.Instance.DuplicateEndpoint(endpoint.Id);
        RebuildLibrary();
    }

    private void LibraryRowCancel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LibraryModelViewModel row })
        {
            CancelDownload(row.Model.Id);
        }
    }

    private void LibraryRowDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LibraryModelViewModel row }) return;

        switch (row.Model.Payload)
        {
            case WhisperModelInfo whisper:
                DeleteWhisper(row, whisper);
                break;
            case ParakeetModelInfo parakeet:
                DeleteParakeet(row, parakeet);
                break;
            case LocalLlmModelInfo localLlm:
                DeleteLocalLlm(row, localLlm);
                break;
            case CustomPostProcessingEndpoint endpoint:
                DeleteEndpoint(endpoint);
                break;
        }
    }

    private void DownloadModel(LibraryModelViewModel row)
    {
        // Local model downloads are unlimited (open source) — no gate.
        SetRowDownloading(row, 0);
        if (!ModelDownloadService.Instance.TryStartDownload(row.Model))
        {
            RebuildLibrary();
        }
    }

    private static void SetRowDownloading(LibraryModelViewModel row, double progress)
    {
        row.Model = WithDownloadState(row.Model, progress);
    }

    private static LibraryModel WithDownloadState(LibraryModel model, double progress)
    {
        return new LibraryModel
        {
            Id = model.Id,
            DisplayName = model.DisplayName,
            ProviderName = model.ProviderName,
            ProviderAssetName = model.ProviderAssetName,
            Kind = model.Kind,
            LocationKind = model.LocationKind,
            StatusKind = LibraryModelStatusKind.Downloading,
            Source = model.Source,
            SizeDescription = model.SizeDescription,
            Tag = model.Tag,
            Detail = model.Detail,
            DetailToolTip = model.DetailToolTip,
            StatusMessage = model.StatusMessage,
            Speed = model.Speed,
            Accuracy = model.Accuracy,
            SupportsCustomVocabulary = model.SupportsCustomVocabulary,
            AvailableViaHyperWhisperCloud = model.AvailableViaHyperWhisperCloud,
            IsHyperWhisperProvider = model.IsHyperWhisperProvider,
            DownloadProgress = progress,
            Payload = model.Payload
        };
    }

    private void CancelDownload(string id)
    {
        ModelDownloadService.Instance.CancelDownload(id);
    }

    private void DeleteWhisper(LibraryModelViewModel row, WhisperModelInfo model)
    {
        if (IsWhisperModelInUse(model.Type, out var modeName))
        {
            ShowModelInUse(row.Model.DisplayName, modeName);
            return;
        }

        if (!ConfirmDelete(row.Model.DisplayName)) return;

        var result = _whisperService.DeleteModel(model);
        if (result.IsFailure)
        {
            ShowDeleteFailed(row.Model.DisplayName, result.Error ?? Loc.S("common.error"));
            RebuildLibrary();
            return;
        }

        RebuildLibrary();
    }

    private void DeleteParakeet(LibraryModelViewModel row, ParakeetModelInfo model)
    {
        if (IsParakeetModelInUse(model.Id, out var modeName))
        {
            ShowModelInUse(row.Model.DisplayName, modeName);
            return;
        }

        if (!ConfirmDelete(row.Model.DisplayName)) return;

        var result = _parakeetService.DeleteModel(model);
        if (result.IsFailure)
        {
            ShowDeleteFailed(row.Model.DisplayName, result.Error ?? Loc.S("common.error"));
            RebuildLibrary();
            return;
        }

        RebuildLibrary();
    }

    private void DeleteLocalLlm(LibraryModelViewModel row, LocalLlmModelInfo model)
    {
        if (IsLocalLlmModelInUse(model.Id, out var modeName))
        {
            ShowModelInUse(row.Model.DisplayName, modeName);
            return;
        }

        if (!ConfirmDelete(row.Model.DisplayName)) return;

        var result = _localLlmService.DeleteModel(model);
        if (result.IsFailure)
        {
            ShowDeleteFailed(row.Model.DisplayName, result.Error ?? Loc.S("common.error"));
            RebuildLibrary();
            return;
        }

        RebuildLibrary();
    }

    private bool ConfirmDelete(string displayName)
    {
        var result = WpfMessageBox.Show(
            Loc.S("settings.models.delete.confirm.message", displayName),
            Loc.S("settings.models.delete.confirm.title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        return result == MessageBoxResult.Yes;
    }

    private static bool IsWhisperModelInUse(string modelId, out string modeName)
    {
        var mode = ModeService.Instance.GetAllModes()
            .FirstOrDefault(m => m.ProviderType != "cloud"
                && string.Equals(m.LocalEngine, "whisper", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(m.Model, modelId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(m.ModelType, modelId, StringComparison.OrdinalIgnoreCase)));
        modeName = mode?.Name ?? "";
        return mode != null;
    }

    private static bool IsParakeetModelInUse(string modelId, out string modeName)
    {
        var mode = ModeService.Instance.GetAllModes()
            .FirstOrDefault(m => m.ProviderType != "cloud"
                && string.Equals(m.LocalEngine, "parakeet", StringComparison.OrdinalIgnoreCase)
                && string.Equals(m.LocalParakeetModel, modelId, StringComparison.OrdinalIgnoreCase));
        modeName = mode?.Name ?? "";
        return mode != null;
    }

    private static bool IsLocalLlmModelInUse(string modelId, out string modeName)
    {
        var targetModelId = LanguageModelInfo.MigrateModelId(modelId);
        var mode = ModeService.Instance.GetAllModes()
            .FirstOrDefault(m => string.Equals(m.PostProcessingProvider, PostProcessingProvider.LocalLlm.ToStringValue(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    LanguageModelInfo.MigrateModelId(m.LocalPostProcessingModel ?? m.LanguageModel),
                    targetModelId,
                    StringComparison.OrdinalIgnoreCase));
        modeName = mode?.Name ?? "";
        return mode != null;
    }

    private static void ShowModelInUse(string modelName, string modeName)
    {
        WpfMessageBox.Show(
            $"{modelName} is used by the \"{modeName}\" mode. Switch that mode to another model before deleting it.",
            Loc.S("settings.models.alert.cannotDelete.title"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static void ShowDeleteFailed(string modelName, string error)
    {
        WpfMessageBox.Show(
            Loc.S("settings.models.deleteFailed.message", modelName, error),
            Loc.S("settings.models.deleteFailed.title"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShouldOpenModelLibraryApiKeys))
        {
            ConsumeApiKeysManagerRequestIfPending();
        }
    }

    private void ConsumeApiKeysManagerRequestIfPending()
    {
        if (_mainViewModel == null || !_mainViewModel.ShouldOpenModelLibraryApiKeys) return;

        // Clear the flag first so OpenApiKeyManager's blocking ShowDialog doesn't re-enter via PropertyChanged.
        _mainViewModel.ShouldOpenModelLibraryApiKeys = false;
        Dispatcher.BeginInvoke(new Action(OpenApiKeyManager));
    }

    private void OpenApiKeyManager()
    {
        var window = new Window
        {
            Title = Loc.S("settings.nav.apiKeys"),
            Width = 760,
            Height = 760,
            Background = (System.Windows.Media.Brush)FindResource("ContentBackgroundBrush"),
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ApiKeysSettingsPage
            {
                Background = (System.Windows.Media.Brush)FindResource("ContentBackgroundBrush")
            }
        };

        window.ShowDialog();
        RebuildLibrary();
        _ = RefreshProviderHealthAsync();
    }

    private void OpenProviderApiKeyModal(LibraryModelViewModel row)
    {
        Window? window = row.Model.Payload switch
        {
            CloudTranscriptionModel model when model.Provider.RequiresApiKey() =>
                new ProviderApiKeyWindow(model.Provider),
            LanguageModelInfo model when model.Provider.RequiresApiKey() =>
                new ProviderApiKeyWindow(model.Provider),
            _ => null
        };

        if (window == null)
        {
            OpenApiKeyManager();
            return;
        }

        window.Owner = Window.GetWindow(this);
        window.ShowDialog();
        RebuildLibrary();
        _ = RefreshProviderHealthAsync();
    }

    private void AddCustomEndpoint_Click(object sender, RoutedEventArgs e)
    {
        var window = new CustomEndpointWindow { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true)
        {
            RebuildLibrary();
        }
    }

    private void DeleteEndpoint(CustomPostProcessingEndpoint endpoint)
    {
        var result = WpfMessageBox.Show(
            $"Delete \"{endpoint.Name}\"? This cannot be undone.",
            "Delete Endpoint",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;
        CustomEndpointManager.Instance.DeleteEndpoint(endpoint.Id);
        RebuildLibrary();
    }

    private void EditEndpoint(CustomPostProcessingEndpoint endpoint)
    {
        var window = new CustomEndpointWindow(endpoint) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true)
        {
            RebuildLibrary();
        }
    }

    private void OnEndpointsChanged(object? sender, EventArgs e)
        => Dispatcher.Invoke(RebuildLibrary);

    private void OnApiKeysChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(RebuildLibrary);
        _ = RefreshProviderHealthAsync();
    }

    private void OnTranscriptionProviderHealthChanged(object? sender, CloudTranscriptionProvider e)
        => Dispatcher.Invoke(RebuildLibrary);

    private void OnPostProcessingProviderHealthChanged(object? sender, PostProcessingProvider e)
        => Dispatcher.Invoke(RebuildLibrary);

    private void OnModelDownloadChanged(object? sender, ModelDownloadChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (e.IsCompleted)
            {
                if (!e.IsSuccess && !string.IsNullOrWhiteSpace(e.Error)
                    && !e.Error.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    WpfMessageBox.Show(
                        Loc.S("settings.models.downloadFailed.message", e.DisplayName ?? e.ModelId, e.Error),
                        Loc.S("settings.models.downloadFailed.title"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }

                RebuildLibrary();
                return;
            }

            var row = _allRows.FirstOrDefault(r => r.Model.Id == e.ModelId);
            if (row != null)
            {
                SetRowDownloading(row, e.Progress);
                ApplyFilters();
            }
        });
    }
}
