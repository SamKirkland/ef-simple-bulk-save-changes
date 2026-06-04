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
        WriteHeaders();

        foreach (var rowCount in RowCounts)
        {
            var saveChangesMeasurement = await MeasureSaveChangesAsync(
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
                    saveChangesMeasurement.SaveMilliseconds,
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
        WriteHeaders();

        foreach (var rowCount in RowCounts)
        {
            var saveChangesMeasurement = await MeasureSaveChangesAsync(
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
                    saveChangesMeasurement.SaveMilliseconds,
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
        WriteHeaders();

        foreach (var rowCount in RowCounts)
        {
            var saveChangesMeasurement = await MeasureSaveChangesAsync(
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
                    saveChangesMeasurement.SaveMilliseconds,
                    async db =>
                    {
                        await SeedUsersAsync(db, rowCount, $"bulk-mixed-{batchSize}");
                        return await StageMixedChangesAsync(db, rowCount, $"bulk-mixed-{batchSize}");
                    });
            }
        }
    }

    [TestMethod]
    public async Task Synchronize_PerformanceAcrossSaveChangesAndBulkBatchSizes()
    {
        WriteHeaders();

        foreach (var rowCount in RowCounts)
        {
            var saveChangesMeasurement = await MeasureSaveChangesAsync(
                "synchronize",
                rowCount,
                async db =>
                {
                    await SeedUsersAsync(db, rowCount, "save-sync");
                    return await StageManualSynchronizeAsync(db, rowCount, "save-sync");
                });

            foreach (var batchSize in GetChangeBatchSizes(rowCount))
            {
                IReadOnlyList<User> sourceUsers = [];

                await MeasureAsync(
                    "synchronize",
                    "BulkSynchronize",
                    rowCount,
                    batchSize,
                    saveChangesMeasurement.SaveMilliseconds,
                    async db =>
                    {
                        await SeedUsersAsync(db, rowCount, $"bulk-sync-{batchSize}");
                        sourceUsers = await CreateSynchronizeSourceAsync(db, rowCount, $"bulk-sync-{batchSize}");
                        return GetSynchronizeSavedCount(rowCount);
                    },
                    db => db.BulkSynchronizeAsync(
                        sourceUsers,
                        options =>
                        {
                            options.BatchSize = batchSize;
                            options.DeleteMissing = true;
                        }));
            }
        }
    }

    private void WriteHeaders()
    {
        TestContext.WriteLine("Scenario, Method, Rows, BatchSize, SaveElapsedMs, SpeedupVsSaveChanges");
        TestContext.WriteLine("VERBOSE Scenario, Method, Rows, BatchSize, FixtureMs, ArrangeMs, SaveMs, VerifyMs, TotalMs, ExpectedSaved, ActualSaved");
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

    private async Task<PerformanceMeasurement> MeasureSaveChangesAsync(
        string scenario,
        int rowCount,
        Func<PerformanceDbContext, Task<int>> arrangeAsync)
    {
        return await MeasureAsync(scenario, "SaveChanges", rowCount, null, null, arrangeAsync, db => db.SaveChangesAsync());
    }

    private async Task<PerformanceMeasurement> MeasureBulkSaveChangesAsync(
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

    private async Task<PerformanceMeasurement> MeasureAsync(
        string scenario,
        string method,
        int rowCount,
        int? batchSize,
        double? saveChangesElapsed,
        Func<PerformanceDbContext, Task<int>> arrangeAsync,
        Func<PerformanceDbContext, Task<int>> saveAsync)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var fixtureStopwatch = Stopwatch.StartNew();
        await using var fixture = await SqliteFixture.CreateAsync();
        await using var db = fixture.CreateContext();
        fixtureStopwatch.Stop();

        var arrangeStopwatch = Stopwatch.StartNew();
        var expectedSavedCount = await arrangeAsync(db);
        arrangeStopwatch.Stop();

        var saveStopwatch = Stopwatch.StartNew();
        var savedCount = await saveAsync(db);
        saveStopwatch.Stop();

        var verifyStopwatch = Stopwatch.StartNew();
        Assert.AreEqual(expectedSavedCount, savedCount);
        Assert.AreEqual(rowCount, await db.Users.CountAsync());
        verifyStopwatch.Stop();
        totalStopwatch.Stop();

        var speedup = saveChangesElapsed is null
            ? "1x"
            : $"{saveChangesElapsed.Value / saveStopwatch.Elapsed.TotalMilliseconds:F2}x";

        TestContext.WriteLine(
            $"{scenario}, {method}, {rowCount}, {batchSize?.ToString() ?? "n/a"}, {saveStopwatch.Elapsed.TotalMilliseconds:F2}, {speedup}");
        TestContext.WriteLine(
            $"VERBOSE {scenario}, {method}, {rowCount}, {batchSize?.ToString() ?? "n/a"}, {fixtureStopwatch.Elapsed.TotalMilliseconds:F2}, {arrangeStopwatch.Elapsed.TotalMilliseconds:F2}, {saveStopwatch.Elapsed.TotalMilliseconds:F2}, {verifyStopwatch.Elapsed.TotalMilliseconds:F2}, {totalStopwatch.Elapsed.TotalMilliseconds:F2}, {expectedSavedCount}, {savedCount}");

        return new PerformanceMeasurement(
            fixtureStopwatch.Elapsed.TotalMilliseconds,
            arrangeStopwatch.Elapsed.TotalMilliseconds,
            saveStopwatch.Elapsed.TotalMilliseconds,
            verifyStopwatch.Elapsed.TotalMilliseconds,
            totalStopwatch.Elapsed.TotalMilliseconds);
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

    private static async Task<int> StageManualSynchronizeAsync(PerformanceDbContext db, int rowCount, string prefix)
    {
        var users = await db.Users.OrderBy(user => user.Id).ToListAsync();
        var updateCount = GetSynchronizeUpdateCount(rowCount);

        for (var index = 0; index < users.Count; index++)
        {
            if (index < updateCount)
            {
                users[index].FirstName = $"{prefix}-source-first-{index + 1}";
                users[index].LastName = $"{prefix}-source-last-{index + 1}";
                users[index].Email = $"{prefix}-source-{index + 1}@example.com";
                continue;
            }

            db.Users.Remove(users[index]);
        }

        db.Users.AddRange(CreateUsers(rowCount - updateCount, $"{prefix}-source-added"));

        return GetSynchronizeSavedCount(rowCount);
    }

    private static async Task<IReadOnlyList<User>> CreateSynchronizeSourceAsync(
        PerformanceDbContext db,
        int rowCount,
        string prefix)
    {
        var existingUsers = await db.Users.AsNoTracking().OrderBy(user => user.Id).ToListAsync();
        var updateCount = GetSynchronizeUpdateCount(rowCount);
        var sourceUsers = new List<User>(rowCount);

        for (var index = 0; index < updateCount; index++)
        {
            sourceUsers.Add(new User
            {
                Id = existingUsers[index].Id,
                FirstName = $"{prefix}-source-first-{index + 1}",
                LastName = $"{prefix}-source-last-{index + 1}",
                Email = $"{prefix}-source-{index + 1}@example.com"
            });
        }

        sourceUsers.AddRange(CreateUsers(rowCount - updateCount, $"{prefix}-source-added"));

        return sourceUsers;
    }

    private static int GetSynchronizeUpdateCount(int rowCount)
    {
        return (rowCount + 1) / 2;
    }

    private static int GetSynchronizeSavedCount(int rowCount)
    {
        var updateCount = GetSynchronizeUpdateCount(rowCount);
        var replaceCount = rowCount - updateCount;
        return updateCount + (replaceCount * 2);
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

    private sealed record PerformanceMeasurement(
        double FixtureMilliseconds,
        double ArrangeMilliseconds,
        double SaveMilliseconds,
        double VerifyMilliseconds,
        double TotalMilliseconds);

    private sealed class User
    {
        public int Id { get; set; }

        public required string FirstName { get; set; }

        public required string LastName { get; set; }

        public string? Email { get; set; }
    }
}
