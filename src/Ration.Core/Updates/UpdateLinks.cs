namespace Ration.Core.Updates;

public static class UpdateLinks
{
    /// <summary>
    /// Sürüm notlarının sayfası: GitHub'daki sürüm etiketi (v0.3.0). Depo adresi https değilse
    /// (yerel klasör, geliştirme kaynağı) bağlantı yoktur.
    /// </summary>
    public static Uri? ReleaseNotes(string repositoryUrl, string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        if (!Uri.TryCreate(repositoryUrl.Trim().TrimEnd('/'), UriKind.Absolute, out var repository) ||
            repository.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var tag = version.Trim().StartsWith('v') ? version.Trim() : "v" + version.Trim();
        return new Uri($"{repository.AbsoluteUri.TrimEnd('/')}/releases/tag/{Uri.EscapeDataString(tag)}");
    }
}
