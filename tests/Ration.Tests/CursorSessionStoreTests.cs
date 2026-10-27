using System.Buffers.Text;
using System.Text;
using Microsoft.Data.Sqlite;
using Ration.Core.Providers.Cursor;

namespace Ration.Tests;

public sealed class CursorSessionStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"ration-cursor-{Guid.NewGuid():N}");

    public CursorSessionStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private static string FakeJwt(string sub, DateTimeOffset expires)
    {
        var header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes("{\"alg\":\"none\"}"));
        var payload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            $"{{\"sub\":\"{sub}\",\"exp\":{expires.ToUnixTimeSeconds()},\"type\":\"session\"}}"));
        return $"{header}.{payload}.signature";
    }

    private string CreateDatabase(params (string Key, object Value)[] rows)
    {
        var path = Path.Combine(_directory, $"state-{Guid.NewGuid():N}.vscdb");
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB);";
            create.ExecuteNonQuery();
        }

        foreach (var (key, value) in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO ItemTable VALUES ($key, $value);";
            insert.Parameters.AddWithValue("$key", key);
            insert.Parameters.AddWithValue("$value", value);
            insert.ExecuteNonQuery();
        }

        return path;
    }

    [Fact]
    public void GecerliOturum_BelirtecKullaniciVePlaniOkur()
    {
        var path = CreateDatabase(
            ("cursorAuth/accessToken", FakeJwt("github|user_ABC123", Now.AddDays(10))),
            ("cursorAuth/stripeMembershipType", "pro"),
            ("cursorAuth/stripeSubscriptionStatus", "active"),
            ("cursorAuth/cachedEmail", "kisi@example.com"));

        var session = CursorSessionStore.TryRead(path);

        Assert.NotNull(session);
        Assert.Equal("user_ABC123", session.UserId);
        Assert.Equal("pro", session.MembershipType);
        Assert.Equal("active", session.SubscriptionStatus);
        Assert.Equal(Now.AddDays(10), session.ExpiresAt);
        Assert.True(session.IsUsable(Now));
    }

    [Fact]
    public void SuresiDolanBelirtec_OkunurAmaKullanilamaz()
    {
        var path = CreateDatabase(("cursorAuth/accessToken", FakeJwt("github|user_ABC123", Now.AddSeconds(30))));

        var session = CursorSessionStore.TryRead(path);

        Assert.NotNull(session);
        Assert.False(session.IsUsable(Now)); // 60 sn payı: dolmak üzere olan belirteç gönderilmez
    }

    [Fact]
    public void Utf16LeBlob_BozulmadanOkunur()
    {
        var token = FakeJwt("github|user_ABC123", Now.AddDays(1));
        var path = CreateDatabase(("cursorAuth/accessToken", Encoding.Unicode.GetBytes(token)));

        var session = CursorSessionStore.TryRead(path);

        Assert.NotNull(session);
        Assert.Equal(token, session.AccessToken);
    }

    [Fact]
    public void DosyaYoksa_BelirtecYoksa_BozukBelirtec_VeTabloYoksa_Null()
    {
        Assert.Null(CursorSessionStore.TryRead(Path.Combine(_directory, "yok.vscdb")));
        Assert.Null(CursorSessionStore.TryRead(CreateDatabase(("cursorAuth/cachedEmail", "kisi@example.com"))));

        var bozuk = CreateDatabase(("cursorAuth/accessToken", "bu-bir-jwt-degil"));
        Assert.Null(CursorSessionStore.TryRead(bozuk));

        var tabloYok = Path.Combine(_directory, "bos.vscdb");
        using (var connection = new SqliteConnection($"Data Source={tabloYok};Pooling=False"))
        {
            connection.Open();
        }

        Assert.Null(CursorSessionStore.TryRead(tabloYok));
    }

    [Fact]
    public void SifreliBelirtec_Taninir_AmaOturumOlarakOkunmaz()
    {
        var sifreli = CreateDatabase(("cursorAuth/accessToken", CursorSessionStore.EncryptedTokenPrefix + "c3ludGhldGljLWNpcGhlcg=="));

        Assert.True(CursorSessionStore.HasEncryptedToken(sifreli));
        Assert.Null(CursorSessionStore.TryRead(sifreli));

        Assert.False(CursorSessionStore.HasEncryptedToken(
            CreateDatabase(("cursorAuth/accessToken", FakeJwt("github|user_ABC123", Now.AddDays(1))))));
        Assert.False(CursorSessionStore.HasEncryptedToken(CreateDatabase(("cursorAuth/accessToken", "bu-bir-jwt-degil"))));
        Assert.False(CursorSessionStore.HasEncryptedToken(CreateDatabase(("cursorAuth/cachedEmail", "kisi@example.com"))));
        Assert.False(CursorSessionStore.HasEncryptedToken(Path.Combine(_directory, "yok.vscdb")));
    }

    [Fact]
    public void Okuma_VeritabaniDegistirmez()
    {
        var path = CreateDatabase(("cursorAuth/accessToken", FakeJwt("github|user_ABC123", Now.AddDays(1))));
        var before = File.ReadAllBytes(path);

        CursorSessionStore.TryRead(path);

        Assert.Equal(before, File.ReadAllBytes(path));
    }
}
