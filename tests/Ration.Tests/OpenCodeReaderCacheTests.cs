using Microsoft.Data.Sqlite;
using Ration.Core.Providers.OpenCode;

namespace Ration.Tests;

/// <summary>Veritabanı değişmediyse OpenCode raporu yeniden sorgulanmaz. Veriler SENTETİKTİR.</summary>
public sealed class OpenCodeReaderCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ration-opencode-cache-{Guid.NewGuid():N}");
    private string DatabasePath => Path.Combine(_root, "opencode.db");

    public OpenCodeReaderCacheTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void CreateDatabase()
    {
        var created = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
        Execute("""
            CREATE TABLE session (
                time_created INTEGER NOT NULL, model TEXT, cost REAL,
                tokens_input INTEGER, tokens_output INTEGER, tokens_reasoning INTEGER,
                tokens_cache_read INTEGER, tokens_cache_write INTEGER);
            CREATE TABLE message (time_created INTEGER NOT NULL, data TEXT NOT NULL);
            INSERT INTO session VALUES (__CREATED__, '{"id":"ornek-model","providerID":"opencode"}', 1.25, 100, 200, 30, 40, 50);
            """.Replace("__CREATED__", created.ToString()));
    }

    private long ReadInputTokens() =>
        OpenCodeLocalUsageReader.Read(DatabasePath, Path.Combine(_root, "cache"))!.InputTokens;

    [Fact]
    public void Degismediyse_OnbellektenDoner_DegisinceYenidenSorgular()
    {
        CreateDatabase();
        Assert.Equal(100, ReadInputTokens());

        // İçeriği değiştir ama boyut ve zaman damgasını eskisi gibi bırak: önbellek isabet etmeli.
        var original = File.GetLastWriteTimeUtc(DatabasePath);
        var originalLength = new FileInfo(DatabasePath).Length;
        Execute("UPDATE session SET tokens_input = 999;");
        SqliteConnection.ClearAllPools();
        Assert.Equal(originalLength, new FileInfo(DatabasePath).Length);
        File.SetLastWriteTimeUtc(DatabasePath, original);

        Assert.Equal(100, ReadInputTokens());

        // Zaman damgası değişince gerçek veri okunur.
        File.SetLastWriteTimeUtc(DatabasePath, original.AddSeconds(5));

        Assert.Equal(999, ReadInputTokens());
    }

    [Fact]
    public void KopyaYolu_BoyutSinirini_VeritabaniWalVeShmIcinUygular()
    {
        CreateDatabase();
        var size = new FileInfo(DatabasePath).Length;

        Assert.True(OpenCodeLocalUsageReader.FitsForCopy(DatabasePath, size));
        Assert.False(OpenCodeLocalUsageReader.FitsForCopy(DatabasePath, size - 1));

        File.WriteAllBytes(DatabasePath + "-wal", new byte[1000]);
        File.WriteAllBytes(DatabasePath + "-shm", new byte[500]);
        Assert.True(OpenCodeLocalUsageReader.FitsForCopy(DatabasePath, size + 1500));
        Assert.False(OpenCodeLocalUsageReader.FitsForCopy(DatabasePath, size + 1499));

        Assert.Equal(256L * 1024 * 1024, OpenCodeLocalUsageReader.MaxCopyBytes);
    }

    [Fact]
    public void DegisimAnahtari_GunBoyutVeZamanlaDegisir()
    {
        CreateDatabase();
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

        var key = OpenCodeLocalUsageReader.ChangeKey(DatabasePath, null, now);

        Assert.NotNull(key);
        Assert.Equal(key, OpenCodeLocalUsageReader.ChangeKey(DatabasePath, null, now.AddHours(3)));
        Assert.NotEqual(key, OpenCodeLocalUsageReader.ChangeKey(DatabasePath, null, now.AddDays(1)));

        File.WriteAllText(DatabasePath + "-wal", "x"); // WAL belirdi
        Assert.NotEqual(key, OpenCodeLocalUsageReader.ChangeKey(DatabasePath, null, now));

        Assert.Null(OpenCodeLocalUsageReader.ChangeKey(Path.Combine(_root, "yok.db"), null, now));
    }
}
