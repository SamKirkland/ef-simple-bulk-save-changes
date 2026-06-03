using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace EfSimpleBulkSaveChanges.Tests;

[TestClass]
public sealed class BulkSaveChangesIntegrationTests
{
    [TestMethod]
    public async Task BulkSaveChangesAsync_InsertsRowsAssignsGeneratedIdsAndAcceptsChanges()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        await using var db = fixture.CreateContext();
        var john = new User { FirstName = "John", LastName = "Doe", Email = "john.doe@example.com" };
        var jane = new User { FirstName = "Jane", LastName = "Smith", Email = null };
        db.Users.AddRange(john, jane);

        var savedCount = await db.BulkSaveChangesAsync();

        Assert.AreEqual(2, savedCount);
        Assert.AreNotEqual(0, john.Id);
        Assert.AreNotEqual(0, jane.Id);
        Assert.AreEqual(EntityState.Unchanged, db.Entry(john).State);
        Assert.AreEqual(EntityState.Unchanged, db.Entry(jane).State);

        var rows = await db.Users.AsNoTracking().OrderBy(user => user.Id).ToListAsync();
        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual("John", rows[0].FirstName);
        Assert.AreEqual("Doe", rows[0].LastName);
        Assert.AreEqual("john.doe@example.com", rows[0].Email);
        Assert.AreEqual("Jane", rows[1].FirstName);
        Assert.AreEqual("Smith", rows[1].LastName);
        Assert.IsNull(rows[1].Email);
    }

    [TestMethod]
    public async Task BulkSaveChangesAsync_UpdatesDifferentColumnsAndDeletesRowsInOneSave()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Users.AddRange(
                new User { FirstName = "John", LastName = "Doe", Email = "john.doe@example.com" },
                new User { FirstName = "Jane", LastName = "Smith", Email = "jane.smith@example.com" },
                new User { FirstName = "Alice", LastName = "Jones", Email = "alice.jones@example.com" });
            await seedDb.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var users = await db.Users.OrderBy(user => user.Id).ToListAsync();
        users[0].FirstName = "Johnny";
        users[1].Email = "jane.updated@example.com";
        db.Users.Remove(users[2]);

        var savedCount = await db.BulkSaveChangesAsync(batchSize: 2);

        Assert.AreEqual(3, savedCount);
        Assert.AreEqual(EntityState.Unchanged, db.Entry(users[0]).State);
        Assert.AreEqual(EntityState.Unchanged, db.Entry(users[1]).State);
        Assert.AreEqual(EntityState.Detached, db.Entry(users[2]).State);

        var rows = await db.Users.AsNoTracking().OrderBy(user => user.Id).ToListAsync();
        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual("Johnny", rows[0].FirstName);
        Assert.AreEqual("Doe", rows[0].LastName);
        Assert.AreEqual("john.doe@example.com", rows[0].Email);
        Assert.AreEqual("Jane", rows[1].FirstName);
        Assert.AreEqual("Smith", rows[1].LastName);
        Assert.AreEqual("jane.updated@example.com", rows[1].Email);
    }

    [TestMethod]
    public async Task BulkSaveChangesAsync_UsesExistingTransactionAndLeavesCommitOrRollbackToCaller()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Users.Add(new User { FirstName = "John", LastName = "Doe", Email = "john.doe@example.com" });

        await db.BulkSaveChangesAsync();
        await transaction.RollbackAsync();

        await using var readDb = fixture.CreateContext();
        Assert.AreEqual(0, await readDb.Users.CountAsync());
    }

    [TestMethod]
    public async Task BulkSaveChangesAsync_RollsBackOwnedTransactionWhenCommandFails()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.UniqueUsers.Add(new UniqueUser { Email = "duplicate@example.com" });
            await seedDb.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var inserted = new User { FirstName = "New", LastName = "User", Email = "new@example.com" };
        var duplicate = new UniqueUser { Email = "duplicate@example.com" };
        db.Users.Add(inserted);
        db.UniqueUsers.Add(duplicate);

        await Assert.ThrowsExceptionAsync<SqliteException>(() => db.BulkSaveChangesAsync());
        Assert.AreEqual(EntityState.Added, db.Entry(inserted).State);
        Assert.AreEqual(EntityState.Added, db.Entry(duplicate).State);

        await using var readDb = fixture.CreateContext();
        Assert.AreEqual(0, await readDb.Users.CountAsync());
        Assert.AreEqual(0, await readDb.Users.CountAsync(user => user.Email == "new@example.com"));
        Assert.AreEqual(1, await readDb.UniqueUsers.CountAsync());
    }

    [TestMethod]
    public async Task BulkSaveChangesAsync_OpensAndClosesConnectionWhenContextConnectionStartsClosed()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";

        try
        {
            await using (var setupDb = CreateFileContext(connectionString))
            {
                await setupDb.Database.EnsureCreatedAsync();
            }

            await using var db = CreateFileContext(connectionString);
            db.Users.Add(new User { FirstName = "John", LastName = "Doe", Email = "john.doe@example.com" });
            Assert.AreEqual(ConnectionState.Closed, db.Database.GetDbConnection().State);

            await db.BulkSaveChangesAsync();

            Assert.AreEqual(ConnectionState.Closed, db.Database.GetDbConnection().State);
            Assert.AreEqual(1, await db.Users.CountAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();

            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    private sealed class SqliteFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private SqliteFixture(string connectionString, SqliteConnection connection)
        {
            ConnectionString = connectionString;
            _connection = connection;
        }

        public string ConnectionString { get; }

        public static async Task<SqliteFixture> CreateAsync()
        {
            var connectionString = $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
            var fixture = new SqliteFixture(connectionString, new SqliteConnection(connectionString));
            await fixture._connection.OpenAsync();
            await using var db = fixture.CreateContext();
            await db.Database.EnsureCreatedAsync();
            return fixture;
        }

        public TestDbContext CreateContext()
        {
            return CreateContext(_connection);
        }

        public TestDbContext CreateContext(SqliteConnection connection)
        {
            var options = new DbContextOptionsBuilder<TestDbContext>()
                .UseSqlite(connection)
                .Options;

            return new TestDbContext(options);
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
        }
    }

    private static TestDbContext CreateFileContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(connectionString)
            .Options;

        return new TestDbContext(options);
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

    private sealed class User
    {
        public int Id { get; set; }

        public required string FirstName { get; set; }

        public required string LastName { get; set; }

        public string? Email { get; set; }
    }

    private sealed class UniqueUser
    {
        public int Id { get; set; }

        public required string Email { get; set; }
    }
}
