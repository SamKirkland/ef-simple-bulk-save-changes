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
WITH source("id", "email", "first_name", "last_name", "tenant_id") AS (
  VALUES (@p0, @p1, @p2, @p3, @p4), (@p5, @p6, @p7, @p8, @p9)
)
UPDATE "users" AS target SET
  "email" = source."email",
  "first_name" = source."first_name",
  "last_name" = source."last_name",
  "tenant_id" = source."tenant_id"
FROM source
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

For high-volume CockroachDB inserts, prefer app-assigned keys or CockroachDB `DEFAULT unique_rowid()` primary keys mapped to a .NET `long`. The library still supports generated keys through `RETURNING`, but CockroachDB `INT GENERATED ... AS IDENTITY` is a poor fit for bulk insert throughput in local manual testing.

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

These results come from `BulkSaveChangesPerformanceTests` using SQLite in-memory databases, `BulkSaveChangesPostgreSqlPerformanceTests` using a Docker-hosted PostgreSQL 16 container, and `BulkSaveChangesCockroachDbPerformanceTests` using a Docker-hosted CockroachDB v26.2.1 single-node container on a local development machine. The CockroachDB performance schema uses `DEFAULT unique_rowid()` keys mapped to .NET `long` values, and each CockroachDB measurement reports the median of three iterations. The PostgreSQL and CockroachDB performance suites start throwaway containers and remove them after the run. Timings are smoke-test measurements, not BenchmarkDotNet results. Insert, update, and mixed measurements time only the save call after entities have been staged in the change tracker. Synchronize measurements time the full synchronize operation: the manual `SaveChanges` path loads matching rows, applies source values, deletes missing rows, adds new rows, and saves; the `BulkSynchronize` path runs `BulkSynchronizeAsync` over the same source shape.

### CockroachDB Outcome Summary

The CockroachDB-focused update change replaces `UNION ALL` update sources with a `VALUES` CTE, and the CockroachDB performance schema now matches a production-friendly `DEFAULT unique_rowid()` key shape. Insert-heavy CockroachDB workloads show the expected lift at larger row counts: 100,000-row bulk inserts completed in a median 1,007.45 ms at batch 10,000, a 15.80x speedup over `SaveChanges`. Update-heavy workloads still benefit strongly from the `VALUES` CTE: 100,000-row bulk updates reached a median 2,081.20 ms at batch 1,000, a 13.66x speedup. Mixed and synchronize scenarios also improved substantially, reaching 14.61x and 11.18x respectively for 100,000 rows with larger batches.

![BulkSaveChanges performance chart](docs/performance-results.svg)

### Insert

| Rows | Method | Batch Size | SQLite ms | SQLite speedup | PostgreSQL ms | PostgreSQL speedup | CockroachDB ms | CockroachDB speedup |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | SaveChanges |  | 0.29 | 1x | 2.94 | 1x | 4.73 | 1x |
| 1 | BulkSaveChanges | 100 | 0.55 | 0.53x | 3.13 | 0.94x | 6.45 | 0.73x |
| 100 | SaveChanges |  | 1.61 | 1x | 6.30 | 1x | 29.22 | 1x |
| 100 | BulkSaveChanges | 100 | 0.62 | 2.61x | 3.74 | 1.68x | 6.03 | 4.84x |
| 1,000 | SaveChanges |  | 20.21 | 1x | 32.70 | 1x | 163.57 | 1x |
| 1,000 | BulkSaveChanges | 100 | 5.37 | 3.76x | 13.89 | 2.35x | 22.03 | 7.43x |
| 1,000 | BulkSaveChanges | 1,000 | 20.44 | 0.99x | 8.16 | 4.01x | 14.88 | 10.99x |
| 10,000 | SaveChanges |  | 173.13 | 1x | 282.89 | 1x | 1,602.72 | 1x |
| 10,000 | BulkSaveChanges | 100 | 69.15 | 2.50x | 115.07 | 2.46x | 179.58 | 8.92x |
| 10,000 | BulkSaveChanges | 1,000 | 206.67 | 0.84x | 65.67 | 4.31x | 126.60 | 12.66x |
| 10,000 | BulkSaveChanges | 10,000 | 1,679.79 | 0.10x | 68.34 | 4.14x | 114.23 | 14.03x |
| 100,000 | SaveChanges |  | 1,389.36 | 1x | 2,715.99 | 1x | 15,917.01 | 1x |
| 100,000 | BulkSaveChanges | 100 | 586.36 | 2.37x | 1,064.35 | 2.55x | 1,763.13 | 9.03x |
| 100,000 | BulkSaveChanges | 1,000 | 2,076.36 | 0.67x | 629.16 | 4.32x | 1,339.83 | 11.88x |
| 100,000 | BulkSaveChanges | 10,000 | 16,842.03 | 0.08x | 599.00 | 4.53x | 1,007.45 | 15.80x |

### Update

| Rows | Method | Batch Size | SQLite ms | SQLite speedup | PostgreSQL ms | PostgreSQL speedup | CockroachDB ms | CockroachDB speedup |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | SaveChanges |  | 1.60 | 1x | 2.79 | 1x | 3.51 | 1x |
| 1 | BulkSaveChanges | 100 | 0.25 | 6.39x | 2.89 | 0.97x | 5.64 | 0.62x |
| 100 | SaveChanges |  | 1.05 | 1x | 5.67 | 1x | 33.29 | 1x |
| 100 | BulkSaveChanges | 100 | 0.83 | 1.26x | 3.81 | 1.49x | 7.46 | 4.46x |
| 1,000 | SaveChanges |  | 11.93 | 1x | 29.85 | 1x | 284.39 | 1x |
| 1,000 | BulkSaveChanges | 100 | 7.56 | 1.58x | 18.89 | 1.58x | 32.81 | 8.67x |
| 1,000 | BulkSaveChanges | 250 | 11.91 | 1.00x | 11.97 | 2.49x | 34.75 | 8.18x |
| 1,000 | BulkSaveChanges | 1,000 |  |  |  |  | 22.59 | 12.59x |
| 1,000 | BulkSaveChanges | 5,000 |  |  |  |  | 22.83 | 12.45x |
| 10,000 | SaveChanges |  | 121.87 | 1x | 284.70 | 1x | 2,820.06 | 1x |
| 10,000 | BulkSaveChanges | 100 | 83.51 | 1.46x | 192.58 | 1.48x | 266.15 | 10.60x |
| 10,000 | BulkSaveChanges | 250 | 139.40 | 0.87x | 127.42 | 2.23x | 305.27 | 9.24x |
| 10,000 | BulkSaveChanges | 1,000 |  |  |  |  | 197.64 | 14.27x |
| 10,000 | BulkSaveChanges | 5,000 |  |  |  |  | 175.20 | 16.10x |
| 100,000 | SaveChanges |  | 1,241.69 | 1x | 2,905.70 | 1x | 28,422.68 | 1x |
| 100,000 | BulkSaveChanges | 100 | 867.52 | 1.43x | 1,320.16 | 2.20x | 2,747.42 | 10.35x |
| 100,000 | BulkSaveChanges | 250 | 1,344.09 | 0.92x | 992.52 | 2.93x | 3,134.77 | 9.07x |
| 100,000 | BulkSaveChanges | 1,000 |  |  |  |  | 2,081.20 | 13.66x |
| 100,000 | BulkSaveChanges | 5,000 |  |  |  |  | 2,103.87 | 13.51x |

### Mixed Add/Update/Delete

| Rows | Method | Batch Size | SQLite ms | SQLite speedup | PostgreSQL ms | PostgreSQL speedup | CockroachDB ms | CockroachDB speedup |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | SaveChanges |  | 0.19 | 1x | 2.59 | 1x | 2.05 | 1x |
| 1 | BulkSaveChanges | 100 | 0.11 | 1.69x | 3.15 | 0.82x | 3.12 | 0.66x |
| 100 | SaveChanges |  | 1.97 | 1x | 5.60 | 1x | 34.54 | 1x |
| 100 | BulkSaveChanges | 100 | 0.50 | 3.96x | 4.83 | 1.16x | 8.26 | 4.18x |
| 1,000 | SaveChanges |  | 9.59 | 1x | 31.71 | 1x | 199.07 | 1x |
| 1,000 | BulkSaveChanges | 100 | 4.75 | 2.02x | 14.27 | 2.22x | 25.23 | 7.89x |
| 1,000 | BulkSaveChanges | 250 | 8.55 | 1.12x | 10.94 | 2.90x | 29.57 | 6.73x |
| 1,000 | BulkSaveChanges | 1,000 |  |  |  |  | 27.17 | 7.33x |
| 1,000 | BulkSaveChanges | 5,000 |  |  |  |  | 24.74 | 8.05x |
| 10,000 | SaveChanges |  | 158.66 | 1x | 274.16 | 1x | 2,001.35 | 1x |
| 10,000 | BulkSaveChanges | 100 | 73.15 | 2.17x | 132.10 | 2.08x | 197.99 | 10.11x |
| 10,000 | BulkSaveChanges | 250 | 84.75 | 1.87x | 134.20 | 2.04x | 261.16 | 7.66x |
| 10,000 | BulkSaveChanges | 1,000 |  |  |  |  | 157.45 | 12.71x |
| 10,000 | BulkSaveChanges | 5,000 |  |  |  |  | 124.11 | 16.13x |
| 100,000 | SaveChanges |  | 1,317.02 | 1x | 2,667.92 | 1x | 20,530.40 | 1x |
| 100,000 | BulkSaveChanges | 100 | 594.26 | 2.22x | 1,073.86 | 2.48x | 2,024.92 | 10.14x |
| 100,000 | BulkSaveChanges | 250 | 788.79 | 1.67x | 788.13 | 3.39x | 1,771.32 | 11.59x |
| 100,000 | BulkSaveChanges | 1,000 |  |  |  |  | 1,522.00 | 13.49x |
| 100,000 | BulkSaveChanges | 5,000 |  |  |  |  | 1,405.10 | 14.61x |

### Synchronize

| Rows | Method | Batch Size | SQLite ms | SQLite speedup | PostgreSQL ms | PostgreSQL speedup | CockroachDB ms | CockroachDB speedup |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | SaveChanges |  | 2.17 | 1x | 4.11 | 1x | 3.11 | 1x |
| 1 | BulkSynchronize | 100 | 4.73 | 0.46x | 8.52 | 0.48x | 8.53 | 0.36x |
| 100 | SaveChanges |  | 1.77 | 1x | 7.51 | 1x | 35.49 | 1x |
| 100 | BulkSynchronize | 100 | 3.24 | 0.55x | 8.84 | 0.85x | 12.30 | 2.88x |
| 1,000 | SaveChanges |  | 20.72 | 1x | 42.07 | 1x | 361.92 | 1x |
| 1,000 | BulkSynchronize | 100 | 24.42 | 0.85x | 32.74 | 1.28x | 61.11 | 5.92x |
| 1,000 | BulkSynchronize | 250 | 18.39 | 1.13x | 21.47 | 1.96x | 47.18 | 7.67x |
| 1,000 | BulkSynchronize | 1,000 |  |  |  |  | 39.17 | 9.24x |
| 1,000 | BulkSynchronize | 5,000 |  |  |  |  | 38.63 | 9.37x |
| 10,000 | SaveChanges |  | 298.91 | 1x | 429.49 | 1x | 3,186.67 | 1x |
| 10,000 | BulkSynchronize | 100 | 214.42 | 1.39x | 349.32 | 1.23x | 513.80 | 6.20x |
| 10,000 | BulkSynchronize | 250 | 200.85 | 1.49x | 208.06 | 2.06x | 393.45 | 8.10x |
| 10,000 | BulkSynchronize | 1,000 |  |  |  |  | 276.58 | 11.52x |
| 10,000 | BulkSynchronize | 5,000 |  |  |  |  | 266.10 | 11.98x |
| 100,000 | SaveChanges |  | 2,347.39 | 1x | 4,322.38 | 1x | 31,996.16 | 1x |
| 100,000 | BulkSynchronize | 100 | 2,071.69 | 1.13x | 3,153.58 | 1.37x | 5,306.82 | 6.03x |
| 100,000 | BulkSynchronize | 250 | 1,884.63 | 1.25x | 1,868.18 | 2.31x | 5,018.84 | 6.38x |
| 100,000 | BulkSynchronize | 1,000 |  |  |  |  | 3,020.46 | 10.59x |
| 100,000 | BulkSynchronize | 5,000 |  |  |  |  | 2,861.01 | 11.18x |
