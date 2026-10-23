using HyperWhisper.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HyperWhisper.PortableApplication.Persistence;

public sealed class VocabularyRepository(ApplicationDb database)
{
    private readonly ApplicationDb _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<IReadOnlyList<VocabularyItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var context = _database.CreateContext();
        return await context.VocabularyItems.AsNoTracking()
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Word)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(VocabularyItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.Word))
            throw new ArgumentException("A vocabulary word is required.", nameof(item));
        await using var context = _database.CreateContext();
        context.VocabularyItems.Add(item);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<VocabularyItem> UpsertAsync(VocabularyItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var word = item.Word.Trim();
        if (word.Length == 0) throw new ArgumentException("A vocabulary word is required.", nameof(item));
        // A word is one line (issue #1777); callers split a multi-line value with
        // VocabularyWords.SplitLines first. The replacement may keep its line breaks.
        if (VocabularyWords.HasLineBreak(word)) throw new ArgumentException("A vocabulary word must be one line.", nameof(item));
        await using var context = _database.CreateContext();
        var existing = await context.VocabularyItems.FirstOrDefaultAsync(candidate => candidate.Id == item.Id, cancellationToken)
            ?? (await context.VocabularyItems.ToListAsync(cancellationToken))
                .FirstOrDefault(candidate => string.Equals(candidate.Word, word, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            item.Word = word;
            context.VocabularyItems.Add(item);
            existing = item;
        }
        else
        {
            existing.Word = word;
            existing.Replacement = string.IsNullOrWhiteSpace(item.Replacement) ? null : item.Replacement.Trim();
            existing.SortOrder = item.SortOrder;
        }
        await context.SaveChangesAsync(cancellationToken);
        return existing;
    }

    /// <summary>
    /// Merges a parsed CSV/TXT file into the list in one save (#2409). Unlike
    /// <see cref="UpsertAsync"/>, a word already listed keeps its spelling and position, and a row
    /// without a replacement never wipes one; <see cref="VocabularyTransfer.MergeInto"/> holds the rules.
    /// </summary>
    public async Task<VocabularyImportResult> ImportAsync(VocabularyTransferParse parsed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        await using var context = _database.CreateContext();
        // MergeInto saves once, so the whole file lands or none of it does.
        cancellationToken.ThrowIfCancellationRequested();
        return VocabularyTransfer.MergeInto(context, parsed.Entries, parsed.Skipped);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = _database.CreateContext();
        var item = await context.VocabularyItems.FindAsync(new object[] { id }, cancellationToken);
        if (item == null) return false;
        context.VocabularyItems.Remove(item);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
