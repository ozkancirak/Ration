using Ration.Core.Model;

namespace Ration.Core.Usage;

/// <summary>Which window of a provider the tray icon follows.</summary>
public enum TrayWindowChoice
{
    /// <summary>The most used of the main windows (session and weekly).</summary>
    Highest,

    /// <summary>The short session window (5-hour and the like).</summary>
    Session,

    /// <summary>The weekly window.</summary>
    Weekly,
}

public static class TrayWindowSelector
{
    /// <summary>
    /// The window a provider contributes to the tray icon and the tab meter. Per-model additional limits
    /// (gpt-reserve etc.) are left out; only the main quotas (Session and Weekly) count. If the chosen
    /// kind does not exist, the most used main window is taken. Null if there is no main window.
    /// </summary>
    public static UsageWindow? Pick(UsageSnapshot snapshot, TrayWindowChoice choice)
    {
        UsageWindow? highest = null;
        UsageWindow? chosen = null;

        foreach (var window in snapshot.Windows)
        {
            if (window.Kind is not (WindowKind.Session or WindowKind.Weekly)) continue;
            if (window.Label is not null && window.Label.Contains("gpt-", StringComparison.OrdinalIgnoreCase)) continue;

            if (highest is null || window.Percent > highest.Percent) highest = window;

            var wanted = choice switch
            {
                TrayWindowChoice.Session => WindowKind.Session,
                TrayWindowChoice.Weekly => WindowKind.Weekly,
                _ => (WindowKind?)null,
            };
            if (window.Kind == wanted && (chosen is null || window.Percent > chosen.Percent)) chosen = window;
        }

        return chosen ?? highest;
    }
}
