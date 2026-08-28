using System.Text.Json;

namespace Context.Memory.Summarization;

/// <summary>Best-effort extraction of a file path from a session event's free-form payload JSON.</summary>
internal static class PayloadFieldExtractor
{
    private static readonly string[] PathFieldNames = ["path", "relativePath", "filePath", "file"];

    public static string? TryExtractPath(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var fieldName in PathFieldNames)
            {
                if (doc.RootElement.TryGetProperty(fieldName, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Payload isn't JSON, or isn't an object — no path to extract.
        }

        return null;
    }
}
