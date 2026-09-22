using System.Drawing;
using Windows.UI.ViewManagement;

namespace Ration.Platform.Windows.Theme;

/// <summary>
/// Windows sistem accent rengi ve yüksek kontrast durumunun tek kaynağı.
/// Sabit renk İÇERMEZ; tüm değerler Windows.UI.ViewManagement.UISettings veya SystemColors'tan dinamik okunur.
/// </summary>
public static class SystemAccent
{
    private static readonly UISettings _uiSettings = new();
    private static readonly AccessibilitySettings _accessibilitySettings = new();

    static SystemAccent()
    {
        try
        {
            // Kanal 1: UISettings.ColorValuesChanged (unpackaged uygulamalarda tek başına yetersiz kalabilir)
            _uiSettings.ColorValuesChanged += (s, e) =>
            {
                WindowsThemeListener.NotifyAccentChanged();
            };
        }
        catch
        {
            // Unpackaged app fallback
        }
    }

    /// <summary>
    /// Yüksek kontrast modu aktif mi? Bu modda accent rengi KULLANILMAZ.
    /// Tray ikonu ve ölçerler SystemColors'tan gelen renkleri kullanmalıdır.
    /// </summary>
    public static bool IsHighContrast
    {
        get
        {
            try
            {
                return _accessibilitySettings.HighContrast;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Kullanıcının ana Windows accent rengi (UIColorType.Accent).
    /// Yüksek kontrast modunda SystemColors.Highlight döner.
    /// </summary>
    public static Color GetAccent()
    {
        if (IsHighContrast)
        {
            return SystemColors.Highlight;
        }

        try
        {
            var c = _uiSettings.GetColorValue(UIColorType.Accent);
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        }
        catch
        {
            return SystemColors.Highlight;
        }
    }

    /// <summary>
    /// Açık tema varyantı (UIColorType.AccentLight1).
    /// </summary>
    public static Color GetAccentLight1()
    {
        if (IsHighContrast)
        {
            return SystemColors.Highlight;
        }

        try
        {
            var c = _uiSettings.GetColorValue(UIColorType.AccentLight1);
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        }
        catch
        {
            return GetAccent();
        }
    }

    /// <summary>
    /// Koyu tema varyantı (UIColorType.AccentDark1).
    /// </summary>
    public static Color GetAccentDark1()
    {
        if (IsHighContrast)
        {
            return SystemColors.Highlight;
        }

        try
        {
            var c = _uiSettings.GetColorValue(UIColorType.AccentDark1);
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        }
        catch
        {
            return GetAccent();
        }
    }
}
