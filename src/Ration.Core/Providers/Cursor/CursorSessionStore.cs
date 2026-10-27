using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Ration.Core.Providers.Cursor;

public sealed record CursorSession(
    string AccessToken,
    string UserId,
    DateTimeOffset ExpiresAt,
    string? MembershipType,
    string? SubscriptionStatus)
{
    // Süresi dolmak üzere olan belirteç gönderilmez; Ration belirteci yenilemez.
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(60);

    public bool IsUsable(DateTimeOffset now) => ExpiresAt > now + Margin;
}

/// <summary>
/// Cursor uygulamasının yerel durum veritabanından (state.vscdb, ItemTable) oturum belirtecini
/// SALT OKUNUR okur. Dosyaya yazılmaz, belirteç yenilenmez, çerez ya da tarayıcı verisine
/// dokunulmaz. Belirteç hiçbir yere yazılmaz; günlüğe de girmez.
/// </summary>
public static class CursorSessionStore
{
    private const string TokenKey = "cursorAuth/accessToken";
    private const string MembershipKey = "cursorAuth/stripeMembershipType";
    private const string StatusKey = "cursorAuth/stripeSubscriptionStatus";

    /// <summary>
    /// Cursor 3.27'den beri saklanan belirteç şifreli ve bu önekle başlıyor; düz JWT değil.
    /// </summary>
    public const string EncryptedTokenPrefix = "cursor-safe-storage:v1:";

    /// <param name="decrypt">
    /// Şifreli değeri (önek dahil) çözen platform işlevi; yoksa ya da null dönerse şifreli belirteç
    /// oturum sayılmaz. Çözülen belirteç de yine JWT olarak doğrulanır.
    /// </param>
    public static CursorSession? TryRead(string? path = null, Func<string, string?>? decrypt = null)
    {
        var values = ReadValues(path ?? KnownPaths.CursorStateDatabaseFile);
        if (values is null || !values.TryGetValue(TokenKey, out var token)) return null;

        if (token.StartsWith(EncryptedTokenPrefix, StringComparison.Ordinal))
        {
            token = Decrypt(decrypt, token);
            if (token is null) return null;
        }

        if (!TryReadClaims(token, out var userId, out var expiresAt)) return null;

        return new CursorSession(
            token,
            userId,
            expiresAt,
            values.GetValueOrDefault(MembershipKey),
            values.GetValueOrDefault(StatusKey));
    }

    /// <summary>
    /// Belirteç var ama Cursor onu şifrelemiş. "Giriş yapılmamış" ile karıştırılmasın diye ayrı
    /// sorulur; değer okunmaz, yalnızca önekine bakılır.
    /// </summary>
    public static bool HasEncryptedToken(string? path = null) =>
        ReadValues(path ?? KnownPaths.CursorStateDatabaseFile) is { } values &&
        values.TryGetValue(TokenKey, out var token) &&
        token.StartsWith(EncryptedTokenPrefix, StringComparison.Ordinal);

    private static Dictionary<string, string>? ReadValues(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 3,
            }.ToString();

            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT key, value FROM ItemTable WHERE key IN ($token, $membership, $status);";
            command.Parameters.AddWithValue("$token", TokenKey);
            command.Parameters.AddWithValue("$membership", MembershipKey);
            command.Parameters.AddWithValue("$status", StatusKey);

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (reader.IsDBNull(1)) continue;
                    var text = ReadText(reader.GetValue(1));
                    if (!string.IsNullOrWhiteSpace(text)) values[reader.GetString(0)] = text.Trim();
                }
            }

            return values;
        }
        catch (SqliteException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string? Decrypt(Func<string, string?>? decrypt, string value)
    {
        if (decrypt is null) return null;

        try
        {
            return decrypt(value)?.Trim();
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // VS Code durum veritabanı değeri metin ya da BLOB olarak tutabilir; BOM'suz ASCII'nin
    // UTF-16LE hali araya NUL baytları koyar, UTF-8 olarak okunursa belirteç bozulur.
    private static string? ReadText(object value)
    {
        if (value is string text) return text;
        if (value is not byte[] bytes || bytes.Length == 0) return null;

        var looksUtf16 = bytes.Length >= 2 && bytes.Length % 2 == 0 && bytes[1] == 0;
        return looksUtf16 ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// JWT yükünden süre sonu (exp) ve kullanıcı kimliği (sub, "github|user_..." biçiminde son
    /// parça) okunur. İmza doğrulanmaz; yalnızca bu belirteç hâlâ gönderilebilir mi diye bakılır.
    /// </summary>
    internal static bool TryReadClaims(string token, out string userId, out DateTimeOffset expiresAt)
    {
        userId = string.Empty;
        expiresAt = default;

        var parts = token.Split('.');
        if (parts.Length != 3) return false;

        try
        {
            using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (!root.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var seconds)) return false;
            if (!root.TryGetProperty("sub", out var sub) || sub.ValueKind != JsonValueKind.String) return false;

            var subject = sub.GetString();
            if (string.IsNullOrWhiteSpace(subject)) return false;

            userId = subject[(subject.LastIndexOf('|') + 1)..];
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return userId.Length > 0;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
