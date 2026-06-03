using Microsoft.EntityFrameworkCore;

namespace EfSimpleBulkSaveChanges;

public static class BulkSaveChangesExtensions
{
    public static Task<int> BulkSaveChangesAsync(
        this DbContext context,
        CancellationToken cancellationToken = default)
    {
        return context.BulkSaveChangesAsync(_ => { }, cancellationToken);
    }

    public static Task<int> BulkSaveChangesAsync(
        this DbContext context,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        return context.BulkSaveChangesAsync(options => options.BatchSize = batchSize, cancellationToken);
    }

    public static async Task<int> BulkSaveChangesAsync(
        this DbContext context,
        Action<BulkSaveChangesOptions> configure,
        CancellationToken cancellationToken = default)
    {
        return await context
            .BulkSaveChangesAsync(configure, BulkSaveChangesCommandExecutor.ExecuteAsync, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<int> BulkSaveChangesAsync(
        this DbContext context,
        Action<BulkSaveChangesOptions> configure,
        Func<DbContext, BulkSaveChangesPlan, CancellationToken, Task> executeAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(executeAsync);

        var options = new BulkSaveChangesOptions();
        configure(options);

        context.ChangeTracker.DetectChanges();

        var entries = context.ChangeTracker
            .Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();

        if (entries.Count == 0)
        {
            return 0;
        }

        var plan = BulkSaveChangesSqlPlanner.CreatePlan(context, entries, options);
        await executeAsync(context, plan, cancellationToken).ConfigureAwait(false);

        context.ChangeTracker.AcceptAllChanges();
        return entries.Count;
    }
}
