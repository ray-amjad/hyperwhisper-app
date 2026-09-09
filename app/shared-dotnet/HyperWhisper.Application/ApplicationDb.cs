using HyperWhisper.Data;
using HyperWhisper.Platform.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace HyperWhisper.PortableApplication.Persistence;

public sealed class ApplicationDb(Func<HyperWhisperDbContext> createContext)
{
    private readonly Func<HyperWhisperDbContext> _createContext =
        createContext ?? throw new ArgumentNullException(nameof(createContext));

    public ApplicationDb(IAppPaths paths)
        : this(() => new HyperWhisperDbContext(paths))
    {
    }

    public HyperWhisperDbContext CreateContext() => _createContext();

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync(cancellationToken);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await MigrateAsync(cancellationToken);
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await context.Modes.AnyAsync(cancellationToken))
        {
            context.Modes.AddRange(PortableModeDefaults.CreateForCurrentRegion());
            await context.SaveChangesAsync(cancellationToken);
        }
        else
        {
            // Seeding is not the same question as the invariant. A store that
            // already has modes can still have two defaults or none — a backup
            // restored from another machine is the realistic route — and this
            // branch is exactly why nothing used to repair it (issue #536).
            var modes = await context.Modes.OrderBy(item => item.SortOrder).ToListAsync(cancellationToken);
            if (DefaultModePolicy.Apply(modes))
                await context.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
