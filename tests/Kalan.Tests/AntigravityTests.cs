using Kalan.Core.Model;
using Kalan.Core.Providers.Antigravity;

namespace Kalan.Tests;

public sealed class AntigravityTests
{
    [Fact]
    public void PortFinder_UsesNewestHttpLine_AndIgnoresGrpc()
    {
        const string log = """
            listening on random port at 4011 for gRPC
            listening on random port at 4012 for HTTP
            listening on random port at 4013 for HTTP
            """;

        Assert.Equal(4013, AntigravityPortFinder.FindPortInLogText(log));
    }

    [Fact]
    public void Parser_MapsFractionsLabelsAndWindowLengths()
    {
        const string json = """
            {
              "groups": [
                {
                  "displayName": "Claude Sonnet",
                  "buckets": [
                    { "window": "5h", "remainingFraction": 0.25, "resetTime": "2026-09-21T12:00:00Z" },
                    { "window": "weekly", "remainingFraction": 0.80, "resetTime": "2026-09-27T12:00:00Z" },
                    { "window": "monthly", "remainingFraction": 0.50 }
                  ]
                }
              ]
            }
            """;

        var windows = AntigravityUsageParser.ParseWindows(json);

        Assert.Equal(2, windows.Count);
        Assert.Equal(WindowKind.Session, windows[0].Kind);
        Assert.Equal(75, windows[0].Percent);
        Assert.Equal(TimeSpan.FromHours(5), windows[0].WindowLength);
        Assert.Equal("Claude Sonnet", windows[0].Label);
        Assert.Equal(WindowKind.Weekly, windows[1].Kind);
        Assert.Equal(20d, windows[1].Percent, precision: 10);
        Assert.Equal(TimeSpan.FromDays(7), windows[1].WindowLength);
    }

    [Fact]
    public void Parser_MalformedOrEmptyShape_ReturnsNoWindows()
    {
        Assert.Empty(AntigravityUsageParser.ParseWindows("{\"groups\":[]}"));
        Assert.Empty(AntigravityUsageParser.ParseWindows("{\"groups\": [{\"displayName\": \"x\"}]}"));
    }
}
