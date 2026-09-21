using Microsoft.UI.Xaml;
using Kalan.Platform.Windows.Theme;

namespace Kalan.App.Views;

internal static class AppTheme
{
    public static void Apply(FrameworkElement root)
    {
        root.RequestedTheme = AppThemePreference.Current switch
        {
            AppThemeMode.Light => ElementTheme.Light,
            AppThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }
}
