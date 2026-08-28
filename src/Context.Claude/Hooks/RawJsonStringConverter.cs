using System.Text.Json;
using System.Text.Json.Serialization;

namespace Context.Claude.Hooks;

/// <summary>
/// Captures a JSON value (object, array, or scalar) as its raw JSON text exposed as a plain
/// <see cref="string"/>, instead of modeling its shape. Used for hook payload fields
/// (<c>tool_input</c>, <c>tool_response</c>) whose structure is tool-defined and not something
/// Context Platform needs to strongly type — consumers can parse the string further if they need to.
/// </summary>
public sealed class RawJsonStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.GetRawText();
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            writer.WriteNullValue();
            return;
        }

        using var document = JsonDocument.Parse(value);
        document.RootElement.WriteTo(writer);
    }
}
