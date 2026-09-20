using System.Drawing;
using System.Drawing.Drawing2D;
using Kalan.Platform.Windows.Theme;

namespace Kalan.Platform.Windows.Tray;

/// <summary>
/// Windows 11 sistem tepsisi için piksel hizalı dikey tank/gauge ikonu üreticisi.
/// Windows 11 WiFi, Pil ve Ses ikonlarının hollow/line-art tasarım diliyle tam uyumludur:
/// - Katı blok yerine içi boş dikey çerçeve (outline) ve aşağıdan yukarıya doluluk seviyesi.
/// - 16px'te 1px çizgi, 32px'te 2px çizgi (DPI ölçekleme).
/// - SmoothingMode.None ile tam piksel kenar keskinliği (bulanıklaşma önleyici).
/// - Renk kuralları:
///     Normal (%0-74): Monokrom (Koyu görev çubuğunda beyaz, açık görev çubuğunda siyah).
///     Uyarı (%75-89): Caution / Kehribar (#CA8A04 / #F59E0B).
///     Kritik (%90+): Critical / Kırmızı (#DC2626 / #EF4444).
///     Yüksek kontrast: Sistem renkleri (SystemColors.WindowText).
///     Tray ikonunda ASLA accent rengi kullanılmaz.
/// </summary>
public static class TrayIconRenderer
{
    public static Bitmap CreateGaugeBitmap(double percentage, bool isLightTheme, int size = 16)
    {
        percentage = Math.Clamp(percentage, 0, 100);
        size = Math.Max(16, size);

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
                // Çerçeve her zaman görev çubuğu zeminine zıt monokrom renktir
                borderColor = isLightTheme
                    ? Color.FromArgb(255, 0, 0, 0)
                    : Color.FromArgb(255, 255, 255, 255);

                if (percentage >= 90)
                {
                    // Kritik (%90+)
                    fillColor = isLightTheme
                        ? Color.FromArgb(220, 38, 38)   // #DC2626
                        : Color.FromArgb(239, 68, 68);  // #EF4444
                }
                else if (percentage >= 75)
                {
                    // Uyarı (%75-89)
                    fillColor = isLightTheme
                        ? Color.FromArgb(202, 138, 4)   // #CA8A04
                        : Color.FromArgb(245, 158, 11);  // #F59E0B
                }
                else
                {
                    // Normal (%0-74): Monokrom
                    fillColor = borderColor;
                }
            }

            // DPI ölçeklemesi ve piksel sınırları
            float scale = size / 16.0f;
            int borderWidth = Math.Max(1, (int)Math.Floor(scale)); // 16px -> 1px, 32px -> 2px

            // Dikey tank geometrisi (16px'te 8x12 piksel)
            int tankW = (int)Math.Round(8 * scale);
            int tankH = (int)Math.Round(12 * scale);

            int tankX = (size - tankW) / 2;
            int tankY = (size - tankH) / 2;

            // 1. İçi boş dış çerçeveyi çiz (outline)
            using (var borderPen = new Pen(borderColor, borderWidth))
            {
                g.DrawRectangle(borderPen, tankX, tankY, tankW - 1, tankH - 1);
            }

            // 2. İç hazne doluluğunu aşağıdan yukarıya doğru çiz (tank dolumu)
            int innerX = tankX + borderWidth;
            int innerY = tankY + borderWidth;
            int innerW = tankW - (2 * borderWidth);
            int innerH = tankH - (2 * borderWidth);

            if (percentage > 0 && innerW > 0 && innerH > 0)
            {
                int fillH = (int)Math.Round((percentage / 100.0) * innerH);
                if (fillH == 0) fillH = 1;
                fillH = Math.Min(fillH, innerH);

                int fillY = innerY + innerH - fillH;

                using (var fillBrush = new SolidBrush(fillColor))
                {
                    g.FillRectangle(fillBrush, innerX, fillY, innerW, fillH);
                }
            }
        }

        return bitmap;
    }

    public static IntPtr CreateGaugeIconHandle(double percentage, bool isLightTheme, int size = 16)
    {
        using var bitmap = CreateGaugeBitmap(percentage, isLightTheme, size);
        return bitmap.GetHicon();
    }

    public static Icon CreateGaugeIcon(double percentage, bool isLightTheme, int size = 16)
    {
        IntPtr hIcon = CreateGaugeIconHandle(percentage, isLightTheme, size);
        return Icon.FromHandle(hIcon);
    }
}
