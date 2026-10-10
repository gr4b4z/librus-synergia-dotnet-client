using System.Text.Json;

namespace Librus.Mcp;

/// <summary>Tiny JSON-Schema builder (no reflection; NativeAOT-safe).</summary>
internal static class Schema
{
    internal sealed record Property(string Name, Action<Utf8JsonWriter> Write, bool Required);

    public static Property Child() => new("child", w =>
    {
        w.WriteString("type", "string");
        w.WriteString("description", "Child selector: numeric id, login, or first name (see list_children). Optional when only one child is linked.");
    }, false);

    public static Property String(string name, string description, string[]? values = null, bool required = false) => new(name, w =>
    {
        w.WriteString("type", "string");
        w.WriteString("description", description);
        if (values is not null)
        {
            w.WriteStartArray("enum");
            foreach (var v in values)
            {
                w.WriteStringValue(v);
            }

            w.WriteEndArray();
        }
    }, required);

    public static Property String(string name, string description, bool required) => String(name, description, null, required);

    public static Property Date(string name, string description) => new(name, w =>
    {
        w.WriteString("type", "string");
        w.WriteString("format", "date");
        w.WriteString("description", description);
    }, false);

    public static Property Bool(string name, string description) => new(name, w =>
    {
        w.WriteString("type", "boolean");
        w.WriteString("description", description);
    }, false);

    public static Property Int(string name, string description) => new(name, w =>
    {
        w.WriteString("type", "integer");
        w.WriteString("description", description);
    }, false);

    public static JsonElement Object(params Property[] properties)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("type", "object");
            w.WriteStartObject("properties");
            foreach (var p in properties)
            {
                w.WriteStartObject(p.Name);
                p.Write(w);
                w.WriteEndObject();
            }

            w.WriteEndObject();
            var required = properties.Where(p => p.Required).ToArray();
            if (required.Length > 0)
            {
                w.WriteStartArray("required");
                foreach (var p in required)
                {
                    w.WriteStringValue(p.Name);
                }

                w.WriteEndArray();
            }

            w.WriteBoolean("additionalProperties", false);
            w.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(buffer.ToArray());
        return doc.RootElement.Clone();
    }
}
