using System.Text;
using System.Text.Json;

namespace Ration.Core.Providers;

/// <summary>
/// Makes raw endpoint responses presentable.
///
/// Quota endpoints do not return tokens but do return identity information: email,
/// user and account ids. Since diagnostic output gets pasted into a chat, an issue or
/// a log file, these are not shown unmasked.
/// Diagnostic output shows metadata only.
/// </summary>
public static class RawResponseRedactor
{
    // No square brackets: this way we do not have to turn off HTML-safe encoding when producing the
    // output. Had we turned it off, raw text from the provider would be printed unescaped when it is
    // later shown in a diagnostics screen.
    public const string Placeholder = "[redacted]";

    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        // kimlik
        "email", "user_id", "userId", "account_id", "accountId",
        "organization_id", "organizationId", "org_id", "orgId", "project_id",
        "projectId", "session_id", "sessionId", "request_id", "requestId",
        "bucket_id", "bucketId",
        "model_id", "modelId", "uuid", "id",
        // authentication (not expected in quota responses but a safety net)
        "token", "id_token", "access_token", "refresh_token", "accessToken",
        "refreshToken", "api_key", "apiKey", "session_key", "sessionKey",
        "csrf_token", "csrfToken", "host_bridge_token", "hostBridgeToken",
        "secret", "password", "cookie", "authorization",
    };

    /// <summary>
    /// Returns the JSON formatted, with the values of sensitive fields masked.
    /// A response that cannot be parsed is never shown — printing it blindly risks a leak.
    /// </summary>
    public static string Redact(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(json);
            using var buffer = new MemoryStream();

            // The default (HTML-safe) encoder is kept on purpose — see the Placeholder note.
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                WriteElement(writer, document.RootElement);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            return L.T("(response could not be parsed as JSON; not shown because it could not be redacted)", "(yanıt JSON olarak ayrıştırılamadı; gizleme yapılamadığı için gösterilmiyor)");
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
