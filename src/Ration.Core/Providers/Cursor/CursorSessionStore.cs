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
    // A token that is about to expire is not sent; Ration does not refresh the token.
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(60);

    public bool IsUsable(DateTimeOffset now) => ExpiresAt > now + Margin;
}

/// <summary>
/// Reads the session token READ-ONLY from the Cursor app's local state database (state.vscdb, ItemTable).
/// Nothing is written to the file, the token is not refreshed, and cookies or browser data are not touched.
/// The token is written nowhere and does not go into the log.
/// </summary>
public static class CursorSessionStore
{
    private const string TokenKey = "cursorAuth/accessToken";
    private const string MembershipKey = "cursorAuth/stripeMembershipType";
    private const string StatusKey = "cursorAuth/stripeSubscriptionStatus";

    /// <summary>
    /// Since Cursor 3.27 the stored token is encrypted and starts with this prefix; it is not a plain JWT.
    /// </summary>
    public const string EncryptedTokenPrefix = "cursor-safe-storage:v1:";

    /// <param name="decrypt">
    /// Platform function that decrypts the encrypted value (prefix included); if it is missing or returns
    /// null, the encrypted token does not count as a session. A decrypted token is still validated as a JWT.
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
    /// The token exists but Cursor has encrypted it. Asked separately so that it is not confused with
    /// "not signed in"; the value is not read, only its prefix is checked.
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

    // The VS Code state database may store the value as text or as a BLOB; the UTF-16LE form of BOM-less
    // ASCII puts NUL bytes in between, and if it is read as UTF-8 the token gets corrupted.
    private static string? ReadText(object value)
    {
        if (value is string text) return text;
        if (value is not byte[] bytes || bytes.Length == 0) return null;

        var looksUtf16 = bytes.Length >= 2 && bytes.Length % 2 == 0 && bytes[1] == 0;
        return looksUtf16 ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// The expiry (exp) and the user id (sub, last part of the "github|user_..." form) are read from the JWT
    /// payload. The signature is not verified; it only checks whether this token can still be sent.
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
