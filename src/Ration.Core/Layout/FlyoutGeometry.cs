namespace Ration.Core.Layout;

public enum FlyoutEdge
{
    Bottom,
    Top,
    Left,
    Right,
}

/// <summary>Fiziksel piksel cinsinden dikdörtgen (sağ/alt dışlayıcı).</summary>
public readonly record struct Bounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public int CenterX => Left + Width / 2;
    public int CenterY => Top + Height / 2;
}

public readonly record struct FlyoutPlacement(int X, int Y, FlyoutEdge Edge);

/// <summary>
/// Açılır pencerenin görev çubuğu simgesine göre yerleşimi. Saf hesap: Win32 çağrısı yoktur,
/// bu yüzden her kenar ve DPI testlerle doğrulanır.
/// </summary>
public static class FlyoutGeometry
{
    public const int MarginDip = 8;

    /// <summary>Boşluk fiziksel piksele çevrilir; 200% ölçekte 8 piksel sıkışık görünüyordu.</summary>
    public static int ScaledMargin(uint dpi) => (int)Math.Round(MarginDip * (dpi == 0 ? 96 : dpi) / 96.0);

    /// <summary>Görev çubuğunun kenarı: çalışma alanının kısaldığı kenar (üst, sol, sağ, yoksa alt).</summary>
    public static FlyoutEdge DetectEdge(Bounds icon, Bounds monitor, Bounds work)
    {
        if (work.Top > monitor.Top) return FlyoutEdge.Top;
        if (work.Left > monitor.Left) return FlyoutEdge.Left;
        if (work.Right < monitor.Right) return FlyoutEdge.Right;
        return FlyoutEdge.Bottom;
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

        // Çalışma alanına sıkıştır; pencere alandan büyükse sol/üst kenar kazanır.
        x = Math.Min(x, work.Right - margin - width);
        x = Math.Max(x, work.Left + margin);
        y = Math.Min(y, work.Bottom - margin - height);
        y = Math.Max(y, work.Top + margin);

        return new FlyoutPlacement(x, y, edge);
    }
}
