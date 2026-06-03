using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EfSimpleBulkSaveChanges;

public static class BulkSynchronizeExtensions
{
    public static Task<int> BulkSynchronizeAsync<TEntity>(
        this DbContext context,
        IEnumerable<TEntity> entities,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        return context.BulkSynchronizeAsync(entities, _ => { }, cancellationToken);
    }

    public static Task<int> BulkSynchronizeAsync<TEntity>(
        this DbContext context,
        IEnumerable<TEntity> entities,
        int batchSize,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        return context.BulkSynchronizeAsync(entities, options => options.BatchSize = batchSize, cancellationToken);
    }

    public static async Task<int> BulkSynchronizeAsync<TEntity>(
        this DbContext context,
        IEnumerable<TEntity> entities,
        Action<BulkSynchronizeOptions> configure,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new BulkSynchronizeOptions();
        configure(options);

        using var enumerator = entities.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return 0;
        }

        var mapping = BulkSynchronizeEntityMapping.Create(context, typeof(TEntity), options.DeleteScopePropertyNames);
        var savedCount = 0;
        var sourceKeys = new HashSet<object>();
        var scopeValues = CreateScopeValueSets(mapping);
        var currentTransaction = context.Database.CurrentTransaction;
        var ownedTransaction = currentTransaction is null
            ? await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
            foreach (var chunk in ReadChunks(enumerator.Current, enumerator, options.BatchSize))
            {
                AddScopeValues(mapping, scopeValues, chunk, options.DeleteMissing);

                var keyedSources = chunk
                    .Select(entity => new SourceEntity<TEntity>(entity, mapping.GetKeyValue(entity)))
                    .ToList();
                var lookupKeys = keyedSources
                    .Where(source => !mapping.IsDefaultKeyValue(source.KeyValue))
                    .Select(source => source.KeyValue)
                    .Distinct()
                    .ToList();
                var existingByKey = await LoadExistingByKeyAsync<TEntity>(context, mapping, lookupKeys, cancellationToken).ConfigureAwait(false);
                var processedEntities = new List<object>(chunk.Length + existingByKey.Count);

                foreach (var source in keyedSources)
                {
                    if (!mapping.IsDefaultKeyValue(source.KeyValue) && existingByKey.TryGetValue(source.KeyValue!, out var existing))
                    {
                        CopyValues(mapping, source.Entity, existing);
                        sourceKeys.Add(source.KeyValue!);
                        processedEntities.Add(existing);
                        continue;
                    }

                    context.Set<TEntity>().Add(source.Entity);
                    processedEntities.Add(source.Entity);
                }

                var chunkSavedCount = await context.BulkSaveChangesAsync(options.BatchSize, cancellationToken).ConfigureAwait(false);
                savedCount += chunkSavedCount;

                foreach (var source in keyedSources)
                {
                    var keyValue = mapping.GetKeyValue(source.Entity);
                    if (!mapping.IsDefaultKeyValue(keyValue))
                    {
                        sourceKeys.Add(keyValue!);
                    }
                }

                DetachEntities(context, processedEntities);
            }

            if (options.DeleteMissing)
            {
                savedCount += await DeleteMissingAsync<TEntity>(context, mapping, options, sourceKeys, scopeValues, cancellationToken).ConfigureAwait(false);
            }

            if (ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.DisposeAsync().ConfigureAwait(false);
            }
        }

        return savedCount;
    }

    private static async Task<Dictionary<object, TEntity>> LoadExistingByKeyAsync<TEntity>(
        DbContext context,
        BulkSynchronizeEntityMapping mapping,
        IReadOnlyCollection<object?> keyValues,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        if (keyValues.Count == 0)
        {
            return [];
        }

        var query = BulkSynchronizeQueryFactory.CreateKeyLookupQuery(context, mapping, keyValues);
        var existingRows = await EntityFrameworkQueryableExtensions
            .ToListAsync((IQueryable<TEntity>)query, cancellationToken)
            .ConfigureAwait(false);

        return existingRows.ToDictionary(entity => mapping.GetKeyValue(entity)!);
    }

    private static void CopyValues<TEntity>(
        BulkSynchronizeEntityMapping mapping,
        TEntity source,
        TEntity target)
        where TEntity : class
    {
        foreach (var property in mapping.CopyProperties)
        {
            mapping.SetPropertyValue(target, property, mapping.GetPropertyValue(source, property));
        }
    }

    private static async Task<int> DeleteMissingAsync<TEntity>(
        DbContext context,
        BulkSynchronizeEntityMapping mapping,
        BulkSynchronizeOptions options,
        HashSet<object> sourceKeys,
        Dictionary<string, HashSet<object?>> scopeValues,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var query = scopeValues.Count == 0
            ? context.Set<TEntity>().AsNoTracking()
            : (IQueryable<TEntity>)BulkSynchronizeQueryFactory.CreateScopeQuery(
                context,
                mapping,
                scopeValues.ToDictionary(pair => pair.Key, pair => (IReadOnlyCollection<object?>)pair.Value));

        var deleteBatch = new List<TEntity>(options.BatchSize);
        var savedCount = 0;
        await foreach (var existing in query.AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var keyValue = mapping.GetKeyValue(existing);
            if (keyValue is not null && sourceKeys.Contains(keyValue))
            {
                continue;
            }

            deleteBatch.Add(existing);
            if (deleteBatch.Count == options.BatchSize)
            {
                savedCount += await DeleteBatchAsync(context, options, deleteBatch, cancellationToken).ConfigureAwait(false);
            }
        }

        if (deleteBatch.Count > 0)
        {
            savedCount += await DeleteBatchAsync(context, options, deleteBatch, cancellationToken).ConfigureAwait(false);
        }

        return savedCount;
    }

    private static async Task<int> DeleteBatchAsync<TEntity>(
        DbContext context,
        BulkSynchronizeOptions options,
        List<TEntity> deleteBatch,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        context.Set<TEntity>().AttachRange(deleteBatch);
        context.Set<TEntity>().RemoveRange(deleteBatch);
        var savedCount = await context.BulkSaveChangesAsync(options.BatchSize, cancellationToken).ConfigureAwait(false);
        DetachEntities(context, deleteBatch);
        deleteBatch.Clear();
        return savedCount;
    }

    private static Dictionary<string, HashSet<object?>> CreateScopeValueSets(BulkSynchronizeEntityMapping mapping)
    {
        return mapping.ScopeProperties.ToDictionary(property => property.Name, _ => new HashSet<object?>());
    }

    private static void AddScopeValues<TEntity>(
        BulkSynchronizeEntityMapping mapping,
        Dictionary<string, HashSet<object?>> scopeValues,
        TEntity[] entities,
        bool validateConsistency)
        where TEntity : class
    {
        foreach (var property in mapping.ScopeProperties)
        {
            var values = scopeValues[property.Name];
            foreach (var entity in entities)
            {
                values.Add(mapping.GetPropertyValue(entity, property));
            }

            if (validateConsistency && values.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Delete scope property '{property.Name}' must have one consistent value across all source entities.");
            }
        }
    }

    private static void DetachEntities(DbContext context, IEnumerable<object> entities)
    {
        foreach (var entity in entities)
        {
            var entry = context.Entry(entity);
            if (entry.State != EntityState.Detached)
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    private static IEnumerable<TEntity[]> ReadChunks<TEntity>(
        TEntity firstEntity,
        IEnumerator<TEntity> enumerator,
        int batchSize)
    {
        var batch = new List<TEntity>(batchSize) { firstEntity };
        while (batch.Count < batchSize && enumerator.MoveNext())
        {
            batch.Add(enumerator.Current);
        }

        yield return batch.ToArray();

        while (enumerator.MoveNext())
        {
            batch.Clear();
            batch.Add(enumerator.Current);
            while (batch.Count < batchSize && enumerator.MoveNext())
            {
                batch.Add(enumerator.Current);
            }

            yield return batch.ToArray();
        }
    }

    private sealed record SourceEntity<TEntity>(TEntity Entity, object? KeyValue)
        where TEntity : class;
}
