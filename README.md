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

## Support

Supported:

- Relational EF Core providers
- Single-column non-shadow primary keys
- Normal scalar mapped properties
- Tracked `Added`, `Modified`, and `Deleted` entries
- Database-generated keys on insert via `RETURNING`

Not supported:

- Non-relational providers
- Composite keys
- Owned entity types
- Inheritance mappings
- Concurrency tokens
- Shadow primary keys
- Entities not mapped to a table

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
CockroachDB: Docker v26.3.1 single-node cluster

Timing are medidan of 3 runs

### Insert

| Rows | Method | Batch Size | SQLite ms | SQLite speedup | PostgreSQL ms | PostgreSQL speedup | CockroachDB ms | CockroachDB speedup |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | SaveChanges |  | 0.15 | 1x | 1.54 | 1x | 4.29 | 1x |
| 1 | BulkSaveChanges | 100 | 0.17 | 0.85x | 2.38 | 0.65x | 5.18 | 0.83x |
| 100 | SaveChanges |  | 1.81 | 1x | 4.47 | 1x | 28.29 | 1x |
| 100 | BulkSaveChanges | 100 | 0.75 | 2.41x | 2.52 | 1.78x | 5.28 | 5.36x |
| 1,000 | SaveChanges |  | 19.37 | 1x | 25.57 | 1x | 170.27 | 1x |
| 1,000 | BulkSaveChanges | 100 | 6.52 | 2.97x | 11.03 | 2.32x | 25.19 | 6.76x |
| 1,000 | BulkSaveChanges | 1,000 | 20.64 | 0.94x | 10.97 | 2.33x | 15.37 | 11.08x |
| 10,000 | SaveChanges |  | 199.88 | 1x | 287.07 | 1x | 1,634.34 | 1x |
| 10,000 | BulkSaveChanges | 100 | 144.46 | 1.38x | 107.85 | 2.66x | 200.55 | 8.15x |
| 10,000 | BulkSaveChanges | 1,000 | 217.28 | 0.92x | 74.39 | 3.86x | 146.53 | 11.15x |
| 10,000 | BulkSaveChanges | 10,000 | 1,739.02 | 0.11x | 63.35 | 4.53x | 117.27 | 13.94x |

### Update

| Rows | Method | Batch Size | SQLite ms | SQLite speedup | PostgreSQL ms | PostgreSQL speedup | CockroachDB ms | CockroachDB speedup |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | SaveChanges |  | 0.93 | 1x | 1.18 | 1x | 2.54 | 1x |
| 1 | BulkSaveChanges | 100 | 0.16 | 5.76x | 1.38 | 0.85x | 2.83 | 0.90x |
| 100 | SaveChanges |  | 1.44 | 1x | 3.72 | 1x | 31.35 | 1x |
| 100 | BulkSaveChanges | 100 | 0.98 | 1.47x | 8.42 | 0.44x | 4.28 | 7.33x |
| 1,000 | SaveChanges |  | 12.52 | 1x | 26.92 | 1x | 280.29 | 1x |
| 1,000 | BulkSaveChanges | 100 | 7.48 | 1.67x | 12.86 | 2.09x | 24.86 | 11.27x |
| 1,000 | BulkSaveChanges | 250 | 11.95 | 1.05x | 9.76 | 2.76x | 24.76 | 11.32x |
| 1,000 | BulkSaveChanges | 1,000 |  |  |  |  | 18.44 | 15.20x |
| 1,000 | BulkSaveChanges | 5,000 |  |  |  |  | 18.31 | 15.31x |
| 10,000 | SaveChanges |  | 179.13 | 1x | 302.04 | 1x | 2,869.10 | 1x |
| 10,000 | BulkSaveChanges | 100 | 106.41 | 1.68x | 183.07 | 1.65x | 263.74 | 10.88x |
| 10,000 | BulkSaveChanges | 250 | 146.04 | 1.23x | 121.89 | 2.48x | 236.77 | 12.12x |
| 10,000 | BulkSaveChanges | 1,000 |  |  |  |  | 188.19 | 15.25x |
| 10,000 | BulkSaveChanges | 5,000 |  |  |  |  | 178.40 | 16.08x |

### Mixed Add/Update/Delete

| Rows | Method | Batch Size | SQLite ms | SQLite speedup | PostgreSQL ms | PostgreSQL speedup | CockroachDB ms | CockroachDB speedup |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | SaveChanges |  | 0.07 | 1x | 1.01 | 1x | 2.02 | 1x |
| 1 | BulkSaveChanges | 100 | 0.11 | 0.61x | 1.63 | 0.62x | 2.87 | 0.70x |
| 100 | SaveChanges |  | 1.40 | 1x | 3.93 | 1x | 23.54 | 1x |
| 100 | BulkSaveChanges | 100 | 0.55 | 2.53x | 3.02 | 1.30x | 5.50 | 4.28x |
| 1,000 | SaveChanges |  | 9.85 | 1x | 27.58 | 1x | 200.75 | 1x |
| 1,000 | BulkSaveChanges | 100 | 4.80 | 2.05x | 11.15 | 2.47x | 23.90 | 8.40x |
| 1,000 | BulkSaveChanges | 250 | 6.70 | 1.47x | 15.45 | 1.79x | 20.71 | 9.69x |
| 1,000 | BulkSaveChanges | 1,000 |  |  |  |  | 19.14 | 10.49x |
| 1,000 | BulkSaveChanges | 5,000 |  |  |  |  | 19.05 | 10.54x |
| 10,000 | SaveChanges |  | 146.95 | 1x | 253.94 | 1x | 2,056.52 | 1x |
| 10,000 | BulkSaveChanges | 100 | 64.04 | 2.29x | 129.02 | 1.97x | 219.49 | 9.37x |
| 10,000 | BulkSaveChanges | 250 | 83.14 | 1.77x | 121.65 | 2.09x | 179.82 | 11.44x |
| 10,000 | BulkSaveChanges | 1,000 |  |  |  |  | 149.32 | 13.77x |
| 10,000 | BulkSaveChanges | 5,000 |  |  |  |  | 133.95 | 15.35x |

