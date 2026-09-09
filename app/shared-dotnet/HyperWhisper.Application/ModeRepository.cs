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
        var modes = await context.Modes.OrderBy(item => item.SortOrder).ToListAsync(cancellationToken);
        var target = modes.SingleOrDefault(item => item.Id == id);
        if (target is null) return false;
        if (modes.Count == 1) throw new InvalidOperationException("Cannot delete the last remaining mode.");
        context.Modes.Remove(target);
        // Deleting the default moves the flag rather than leaving none. Which
        // mode it moves to is the shared core's decision (issue #536), so a
        // backup restored on Linux, Windows and macOS promotes the same one.
        var remaining = modes.Where(item => item.Id != id).ToList();
        var moved = DefaultModePolicy.ApplyAndReport(remaining);
        foreach (var row in remaining.Where(row => moved.Contains(row.Id)))
            row.ModifiedDate = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task UpsertSafelyAsync(Mode mode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mode);
        mode.Name = mode.Name.Trim();
        if (mode.Name.Length == 0) throw new ArgumentException("A mode name is required.", nameof(mode));
        await using var context = _database.CreateContext();
        var all = await context.Modes.OrderBy(item => item.SortOrder).ToListAsync(cancellationToken);
        if (all.Any(item => item.Id != mode.Id && string.Equals(item.Name, mode.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A mode with that name already exists.");
        var existing = all.SingleOrDefault(item => item.Id == mode.Id);
        // Both checks read the row as it stands BEFORE the write (issue #536).
        if (existing is not null
            && DefaultModePolicy.CheckRename(existing, mode.Name)
                == PortableModeNameChange.RejectedDefaultIsFixed)
            throw new InvalidOperationException("The default mode's name cannot be changed.");
        if (DefaultModePolicy.CheckDefaultFlag(all, mode.Id, mode.IsDefault)
            == PortableDefaultFlagChange.RejectedLastDefault)
            throw new InvalidOperationException("At least one mode must remain the default.");
        if (existing is null)
        {
            context.Modes.Add(mode);
            all.Add(mode);
        }
        else
        {
            context.Entry(existing).CurrentValues.SetValues(mode);
        }
        DefaultModePolicy.Apply(all, mode.IsDefault ? mode.Id : null);
        await context.SaveChangesAsync(cancellationToken);
    }
}
