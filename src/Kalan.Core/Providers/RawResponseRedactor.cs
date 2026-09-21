using System.Text;
using System.Text.Json;

namespace Kalan.Core.Providers;

/// <summary>
/// Ham uç nokta yanıtlarını gösterilebilir hale getirir.
///
/// Kota uç noktaları token döndürmez ama kimlik bilgisi döndürür: e-posta,
/// kullanıcı ve hesap kimlikleri. Tanı çıktısı bir sohbete, bir issue'ya ya da
/// bir log dosyasına yapıştırılacağı için bunlar maskelenmeden gösterilmez
/// (AGENTS.md §2.3: diagnostics yalnızca metadata gösterir).
/// </summary>
public static class RawResponseRedactor
{
    // Köşeli parantez yok: böylece çıktıyı üretirken HTML-safe encoding'i kapatmamız
    // gerekmiyor. Kapatsaydık, sağlayıcıdan gelen ham metin ileride bir diagnostics
    // ekranında gösterildiğinde escape edilmeden basilırdı.
    public const string Placeholder = "[gizlendi]";

    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        // kimlik
        "email", "user_id", "userId", "account_id", "accountId",
        "organization_id", "organizationId", "org_id", "orgId", "uuid", "id",
        // kimlik doğrulama (kota yanıtlarında beklenmez ama güvenlik ağı)
        "token", "id_token", "access_token", "refresh_token", "accessToken",
        "refreshToken", "api_key", "apiKey", "session_key", "sessionKey",
        "cookie", "authorization",
    };

    /// <summary>
    /// Hassas alanların değerini maskeleyerek JSON'u biçimli döndürür.
    /// Ayrıştırılamayan yanıt hiç gösterilmez — körlemesine basmak sızıntı riskidir.
    /// </summary>
    public static string Redact(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(json);
            using var buffer = new MemoryStream();

            // Varsayılan (HTML-safe) encoder bilerek korunuyor — bkz. Placeholder notu.
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                WriteElement(writer, document.RootElement);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            return "(yanıt JSON olarak ayrıştırılamadı; gizleme yapılamadığı için gösterilmiyor)";
        }
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();

                foreach (var property in element.EnumerateObject())
                {
                    if (SensitiveKeys.Contains(property.Name))
                    {
                        writer.WriteString(property.Name, Placeholder);
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();

                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(writer, item);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}
