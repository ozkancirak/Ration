using Ration.Core.Updates;

namespace Ration.Tests;

public class UpdateLinksTests
{
    [Theory]
    [InlineData("https://github.com/ozkancirak/Ration", "0.3.0", "https://github.com/ozkancirak/Ration/releases/tag/v0.3.0")]
    [InlineData("https://github.com/ozkancirak/Ration/", "0.3.0", "https://github.com/ozkancirak/Ration/releases/tag/v0.3.0")]
    [InlineData("https://github.com/ozkancirak/Ration", "v0.3.0", "https://github.com/ozkancirak/Ration/releases/tag/v0.3.0")]
    [InlineData("https://github.com/ozkancirak/Ration", "0.4.0-beta.1", "https://github.com/ozkancirak/Ration/releases/tag/v0.4.0-beta.1")]
    public void ReleaseNotes_PointsAtTheVersionTag(string repository, string version, string expected)
    {
        Assert.Equal(expected, UpdateLinks.ReleaseNotes(repository, version)?.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://github.com/ozkancirak/Ration", null)]
    [InlineData("https://github.com/ozkancirak/Ration", "")]
    [InlineData(@"C:eedsation", "0.3.0")]
    [InlineData("http://example.test/ration", "0.3.0")]
    [InlineData("not a url", "0.3.0")]
    public void ReleaseNotes_IsMissingWithoutAVersionOrAnHttpsRepository(string repository, string? version)
    {
        Assert.Null(UpdateLinks.ReleaseNotes(repository, version));
    }
}
