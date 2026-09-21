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

    // Kaynak: openai/codex → codex-rs/tui/src/empty_state_animation/paths.rs,
    // CODEX sabiti. Kaynak viewBox 20; koordinatlar çevrilmeden 1.2 ile 24'e
    // yerleştirilir.
    private const string CodexPathData =
        "M16.585 10C16.585 6.3632 13.6368 3.41504 10 3.41504C6.3632 3.41504 3.41504 6.3632 3.41504 10C3.41504 13.6368 6.3632 16.585 10 16.585C13.6368 16.585 16.585 13.6368 16.585 10ZM13.333 11.418L13.4678 11.4316C13.7705 11.4938 13.9979 11.7619 13.998 12.083C13.998 12.4042 13.7706 12.6722 13.4678 12.7344L13.333 12.748H10.833C10.4659 12.7479 10.168 12.4502 10.168 12.083C10.1681 11.716 10.466 11.4181 10.833 11.418H13.333ZM7.65332 12.4258C7.46427 12.7404 7.05603 12.8422 6.74121 12.6533C6.42642 12.4644 6.32391 12.0561 6.5127 11.7412L7.65332 12.4258ZM8.90332 9.6582C9.02956 9.86872 9.02956 10.1313 8.90332 10.3418L7.65332 12.4258L7.08301 12.083L6.5127 11.7412L7.55664 10L6.5127 8.25879L7.08301 7.91699L7.65332 7.57422L8.90332 9.6582ZM6.74121 7.34668C7.05603 7.15779 7.46427 7.25955 7.65332 7.57422L6.5127 8.25879C6.32391 7.94392 6.42642 7.53563 6.74121 7.34668ZM17.915 10C17.915 14.3713 14.3713 17.915 10 17.915C5.62867 17.915 2.08496 14.3713 2.08496 10C2.08496 5.62867 5.62867 2.08496 10 2.08496C14.3713 2.08496 17.915 5.62867 17.915 10Z";

    // antigravity.google'daki Copy Logo as SVG çıktısı gradyan/mask içeriyor;
    // tek renk Fluent ikon için aynı resmi işaretin lobe-icons (MIT) 24'luk
    // EvenOdd geometrisi kullanılır. Aktif renk #3186FF'tir.
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
            ["codex"] = new("codex", CodexPathData, 20, null, LayoutScale: 1.2),
            ["antigravity"] = new("antigravity", AntigravityPathData, 24, "3186FF"),
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
