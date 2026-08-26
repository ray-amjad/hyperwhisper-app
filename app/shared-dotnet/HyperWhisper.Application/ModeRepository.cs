using HyperWhisper.Data.Entities;
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
        if (target.IsDefault)
        {
            var replacement = modes.First(item => item.Id != id);
            replacement.IsDefault = true;
            replacement.ModifiedDate = DateTime.UtcNow;
        }
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task UpsertSafelyAsync(Mode mode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mode);
        mode.Name = mode.Name.Trim();
        if (mode.Name.Length == 0) throw new ArgumentException("A mode name is required.", nameof(mode));
        await using var context = _database.CreateContext();
        var all = await context.Modes.ToListAsync(cancellationToken);
        if (all.Any(item => item.Id != mode.Id && string.Equals(item.Name, mode.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A mode with that name already exists.");
        if (all.Count == 0) mode.IsDefault = true;
        if (mode.IsDefault)
            foreach (var item in all.Where(item => item.Id != mode.Id)) item.IsDefault = false;
        else if (all.Count > 0 && all.All(item => item.Id == mode.Id || !item.IsDefault))
            throw new InvalidOperationException("At least one mode must remain the default.");
        var existing = all.SingleOrDefault(item => item.Id == mode.Id);
        if (existing is null) context.Modes.Add(mode);
        else context.Entry(existing).CurrentValues.SetValues(mode);
        await context.SaveChangesAsync(cancellationToken);
    }
}
