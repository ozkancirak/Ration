using System.Security.Cryptography;
using Ration.Core.Providers;
using Ration.Core.Providers.Cursor;

namespace Ration.Platform.Windows.Providers;

/// <summary>
/// Decrypts the Cursor 3.27+ encrypted session token: opens the key in <c>Local State</c> with Windows
/// DPAPI (this user only), then decrypts the token with <see cref="CursorTokenCipher"/>.
/// Nothing is written; the key and token do not go to the log and are not kept in memory persistently.
/// </summary>
public static class CursorTokenProtector
{
    public static string? TryDecrypt(string encryptedValue) =>
        TryDecrypt(encryptedValue, KnownPaths.CursorLocalStateFile);

    public static string? TryDecrypt(string encryptedValue, string localStatePath)
    {
        if (!File.Exists(localStatePath)) return null;

        try
        {
            var protectedKey = CursorTokenCipher.TryReadProtectedKey(File.ReadAllText(localStatePath));
            if (protectedKey is null) return null;

            var key = ProtectedData.Unprotect(protectedKey, null, DataProtectionScope.CurrentUser);
            try
            {
                return CursorTokenCipher.TryDecrypt(encryptedValue, key);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
