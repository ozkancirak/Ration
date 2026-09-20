using System.Text.Json;

namespace Kalan.Core.Providers;

internal static class JsonHelpers
{
    /// <summary>
    /// Zaman damgasını ISO-8601 string veya Unix epoch (saniye ya da milisaniye)
    /// olarak okur. Tanınmayan biçimde null döner — uydurma tarih üretmez.
    /// </summary>
    public static DateTimeOffset? ReadTimestamp(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(element.GetString(), out var parsed))
        {
            return parsed;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var epoch))
        {
            // 100 milyar sınırı saniye ile milisaniyeyi güvenle ayırır:
            // saniye cinsinden 100e9 yıl 5138'e denk gelir, milisaniye cinsinden 1973'e.
            return epoch > 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                : DateTimeOffset.FromUnixTimeSeconds(epoch);
        }

        return null;
    }

    /// <summary>Verilen isimlerden ilk sayısal alanı döndürür; yoksa null.</summary>
    public static double? ReadFirstNumber(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.Number)
            {
                return value.GetDouble();
            }
        }

        return null;
    }
}
