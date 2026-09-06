using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using HyperWhisper.Data.Entities;
using HyperWhisper.Linux.Localization;
using HyperWhisper.PortableApplication.ViewModels;
using HyperWhisper.SharedCore;

namespace HyperWhisper.Linux;

/// <summary>
/// Turns an icon resource key into the geometry behind it. Windows reaches its glyphs through a
/// Segoe MDL2 code point it can bind straight to a TextBlock; Avalonia has to look the geometry
/// up, so a row that picks its own glyph (a Model Library status, a mode's provider kind) binds
/// the key and this converter resolves it.
/// </summary>
public sealed class IconKeyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || key.Length == 0) return null;
        return Application.Current?.Resources.TryGetResource(key, null, out var geometry) == true
            ? geometry as Geometry
            : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Renders a raw option id through the string catalog: the ConverterParameter is the key prefix
/// and the bound value is the id, so "custom" with parameter "mode.editor.preset." reads
/// "mode.editor.preset.custom" → "Custom".
///
/// The Windows mode editor spells every option out as its own ComboBoxItem with a Tag for the
/// stored id and a {loc:Loc} Content for the label. Linux binds the same id lists off the view
/// model, so this is what supplies the label half. An unknown id falls back to itself rather
/// than throwing, which keeps a mode written by a newer build readable.
///
/// The parameter may also carry a SUFFIX, separated by "|", for the catalogs whose keys wrap the
/// id on both sides: "modes.cloudAccuracy.|.label" turns "elevenLabsScribeV2" into
/// "modes.cloudAccuracy.elevenLabsScribeV2.label". Without that these twelve rows had no
/// reachable key at all and the combo showed raw camelCase.
///
/// An EMPTY id resolves to "{prefix}default" rather than to an empty label. The medical-domain
/// list is ["", "medical"], and the empty entry was rendering as a blank first row, which reads
/// as a broken combo rather than as "General".
/// </summary>
public sealed class OptionLabelConverter : IValueConverter
{
    private readonly AvaloniaLocalizationBridge _localization;

    public OptionLabelConverter(AvaloniaLocalizationBridge localization) => _localization = localization;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string id) return string.Empty;
        if (parameter is not string spec || spec.Length == 0) return id;

        var split = spec.IndexOf('|');
        var prefix = split >= 0 ? spec[..split] : spec;
        var suffix = split >= 0 ? spec[(split + 1)..] : string.Empty;
        if (prefix.Length == 0) return id;

        var key = prefix + (id.Length == 0 ? "default" : id) + suffix;
        var label = _localization[key];
        // The bridge renders an unknown key as the key itself, so that is the miss signal.
        return string.IsNullOrEmpty(label) || label == key ? id : label;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// "en" becomes "English" and an empty code becomes the automatic string, exactly as the Windows
/// LanguageCodeToDisplayNameConverter does on a mode card. The names come from the shared core,
/// so no second language table exists here.
/// </summary>
public sealed class LanguageDisplayNameConverter : IValueConverter
{
    private readonly AvaloniaLocalizationBridge _localization;
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    public LanguageDisplayNameConverter(AvaloniaLocalizationBridge localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        try
        {
            foreach (var language in SharedCoreBridge.AllLanguages())
                _names[language.Code] = language.DisplayName ?? language.Code;
        }
        catch (Exception)
        {
            // Without the native core a card still prints the raw code, which is what it did before.
        }
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var code = value as string;
        if (string.IsNullOrWhiteSpace(code) || string.Equals(code, "auto", StringComparison.OrdinalIgnoreCase))
            return _localization["language.automatic"];
        return _names.TryGetValue(code.Trim(), out var name) ? name : code;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// The provider/model line on a mode card. Windows draws it as plain inline text with no chip:
/// a cloud mode reads "{Provider}" or "{Provider} · {Model}", a local mode reads just the model.
/// The two are mutually exclusive, which is why one converter answers for both with a parameter
/// naming the half being drawn.
/// </summary>
/// <summary>
/// True when a collection has at least one item. The Home audio-input row binds its visibility
/// here: on a machine with no capture device the combo rendered as an empty 60x28 box holding
/// nothing but a chevron, which reads as a broken control rather than as "no microphone".
/// </summary>
public sealed class AnyItemsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is System.Collections.ICollection { Count: > 0 };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ModeProviderLineConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Mode mode) return null;
        var isCloud = string.Equals(mode.ProviderType, "cloud", StringComparison.OrdinalIgnoreCase);
        var want = parameter as string;
        if (want is "cloudVisible") return isCloud;
        if (want is "localVisible") return !isCloud;
        if (want is "local") return mode.Model ?? mode.LocalEngine;

        // Windows (Views/Pages/ModesPage.xaml.cs ModeCard_Loaded) prints the provider ALONE for
        // HyperWhisper Cloud and only appends the model for a BYOK provider. Appending it in both
        // cases gave the card a third segment, which pushed the post-processing name past the
        // card edge and clipped it: "HyperWhisper Cloud · scribe_v2 · anthropic:claude-h...".
        var provider = ProviderDisplayName(mode.CloudProvider);
        if (IsHyperWhisperCloud(mode.CloudProvider)) return provider;
        var model = mode.CloudTranscriptionModel ?? mode.Model;
        return string.IsNullOrWhiteSpace(model) ? provider : $"{provider} · {model}";
    }

    internal static bool IsHyperWhisperCloud(string? id) => id?.ToLowerInvariant() switch
    {
        null or "" or "hyperwhisper" or "hyperwhispercloud" or "hyperwhisper_cloud" => true,
        _ => false,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static string ProviderDisplayName(string? id) => id?.ToLowerInvariant() switch
    {
        // Windows shortens this to "HyperWhisper" on a mode card; the long form is the page copy.
        null or "" or "hyperwhisper" or "hyperwhispercloud" or "hyperwhisper_cloud" => "HyperWhisper",
        "openai" => "OpenAI",
        "groq" => "Groq",
        "elevenlabs" => "ElevenLabs",
        "mistral" => "Mistral",
        "grok" or "xai" => "xAI",
        "deepgram" => "Deepgram",
        "assemblyai" => "AssemblyAI",
        "soniox" => "Soniox",
        "gemini" => "Gemini",
        "geminitranscribe" => "Gemini 3.5 Transcribe",
        "microsoftazurespeech" => "Azure Speech",
        "googlespeech" => "Google Speech",
        "meta" => "Meta",
        _ => id,
    };
}

/// <summary>
/// The inline post-processing segment: a separator, a glyph and the post-processing model name,
/// shown only while the mode actually post-processes. PostProcessingMode 0 is off.
/// </summary>
public sealed class ModePostProcessingConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Mode mode) return parameter as string == "visible" ? false : null;
        var enabled = mode.PostProcessingMode != 0;
        if (parameter as string == "visible") return enabled;
        if (!enabled) return null;
        // Windows (Converters/PostProcessingDisplayConverter.cs) names the PROVIDER, not the
        // model, when post-processing runs on HyperWhisper Cloud, so its card reads
        // "HyperWhisper" where this one read the raw "anthropic:claude-haiku-4-5".
        if (mode.PostProcessingMode == 1 && ModeProviderLineConverter.IsHyperWhisperCloud(mode.PostProcessingProvider))
            return "HyperWhisper";
        // Local post-processing names a GGUF file; a BYOK cloud provider names a "vendor:model"
        // pair. Windows prints whichever one the mode actually runs.
        var model = mode.PostProcessingMode == 2
            ? mode.LocalPostProcessingModel
            : mode.LanguageModel ?? mode.CloudPostProcessingModel;
        return string.IsNullOrWhiteSpace(model) ? mode.PostProcessingProvider ?? string.Empty : model;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Resolves a history day header. The shared view model cannot localize, so it hands over a
/// <see cref="HistoryDateGroup"/> that carries either a catalogue key ("Today", "Yesterday") or
/// an already formatted date, which needs no translation.
/// </summary>
public sealed class HistoryGroupHeaderConverter : IValueConverter
{
    private readonly AvaloniaLocalizationBridge _localization;

    public HistoryGroupHeaderConverter(AvaloniaLocalizationBridge localization)
        => _localization = localization ?? throw new ArgumentNullException(nameof(localization));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not HistoryDateGroup group) return null;
        return group.LocalizationKey is { Length: > 0 } key ? _localization[key] : group.Text;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Answers whether a transcript row is in the state a status pill names. Windows uses a
/// TranscriptStatusToVisibilityConverter with the same three parameters; "Retrying" is a
/// Processing row that has already been retried at least once.
/// </summary>
public sealed class TranscriptStatusConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Transcript transcript) return false;
        return (parameter as string) switch
        {
            "Processing" => transcript is { Status: TranscriptStatus.Processing, RetryCount: 0 },
            "Failed" => transcript.Status == TranscriptStatus.Failed,
            "Retrying" => transcript is { Status: TranscriptStatus.Processing, RetryCount: > 0 },
            _ => false,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// The transcript body swaps to a monospace face while the raw text is shown, which is what the
/// Windows BoolToFontFamilyConverter does over the same TextBox.
/// </summary>
public sealed class TranscriptFontConverter : IValueConverter
{
    private static readonly FontFamily Monospace = new("DejaVu Sans Mono, Liberation Mono, monospace");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Monospace : FontFamily.Default;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// The right-hand page padding, widened by the width of the classic scroll bar whenever that bar
/// is showing.
///
/// Windows applies its PagePadding of 24 inside the scroll area and lets the stock 17px WPF scroll
/// bar sit hard against the window edge, so a page that scrolls ends its cards 17px further left
/// than a page that does not: x958 against x975. Reserving the gutter with a grid column does not
/// work here, because Avalonia measures the scroll content before it knows whether the bar will be
/// needed, so the content keeps the width it was measured at and never reflows. Binding the padding
/// to the bar's own visibility is what makes the two apps agree on where a card ends, and it is
/// what stops paragraphs breaking at different words on the two platforms.
/// </summary>
public sealed class ScrollGutterPaddingConverter : IValueConverter
{
    public static readonly ScrollGutterPaddingConverter Instance = new();

    /// <summary>Windows PagePadding, applied on all four sides.</summary>
    private const double PagePadding = 24;

    /// <summary>SystemParameters.VerticalScrollBarWidth at 96dpi, which is what Windows draws.</summary>
    private const double ClassicScrollBarWidth = 17;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true
            ? new Thickness(PagePadding, PagePadding, PagePadding + ClassicScrollBarWidth, PagePadding)
            : new Thickness(PagePadding);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Scales a TextBlock's FontSize into one of the two text metrics Windows applies and Avalonia
/// does not, so a paragraph on Linux occupies the same box and breaks at the same word.
///
/// Both apps declare the same family and both resolve the Inter Display cut, but they take two
/// different numbers off it.
///
/// LINE BOX. Inter Display's own vertical metrics are hhea/typo/win ascender 1974 and descender
/// -502 over a 2048 unit em, which is 1.2090em, or 15.72px at the 13px body size. Avalonia honours
/// that, so Linux stacked body lines on a 15px pitch. WPF does not: a FontFamily built from a
/// comma-separated list takes its LineSpacing from the composite, which resolves to Segoe UI's
/// 1.3301em, or 17.29px at 13px, and the Windows captures measure a 17px pitch on every multi-line
/// paragraph. The 1.57px per line is the whole of the "cards are a few px short, pages end 8-25px
/// high" defect: a two-line card lost 3px, a page of them lost twenty.
///
/// TRACKING. At regular weight the same string is about 2.9% narrower on Linux. Measured ink, same
/// strings, Windows against Linux: 146/141, 236/229, 269/257, 225/220 on Home and 510/498 on the
/// Local API paragraph. A least-squares fit over those five gives 1.0287, or 0.165px per character
/// at 13px, which is 0.0127em. SemiBold is not affected -- the four Home card titles measure 98/97,
/// 130/131, 93/91 and 101/99, inside a pixel -- so the tracking setter is scoped to Normal weight
/// and the card titles keep the parity they already had.
/// </summary>
public sealed class FontMetricConverter : IValueConverter
{
    /// <summary>Multiplier applied to FontSize. See the class remarks for where each value comes from.</summary>
    public double Factor { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double size && size > 0 && !double.IsNaN(size) && !double.IsInfinity(size)
            ? size * Factor
            : AvaloniaProperty.UnsetValue;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Turns a provider's bare logo name ("providerGroq") into the bitmap for the Model Library's
/// 32px avatar tile. The PNGs are the Windows app's own, linked into this project's
/// AvaloniaResource items rather than copied, so both heads draw the same marks from one file.
///
/// A null return is meaningful: it is the signal for the row to fall back to the letter
/// monogram, exactly as Windows does for a provider with no PNG (Meta). The shared
/// ProviderAssets.Exists already filters those out, so a null here means the file was expected
/// and is genuinely missing, which must still not throw inside a list item template.
///
/// Bitmaps are cached because a ListBox re-runs the converter on every row recycle, and decoding
/// seventeen PNGs per scroll was visible.
/// </summary>
public sealed class ProviderLogoConverter : IValueConverter
{
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.Ordinal);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string name || name.Length == 0) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;
            Bitmap? bitmap = null;
            try
            {
                var uri = new Uri($"avares://HyperWhisper/Assets/Providers/{name}.png");
                if (AssetLoader.Exists(uri)) bitmap = new Bitmap(AssetLoader.Open(uri));
            }
            catch (Exception)
            {
                // A missing or corrupt logo must degrade to the monogram, never take the page
                // down: this runs inside a list item template.
                bitmap = null;
            }
            Cache[name] = bitmap;
            return bitmap;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// "anthropic:claude-haiku-4-5" becomes "Anthropic — Claude Haiku 4.5".
///
/// The stored value of a HyperWhisper Cloud post-processing choice is a raw "provider:model"
/// pair. Windows never shows it: its mode editor draws two combos whose items carry
/// model.DisplayName off the CloudPp catalog (Views/Windows/ModeEditorWindow.xaml.cs:1000-1039).
/// Linux was drawing the pair into a plain TextBox, which put a raw id on screen. The labels
/// here come from the same core catalog, so no second table exists. An id that is not in the
/// catalog falls back to itself rather than to an empty row.
/// </summary>
public sealed class CloudPostProcessingLabelConverter : IValueConverter
{
    private Dictionary<string, string>? _labels;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string id || id.Length == 0) return string.Empty;
        return Labels().TryGetValue(id, out var label) ? label : id;
    }

    private Dictionary<string, string> Labels()
        => _labels ??= HyperWhisper.ModelReadiness.CloudPostProcessingCatalog.Entries
            .ToDictionary(entry => entry.Value, entry => entry.Label, StringComparer.OrdinalIgnoreCase);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// "large-v3-turbo" becomes "Large v3 Turbo".
///
/// The on-device model combo stores a catalog id, and the id is what Windows keeps in its
/// ComboBoxItem.Tag while drawing model.DisplayName as the Content (ModeEditorWindow.xaml.cs:411-446).
/// Linux was drawing the tag. The names come from PortableModelCatalog, which is the portable copy
/// of the very registry Windows reads, so there is no second table. Windows also appends the file
/// size; Linux keeps the shorter label rather than deriving a size string that would disagree with
/// Windows' hand-written one ("1.5 GB" for a 1.62 GB file).
/// </summary>
public sealed class LocalModelLabelConverter : IValueConverter
{
    private Dictionary<string, string>? _names;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string id || id.Length == 0) return string.Empty;
        return Names().TryGetValue(id, out var name) ? name : id;
    }

    private Dictionary<string, string> Names()
        => _names ??= HyperWhisper.ModelManagement.PortableModelCatalog.All
            .GroupBy(model => model.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().DisplayName, StringComparer.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// "scribe_v2" becomes "Scribe v2".
///
/// The BYOK model picker stores the vendor's own model id; Windows keeps that id in a Tag and
/// draws the catalog's display name (ModeEditorWindow.xaml.cs:491-501). An id the catalog does
/// not carry — a mode written by another platform, say — falls back to itself rather than to a
/// blank row.
/// </summary>
public sealed class CloudSttModelLabelConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string id && id.Length > 0
            ? HyperWhisper.ModelReadiness.CloudSttModelCatalog.Label(id)
            : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
