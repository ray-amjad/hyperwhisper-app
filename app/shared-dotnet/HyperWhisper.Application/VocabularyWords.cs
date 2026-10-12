namespace HyperWhisper.PortableApplication.Persistence;

/// <summary>
/// A vocabulary word is one line (issues #1475, #1777). A pasted multi-line text becomes one
/// word per non-empty line; a snippet's replacement keeps its line breaks. The Windows page, the
/// Linux page and both backup imports split with the same rule as macOS
/// (<c>PersistenceController.vocabularyWordLines</c>).
/// </summary>
public static class VocabularyWords
{
    // The characters Foundation's CharacterSet.newlines holds, so all three heads split alike.
    private static readonly char[] LineBreaks = ['\n', '\u000B', '\u000C', '\r', '\u0085', '\u2028', '\u2029'];

    /// <summary>
    /// One trimmed word per non-empty line. Single-line input comes back as one trimmed word;
    /// blank input comes back empty.
    /// </summary>
    public static IReadOnlyList<string> SplitLines(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : text.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>True when <paramref name="word"/> holds a line break anywhere.</summary>
    public static bool HasLineBreak(string? word) => word is not null && word.IndexOfAny(LineBreaks) >= 0;
}
