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

## SQLite, PostgreSQL, and CockroachDB Performance Results

These results come from `BulkSaveChangesPerformanceTests` using SQLite in-memory databases, `BulkSaveChangesPostgreSqlPerformanceTests` using a Docker-hosted PostgreSQL 16 container, and `BulkSaveChangesCockroachDbPerformanceTests` using a Docker-hosted CockroachDB v26.2.1 single-node container on a local development machine. The PostgreSQL and CockroachDB performance suites start throwaway containers and remove them after the run. Timings are smoke-test measurements, not BenchmarkDotNet results. Insert, update, and mixed measurements time only the save call after entities have been staged in the change tracker. Synchronize measurements time the full synchronize operation: the manual `SaveChanges` path loads matching rows, applies source values, deletes missing rows, adds new rows, and saves; the `BulkSynchronize` path runs `BulkSynchronizeAsync` over the same source shape. Update, mixed, and synchronize scenarios use smaller bulk batch sizes because SQLite limits the number of `UNION ALL` terms in the generated update shape.

![BulkSaveChanges performance chart](docs/performance-results.svg)

### Insert

| Rows | Method | Batch Size | SQLite ms | SQLite speedup | PostgreSQL ms | PostgreSQL speedup | CockroachDB ms | CockroachDB speedup |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | SaveChanges |  | 0.62 | 1x | 2.07 | 1x | 12.58 | 1x |
| 1 | BulkSaveChanges | 100 | 0.62 | 1.00x | 2.65 | 0.78x | 20.57 | 0.61x |
| 100 | SaveChanges |  | 5.01 | 1x | 15.33 | 1x | 848.07 | 1x |
| 100 | BulkSaveChanges | 100 | 1.25 | 4.01x | 3.96 | 3.87x | 463.56 | 1.83x |
| 1,000 | SaveChanges |  | 35.22 | 1x | 39.68 | 1x | 7,990.47 | 1x |
| 1,000 | BulkSaveChanges | 100 | 9.64 | 3.65x | 12.15 | 3.26x | 4,236.69 | 1.89x |
| 1,000 | BulkSaveChanges | 1,000 | 25.61 | 1.38x | 7.16 | 5.54x | 2,595.13 | 3.08x |
| 10,000 | SaveChanges |  | 378.37 | 1x | 277.68 | 1x | 27,349.74 | 1x |
| 10,000 | BulkSaveChanges | 100 | 144.16 | 2.62x | 99.58 | 2.79x | 9,518.90 | 2.87x |
| 10,000 | BulkSaveChanges | 1,000 | 268.26 | 1.41x | 71.09 | 3.91x | 9,377.52 | 2.92x |
| 10,000 | BulkSaveChanges | 10,000 | 1,736.32 | 0.22x | 70.44 | 3.94x | 9,293.94 | 2.94x |

### Update

| Rows | Method | Batch Size | SQLite ms | SQLite speedup | PostgreSQL ms | PostgreSQL speedup | CockroachDB ms | CockroachDB speedup |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | SaveChanges |  | 3.43 | 1x | 2.68 | 1x | 5.12 | 1x |
| 1 | BulkSaveChanges | 100 | 0.43 | 8.00x | 1.47 | 1.83x | 4.14 | 1.24x |
| 100 | SaveChanges |  | 5.09 | 1x | 3.91 | 1x | 31.81 | 1x |
| 100 | BulkSaveChanges | 100 | 1.74 | 2.92x | 3.42 | 1.14x | 11.42 | 2.78x |
| 1,000 | SaveChanges |  | 16.52 | 1x | 32.71 | 1x | 268.75 | 1x |
| 1,000 | BulkSaveChanges | 100 | 8.42 | 1.96x | 24.52 | 1.33x | 60.80 | 4.42x |
| 1,000 | BulkSaveChanges | 250 | 13.32 | 1.24x | 21.71 | 1.51x | 75.15 | 3.58x |
| 10,000 | SaveChanges |  | 210.75 | 1x | 292.45 | 1x | 2,793.38 | 1x |
| 10,000 | BulkSaveChanges | 100 | 91.24 | 2.31x | 295.42 | 0.99x | 577.79 | 4.83x |
| 10,000 | BulkSaveChanges | 250 | 150.94 | 1.40x | 241.43 | 1.21x | 633.16 | 4.41x |

### Mixed Add/Update/Delete

| Rows | Method | Batch Size | SQLite ms | SQLite speedup | PostgreSQL ms | PostgreSQL speedup | CockroachDB ms | CockroachDB speedup |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | SaveChanges |  | 0.09 | 1x | 1.07 | 1x | 3.01 | 1x |
| 1 | BulkSaveChanges | 100 | 0.13 | 0.66x | 1.59 | 0.68x | 3.43 | 0.88x |
| 100 | SaveChanges |  | 4.81 | 1x | 8.56 | 1x | 87.89 | 1x |
| 100 | BulkSaveChanges | 100 | 0.76 | 6.36x | 3.43 | 2.50x | 43.48 | 2.02x |
| 1,000 | SaveChanges |  | 9.62 | 1x | 24.74 | 1x | 500.10 | 1x |
| 1,000 | BulkSaveChanges | 100 | 5.50 | 1.75x | 17.17 | 1.44x | 222.67 | 2.25x |
| 1,000 | BulkSaveChanges | 250 | 7.28 | 1.32x | 12.16 | 2.03x | 374.80 | 1.33x |
| 10,000 | SaveChanges |  | 142.60 | 1x | 262.10 | 1x | 24,891.10 | 1x |
| 10,000 | BulkSaveChanges | 100 | 83.62 | 1.71x | 206.83 | 1.27x | 7,113.97 | 3.50x |
| 10,000 | BulkSaveChanges | 250 | 88.50 | 1.61x | 137.29 | 1.91x | 6,299.55 | 3.95x |

### Synchronize

| Rows | Method | Batch Size | SQLite ms | SQLite speedup | PostgreSQL ms | PostgreSQL speedup | CockroachDB ms | CockroachDB speedup |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | SaveChanges |  | 7.35 | 1x | 9.25 | 1x | 8.68 | 1x |
| 1 | BulkSynchronize | 100 | 24.81 | 0.30x | 31.02 | 0.30x | 33.46 | 0.26x |
| 100 | SaveChanges |  | 7.95 | 1x | 18.79 | 1x | 408.28 | 1x |
| 100 | BulkSynchronize | 100 | 8.05 | 0.99x | 19.34 | 0.97x | 111.08 | 3.68x |
| 1,000 | SaveChanges |  | 47.77 | 1x | 55.88 | 1x | 2,031.35 | 1x |
| 1,000 | BulkSynchronize | 100 | 34.95 | 1.37x | 44.86 | 1.25x | 994.76 | 2.04x |
| 1,000 | BulkSynchronize | 250 | 42.48 | 1.12x | 27.88 | 2.00x | 1,006.78 | 2.02x |
| 10,000 | SaveChanges |  | 654.93 | 1x | 443.74 | 1x | 7,521.64 | 1x |
| 10,000 | BulkSynchronize | 100 | 334.68 | 1.96x | 418.43 | 1.06x | 3,537.93 | 2.13x |
| 10,000 | BulkSynchronize | 250 | 238.75 | 2.74x | 271.75 | 1.63x | 3,530.99 | 2.13x |
