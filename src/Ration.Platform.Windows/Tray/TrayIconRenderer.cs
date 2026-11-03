using System.Drawing;
using System.Drawing.Drawing2D;
using Ration.Platform.Windows.Theme;

namespace Ration.Platform.Windows.Tray;

/// <summary>
/// Pixel-aligned vertical tank/gauge icon generator for the Windows 11 system tray.
/// Fully consistent with the hollow/line-art design language of the Windows 11 WiFi, Battery and Volume icons:
/// - A hollow vertical frame (outline) instead of a solid block, with a fill level from bottom to top.
/// - The fill is the usable quota: (100 - Percent) / 100. 100% usage = empty tank.
/// - 1px line at 16px, 2px line at 32px (DPI scaling).
/// - SmoothingMode.None for exact pixel edge sharpness (prevents blurring).
/// - Color rules (the three thresholds of the flyout meter do not apply here):
///     Normal: Monochrome (white on a dark background, black on a light one) — outline and fill are one color.
///     Percent >= 90: the fill is red (critical). The outline always stays monochrome.
///     High contrast: System colors (SystemColors.WindowText).
///     The accent color is NEVER used in the tray icon.
/// </summary>
public static class TrayIconRenderer
{
    /// <param name="stale">The percentage is the last good result after a failed refresh: the whole icon is drawn faded.</param>
    public static Bitmap CreateGaugeBitmap(double? percentage, bool isLightTheme, int size = 16, bool stale = false)
    {
        bool hasData = percentage is double value
            && !double.IsNaN(value)
            && !double.IsInfinity(value);
        double usage = hasData ? Math.Clamp(percentage!.Value, 0, 100) : 0;
        // The caller passes the SM_CXSMICON value as is; forcing the small DPI box to 16 would again enlarge
        // the icon area the shell asks for and overflow it.
        size = Math.Max(8, size);

        var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.None;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.Clear(Color.Transparent);

            bool isHighContrast = SystemAccent.IsHighContrast;

            Color borderColor;
            Color fillColor;

            if (isHighContrast)
            {
                borderColor = SystemColors.WindowText;
                fillColor = SystemColors.WindowText;
            }
            else
            {
                // The frame is always a monochrome color contrasting with the taskbar background
                borderColor = isLightTheme
                    ? Color.FromArgb(255, 0, 0, 0)
                    : Color.FromArgb(255, 255, 255, 255);

                if (hasData && usage >= 90)
                {
                    // Critical (90%+): the fill is red. The outline stays monochrome.
                    fillColor = isLightTheme
                        ? Color.FromArgb(220, 38, 38)   // #DC2626
                        : Color.FromArgb(239, 68, 68);  // #EF4444
                }
                else
                {
                    // The fill stays a different shade from the outline; the two levels are distinguishable even at 16px.
                    fillColor = isLightTheme
                        ? Color.FromArgb(120, 0, 0, 0)
                        : Color.FromArgb(170, 255, 255, 255);
                }
            }

            if (stale && !isHighContrast)
            {
                // Old data must not pass for a fresh reading. High contrast keeps the system colors.
                borderColor = Faded(borderColor);
                fillColor = Faded(fillColor);
            }

            // DPI scaling and pixel boundaries
            float scale = size / 16.0f;
            int borderWidth = Math.Max(1, (int)Math.Floor(scale)); // 16px -> 1px, 32px -> 2px

            // Fill the box coming from GetSystemMetricsForDpi optically; the previous 8px
            // body used only half of the tray icon.
            int edgeInset = Math.Max(borderWidth, (int)Math.Round(size / 16.0f));
            int tankW = Math.Max(2 * borderWidth + 1, size - (2 * edgeInset));
            int tankH = Math.Max(2 * borderWidth + 1, size - (2 * edgeInset));

            int tankX = (size - tankW) / 2;
            int tankY = (size - tankH) / 2;

            int innerX = tankX + borderWidth;
            int innerY = tankY + borderWidth;
            int innerW = tankW - (2 * borderWidth);
            int innerH = tankH - (2 * borderWidth);

            if (hasData && innerW > 0 && innerH > 0)
            {
                // The fill is the usable quota: 0% usage = full tank, 100% = empty tank.
                double remainingFraction = (100.0 - usage) / 100.0;
                int fillH = (int)Math.Round(remainingFraction * innerH);
                if (fillH == 0 && remainingFraction > 0) fillH = 1;
                fillH = Math.Min(fillH, innerH);

                int fillY = innerY + innerH - fillH;

                if (fillH > 0)
                {
                    using var fillBrush = new SolidBrush(fillColor);
                    g.FillRectangle(fillBrush, innerX, fillY, innerW, fillH);
                }
            }
            else if (!hasData && innerW > 0 && innerH > 0)
            {
                // No data: no fill; only a thin unknown marker in the middle.
                int dashWidth = Math.Max(borderWidth * 2, innerW / 2);
                int dashX = tankX + (tankW - dashWidth) / 2;
                int dashY = tankY + (tankH - borderWidth) / 2;
                using var dashBrush = new SolidBrush(borderColor);
                g.FillRectangle(dashBrush, dashX, dashY, dashWidth, borderWidth);
            }

            // Draw the outline last: the fill can never swallow the frame. Since the pen centers the line
            // and spills to one side at 2px and above, the frame is drawn with exact-pixel rectangles.
            using var borderBrush = new SolidBrush(borderColor);
            g.FillRectangle(borderBrush, tankX, tankY, tankW, borderWidth);
            g.FillRectangle(borderBrush, tankX, tankY + tankH - borderWidth, tankW, borderWidth);
            g.FillRectangle(borderBrush, tankX, tankY, borderWidth, tankH);
            g.FillRectangle(borderBrush, tankX + tankW - borderWidth, tankY, borderWidth, tankH);
        }

        return bitmap;
    }

    private static Color Faded(Color color) => Color.FromArgb(color.A * 45 / 100, color);

    public static IntPtr CreateGaugeIconHandle(double? percentage, bool isLightTheme, int size = 16, bool stale = false)
    {
        using var bitmap = CreateGaugeBitmap(percentage, isLightTheme, size, stale);
        return bitmap.GetHicon();
    }

    public static Icon CreateGaugeIcon(double? percentage, bool isLightTheme, int size = 16)
    {
        IntPtr hIcon = CreateGaugeIconHandle(percentage, isLightTheme, size);
        return Icon.FromHandle(hIcon);
    }
}
