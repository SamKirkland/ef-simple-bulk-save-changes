using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace EfSimpleBulkSaveChanges;

internal static class BulkSynchronizeQueryFactory
{
    public static IQueryable CreateKeyLookupQuery(
        DbContext context,
        BulkSynchronizeEntityMapping mapping,
        IReadOnlyCollection<object?> keyValues)
    {
        var method = typeof(BulkSynchronizeQueryFactory)
            .GetMethod(nameof(CreateKeyLookupQueryCore), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(mapping.ClrType, mapping.KeyProperty.ClrType);

        return (IQueryable)method.Invoke(null, [context, mapping.KeyProperty.Name, keyValues])!;
    }

    public static IQueryable CreateScopeQuery(
        DbContext context,
        BulkSynchronizeEntityMapping mapping,
        IReadOnlyDictionary<string, IReadOnlyCollection<object?>> scopeValues)
    {
        var method = typeof(BulkSynchronizeQueryFactory)
            .GetMethod(nameof(CreateScopeQueryCore), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(mapping.ClrType);

        return (IQueryable)method.Invoke(null, [context, scopeValues])!;
    }

    private static IQueryable<TEntity> CreateKeyLookupQueryCore<TEntity, TKey>(
        DbContext context,
        string keyPropertyName,
        IReadOnlyCollection<object?> keyValues)
        where TEntity : class
    {
        var typedKeys = keyValues.Select(ConvertKey<TKey>).ToList();
        return context.Set<TEntity>()
            .Where(BuildContainsPredicate<TEntity, TKey>(keyPropertyName, typedKeys));
    }

    private static IQueryable<TEntity> CreateScopeQueryCore<TEntity>(
        DbContext context,
        IReadOnlyDictionary<string, IReadOnlyCollection<object?>> scopeValues)
        where TEntity : class
    {
        IQueryable<TEntity> query = context.Set<TEntity>().AsNoTracking();
        foreach (var (propertyName, values) in scopeValues)
        {
            var propertyType = typeof(TEntity).GetProperty(propertyName)?.PropertyType
                ?? throw new NotSupportedException($"Property '{propertyName}' cannot be found on entity type '{typeof(TEntity).Name}'.");

            var method = typeof(BulkSynchronizeQueryFactory)
                .GetMethod(nameof(ApplyScopeFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(typeof(TEntity), propertyType);

            query = (IQueryable<TEntity>)method.Invoke(null, [query, propertyName, values])!;
        }

        return query;
    }

    private static IQueryable<TEntity> ApplyScopeFilter<TEntity, TProperty>(
        IQueryable<TEntity> query,
        string propertyName,
        IReadOnlyCollection<object?> values)
        where TEntity : class
    {
        var typedValues = values.Select(ConvertKey<TProperty>).ToList();
        return query.Where(BuildContainsPredicate<TEntity, TProperty>(propertyName, typedValues));
    }

    private static Expression<Func<TEntity, bool>> BuildContainsPredicate<TEntity, TValue>(
        string propertyName,
        List<TValue?> values)
    {
        var parameter = Expression.Parameter(typeof(TEntity), "entity");
        var property = Expression.Call(
            typeof(EF),
            nameof(EF.Property),
            [typeof(TValue)],
            parameter,
            Expression.Constant(propertyName));
        var contains = Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.Contains),
            [typeof(TValue)],
            Expression.Constant(values),
            property);

        return Expression.Lambda<Func<TEntity, bool>>(contains, parameter);
    }

    private static T? ConvertKey<T>(object? value)
    {
        if (value is null)
        {
            return default;
        }

        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (targetType.IsInstanceOfType(value))
        {
            return (T)value;
        }

        return (T)Convert.ChangeType(value, targetType);
    }
}
