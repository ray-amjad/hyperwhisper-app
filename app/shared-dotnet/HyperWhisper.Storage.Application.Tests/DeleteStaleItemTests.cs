using HyperWhisper.Data.Entities;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.ViewModels;

/// <summary>
/// Issue #2749: a Vocabulary or History delete that answered "not found", because another writer
/// had already removed the row, only set Status, so the page kept listing a row that was gone.
/// </summary>
static class DeleteStaleItemTests
{
    public static async Task RunAsync()
    {
        await VocabularyAsync();
        await HistoryAsync();
        Console.WriteLine("PASS a delete answered 'not found' drops the stale Vocabulary and History rows");
    }

    static async Task VocabularyAsync()
    {
        var root = NewRoot();
        try
        {
            var db = new ApplicationDb(new TestPaths(root));
            await db.InitializeAsync();
            var repository = new VocabularyRepository(db);
            var outsideWriter = new VocabularyRepository(db);
            var a = await repository.UpsertAsync(new VocabularyItem { Word = "alpha", SortOrder = 0 });
            var b = await repository.UpsertAsync(new VocabularyItem { Word = "bravo", SortOrder = 1 });
            var c = await repository.UpsertAsync(new VocabularyItem { Word = "charlie", SortOrder = 2 });
            var page = new VocabularyViewModel(repository);
            await page.RefreshAsync();
            Check(page.Items.Count == 3, $"expected 3 terms, got {page.Items.Count}");

            // B is open in the pill with an edit not yet saved; another writer removes A, and the
            // user deletes A's stale row.
            var editing = page.Items.Single(item => item.Id == b.Id);
            page.Selected = editing;
            page.Word = "bravo typed";
            Check(await outsideWriter.DeleteAsync(a.Id), "the outside writer could not delete alpha");
            await page.DeleteAsync(page.Items.Single(item => item.Id == a.Id));
            Check(page.Status.ErrorCode == "vocabulary.not_found" && page.Status.Message == "The vocabulary item no longer exists.",
                $"deleting a term already gone showed {page.Status.ErrorCode}: {page.Status.Message}");
            Check(page.Items.Select(item => item.Id).SequenceEqual([b.Id, c.Id]),
                $"after deleting a term already gone the list holds [{string.Join(", ", page.Items.Select(item => item.Word))}], expected bravo, charlie");
            Check(ReferenceEquals(page.Selected, editing) && page.Items.Contains(editing),
                "the reload left Selected on an instance the list no longer holds");
            Check(page.Word == "bravo typed", "the reload threw away the pill's unsaved edit");
            Check(page.InputError == "The vocabulary item no longer exists.", $"the inline error read '{page.InputError}'");

            // The term being edited is the one another writer removed: the list drops it, Selected
            // lets go of it, the pill keeps the typed text, and the next save adds it as a new term
            // rather than bringing the removed row back.
            page.Selected = page.Items.Single(item => item.Id == c.Id);
            page.Word = "charlie typed";
            Check(await outsideWriter.DeleteAsync(c.Id), "the outside writer could not delete charlie");
            await page.DeleteAsync(page.Selected);
            Check(page.Status.ErrorCode == "vocabulary.not_found", $"deleting the selected term already gone showed {page.Status.ErrorCode}");
            Check(page.Items.Count == 1 && page.Items.Single().Id == b.Id,
                $"after deleting the selected term already gone the list holds [{string.Join(", ", page.Items.Select(item => item.Word))}]");
            Check(page.Selected is null, "deleting the selected term already gone left Selected on it");
            Check(page.Word == "charlie typed", "deleting the selected term already gone cleared the pill");
            await page.SaveAsync();
            var stored = await repository.ListAsync();
            Check(stored.Count == 2 && stored.All(item => item.Id != c.Id) && stored.Any(item => item.Word == "charlie typed"),
                $"the save after it stored [{string.Join(", ", stored.Select(item => item.Word))}]");
        }
        finally { Cleanup(root); }
    }

    static async Task HistoryAsync()
    {
        var root = NewRoot();
        try
        {
            var paths = new TestPaths(root);
            var db = new ApplicationDb(paths);
            await db.InitializeAsync();
            var repository = new HistoryRepository(db, paths);
            var outsideWriter = new HistoryRepository(db, paths);
            var now = DateTime.UtcNow;
            var rows = Enumerable.Range(0, 5)
                .Select(index => new Transcript { Text = $"t{index}", Status = TranscriptStatus.Completed, Date = now.AddMinutes(-index) })
                .ToArray();
            foreach (var row in rows) await repository.AddAsync(row);
            var page = new HistoryViewModel(repository);
            await page.RefreshAsync();
            Check(page.Items.Count == 5, $"expected 5 transcripts, got {page.Items.Count}");

            // One selected row, removed by another writer, then Delete.
            Check(await outsideWriter.DeleteAsync(rows[1].Id), "the outside writer could not delete t1");
            await page.DeleteAsync(page.Items.Single(item => item.Id == rows[1].Id));
            Check(page.Status.ErrorCode == "history.not_found" && page.Status.Message == "The transcript no longer exists.",
                $"deleting a transcript already gone showed {page.Status.ErrorCode}: {page.Status.Message}");
            Check(page.Items.Select(item => item.Id).SequenceEqual([rows[0].Id, rows[2].Id, rows[3].Id, rows[4].Id]),
                $"after deleting a transcript already gone the list holds [{string.Join(", ", page.Items.Select(item => item.Text))}]");
            Check(!page.GroupedItems.OfType<Transcript>().Any(item => item.Id == rows[1].Id), "the day groups still list t1");
            Check(page.SelectedItems.Count == 0 && page.Selected is null, "the gone transcript stayed selected");

            // Every selected row already gone: all of them leave the list, the rest stay.
            Check(await outsideWriter.DeleteAsync(rows[2].Id) && await outsideWriter.DeleteAsync(rows[3].Id),
                "the outside writer could not delete t2 and t3");
            page.UpdateSelection(page.Items.Where(item => item.Id == rows[2].Id || item.Id == rows[3].Id));
            await page.DeleteSelectedAsync();
            Check(page.Status.ErrorCode == "history.not_found", $"deleting two transcripts already gone showed {page.Status.ErrorCode}");
            Check(page.Items.Select(item => item.Id).SequenceEqual([rows[0].Id, rows[4].Id]),
                $"after deleting two transcripts already gone the list holds [{string.Join(", ", page.Items.Select(item => item.Text))}]");
            Check(page.SelectedItems.Count == 0 && !page.HasSelection, "the gone transcripts stayed selected");

            // A writer that goes around the repository (storage cleanup) moves no write generation,
            // so the page could reuse its rows on the next visit; the not-found answer marks them stale.
            var extra = new Transcript { Text = "t5", Status = TranscriptStatus.Completed, Date = now.AddMinutes(-10) };
            await repository.AddAsync(extra);
            await page.RefreshAsync();
            await using (var context = db.CreateContext())
            {
                context.Transcripts.Remove(context.Transcripts.Single(item => item.Id == extra.Id));
                await context.SaveChangesAsync();
            }
            Check(page.TryReuseLoadedRows(), "the rows were stale before the delete");
            await page.DeleteAsync(page.Items.Single(item => item.Id == extra.Id));
            Check(page.Status.ErrorCode == "history.not_found", $"deleting t5 already gone showed {page.Status.ErrorCode}");
            Check(page.Items.All(item => item.Id != extra.Id), "t5 stayed listed");
            Check(!page.TryReuseLoadedRows(), "after a not-found answer the next visit would reuse rows another writer changed");

            // Mixed: one selected row is gone, one is not. The delete succeeds and both leave.
            await page.RefreshAsync();
            Check(await outsideWriter.DeleteAsync(rows[0].Id), "the outside writer could not delete t0");
            page.UpdateSelection(page.Items.ToArray());
            await page.DeleteSelectedAsync();
            Check(!page.Status.HasError && page.Status.Message == "1 transcript deleted; audio retained",
                $"the mixed delete showed {page.Status.ErrorCode}: {page.Status.Message}");
            Check(page.Items.Count == 0 && page.SelectedItems.Count == 0, "the mixed delete left a row listed or selected");
            Check((await repository.ListAsync()).Count == 0, "the mixed delete left a stored row");
        }
        finally { Cleanup(root); }
    }

    static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hyperwhisper-delete-stale-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "recordings"));
        return root;
    }

    static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Assertion failed: {name}");
    }
}
