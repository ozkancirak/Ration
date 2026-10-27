using System.Security.Cryptography;
using System.Text;
using Ration.Core.Providers.Cursor;

namespace Ration.Tests;

public sealed class CursorTokenCipherTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    // Chromium os_crypt düzeni: "v10" | nonce(12) | şifreli metin | etiket(16), AES-256-GCM.
    private static string Encrypt(string plain, byte[] key, string version = "v10")
    {
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        var bytes = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[bytes.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, bytes, cipher, tag);

        var payload = Encoding.ASCII.GetBytes(version).Concat(nonce).Concat(cipher).Concat(tag).ToArray();
        return CursorSessionStore.EncryptedTokenPrefix + Convert.ToBase64String(payload);
    }

    [Theory]
    [InlineData("v10")]
    [InlineData("v11")]
    public void SifreliBelirtec_DogruAnahtarlaCozulur(string version)
    {
        var value = Encrypt("  sentetik.jwt.belirteci  ", Key, version);

        Assert.Equal("sentetik.jwt.belirteci", CursorTokenCipher.TryDecrypt(value, Key));
    }

    [Fact]
    public void YanlisAnahtar_BozukVeri_VeBilinmeyenBicim_Null()
    {
        var value = Encrypt("belirtec", Key);

        Assert.Null(CursorTokenCipher.TryDecrypt(value, Enumerable.Repeat((byte)7, 32).ToArray()));
        Assert.Null(CursorTokenCipher.TryDecrypt(value, new byte[16]));                       // anahtar boyu
        Assert.Null(CursorTokenCipher.TryDecrypt("eyJ.duz.jwt", Key));                         // önek yok
        Assert.Null(CursorTokenCipher.TryDecrypt(CursorSessionStore.EncryptedTokenPrefix + "bu base64 degil!", Key));
        Assert.Null(CursorTokenCipher.TryDecrypt(CursorSessionStore.EncryptedTokenPrefix + Convert.ToBase64String(new byte[10]), Key)); // kısa
        Assert.Null(CursorTokenCipher.TryDecrypt(Encrypt("belirtec", Key, "v20"), Key));      // app-bound biçim desteklenmez

        var bytes = Convert.FromBase64String(value[CursorSessionStore.EncryptedTokenPrefix.Length..]);
        bytes[^1] ^= 0xFF;                                                                       // etiket bozuldu
        Assert.Null(CursorTokenCipher.TryDecrypt(CursorSessionStore.EncryptedTokenPrefix + Convert.ToBase64String(bytes), Key));
    }

    [Fact]
    public void LocalState_DpapiOnekiAtilir_DigerleriNull()
    {
        var blob = new byte[] { 1, 2, 3, 4 };
        var withMarker = Convert.ToBase64String(Encoding.ASCII.GetBytes("DPAPI").Concat(blob).ToArray());

        static string State(string key) => "{\"os_crypt\":{\"encrypted_key\":\"" + key + "\"}}";

        Assert.Equal(blob, CursorTokenCipher.TryReadProtectedKey(State(withMarker)));

        Assert.Null(CursorTokenCipher.TryReadProtectedKey(State(Convert.ToBase64String(blob))));
        Assert.Null(CursorTokenCipher.TryReadProtectedKey("""{"os_crypt":{}}"""));
        Assert.Null(CursorTokenCipher.TryReadProtectedKey("""{"baska":1}"""));
        Assert.Null(CursorTokenCipher.TryReadProtectedKey("""{"os_crypt":{"encrypted_key":"base64degil!"}}"""));
        Assert.Null(CursorTokenCipher.TryReadProtectedKey("<html>"));
    }
}
