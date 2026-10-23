using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.ViewModels;

// Issue #2409: vocabulary import/export as CSV and TXT. The shared .NET reader and writer
// (VocabularyTransfer, used by the Windows and Linux heads) against the golden vectors in
// shared-conformance/vocabulary-transfer-vectors.json, then the Linux page's view model end to end
// against a real migrated ApplicationDb.
static class VocabularyTransferTests
{
    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        RunGoldenVectors();
        RunReaderEdgeCases();
        await RoundTripThroughThePageAsync(Path.Combine(root, "round-trip"));
        await MergeRulesAsync(Path.Combine(root, "merge"));
        await UnreadableFilesWriteNothingAsync(Path.Combine(root, "unreadable"));
        await FailedSaveIsNotABadFileAsync(Path.Combine(root, "save-failed"));
        Console.WriteLine("Vocabulary transfer tests passed.");
    }

    private sealed record Vectors(
        IReadOnlyList<VocabularyTransferEntry> RoundTripEntries,
        string Csv,
        string Txt,
        IReadOnlyList<(string Name, string Input, IReadOnlyList<VocabularyTransferEntry> Expected, int Skipped)> CsvImport,
        IReadOnlyList<(string Name, string Input, IReadOnlyList<VocabularyTransferEntry> Expected, int Skipped)> TxtImport);

    private static Vectors LoadVectors()
    {
        // Walk up from the build output to the repository, as CloudPostProcessing.Tests does,
        // so the harness needs no csproj link to the vectors file.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "shared-conformance", "vocabulary-transfer-vectors.json")))
            directory = directory.Parent;
        Check(directory is not null, "shared-conformance/vocabulary-transfer-vectors.json was not found above the test output");
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory!.FullName, "shared-conformance", "vocabulary-transfer-vectors.json")));
        var json = document.RootElement;
        static IReadOnlyList<VocabularyTransferEntry> Entries(JsonElement array) =>
            array.EnumerateArray()
                .Select(entry => new VocabularyTransferEntry(
                    entry.GetProperty("word").GetString()!,
                    entry.GetProperty("replacement").ValueKind == JsonValueKind.Null ? null : entry.GetProperty("replacement").GetString()))
                .ToList();
        static IReadOnlyList<(string, string, IReadOnlyList<VocabularyTransferEntry>, int)> Cases(JsonElement array) =>
            array.EnumerateArray()
                .Select(item => (item.GetProperty("name").GetString()!, item.GetProperty("input").GetString()!,
                    Entries(item.GetProperty("expected")), item.GetProperty("skipped").GetInt32()))
                .ToList();
        var roundTrip = json.GetProperty("roundTrip");
        return new Vectors(
            Entries(roundTrip.GetProperty("entries")),
            roundTrip.GetProperty("csv").GetString()!,
            roundTrip.GetProperty("txt").GetString()!,
            Cases(json.GetProperty("csvImport")),
            Cases(json.GetProperty("txtImport")));
    }

    private static void RunGoldenVectors()
    {
        var vectors = LoadVectors();
        Check(vectors.RoundTripEntries.Count > 0 && vectors.CsvImport.Count > 0 && vectors.TxtImport.Count > 0, "the vectors file is empty");

        var csv = VocabularyTransfer.WriteCsv(vectors.RoundTripEntries);
        Check(csv == vectors.Csv, $"the CSV writer does not reproduce roundTrip.csv:\n{Show(csv)}\nexpected\n{Show(vectors.Csv)}");
        var txt = VocabularyTransfer.WriteTxt(vectors.RoundTripEntries);
        Check(txt == vectors.Txt, $"the TXT writer does not reproduce roundTrip.txt:\n{Show(txt)}\nexpected\n{Show(vectors.Txt)}");
        Check(VocabularyTransfer.Encode(csv).SequenceEqual(Encoding.UTF8.GetBytes(vectors.Csv)), "the CSV bytes are not the vector's UTF-8 bytes (a BOM?)");

        var csvBack = VocabularyTransfer.ReadCsv(vectors.Csv);
        CheckEntries(csvBack.Entries, vectors.RoundTripEntries, "roundTrip.csv read back");
        Check(csvBack.Skipped == 0, "roundTrip.csv read back skipped a word");
        var txtBack = VocabularyTransfer.ReadTxt(vectors.Txt);
        CheckEntries(txtBack.Entries, vectors.RoundTripEntries, "roundTrip.txt read back");
        Check(txtBack.Skipped == 0, "roundTrip.txt read back skipped a word");

        foreach (var (name, input, expected, skipped) in vectors.CsvImport)
        {
            // Through Decode as well, so the BOM rule is exercised on the bytes a file holds.
            var parsed = VocabularyTransfer.ReadCsv(VocabularyTransfer.Decode(Encoding.UTF8.GetBytes(input)));
            CheckEntries(parsed.Entries, expected, $"csvImport '{name}'");
            Check(parsed.Skipped == skipped, $"csvImport '{name}' skipped {parsed.Skipped}, expected {skipped}");
            CheckEntries(VocabularyTransfer.ReadCsv(input).Entries, expected, $"csvImport '{name}' (text)");
        }
        foreach (var (name, input, expected, skipped) in vectors.TxtImport)
        {
            var parsed = VocabularyTransfer.ReadTxt(VocabularyTransfer.Decode(Encoding.UTF8.GetBytes(input)));
            CheckEntries(parsed.Entries, expected, $"txtImport '{name}'");
            Check(parsed.Skipped == skipped, $"txtImport '{name}' skipped {parsed.Skipped}, expected {skipped}");
            CheckEntries(VocabularyTransfer.ReadTxt(input).Entries, expected, $"txtImport '{name}' (text)");
        }
    }

    private static void RunReaderEdgeCases()
    {
        Check(VocabularyTransfer.FormatForPath("/tmp/HyperWhisper Vocabulary.csv") == VocabularyTransferFormat.Csv
              && VocabularyTransfer.FormatForPath("/tmp/list.TXT") == VocabularyTransferFormat.Txt
              && VocabularyTransfer.FormatForPath("/tmp/list") == VocabularyTransferFormat.Csv,
            "the extension does not pick the format (.txt is TXT, anything else CSV)");
        Check(VocabularyTransfer.DefaultFileName == "HyperWhisper Vocabulary.csv", "the export's default name changed");

        // A header-shaped word on a later row is a word, not a header.
        CheckEntries(VocabularyTransfer.ReadCsv("alpha\r\nword\r\n").Entries, [new("alpha", null), new("word", null)], "a later 'word' row");
        // An unclosed quote runs to the end instead of throwing.
        CheckEntries(VocabularyTransfer.ReadCsv("\"open, never closed\n").Entries, [new("open, never closed", null)], "an unclosed quote");
        // A lone CR ends a row too, and a quote inside an unquoted field is text.
        CheckEntries(VocabularyTransfer.ReadCsv("a\rb 5\" disk,x\r").Entries, [new("a", null), new("b 5\" disk", "x")], "a lone CR and a bare quote");
        // TXT: a tab inside a word is written as a space; a CRLF in a replacement is written \n.
        Check(VocabularyTransfer.WriteTxt([new("a\tb", "one\r\ntwo")]) == "a b\tone\\ntwo\n", "TXT does not flatten a word's tab or a CRLF replacement");
        // An empty file is no rows, not an error.
        Check(VocabularyTransfer.ReadCsv(string.Empty).Entries.Count == 0 && VocabularyTransfer.ReadTxt(string.Empty).Entries.Count == 0, "an empty file produced rows");
        // Not UTF-8 is refused.
        var refused = false;
        try { _ = VocabularyTransfer.Decode([0x63, 0x61, 0x66, 0xE9, 0x0A]); }
        catch (VocabularyTransferException) { refused = true; }
        Check(refused, "Latin-1 bytes were decoded instead of refused");
    }

    private static async Task<(ApplicationDb Database, VocabularyRepository Repository, VocabularyViewModel Page)> NewPageAsync(string root)
    {
        Directory.CreateDirectory(root);
        var database = new ApplicationDb(new Paths(root));
        await database.InitializeAsync();
        var repository = new VocabularyRepository(database);
        var page = new VocabularyViewModel(repository);
        await page.RefreshAsync();
        return (database, repository, page);
    }

    private static async Task SeedAsync(VocabularyRepository repository, IEnumerable<VocabularyTransferEntry> entries)
    {
        var order = 0;
        foreach (var entry in entries)
            await repository.AddAsync(new HyperWhisper.Data.Entities.VocabularyItem { Word = entry.Word, Replacement = entry.Replacement, SortOrder = order++, Source = "manual" });
    }

    private static async Task<IReadOnlyList<VocabularyTransferEntry>> RowsAsync(VocabularyRepository repository) =>
        (await repository.ListAsync()).Select(item => new VocabularyTransferEntry(item.Word, item.Replacement)).ToList();

    private static async Task<string> SnapshotAsync(VocabularyRepository repository) =>
        string.Join("\n", (await repository.ListAsync()).Select(item => $"{item.Id}|{item.Word}|{item.Replacement}|{item.SortOrder}|{item.Source}|{item.CreatedDate:O}"));

    // Export the round-trip list to .csv and .txt from the page, then import each file into an
    // empty store and into the same store: the words, replacements and order come back exactly,
    // and the same-store import changes nothing.
    private static async Task RoundTripThroughThePageAsync(string root)
    {
        var vectors = LoadVectors();
        var (_, repository, page) = await NewPageAsync(Path.Combine(root, "source"));
        await SeedAsync(repository, vectors.RoundTripEntries);
        await page.RefreshAsync();
        CheckEntries(await RowsAsync(repository), vectors.RoundTripEntries, "the seeded list");

        foreach (var extension in new[] { "csv", "txt" })
        {
            var file = Path.Combine(root, $"HyperWhisper Vocabulary.{extension}");
            await page.ExportAsync(file);
            Check(!page.Status.HasError && page.HasTransferMessage && page.TransferMessage.Contains(Path.GetFileName(file), StringComparison.Ordinal),
                $"the {extension} export failed or said nothing: {page.Status.ErrorCode} '{page.TransferMessage}'");
            var written = await File.ReadAllTextAsync(file);
            Check(written == (extension == "csv" ? vectors.Csv : vectors.Txt), $"the page's {extension} export is not the golden file:\n{Show(written)}");
            Check(Directory.GetFiles(root, "*.tmp-*").Length == 0, "the export left its temporary file behind");

            var (_, emptyRepository, emptyPage) = await NewPageAsync(Path.Combine(root, $"empty-{extension}"));
            await emptyPage.ImportAsync(file);
            Check(!emptyPage.Status.HasError && emptyPage.LastImport == new VocabularyImportResult(vectors.RoundTripEntries.Count, 0, 0),
                $"the {extension} import into an empty store reported {emptyPage.LastImport} / {emptyPage.Status.ErrorCode}");
            CheckEntries(await RowsAsync(emptyRepository), vectors.RoundTripEntries, $"the {extension} import into an empty store");
            CheckEntries(emptyPage.Items.Select(item => new VocabularyTransferEntry(item.Word, item.Replacement)).ToList(), vectors.RoundTripEntries,
                $"the page list after the {extension} import");
            Check(emptyPage.TransferMessage == "Vocabulary imported. Added: 10, updated: 0, skipped (over 1000 characters): 0.",
                $"the {extension} import's result line reads '{emptyPage.TransferMessage}'");

            var before = await SnapshotAsync(repository);
            await page.ImportAsync(file);
            Check(page.LastImport == new VocabularyImportResult(0, 0, 0), $"re-importing the {extension} export into its own store reported {page.LastImport}");
            Check(await SnapshotAsync(repository) == before, $"re-importing the {extension} export into its own store changed a row");
        }

        // A localized page: the head's text is used, with its arguments.
        page.Localize = key => key == "vocabulary.transfer.imported" ? "+{0} ~{1} -{2}" : null;
        await page.ImportAsync(Path.Combine(root, "HyperWhisper Vocabulary.csv"));
        Check(page.TransferMessage == "+0 ~0 -0", $"the head's localized result line was not used: '{page.TransferMessage}'");
    }

    private static async Task MergeRulesAsync(string root)
    {
        var (_, repository, page) = await NewPageAsync(root);
        await SeedAsync(repository, [new("OpenAI", "Open AI"), new("Kubernetes", null), new("my sign-off", "Best,\nRay")]);
        await page.RefreshAsync();

        // A words-only TXT keeps every replacement, and a listed word keeps its spelling and place.
        var wordsOnly = Path.Combine(root, "words.txt");
        await File.WriteAllTextAsync(wordsOnly, "openai\nMY SIGN-OFF\nnew one\n");
        await page.ImportAsync(wordsOnly);
        CheckEntries(await RowsAsync(repository),
            [new("OpenAI", "Open AI"), new("Kubernetes", null), new("my sign-off", "Best,\nRay"), new("new one", null)],
            "a words-only TXT import");
        Check(page.LastImport == new VocabularyImportResult(1, 0, 0), $"the words-only import reported {page.LastImport}");

        // A non-empty replacement updates a listed word in place. Inside the file the first row
        // of a word keeps the position and the last non-empty replacement wins.
        var csv = Path.Combine(root, "update.csv");
        await File.WriteAllTextAsync(csv, "word,replacement\nkubernetes,K8s\nzeta,\nalpha,A1\nZETA,Z\nAlpha,\nalpha,A2\n" +
            new string('q', VocabularyWords.MaxWordChars + 1) + ",too long\n");
        await page.ImportAsync(csv);
        CheckEntries(await RowsAsync(repository),
            [new("OpenAI", "Open AI"), new("Kubernetes", "K8s"), new("my sign-off", "Best,\nRay"), new("new one", null), new("zeta", "Z"), new("alpha", "A2")],
            "a CSV import with updates and in-file duplicates");
        Check(page.LastImport == new VocabularyImportResult(2, 1, 1), $"the update import reported {page.LastImport}");
        Check(page.TransferMessage == "Vocabulary imported. Added: 2, updated: 1, skipped (over 1000 characters): 1.", $"the result line reads '{page.TransferMessage}'");
        var orders = (await repository.ListAsync()).Select(item => item.SortOrder).ToList();
        Check(orders.SequenceEqual(orders.OrderBy(order => order)) && orders.Distinct().Count() == orders.Count, $"the sort orders are not strictly increasing: {string.Join(",", orders)}");
    }

    private static async Task UnreadableFilesWriteNothingAsync(string root)
    {
        var (_, repository, page) = await NewPageAsync(root);
        await SeedAsync(repository, [new("keep", "me")]);
        await page.RefreshAsync();
        var before = await SnapshotAsync(repository);

        var latin1 = Path.Combine(root, "latin1.csv");
        await File.WriteAllBytesAsync(latin1, [0x63, 0x61, 0x66, 0xE9, 0x2C, 0x78, 0x0A]);
        await page.ImportAsync(latin1);
        Check(page.HasInputError && page.Status.ErrorCode == "vocabulary.import_unreadable" && !page.HasTransferMessage,
            $"a non-UTF-8 file did not show the error line: {page.Status.ErrorCode} '{page.InputError}'");
        Check(await SnapshotAsync(repository) == before, "a non-UTF-8 file wrote rows");

        await page.ImportAsync(Path.Combine(root, "missing.txt"));
        Check(page.HasInputError && page.Status.ErrorCode == "vocabulary.import_unreadable", $"a missing file did not show the error line: {page.Status.ErrorCode}");
        Check(await SnapshotAsync(repository) == before, "a missing file wrote rows");

        await page.ExportAsync(Path.Combine(root, "no-such-directory", "out.csv"));
        Check(page.HasInputError && page.Status.ErrorCode == "vocabulary.export_failed", $"an export to a missing folder did not show the error line: {page.Status.ErrorCode}");
    }

    // A file that parsed but could not be saved says so, not "Could not read this file": a
    // trigger makes SQLite refuse the merge's insert, the way a full or locked disk would.
    private static async Task FailedSaveIsNotABadFileAsync(string root)
    {
        var (database, repository, page) = await NewPageAsync(root);
        await SeedAsync(repository, [new("keep", "me")]);
        await page.RefreshAsync();
        var before = await SnapshotAsync(repository);
        await using (var context = database.CreateContext())
            await context.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER refuse_vocabulary BEFORE INSERT ON \"VocabularyItems\" BEGIN SELECT RAISE(ABORT, 'refused'); END");

        var file = Path.Combine(root, "good.csv");
        await File.WriteAllTextAsync(file, "word,replacement\nnew word,\n");
        await page.ImportAsync(file);
        Check(page.HasInputError && page.Status.ErrorCode == "vocabulary.import_save_failed" && !page.HasTransferMessage,
            $"a failed save did not show its own error line: {page.Status.ErrorCode} '{page.InputError}'");
        Check(page.InputError == "The vocabulary could not be saved. Nothing was imported.", $"a failed save reads '{page.InputError}'");
        Check(await SnapshotAsync(repository) == before, "a failed save wrote rows");

        page.Localize = key => key == "vocabulary.transfer.saveFailed" ? "localized save failure" : null;
        await page.ImportAsync(file);
        Check(page.InputError == "localized save failure", $"the head's saveFailed text was not used: '{page.InputError}'");
    }

    private static void CheckEntries(IReadOnlyList<VocabularyTransferEntry> actual, IReadOnlyList<VocabularyTransferEntry> expected, string what) =>
        Check(actual.SequenceEqual(expected),
            $"{what}: got [{string.Join(" | ", actual.Select(ShowEntry))}], expected [{string.Join(" | ", expected.Select(ShowEntry))}]");

    private static string ShowEntry(VocabularyTransferEntry entry) =>
        Show(entry.Replacement is null ? entry.Word : $"{entry.Word} -> {entry.Replacement}");

    private static string Show(string value)
    {
        var shown = value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        return shown.Length > 200 ? shown[..200] + $"...({value.Length} chars)" : shown;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Vocabulary transfer (#2409): {message}");
    }

    private sealed class Paths(string root) : IAppPaths
    {
        public string DataDirectory => root;
        public string ConfigDirectory => root;
        public string CacheDirectory => root;
        public string StateDirectory => root;
        public string LogsDirectory => root;
        public string ModelsDirectory => root;
        public string RecordingsDirectory => root;
        public string RuntimeDirectory => root;
        public string TemporaryDirectory => root;
    }
}
