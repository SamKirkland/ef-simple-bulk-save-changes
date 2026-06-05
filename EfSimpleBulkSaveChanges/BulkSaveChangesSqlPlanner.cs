using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace EfSimpleBulkSaveChanges;

internal static class BulkSaveChangesSqlPlanner
{
    private const int MaxParametersPerCommand = 65_535;
    private const string LoggerName = "EfSimpleBulkSaveChanges.BulkSaveChanges";

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
            var mapping = EntityMapping.Create(context, entityGroup.Key);
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
        var entryList = entries.ToList();
        var effectiveBatchSize = GetEffectiveBatchSize(mapping, "insert", batchSize, mapping.MaxInsertParametersPerRow, entryList.Count);

        foreach (var batch in entryList.Chunk(effectiveBatchSize))
        {
            if (batch.Length == 0)
            {
                continue;
            }

            var insertProperties = mapping.GetInsertProperties(batch);
            var actualBatchSize = GetEffectiveBatchSize(mapping, "insert", batch.Length, insertProperties.Count, batch.Length);
            if (actualBatchSize < batch.Length)
            {
                foreach (var command in CreateInsertCommands(mapping, batch, actualBatchSize))
                {
                    yield return command;
                }

                continue;
            }

            var parameters = new List<BulkSaveChangesParameter>();
            var generatedAssignments = new List<GeneratedValueAssignment>();
            var generatedProperties = mapping.GetGeneratedProperties(batch);
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
        var entryList = entries.ToList();
        var updateProperties = mapping.UpdatableProperties;
        if (updateProperties.Count == 0)
        {
            yield break;
        }

        var effectiveBatchSize = GetEffectiveBatchSize(mapping, "update", batchSize, updateProperties.Count + 1, entryList.Count);

        foreach (var batch in entryList.Chunk(effectiveBatchSize))
        {
            if (batch.Length == 0)
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
        var entryList = entries.ToList();
        var effectiveBatchSize = GetEffectiveBatchSize(mapping, "delete", batchSize, 1, entryList.Count);

        foreach (var batch in entryList.Chunk(effectiveBatchSize))
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

    private static int GetEffectiveBatchSize(
        EntityMapping mapping,
        string operation,
        int requestedBatchSize,
        int parametersPerRow,
        int rowCount)
    {
        if (parametersPerRow <= 0)
        {
            return requestedBatchSize;
        }

        if (parametersPerRow > MaxParametersPerCommand)
        {
            throw new NotSupportedException(
                $"Entity type '{mapping.EntityTypeName}' requires {parametersPerRow} parameters per {operation} row, which exceeds the Npgsql single-command parameter limit of {MaxParametersPerCommand}.");
        }

        var effectiveBatchSize = Math.Min(requestedBatchSize, MaxParametersPerCommand / parametersPerRow);
        if (effectiveBatchSize < requestedBatchSize && rowCount > effectiveBatchSize)
        {
            mapping.Logger.LogWarning(
                "Bulk {Operation} for entity type '{EntityType}' requested batch size {RequestedBatchSize}, but each row uses {ParametersPerRow} parameters. To stay under Npgsql's {MaxParametersPerCommand}-parameter command limit, commands will be split using an effective batch size of {EffectiveBatchSize}. Set BatchSize to {EffectiveBatchSize} or lower to avoid this warning.",
                operation,
                mapping.EntityTypeName,
                requestedBatchSize,
                parametersPerRow,
                MaxParametersPerCommand,
                effectiveBatchSize,
                effectiveBatchSize);
        }

        return effectiveBatchSize;
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
            string entityTypeName,
            string tableIdentifier,
            IProperty keyProperty,
            string keyColumnIdentifier,
            IReadOnlyList<IProperty> insertProperties,
            IReadOnlyList<IProperty> updatableProperties,
            IReadOnlyList<IProperty> generatedProperties,
            IReadOnlyDictionary<IProperty, string> columnNames,
            ILogger logger)
        {
            TableIdentifier = tableIdentifier;
            EntityTypeName = entityTypeName;
            KeyProperty = keyProperty;
            KeyColumnIdentifier = keyColumnIdentifier;
            InsertProperties = insertProperties;
            UpdatableProperties = updatableProperties;
            GeneratedProperties = generatedProperties;
            _columnNames = columnNames;
            Logger = logger;
        }

        public string TableIdentifier { get; }

        public string EntityTypeName { get; }

        public IProperty KeyProperty { get; }

        public string KeyColumnIdentifier { get; }

        public IReadOnlyList<IProperty> InsertProperties { get; }

        public IReadOnlyList<IProperty> UpdatableProperties { get; }

        public IReadOnlyList<IProperty> GeneratedProperties { get; }

        public int MaxInsertParametersPerRow => InsertProperties.Count;

        public ILogger Logger { get; }

        public static EntityMapping Create(DbContext context, IEntityType entityType)
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
                entityType.DisplayName(),
                tableIdentifier,
                keyProperty,
                QuoteIdentifier(columnNames[keyProperty]),
                insertProperties,
                updatableProperties,
                generatedProperties,
                columnNames,
                context.GetService<ILoggerFactory>().CreateLogger(LoggerName));
        }

        public string GetColumnIdentifier(IProperty property)
        {
            return QuoteIdentifier(_columnNames[property]);
        }

        public IReadOnlyList<IProperty> GetInsertProperties(IReadOnlyList<EntityEntry> entries)
        {
            return InsertProperties
                .Concat(GeneratedProperties.Where(property => IsClientGeneratedKey(property) && HasPermanentGeneratedValues(entries, property)))
                .OrderBy(property => _columnNames[property], StringComparer.Ordinal)
                .ToList();
        }

        public IReadOnlyList<IProperty> GetGeneratedProperties(IReadOnlyList<EntityEntry> entries)
        {
            return GeneratedProperties
                .Where(property => !IsClientGeneratedKey(property) || !HasPermanentGeneratedValues(entries, property))
                .ToList();
        }

        private static bool IsClientGeneratedKey(IProperty property)
        {
            return property.IsPrimaryKey();
        }

        private static bool HasPermanentGeneratedValues(IReadOnlyList<EntityEntry> entries, IProperty property)
        {
            return entries.All(entry => !entry.Property(property).IsTemporary);
        }
    }

    private static string QuoteIdentifier(string identifier)
    {
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
