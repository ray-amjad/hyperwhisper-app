using HyperWhisper.Data.Entities;
using HyperWhisper.SharedCore;
using Microsoft.EntityFrameworkCore;

namespace HyperWhisper.PortableApplication.Persistence;

public sealed class ModeRepository(ApplicationDb database)
{
    private readonly ApplicationDb _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<IReadOnlyList<Mode>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var context = _database.CreateContext();
        return await context.Modes.AsNoTracking()
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task UpsertAsync(Mode mode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mode);
        if (string.IsNullOrWhiteSpace(mode.Name))
            throw new ArgumentException("A mode name is required.", nameof(mode));
        await using var context = _database.CreateContext();
        var exists = await context.Modes.AnyAsync(item => item.Id == mode.Id, cancellationToken);
        if (exists) context.Modes.Update(mode);
        else context.Modes.Add(mode);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = _database.CreateContext();
        var mode = await context.Modes.FindAsync(new object[] { id }, cancellationToken);
        if (mode == null) return false;
        context.Modes.Remove(mode);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteSafelyAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = _database.CreateContext();
        // The count check and the delete are one unit (issue #2663). Without the transaction two
        // overlapping deletes of different modes each read two rows, both pass the check, and the
        // table ends with none. A non-deferred SQLite transaction is BEGIN IMMEDIATE, which takes
        // the write lock before the read, so a second delete waits here and then sees one row.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        // Same order as ListAsync, so the mode the default flag moves to is the one the Local API's
        // delete moved it to before that path was routed through here.
        var modes = await context.Modes
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Name)
            .ToListAsync(cancellationToken);
        var target = modes.SingleOrDefault(item => item.Id == id);
        if (target is null) return false;
        if (modes.Count == 1) throw new ModeRuleViolationException("modes.last_mode", "Cannot delete the last remaining mode.");
        context.Modes.Remove(target);
        // Deleting the default moves the flag rather than leaving none. Which
        // mode it moves to is the shared core's decision (issue #536), so a
        // backup restored on Linux, Windows and macOS promotes the same one.
        var remaining = modes.Where(item => item.Id != id).ToList();
        var moved = DefaultModePolicy.ApplyAndReport(remaining);
        foreach (var row in remaining.Where(row => moved.Contains(row.Id)))
            row.ModifiedDate = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// The Modes page's create. Refused with a <see cref="ModeRuleViolationException"/> when a mode
    /// rule forbids it.
    /// </summary>
    public Task CreateSafelyAsync(Mode mode, CancellationToken cancellationToken = default)
        => SaveSafelyAsync(mode, update: false, cancellationToken);

    /// <summary>
    /// The Modes page's update of a stored mode. A mode another writer deleted is refused as
    /// <c>modes.not_found</c>, never written back (issue #2748).
    /// </summary>
    public Task UpdateSafelyAsync(Mode mode, CancellationToken cancellationToken = default)
        => SaveSafelyAsync(mode, update: true, cancellationToken);

    private async Task SaveSafelyAsync(Mode mode, bool update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mode);
        mode.Name = mode.Name.Trim();
        if (mode.Name.Length == 0) throw new ArgumentException("A mode name is required.", nameof(mode));
        var saved = await SaveAtomicallyAsync(update ? mode.Id : null, (all, stored) =>
        {
            if (all.Any(item => item.Id != mode.Id && string.Equals(item.Name, mode.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ModeRuleViolationException("modes.name_taken", "A mode with that name already exists.");
            // Both checks read the row as it stands BEFORE the write (issue #536).
            if (stored is not null
                && DefaultModePolicy.CheckRename(stored, mode.Name)
                    == PortableModeNameChange.RejectedDefaultIsFixed)
                throw new ModeRuleViolationException("modes.default_name_fixed", "The default mode's name cannot be changed.");
            if (DefaultModePolicy.CheckDefaultFlag(all, mode.Id, mode.IsDefault)
                == PortableDefaultFlagChange.RejectedLastDefault)
                throw new ModeRuleViolationException("modes.last_default", "At least one mode must remain the default.");
            return mode;
        }, cancellationToken);
        if (saved is null) throw new ModeRuleViolationException("modes.not_found", "The mode no longer exists.");
    }

    /// <summary>
    /// Reads every mode, lets <paramref name="prepare"/> check them and build the row to write, makes
    /// exactly one mode the default, and writes, as one unit (issue #2748). Both save paths, the Modes
    /// page and the Local API's create and patch, go through here.
    /// </summary>
    /// <param name="updateId">
    /// The mode being updated, or <c>null</c> for a create. An update of a mode that is not stored
    /// when the transaction reads writes nothing and answers <c>null</c>: a row another writer
    /// deleted is never re-inserted.
    /// </param>
    /// <param name="prepare">
    /// Gets every stored row in <see cref="ListAsync"/> order and, for an update, a separate copy of
    /// the stored row to change (<c>null</c> for a create), both untracked. It returns the row to
    /// write, whose id must be <paramref name="updateId"/> for an update, and throws to refuse; the
    /// caller keeps its own exception types and messages, and nothing is written.
    /// </param>
    /// <returns>The row written, or <c>null</c> when the mode to update was gone.</returns>
    public async Task<Mode?> SaveAtomicallyAsync(
        Guid? updateId,
        Func<IReadOnlyList<Mode>, Mode?, Mode> prepare,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        await using var context = _database.CreateContext();
        // Same transaction shape as DeleteSafelyAsync (issue #2663). Without it a save read the rows,
        // checked them and wrote in separate steps, so a delete or another save that committed in
        // between left two modes with one name, no default, or a deleted mode written back. A
        // non-deferred SQLite transaction is BEGIN IMMEDIATE: the write lock is taken before the read,
        // so every check below sees the rows the write lands on.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var all = await context.Modes.AsNoTracking()
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Name)
            .ToListAsync(cancellationToken);
        Mode? stored = null;
        if (updateId is { } id)
        {
            if (all.All(item => item.Id != id)) return null;
            stored = await context.Modes.AsNoTracking().SingleAsync(item => item.Id == id, cancellationToken);
        }
        var pending = prepare(all, stored) ?? throw new InvalidOperationException("No mode was prepared to save.");
        if (updateId is not null && pending.Id != updateId)
            throw new InvalidOperationException("An update must write the mode it read.");
        // Exactly one default across every row that exists after the write. Which one is the shared
        // core's decision (issue #536); the other rows it moved are written with this save.
        var after = all.Where(item => item.Id != pending.Id).Append(pending)
            .OrderBy(item => item.SortOrder).ToList();
        var moved = DefaultModePolicy.ApplyAndReport(after, pending.IsDefault ? pending.Id : null);
        foreach (var row in after.Where(row => row.Id != pending.Id && moved.Contains(row.Id)))
        {
            row.ModifiedDate = DateTime.UtcNow;
            context.Modes.Update(row);
        }
        if (updateId is null) context.Modes.Add(pending);
        else context.Modes.Update(pending);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return pending;
    }
}

/// <summary>
/// A save <see cref="ModeRepository.CreateSafelyAsync"/> / <see cref="ModeRepository.UpdateSafelyAsync"/> or a delete
/// <see cref="ModeRepository.DeleteSafelyAsync"/> refused on a mode rule, not on a storage fault.
/// <see cref="Code"/> is the status code the editor shows it under (issues #2551, #2561).
/// </summary>
public sealed class ModeRuleViolationException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
