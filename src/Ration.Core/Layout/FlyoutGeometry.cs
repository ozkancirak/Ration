namespace Ration.Core.Layout;

public enum FlyoutEdge
{
    Bottom,
    Top,
    Left,
    Right,
}

/// <summary>Rectangle in physical pixels (right/bottom exclusive).</summary>
public readonly record struct Bounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public int CenterX => Left + Width / 2;
    public int CenterY => Top + Height / 2;
}

public readonly record struct FlyoutPlacement(int X, int Y, FlyoutEdge Edge);

/// <summary>
/// Placement of the flyout relative to the taskbar icon. Pure calculation: there is no Win32 call,
/// so every edge and DPI is verified by tests.
/// </summary>
public static class FlyoutGeometry
{
    public const int MarginDip = 8;

    /// <summary>The gap is converted to physical pixels; 8 pixels looked cramped at 200% scale.</summary>
    public static int ScaledMargin(uint dpi) => (int)Math.Round(MarginDip * (dpi == 0 ? 96 : dpi) / 96.0);

    /// <summary>
    /// Finds which edge the taskbar is on. If the work area is shortened from one edge,
    /// that is the edge; if several edges are shortened (a side bar, a docked window), the one closest
    /// to the icon is chosen. If none is shortened (auto-hide), the edge closest to the icon again.
    /// </summary>
    public static FlyoutEdge DetectEdge(Bounds icon, Bounds monitor, Bounds work)
    {
        var shrunk = new List<FlyoutEdge>(4);
        if (work.Bottom < monitor.Bottom) shrunk.Add(FlyoutEdge.Bottom);
        if (work.Top > monitor.Top) shrunk.Add(FlyoutEdge.Top);
        if (work.Left > monitor.Left) shrunk.Add(FlyoutEdge.Left);
        if (work.Right < monitor.Right) shrunk.Add(FlyoutEdge.Right);
        if (shrunk.Count == 1) return shrunk[0];

        var candidates = shrunk.Count > 1
            ? shrunk
            : [FlyoutEdge.Bottom, FlyoutEdge.Top, FlyoutEdge.Left, FlyoutEdge.Right];

        // On a tie the first in the list (Bottom takes priority) wins.
        var best = candidates[0];
        var bestDistance = int.MaxValue;
        foreach (var edge in candidates)
        {
            var distance = edge switch
            {
                FlyoutEdge.Bottom => Math.Abs(monitor.Bottom - icon.CenterY),
                FlyoutEdge.Top => Math.Abs(icon.CenterY - monitor.Top),
                FlyoutEdge.Left => Math.Abs(icon.CenterX - monitor.Left),
                _ => Math.Abs(monitor.Right - icon.CenterX),
            };
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = edge;
            }
        }

        return best;
    }

    public static FlyoutPlacement Place(Bounds icon, Bounds monitor, Bounds work, int width, int height, int margin)
    {
        var edge = DetectEdge(icon, monitor, work);
        int x = icon.CenterX - width / 2;
        int y = icon.CenterY - height / 2;

        switch (edge)
        {
            case FlyoutEdge.Bottom: y = icon.Top - height - margin; break;
            case FlyoutEdge.Top: y = icon.Bottom + margin; break;
            case FlyoutEdge.Left: x = icon.Right + margin; break;
            case FlyoutEdge.Right: x = icon.Left - width - margin; break;
        }

        // Clamp to the work area; if the window is larger than the area, the left/top edge wins.
        x = Math.Min(x, work.Right - margin - width);
        x = Math.Max(x, work.Left + margin);
        y = Math.Min(y, work.Bottom - margin - height);
        y = Math.Max(y, work.Top + margin);

        return new FlyoutPlacement(x, y, edge);
    }
}
