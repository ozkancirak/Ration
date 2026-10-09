using Ration.Core.Layout;

namespace Ration.Tests;

public class FlyoutGeometryTests
{
    // 1920x1080 ekran, alt görev çubuğu 48 px.
    private static readonly Bounds Monitor = new(0, 0, 1920, 1080);
    private static readonly Bounds WorkBottom = new(0, 0, 1920, 1032);

    private static Bounds IconAt(int centerX, int centerY, int size = 24) =>
        new(centerX - size / 2, centerY - size / 2, centerX + size / 2, centerY + size / 2);

    [Theory]
    [InlineData(96u, 8)]
    [InlineData(120u, 10)]
    [InlineData(144u, 12)]
    [InlineData(168u, 14)]
    [InlineData(192u, 16)]
    [InlineData(0u, 8)]
    public void ScaledMargin_FollowsDpi(uint dpi, int expected)
    {
        Assert.Equal(expected, FlyoutGeometry.ScaledMargin(dpi));
    }

    [Theory]
    [InlineData(96u)]
    [InlineData(120u)]
    [InlineData(144u)]
    [InlineData(192u)]
    public void BottomTaskbar_PlacesAboveIconWithScaledGap(uint dpi)
    {
        var icon = IconAt(1000, 1056);
        var margin = FlyoutGeometry.ScaledMargin(dpi);
        var width = (int)(380 * dpi / 96.0);
        var height = (int)(420 * dpi / 96.0);

        var placement = FlyoutGeometry.Place(icon, Monitor, WorkBottom, width, height, margin);

        Assert.Equal(FlyoutEdge.Bottom, placement.Edge);
        // Simge görev çubuğunun içinde; pencere çubuğun üst kenarından `margin` kadar yukarıda durur.
        Assert.Equal(WorkBottom.Bottom - margin - height, placement.Y);
        Assert.Equal(icon.CenterX - width / 2, placement.X);
    }

    [Fact]
    public void IconNearRightEdge_IsClampedInsideWorkArea()
    {
        var icon = IconAt(1900, 1056);
        var placement = FlyoutGeometry.Place(icon, Monitor, WorkBottom, 380, 420, 8);

        Assert.Equal(WorkBottom.Right - 8 - 380, placement.X);
    }

    [Fact]
    public void TallWindow_IsPinnedToTopOfWorkArea()
    {
        var icon = IconAt(1700, 1056);
        var placement = FlyoutGeometry.Place(icon, Monitor, WorkBottom, 380, 2000, 8);

        Assert.Equal(WorkBottom.Top + 8, placement.Y);
    }
}
