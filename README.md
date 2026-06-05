# EF Simple Bulk Save Changes

Small EF Core utilities for reducing database round trips and improving entity framework insert and update performance without adopting a large bulk library. The focus is simple PostgreSQL/CockroachDB-friendly SQL without `MERGE`, temp tables, or CTE-heavy tricks.

## Quick Start

There is no nuget package, instead copy the files from `EfSimpleBulkSaveChanges/` into your app or shared library:

## Available Utilities

### Example

`BulkSaveChangesAsync()` is a drop-in replacement for `SaveChanges()` and works by plugging into entity frameworks tracked changes (`Added`, `Modified`, and `Deleted` entities).

```csharp
using EfSimpleBulkSaveChanges;

using var dbContext = new AppDbContext();

// 1. Create your list of entities
var newUsers = new List<User>
{
    new User { Name = "Alice", Email = "alice@example.com" },
    new User { Name = "Bob", Email = "bob@example.com" },
    ...
};

// 2. Add the list to the DbContext
dbContext.Users.AddRange(newUsers);

// 3. Save changes to the database using BulkSaveChangesAsync() instead of SaveChanges()
// dbContext.SaveChanges();
await dbContext.BulkSaveChangesAsync();
```

## Example Entity

The SQL examples below assume:

```csharp
public sealed class User
{
    public long Id { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public string? Email { get; set; }
    public int TenantId { get; set; }
}
```

## SQL Shape

Entity Framework usually emits one database command per tracked row, while this library batches rows into a single insert or update command for all rows.

### Insert

```csharp
dbContext.Users.AddRange(
    new User { FirstName = "John", LastName = "Doe", Email = "john.doe@example.com", TenantId = 1 },
    new User { FirstName = "Jane", LastName = "Smith", Email = "jane.smith@example.com", TenantId = 1 }
);
```

Typical Entity Framework `SaveChangesAsync()` would result in the following SQL:

```sql
INSERT INTO "users" ("email", "first_name", "last_name", "tenant_id")
VALUES (@p0, @p1, @p2, @p3)
RETURNING "id";

INSERT INTO "users" ("email", "first_name", "last_name", "tenant_id")
VALUES (@p4, @p5, @p6, @p7)
RETURNING "id";
```

`BulkSaveChangesAsync()` results in the following SQL:

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

Typical Entity Framework `SaveChangesAsync()` would result in the following SQL:

```sql
UPDATE "users"
SET "first_name" = @p0
WHERE "id" = @p1;

UPDATE "users"
SET "email" = @p2
WHERE "id" = @p3;
```

`BulkSaveChangesAsync()` results in the following SQL:

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

### Delete

```csharp
db.Users.RemoveRange(usersToDelete);
```

Typical Entity Framework `SaveChangesAsync()` would result in the following SQL:

```sql
DELETE FROM "users"
WHERE "id" = @p0;

DELETE FROM "users"
WHERE "id" = @p1;
```

`BulkSaveChangesAsync()` results in the following SQL:

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

![BulkSaveChanges performance chart](docs/performance-results.svg)

All tests performned on a development machine
SQLite: using in-memory databases
PostgreSQL: Docker v16 container
CockroachDB: Docker v26.2.1 single-node cluster

Timing are medidan of 3 runs

### Insert

| Rows | Method | Batch Size | SQLite ms | SQLite speedup | PostgreSQL ms | PostgreSQL speedup | CockroachDB ms | CockroachDB speedup |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | SaveChanges |  | 0.21 | 1x | 1.12 | 1x | 4.70 | 1x |
| 1 | BulkSaveChanges | 100 | 0.50 | 0.42x | 1.50 | 0.75x | 5.62 | 0.84x |
| 100 | SaveChanges |  | 1.61 | 1x | 3.95 | 1x | 25.00 | 1x |
| 100 | BulkSaveChanges | 100 | 0.65 | 2.48x | 1.86 | 2.12x | 5.88 | 4.25x |
| 1,000 | SaveChanges |  | 12.68 | 1x | 24.39 | 1x | 169.65 | 1x |
| 1,000 | BulkSaveChanges | 100 | 5.28 | 2.40x | 17.44 | 1.40x | 24.54 | 6.91x |
| 1,000 | BulkSaveChanges | 1,000 | 20.21 | 0.63x | 6.09 | 4.00x | 15.70 | 10.80x |
| 10,000 | SaveChanges |  | 159.19 | 1x | 267.23 | 1x | 1,668.49 | 1x |
| 10,000 | BulkSaveChanges | 100 | 71.06 | 2.24x | 99.33 | 2.69x | 177.35 | 9.41x |
| 10,000 | BulkSaveChanges | 1,000 | 236.60 | 0.67x | 75.84 | 3.52x | 124.34 | 13.42x |
| 10,000 | BulkSaveChanges | 10,000 | 1,662.23 | 0.10x | 56.07 | 4.77x | 110.24 | 15.14x |

### Update

| Rows | Method | Batch Size | SQLite ms | SQLite speedup | PostgreSQL ms | PostgreSQL speedup | CockroachDB ms | CockroachDB speedup |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | SaveChanges |  | 1.02 | 1x | 1.08 | 1x | 2.17 | 1x |
| 1 | BulkSaveChanges | 100 | 0.10 | 10.26x | 1.16 | 0.92x | 2.79 | 0.78x |
| 100 | SaveChanges |  | 1.00 | 1x | 3.53 | 1x | 30.65 | 1x |
| 100 | BulkSaveChanges | 100 | 0.82 | 1.23x | 10.13 | 0.35x | 4.57 | 6.71x |
| 1,000 | SaveChanges |  | 9.73 | 1x | 27.10 | 1x | 283.34 | 1x |
| 1,000 | BulkSaveChanges | 100 | 6.97 | 1.40x | 12.32 | 2.20x | 26.60 | 10.65x |
| 1,000 | BulkSaveChanges | 250 | 11.87 | 0.82x | 12.39 | 2.19x | 25.28 | 11.21x |
| 1,000 | BulkSaveChanges | 1,000 |  |  |  |  | 18.39 | 15.40x |
| 1,000 | BulkSaveChanges | 5,000 |  |  |  |  | 18.31 | 15.48x |
| 10,000 | SaveChanges |  | 145.81 | 1x | 281.13 | 1x | 2,824.35 | 1x |
| 10,000 | BulkSaveChanges | 100 | 85.36 | 1.71x | 174.28 | 1.61x | 267.05 | 10.58x |
| 10,000 | BulkSaveChanges | 250 | 139.40 | 1.05x | 114.87 | 2.45x | 243.64 | 11.59x |
| 10,000 | BulkSaveChanges | 1,000 |  |  |  |  | 191.98 | 14.71x |
| 10,000 | BulkSaveChanges | 5,000 |  |  |  |  | 174.77 | 16.16x |

### Mixed Add/Update/Delete

| Rows | Method | Batch Size | SQLite ms | SQLite speedup | PostgreSQL ms | PostgreSQL speedup | CockroachDB ms | CockroachDB speedup |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | SaveChanges |  | 0.07 | 1x | 0.97 | 1x | 1.87 | 1x |
| 1 | BulkSaveChanges | 100 | 0.09 | 0.80x | 1.19 | 0.81x | 2.78 | 0.67x |
| 100 | SaveChanges |  | 1.37 | 1x | 3.64 | 1x | 25.47 | 1x |
| 100 | BulkSaveChanges | 100 | 0.42 | 3.24x | 2.66 | 1.37x | 5.91 | 4.31x |
| 1,000 | SaveChanges |  | 8.73 | 1x | 24.77 | 1x | 197.97 | 1x |
| 1,000 | BulkSaveChanges | 100 | 4.49 | 1.95x | 10.94 | 2.26x | 22.74 | 8.71x |
| 1,000 | BulkSaveChanges | 250 | 6.52 | 1.34x | 8.39 | 2.95x | 20.17 | 9.82x |
| 1,000 | BulkSaveChanges | 1,000 |  |  |  |  | 18.64 | 10.62x |
| 1,000 | BulkSaveChanges | 5,000 |  |  |  |  | 17.85 | 11.09x |
| 10,000 | SaveChanges |  | 142.30 | 1x | 247.34 | 1x | 2,012.80 | 1x |
| 10,000 | BulkSaveChanges | 100 | 53.76 | 2.65x | 126.95 | 1.95x | 205.67 | 9.79x |
| 10,000 | BulkSaveChanges | 250 | 91.58 | 1.55x | 124.73 | 1.98x | 182.52 | 11.03x |
| 10,000 | BulkSaveChanges | 1,000 |  |  |  |  | 142.58 | 14.12x |
| 10,000 | BulkSaveChanges | 5,000 |  |  |  |  | 125.13 | 16.09x |

### Synchronize

| Rows | Method | Batch Size | SQLite ms | SQLite speedup | PostgreSQL ms | PostgreSQL speedup | CockroachDB ms | CockroachDB speedup |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | SaveChanges |  | 1.81 | 1x | 2.33 | 1x | 3.01 | 1x |
| 1 | BulkSynchronize | 100 | 4.57 | 0.40x | 6.14 | 0.38x | 7.05 | 0.43x |
| 100 | SaveChanges |  | 1.68 | 1x | 9.69 | 1x | 35.95 | 1x |
| 100 | BulkSynchronize | 100 | 2.88 | 0.58x | 6.45 | 1.50x | 11.74 | 3.06x |
| 1,000 | SaveChanges |  | 28.58 | 1x | 43.89 | 1x | 310.18 | 1x |
| 1,000 | BulkSynchronize | 100 | 20.76 | 1.38x | 31.38 | 1.40x | 55.82 | 5.56x |
| 1,000 | BulkSynchronize | 250 | 18.27 | 1.56x | 17.11 | 2.57x | 42.42 | 7.31x |
| 1,000 | BulkSynchronize | 1,000 |  |  |  |  | 34.80 | 8.91x |
| 1,000 | BulkSynchronize | 5,000 |  |  |  |  | 37.50 | 8.27x |
| 10,000 | SaveChanges |  | 228.00 | 1x | 414.70 | 1x | 3,125.55 | 1x |
| 10,000 | BulkSynchronize | 100 | 201.70 | 1.13x | 316.80 | 1.31x | 526.13 | 5.94x |
| 10,000 | BulkSynchronize | 250 | 208.41 | 1.09x | 219.20 | 1.89x | 397.32 | 7.87x |
| 10,000 | BulkSynchronize | 1,000 |  |  |  |  | 281.17 | 11.12x |
| 10,000 | BulkSynchronize | 5,000 |  |  |  |  | 272.56 | 11.47x |
