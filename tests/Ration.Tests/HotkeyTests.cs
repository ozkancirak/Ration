using Ration.Core.Settings;

namespace Ration.Tests;

public sealed class HotkeyTests
{
    [Fact]
    public void Varsayilan_CtrlAltR()
    {
        Assert.Equal("Ctrl+Alt+R", Hotkey.Default.ToString());
        Assert.True(Hotkey.TryParse("Ctrl+Alt+R", out var parsed));
        Assert.Equal(Hotkey.Default, parsed);
    }

    [Theory]
    [InlineData("ctrl+shift+f5", "Ctrl+Shift+F5")]
    [InlineData("Alt + 7", "Alt+7")]
    [InlineData("Win+Alt+F24", "Alt+Win+F24")]
    [InlineData("Control+Alt+r", "Ctrl+Alt+R")]
    public void Ayristirir_VeKanonikYazar(string input, string expected)
    {
        Assert.True(Hotkey.TryParse(input, out var hotkey));
        Assert.Equal(expected, hotkey.ToString());
        Assert.True(Hotkey.TryParse(hotkey.ToString(), out var again));
        Assert.Equal(hotkey, again);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("R")]              // değiştirici yok
    [InlineData("Shift+R")]        // yalnız Shift yazı yazmayı bozar
    [InlineData("Ctrl+Alt")]       // tuş yok
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Alt+Esc")]   // desteklenmeyen tuş
    [InlineData("Ctrl+Alt+F25")]
    [InlineData("Ctrl+Alt+F0")]
    [InlineData("Hyper+R")]
    [InlineData("Ctrl+Alt+R+S")]
    public void Gecersizleri_Reddeder(string? input)
    {
        Assert.False(Hotkey.TryParse(input, out _));
    }

    [Fact]
    public void TryCreate_DegistiriciVeTusuDogrular()
    {
        Assert.True(Hotkey.TryCreate(Hotkey.Control, 'K', out _));
        Assert.False(Hotkey.TryCreate(Hotkey.Shift, 'K', out _));
        Assert.False(Hotkey.TryCreate(Hotkey.Control, 0x1B, out _)); // VK_ESCAPE
        Assert.False(Hotkey.TryCreate(0x100 | Hotkey.Control, 'K', out _));
    }
}
