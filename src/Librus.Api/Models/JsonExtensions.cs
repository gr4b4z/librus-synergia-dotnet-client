using System.Text.Json;

namespace Librus.Api.Models;

internal static class JsonExtensions
{
    public static string? GetStringOrNumber(this Dictionary<string, JsonElement>? data, params string[] keys)
    {
        if (data is null)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (data.TryGetValue(key, out var value))
            {
                switch (value.ValueKind)
                {
                    case JsonValueKind.String:
                        var s = value.GetString();
                        if (!string.IsNullOrEmpty(s))
                        {
                            return s;
                        }

                        break;
                    case JsonValueKind.Number:
                        return value.GetRawText();
                }
            }
        }

        return null;
    }
}

/// <summary>Public helper to read loose string/number fields kept in DTO extension data.</summary>
public static class SynergiaEntityText
{
    public static string? Get(Dictionary<string, JsonElement>? data, params string[] keys) => data.GetStringOrNumber(keys);
}

internal static class JsonElementExtensions
{
    public static bool? GetBool(this Dictionary<string, JsonElement>? data, string key)
    {
        if (data is null || !data.TryGetValue(key, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.GetRawText() != "0",
            JsonValueKind.String => v.GetString() is "1" or "true" or "True",
            _ => null,
        };
    }

    public static Dictionary<string, JsonElement>? GetObject(this Dictionary<string, JsonElement>? data, string key)
    {
        if (data is null || !data.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return v.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
    }

    public static string? RefId(this Dictionary<string, JsonElement>? data, string key) =>
        data.GetObject(key).GetStringOrNumber("Id");
}
