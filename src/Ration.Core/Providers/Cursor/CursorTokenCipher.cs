using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ration.Core.Providers.Cursor;

/// <summary>
/// Since Cursor 3.27 the session token has been encrypted with Electron <c>safeStorage</c> and stored as
/// <c>cursor-safe-storage:v1:</c> + base64. On Windows Chromium's os_crypt layout is used:
/// <c>"v10" | nonce(12) | ciphertext | tag(16)</c>, AES-256-GCM; the key is the DPAPI-unprotected form of the
/// <c>os_crypt.encrypted_key</c> value in Cursor's <c>Local State</c> file. This class only does the pure
/// crypto part (DPAPI lives in the platform layer).
/// The format has not been verified on a real Cursor 3.27 install; a value that cannot be decrypted returns
/// null and the caller falls back to the "encrypted, cannot be read" message.
/// </summary>
public static class CursorTokenCipher
{
    private const int PrefixLength = 3;   // "v10" / "v11"
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private static readonly byte[] DpapiMarker = "DPAPI"u8.ToArray();

    /// <param name="value">The stored value, including <see cref="CursorSessionStore.EncryptedTokenPrefix"/>.</param>
    /// <param name="key">The 32-byte AES key.</param>
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
    /// Extracts the DPAPI-protected key blob from the <c>Local State</c> JSON (the "DPAPI" prefix is dropped).
    /// null if it is missing or the format differs.
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
