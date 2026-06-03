using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EfSimpleBulkSaveChanges.Tests;

[TestClass]
public sealed class BulkSynchronizeIntegrationTests
{
    [TestMethod]
    public async Task BulkSynchronizeAsync_InsertsRowsAndAssignsGeneratedIds()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        await using var db = fixture.CreateContext();
        var users = new[]
        {
            new User { FirstName = "John", LastName = "Doe", Email = "john.doe@example.com", TenantId = 1 },
            new User { FirstName = "Jane", LastName = "Smith", Email = null, TenantId = 1 }
        };

        var savedCount = await db.BulkSynchronizeAsync(users);

        Assert.AreEqual(2, savedCount);
        Assert.AreNotEqual(0, users[0].Id);
        Assert.AreNotEqual(0, users[1].Id);

        var rows = await db.Users.AsNoTracking().OrderBy(user => user.Id).ToListAsync();
        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual("John", rows[0].FirstName);
        Assert.AreEqual("Jane", rows[1].FirstName);
        Assert.IsNull(rows[1].Email);
    }

    [TestMethod]
    public async Task BulkSynchronizeAsync_BatchSizeFlushesAndDetachesProcessedEntries()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        await using var db = fixture.CreateContext();

        var savedCount = await db.BulkSynchronizeAsync(
            [
                new User { FirstName = "One", LastName = "A", Email = "one@example.com", TenantId = 1 },
                new User { FirstName = "Two", LastName = "B", Email = "two@example.com", TenantId = 1 },
                new User { FirstName = "Three", LastName = "C", Email = "three@example.com", TenantId = 1 }
            ],
            batchSize: 2);

        Assert.AreEqual(3, savedCount);
        Assert.AreEqual(0, db.ChangeTracker.Entries().Count());
        Assert.AreEqual(3, await db.Users.CountAsync());
    }

    [TestMethod]
    public async Task BulkSynchronizeAsync_UpdatesExistingRowsAndPreservesMissingRowsWhenDeleteDisabled()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        int johnId;
        await using (var seedDb = fixture.CreateContext())
        {
            var john = new User { FirstName = "John", LastName = "Doe", Email = "john.doe@example.com", TenantId = 1 };
            seedDb.Users.AddRange(
                john,
                new User { FirstName = "Extra", LastName = "User", Email = "extra@example.com", TenantId = 1 });
            await seedDb.SaveChangesAsync();
            johnId = john.Id;
        }

        await using var db = fixture.CreateContext();
        var savedCount = await db.BulkSynchronizeAsync([
            new User { Id = johnId, FirstName = "Johnny", LastName = "Doe", Email = "johnny@example.com", TenantId = 1 }
        ]);

        Assert.AreEqual(1, savedCount);
        var rows = await db.Users.AsNoTracking().OrderBy(user => user.Id).ToListAsync();
        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual("Johnny", rows[0].FirstName);
        Assert.AreEqual("johnny@example.com", rows[0].Email);
        Assert.AreEqual("Extra", rows[1].FirstName);
    }

    [TestMethod]
    public async Task BulkSynchronizeAsync_InsertsNonDefaultGeneratedKeyWhenRowDoesNotExist()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        await using var db = fixture.CreateContext();
        var user = new User { Id = 123, FirstName = "External", LastName = "User", Email = "external@example.com", TenantId = 1 };

        var savedCount = await db.BulkSynchronizeAsync([user]);

        Assert.AreEqual(1, savedCount);
        Assert.AreNotEqual(0, user.Id);
        var saved = await db.Users.SingleAsync();
        Assert.AreEqual("External", saved.FirstName);
    }

    [TestMethod]
    public async Task BulkSynchronizeAsync_DeletesMissingRowsWhenEnabled()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        int keepId;
        await using (var seedDb = fixture.CreateContext())
        {
            var keep = new User { FirstName = "Keep", LastName = "User", Email = "keep@example.com", TenantId = 1 };
            seedDb.Users.AddRange(
                keep,
                new User { FirstName = "Delete", LastName = "User", Email = "delete@example.com", TenantId = 1 });
            await seedDb.SaveChangesAsync();
            keepId = keep.Id;
        }

        await using var db = fixture.CreateContext();
        var savedCount = await db.BulkSynchronizeAsync(
            [new User { Id = keepId, FirstName = "Keep", LastName = "Updated", Email = "keep@example.com", TenantId = 1 }],
            options => options.DeleteMissing = true);

        Assert.AreEqual(2, savedCount);
        var rows = await db.Users.AsNoTracking().ToListAsync();
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("Updated", rows[0].LastName);
    }

    [TestMethod]
    public async Task BulkSynchronizeAsync_ScopedDeletePreservesRowsOutsideSourceScope()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        int tenantOneKeepId;
        await using (var seedDb = fixture.CreateContext())
        {
            var tenantOneKeep = new User { FirstName = "Tenant", LastName = "One", Email = "one@example.com", TenantId = 1 };
            seedDb.Users.AddRange(
                tenantOneKeep,
                new User { FirstName = "Tenant", LastName = "One Delete", Email = "delete@example.com", TenantId = 1 },
                new User { FirstName = "Tenant", LastName = "Two", Email = "two@example.com", TenantId = 2 });
            await seedDb.SaveChangesAsync();
            tenantOneKeepId = tenantOneKeep.Id;
        }

        await using var db = fixture.CreateContext();
        await db.BulkSynchronizeAsync(
            [new User { Id = tenantOneKeepId, FirstName = "Tenant", LastName = "One Updated", Email = "one@example.com", TenantId = 1 }],
            options =>
            {
                options.DeleteMissing = true;
                options.DeleteScopePropertyNames.Add(nameof(User.TenantId));
            });

        var rows = await db.Users.AsNoTracking().OrderBy(user => user.TenantId).ThenBy(user => user.Id).ToListAsync();
        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual(1, rows[0].TenantId);
        Assert.AreEqual("One Updated", rows[0].LastName);
        Assert.AreEqual(2, rows[1].TenantId);
        Assert.AreEqual("Two", rows[1].LastName);
    }

    [TestMethod]
    public async Task BulkSynchronizeAsync_ScopedDeleteRequiresConsistentScopeValues()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        await using var db = fixture.CreateContext();

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => db.BulkSynchronizeAsync(
            [
                new User { FirstName = "Tenant", LastName = "One", Email = "one@example.com", TenantId = 1 },
                new User { FirstName = "Tenant", LastName = "Two", Email = "two@example.com", TenantId = 2 }
            ],
            options =>
            {
                options.DeleteMissing = true;
                options.DeleteScopePropertyNames.Add(nameof(User.TenantId));
            }));
        Assert.AreEqual("Delete scope property 'TenantId' must have one consistent value across all source entities.", exception.Message);
    }

    [TestMethod]
    public async Task BulkSynchronizeAsync_EmptySourceDoesNothingEvenWhenDeleteEnabled()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Users.Add(new User { FirstName = "Keep", LastName = "User", Email = "keep@example.com", TenantId = 1 });
            await seedDb.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var savedCount = await db.BulkSynchronizeAsync(
            Array.Empty<User>(),
            options => options.DeleteMissing = true);

        Assert.AreEqual(0, savedCount);
        Assert.AreEqual(1, await db.Users.CountAsync());
    }

    [TestMethod]
    public async Task BulkSynchronizeAsync_RollsBackAllChunksWhenLaterChunkFails()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.UniqueUsers.Add(new UniqueUser { Email = "duplicate@example.com" });
            await seedDb.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();

        await Assert.ThrowsExceptionAsync<SqliteException>(() => db.BulkSynchronizeAsync(
            [
                new UniqueUser { Email = "first@example.com" },
                new UniqueUser { Email = "duplicate@example.com" }
            ],
            batchSize: 1));

        await using var readDb = fixture.CreateContext();
        CollectionAssert.AreEqual(
            new[] { "duplicate@example.com" },
            await readDb.UniqueUsers.Select(user => user.Email).OrderBy(email => email).ToArrayAsync());
    }

    [TestMethod]
    public async Task BulkSynchronizeAsync_InvalidBatchSizeThrows()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        await using var db = fixture.CreateContext();

        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(() =>
            db.BulkSynchronizeAsync(
                [new User { FirstName = "John", LastName = "Doe", Email = "john@example.com", TenantId = 1 }],
                batchSize: 0));
    }

    [TestMethod]
    public async Task BulkSynchronizeAsync_NonRelationalProviderThrows()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase($"sync-{Guid.NewGuid()}")
            .Options;

        await using var db = new TestDbContext(options);

        await Assert.ThrowsExceptionAsync<NotSupportedException>(() =>
            db.BulkSynchronizeAsync([
                new User { FirstName = "John", LastName = "Doe", Email = "john@example.com", TenantId = 1 }
            ]));
    }

    [TestMethod]
    public async Task BulkSynchronizeAsync_UnsupportedCompositeKeyThrows()
    {
        var options = new DbContextOptionsBuilder<CompositeKeyContext>()
            .UseSqlite(new SqliteConnection("Data Source=:memory:"))
            .Options;

        await using var db = new CompositeKeyContext(options);

        var exception = await Assert.ThrowsExceptionAsync<NotSupportedException>(() =>
            db.BulkSynchronizeAsync([
                new CompositeKeyEntity { TenantId = 1, Code = "A", Name = "Alpha" }
            ]));
        Assert.AreEqual("Entity type 'CompositeKeyEntity' must have a single-column primary key.", exception.Message);
    }

    [TestMethod]
    public async Task BulkSynchronizeAsync_UnsupportedConcurrencyTokenThrows()
    {
        var options = new DbContextOptionsBuilder<ConcurrencyTokenContext>()
            .UseSqlite(new SqliteConnection("Data Source=:memory:"))
            .Options;

        await using var db = new ConcurrencyTokenContext(options);

        var exception = await Assert.ThrowsExceptionAsync<NotSupportedException>(() =>
            db.BulkSynchronizeAsync([new VersionedUser { Name = "John", Version = 1 }]));
        Assert.AreEqual(
            "Entity type 'VersionedUser' has concurrency tokens. Concurrency tokens are not supported by BulkSynchronizeAsync.",
            exception.Message);
    }

    [TestMethod]
    public async Task BulkSynchronizeAsync_UnsupportedOwnedTypeThrows()
    {
        var options = new DbContextOptionsBuilder<OwnedTypeContext>()
            .UseSqlite(new SqliteConnection("Data Source=:memory:"))
            .Options;

        await using var db = new OwnedTypeContext(options);

        var exception = await Assert.ThrowsExceptionAsync<NotSupportedException>(() =>
            db.BulkSynchronizeAsync([new Address { City = "Austin" }]));
        Assert.AreEqual(
            "Entity type 'Address' is owned. Owned entity types are not supported by BulkSynchronizeAsync.",
            exception.Message);
    }

    [TestMethod]
    public async Task BulkSynchronizeAsync_UnsupportedInheritanceThrows()
    {
        var options = new DbContextOptionsBuilder<InheritanceContext>()
            .UseSqlite(new SqliteConnection("Data Source=:memory:"))
            .Options;

        await using var db = new InheritanceContext(options);

        var exception = await Assert.ThrowsExceptionAsync<NotSupportedException>(() =>
            db.BulkSynchronizeAsync([new Employee { Name = "John", Department = "Engineering" }]));
        Assert.AreEqual(
            "Entity type 'Employee' uses inheritance. Inheritance mappings are not supported by BulkSynchronizeAsync.",
            exception.Message);
    }

    [TestMethod]
    public async Task BulkSynchronizeAsync_UnsupportedShadowKeyThrows()
    {
        var options = new DbContextOptionsBuilder<ShadowKeyContext>()
            .UseSqlite(new SqliteConnection("Data Source=:memory:"))
            .Options;

        await using var db = new ShadowKeyContext(options);

        var exception = await Assert.ThrowsExceptionAsync<NotSupportedException>(() =>
            db.BulkSynchronizeAsync([new ShadowKeyUser { Name = "John" }]));
        Assert.AreEqual(
            "Entity type 'ShadowKeyUser' uses a shadow primary key. Shadow keys are not supported by BulkSynchronizeAsync.",
            exception.Message);
    }

    [TestMethod]
    public async Task BulkSynchronizeAsync_UnsupportedTablelessMappingThrows()
    {
        var options = new DbContextOptionsBuilder<ViewContext>()
            .UseSqlite(new SqliteConnection("Data Source=:memory:"))
            .Options;

        await using var db = new ViewContext(options);

        var exception = await Assert.ThrowsExceptionAsync<NotSupportedException>(() =>
            db.BulkSynchronizeAsync([new ViewRow { Id = 1, Name = "John" }]));
        Assert.AreEqual("Entity type 'ViewRow' is not mapped to a table.", exception.Message);
    }

    [TestMethod]
    public void BulkSynchronizeKeyLookupSql_IsSimplePrimaryKeyInPredicate()
    {
        using var db = CreatePostgresContext();
        var query = BulkSynchronizeQueryFactory.CreateKeyLookupQuery(
            db,
            BulkSynchronizeEntityMapping.Create(db, typeof(User)),
            new object[] { 1, 2, 3 });

        var sql = query.ToQueryString();

        Assert.AreEqual(
            NormalizeSql(
                """
                SELECT u.id, u.email, u.first_name, u.last_name, u.tenant_id
                FROM users AS u
                WHERE u.id IN (1, 2, 3)
                """),
            NormalizeSql(sql));
        Assert.IsFalse(sql.Contains("MERGE", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(sql.Contains("WITH", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeSql(string sql)
    {
        return sql.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }

    private static TestDbContext CreatePostgresContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql("Host=localhost;Database=bulk")
            .Options;

        return new TestDbContext(options);
    }

    private sealed class SqliteFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private SqliteFixture(SqliteConnection connection)
        {
            _connection = connection;
        }

        public static async Task<SqliteFixture> CreateAsync()
        {
            var connection = new SqliteConnection($"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared");
            var fixture = new SqliteFixture(connection);
            await connection.OpenAsync();
            await using var db = fixture.CreateContext();
            await db.Database.EnsureCreatedAsync();
            return fixture;
        }

        public TestDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<TestDbContext>()
                .UseSqlite(_connection)
                .Options;

            return new TestDbContext(options);
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
        }
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<User> Users => Set<User>();

        public DbSet<UniqueUser> UniqueUsers => Set<UniqueUser>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<User>(entity =>
            {
                entity.ToTable("users");
                entity.HasKey(user => user.Id);
                entity.Property(user => user.Id).HasColumnName("id").ValueGeneratedOnAdd();
                entity.Property(user => user.FirstName).HasColumnName("first_name");
                entity.Property(user => user.LastName).HasColumnName("last_name");
                entity.Property(user => user.Email).HasColumnName("email");
                entity.Property(user => user.TenantId).HasColumnName("tenant_id");
            });

            modelBuilder.Entity<UniqueUser>(entity =>
            {
                entity.ToTable("unique_users");
                entity.HasKey(user => user.Id);
                entity.Property(user => user.Id).HasColumnName("id").ValueGeneratedOnAdd();
                entity.Property(user => user.Email).HasColumnName("email");
                entity.HasIndex(user => user.Email).IsUnique();
            });
        }
    }

    private sealed class CompositeKeyContext(DbContextOptions<CompositeKeyContext> options) : DbContext(options)
    {
        public DbSet<CompositeKeyEntity> CompositeKeyEntities => Set<CompositeKeyEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CompositeKeyEntity>(entity =>
            {
                entity.ToTable("composite_key_entities");
                entity.HasKey(item => new { item.TenantId, item.Code });
            });
        }
    }

    private sealed class ConcurrencyTokenContext(DbContextOptions<ConcurrencyTokenContext> options) : DbContext(options)
    {
        public DbSet<VersionedUser> Users => Set<VersionedUser>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<VersionedUser>(entity =>
            {
                entity.ToTable("versioned_users");
                entity.HasKey(user => user.Id);
                entity.Property(user => user.Version).IsConcurrencyToken();
            });
        }
    }

    private sealed class OwnedTypeContext(DbContextOptions<OwnedTypeContext> options) : DbContext(options)
    {
        public DbSet<UserWithAddress> Users => Set<UserWithAddress>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<UserWithAddress>(entity =>
            {
                entity.ToTable("users");
                entity.HasKey(user => user.Id);
                entity.OwnsOne(user => user.Address);
            });
        }
    }

    private sealed class InheritanceContext(DbContextOptions<InheritanceContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Person>(entity =>
            {
                entity.ToTable("people");
                entity.HasKey(person => person.Id);
            });

            modelBuilder.Entity<Employee>();
        }
    }

    private sealed class ShadowKeyContext(DbContextOptions<ShadowKeyContext> options) : DbContext(options)
    {
        public DbSet<ShadowKeyUser> Users => Set<ShadowKeyUser>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ShadowKeyUser>(entity =>
            {
                entity.ToTable("shadow_key_users");
                entity.Property<int>("Id");
                entity.HasKey("Id");
                entity.Property(user => user.Name).HasColumnName("name");
            });
        }
    }

    private sealed class ViewContext(DbContextOptions<ViewContext> options) : DbContext(options)
    {
        public DbSet<ViewRow> ViewRows => Set<ViewRow>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ViewRow>(entity =>
            {
                entity.HasKey(row => row.Id);
                entity.ToView("view_rows");
            });
        }
    }

    private sealed class User
    {
        public int Id { get; set; }

        public required string FirstName { get; set; }

        public required string LastName { get; set; }

        public string? Email { get; set; }

        public int TenantId { get; set; }
    }

    private sealed class UniqueUser
    {
        public int Id { get; set; }

        public required string Email { get; set; }
    }

    private sealed class CompositeKeyEntity
    {
        public int TenantId { get; set; }

        public required string Code { get; set; }

        public required string Name { get; set; }
    }

    private sealed class VersionedUser
    {
        public int Id { get; set; }

        public required string Name { get; set; }

        public int Version { get; set; }
    }

    private sealed class UserWithAddress
    {
        public int Id { get; set; }

        public required string Name { get; set; }

        public required Address Address { get; set; }
    }

    private sealed class Address
    {
        public required string City { get; set; }
    }

    private class Person
    {
        public int Id { get; set; }

        public required string Name { get; set; }
    }

    private sealed class Employee : Person
    {
        public required string Department { get; set; }
    }

    private sealed class ShadowKeyUser
    {
        public required string Name { get; set; }
    }

    private sealed class ViewRow
    {
        public int Id { get; set; }

        public required string Name { get; set; }
    }
}
