using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ration.Core.Providers.Cursor;

/// <summary>
/// Cursor 3.27'den beri oturum belirtecini Electron <c>safeStorage</c> ile şifreleyip
/// <c>cursor-safe-storage:v1:</c> + base64 olarak saklıyor. Windows'ta Chromium'un os_crypt
/// düzeni kullanılır: <c>"v10" | nonce(12) | şifreli metin | etiket(16)</c>, AES-256-GCM; anahtar
/// Cursor'ın <c>Local State</c> dosyasındaki <c>os_crypt.encrypted_key</c> değerinin DPAPI ile
/// açılmış halidir. Bu sınıf yalnızca saf kripto kısmını yapar (DPAPI platform katmanında).
/// Biçim gerçek bir Cursor 3.27 kurulumunda doğrulanmadı; çözülemeyen değer null döner ve
/// çağıran "şifreli, okunamıyor" mesajına düşer.
/// </summary>
public static class CursorTokenCipher
{
    private const int PrefixLength = 3;   // "v10" / "v11"
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private static readonly byte[] DpapiMarker = "DPAPI"u8.ToArray();

    /// <param name="value">Saklanan değer, <see cref="CursorSessionStore.EncryptedTokenPrefix"/> dahil.</param>
    /// <param name="key">32 baytlık AES anahtarı.</param>
    public static string? TryDecrypt(string value, byte[] key)
    {
        if (!value.StartsWith(CursorSessionStore.EncryptedTokenPrefix, StringComparison.Ordinal) || key.Length != 32)
        {
            return null;
        }

        try
        {
            var payload = Convert.FromBase64String(value[CursorSessionStore.EncryptedTokenPrefix.Length..]);
            if (payload.Length < PrefixLength + NonceLength + TagLength ||
                payload[0] != (byte)'v' ||
                payload[1] != (byte)'1' ||
                payload[2] is not ((byte)'0' or (byte)'1'))
            {
                return null;
            }

            var nonce = payload.AsSpan(PrefixLength, NonceLength);
            var cipherLength = payload.Length - PrefixLength - NonceLength - TagLength;
            var cipher = payload.AsSpan(PrefixLength + NonceLength, cipherLength);
            var tag = payload.AsSpan(payload.Length - TagLength, TagLength);

            var plain = new byte[cipherLength];
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain).Trim();
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>Local State</c> JSON'undan DPAPI ile korunmuş anahtar bloğunu çıkarır ("DPAPI" öneki atılır).
    /// Yoksa ya da biçim farklıysa null.
    /// </summary>
    public static byte[]? TryReadProtectedKey(string localStateJson)
    {
        try
        {
            using var document = JsonDocument.Parse(localStateJson);
            if (!document.RootElement.TryGetProperty("os_crypt", out var osCrypt) ||
                osCrypt.ValueKind != JsonValueKind.Object ||
                !osCrypt.TryGetProperty("encrypted_key", out var encoded) ||
                encoded.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var blob = Convert.FromBase64String(encoded.GetString()!);
            return blob.Length > DpapiMarker.Length && blob.AsSpan(0, DpapiMarker.Length).SequenceEqual(DpapiMarker)
                ? blob[DpapiMarker.Length..]
                : null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }
}
