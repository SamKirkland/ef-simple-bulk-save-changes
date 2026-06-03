using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace EfSimpleBulkSaveChanges;

internal sealed class BulkSynchronizeEntityMapping
{
    private BulkSynchronizeEntityMapping(
        IEntityType entityType,
        IProperty keyProperty,
        IReadOnlyList<IProperty> copyProperties,
        IReadOnlyList<IProperty> scopeProperties)
    {
        EntityType = entityType;
        KeyProperty = keyProperty;
        CopyProperties = copyProperties;
        ScopeProperties = scopeProperties;
    }

    public IEntityType EntityType { get; }

    public IProperty KeyProperty { get; }

    public IReadOnlyList<IProperty> CopyProperties { get; }

    public IReadOnlyList<IProperty> ScopeProperties { get; }

    public Type ClrType => EntityType.ClrType;

    public static BulkSynchronizeEntityMapping Create(
        DbContext context,
        Type entityClrType,
        IReadOnlyCollection<string>? deleteScopePropertyNames = null)
    {
        if (!context.Database.IsRelational())
        {
            throw new NotSupportedException("BulkSynchronizeAsync requires a relational EF Core provider.");
        }

        var entityType = context.Model.FindEntityType(entityClrType)
            ?? throw new NotSupportedException($"Entity type '{entityClrType.Name}' is not part of the DbContext model.");

        if (entityType.IsOwned())
        {
            throw new NotSupportedException($"Entity type '{entityType.DisplayName()}' is owned. Owned entity types are not supported by BulkSynchronizeAsync.");
        }

        if (entityType.BaseType is not null || entityType.GetDerivedTypes().Any())
        {
            throw new NotSupportedException($"Entity type '{entityType.DisplayName()}' uses inheritance. Inheritance mappings are not supported by BulkSynchronizeAsync.");
        }

        if (entityType.GetProperties().Any(property => property.IsConcurrencyToken))
        {
            throw new NotSupportedException($"Entity type '{entityType.DisplayName()}' has concurrency tokens. Concurrency tokens are not supported by BulkSynchronizeAsync.");
        }

        var tableName = entityType.GetTableName();
        if (tableName is null)
        {
            throw new NotSupportedException($"Entity type '{entityType.DisplayName()}' is not mapped to a table.");
        }

        var storeObject = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
        var key = entityType.FindPrimaryKey();
        if (key is null || key.Properties.Count != 1)
        {
            throw new NotSupportedException($"Entity type '{entityType.DisplayName()}' must have a single-column primary key.");
        }

        var keyProperty = key.Properties[0];
        if (keyProperty.IsShadowProperty())
        {
            throw new NotSupportedException($"Entity type '{entityType.DisplayName()}' uses a shadow primary key. Shadow keys are not supported by BulkSynchronizeAsync.");
        }

        if (keyProperty.GetColumnName(storeObject) is null)
        {
            throw new NotSupportedException($"Primary key for entity type '{entityType.DisplayName()}' is not mapped to the target table.");
        }

        var mappedProperties = entityType
            .GetProperties()
            .Where(property => !property.IsShadowProperty() && property.GetColumnName(storeObject) is not null)
            .ToList();

        var copyProperties = mappedProperties
            .Where(property => !property.IsPrimaryKey() && property.ValueGenerated == ValueGenerated.Never)
            .ToList();

        var scopeProperties = new List<IProperty>();
        foreach (var propertyName in deleteScopePropertyNames ?? [])
        {
            var property = mappedProperties.SingleOrDefault(property => property.Name == propertyName);
            if (property is null)
            {
                throw new ArgumentException(
                    $"Delete scope property '{propertyName}' is not a mapped scalar property on entity type '{entityType.DisplayName()}'.",
                    nameof(deleteScopePropertyNames));
            }

            if (property.IsPrimaryKey())
            {
                throw new ArgumentException(
                    $"Delete scope property '{propertyName}' cannot be the primary key.",
                    nameof(deleteScopePropertyNames));
            }

            scopeProperties.Add(property);
        }

        return new BulkSynchronizeEntityMapping(entityType, keyProperty, copyProperties, scopeProperties);
    }

    public object? GetKeyValue(object entity)
    {
        return GetPropertyValue(entity, KeyProperty);
    }

    public void SetKeyValue(object entity, object? value)
    {
        SetPropertyValue(entity, KeyProperty, value);
    }

    public object? GetPropertyValue(object entity, IProperty property)
    {
        if (property.PropertyInfo is not null)
        {
            return property.PropertyInfo.GetValue(entity);
        }

        if (property.FieldInfo is not null)
        {
            return property.FieldInfo.GetValue(entity);
        }

        throw new NotSupportedException($"Property '{property.Name}' on entity type '{EntityType.DisplayName()}' cannot be read.");
    }

    public void SetPropertyValue(object entity, IProperty property, object? value)
    {
        if (property.PropertyInfo is not null)
        {
            property.PropertyInfo.SetValue(entity, value);
            return;
        }

        if (property.FieldInfo is not null)
        {
            property.FieldInfo.SetValue(entity, value);
            return;
        }

        throw new NotSupportedException($"Property '{property.Name}' on entity type '{EntityType.DisplayName()}' cannot be written.");
    }

    public bool IsDefaultKeyValue(object? keyValue)
    {
        return Equals(keyValue, GetDefaultValue(KeyProperty.ClrType));
    }

    private static object? GetDefaultValue(Type type)
    {
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
