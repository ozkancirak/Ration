namespace Ration.Core.Updates;

public static class UpdateLinks
{
    /// <summary>
    /// The release notes page: the release tag on GitHub (v0.3.0). If the repo address is not https
    /// (local folder, development source) there is no link.
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
