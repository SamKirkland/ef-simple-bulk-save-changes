using System.Diagnostics;
using System.Globalization;

namespace EfSimpleBulkSaveChanges.Tests;

internal static class PerformanceReadmeUpdater
{
    private static readonly Lock Sync = new();
    private static readonly Dictionary<string, string> ScenarioHeadings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["insert"] = "Insert",
        ["update"] = "Update",
        ["mixed"] = "Mixed Add/Update/Delete",
        ["synchronize"] = "Synchronize"
    };

    private static readonly Dictionary<string, (int Elapsed, int Speedup)> DatabaseColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SQLite"] = (3, 4),
        ["PostgreSQL"] = (5, 6),
        ["CockroachDB"] = (7, 8)
    };

    public static void Record(
        string database,
        string scenario,
        string method,
        int rowCount,
        int? batchSize,
        double elapsedMilliseconds,
        string speedup)
    {
        lock (Sync)
        {
            var root = FindRepositoryRoot();
            var readmePath = Path.Combine(root, "README.md");
            var lines = File.ReadAllLines(readmePath).ToList();
            var rowIndex = FindPerformanceRow(lines, scenario, method, rowCount, batchSize);
            var cells = lines[rowIndex].Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
            var columns = GetDatabaseColumns(database, cells.Length);

            cells[columns.Elapsed] = elapsedMilliseconds.ToString("N2", CultureInfo.InvariantCulture);
            cells[columns.Speedup] = speedup;
            lines[rowIndex] = "| " + string.Join(" | ", cells) + " |";
            UpdateCockroachDbOutcomeSummary(lines);

            File.WriteAllLines(readmePath, lines);
        }
    }

    public static void RegenerateChart()
    {
        lock (Sync)
        {
            var root = FindRepositoryRoot();
            var readmePath = Path.Combine(root, "README.md");
            var lines = File.ReadAllLines(readmePath).ToList();
            UpdateCockroachDbOutcomeSummary(lines);
            File.WriteAllLines(readmePath, lines);

            var scriptPath = Path.Combine(root, "scripts", "Generate-PerformanceChart.ps1");
            var startInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "powershell" : "pwsh",
                WorkingDirectory = root,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            if (OperatingSystem.IsWindows())
            {
                startInfo.ArgumentList.Add("-ExecutionPolicy");
                startInfo.ArgumentList.Add("Bypass");
            }

            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start performance chart generation.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Generating the performance chart failed with exit code {process.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
            }
        }
    }

    private static void UpdateCockroachDbOutcomeSummary(List<string> lines)
    {
        var summaryIndex = lines.FindIndex(line => line == "### CockroachDB Outcome Summary");
        if (summaryIndex < 0 || summaryIndex + 2 >= lines.Count)
        {
            return;
        }

        var insertSaveChanges = GetPerformanceCell(lines, "insert", "SaveChanges", 10_000, null, "CockroachDB").Elapsed;
        var insertBulk = GetPerformanceCell(lines, "insert", "BulkSaveChanges", 10_000, 10_000, "CockroachDB").Elapsed;
        var updateSaveChanges = GetPerformanceCell(lines, "update", "SaveChanges", 10_000, null, "CockroachDB").Elapsed;
        var updateBulk = GetPerformanceCell(lines, "update", "BulkSaveChanges", 10_000, 5_000, "CockroachDB").Elapsed;
        var mixedSaveChanges = GetPerformanceCell(lines, "mixed", "SaveChanges", 10_000, null, "CockroachDB").Elapsed;
        var mixedBulk = GetPerformanceCell(lines, "mixed", "BulkSaveChanges", 10_000, 5_000, "CockroachDB").Elapsed;
        var synchronizeSaveChanges = GetPerformanceCell(lines, "synchronize", "SaveChanges", 10_000, null, "CockroachDB").Elapsed;
        var synchronizeBulk = GetPerformanceCell(lines, "synchronize", "BulkSynchronize", 10_000, 5_000, "CockroachDB").Elapsed;

        lines[summaryIndex + 2] =
            $"The CockroachDB-focused update change replaces `UNION ALL` update sources with a `VALUES` CTE, and the CockroachDB performance schema now matches a production-friendly `DEFAULT unique_rowid()` key shape. With a single-node CockroachDB cluster, larger batches reduce round trips substantially across inserts, updates, mixed changes, and synchronization. At 10,000 rows, bulk inserts with batch 10,000 completed in a median {insertBulk} ms versus {insertSaveChanges} ms for `SaveChanges`; 10,000-row updates reached {updateBulk} ms at batch 5,000 versus {updateSaveChanges} ms; mixed changes reached {mixedBulk} ms at batch 5,000 versus {mixedSaveChanges} ms; and synchronize reached {synchronizeBulk} ms at batch 5,000 versus {synchronizeSaveChanges} ms.";
    }

    private static (string Elapsed, string Speedup) GetPerformanceCell(
        List<string> lines,
        string scenario,
        string method,
        int rowCount,
        int? batchSize,
        string database)
    {
        var rowIndex = FindPerformanceRow(lines, scenario, method, rowCount, batchSize);
        var cells = lines[rowIndex].Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
        var columns = GetDatabaseColumns(database, cells.Length);
        return (cells[columns.Elapsed], cells[columns.Speedup]);
    }

    private static int FindPerformanceRow(
        List<string> lines,
        string scenario,
        string method,
        int rowCount,
        int? batchSize)
    {
        var heading = ScenarioHeadings[scenario];
        var inScenario = false;
        var expectedRows = rowCount.ToString("N0", CultureInfo.InvariantCulture);
        var expectedBatchSize = batchSize?.ToString("N0", CultureInfo.InvariantCulture) ?? string.Empty;

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                inScenario = string.Equals(line[4..].Trim(), heading, StringComparison.Ordinal);
                continue;
            }

            if (!inScenario || !line.StartsWith("|", StringComparison.Ordinal) || line.Contains("---", StringComparison.Ordinal) || line.Contains("Rows |", StringComparison.Ordinal))
            {
                continue;
            }

            var cells = line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
            if (cells.Length < 5)
            {
                continue;
            }

            if (cells[0] == expectedRows && cells[1] == method && cells[2] == expectedBatchSize)
            {
                return index;
            }
        }

        throw new InvalidOperationException(
            $"Could not find README performance row for {scenario}, {method}, {rowCount}, {expectedBatchSize}.");
    }

    private static (int Elapsed, int Speedup) GetDatabaseColumns(string database, int cellCount)
    {
        if (cellCount == 5)
        {
            if (!string.Equals(database, "SQLite", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"A 5-column performance table can only be updated with SQLite results, not {database}.");
            }

            return (3, 4);
        }

        return DatabaseColumns.TryGetValue(database, out var columns)
            ? columns
            : throw new InvalidOperationException($"Unsupported performance database '{database}'.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "README.md"))
                && File.Exists(Path.Combine(directory.FullName, "EfSimpleBulkSaveChanges.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find the repository root.");
    }
}
