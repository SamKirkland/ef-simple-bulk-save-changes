using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EfSimpleBulkSaveChanges;

internal static class BulkSaveChangesCommandExecutor
{
    public static async Task ExecuteAsync(
        DbContext context,
        BulkSaveChangesPlan plan,
        CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;

        if (closeConnection)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        var currentTransaction = context.Database.CurrentTransaction;
        var ownedTransaction = currentTransaction is null
            ? await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
            foreach (var command in plan.Commands)
            {
                await ExecuteCommandAsync(context, command, cancellationToken).ConfigureAwait(false);
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

            if (closeConnection)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task ExecuteCommandAsync(
        DbContext context,
        BulkSaveChangesCommand plannedCommand,
        CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();

        command.CommandText = plannedCommand.CommandText;

        var transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }

        foreach (var plannedParameter in plannedCommand.Parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = plannedParameter.Name;
            parameter.Value = plannedParameter.Value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        if (plannedCommand.GeneratedValueAssignments.Count == 0)
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var assignmentIndex = 0;
        var generatedColumnCount = plannedCommand.GeneratedValueAssignments
            .Select(assignment => assignment.Property)
            .Distinct()
            .Count();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            for (var columnIndex = 0; columnIndex < generatedColumnCount; columnIndex++)
            {
                var assignment = plannedCommand.GeneratedValueAssignments[assignmentIndex++];
                var providerValue = await reader.IsDBNullAsync(columnIndex, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetValue(columnIndex);

                assignment.Entry.Property(assignment.Property).CurrentValue = ConvertFromProviderValue(providerValue, assignment);
            }
        }
    }

    private static object? ConvertFromProviderValue(object? providerValue, GeneratedValueAssignment assignment)
    {
        if (providerValue is null)
        {
            return null;
        }

        var converter = assignment.Property.GetTypeMapping().Converter;
        var value = converter is null ? providerValue : converter.ConvertFromProvider(providerValue);
        if (value is null)
        {
            return null;
        }

        var clrType = Nullable.GetUnderlyingType(assignment.Property.ClrType) ?? assignment.Property.ClrType;
        if (clrType.IsInstanceOfType(value))
        {
            return value;
        }

        return Convert.ChangeType(value, clrType);
    }
}
