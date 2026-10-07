using System.ComponentModel;
using HyperWhisper.SharedCore;

namespace HyperWhisper.PortableApplication.ViewModels;

/// <summary>
/// One row of the streaming language picker: a BCP-47 code and the name the picker shows.
/// </summary>
public sealed record StreamingLanguageOption(string Code, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// Live transcription as its own page. The Windows app gives streaming a sidebar entry of its
/// own, and the shell picks a page template from the view model type, so streaming needs a type
/// the settings page does not also match. Every value still lives on <see cref="SettingsViewModel"/>;
/// this only re-presents it.
/// </summary>
public sealed class StreamingSettingsViewModel : ViewModelBase
{
    private readonly IReadOnlyList<StreamingLanguageOption> _allLanguages;
    private readonly bool _catalogAvailable;
    private IReadOnlyList<StreamingLanguageOption> _languages;

    public StreamingSettingsViewModel(SettingsViewModel settings)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _allLanguages = BuildLanguages(out _catalogAvailable);
        _languages = AllowedLanguages(_allLanguages, CurrentLanguageCatalogEntryId());
        Settings.PropertyChanged += OnSettingsChanged;
        EnforceAllowedLanguage();
    }

    public SettingsViewModel Settings { get; }

    /// <summary>
    /// Windows shows a picker of language display names, not a free-text code box. The list is
    /// the shared core's, so a language the core drops disappears from every platform at once.
    /// It holds only the languages the selected provider (or HyperWhisper Cloud live tier)
    /// declares in the shared catalog, as macOS and Windows have since #832 (#1346).
    /// </summary>
    public IReadOnlyList<StreamingLanguageOption> Languages => _languages;

    public StreamingLanguageOption? SelectedLanguage
    {
        get => Languages.FirstOrDefault(option =>
                   string.Equals(option.Code, Settings.StreamingLanguage, StringComparison.OrdinalIgnoreCase))
               ?? Languages.FirstOrDefault();
        set
        {
            if (value is null || string.Equals(value.Code, Settings.StreamingLanguage, StringComparison.Ordinal)) return;
            Settings.StreamingLanguage = value.Code;
        }
    }

    /// <summary>Windows hides the Engine card, both panels and the Language card until this is on.</summary>
    public bool IsStreamingEnabled => Settings.StreamingEnabled;

    /// <summary>The Deepgram "Model" row shows only while Deepgram is the provider.</summary>
    public bool UsesDeepgram =>
        string.Equals(Settings.StreamingProvider, "deepgram", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The shared-catalog entry whose language set the picker offers for a streaming provider
    /// (the storage spelling <see cref="SettingsViewModel.StreamingProvider"/> holds).
    /// HyperWhisper Cloud answers with its live tier, which the settings setter has already
    /// clamped to the live-eligible set. A local provider answers null and keeps the full list.
    /// Mirrors Windows <c>StreamingSettingsPage.LanguageCatalogEntryId</c>.
    /// </summary>
    public static string? LanguageCatalogEntryId(string? provider, string? cloudTier) => provider switch
    {
        "hyperwhisper" => string.IsNullOrWhiteSpace(cloudTier) ? null : cloudTier,
        "deepgram" => "deepgramNova3",
        "elevenlabs" => "elevenLabsScribeV2",
        "openai" => "openaiWhisper",
        "geminiTranscribe" => "geminiTranscribe",
        "grok" => "grokStt",
        _ => null,
    };

    /// <summary>
    /// The rows a catalog entry declares, matched on primary subtag so a region row (en-GB,
    /// pt-BR) survives an entry that declares its base code. "Automatic" always stays first. An
    /// entry the catalog leaves "unverified", an unknown id, or a catalog fault keeps every row.
    /// </summary>
    public static IReadOnlyList<StreamingLanguageOption> AllowedLanguages(
        IReadOnlyList<StreamingLanguageOption> all, string? entryId)
    {
        IReadOnlyList<string>? codes;
        try
        {
            codes = SharedCoreBridge.CloudSttPickerLanguageCodes(entryId);
        }
        catch (Exception)
        {
            codes = null;
        }
        if (codes is not { Count: > 0 }) return all;

        var allowed = new HashSet<string>(codes, StringComparer.OrdinalIgnoreCase);
        var filtered = all
            .Where(option => string.Equals(option.Code, "auto", StringComparison.OrdinalIgnoreCase)
                             || allowed.Contains(PrimarySubtag(option.Code)))
            .ToList();
        return filtered.Count > 1 ? filtered : all;
    }

    private static string PrimarySubtag(string code)
    {
        var normalized = code.Trim().Replace('_', '-');
        var dash = normalized.IndexOf('-');
        return (dash >= 0 ? normalized[..dash] : normalized).ToLowerInvariant();
    }

    private string? CurrentLanguageCatalogEntryId() =>
        LanguageCatalogEntryId(Settings.StreamingProvider, Settings.StreamingCloudTier);

    private void RefreshLanguages()
    {
        _languages = AllowedLanguages(_allLanguages, CurrentLanguageCatalogEntryId());
        Notify(nameof(Languages));
        EnforceAllowedLanguage();
        Notify(nameof(SelectedLanguage));
    }

    /// <summary>
    /// A saved language the selected provider does not offer resets to "Automatic", as macOS's
    /// enforceAllowedLanguage() and Windows's RefreshLanguageOptions() do. Otherwise the picker
    /// would draw Automatic while the stale code kept being sent to the stream. It also runs
    /// when the language itself is written: Load() writes the language after the provider and
    /// the live tier, so a stale saved pair is caught on load and a valid one is kept.
    /// </summary>
    private void EnforceAllowedLanguage()
    {
        // Without the native core the list is "Automatic" alone; that is no evidence the saved
        // language is wrong, so leave it.
        if (!_catalogAvailable) return;
        var current = Settings.StreamingLanguage;
        if (_languages.Any(option => string.Equals(option.Code, current, StringComparison.OrdinalIgnoreCase))) return;
        Settings.StreamingLanguage = "auto";
    }

    private static List<StreamingLanguageOption> BuildLanguages(out bool catalogAvailable)
    {
        var options = new List<StreamingLanguageOption> { new("auto", "Automatic") };
        catalogAvailable = false;
        try
        {
            foreach (var language in SharedCoreBridge.AllLanguages())
            {
                if (string.Equals(language.Code, "auto", StringComparison.OrdinalIgnoreCase)) continue;
                options.Add(new(language.Code, language.DisplayName ?? language.Code));
            }
            catalogAvailable = options.Count > 1;
        }
        catch (Exception)
        {
            // A missing native core must not blank the picker: "Automatic" alone still saves.
        }
        return options;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SettingsViewModel.StreamingEnabled):
                Notify(nameof(IsStreamingEnabled));
                break;
            case nameof(SettingsViewModel.StreamingProvider):
                Notify(nameof(UsesDeepgram));
                RefreshLanguages();
                break;
            case nameof(SettingsViewModel.StreamingCloudTier):
                // Each live tier declares its own language set.
                RefreshLanguages();
                break;
            case nameof(SettingsViewModel.StreamingLanguage):
                EnforceAllowedLanguage();
                Notify(nameof(SelectedLanguage));
                break;
        }
    }
}
