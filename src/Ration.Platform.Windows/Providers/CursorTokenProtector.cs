using System.Security.Cryptography;
using Ration.Core.Providers;
using Ration.Core.Providers.Cursor;

namespace Ration.Platform.Windows.Providers;

/// <summary>
/// Cursor 3.27+ şifreli oturum belirtecini çözer: <c>Local State</c> içindeki anahtarı Windows
/// DPAPI ile (yalnızca bu kullanıcı) açar, sonra <see cref="CursorTokenCipher"/> ile belirteci
/// çözer. Hiçbir şey yazılmaz; anahtar ve belirteç günlüğe girmez, bellekte kalıcı tutulmaz.
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
