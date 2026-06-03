using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfSimpleBulkSaveChanges;

internal sealed class BulkSaveChangesPlan(IReadOnlyList<BulkSaveChangesCommand> commands)
{
    public IReadOnlyList<BulkSaveChangesCommand> Commands { get; } = commands;
}

internal sealed class BulkSaveChangesCommand(
    string commandText,
    IReadOnlyList<BulkSaveChangesParameter> parameters,
    IReadOnlyList<GeneratedValueAssignment> generatedValueAssignments)
{
    public string CommandText { get; } = commandText;

    public IReadOnlyList<BulkSaveChangesParameter> Parameters { get; } = parameters;

    public IReadOnlyList<GeneratedValueAssignment> GeneratedValueAssignments { get; } = generatedValueAssignments;
}

internal sealed class BulkSaveChangesParameter(string name, object? value)
{
    public string Name { get; } = name;

    public object? Value { get; } = value;
}

internal sealed class GeneratedValueAssignment(EntityEntry entry, IProperty property)
{
    public EntityEntry Entry { get; } = entry;

    public IProperty Property { get; } = property;
}
