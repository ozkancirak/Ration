using Ration.Core.Diagnostics;

namespace Ration.App.Views;

internal static class ClipboardHelper
{
    /// <summary>
    /// Metni panoya koyar. Pano başka bir uygulama tarafından tutuluyorsa SetContent COM hatası
    /// verir; bu, olay işleyicisinden sızıp uygulamayı kapatmasın diye yakalanır.
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
