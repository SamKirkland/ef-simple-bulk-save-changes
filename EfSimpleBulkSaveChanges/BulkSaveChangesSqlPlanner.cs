using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace EfSimpleBulkSaveChanges;

internal static class BulkSaveChangesSqlPlanner
{
    public static BulkSaveChangesPlan CreatePlan(
        DbContext context,
        IReadOnlyList<EntityEntry> entries,
        BulkSaveChangesOptions options)
    {
        if (!context.Database.IsRelational())
        {
            throw new NotSupportedException("BulkSaveChangesAsync requires a relational EF Core provider.");
        }

        var commands = new List<BulkSaveChangesCommand>();

        foreach (var entityGroup in entries.GroupBy(entry => entry.Metadata))
        {
            var mapping = EntityMapping.Create(entityGroup.Key);
            var entityEntries = entityGroup.ToList();

            commands.AddRange(CreateInsertCommands(mapping, entityEntries.Where(entry => entry.State == EntityState.Added), options.BatchSize));
            commands.AddRange(CreateUpdateCommands(mapping, entityEntries.Where(entry => entry.State == EntityState.Modified), options.BatchSize));
            commands.AddRange(CreateDeleteCommands(mapping, entityEntries.Where(entry => entry.State == EntityState.Deleted), options.BatchSize));
        }

        return new BulkSaveChangesPlan(commands);
    }

    private static IEnumerable<BulkSaveChangesCommand> CreateInsertCommands(
        EntityMapping mapping,
        IEnumerable<EntityEntry> entries,
        int batchSize)
    {
        foreach (var batch in entries.Chunk(batchSize))
        {
            if (batch.Length == 0)
            {
                continue;
            }

            var parameters = new List<BulkSaveChangesParameter>();
            var generatedAssignments = new List<GeneratedValueAssignment>();
            var insertProperties = mapping.InsertProperties;
            var generatedProperties = mapping.GeneratedProperties;
            var sql = new StringBuilder();

            sql.Append("INSERT INTO ");
            sql.Append(mapping.TableIdentifier);
            sql.Append(" (");
            sql.AppendJoin(", ", insertProperties.Select(mapping.GetColumnIdentifier));
            sql.AppendLine(")");
            sql.Append("VALUES ");

            for (var rowIndex = 0; rowIndex < batch.Length; rowIndex++)
            {
                if (rowIndex > 0)
                {
                    sql.Append(", ");
                }

                sql.Append('(');

                for (var columnIndex = 0; columnIndex < insertProperties.Count; columnIndex++)
                {
                    if (columnIndex > 0)
                    {
                        sql.Append(", ");
                    }

                    var property = insertProperties[columnIndex];
                    var parameterName = AddParameter(parameters, batch[rowIndex].Property(property).CurrentValue, property);
                    sql.Append(parameterName);
                }

                sql.Append(')');

                foreach (var property in generatedProperties)
                {
                    generatedAssignments.Add(new GeneratedValueAssignment(batch[rowIndex], property));
                }
            }

            if (generatedProperties.Count > 0)
            {
                sql.AppendLine();
                sql.Append("RETURNING ");
                sql.AppendJoin(", ", generatedProperties.Select(mapping.GetColumnIdentifier));
            }

            sql.Append(';');
            yield return new BulkSaveChangesCommand(sql.ToString(), parameters, generatedAssignments);
        }
    }

    private static IEnumerable<BulkSaveChangesCommand> CreateUpdateCommands(
        EntityMapping mapping,
        IEnumerable<EntityEntry> entries,
        int batchSize)
    {
        foreach (var batch in entries.Chunk(batchSize))
        {
            var updateProperties = mapping.UpdatableProperties;
            if (batch.Length == 0 || updateProperties.Count == 0)
            {
                continue;
            }

            var parameters = new List<BulkSaveChangesParameter>();
            var sql = new StringBuilder();

            sql.Append("WITH source(");
            sql.Append(mapping.KeyColumnIdentifier);
            foreach (var property in updateProperties)
            {
                sql.Append(", ");
                sql.Append(mapping.GetColumnIdentifier(property));
            }

            sql.AppendLine(") AS (");
            sql.Append("  VALUES ");

            for (var rowIndex = 0; rowIndex < batch.Length; rowIndex++)
            {
                if (rowIndex > 0)
                {
                    sql.Append(", ");
                }

                sql.Append('(');
                sql.Append(AddParameter(parameters, batch[rowIndex].Property(mapping.KeyProperty).CurrentValue, mapping.KeyProperty));

                foreach (var property in updateProperties)
                {
                    sql.Append(", ");
                    sql.Append(AddParameter(parameters, batch[rowIndex].Property(property).CurrentValue, property));
                }

                sql.Append(')');
            }

            sql.AppendLine();
            sql.AppendLine(")");
            sql.Append("UPDATE ");
            sql.Append(mapping.TableIdentifier);
            sql.AppendLine(" AS target SET");

            for (var propertyIndex = 0; propertyIndex < updateProperties.Count; propertyIndex++)
            {
                if (propertyIndex > 0)
                {
                    sql.AppendLine(",");
                }

                var property = updateProperties[propertyIndex];
                sql.Append("  ");
                sql.Append(mapping.GetColumnIdentifier(property));
                sql.Append(" = source.");
                sql.Append(mapping.GetColumnIdentifier(property));
            }

            sql.AppendLine();
            sql.AppendLine("FROM source");
            sql.Append("WHERE target.");
            sql.Append(mapping.KeyColumnIdentifier);
            sql.Append(" = source.");
            sql.Append(mapping.KeyColumnIdentifier);
            sql.Append(';');

            yield return new BulkSaveChangesCommand(sql.ToString(), parameters, []);
        }
    }

    private static IEnumerable<BulkSaveChangesCommand> CreateDeleteCommands(
        EntityMapping mapping,
        IEnumerable<EntityEntry> entries,
        int batchSize)
    {
        foreach (var batch in entries.Chunk(batchSize))
        {
            if (batch.Length == 0)
            {
                continue;
            }

            var parameters = new List<BulkSaveChangesParameter>();
            var sql = new StringBuilder();

            sql.Append("DELETE FROM ");
            sql.Append(mapping.TableIdentifier);
            sql.Append(" WHERE ");
            sql.Append(mapping.KeyColumnIdentifier);
            sql.Append(" IN (");
            sql.AppendJoin(", ", batch.Select(entry => AddParameter(parameters, entry.Property(mapping.KeyProperty).CurrentValue, mapping.KeyProperty)));
            sql.Append(");");

            yield return new BulkSaveChangesCommand(sql.ToString(), parameters, []);
        }
    }

    private static string GetOrAddKeyParameter(
        EntityMapping mapping,
        EntityEntry entry,
        List<BulkSaveChangesParameter> parameters,
        Dictionary<EntityEntry, string> keyParameterNames)
    {
        if (keyParameterNames.TryGetValue(entry, out var parameterName))
        {
            return parameterName;
        }

        parameterName = AddParameter(parameters, entry.Property(mapping.KeyProperty).CurrentValue, mapping.KeyProperty);
        keyParameterNames.Add(entry, parameterName);
        return parameterName;
    }

    private static string AddParameter(List<BulkSaveChangesParameter> parameters, object? value, IProperty property)
    {
        var parameterName = $"@p{parameters.Count}";
        var converter = property.GetTypeMapping().Converter;
        var providerValue = converter is null ? value : converter.ConvertToProvider(value);
        parameters.Add(new BulkSaveChangesParameter(parameterName, providerValue));
        return parameterName;
    }

    private sealed class EntityMapping
    {
        private readonly IReadOnlyDictionary<IProperty, string> _columnNames;

        private EntityMapping(
            string tableIdentifier,
            IProperty keyProperty,
            string keyColumnIdentifier,
            IReadOnlyList<IProperty> insertProperties,
            IReadOnlyList<IProperty> updatableProperties,
            IReadOnlyList<IProperty> generatedProperties,
            IReadOnlyDictionary<IProperty, string> columnNames)
        {
            TableIdentifier = tableIdentifier;
            KeyProperty = keyProperty;
            KeyColumnIdentifier = keyColumnIdentifier;
            InsertProperties = insertProperties;
            UpdatableProperties = updatableProperties;
            GeneratedProperties = generatedProperties;
            _columnNames = columnNames;
        }

        public string TableIdentifier { get; }

        public IProperty KeyProperty { get; }

        public string KeyColumnIdentifier { get; }

        public IReadOnlyList<IProperty> InsertProperties { get; }

        public IReadOnlyList<IProperty> UpdatableProperties { get; }

        public IReadOnlyList<IProperty> GeneratedProperties { get; }

        public static EntityMapping Create(IEntityType entityType)
        {
            if (entityType.IsOwned())
            {
                throw new NotSupportedException($"Entity type '{entityType.DisplayName()}' is owned. Owned entity types are not supported by BulkSaveChangesAsync.");
            }

            if (entityType.BaseType is not null || entityType.GetDerivedTypes().Any())
            {
                throw new NotSupportedException($"Entity type '{entityType.DisplayName()}' uses inheritance. Inheritance mappings are not supported by BulkSaveChangesAsync.");
            }

            if (entityType.GetProperties().Any(property => property.IsConcurrencyToken))
            {
                throw new NotSupportedException($"Entity type '{entityType.DisplayName()}' has concurrency tokens. Concurrency tokens are not supported by BulkSaveChangesAsync.");
            }

            var tableName = entityType.GetTableName();
            if (tableName is null)
            {
                throw new NotSupportedException($"Entity type '{entityType.DisplayName()}' is not mapped to a table.");
            }

            var schema = entityType.GetSchema();
            var storeObject = StoreObjectIdentifier.Table(tableName, schema);
            var key = entityType.FindPrimaryKey();
            if (key is null || key.Properties.Count != 1)
            {
                throw new NotSupportedException($"Entity type '{entityType.DisplayName()}' must have a single-column primary key.");
            }

            var keyProperty = key.Properties[0];
            if (keyProperty.IsShadowProperty())
            {
                throw new NotSupportedException($"Entity type '{entityType.DisplayName()}' uses a shadow primary key. Shadow keys are not supported by BulkSaveChangesAsync.");
            }

            var columnNames = new Dictionary<IProperty, string>();
            foreach (var property in entityType
                .GetProperties()
                .OrderBy(property => property.GetColumnName(storeObject), StringComparer.Ordinal))
            {
                if (property.IsShadowProperty())
                {
                    continue;
                }

                var columnName = property.GetColumnName(storeObject);
                if (columnName is not null)
                {
                    columnNames.Add(property, columnName);
                }
            }

            if (!columnNames.ContainsKey(keyProperty))
            {
                throw new NotSupportedException($"Primary key for entity type '{entityType.DisplayName()}' is not mapped to the target table.");
            }

            var generatedProperties = columnNames.Keys
                .Where(property => property.ValueGenerated == ValueGenerated.OnAdd)
                .ToList();
            var insertProperties = columnNames.Keys
                .Where(property => property.ValueGenerated != ValueGenerated.OnAdd)
                .ToList();
            var updatableProperties = columnNames.Keys
                .Where(property => !property.IsPrimaryKey() && property.ValueGenerated == ValueGenerated.Never)
                .ToList();

            if (insertProperties.Count == 0)
            {
                throw new NotSupportedException($"Entity type '{entityType.DisplayName()}' has no insertable columns.");
            }

            var tableIdentifier = schema is null
                ? QuoteIdentifier(tableName)
                : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(tableName)}";

            return new EntityMapping(
                tableIdentifier,
                keyProperty,
                QuoteIdentifier(columnNames[keyProperty]),
                insertProperties,
                updatableProperties,
                generatedProperties,
                columnNames);
        }

        public string GetColumnIdentifier(IProperty property)
        {
            return QuoteIdentifier(_columnNames[property]);
        }
    }

    private static string QuoteIdentifier(string identifier)
    {
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
