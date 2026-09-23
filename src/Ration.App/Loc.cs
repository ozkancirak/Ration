using Microsoft.UI.Xaml.Markup;
using Ration.Core;

namespace Ration.App;

/// <summary>XAML'de iki dilli metin: <c>Text="{l:Loc En='Refresh', Tr='Yenile'}"</c>.</summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class Loc : MarkupExtension
{
    public string En { get; set; } = string.Empty;
    public string Tr { get; set; } = string.Empty;

    protected override object ProvideValue() => L.T(En, Tr);
}
