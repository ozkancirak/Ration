using System.Globalization;
using Microsoft.Data.Sqlite;
using Kalan.Core.Model;

namespace Kalan.Core.Providers.OpenCode;

/// <summary>
/// OpenCode veritabanını canlı dosyaya bağlanmadan okur. Ana DB ve varsa WAL/SHM
/// önce Kalan cache altında tek kullanımlık bir klasöre kopyalanır.
/// </summary>
public static class OpenCodeLocalUsageReader
{
    public static CostReport? Read(
        string databasePath,
        string cacheDirectory,
        CancellationToken ct = default)
    {
        if (!File.Exists(databasePath)) return null;

        var copyDirectory = Path.Combine(
            cacheDirectory,
            "read-" + Guid.NewGuid().ToString("N"));
        var copyPath = Path.Combine(copyDirectory, "opencode.db");
        var periodEnd = DateTimeOffset.UtcNow;
        var periodStart = periodEnd.AddDays(-30);

        try
        {
            Directory.CreateDirectory(copyDirectory);
            CopyFile(databasePath, copyPath, ct);
            CopyIfPresent(databasePath + "-wal", Path.Combine(copyDirectory, "opencode.db-wal"), ct);
            CopyIfPresent(databasePath + "-shm", Path.Combine(copyDirectory, "opencode.db-shm"), ct);

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = copyPath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
            }.ToString();

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT
                        SUM(cost),
                        SUM(tokens_input),
                        SUM(tokens_output),
                        SUM(tokens_reasoning),
                        SUM(tokens_cache_read),
                        SUM(tokens_cache_write)
                    FROM session
                    WHERE time_created >= $threshold;
                    """;
                command.Parameters.AddWithValue("$threshold", periodStart.ToUnixTimeMilliseconds());

                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) return null;

                    return new CostReport(
                        TotalCost: ReadDecimal(reader, 0),
                        Currency: "USD",
                        PeriodStart: periodStart,
                        PeriodEnd: periodEnd,
                        InputTokens: ReadLong(reader, 1),
                        OutputTokens: ReadLong(reader, 2),
                        ReasoningTokens: ReadLong(reader, 3),
                        CacheReadTokens: ReadLong(reader, 4),
                        CacheCreationTokens: ReadLong(reader, 5));
                }
            }
        }
        catch (SqliteException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteDirectory(copyDirectory);
        }
    }

    private static void CopyIfPresent(string source, string destination, CancellationToken ct)
    {
        if (File.Exists(source)) CopyFile(source, destination, ct);
    }

    private static void CopyFile(string source, string destination, CancellationToken ct)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        ct.ThrowIfCancellationRequested();
    }

    private static long ReadLong(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? 0
            : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static decimal ReadDecimal(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? 0m
            : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
