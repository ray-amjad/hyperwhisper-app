using HyperWhisper.SharedCore;

namespace HyperWhisper.Models;

/// <summary>
/// LANGUAGE METADATA
///
/// Represents a language option for transcription.
///
/// WHERE THE DATA LIVES:
/// The rows are no longer written here. They come from the shared catalog in
/// the Rust core (<c>shared-core-rs/crates/hw-catalog</c>), reached through
/// <see cref="SharedCoreBridge"/>, which is the same list macOS and Linux read.
/// This class is a thin projection of that list onto the type WPF binds to, so
/// its public shape is unchanged for its call sites.
///
/// WHAT CHANGED FOR WINDOWS (issue #285):
/// The hand-written table this class used to hold had 102 rows and no notion
/// of a region or a script. (Its own doc comment claimed 101, which is how
/// long a hand-counted table stays right.) The picker now gains the 24 region
/// and script variant rows only macOS listed — en-GB, en-US, pt-BR, pt-PT,
/// zh-Hans, zh-Hant, es-419 and the rest — for 126 in total. And
/// <see cref="GetDisplayName"/> canonicalizes before it looks up, so a stored
/// <c>en_GB</c> or <c>zh-hant</c> resolves to a real row instead of matching
/// nothing and printing the raw tag.
/// One name changed: zh-TW now reads "Chinese (Traditional, Taiwan)", which is
/// what tells it apart from the zh-Hant row beside it.
///
/// ORGANIZATION:
/// - "Automatic" first, then popular languages for quick access
/// - Remaining languages in alphabetical order
/// (that order is the catalog's, not this file's)
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
    /// Every language the picker offers, in picker order: "Automatic", then the
    /// popular codes, then the rest alphabetically. 126 rows, read from the
    /// shared catalog rather than declared here.
    ///
    /// <para>Built once at type initialization. It is deliberately a field and
    /// not a property: WPF re-evaluates a binding source more than once, and a
    /// property would cross the FFI and rebuild 126 objects every time. The
    /// catalog is compiled into the core and cannot change while the app runs,
    /// so there is nothing to refresh.</para>
    ///
    /// <para>Stays a <c>LanguageInfo[]</c> because call sites read
    /// <c>.Length</c> off it.</para>
    /// </summary>
    public static readonly LanguageInfo[] AllLanguages = BuildAllLanguages();

    private static LanguageInfo[] BuildAllLanguages()
    {
        var catalog = SharedCoreBridge.AllLanguages();
        var languages = new LanguageInfo[catalog.Count];
        for (var index = 0; index < catalog.Count; index++)
        {
            var row = catalog[index];
            // A null display name means the catalog does not know the code, so
            // the host localizes it — which cannot happen for a row that came
            // OUT of the catalog. Fall back to the code anyway: this runs in a
            // static initializer, where a throw would take the whole app down
            // before any window opens.
            languages[index] = new LanguageInfo(row.Code, row.DisplayName ?? row.Code);
        }

        return languages;
    }

    /// <summary>
    /// Soniox async-model supported languages verified from official Soniox docs on 2026-03-21.
    /// </summary>
    public static readonly string[] SonioxAsyncLanguageCodes =
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
    /// Returns the code itself if the catalog does not know it.
    ///
    /// <para>This used to be a linear scan for an exact <c>Code == code</c>
    /// match over the hand-written table, which meant every spelling the app
    /// did not itself emit matched nothing: a mode that had stored
    /// <c>en_GB</c>, or a provider that advertised <c>zh-hant</c>, rendered as
    /// the raw tag in the picker and in the library filter. The core
    /// canonicalizes the code first, so all of those resolve.</para>
    ///
    /// <para>The miss path is unchanged on purpose — the raw code back, not its
    /// canonical form. A code the catalog does not know is one no picker offers,
    /// so the string is only ever shown, and showing the user exactly what is
    /// stored is the more useful of the two.</para>
    /// </summary>
    public static string GetDisplayName(string code)
    {
        // The old scan could never match a null or blank code, so it returned it
        // unchanged. Keep that: the bridge rejects a null argument, and this
        // runs on a value converter's render path where a throw is a crash.
        if (string.IsNullOrWhiteSpace(code)) return code;

        return SharedCoreBridge.LanguageInfo(code)?.DisplayName ?? code;
    }
}
