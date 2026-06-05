using System.Diagnostics;
using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace EfSimpleBulkSaveChanges.Tests;

[TestClass]
[TestCategory("Performance")]
[TestCategory("PostgreSQL")]
[DoNotParallelize]
public sealed class BulkSaveChangesPostgreSqlPerformanceTests
{
    private static readonly int[] RowCounts = [1, 100, 1_000, 10_000];
    private static readonly int[] LargeChangeBulkBatchSizes = [100, 250];
    private static PostgreSqlServer? Server;

    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static async Task StartPostgreSqlAsync(TestContext _)
    {
        try
        {
            Server = await PostgreSqlServer.StartAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            Assert.Inconclusive($"PostgreSQL performance tests require a running Docker engine. {ex.Message}");
            throw;
        }

        await using var fixture = await PostgreSqlFixture.CreateAsync(Server.ConnectionString);
        await using var db = fixture.CreateContext();

        db.Users.AddRange(CreateUsers(1, "warmup-save"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        db.Users.AddRange(CreateUsers(1, "warmup-bulk"));
        await db.BulkSaveChangesAsync(batchSize: 100);
    }

    [ClassCleanup]
    public static async Task StopPostgreSqlAsync()
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
    public async Task Inserts_ManualSqlVersusNpgsqlBatchSettings()
    {
        const int rowCount = 1_000;
        int?[] npgsqlMaxBatchSizes = [null, 1, 42, 100, 1_000];
        int[] manualBatchSizes = [1, 42, 100, 1_000];

        TestContext.WriteLine("Database, Scenario, Method, Rows, MaxBatchSize, CommandCount, MinStatementsPerCommand, MaxStatementsPerCommand, SaveElapsedMs, RelativeToDefaultSaveChanges");

        double? defaultSaveChangesElapsed = null;

        foreach (var maxBatchSize in npgsqlMaxBatchSizes)
        {
            var measurement = await MeasureNpgsqlInsertBatchSettingAsync(rowCount, maxBatchSize);
            defaultSaveChangesElapsed ??= measurement.SaveMilliseconds;
            WriteInsertBatchComparison(
                "SaveChanges",
                rowCount,
                maxBatchSize,
                measurement,
                defaultSaveChangesElapsed.Value);
        }

        var baselineElapsed = defaultSaveChangesElapsed
            ?? throw new UnreachableException("The default SaveChanges measurement should always run first.");

        foreach (var batchSize in manualBatchSizes)
        {
            var measurement = await MeasureManualInsertCommandsAsync(rowCount, batchSize);
            WriteInsertBatchComparison(
                "Manual single-row INSERT batch",
                rowCount,
                batchSize,
                measurement,
                baselineElapsed);
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
        TestContext.WriteLine("VERBOSE Database, Scenario, Method, Rows, BatchSize, FixtureMs, ArrangeMs, SaveMs, VerifyMs, TotalMs, ExpectedSaved, ActualSaved");
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

    private async Task<InsertBatchMeasurement> MeasureNpgsqlInsertBatchSettingAsync(int rowCount, int? maxBatchSize)
    {
        if (Server is null)
        {
            Assert.Inconclusive("PostgreSQL server was not initialized.");
            throw new UnreachableException();
        }

        await using var fixture = await PostgreSqlFixture.CreateAsync(Server.ConnectionString);
        var diagnostics = new InsertCommandDiagnostics();
        await using var db = fixture.CreateContext(maxBatchSize, diagnostics);

        db.Users.AddRange(CreateUsers(rowCount, $"save-insert-max-{maxBatchSize?.ToString() ?? "default"}"));

        var stopwatch = Stopwatch.StartNew();
        var savedCount = await db.SaveChangesAsync();
        stopwatch.Stop();

        Assert.AreEqual(rowCount, savedCount);
        Assert.AreEqual(rowCount, await db.Users.CountAsync());

        return diagnostics.ToMeasurement(stopwatch.Elapsed.TotalMilliseconds);
    }

    private async Task<InsertBatchMeasurement> MeasureManualInsertCommandsAsync(int rowCount, int batchSize)
    {
        if (Server is null)
        {
            Assert.Inconclusive("PostgreSQL server was not initialized.");
            throw new UnreachableException();
        }

        await using var fixture = await PostgreSqlFixture.CreateAsync(Server.ConnectionString);
        var commandCount = 0;
        var minStatementsPerCommand = int.MaxValue;
        var maxStatementsPerCommand = 0;

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var stopwatch = Stopwatch.StartNew();

        for (var batchStart = 1; batchStart <= rowCount; batchStart += batchSize)
        {
            var count = Math.Min(batchSize, rowCount - batchStart + 1);
            commandCount++;
            minStatementsPerCommand = Math.Min(minStatementsPerCommand, count);
            maxStatementsPerCommand = Math.Max(maxStatementsPerCommand, count);

            await using var batch = new NpgsqlBatch(connection);

            for (var offset = 0; offset < count; offset++)
            {
                var index = batchStart + offset;
                var command = new NpgsqlBatchCommand("""
                INSERT INTO performance_users (first_name, last_name, email)
                VALUES ($1, $2, $3)
                RETURNING id;
                """);

                command.Parameters.Add(new NpgsqlParameter { Value = $"manual-insert-{batchSize}-first-{index}" });
                command.Parameters.Add(new NpgsqlParameter { Value = $"manual-insert-{batchSize}-last-{index}" });
                command.Parameters.Add(new NpgsqlParameter { Value = $"manual-insert-{batchSize}-{index}@example.com" });
                batch.BatchCommands.Add(command);
            }

            await using var reader = await batch.ExecuteReaderAsync();

            do
            {
                while (await reader.ReadAsync())
                {
                    _ = reader.GetInt32(0);
                }
            }
            while (await reader.NextResultAsync());
        }

        stopwatch.Stop();

        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = "SELECT COUNT(*) FROM performance_users;";
            Assert.AreEqual(rowCount, Convert.ToInt32(await countCommand.ExecuteScalarAsync()));
        }

        return new InsertBatchMeasurement(
            stopwatch.Elapsed.TotalMilliseconds,
            commandCount,
            minStatementsPerCommand,
            maxStatementsPerCommand);
    }

    private void WriteInsertBatchComparison(
        string method,
        int rowCount,
        int? maxBatchSize,
        InsertBatchMeasurement measurement,
        double defaultSaveChangesElapsed)
    {
        var relative = $"{defaultSaveChangesElapsed / measurement.SaveMilliseconds:F2}x";
        TestContext.WriteLine(
            $"PostgreSQL, insert, {method}, {rowCount}, {maxBatchSize?.ToString() ?? "default"}, {measurement.CommandCount}, {measurement.MinStatementsPerCommand}, {measurement.MaxStatementsPerCommand}, {measurement.SaveMilliseconds:F2}, {relative}");
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
            Assert.Inconclusive("PostgreSQL server was not initialized.");
            throw new UnreachableException();
        }

        var totalStopwatch = Stopwatch.StartNew();
        var fixtureStopwatch = Stopwatch.StartNew();
        await using var fixture = await PostgreSqlFixture.CreateAsync(Server.ConnectionString);
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
            $"PostgreSQL, {scenario}, {method}, {rowCount}, {batchSize?.ToString() ?? "n/a"}, {saveStopwatch.Elapsed.TotalMilliseconds:F2}, {speedup}");
        TestContext.WriteLine(
            $"VERBOSE PostgreSQL, {scenario}, {method}, {rowCount}, {batchSize?.ToString() ?? "n/a"}, {fixtureStopwatch.Elapsed.TotalMilliseconds:F2}, {arrangeStopwatch.Elapsed.TotalMilliseconds:F2}, {saveStopwatch.Elapsed.TotalMilliseconds:F2}, {verifyStopwatch.Elapsed.TotalMilliseconds:F2}, {totalStopwatch.Elapsed.TotalMilliseconds:F2}, {expectedSavedCount}, {savedCount}");
        PerformanceReadmeUpdater.Record(
            "PostgreSQL",
            scenario,
            method,
            rowCount,
            batchSize,
            saveStopwatch.Elapsed.TotalMilliseconds,
            speedup);

        return new PerformanceMeasurement(saveStopwatch.Elapsed.TotalMilliseconds);
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

    private sealed class PostgreSqlServer : IAsyncDisposable
    {
        private readonly string _containerName;

        private PostgreSqlServer(string containerName, int port)
        {
            _containerName = containerName;
            ConnectionString = $"Host=localhost;Port={port};Username=postgres;Password=postgres;Database=postgres";
        }

        public string ConnectionString { get; }

        public static async Task<PostgreSqlServer> StartAsync()
        {
            var port = GetFreeTcpPort();
            var containerName = $"ef-simple-bulk-perf-{Guid.NewGuid():N}";

            await RunDockerAsync(
                "run",
                "--rm",
                "-d",
                "--name",
                containerName,
                "-e",
                "POSTGRES_PASSWORD=postgres",
                "-p",
                $"127.0.0.1:{port}:5432",
                "postgres:16-alpine");

            var server = new PostgreSqlServer(containerName, port);
            await server.WaitUntilReadyAsync();
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            await RunDockerAsync("rm", "-f", _containerName);
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

            throw new TimeoutException("PostgreSQL container did not become ready in time.", lastException);
        }

        private static int GetFreeTcpPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    private sealed class PostgreSqlFixture : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _databaseName;

        private PostgreSqlFixture(string adminConnectionString, string databaseName)
        {
            _adminConnectionString = adminConnectionString;
            _databaseName = databaseName;
            ConnectionString = $"{adminConnectionString};Database={databaseName}";
        }

        public string ConnectionString { get; }

        public static async Task<PostgreSqlFixture> CreateAsync(string adminConnectionString)
        {
            var databaseName = $"perf_{Guid.NewGuid():N}";
            await using var connection = new NpgsqlConnection(adminConnectionString);
            await connection.OpenAsync();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"CREATE DATABASE {QuoteIdentifier(databaseName)}";
                await command.ExecuteNonQueryAsync();
            }

            var fixture = new PostgreSqlFixture(adminConnectionString, databaseName);
            await using var db = fixture.CreateContext();
            await db.Database.EnsureCreatedAsync();
            return fixture;
        }

        public PerformanceDbContext CreateContext(
            int? maxBatchSize = null,
            IInterceptor? interceptor = null)
        {
            var optionsBuilder = new DbContextOptionsBuilder<PerformanceDbContext>()
                .UseNpgsql(
                    ConnectionString,
                    npgsqlOptions =>
                    {
                        if (maxBatchSize is not null)
                        {
                            npgsqlOptions.MaxBatchSize(maxBatchSize.Value);
                        }
                    });

            if (interceptor is not null)
            {
                optionsBuilder.AddInterceptors(interceptor);
            }

            var options = optionsBuilder.Options;

            return new PerformanceDbContext(options);
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync();
            await using (var terminateCommand = connection.CreateCommand())
            {
                terminateCommand.CommandText = """
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = @databaseName;
                """;
                terminateCommand.Parameters.AddWithValue("databaseName", _databaseName);
                await terminateCommand.ExecuteNonQueryAsync();
            }

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

    private sealed record InsertBatchMeasurement(
        double SaveMilliseconds,
        int CommandCount,
        int MinStatementsPerCommand,
        int MaxStatementsPerCommand);

    private sealed class InsertCommandDiagnostics : DbCommandInterceptor
    {
        private readonly List<int> _statementsPerCommand = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CountInsertStatements(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public InsertBatchMeasurement ToMeasurement(double saveMilliseconds)
        {
            return new InsertBatchMeasurement(
                saveMilliseconds,
                _statementsPerCommand.Count,
                _statementsPerCommand.Count == 0 ? 0 : _statementsPerCommand.Min(),
                _statementsPerCommand.Count == 0 ? 0 : _statementsPerCommand.Max());
        }

        private void CountInsertStatements(DbCommand command)
        {
            var insertCount = 0;
            var commandText = command.CommandText.AsSpan();
            const string insertPrefix = "INSERT INTO performance_users";

            while (true)
            {
                var index = commandText.IndexOf(insertPrefix, StringComparison.OrdinalIgnoreCase);

                if (index < 0)
                {
                    break;
                }

                insertCount++;
                commandText = commandText[(index + insertPrefix.Length)..];
            }

            if (insertCount > 0)
            {
                _statementsPerCommand.Add(insertCount);
            }
        }
    }

    private sealed class User
    {
        public int Id { get; set; }

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
