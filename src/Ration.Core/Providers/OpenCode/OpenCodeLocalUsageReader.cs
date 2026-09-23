using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ration.Core.Cost;
using Ration.Core.Model;

namespace Ration.Core.Providers.OpenCode;

/// <summary>
/// OpenCode veritabanını salt okunur okur. Önce doğrudan salt okunur bağlantı denenir:
/// SQLite okuyucusu DB dosyasını değiştirmez, WAL modunda OpenCode'un yazmasını engellemez.
/// Önceden her okumada DB kopyalanıyordu; 1,1 GB'lık bir DB ile her yenilemede 1,1 GB
/// disk yazımı ve yarıda kalan kopyalardan GB'larca kalıntı demekti. Doğrudan açılamazsa
/// (kilit vb.) eski yol: tek kullanımlık klasöre kopyalayıp oku.
/// </summary>
public static class OpenCodeLocalUsageReader
{
    public static CostReport? Read(
        string databasePath,
        string cacheDirectory,
        CancellationToken ct = default,
        string? freeModelPath = null)
    {
        if (!File.Exists(databasePath)) return null;

        CleanupStaleCopies(cacheDirectory);
        var start = DateTimeOffset.UtcNow.AddDays(-30);
        try
        {
            return Query(databasePath, start, freeModelPath, ct);
        }
        catch (SqliteException)
        {
            // Doğrudan okunamadı; kopya üzerinden dene.
        }

        var copyDirectory = Path.Combine(
            cacheDirectory,
            "read-" + Guid.NewGuid().ToString("N"));
        var copyPath = Path.Combine(copyDirectory, "opencode.db");

        try
        {
            Directory.CreateDirectory(copyDirectory);
            CopyFile(databasePath, copyPath, ct);
            CopyIfPresent(databasePath + "-wal", Path.Combine(copyDirectory, "opencode.db-wal"), ct);
            CopyIfPresent(databasePath + "-shm", Path.Combine(copyDirectory, "opencode.db-shm"), ct);

            return Query(copyPath, start, freeModelPath, ct);
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

    private static CostReport? Query(string path, DateTimeOffset periodStart, string? freeModelPath, CancellationToken ct)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            // Havuzlanmış bağlantı dosya tanıtıcısını açık tutar; her okumadan sonra bırak.
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        var modelUsage = HasColumn(connection, "model")
            ? ReadModelUsage(connection, periodStart)
            : null;
        var freeUsage = ReadFreeUsage(connection, freeModelPath, ct);
        var daily = ReadDaily(connection, periodStart);

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

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        return new CostReport(
            TotalCost: ReadDecimal(reader, 0),
            Currency: "USD",
            PeriodStart: periodStart,
            PeriodEnd: DateTimeOffset.UtcNow,
            InputTokens: ReadLong(reader, 1),
            OutputTokens: ReadLong(reader, 2),
            ReasoningTokens: ReadLong(reader, 3),
            CacheReadTokens: ReadLong(reader, 4),
            CacheCreationTokens: ReadLong(reader, 5),
            Models: modelUsage,
            FreeUsage: freeUsage,
            Daily: daily);
    }

    /// <summary>Günlük grafik için yerel güne göre token toplamı (oturum oluşturulma günü).</summary>
    private static IReadOnlyList<DailyTokens> ReadDaily(SqliteConnection connection, DateTimeOffset periodStart)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                date(time_created / 1000, 'unixepoch', 'localtime'),
                SUM(COALESCE(tokens_input, 0) + COALESCE(tokens_output, 0)
                    + COALESCE(tokens_cache_read, 0) + COALESCE(tokens_cache_write, 0))
            FROM session
            WHERE time_created >= $threshold
            GROUP BY 1
            ORDER BY 1;
            """;
        command.Parameters.AddWithValue("$threshold", periodStart.ToUnixTimeMilliseconds());

        var result = new List<DailyTokens>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) ||
                !DateOnly.TryParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            result.Add(new DailyTokens(day, ReadLong(reader, 1)));
        }
        return result;
    }

    /// <summary>Süreç kopyalama sırasında kapanırsa kalan eski okuma klasörlerini siler.</summary>
    private static void CleanupStaleCopies(string cacheDirectory)
    {
        try
        {
            if (!Directory.Exists(cacheDirectory)) return;
            foreach (var dir in Directory.EnumerateDirectories(cacheDirectory, "read-*"))
            {
                if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) > TimeSpan.FromMinutes(10))
                {
                    TryDeleteDirectory(dir);
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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
            var model = reader.IsDBNull(0)
                ? "(bilinmeyen model)"
                : NormalizeModelName(reader.GetString(0));
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

    private static string NormalizeModelName(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('{')) return trimmed;

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return trimmed;

            foreach (var name in new[] { "id", "modelID", "modelId" })
            {
                if (document.RootElement.TryGetProperty(name, out var id) &&
                    id.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    return id.GetString()!;
                }
            }
        }
        catch (JsonException) { }

        return trimmed;
    }

    private static FreeModelUsage? ReadFreeUsage(
        SqliteConnection connection,
        string? freeModelPath,
        CancellationToken ct)
    {
        if (!HasTable(connection, "message") ||
            !HasColumn(connection, "message", "time_created") ||
            !HasColumn(connection, "message", "data"))
        {
            return null;
        }

        var catalog = FreeModelCatalog.LoadOrEmpty(freeModelPath);
        var start = new DateTimeOffset(DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc));
        var end = start.AddDays(1);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT data
            FROM message
            WHERE time_created >= $start AND time_created < $end;
            """;
        command.Parameters.AddWithValue("$start", start.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$end", end.ToUnixTimeMilliseconds());

        var count = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.IsDBNull(0)) continue;

            try
            {
                using var document = JsonDocument.Parse(reader.GetString(0));
                var data = document.RootElement;
                if (!TryReadString(data, "role", out var role) ||
                    !role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ||
                    !TryReadString(data, "modelID", out var modelId))
                {
                    continue;
                }

                if (catalog.IsFree(modelId)) count++;
            }
            catch (JsonException) { }
        }

        return new FreeModelUsage(count, start);
    }

    private static bool HasTable(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        return command.ExecuteScalar() is not null;
    }

    private static bool HasColumn(SqliteConnection connection, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (!reader.IsDBNull(1) &&
                string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
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
