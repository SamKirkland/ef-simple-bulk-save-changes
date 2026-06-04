using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EfSimpleBulkSaveChanges.Tests;

[TestClass]
[TestCategory("Performance")]
[DoNotParallelize]
public sealed class BulkSaveChangesPerformanceTests
{
    private static readonly int[] RowCounts = [1, 100, 1_000, 10_000];

    // SQLite rejects larger UNION ALL update batches with "too many terms in compound SELECT".
    private static readonly int[] LargeChangeBulkBatchSizes = [100, 250];

    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static async Task WarmUpAsync(TestContext _)
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        await using var db = fixture.CreateContext();

        db.Users.AddRange(CreateUsers(1, "warmup-save"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        db.Users.AddRange(CreateUsers(1, "warmup-bulk"));
        await db.BulkSaveChangesAsync(batchSize: 100);
    }

    [TestMethod]
    public async Task Inserts_PerformanceAcrossSaveChangesAndBulkBatchSizes()
    {
        TestContext.WriteLine("Scenario, Method, Rows, BatchSize, ElapsedMs, SpeedupVsSaveChanges");

        foreach (var rowCount in RowCounts)
        {
            var saveChangesElapsed = await MeasureSaveChangesAsync(
                "insert",
                rowCount,
                db =>
                {
                    db.Users.AddRange(CreateUsers(rowCount, "save-insert"));
                    return Task.FromResult(rowCount);
                });

            foreach (var batchSize in GetInsertBatchSizes(rowCount))
            {
                await MeasureBulkSaveChangesAsync(
                    "insert",
                    rowCount,
                    batchSize,
                    saveChangesElapsed,
                    db =>
                    {
                        db.Users.AddRange(CreateUsers(rowCount, $"bulk-insert-{batchSize}"));
                        return Task.FromResult(rowCount);
                    });
            }
        }
    }

    [TestMethod]
    public async Task Updates_PerformanceAcrossSaveChangesAndBulkBatchSizes()
    {
        TestContext.WriteLine("Scenario, Method, Rows, BatchSize, ElapsedMs, SpeedupVsSaveChanges");

        foreach (var rowCount in RowCounts)
        {
            var saveChangesElapsed = await MeasureSaveChangesAsync(
                "update",
                rowCount,
                async db =>
                {
                    await SeedUsersAsync(db, rowCount, "save-update");
                    var users = await db.Users.OrderBy(user => user.Id).ToListAsync();
                    UpdateUsers(users);
                    return rowCount;
                });

            foreach (var batchSize in GetChangeBatchSizes(rowCount))
            {
                await MeasureBulkSaveChangesAsync(
                    "update",
                    rowCount,
                    batchSize,
                    saveChangesElapsed,
                    async db =>
                    {
                        await SeedUsersAsync(db, rowCount, $"bulk-update-{batchSize}");
                        var users = await db.Users.OrderBy(user => user.Id).ToListAsync();
                        UpdateUsers(users);
                        return rowCount;
                    });
            }
        }
    }

    [TestMethod]
    public async Task MixedAddsUpdatesDeletes_PerformanceAcrossSaveChangesAndBulkBatchSizes()
    {
        TestContext.WriteLine("Scenario, Method, Rows, BatchSize, ElapsedMs, SpeedupVsSaveChanges");

        foreach (var rowCount in RowCounts)
        {
            var saveChangesElapsed = await MeasureSaveChangesAsync(
                "mixed",
                rowCount,
                async db =>
                    {
                        await SeedUsersAsync(db, rowCount, "save-mixed");
                        return await StageMixedChangesAsync(db, rowCount, "save-mixed");
                    });

            foreach (var batchSize in GetChangeBatchSizes(rowCount))
            {
                await MeasureBulkSaveChangesAsync(
                    "mixed",
                    rowCount,
                    batchSize,
                    saveChangesElapsed,
                    async db =>
                    {
                        await SeedUsersAsync(db, rowCount, $"bulk-mixed-{batchSize}");
                        return await StageMixedChangesAsync(db, rowCount, $"bulk-mixed-{batchSize}");
                    });
            }
        }
    }

    private static IEnumerable<int> GetInsertBatchSizes(int rowCount)
    {
        if (rowCount == 1)
        {
            yield return 100;
            yield break;
        }

        yield return 100;

        if (rowCount >= 1_000)
        {
            yield return 1_000;
        }

        if (rowCount >= 10_000)
        {
            yield return 10_000;
        }
    }

    private static IEnumerable<int> GetChangeBatchSizes(int rowCount)
    {
        if (rowCount <= 100)
        {
            yield return 100;
            yield break;
        }

        foreach (var batchSize in LargeChangeBulkBatchSizes)
        {
            yield return batchSize;
        }
    }

    private async Task<double> MeasureSaveChangesAsync(
        string scenario,
        int rowCount,
        Func<PerformanceDbContext, Task<int>> arrangeAsync)
    {
        return await MeasureAsync(scenario, "SaveChanges", rowCount, null, null, arrangeAsync, db => db.SaveChangesAsync());
    }

    private async Task<double> MeasureBulkSaveChangesAsync(
        string scenario,
        int rowCount,
        int batchSize,
        double saveChangesElapsed,
        Func<PerformanceDbContext, Task<int>> arrangeAsync)
    {
        return await MeasureAsync(
            scenario,
            "BulkSaveChanges",
            rowCount,
            batchSize,
            saveChangesElapsed,
            arrangeAsync,
            db => db.BulkSaveChangesAsync(batchSize));
    }

    private async Task<double> MeasureAsync(
        string scenario,
        string method,
        int rowCount,
        int? batchSize,
        double? saveChangesElapsed,
        Func<PerformanceDbContext, Task<int>> arrangeAsync,
        Func<PerformanceDbContext, Task<int>> saveAsync)
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        await using var db = fixture.CreateContext();

        var expectedSavedCount = await arrangeAsync(db);

        var stopwatch = Stopwatch.StartNew();
        var savedCount = await saveAsync(db);
        stopwatch.Stop();

        Assert.AreEqual(expectedSavedCount, savedCount);
        Assert.AreEqual(rowCount, await db.Users.CountAsync());

        var elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        var speedup = saveChangesElapsed is null
            ? "1x"
            : $"{saveChangesElapsed.Value / elapsedMilliseconds:F2}x";

        TestContext.WriteLine(
            $"{scenario}, {method}, {rowCount}, {batchSize?.ToString() ?? "n/a"}, {elapsedMilliseconds:F2}, {speedup}");
        return elapsedMilliseconds;
    }

    private static IReadOnlyList<User> CreateUsers(int count, string prefix)
    {
        return Enumerable.Range(1, count)
            .Select(index => new User
            {
                FirstName = $"{prefix}-first-{index}",
                LastName = $"{prefix}-last-{index}",
                Email = $"{prefix}-{index}@example.com"
            })
            .ToArray();
    }

    private static async Task SeedUsersAsync(PerformanceDbContext db, int count, string prefix)
    {
        db.Users.AddRange(CreateUsers(count, prefix));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static void UpdateUsers(IEnumerable<User> users)
    {
        foreach (var user in users)
        {
            user.FirstName = $"{user.FirstName}-updated";
            user.Email = $"updated-{user.Id}@example.com";
        }
    }

    private static async Task<int> StageMixedChangesAsync(PerformanceDbContext db, int rowCount, string prefix)
    {
        var users = await db.Users.OrderBy(user => user.Id).ToListAsync();
        var updateCount = 0;
        var deleteCount = 0;

        for (var index = 0; index < users.Count; index++)
        {
            if (index % 3 == 0)
            {
                users[index].LastName = $"{users[index].LastName}-mixed";
                updateCount++;
                continue;
            }

            if (index % 3 == 1)
            {
                db.Users.Remove(users[index]);
                deleteCount++;
            }
        }

        var addCount = deleteCount;
        db.Users.AddRange(CreateUsers(addCount, $"{prefix}-added"));

        return updateCount + deleteCount + addCount;
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

        public PerformanceDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<PerformanceDbContext>()
                .UseSqlite(_connection)
                .Options;

            return new PerformanceDbContext(options);
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
        }
    }

    private sealed class PerformanceDbContext(DbContextOptions<PerformanceDbContext> options) : DbContext(options)
    {
        public DbSet<User> Users => Set<User>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<User>(entity =>
            {
                entity.ToTable("performance_users");
                entity.HasKey(user => user.Id);
                entity.Property(user => user.Id).HasColumnName("id").ValueGeneratedOnAdd();
                entity.Property(user => user.FirstName).HasColumnName("first_name");
                entity.Property(user => user.LastName).HasColumnName("last_name");
                entity.Property(user => user.Email).HasColumnName("email");
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
}
