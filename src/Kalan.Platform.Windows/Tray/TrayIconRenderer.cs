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
    public static Bitmap CreateGaugeBitmap(double? percentage, bool isLightTheme, int size = 16)
    {
        bool hasData = percentage is double value
            && !double.IsNaN(value)
            && !double.IsInfinity(value);
        double usage = hasData ? Math.Clamp(percentage!.Value, 0, 100) : 0;
        // Çağıran SM_CXSMICON değerini aynen verir; küçük DPI kutusunu 16'ya
        // zorlamak, Shell'in istediği ikon alanını yeniden büyütüp taşırır.
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
                // Çerçeve her zaman görev çubuğu zeminine zıt monokrom renktir
                borderColor = isLightTheme
                    ? Color.FromArgb(255, 0, 0, 0)
                    : Color.FromArgb(255, 255, 255, 255);

                if (hasData && usage >= 90)
                {
                    // Kritik (%90+): dolgu kırmızı. Kontur monokrom kalır.
                    fillColor = isLightTheme
                        ? Color.FromArgb(220, 38, 38)   // #DC2626
                        : Color.FromArgb(239, 68, 68);  // #EF4444
                }
                else
                {
                    // Dolgu konturdan ayrı tonda kalır; iki seviye 16px'te bile seçilir.
                    fillColor = isLightTheme
                        ? Color.FromArgb(120, 0, 0, 0)
                        : Color.FromArgb(170, 255, 255, 255);
                }
            }

            // DPI ölçeklemesi ve piksel sınırları
            float scale = size / 16.0f;
            int borderWidth = Math.Max(1, (int)Math.Floor(scale)); // 16px -> 1px, 32px -> 2px

            // GetSystemMetricsForDpi ile gelen kutuyu optik olarak doldur; önceki 8px
            // gövde tepsi ikonunun yalnızca yarısını kullanıyordu.
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
                // Dolgu KALAN kotadır: %0 kullanım = dolu tank, %100 = boş tank.
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
                // Veri yok: dolgu yok; yalnızca ortada ince bir bilinmiyor işareti.
                int dashWidth = Math.Max(borderWidth * 2, innerW / 2);
                int dashX = tankX + (tankW - dashWidth) / 2;
                int dashY = tankY + (tankH - borderWidth) / 2;
                using var dashBrush = new SolidBrush(borderColor);
                g.FillRectangle(dashBrush, dashX, dashY, dashWidth, borderWidth);
            }

            // Konturu en son çiz: dolgu hiçbir zaman 1px çerçeveyi yutamaz.
            using var borderPen = new Pen(borderColor, borderWidth);
            g.DrawRectangle(borderPen, tankX, tankY, tankW - 1, tankH - 1);
        }

        return bitmap;
    }

    public static IntPtr CreateGaugeIconHandle(double? percentage, bool isLightTheme, int size = 16)
    {
        using var bitmap = CreateGaugeBitmap(percentage, isLightTheme, size);
        return bitmap.GetHicon();
    }

    public static Icon CreateGaugeIcon(double? percentage, bool isLightTheme, int size = 16)
    {
        IntPtr hIcon = CreateGaugeIconHandle(percentage, isLightTheme, size);
        return Icon.FromHandle(hIcon);
    }
}
