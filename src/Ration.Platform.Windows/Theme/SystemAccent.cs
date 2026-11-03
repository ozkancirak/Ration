using System.Drawing;
using Windows.UI.ViewManagement;

namespace Ration.Platform.Windows.Theme;

/// <summary>
/// The single source of the Windows system accent color and the high contrast state.
/// It contains NO fixed color; all values are read dynamically from Windows.UI.ViewManagement.UISettings or SystemColors.
/// </summary>
public static class SystemAccent
{
    private static readonly UISettings _uiSettings = new();
    private static readonly AccessibilitySettings _accessibilitySettings = new();

    static SystemAccent()
    {
        try
        {
            // Channel 1: UISettings.ColorValuesChanged (may be insufficient on its own in unpackaged apps)
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
    /// Is high contrast mode active? The accent color is NOT used in this mode.
    /// The tray icon and meters must use the colors from SystemColors.
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
    /// The user's main Windows accent color (UIColorType.Accent).
    /// Returns SystemColors.Highlight in high contrast mode.
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
    /// The light theme variant (UIColorType.AccentLight1).
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
    /// The dark theme variant (UIColorType.AccentDark1).
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
