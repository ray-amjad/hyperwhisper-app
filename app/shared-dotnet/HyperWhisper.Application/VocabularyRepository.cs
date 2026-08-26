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

    public async Task<int> MergeAsync(IEnumerable<VocabularyItem> items, CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var item in items.Where(item => !string.IsNullOrWhiteSpace(item.Word)))
        {
            _ = await UpsertAsync(item, cancellationToken);
            count++;
        }
        return count;
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
