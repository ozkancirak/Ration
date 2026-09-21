using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using Kalan.Platform.Windows.Interop;
using WinRT.Interop;

namespace Kalan.App.Views;

/// <summary>
/// WinUI 3 pencerelerinin ekran görüntüsünü PrintWindow (ve gerekirse CopyFromScreen fallback'i)
/// ile PNG dosyasına kaydeden yardımcı sınıf.
/// </summary>
public static class WindowScreenshotHelper
{
    public static async Task<bool> CaptureWindowAsync(IntPtr hwnd, string outputPath, int delayMs = 600)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        // Render ve düzenin (XAML layout) tamamlanması için kısa bir bekleme
        if (delayMs > 0)
        {
            await Task.Delay(delayMs);
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        int width = rect.Width;
        int height = rect.Height;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        string? dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 1. Öncelik: PrintWindow (PW_RENDERFULLCONTENT)
        using var printBmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        bool printSuccess = false;

        using (var g = Graphics.FromImage(printBmp))
        {
            IntPtr hdc = g.GetHdc();
            try
            {
                printSuccess = NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        if (printSuccess && HasVisibleContent(printBmp))
        {
            printBmp.Save(outputPath, ImageFormat.Png);
            return true;
        }

        // 2. Fallback: Ekranda görünür olan pencereyi doğrudan masaüstü yüzeyinden yakala
        try
        {
            using var screenBmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(screenBmp))
            {
                g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            }

            screenBmp.Save(outputPath, ImageFormat.Png);
            return true;
        }
        catch
        {
            // Eğer CopyFromScreen de başarısız olursa ve PrintWindow en azından bir bitmap oluşturduysa onu kaydet
            printBmp.Save(outputPath, ImageFormat.Png);
            return true;
        }
    }

    public static Task<bool> CaptureWindowAsync(Microsoft.UI.Xaml.Window window, string outputPath, int delayMs = 600)
    {
        IntPtr hwnd = WindowNative.GetWindowHandle(window);
        return CaptureWindowAsync(hwnd, outputPath, delayMs);
    }

    private static bool HasVisibleContent(Bitmap bmp)
    {
        // Bitmap'in tamamen boş (tüm pikseller 0) olup olmadığını örnekleme ile denetle
        int stepX = Math.Max(1, bmp.Width / 20);
        int stepY = Math.Max(1, bmp.Height / 20);

        for (int y = 0; y < bmp.Height; y += stepY)
        {
            for (int x = 0; x < bmp.Width; x += stepX)
            {
                Color c = bmp.GetPixel(x, y);
                if (c.A > 0 && (c.R > 0 || c.G > 0 || c.B > 0))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
