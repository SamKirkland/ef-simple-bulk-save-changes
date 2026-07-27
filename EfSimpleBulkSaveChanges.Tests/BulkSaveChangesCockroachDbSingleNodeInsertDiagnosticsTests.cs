using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace EfSimpleBulkSaveChanges.Tests;

[TestClass]
[TestCategory("Performance")]
[TestCategory("CockroachDB")]
[TestCategory("CockroachDBSingleNode")]
[DoNotParallelize]
public sealed class BulkSaveChangesCockroachDbSingleNodeInsertDiagnosticsTests
{
    private static CockroachDbSingleNodeServer? Server;

    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static async Task StartCockroachDbAsync(TestContext _)
    {
        try
        {
            Server = await CockroachDbSingleNodeServer.StartAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            Assert.Inconclusive($"CockroachDB single-node diagnostics require a running Docker engine. {ex.Message}");
            throw;
        }

        await using var fixture = await CockroachDbFixture.CreateAsync(Server.ConnectionString);
        await using var db = fixture.CreateContext();

        db.Users.AddRange(CreateUsers(1, "warmup-save"));
        await db.SaveChangesAsync();
    }

    [ClassCleanup]
    public static async Task StopCockroachDbAsync()
    {
        if (Server is not null)
        {
            await Server.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Inserts_DotnetNpgsqlBatchSettingsVersusDirectCockroachSql()
    {
        if (Server is null)
        {
            Assert.Inconclusive("CockroachDB single-node server was not initialized.");
            throw new UnreachableException();
        }

        const int rowCount = 1_000;
        int?[] npgsqlMaxBatchSizes = [null, 1, 42, 100, 1_000];
        int[] directSqlBatchSizes = [1, 42, 100, 1_000];

        TestContext.WriteLine("Database, Scenario, Method, Rows, MaxBatchSize, CommandCount, MinStatementsPerCommand, MaxStatementsPerCommand, SaveElapsedMs, RelativeToDefaultSaveChanges");

        double? defaultSaveChangesElapsed = null;

        foreach (var maxBatchSize in npgsqlMaxBatchSizes)
        {
            var measurement = await MeasureNpgsqlInsertBatchSettingAsync(rowCount, maxBatchSize);
            defaultSaveChangesElapsed ??= measurement.SaveMilliseconds;
            WriteInsertBatchComparison("SaveChanges", rowCount, maxBatchSize, measurement, defaultSaveChangesElapsed.Value);
        }

        var baselineElapsed = defaultSaveChangesElapsed
            ?? throw new UnreachableException("The default SaveChanges measurement should always run first.");

        foreach (var batchSize in directSqlBatchSizes)
        {
            var measurement = await MeasureDirectCockroachSqlInsertCommandsAsync(rowCount, batchSize);
            WriteInsertBatchComparison("Direct cockroach sql single-row INSERT batch", rowCount, batchSize, measurement, baselineElapsed);
        }
    }

    private async Task<InsertBatchMeasurement> MeasureNpgsqlInsertBatchSettingAsync(int rowCount, int? maxBatchSize)
    {
        if (Server is null)
        {
            Assert.Inconclusive("CockroachDB single-node server was not initialized.");
            throw new UnreachableException();
        }

        await using var fixture = await CockroachDbFixture.CreateAsync(Server.ConnectionString);
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

    private async Task<InsertBatchMeasurement> MeasureDirectCockroachSqlInsertCommandsAsync(int rowCount, int batchSize)
    {
        if (Server is null)
        {
            Assert.Inconclusive("CockroachDB single-node server was not initialized.");
            throw new UnreachableException();
        }

        await using var fixture = await CockroachDbFixture.CreateAsync(Server.ConnectionString);
        var commandCount = (rowCount + batchSize - 1) / batchSize;
        var minStatementsPerCommand = rowCount % batchSize == 0 ? batchSize : rowCount % batchSize;
        var script = CreateDirectInsertScript(fixture.DatabaseName, rowCount, batchSize);

        var result = await Server.RunShellScriptAsync(script);
        var elapsedLine = result
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => line.StartsWith("elapsed_ms=", StringComparison.Ordinal));

        Assert.IsNotNull(elapsedLine, $"Could not find elapsed_ms in cockroach sql output:{Environment.NewLine}{result}");
        var elapsedMilliseconds = double.Parse(elapsedLine["elapsed_ms=".Length..]);

        await using var db = fixture.CreateContext();
        Assert.AreEqual(rowCount, await db.Users.CountAsync());

        return new InsertBatchMeasurement(elapsedMilliseconds, commandCount, minStatementsPerCommand, batchSize);
    }

    private static string CreateDirectInsertScript(string databaseName, int rowCount, int batchSize)
    {
        var script = new StringBuilder();
        script.AppendLine("set -eu");
        script.AppendLine("start=$(date +%s%N)");
        script.AppendLine($"batch_size={batchSize}");
        script.AppendLine($"row_count={rowCount}");
        script.AppendLine("batch_start=1");
        script.AppendLine("while [ \"$batch_start\" -le \"$row_count\" ]; do");
        script.AppendLine("  batch_end=$((batch_start + batch_size - 1))");
        script.AppendLine("  if [ \"$batch_end\" -gt \"$row_count\" ]; then batch_end=\"$row_count\"; fi");
        script.AppendLine("  sql_file=\"/tmp/direct-insert-$$.sql\"");
        script.AppendLine("  : > \"$sql_file\"");
        script.AppendLine("  i=\"$batch_start\"");
        script.AppendLine("  while [ \"$i\" -le \"$batch_end\" ]; do");
        script.AppendLine("    printf \"INSERT INTO performance_users (first_name, last_name, email) VALUES ('direct-%s-first-%s', 'direct-%s-last-%s', 'direct-%s-%s@example.com') RETURNING id;\\n\" \"$batch_size\" \"$i\" \"$batch_size\" \"$i\" \"$batch_size\" \"$i\" >> \"$sql_file\"");
        script.AppendLine("    i=$((i + 1))");
        script.AppendLine("  done");
        script.AppendLine("  if [ \"$batch_size\" -le 100 ]; then");
        script.AppendLine($"    /cockroach/cockroach sql --insecure --database={QuoteShell(databaseName)} --execute \"$(cat \"$sql_file\")\" >/dev/null");
        script.AppendLine("  else");
        script.AppendLine($"    /cockroach/cockroach sql --insecure --database={QuoteShell(databaseName)} --file \"$sql_file\" >/dev/null");
        script.AppendLine("  fi");
        script.AppendLine("  rm -f \"$sql_file\"");
        script.AppendLine("  batch_start=$((batch_end + 1))");
        script.AppendLine("done");
        script.AppendLine("end=$(date +%s%N)");
        script.AppendLine("awk -v start=\"$start\" -v end=\"$end\" 'BEGIN { printf \"elapsed_ms=%.2f\\n\", (end - start) / 1000000 }'");
        return script.ToString();
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
            $"CockroachDB single-node, insert, {method}, {rowCount}, {maxBatchSize?.ToString() ?? "default"}, {measurement.CommandCount}, {measurement.MinStatementsPerCommand}, {measurement.MaxStatementsPerCommand}, {measurement.SaveMilliseconds:F2}, {relative}");
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

    private static string QuoteIdentifier(string identifier)
    {
        return "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }

    private static string QuoteShell(string value)
    {
        return "'" + value.Replace("'", "'\"'\"'") + "'";
    }

    private sealed class CockroachDbSingleNodeServer : IAsyncDisposable
    {
        private readonly string _containerName;

        private CockroachDbSingleNodeServer(string containerName, int port)
        {
            _containerName = containerName;
            ConnectionString = $"Host=localhost;Port={port};Username=root;Database=defaultdb;SSL Mode=Disable;Command Timeout=300";
        }

        public string ConnectionString { get; }

        public static async Task<CockroachDbSingleNodeServer> StartAsync()
        {
            var port = GetFreeTcpPort();
            var containerName = $"ef-simple-bulk-single-crdb-{Guid.NewGuid():N}";

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
                "--insecure");

            var server = new CockroachDbSingleNodeServer(containerName, port);
            await server.WaitUntilReadyAsync();
            return server;
        }

        public async Task<string> RunShellScriptAsync(string script)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "docker",
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true
            };

            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(_containerName);
            startInfo.ArgumentList.Add("/bin/sh");

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start docker.");
            await process.StandardInput.WriteAsync(script.Replace("\r\n", "\n"));
            process.StandardInput.Close();

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"docker exec failed with exit code {process.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
            }

            return output;
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

            throw new TimeoutException("CockroachDB single-node container did not become ready in time.", lastException);
        }

        private static int GetFreeTcpPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    private sealed class CockroachDbFixture : IAsyncDisposable
    {
        private readonly string _adminConnectionString;

        private CockroachDbFixture(string adminConnectionString, string databaseName)
        {
            _adminConnectionString = adminConnectionString;
            DatabaseName = databaseName;
            ConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
            {
                Database = databaseName
            }.ConnectionString;
        }

        public string ConnectionString { get; }

        public string DatabaseName { get; }

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

            return new PerformanceDbContext(optionsBuilder.Options);
        }

        private async Task CreateSchemaAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
            CREATE TABLE performance_users (
                id INT PRIMARY KEY DEFAULT unordered_unique_rowid(),
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
            await using var dropCommand = connection.CreateCommand();
            dropCommand.CommandText = $"DROP DATABASE IF EXISTS {QuoteIdentifier(DatabaseName)}";
            await dropCommand.ExecuteNonQueryAsync();
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
