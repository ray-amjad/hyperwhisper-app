using System.Text;
using HyperWhisper.Data;
using HyperWhisper.Data.Entities;

namespace HyperWhisper.PortableApplication.Persistence;

/// <summary>One vocabulary term as a transfer file holds it. A null replacement means none.</summary>
public sealed record VocabularyTransferEntry(string Word, string? Replacement);

/// <summary>The two file formats of issue #2409. The extension picks the format.</summary>
public enum VocabularyTransferFormat
{
    Csv,
    Txt,
}

/// <summary>The rows a file holds, in file order, and how many over-long words were dropped.</summary>
public sealed record VocabularyTransferParse(IReadOnlyList<VocabularyTransferEntry> Entries, int Skipped);

/// <summary>What an import did to the store.</summary>
public sealed record VocabularyImportResult(int Added, int Updated, int Skipped);

/// <summary>The file is not one an import can read: not UTF-8, or not readable at all.</summary>
public sealed class VocabularyTransferException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Vocabulary import and export as CSV and TXT (issue #2409), shared by the Windows and Linux
/// heads. The format rules are the contract in shared-conformance/vocabulary-transfer-vectors.json:
/// <list type="bullet">
/// <item>CSV is UTF-8 RFC 4180 with a <c>word,replacement</c> header and CRLF rows; a field is
/// quoted only when it holds a comma, a double quote, CR or LF. The reader accepts a BOM, LF or
/// CRLF, no header, one column, extra columns and blank rows.</item>
/// <item>TXT is UTF-8 with LF rows, one term per line and the replacement after the first tab. In
/// the replacement a backslash is written <c>\\</c> and a line break <c>\n</c>; a tab inside a
/// word is written as a space.</item>
/// </list>
/// Both readers trim, treat a blank replacement as none, split a word that spans lines into one
/// word per line (#1777, <see cref="VocabularyWords.SplitLines"/>), skip blank words and count the
/// words longer than <see cref="VocabularyWords.MaxWordChars"/> (#2390) as skipped.
/// </summary>
public static class VocabularyTransfer
{
    /// <summary>The name the export picker suggests. Export defaults to CSV.</summary>
    public const string DefaultFileName = "HyperWhisper Vocabulary.csv";

    /// <summary>The source marker an imported row carries: the one a typed row carries.</summary>
    public const string ImportedSource = "manual";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary><c>.txt</c> (any case) is TXT; anything else is CSV, the default format.</summary>
    public static VocabularyTransferFormat FormatForPath(string path) =>
        string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase)
            ? VocabularyTransferFormat.Txt
            : VocabularyTransferFormat.Csv;

    // ---- WRITE ------------------------------------------------------------------------------

    public static string Write(VocabularyTransferFormat format, IEnumerable<VocabularyTransferEntry> entries) =>
        format == VocabularyTransferFormat.Txt ? WriteTxt(entries) : WriteCsv(entries);

    public static string WriteCsv(IEnumerable<VocabularyTransferEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var builder = new StringBuilder("word,replacement\r\n");
        foreach (var entry in entries)
        {
            builder.Append(CsvField(entry.Word)).Append(',').Append(CsvField(entry.Replacement ?? string.Empty)).Append("\r\n");
        }
        return builder.ToString();
    }

    public static string WriteTxt(IEnumerable<VocabularyTransferEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            // A word is one line already (#1777); a tab would split it from itself on the way back.
            builder.Append(entry.Word.Replace('\t', ' '));
            if (!string.IsNullOrEmpty(entry.Replacement))
            {
                builder.Append('\t');
                // The format has one line-break escape, so CRLF and a lone CR are written as \n.
                var replacement = entry.Replacement.Replace("\r\n", "\n").Replace('\r', '\n');
                foreach (var character in replacement)
                {
                    if (character == '\\') builder.Append(@"\\");
                    else if (character == '\n') builder.Append(@"\n");
                    else builder.Append(character);
                }
            }
            builder.Append('\n');
        }
        return builder.ToString();
    }

    /// <summary>The file's bytes: UTF-8 without a BOM, as the vectors are.</summary>
    public static byte[] Encode(string text) => StrictUtf8.GetBytes(text);

    private static string CsvField(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    // ---- READ -------------------------------------------------------------------------------

    /// <summary>
    /// Decodes a file's bytes as strict UTF-8 and drops a leading BOM. Throws
    /// <see cref="VocabularyTransferException"/> when the bytes are not UTF-8.
    /// </summary>
    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException error) { throw new VocabularyTransferException("The file is not UTF-8 text.", error); }
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    public static VocabularyTransferParse Read(VocabularyTransferFormat format, string text) =>
        format == VocabularyTransferFormat.Txt ? ReadTxt(text) : ReadCsv(text);

    public static VocabularyTransferParse ReadCsv(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var rows = ParseCsvRows(StripBom(text));
        var builder = new EntryBuilder();
        for (var index = 0; index < rows.Count; index++)
        {
            var fields = rows[index];
            if (index == 0 && string.Equals(fields[0].Trim(), "word", StringComparison.OrdinalIgnoreCase)) continue;
            builder.Add(fields[0], fields.Count > 1 ? fields[1] : null);
        }
        return builder.Build();
    }

    public static VocabularyTransferParse ReadTxt(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new EntryBuilder();
        foreach (var line in StripBom(text).Replace("\r\n", "\n").Split('\n'))
        {
            var tab = line.IndexOf('\t');
            if (tab < 0) builder.Add(line, null);
            else builder.Add(line[..tab], UnescapeTxt(line[(tab + 1)..]));
        }
        return builder.Build();
    }

    private static string StripBom(string text) => text.Length > 0 && text[0] == '﻿' ? text[1..] : text;

    // Left to right, so "\\n" is a backslash then n. Any other backslash reads as itself.
    private static string UnescapeTxt(string value)
    {
        if (value.IndexOf('\\') < 0) return value;
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\\' && index + 1 < value.Length && value[index + 1] is '\\' or 'n')
            {
                builder.Append(value[index + 1] == 'n' ? '\n' : '\\');
                index++;
            }
            else builder.Append(character);
        }
        return builder.ToString();
    }

    /// <summary>
    /// RFC 4180 records. LF, CRLF and a lone CR end a record outside quotes. Lenient where the RFC
    /// is not: a quote inside an unquoted field is kept as text, text after a closing quote is
    /// kept, and an unclosed quote runs to the end of the file.
    /// </summary>
    private static List<List<string>> ParseCsvRows(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (inQuotes)
            {
                if (character != '"') { field.Append(character); continue; }
                if (index + 1 < text.Length && text[index + 1] == '"') { field.Append('"'); index++; continue; }
                inQuotes = false;
                continue;
            }
            switch (character)
            {
                case '"' when !fieldStarted:
                    inQuotes = true;
                    fieldStarted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    fieldStarted = false;
                    break;
                case '\r' or '\n':
                    if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                    row.Add(field.ToString());
                    rows.Add(row);
                    row = [];
                    field.Clear();
                    fieldStarted = false;
                    break;
                default:
                    field.Append(character);
                    fieldStarted = true;
                    break;
            }
        }
        if (fieldStarted || field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }

    private sealed class EntryBuilder
    {
        private readonly List<VocabularyTransferEntry> _entries = [];
        private int _skipped;

        public void Add(string word, string? replacement)
        {
            var trimmedReplacement = string.IsNullOrWhiteSpace(replacement) ? null : replacement.Trim();
            foreach (var line in VocabularyWords.SplitLines(word))
            {
                if (line.Length > VocabularyWords.MaxWordChars) { _skipped++; continue; }
                _entries.Add(new VocabularyTransferEntry(line, trimmedReplacement));
            }
        }

        public VocabularyTransferParse Build() => new(_entries, _skipped);
    }

    // ---- MERGE ------------------------------------------------------------------------------

    /// <summary>
    /// Merges parsed rows into the store held by <paramref name="context"/> and saves once:
    /// <list type="bullet">
    /// <item>A word the list lacks (trimmed, any case) is appended after the existing rows, in
    /// file order, with the next sort orders.</item>
    /// <item>A word the list holds keeps its spelling, position and source; it takes the file's
    /// replacement only when the file gives a non-empty one, so a words-only file never wipes a
    /// replacement.</item>
    /// <item>A word the file repeats keeps the position of its first row; the last non-empty
    /// replacement the file gives it wins.</item>
    /// </list>
    /// <paramref name="skipped"/> is the parse's over-long count, carried into the result.
    /// </summary>
    public static VocabularyImportResult MergeInto(
        HyperWhisperDbContext context,
        IReadOnlyList<VocabularyTransferEntry> entries,
        int skipped,
        string? source = ImportedSource)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entries);
        var existing = context.VocabularyItems.ToList();
        // The same key as the Windows add path and the backup merge: trimmed, lowered invariantly.
        var byKey = new Dictionary<string, VocabularyItem>(StringComparer.Ordinal);
        foreach (var item in existing) byKey.TryAdd(Key(item.Word), item);
        var originalReplacements = existing.ToDictionary(item => item.Id, item => item.Replacement);
        var added = new HashSet<Guid>();
        var nextSortOrder = existing.Count == 0 ? 0 : existing.Max(item => item.SortOrder) + 1;
        var now = DateTime.UtcNow;

        foreach (var entry in entries)
        {
            var word = entry.Word.Trim();
            if (word.Length == 0 || word.Length > VocabularyWords.MaxWordChars || VocabularyWords.HasLineBreak(word)) continue;
            var replacement = string.IsNullOrWhiteSpace(entry.Replacement) ? null : entry.Replacement.Trim();
            if (byKey.TryGetValue(Key(word), out var item))
            {
                if (replacement is not null) item.Replacement = replacement;
                continue;
            }
            item = new VocabularyItem
            {
                Id = Guid.NewGuid(),
                Word = word,
                Replacement = replacement,
                SortOrder = nextSortOrder++,
                CreatedDate = now,
                Source = source,
            };
            context.VocabularyItems.Add(item);
            byKey[Key(word)] = item;
            added.Add(item.Id);
        }

        var updated = existing.Count(item => !string.Equals(item.Replacement, originalReplacements[item.Id], StringComparison.Ordinal));
        if (added.Count > 0 || updated > 0) context.SaveChanges();
        return new VocabularyImportResult(added.Count, updated, skipped);
    }

    /// <summary>
    /// The rows as an export writes them, in the order given (the page's display order). A row
    /// saved before #1777 may still hold a multi-line word; it is written one word per line, each
    /// with the row's replacement, which is what reading it back would make of it anyway.
    /// </summary>
    public static IReadOnlyList<VocabularyTransferEntry> ToEntries(IEnumerable<VocabularyItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var entries = new List<VocabularyTransferEntry>();
        foreach (var item in items)
        {
            var replacement = string.IsNullOrWhiteSpace(item.Replacement) ? null : item.Replacement.Trim();
            foreach (var line in VocabularyWords.SplitLines(item.Word)) entries.Add(new VocabularyTransferEntry(line, replacement));
        }
        return entries;
    }

    private static string Key(string? word) => (word ?? string.Empty).Trim().ToLowerInvariant();
}
