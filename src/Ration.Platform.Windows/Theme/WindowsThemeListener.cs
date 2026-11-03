using Microsoft.Win32;

namespace Ration.Platform.Windows.Theme;

public static class WindowsThemeListener
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string SystemUsesLightThemeValue = "SystemUsesLightTheme";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    public static event Action<bool>? ThemeChanged;
    public static event Action? DisplayChanged;
    public static event Action? AccentChanged;

    static WindowsThemeListener()
    {
        // SystemEvents sets up its own message loop on a separate thread; events do not arrive on the UI thread.
        // The subscriber has to marshal with DispatcherQueue.
        try
        {
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color)
                {
                    NotifyAccentChanged();
                    NotifyThemeChanged(IsTaskbarLightTheme());
                }
            };

            SystemEvents.DisplaySettingsChanged += (s, e) => DisplayChanged?.Invoke();
        }
        catch
        {
            // SystemEvents might fail in certain restricted contexts
        }
    }

    public static void NotifyAccentChanged()
    {
        AccentChanged?.Invoke();
    }

    public static void NotifyThemeChanged(bool isLightTheme)
    {
        ThemeChanged?.Invoke(isLightTheme);
    }

    public static bool IsHighContrast => SystemAccent.IsHighContrast;

    public static bool IsTaskbarLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (key != null)
            {
                var val = key.GetValue(SystemUsesLightThemeValue);
                if (val is int intVal)
                {
                    return intVal != 0;
                }
            }
        }
        catch
        {
            // Fallback default
        }
        return false; // Default to dark taskbar (standard for Windows 11)
    }

    public static bool IsAppLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (key != null)
            {
                var val = key.GetValue(AppsUseLightThemeValue);
                if (val is int intVal)
                {
                    return intVal != 0;
                }
            }
        }
        catch
        {
            // Fallback default
        }
        return false; // Default to dark app theme
    }
}
