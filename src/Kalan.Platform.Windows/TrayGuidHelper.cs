using System.Security.Cryptography;
using System.Text;

namespace Kalan.Platform.Windows;

public static class TrayGuidHelper
{
    public static Guid FromProcessPath(string? customPath = null)
    {
        string path = customPath
            ?? Environment.ProcessPath
            ?? AppContext.BaseDirectory;

        string normalized = Path.GetFullPath(path).Trim().ToLowerInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return new Guid(hash.AsSpan(0, 16));
    }
}
