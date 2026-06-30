namespace HyperWhisper.Models;

/// <summary>
/// LANGUAGE METADATA
///
/// Represents a language option for transcription.
/// Contains all Whisper-supported languages (101 total).
/// Ported from macOS LanguageData.swift to maintain parity.
///
/// ORGANIZATION:
/// - Popular languages appear first for quick access
/// - Remaining languages in alphabetical order
/// </summary>
public class LanguageInfo
{
    public string Code { get; }
    public string DisplayName { get; }

    public LanguageInfo(string code, string displayName)
    {
        Code = code;
        DisplayName = displayName;
    }

    public override string ToString() => DisplayName;

    /// <summary>
    /// All Whisper-supported languages.
    /// Popular languages listed first, then alphabetical.
    /// Total: 101 languages including "Automatic" detection.
    /// </summary>
    public static readonly LanguageInfo[] AllLanguages = new[]
    {
        // =====================================================================
        // POPULAR LANGUAGES (shown first for quick access)
        // =====================================================================
        new LanguageInfo("auto", "Automatic"),
        new LanguageInfo("en", "English"),
        new LanguageInfo("ja", "Japanese"),
        new LanguageInfo("es", "Spanish"),
        new LanguageInfo("zh", "Chinese"),
        new LanguageInfo("zh-TW", "Chinese (Traditional)"),
        new LanguageInfo("nl", "Dutch"),
        new LanguageInfo("hi", "Hindi"),
        new LanguageInfo("ru", "Russian"),
        new LanguageInfo("ko", "Korean"),
        new LanguageInfo("it", "Italian"),
        new LanguageInfo("uk", "Ukrainian"),
        new LanguageInfo("pl", "Polish"),
        new LanguageInfo("pt", "Portuguese"),
        new LanguageInfo("el", "Greek"),
        new LanguageInfo("cs", "Czech"),
        new LanguageInfo("sv", "Swedish"),
        new LanguageInfo("no", "Norwegian"),
        new LanguageInfo("da", "Danish"),
        new LanguageInfo("id", "Indonesian"),

        // =====================================================================
        // ALL OTHER LANGUAGES (alphabetical order)
        // =====================================================================
        new LanguageInfo("af", "Afrikaans"),
        new LanguageInfo("sq", "Albanian"),
        new LanguageInfo("am", "Amharic"),
        new LanguageInfo("ar", "Arabic"),
        new LanguageInfo("hy", "Armenian"),
        new LanguageInfo("as", "Assamese"),
        new LanguageInfo("az", "Azerbaijani"),
        new LanguageInfo("ba", "Bashkir"),
        new LanguageInfo("eu", "Basque"),
        new LanguageInfo("be", "Belarusian"),
        new LanguageInfo("bn", "Bengali"),
        new LanguageInfo("bs", "Bosnian"),
        new LanguageInfo("br", "Breton"),
        new LanguageInfo("bg", "Bulgarian"),
        new LanguageInfo("yue", "Cantonese"),
        new LanguageInfo("ca", "Catalan"),
        new LanguageInfo("hr", "Croatian"),
        new LanguageInfo("et", "Estonian"),
        new LanguageInfo("fo", "Faroese"),
        new LanguageInfo("fi", "Finnish"),
        new LanguageInfo("fr", "French"),
        new LanguageInfo("gl", "Galician"),
        new LanguageInfo("ka", "Georgian"),
        new LanguageInfo("de", "German"),
        new LanguageInfo("gu", "Gujarati"),
        new LanguageInfo("ht", "Haitian"),
        new LanguageInfo("ha", "Hausa"),
        new LanguageInfo("haw", "Hawaiian"),
        new LanguageInfo("he", "Hebrew"),
        new LanguageInfo("hu", "Hungarian"),
        new LanguageInfo("is", "Icelandic"),
        new LanguageInfo("jw", "Javanese"),
        new LanguageInfo("kn", "Kannada"),
        new LanguageInfo("kk", "Kazakh"),
        new LanguageInfo("km", "Khmer"),
        new LanguageInfo("lo", "Lao"),
        new LanguageInfo("la", "Latin"),
        new LanguageInfo("lv", "Latvian"),
        new LanguageInfo("ln", "Lingala"),
        new LanguageInfo("lt", "Lithuanian"),
        new LanguageInfo("lb", "Luxembourgish"),
        new LanguageInfo("mk", "Macedonian"),
        new LanguageInfo("mg", "Malagasy"),
        new LanguageInfo("ms", "Malay"),
        new LanguageInfo("ml", "Malayalam"),
        new LanguageInfo("mt", "Maltese"),
        new LanguageInfo("mi", "Maori"),
        new LanguageInfo("mr", "Marathi"),
        new LanguageInfo("mn", "Mongolian"),
        new LanguageInfo("my", "Myanmar"),
        new LanguageInfo("ne", "Nepali"),
        new LanguageInfo("nn", "Nynorsk"),
        new LanguageInfo("oc", "Occitan"),
        new LanguageInfo("ps", "Pashto"),
        new LanguageInfo("fa", "Persian"),
        new LanguageInfo("pa", "Punjabi"),
        new LanguageInfo("ro", "Romanian"),
        new LanguageInfo("sa", "Sanskrit"),
        new LanguageInfo("sr", "Serbian"),
        new LanguageInfo("sn", "Shona"),
        new LanguageInfo("sd", "Sindhi"),
        new LanguageInfo("si", "Sinhala"),
        new LanguageInfo("sk", "Slovak"),
        new LanguageInfo("sl", "Slovenian"),
        new LanguageInfo("so", "Somali"),
        new LanguageInfo("su", "Sundanese"),
        new LanguageInfo("sw", "Swahili"),
        new LanguageInfo("tl", "Tagalog"),
        new LanguageInfo("tg", "Tajik"),
        new LanguageInfo("ta", "Tamil"),
        new LanguageInfo("tt", "Tatar"),
        new LanguageInfo("te", "Telugu"),
        new LanguageInfo("th", "Thai"),
        new LanguageInfo("bo", "Tibetan"),
        new LanguageInfo("tr", "Turkish"),
        new LanguageInfo("tk", "Turkmen"),
        new LanguageInfo("ur", "Urdu"),
        new LanguageInfo("uz", "Uzbek"),
        new LanguageInfo("vi", "Vietnamese"),
        new LanguageInfo("cy", "Welsh"),
        new LanguageInfo("yi", "Yiddish"),
        new LanguageInfo("yo", "Yoruba")
    };

    /// <summary>
    /// Soniox stt-async-v4 supported languages verified from official Soniox docs on 2026-03-21.
    /// </summary>
    public static readonly string[] SonioxAsyncV4LanguageCodes =
    {
        "auto",
        "af", "sq", "ar", "az", "eu", "be", "bn", "bs", "bg", "ca",
        "zh", "hr", "cs", "da", "nl", "en", "et", "fi", "fr", "gl",
        "de", "el", "gu", "he", "hi", "hu", "id", "it", "ja", "kn",
        "kk", "ko", "lv", "lt", "mk", "ms", "ml", "mr", "no", "fa",
        "pl", "pt", "pa", "ro", "ru", "sr", "sk", "sl", "es", "sw",
        "sv", "tl", "ta", "te", "th", "tr", "uk", "ur", "vi", "cy"
    };

    /// <summary>
    /// Gets the display name for a language code.
    /// Returns the code itself if not found.
    /// </summary>
    public static string GetDisplayName(string code)
    {
        foreach (var lang in AllLanguages)
        {
            if (lang.Code == code) return lang.DisplayName;
        }
        return code;
    }
}
