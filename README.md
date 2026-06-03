# EF Simple Bulk Save Changes

Small EF Core helpers for reducing database round trips without adopting a large bulk library. The focus is simple PostgreSQL/CockroachDB-friendly SQL: multi-row inserts, batched updates, batched deletes, and source-list synchronization without `MERGE`, temp tables, or CTE-heavy tricks.

## Quick Start

Copy these files from `EfSimpleBulkSaveChanges/` into your app or shared library:

```text
BulkSaveChangesCommand.cs
BulkSaveChangesCommandExecutor.cs
BulkSaveChangesExtensions.cs
BulkSaveChangesOptions.cs
BulkSaveChangesSqlPlanner.cs
BulkSynchronizeEntityMapping.cs
BulkSynchronizeExtensions.cs
BulkSynchronizeOptions.cs
BulkSynchronizeQueryFactory.cs
```

Add EF Core relational support if your project does not already reference it:

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.Relational" Version="9.0.2" />
```

Then use the extension methods:

```csharp
using EfSimpleBulkSaveChanges;

await db.BulkSaveChangesAsync();
await db.BulkSaveChangesAsync(batchSize: 2_000);

await db.BulkSynchronizeAsync(usersFromImport);
await db.BulkSynchronizeAsync(usersFromImport, options =>
{
    options.BatchSize = 5_000;
    options.DeleteMissing = true;
    options.DeleteScopePropertyNames.Add(nameof(User.TenantId));
});
```

`BulkSaveChangesAsync` saves tracked `Added`, `Modified`, and `Deleted` entities. `BulkSynchronizeAsync` accepts detached/source entities, loads matching rows by primary key, applies changes through EF tracking, inserts missing rows, and optionally deletes rows missing from the source.

## Example Entity

The SQL examples below assume:

```csharp
public sealed class User
{
    public int Id { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public string? Email { get; set; }
    public int TenantId { get; set; }
}
```

## SQL Shape

Exact parameter names and aliases can vary by provider/version. These examples show the important difference: EF usually emits one data-change command per tracked row, while this library batches rows into fewer commands.

### Insert

```csharp
db.Users.AddRange(
    new User { FirstName = "John", LastName = "Doe", Email = "john.doe@example.com", TenantId = 1 },
    new User { FirstName = "Jane", LastName = "Smith", Email = "jane.smith@example.com", TenantId = 1 });
```

Typical EF `SaveChangesAsync` shape:

```sql
INSERT INTO "users" ("email", "first_name", "last_name", "tenant_id")
VALUES (@p0, @p1, @p2, @p3)
RETURNING "id";

INSERT INTO "users" ("email", "first_name", "last_name", "tenant_id")
VALUES (@p4, @p5, @p6, @p7)
RETURNING "id";
```

Bulk shape:

```sql
INSERT INTO "users" ("email", "first_name", "last_name", "tenant_id")
VALUES (@p0, @p1, @p2, @p3), (@p4, @p5, @p6, @p7)
RETURNING "id";
```

### Update

```csharp
users[0].FirstName = "Johnny";
users[1].Email = "jane.updated@example.com";
```

Typical EF `SaveChangesAsync` shape:

```sql
UPDATE "users"
SET "first_name" = @p0
WHERE "id" = @p1;

UPDATE "users"
SET "email" = @p2
WHERE "id" = @p3;
```

Bulk shape:

```sql
UPDATE "users" AS target SET
  "email" = source."email",
  "first_name" = source."first_name",
  "last_name" = source."last_name",
  "tenant_id" = source."tenant_id"
FROM (SELECT @p0 AS "id", @p1 AS "email", @p2 AS "first_name", @p3 AS "last_name", @p4 AS "tenant_id"
      UNION ALL SELECT @p5, @p6, @p7, @p8, @p9) AS source
WHERE target."id" = source."id";
```

For modified rows, the bulk update writes all normal updatable scalar columns, not only the specific properties EF marked modified. That keeps the SQL simple and lets one command update many rows.

### Delete

```csharp
db.Users.RemoveRange(usersToDelete);
```

Typical EF `SaveChangesAsync` shape:

```sql
DELETE FROM "users"
WHERE "id" = @p0;

DELETE FROM "users"
WHERE "id" = @p1;
```

Bulk shape:

```sql
DELETE FROM "users" WHERE "id" IN (@p0, @p1);
```

### Synchronize

EF Core does not have a direct synchronize operation. A manual version usually loads existing rows, compares them to the source list, then calls `Add`, updates properties, and optionally removes missing rows.

`BulkSynchronizeAsync` automates that pattern in batches. It uses a simple key lookup:

```sql
SELECT "u"."id", "u"."email", "u"."first_name", "u"."last_name", "u"."tenant_id"
FROM "users" AS "u"
WHERE "u"."id" IN (@p0, @p1, @p2);
```

Then it reuses the same batched insert, update, and delete shapes shown above.

Delete behavior is opt-in. Empty source lists are always a no-op. For partial/table-partition sync, scope deletes to a mapped scalar property:

```csharp
await db.BulkSynchronizeAsync(usersFromTenant, options =>
{
    options.DeleteMissing = true;
    options.DeleteScopePropertyNames.Add(nameof(User.TenantId));
});
```

All source rows must have the same value for each delete scope property.

## Support

Supported:

- Relational EF Core providers
- Single-column non-shadow primary keys
- Normal scalar mapped properties
- Tracked `Added`, `Modified`, and `Deleted` entries
- Database-generated keys on insert via `RETURNING`
- `BulkSynchronizeAsync` source-list insert/update with opt-in delete-missing behavior

Not supported:

- Non-relational providers
- Composite keys
- Owned entity types
- Inheritance mappings
- Concurrency tokens
- Shadow primary keys
- Entities not mapped to a table
- Custom synchronize keys, expression-based scopes, soft delete formulas, `MERGE`, temp-table sync, and keep-identity synchronize imports

## Testing

```powershell
dotnet test
```

The tests assert generated SQL and parameter values without requiring a running PostgreSQL or CockroachDB instance.
