using Ration.Core.Diagnostics;

namespace Ration.App.Views;

internal static class ClipboardHelper
{
    /// <summary>
    /// Puts text on the clipboard. If the clipboard is held by another app, SetContent throws a COM error;
    /// this is caught so it does not leak out of the event handler and close the app.
    /// </summary>
    public static bool TryCopy(string text)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            return true;
        }
        catch (Exception ex)
        {
            Trace.Error("clipboard", $"copy failed type={ex.GetType().Name}");
            return false;
        }
    }
}
