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
    public StreamingSettingsViewModel(SettingsViewModel settings)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Languages = BuildLanguages();
        Settings.PropertyChanged += OnSettingsChanged;
    }

    public SettingsViewModel Settings { get; }

    /// <summary>
    /// Windows shows a picker of language display names, not a free-text code box. The list is
    /// the shared core's, so a language the core drops disappears from every platform at once.
    /// </summary>
    public IReadOnlyList<StreamingLanguageOption> Languages { get; }

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

    private static List<StreamingLanguageOption> BuildLanguages()
    {
        var options = new List<StreamingLanguageOption> { new("auto", "Automatic") };
        try
        {
            foreach (var language in SharedCoreBridge.AllLanguages())
            {
                if (string.Equals(language.Code, "auto", StringComparison.OrdinalIgnoreCase)) continue;
                options.Add(new(language.Code, language.DisplayName ?? language.Code));
            }
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
                break;
            case nameof(SettingsViewModel.StreamingLanguage):
                Notify(nameof(SelectedLanguage));
                break;
        }
    }
}
