using System.Drawing;
using System.Drawing.Drawing2D;
using Kalan.Platform.Windows.Theme;

namespace Kalan.Platform.Windows.Tray;

/// <summary>
/// Windows 11 sistem tepsisi için piksel hizalı dikey tank/gauge ikonu üreticisi.
/// Windows 11 WiFi, Pil ve Ses ikonlarının hollow/line-art tasarım diliyle tam uyumludur:
/// - Katı blok yerine içi boş dikey çerçeve (outline) ve aşağıdan yukarıya doluluk seviyesi.
/// - Dolgu KALAN kotadır: (100 - Percent) / 100. %100 dolu = boş tank.
/// - 16px'te 1px çizgi, 32px'te 2px çizgi (DPI ölçekleme).
/// - SmoothingMode.None ile tam piksel kenar keskinliği (bulanıklaşma önleyici).
/// - Renk kuralları (flyout ölçerindeki üç eşik buraya girmez):
///     Normal: Monokrom (koyu zeminde beyaz, açık zeminde siyah) — kontur ve dolgu tek renk.
///     Percent >= 90: dolgu kırmızı (kritik). Kontur her zaman monokrom kalır.
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
                    // Kritik (%90+): dolgu kırmızı. Kontur monokrom kalır.
                    fillColor = isLightTheme
                        ? Color.FromArgb(220, 38, 38)   // #DC2626
                        : Color.FromArgb(239, 68, 68);  // #EF4444
                }
                else
                {
                    // Normal: konturla aynı tek renk (monokrom). %75 amber'i yok.
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

            // 2. İç hazne doluluğunu aşağıdan yukarıya doğru çiz.
            // Dolgu KALAN kotadır: %0 kullanım = dolu tank, %100 = boş tank.
            double remainingFraction = (100.0 - percentage) / 100.0;

            int innerX = tankX + borderWidth;
            int innerY = tankY + borderWidth;
            int innerW = tankW - (2 * borderWidth);
            int innerH = tankH - (2 * borderWidth);

            if (remainingFraction > 0 && innerW > 0 && innerH > 0)
            {
                int fillH = (int)Math.Round(remainingFraction * innerH);
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
