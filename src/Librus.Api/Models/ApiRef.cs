using System.Text.Json;
using System.Text.Json.Serialization;

namespace Librus.Api.Models;

/// <summary>
/// A Librus resource reference (<c>{ "Id": 1, "Url": "https://api.librus.pl/3.0/Users/1" }</c>).
/// Some endpoints inline the whole object instead; extra fields are kept in <see cref="AdditionalProperties"/>.
/// </summary>
public sealed class ApiRef
{
    [JsonPropertyName("Id")] public LibrusId? Id { get; set; }

    [JsonPropertyName("Url")] public string? Url { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    public override string ToString() => Id?.Value ?? "";
}

/// <summary>An entity the OpenAPI document leaves untyped; all fields live in <see cref="AdditionalProperties"/>.</summary>
public sealed class SynergiaEntity
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    public JsonElement? this[string key] =>
        AdditionalProperties is not null && AdditionalProperties.TryGetValue(key, out var value) ? value : null;

    public string? GetString(string key) => this[key] switch
    {
        { ValueKind: JsonValueKind.String } e => e.GetString(),
        { ValueKind: JsonValueKind.Number } e => e.GetRawText(),
        _ => null,
    };
}

/// <summary>A binary download (attachment).</summary>
public sealed record LibrusBinary(byte[] Data, string? ContentType, string? FileName);
