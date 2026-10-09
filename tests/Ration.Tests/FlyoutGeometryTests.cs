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

    [Fact]
    public void TopTaskbar_PlacesBelowIcon()
    {
        var work = new Bounds(0, 48, 1920, 1080);
        var icon = IconAt(1700, 24);

        var placement = FlyoutGeometry.Place(icon, Monitor, work, 380, 420, 8);

        Assert.Equal(FlyoutEdge.Top, placement.Edge);
        Assert.Equal(work.Top + 8, placement.Y);
        Assert.Equal(icon.CenterX - 190, placement.X);
    }

    [Fact]
    public void LeftTaskbar_PlacesToTheRightAndCentersOnIcon()
    {
        var work = new Bounds(64, 0, 1920, 1080);
        var icon = IconAt(32, 600);

        var placement = FlyoutGeometry.Place(icon, Monitor, work, 380, 420, 8);

        Assert.Equal(FlyoutEdge.Left, placement.Edge);
        Assert.Equal(work.Left + 8, placement.X);
        Assert.Equal(icon.CenterY - 210, placement.Y);
    }

    [Fact]
    public void RightTaskbar_PlacesToTheLeftAndCentersOnIcon()
    {
        var work = new Bounds(0, 0, 1856, 1080);
        var icon = IconAt(1888, 600);

        var placement = FlyoutGeometry.Place(icon, Monitor, work, 380, 420, 8);

        Assert.Equal(FlyoutEdge.Right, placement.Edge);
        Assert.Equal(work.Right - 8 - 380, placement.X);
        Assert.Equal(icon.CenterY - 210, placement.Y);
    }

    [Theory]
    [InlineData(1700, 1056, FlyoutEdge.Bottom)]
    [InlineData(1700, 24, FlyoutEdge.Top)]
    [InlineData(24, 600, FlyoutEdge.Left)]
    [InlineData(1896, 600, FlyoutEdge.Right)]
    public void AutoHiddenTaskbar_UsesTheEdgeNearestTheIcon(int x, int y, FlyoutEdge expected)
    {
        // Otomatik gizlemede çalışma alanı ekranın tamamıdır; kenar simgenin yerinden bulunur.
        Assert.Equal(expected, FlyoutGeometry.DetectEdge(IconAt(x, y), Monitor, Monitor));
    }

    [Fact]
    public void SeveralShrunkEdges_PickTheOneNearestTheIcon()
    {
        // Alt görev çubuğu + sağda yan çubuk: iki kenar da kısalmış.
        var work = new Bounds(0, 0, 1600, 1032);

        Assert.Equal(FlyoutEdge.Bottom, FlyoutGeometry.DetectEdge(IconAt(1000, 1056), Monitor, work));
        Assert.Equal(FlyoutEdge.Right, FlyoutGeometry.DetectEdge(IconAt(1910, 400), Monitor, work));
    }

    [Theory]
    [InlineData(1920, 0)]      // birincil ekranın sağında
    [InlineData(-2560, 0)]     // solunda (negatif koordinat)
    [InlineData(300, -1080)]   // üstünde
    public void SecondaryMonitor_PlacesInsideItsOwnWorkArea(int originX, int originY)
    {
        // 150% ölçekli ikinci ekran: 2560x1440 fiziksel piksel, alt görev çubuğu 72 px.
        var monitor = new Bounds(originX, originY, originX + 2560, originY + 1440);
        var work = new Bounds(originX, originY, originX + 2560, originY + 1368);
        var icon = IconAt(originX + 2400, originY + 1404, 36);
        var margin = FlyoutGeometry.ScaledMargin(144);

        var placement = FlyoutGeometry.Place(icon, monitor, work, 570, 630, margin);

        Assert.Equal(FlyoutEdge.Bottom, placement.Edge);
        Assert.InRange(placement.X, work.Left + margin, work.Right - margin - 570);
        Assert.Equal(work.Bottom - margin - 630, placement.Y);
    }
}
