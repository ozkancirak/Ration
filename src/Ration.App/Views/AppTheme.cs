using Microsoft.UI.Xaml;
using Ration.Platform.Windows.Theme;

namespace Ration.App.Views;

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
