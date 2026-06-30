using System.Windows;

namespace HyperWhisper.Models;

public enum LibraryModelKind
{
    Voice,
    Text
}

public enum LibraryModelLocationKind
{
    Cloud,
    Offline
}

public enum LibraryModelStatusKind
{
    Enabled,
    Locked,
    Error,
    Downloadable,
    Downloading
}

public enum LibraryModelSource
{
    CloudTranscription,
    PostProcessing,
    Whisper,
    Parakeet,
    LocalLlm,
    CustomEndpoint
}

public sealed class LibraryModel
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string ProviderName { get; init; }
    public required string ProviderAssetName { get; init; }
    public required LibraryModelKind Kind { get; init; }
    public required LibraryModelLocationKind LocationKind { get; init; }
    public required LibraryModelStatusKind StatusKind { get; init; }
    public required LibraryModelSource Source { get; init; }
    public string? SizeDescription { get; init; }
    public string? Tag { get; init; }
    public string? Detail { get; init; }
    public string? DetailToolTip { get; init; }
    public string? StatusMessage { get; init; }
    public int Speed { get; init; }
    public int Accuracy { get; init; }
    public required bool SupportsCustomVocabulary { get; init; }
    public required bool AvailableViaHyperWhisperCloud { get; init; }
    /// <summary>
    /// Base ISO language codes (region/script stripped, e.g. "en", "es", "zh")
    /// this voice model can transcribe, for the Model Library language filter.
    /// Empty when <see cref="SupportsAllLanguages"/> is true and for text models.
    /// Cloud values come from the shared catalog; local values are resolved
    /// in-code in <see cref="Services.ModelLibraryManager"/>.
    /// </summary>
    public IReadOnlyCollection<string> SupportedLanguages { get; init; } = Array.Empty<string>();
    /// <summary>
    /// True when the model passes every language filter — text models,
    /// Whisper-family, Gemini, Google Chirp, etc. Defaulted so non-voice rows
    /// need not set it.
    /// </summary>
    public bool SupportsAllLanguages { get; init; } = true;
    /// <summary>
    /// True when this row's own provider IS HyperWhisper Cloud — used to
    /// suppress the "HyperWhisper Cloud" pill on the row that already says it
    /// in its provider name. Defaults to false so non-HWC rows behave normally.
    /// </summary>
    public bool IsHyperWhisperProvider { get; init; }
    public double? DownloadProgress { get; init; }
    public object? Payload { get; init; }

    public bool IsInstalled => LocationKind == LibraryModelLocationKind.Offline && StatusKind == LibraryModelStatusKind.Enabled;
    public bool IsCloud => LocationKind == LibraryModelLocationKind.Cloud;
    public bool IsVoice => Kind == LibraryModelKind.Voice;
    public bool IsText => Kind == LibraryModelKind.Text;

    /// <summary>
    /// Whether this model stays visible when the library is filtered to
    /// <paramref name="baseCode"/> (already region-stripped). Voice-only concept;
    /// callers gate on <see cref="IsVoice"/> before applying it.
    /// </summary>
    public bool SupportsLanguage(string baseCode)
        => SupportsAllLanguages || SupportedLanguages.Contains(baseCode);
}

public sealed class LibraryModelViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private LibraryModel _model;

    public LibraryModelViewModel(LibraryModel model)
    {
        _model = model;
    }

    public LibraryModel Model
    {
        get => _model;
        set
        {
            _model = value;
            OnPropertyChanged(string.Empty);
        }
    }

    public string Id => Model.Id;
    public string DisplayName => Model.DisplayName;
    public string ProviderName => Model.ProviderName;
    public string ProviderAssetPath => $"/Assets/Providers/{Model.ProviderAssetName}.png";
    public string TypeText => Model.Kind == LibraryModelKind.Voice ? "Voice" : "Language";
    public string LocationText => Model.LocationKind == LibraryModelLocationKind.Cloud ? "Cloud" : (Model.SizeDescription ?? "Offline");
    public string CompactMetadataText => $"{TypeText} - Fast {FormatRating(Model.Speed)} - Acc {FormatRating(Model.Accuracy)} - {LocationText}";
    public string TagText => Model.Tag ?? "";
    public Visibility TagVisibility => string.IsNullOrWhiteSpace(Model.Tag) ? Visibility.Collapsed : Visibility.Visible;
    public string Detail => Model.Detail ?? "";
    public string? DetailToolTip
    {
        get
        {
            var text = Model.DetailToolTip ?? Model.Detail;
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
    }
    public Visibility DetailVisibility => string.IsNullOrWhiteSpace(Model.Detail) ? Visibility.Collapsed : Visibility.Visible;
    public string StatusText => Model.StatusKind switch
    {
        LibraryModelStatusKind.Enabled when Model.Source == LibraryModelSource.CustomEndpoint => "Connected",
        LibraryModelStatusKind.Enabled => Model.IsCloud ? "Connected" : "Installed",
        LibraryModelStatusKind.Locked => "Connect",
        LibraryModelStatusKind.Error => Model.StatusMessage ?? "Needs attention",
        LibraryModelStatusKind.Downloadable => "Download",
        LibraryModelStatusKind.Downloading => $"{Math.Max(1, Model.DownloadProgress ?? 0):F0}%",
        _ => ""
    };
    public string StatusGlyph => Model.StatusKind switch
    {
        LibraryModelStatusKind.Enabled when Model.Source == LibraryModelSource.CustomEndpoint => "\uE753",
        LibraryModelStatusKind.Enabled => Model.IsCloud ? "\uE753" : "\uE73E",
        LibraryModelStatusKind.Locked => "\uE72E",
        LibraryModelStatusKind.Error => "\uE783",
        LibraryModelStatusKind.Downloadable => "\uE896",
        LibraryModelStatusKind.Downloading => "\uE895",
        _ => "\uE946"
    };
    public Visibility DownloadProgressVisibility => Model.StatusKind == LibraryModelStatusKind.Downloading
        ? Visibility.Visible
        : Visibility.Collapsed;
    public double DownloadProgress => Model.DownloadProgress ?? 0;
    public Visibility DeleteVisibility => Model.IsInstalled || Model.Source == LibraryModelSource.CustomEndpoint
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility CancelVisibility => Model.StatusKind == LibraryModelStatusKind.Downloading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DuplicateVisibility => Model.Source == LibraryModelSource.CustomEndpoint ? Visibility.Visible : Visibility.Collapsed;
    public bool IsPrimaryActionEnabled =>
        Model.Source == LibraryModelSource.CustomEndpoint
        || Model.IsCloud
        || Model.StatusKind is LibraryModelStatusKind.Downloadable or LibraryModelStatusKind.Downloading;

    public Visibility CloudPillVisibility =>
        Model.AvailableViaHyperWhisperCloud && !Model.IsHyperWhisperProvider
            ? Visibility.Visible
            : Visibility.Collapsed;
    public string CloudPillText => "HyperWhisper Cloud";
    public string CloudPillToolTip =>
        "Reachable through credit-based HyperWhisper Cloud — no provider API key required";

    // --- macOS ModelRow column parity -------------------------------------

    // Type column: a single glyph (Segoe MDL2) instead of the old "Voice/Language" text.
    public string KindGlyph => Model.Kind == LibraryModelKind.Voice ? "\uE720" : "\uE8E4"; // Microphone / AlignLeft
    public string KindToolTip => Model.Kind == LibraryModelKind.Voice ? "Voice model" : "Text model";

    // Speed/Accuracy gauges (5-segment bars).
    public int Speed => Math.Clamp(Model.Speed, 0, 5);
    public int Accuracy => Math.Clamp(Model.Accuracy, 0, 5);

    // Gauge fill colour mirrors macOS gaugeColor: accent when usable, secondary
    // when locked/downloadable, warning on error. Exposed as a resource KEY so the
    // GaugeBar can DynamicResource it and follow live light/dark theme switches.
    public string GaugeBrushKey => Model.StatusKind switch
    {
        LibraryModelStatusKind.Error => "WarningBrush",
        LibraryModelStatusKind.Locked or LibraryModelStatusKind.Downloadable => "TextSecondaryBrush",
        _ => "AccentBrush" // Enabled, Downloading
    };

    // Cloud / Offline column.
    public string SizeText => Model.SizeDescription ?? "";
    public Visibility SizeVisibility =>
        !Model.IsCloud && !string.IsNullOrWhiteSpace(Model.SizeDescription)
            ? Visibility.Visible
            : Visibility.Collapsed;
    public Visibility CloudGlyphVisibility => Model.IsCloud ? Visibility.Visible : Visibility.Collapsed;

    private static string FormatRating(int rating)
        => $"{Math.Clamp(rating, 0, 5)}/5";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
}
