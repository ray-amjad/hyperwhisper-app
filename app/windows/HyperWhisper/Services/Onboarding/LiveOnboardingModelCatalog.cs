// The live IOnboardingModelCatalog adapter for the first-run flow. Part of the onboarding
// composition described in OnboardingLiveDependencies.cs; split out of that file (#941).

using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.Utilities;
using HyperWhisper.ViewModels.Onboarding;

namespace HyperWhisper.Services.Onboarding;

// =============================================================================
// Model catalog
// =============================================================================

/// <summary>
/// The curated on-device shortlist plus download state for BOTH engines.
///
/// Windows is structurally better placed than macOS here: ModelDownloadService
/// raises ONE DownloadChanged stream carrying Progress / IsCompleted / IsSuccess
/// / Error for every engine, so the macOS bug where a Parakeet failure could only
/// ever be reported as a Whisper one cannot recur. The flow still funnels it
/// through a single SetupErrorMessage keyed on the selected model's engine.
/// </summary>
public sealed class LiveOnboardingModelCatalog : IOnboardingModelCatalog, IDisposable
{
    private readonly WhisperModelService _whisper;
    private readonly ParakeetModelService _parakeet;
    private readonly ModelDownloadService _downloads;

    private OnboardingDownloadErrors _errors = OnboardingDownloadErrors.None;

    /// <summary>
    /// Download progress per Model Library row id, as a FRACTION.
    ///
    /// Concurrent, not a plain Dictionary. <see cref="OnDownloadChanged"/> now
    /// marshals to the UI thread (see OnboardingUiDispatch) so writes and the
    /// binding layer's reads land on one thread in the app - but this type is
    /// the honest one for a store whose producer is a background download
    /// thread and whose consumers are public methods any caller may reach. The
    /// unsynchronised version could tear a read against an insert-and-resize:
    /// a torn value, an InvalidOperationException, or an unrecoverable spin
    /// inside Dictionary while the Setup step was on screen.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _progress =
        new(StringComparer.Ordinal);

    private bool _disposed;

    public LiveOnboardingModelCatalog(
        WhisperModelService whisper,
        ParakeetModelService parakeet,
        ModelDownloadService downloads)
    {
        _whisper = whisper;
        _parakeet = parakeet;
        _downloads = downloads;
        _downloads.DownloadChanged += OnDownloadChanged;
    }

    public event EventHandler<OnboardingDownloadErrors>? DownloadErrorsChanged;

    public event EventHandler? DownloadActivity;

    /// <summary>
    /// The shortlist, in macOS's order. The ids are the WINDOWS ids, because
    /// OnboardingModelSelection.Id is written verbatim onto the Mode row: Parakeet
    /// is "parakeet-v2"/"parakeet-v3" here (macOS spells them
    /// "parakeet-tdt-0.6b-v2"/"-v3") and Whisper's turbo build is
    /// "large-v3-turbo" (macOS spells it "large-v3_turbo"). Sizes are read from
    /// the same catalogs the Model Library uses so the two screens agree.
    /// </summary>
    public IReadOnlyList<OnboardingModelSelection> Models => SupportedModels;

    internal static readonly IReadOnlyList<OnboardingModelSelection> CuratedModels = BuildCurated();

    /// <summary>
    /// The shortlist minus the engines this machine cannot run.
    ///
    /// ModelLibraryManager already refuses to enable a row whose engine is
    /// unsupported (PlatformHelper.SupportsWhisperTranscription /
    /// SupportsParakeetTranscription), and ModeService checks the same pair before
    /// it will use a local Mode - but first run offered the whole shortlist and
    /// RECOMMENDED Parakeet. On an ARM64 machine with no native sherpa-onnx daemon
    /// that is a model the user can download, select, and commit into a Mode that
    /// then fails on the very next screen. Filtered at the source so the Configure
    /// list, the recommendation and the committed Mode all agree.
    ///
    /// Computed once. Both predicates read the OS architecture and the presence of
    /// a payload in the app directory; neither changes while the process runs.
    /// </summary>
    internal static readonly IReadOnlyList<OnboardingModelSelection> SupportedModels = BuildSupported();

    private static IReadOnlyList<OnboardingModelSelection> BuildSupported()
    {
        var supported = CuratedModels
            .Where(m => m.Kind switch
            {
                OnboardingModelKind.Whisper => PlatformHelper.SupportsWhisperTranscription,
                OnboardingModelKind.Parakeet => PlatformHelper.SupportsParakeetTranscription,
                _ => false
            })
            .ToList();

        if (supported.Count != CuratedModels.Count)
        {
            LoggingService.Info(
                $"LiveOnboardingModelCatalog: {CuratedModels.Count - supported.Count} of "
                + $"{CuratedModels.Count} curated models are unsupported on this platform "
                + $"({PlatformHelper.ArchitectureName})");
        }

        // The recommendation has to survive the filter. Parakeet V2 carries it, and
        // it is the first thing to go on ARM64 - leaving a list where nothing is
        // recommended and the flow's default pick is whatever happened to be first.
        if (supported.Count > 0 && !supported.Any(m => m.IsRecommended))
        {
            supported[0] = supported[0] with { IsRecommended = true };
        }

        return supported;
    }

    private static IReadOnlyList<OnboardingModelSelection> BuildCurated()
    {
        string ParakeetSize(string id) =>
            ParakeetModelInfo.AllModels.FirstOrDefault(m => m.Id == id)?.Size ?? "";

        string WhisperSize(string type) =>
            WhisperModelInfo.AllModels.FirstOrDefault(m => m.Type == type)?.Size ?? "";

        return new[]
        {
            new OnboardingModelSelection(
                "parakeet-v2", OnboardingModelKind.Parakeet,
                "Parakeet V2", "onboarding.model.parakeetV2.subtitle",
                ParakeetSize("parakeet-v2"), 5, 3, IsRecommended: true),
            new OnboardingModelSelection(
                "parakeet-v3", OnboardingModelKind.Parakeet,
                "Parakeet V3", "onboarding.model.parakeetV3.subtitle",
                ParakeetSize("parakeet-v3"), 5, 3, IsRecommended: false),
            new OnboardingModelSelection(
                "base", OnboardingModelKind.Whisper,
                "Whisper Base", "onboarding.model.whisperBase.subtitle",
                WhisperSize("base"), 5, 1, IsRecommended: false),
            new OnboardingModelSelection(
                "large-v3-turbo", OnboardingModelKind.Whisper,
                "Whisper Large v3 Turbo", "onboarding.model.whisperTurbo.subtitle",
                WhisperSize("large-v3-turbo"), 4, 3, IsRecommended: false),
        };
    }

    /// <summary>
    /// ModelDownloadService keys everything on the Model Library's row id
    /// ("whisper-base", "parakeet-parakeet-v2"), not on the raw model id the Mode
    /// row stores. One place converts.
    /// </summary>
    internal static string LibraryId(OnboardingModelSelection model) => model.Kind switch
    {
        OnboardingModelKind.Whisper => $"whisper-{model.Id}",
        OnboardingModelKind.Parakeet => $"parakeet-{model.Id}",
        _ => model.Id
    };

    public bool IsInstalled(OnboardingModelSelection model)
    {
        try
        {
            return model.Kind switch
            {
                OnboardingModelKind.Whisper => WhisperInfo(model.Id) is { } w && _whisper.IsModelDownloaded(w),
                OnboardingModelKind.Parakeet => ParakeetInfo(model.Id) is { } p && _parakeet.IsModelDownloaded(p),
                _ => false
            };
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LiveOnboardingModelCatalog: install check failed for {model.Id}: {ex.Message}");
            return false;
        }
    }

    public bool IsDownloading(OnboardingModelSelection model) => _downloads.IsDownloading(LibraryId(model));

    public double Progress(OnboardingModelSelection model) =>
        _progress.TryGetValue(LibraryId(model), out var value) ? value : 0;

    public void StartDownload(OnboardingModelSelection model)
    {
        var row = BuildLibraryRow(model);
        if (row is null)
        {
            LoggingService.Warn($"LiveOnboardingModelCatalog: no catalog entry for '{model.Id}'");
            PublishError(model.Kind, Loc.S("onboarding.setup.model.notFound"));
            return;
        }

        // A new attempt supersedes the last failure for this engine, so the setup
        // step does not keep showing an error while a retry is running.
        PublishError(model.Kind, null);
        _downloads.TryStartDownload(row);
    }

    /// <summary>
    /// The minimum LibraryModel ModelDownloadService needs: it only reads Id,
    /// DisplayName and Payload. Building it here rather than running the whole
    /// ModelLibraryManager.Rebuild() keeps onboarding off the cloud-catalog and
    /// health-probe paths it has no use for.
    /// </summary>
    private LibraryModel? BuildLibraryRow(OnboardingModelSelection model)
    {
        object? payload = model.Kind switch
        {
            OnboardingModelKind.Whisper => WhisperInfo(model.Id),
            OnboardingModelKind.Parakeet => ParakeetInfo(model.Id),
            _ => null
        };

        if (payload is null) return null;

        return new LibraryModel
        {
            Id = LibraryId(model),
            DisplayName = model.DisplayName,
            ProviderName = model.Kind == OnboardingModelKind.Whisper ? "Whisper" : "NVIDIA",
            ProviderAssetName = ProviderAssetNameFor(model.Kind),
            Kind = LibraryModelKind.Voice,
            LocationKind = LibraryModelLocationKind.Offline,
            StatusKind = LibraryModelStatusKind.Downloadable,
            Source = model.Kind == OnboardingModelKind.Whisper ? LibraryModelSource.Whisper : LibraryModelSource.Parakeet,
            SizeDescription = model.Size,
            Speed = model.Speed,
            Accuracy = model.Accuracy,
            SupportsCustomVocabulary = false,
            AvailableViaHyperWhisperCloud = false,
            Payload = payload
        };
    }

    /// <summary>
    /// The Assets/Providers logo for a local engine.
    ///
    /// The Parakeet arm matches ParakeetModelInfo.ProviderAssetName, which is what
    /// the Model Library uses. It did not: the name here had a "Local" in it that
    /// no PNG has ever had, so the row this builds drew nothing where the Library
    /// draws the NVIDIA mark. Named and internal so the smoke suite can assert both
    /// arms against ProviderAssets.ShippedNames rather than trusting a literal
    /// buried in an object initialiser.
    /// </summary>
    internal static string ProviderAssetNameFor(OnboardingModelKind kind) =>
        kind == OnboardingModelKind.Whisper ? "providerLocalWhisper" : "providerParakeet";

    private static WhisperModelInfo? WhisperInfo(string id) =>
        WhisperModelInfo.AllModels.FirstOrDefault(m => m.Type == id);

    private static ParakeetModelInfo? ParakeetInfo(string id) =>
        ParakeetModelInfo.AllModels.FirstOrDefault(m => m.Id == id);

    /// <summary>
    /// ModelDownloadService raises this synchronously, with no marshalling, from
    /// inside its <c>Task.Run(() =&gt; RunDownloadAsync(download))</c>. Everything
    /// below either mutates state the UI thread reads or raises an event the
    /// flow turns into PropertyChanged, so the whole body is posted rather than
    /// only the event: splitting the two would leave the write racing the read
    /// it is meant to feed.
    /// </summary>
    private void OnDownloadChanged(object? sender, ModelDownloadChangedEventArgs e)
    {
        var model = CuratedModels.FirstOrDefault(m => LibraryId(m) == e.ModelId);
        if (model is null) return;

        OnboardingUiDispatch.Post(() =>
        {
            // A tick queued before Dispose and delivered after it has nothing
            // left to update.
            if (_disposed) return;

            _progress[e.ModelId] = ProgressFraction(e.Progress);

            if (e.IsCompleted)
            {
                _progress.TryRemove(e.ModelId, out _);
                // Carry the engine the FAILURE belongs to, so a Whisper failure is
                // never attributed to a selected Parakeet model.
                PublishError(model.Kind, e.IsSuccess ? null : (e.Error ?? Loc.S("onboarding.setup.model.downloadFailed")));
            }

            DownloadActivity?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>
    /// UNIT CONVERSION, not decoration.
    ///
    /// ModelDownloadService reports a PERCENTAGE: it computes
    /// <c>Math.Clamp(p * 100, 0, 100)</c> and its notify gate only fires once a
    /// full point has passed, so the first value onboarding ever sees is ~1.0. The
    /// seam is a FRACTION: OnboardingProgressBar clamps Value to [0,1] and
    /// SelectedModelProgressPercent multiplies by 100. Storing the percentage
    /// verbatim painted the bar full and the pill "Downloading... 100%" at 1% of a
    /// ~170 s Parakeet download, which reads as a hang.
    /// </summary>
    internal static double ProgressFraction(double percent) => Math.Clamp(percent / 100.0, 0, 1);

    private void PublishError(OnboardingModelKind kind, string? message)
    {
        var next = kind == OnboardingModelKind.Whisper
            ? _errors with { Whisper = message }
            : _errors with { Parakeet = message };

        if (next == _errors) return;

        _errors = next;
        DownloadErrorsChanged?.Invoke(this, _errors);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _downloads.DownloadChanged -= OnDownloadChanged;
    }
}
