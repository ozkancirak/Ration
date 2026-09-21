using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Kalan.Platform.Windows.Theme;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace Kalan.App.Views;

internal sealed record ProviderIconDefinition(
    string Id,
    string PathData,
    double ViewBoxSize,
    string? BrandHex,
    bool UsePrimaryBrushInDarkTheme = false,
    double LayoutScale = 1,
    bool IsFallback = false);

/// <summary>
/// Sağlayıcı işaretlerinin tek kaynağı. Flyout ve Ayarlar aynı path, viewBox ve
/// marka rengi tanımlarını kullanır.
/// </summary>
internal static class ProviderIcons
{
    // Simple Icons kaydı: https://github.com/simple-icons/simple-icons/blob/develop/icons/claude.svg
    // hex alanı #D97757 (CC0).
    private const string ClaudePathData =
        "m4.7144 15.9555 4.7174-2.6471.079-.2307-.079-.1275h-.2307l-.7893-.0486-2.6956-.0729-2.3375-.0971-2.2646-.1214-.5707-.1215-.5343-.7042.0546-.3522.4797-.3218.686.0608 1.5179.1032 2.2767.1578 1.6514.0972 2.4468.255h.3886l.0546-.1579-.1336-.0971-.1032-.0972L6.973 9.8356l-2.55-1.6879-1.3356-.9714-.7225-.4918-.3643-.4614-.1578-1.0078.6557-.7225.8803.0607.2246.0607.8925.686 1.9064 1.4754 2.4893 1.8336.3643.3035.1457-.1032.0182-.0728-.164-.2733-1.3539-2.4467-1.445-2.4893-.6435-1.032-.17-.6194c-.0607-.255-.1032-.4674-.1032-.7285L6.287.1335 6.6997 0l.9957.1336.419.3642.6192 1.4147 1.0018 2.2282 1.5543 3.0296.4553.8985.2429.8318.091.255h.1579v-.1457l.1275-1.706.2368-2.0947.2307-2.6957.0789-.7589.3764-.9107.7468-.4918.5828.2793.4797.686-.0668.4433-.2853 1.8517-.5586 2.9021-.3643 1.9429h.2125l.2429-.2429.9835-1.3053 1.6514-2.0643.7286-.8196.85-.9046.5464-.4311h1.0321l.759 1.1293-.34 1.1657-1.0625 1.3478-.8804 1.1414-1.2628 1.7-.7893 1.36.0729.1093.1882-.0183 2.8535-.607 1.5421-.2794 1.8396-.3157.8318.3886.091.3946-.3278.8075-1.967.4857-2.3072.4614-3.4364.8136-.0425.0304.0486.0607 1.5482.1457.6618.0364h1.621l3.0175.2247.7892.522.4736.6376-.079.4857-1.2142.6193-1.6393-.3886-3.825-.9107-1.3113-.3279h-.1822v.1093l1.0929 1.0686 2.0035 1.8092 2.5075 2.3314.1275.5768-.3218.4554-.34-.0486-2.2039-1.6575-.85-.7468-1.9246-1.621h-.1275v.17l.4432.6496 2.3436 3.5214.1214 1.0807-.17.3521-.6071.2125-.6679-.1214-1.3721-1.9246L14.38 17.959l-1.1414-1.9428-.1397.079-.674 7.2552-.3156.3703-.7286.2793-.6071-.4614-.3218-.7468.3218-1.4753.3886-1.9246.3157-1.53.2853-1.9004.17-.6314-.0121-.0425-.1397.0182-1.4328 1.9672-2.1796 2.9446-1.7243 1.8456-.4128.164-.7164-.3704.0667-.6618.4008-.5889 2.386-3.0357 1.4389-1.882.929-1.0868-.0062-.1579h-.0546l-6.3385 4.1164-1.1293.1457-.4857-.4554.0608-.7467.2307-.2429 1.9064-1.3114Z";

    // OpenAI'nin resmi ChatGPT/OpenAI monoblossom SVG kaynağı:
    // https://cdn.openai.com/brand/OpenAI-Logos-2025.zip
    // Codex sekmesi ChatGPT işaretini kullanır; aktif renk monokrom primary'dir.
    private const string ChatGptPathData =
        "M9.2051 8.7651V6.5054C9.2051 6.3151 9.2765 6.1724 9.4429 6.0773L13.9861 3.4609C14.6046 3.1041 15.342 2.9377 16.103 2.9377C18.9572 2.9377 20.7651 5.1498 20.7651 7.5045C20.7651 7.671 20.7651 7.8613 20.7412 8.0516L16.0316 5.2924C15.7462 5.126 15.4607 5.126 15.1753 5.2924L9.2051 8.7651ZM19.8135 17.5659V12.1664C19.8135 11.8333 19.6707 11.5955 19.3854 11.429L13.4152 7.9563L15.3656 6.8383C15.5321 6.7433 15.6748 6.7433 15.8413 6.8383L20.3845 9.4547C21.6928 10.216 22.5728 11.8333 22.5728 13.4031C22.5728 15.2108 21.5025 16.8759 19.8135 17.5657V17.5659ZM7.8017 12.8088L5.8513 11.6671C5.6849 11.5721 5.6134 11.4293 5.6134 11.239V6.0061C5.6134 3.4611 7.5639 1.5343 10.2042 1.5343C11.2033 1.5343 12.1307 1.8674 12.9159 2.462L8.2301 5.1737C7.9447 5.3401 7.802 5.578 7.802 5.9111V12.809L7.8017 12.8088ZM12 15.2349L9.2051 13.665V10.3352L12 8.7653L14.7947 10.3352V13.665L12 15.2349ZM13.7958 22.4659C12.7967 22.4659 11.8693 22.1328 11.0841 21.5382L15.7699 18.8265C16.0553 18.6601 16.198 18.4222 16.198 18.0891V11.1912L18.1724 12.3329C18.3388 12.4279 18.4102 12.5707 18.4102 12.761V17.9939C18.4102 20.5389 16.4359 22.4657 13.7958 22.4657V22.4659ZM8.1585 17.1616L3.6153 14.5453C2.307 13.784 1.427 12.1667 1.427 10.5968C1.427 8.7653 2.5212 7.1241 4.2098 6.4343V11.8574C4.2098 12.1905 4.3527 12.4284 4.638 12.5948L10.5846 16.0436L8.6341 17.1616C8.4677 17.2567 8.3249 17.2567 8.1585 17.1616ZM7.897 21.0625C5.2092 21.0625 3.2349 19.0407 3.2349 16.5432C3.2349 16.3529 3.2588 16.1626 3.2824 15.9722L7.9682 18.6839C8.2535 18.8504 8.5391 18.8504 8.8245 18.6839L14.7947 15.2351V17.4948C14.7947 17.6851 14.7233 17.8278 14.5568 17.9229L10.0137 20.5393C9.3952 20.8961 8.6578 21.0625 7.8968 21.0625H7.897ZM13.7958 23.8929C16.6739 23.8929 19.0762 21.8474 19.6234 19.1357C22.2874 18.4459 24 15.9484 24 13.4033C24 11.7383 23.2865 10.121 22.002 8.9554C22.121 8.4559 22.1923 7.9563 22.1923 7.457C22.1923 4.0557 19.4331 1.5105 16.2458 1.5105C15.6037 1.5105 14.9852 1.6055 14.3668 1.8197C13.2962 0.7731 11.8215 0.1071 10.2042 0.1071C7.3261 0.1071 4.9238 2.1526 4.3766 4.8642C1.7126 5.5541 0 8.0516 0 10.5966C0 12.2617 0.7135 13.879 1.998 15.0445C1.879 15.5441 1.8077 16.0436 1.8077 16.543C1.8077 19.9443 4.5669 22.4895 7.7542 22.4895C8.3963 22.4895 9.0148 22.3945 9.6332 22.1803C10.7035 23.2269 12.1782 23.8929 13.7958 23.8929Z";

    // antigravity.google'daki Copy Logo as SVG çıktısı gradyan/mask içeriyor;
    // tek renk Fluent ikon için aynı resmi işaretin lobe-icons (MIT) 24'luk
    // EvenOdd geometrisi kullanılır. Aktif renk siyah (#000000), koyu temada
    // görünürlük için TextFillColorPrimaryBrush'tır.
    private const string AntigravityPathData =
        "M21.751 22.607c1.34 1.005 3.35.335 1.508-1.508C17.73 15.74 18.904 1 12.037 1 5.17 1 6.342 15.74.815 21.1c-2.01 2.009.167 2.511 1.507 1.506 5.192-3.517 4.857-9.714 9.715-9.714 4.857 0 4.522 6.197 9.714 9.715z";

    // lobe-icons path (https://github.com/lobehub/lobe-icons); açık temada
    // #000000, koyu temada birincil metin (beyaz).
    private const string OpenCodePathData =
        "M16 6H8v12h8V6zm4 16H4V2h16v20z";

    private const string FallbackPathData =
        "M4,12 L12,4 L20,12 L12,20 Z M8,12 H16 V15 H8 Z";

    private static readonly IReadOnlyDictionary<string, ProviderIconDefinition> Definitions =
        new Dictionary<string, ProviderIconDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = new("claude", ClaudePathData, 24, "D97757"),
            ["codex"] = new("codex", ChatGptPathData, 24, null),
            ["antigravity"] = new("antigravity", AntigravityPathData, 24, "000000", UsePrimaryBrushInDarkTheme: true),
            ["opencode"] = new("opencode", OpenCodePathData, 24, "000000", UsePrimaryBrushInDarkTheme: true),
        };

    public static ProviderIconDefinition Get(string providerId) =>
        Definitions.TryGetValue(providerId, out var definition)
            ? definition
            : new ProviderIconDefinition(providerId, FallbackPathData, 24, null, IsFallback: true);

    public static UIElement Create(string providerId, double size, bool active) =>
        Create(Get(providerId), size, active);

    public static UIElement Create(ProviderIconDefinition definition, double size, bool active)
    {
        var brush = GetBrush(definition, active);
        var path = CreatePath(definition);
        path.Fill = brush;

        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = path,
        };
    }

    public static PathIcon CreateIconElement(string providerId, bool active)
    {
        var definition = Get(providerId);
        var icon = (PathIcon)XamlReader.Load(
            $"<PathIcon xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Data=\"{definition.PathData}\" />");
        icon.Width = 20;
        icon.Height = 20;
        icon.Foreground = GetBrush(definition, active);
        return icon;
    }

    private static XamlPath CreatePath(ProviderIconDefinition definition)
    {
        var layoutSize = (definition.ViewBoxSize * definition.LayoutScale)
            .ToString(CultureInfo.InvariantCulture);
        return (XamlPath)XamlReader.Load(
            $"<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Data=\"{definition.PathData}\" Width=\"{layoutSize}\" Height=\"{layoutSize}\" Stretch=\"Uniform\" />");
    }

    public static Brush GetBrush(ProviderIconDefinition definition, bool active)
    {
        if (!active)
        {
            return QuotaVisuals.Fill("TextFillColorTertiaryBrush");
        }

        if (definition.UsePrimaryBrushInDarkTheme && !WindowsThemeListener.IsAppLightTheme())
        {
            return QuotaVisuals.Fill("TextFillColorPrimaryBrush");
        }

        if (string.IsNullOrWhiteSpace(definition.BrandHex))
        {
            return QuotaVisuals.Fill("TextFillColorPrimaryBrush");
        }

        var hex = definition.BrandHex;
        var color = Windows.UI.Color.FromArgb(
            255,
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16));
        return new SolidColorBrush(color);
    }

    public static void SetBrush(UIElement icon, Brush brush)
    {
        if (icon is Viewbox { Child: XamlPath path })
        {
            path.Fill = brush;
        }
    }
}
