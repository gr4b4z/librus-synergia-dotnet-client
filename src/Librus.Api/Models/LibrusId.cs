using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Librus.Api.Models;

/// <summary>
/// Librus identifiers arrive as JSON strings or numbers depending on the endpoint.
/// <see cref="LibrusId"/> accepts both and always exposes the canonical string form.
/// </summary>
[JsonConverter(typeof(LibrusIdJsonConverter))]
public readonly record struct LibrusId(string Value)
{
    public static implicit operator LibrusId(string value) => new(value);

    public static implicit operator LibrusId(long value) => new(value.ToString(CultureInfo.InvariantCulture));

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public override string ToString() => Value ?? "";
}

public sealed class LibrusIdJsonConverter : JsonConverter<LibrusId>
{
    public override LibrusId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => new LibrusId(reader.GetString() ?? ""),
        JsonTokenType.Number => new LibrusId(reader.TryGetInt64(out var l)
            ? l.ToString(CultureInfo.InvariantCulture)
            : reader.GetDouble().ToString(CultureInfo.InvariantCulture)),
        JsonTokenType.True => new LibrusId("true"),
        JsonTokenType.False => new LibrusId("false"),
        _ => throw new JsonException($"Expected a string or number id, got {reader.TokenType}."),
    };

    public override void Write(Utf8JsonWriter writer, LibrusId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
