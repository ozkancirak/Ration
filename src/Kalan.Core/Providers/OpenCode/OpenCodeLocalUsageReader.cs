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
                var modelUsage = HasColumn(connection, "model")
                    ? ReadModelUsage(connection, periodStart)
                    : null;

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
                        CacheCreationTokens: ReadLong(reader, 5),
                        Models: modelUsage);
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

    private static bool HasColumn(SqliteConnection connection, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(session);";
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (reader.IsDBNull(1)) continue;
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<ModelTokenUsage> ReadModelUsage(
        SqliteConnection connection,
        DateTimeOffset periodStart)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COALESCE(NULLIF(TRIM(model), ''), '(bilinmeyen model)') AS model_name,
                SUM(COALESCE(tokens_input, 0)) AS input_tokens,
                SUM(COALESCE(tokens_output, 0)) AS output_tokens,
                SUM(COALESCE(tokens_cache_read, 0)) AS cache_read_tokens,
                SUM(COALESCE(tokens_cache_write, 0)) AS cache_write_tokens,
                SUM(
                    COALESCE(tokens_input, 0) +
                    COALESCE(tokens_output, 0) +
                    COALESCE(tokens_cache_read, 0) +
                    COALESCE(tokens_cache_write, 0)) AS total_tokens
            FROM session
            WHERE time_created >= $threshold
            GROUP BY model_name
            ORDER BY total_tokens DESC;
            """;
        command.Parameters.AddWithValue("$threshold", periodStart.ToUnixTimeMilliseconds());

        using var reader = command.ExecuteReader();
        var models = new List<ModelTokenUsage>();
        while (reader.Read())
        {
            var model = reader.IsDBNull(0) ? "(bilinmeyen model)" : reader.GetString(0);
            var input = ReadLong(reader, 1);
            var output = ReadLong(reader, 2);
            var cacheRead = ReadLong(reader, 3);
            var cacheWrite = ReadLong(reader, 4);
            var tokens = reader.IsDBNull(5)
                ? 0L
                : Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture);
            if (tokens > 0)
            {
                models.Add(new ModelTokenUsage(model, tokens, input, output, cacheRead, cacheWrite));
            }
        }

        return models;
    }

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
