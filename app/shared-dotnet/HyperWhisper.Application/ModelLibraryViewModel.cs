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
    public string DisplayName => Capability.DisplayName;
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
        // ModelLanguageCount first: `SupportedLanguages` is PROVIDER-level for a
        // cloud row, so where a provider's models do not share one table it is
        // their union and printing its length tells a MAI-Transcribe 1.5 user
        // the model speaks 18 languages it cannot transcribe.
        Capability.SupportsAllLanguages ? "All languages"
            : Capability.IsEnglishOnly ? "English only"
            : (Capability.ModelLanguageCount ?? Capability.SupportedLanguages.Count) is var languageCount
                && languageCount > 0 ? $"{languageCount} languages/locales" : null,
        Capability.CloudTierEligible ? "Cloud tier" : null,
        Capability.ByokEligible ? "BYOK" : null,
    }.Where(value => value is not null));
    public string? CredentialAccount => Capability.CredentialAccount;
    public string? CredentialNavigationActionId => Capability.CredentialAccount is null ? null
        : string.Equals(Capability.CredentialAccount, "LicenseKey", StringComparison.Ordinal)
            ? "navigate.account"
            : $"navigate.credentials:{Capability.CredentialAccount}";
    public string? AccountNavigationActionId => Capability.CloudTierEligible ? "navigate.account" : null;
    public bool Installed { get => _installed; set => Set(ref _installed, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public ReadinessState Readiness { get => _readiness; private set => Set(ref _readiness, value); }
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

public enum ModelLibrarySort { Recommended, Name, Provider, Readiness }

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
        Selected = Items.FirstOrDefault(item => item.Model?.IsRecommended == true) ?? Items.FirstOrDefault();
        if (_readiness is not null)
        {
            _readiness.ReadinessChanged += OnReadinessChanged;
            _readiness.CredentialInvalidated += OnCredentialInvalidated;
        }
    }
    public ObservableCollection<ManagedModelViewModel> Items { get; } = new();
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

    private void ApplyView()
    {
        var selectedKey = Selected?.Id;
        IEnumerable<ManagedModelViewModel> query = _allItems.Where(item =>
            (_deploymentFilter is null || item.Capability.Deployment == _deploymentFilter)
            && (_workloadFilter is null || item.Capability.Workload == _workloadFilter)
            && (_surfaceFilter is null || item.Capability.Surface == _surfaceFilter)
            && (string.IsNullOrWhiteSpace(_searchText)
                || item.DisplayName.Contains(_searchText.Trim(), StringComparison.OrdinalIgnoreCase)
                || item.ProviderId.Contains(_searchText.Trim(), StringComparison.OrdinalIgnoreCase)
                || item.ModelId.Contains(_searchText.Trim(), StringComparison.OrdinalIgnoreCase)));
        query = _sort switch
        {
            ModelLibrarySort.Name => query.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase),
            ModelLibrarySort.Provider => query.OrderBy(item => item.ProviderId, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase),
            ModelLibrarySort.Readiness => query.OrderBy(item => item.Readiness).ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase),
            _ => query.OrderByDescending(item => item.Model?.IsRecommended == true)
                .ThenBy(item => item.Capability.Deployment)
                .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase),
        };
        Items.Clear();
        foreach (var item in query) Items.Add(item);
        if (selectedKey is not null) Selected = Items.FirstOrDefault(item => item.Id == selectedKey);
        if (Selected is null) Selected = Items.FirstOrDefault();
        RaiseCommands();
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
