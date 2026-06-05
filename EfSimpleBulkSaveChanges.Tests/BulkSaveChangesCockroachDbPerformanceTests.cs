using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EfSimpleBulkSaveChanges.Tests;

[TestClass]
[TestCategory("Performance")]
[TestCategory("CockroachDB")]
[TestCategory("CockroachDBSingleNode")]
[DoNotParallelize]
public sealed class BulkSaveChangesCockroachDbPerformanceTests
{
    private const int MeasurementIterations = 3;
    private static readonly int[] RowCounts = [1, 100, 1_000, 10_000];
    private static readonly int[] LargeChangeBulkBatchSizes = [100, 250, 1_000, 5_000];
    private static CockroachDbServer? Server;

    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static async Task StartCockroachDbAsync(TestContext _)
    {
        try
        {
            Server = await CockroachDbServer.StartAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            Assert.Inconclusive($"CockroachDB performance tests require a running Docker engine. {ex.Message}");
            throw;
        }

        await using var fixture = await CockroachDbFixture.CreateAsync(Server.ConnectionString);
        await using var db = fixture.CreateContext();

        db.Users.AddRange(CreateUsers(1, "warmup-save"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        db.Users.AddRange(CreateUsers(1, "warmup-bulk"));
        await db.BulkSaveChangesAsync(batchSize: 100);
    }

    [ClassCleanup]
    public static async Task StopCockroachDbAsync()
    {
        if (Server is not null)
        {
            await Server.DisposeAsync();
        }

        PerformanceReadmeUpdater.RegenerateChart();
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

    private void WriteHeaders()
    {
        TestContext.WriteLine("Database, Scenario, Method, Rows, BatchSize, SaveElapsedMs, SpeedupVsSaveChanges");
        TestContext.WriteLine("VERBOSE Database, Scenario, Method, Rows, BatchSize, Iteration, FixtureMs, ArrangeMs, SaveMs, VerifyMs, TotalMs, ExpectedSaved, ActualSaved");
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

        if (rowCount >= 100_000)
        {
            yield return 10_000;
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
        if (Server is null)
        {
            Assert.Inconclusive("CockroachDB server was not initialized.");
            throw new UnreachableException();
        }

        var measurements = new List<PerformanceIterationMeasurement>(MeasurementIterations);
        for (var iteration = 1; iteration <= MeasurementIterations; iteration++)
        {
            var totalStopwatch = Stopwatch.StartNew();
            var fixtureStopwatch = Stopwatch.StartNew();
            await using var fixture = await CockroachDbFixture.CreateAsync(Server.ConnectionString);
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

            var measurement = new PerformanceIterationMeasurement(
                fixtureStopwatch.Elapsed.TotalMilliseconds,
                arrangeStopwatch.Elapsed.TotalMilliseconds,
                saveStopwatch.Elapsed.TotalMilliseconds,
                verifyStopwatch.Elapsed.TotalMilliseconds,
                totalStopwatch.Elapsed.TotalMilliseconds,
                expectedSavedCount,
                savedCount);
            measurements.Add(measurement);

            TestContext.WriteLine(
                $"VERBOSE CockroachDB, {scenario}, {method}, {rowCount}, {batchSize?.ToString() ?? "n/a"}, {iteration}, {measurement.FixtureMilliseconds:F2}, {measurement.ArrangeMilliseconds:F2}, {measurement.SaveMilliseconds:F2}, {measurement.VerifyMilliseconds:F2}, {measurement.TotalMilliseconds:F2}, {measurement.ExpectedSavedCount}, {measurement.SavedCount}");
        }

        var medianSaveMilliseconds = GetMedian(measurements.Select(measurement => measurement.SaveMilliseconds));

        var speedup = saveChangesElapsed is null
            ? "1x"
            : $"{saveChangesElapsed.Value / medianSaveMilliseconds:F2}x";

        TestContext.WriteLine(
            $"CockroachDB, {scenario}, {method}, {rowCount}, {batchSize?.ToString() ?? "n/a"}, {medianSaveMilliseconds:F2}, {speedup}");
        PerformanceReadmeUpdater.Record(
            "CockroachDB",
            scenario,
            method,
            rowCount,
            batchSize,
            medianSaveMilliseconds,
            speedup);

        return new PerformanceMeasurement(medianSaveMilliseconds);
    }

    private static double GetMedian(IEnumerable<double> values)
    {
        var sortedValues = values.Order().ToArray();
        var midpoint = sortedValues.Length / 2;
        return sortedValues.Length % 2 == 0
            ? (sortedValues[midpoint - 1] + sortedValues[midpoint]) / 2
            : sortedValues[midpoint];
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

    private sealed class CockroachDbServer : IAsyncDisposable
    {
        private readonly string _containerName;

        private CockroachDbServer(string containerName, int port)
        {
            _containerName = containerName;
            ConnectionString = $"Host=localhost;Port={port};Username=root;Database=defaultdb;SSL Mode=Disable;Command Timeout=300";
        }

        public string ConnectionString { get; }

        public static async Task<CockroachDbServer> StartAsync()
        {
            var port = GetFreeTcpPort();
            var containerName = $"ef-simple-bulk-perf-{Guid.NewGuid():N}";

            try
            {
                await RunDockerAsync(
                    "run",
                    "--rm",
                    "-d",
                    "--name",
                    containerName,
                    "-p",
                    $"127.0.0.1:{port}:26257",
                    "cockroachdb/cockroach:v26.2.1",
                    "start-single-node",
                    "--insecure",
                    "--max-sql-memory",
                    "1GiB");

                var server = new CockroachDbServer(containerName, port);
                await server.WaitUntilReadyAsync();
                return server;
            }
            catch
            {
                await TryRemoveDockerContainerAsync(containerName);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await TryRemoveDockerContainerAsync(_containerName);
        }

        private async Task WaitUntilReadyAsync()
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
            Exception? lastException = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    await using var connection = new NpgsqlConnection(ConnectionString);
                    await connection.OpenAsync();
                    return;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    await Task.Delay(500);
                }
            }

            throw new TimeoutException("CockroachDB single-node container did not become ready in time.", lastException);
        }

        private static int GetFreeTcpPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private static async Task TryRemoveDockerContainerAsync(string containerName)
        {
            try
            {
                await RunDockerAsync("rm", "-f", containerName);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private sealed class CockroachDbFixture : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _databaseName;

        private CockroachDbFixture(string adminConnectionString, string databaseName)
        {
            _adminConnectionString = adminConnectionString;
            _databaseName = databaseName;
            ConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
            {
                Database = databaseName
            }.ConnectionString;
        }

        public string ConnectionString { get; }

        public static async Task<CockroachDbFixture> CreateAsync(string adminConnectionString)
        {
            var databaseName = $"perf_{Guid.NewGuid():N}";
            await using var connection = new NpgsqlConnection(adminConnectionString);
            await connection.OpenAsync();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"CREATE DATABASE {QuoteIdentifier(databaseName)}";
                await command.ExecuteNonQueryAsync();
            }

            var fixture = new CockroachDbFixture(adminConnectionString, databaseName);
            await fixture.CreateSchemaAsync();
            return fixture;
        }

        public PerformanceDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<PerformanceDbContext>()
                .UseNpgsql(ConnectionString)
                .Options;

            return new PerformanceDbContext(options);
        }

        private async Task CreateSchemaAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
            CREATE TABLE performance_users (
                id INT PRIMARY KEY DEFAULT unique_rowid(),
                first_name TEXT NOT NULL,
                last_name TEXT NOT NULL,
                email TEXT NULL
            );
            """;
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync();
            await using (var dropCommand = connection.CreateCommand())
            {
                dropCommand.CommandText = $"DROP DATABASE IF EXISTS {QuoteIdentifier(_databaseName)}";
                await dropCommand.ExecuteNonQueryAsync();
            }
        }

        private static string QuoteIdentifier(string identifier)
        {
            return "\"" + identifier.Replace("\"", "\"\"") + "\"";
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

    private sealed record PerformanceMeasurement(double SaveMilliseconds);

    private sealed record PerformanceIterationMeasurement(
        double FixtureMilliseconds,
        double ArrangeMilliseconds,
        double SaveMilliseconds,
        double VerifyMilliseconds,
        double TotalMilliseconds,
        int ExpectedSavedCount,
        int SavedCount);

    private sealed class User
    {
        public long Id { get; set; }

        public required string FirstName { get; set; }

        public required string LastName { get; set; }

        public string? Email { get; set; }
    }

    private static async Task RunDockerAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start docker.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker {string.Join(' ', arguments)} failed with exit code {process.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
        }
    }
}
