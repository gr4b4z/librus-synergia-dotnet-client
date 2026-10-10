using System.Text.Json;
using System.Text.Json.Serialization;

namespace Librus.Portal;

/// <summary>
/// A child (Synergia) account linked to the Konto LIBRUS login, as returned by
/// <c>portal.librus.pl/api/v3/SynergiaAccounts</c>. The bearer token is intentionally not part
/// of this public model; <see cref="LibrusSession"/> keeps it internally.
/// </summary>
public sealed record Child(
    int Id,
    string Login,
    string StudentName,
    string AccountIdentifier,
    string Group,
    string State,
    IReadOnlyList<string> Scopes)
{
    public bool HasScope(string scope) => Scopes.Contains(scope, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Raw <c>/SynergiaAccounts</c> entry. Contains the bearer token: never log or serialize it outward.</summary>
internal sealed class SynergiaAccountDto
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("accountIdentifier")] public string? AccountIdentifier { get; set; }
    [JsonPropertyName("group")] public string? Group { get; set; }
    [JsonPropertyName("accessToken")] public string? AccessToken { get; set; }
    [JsonPropertyName("login")] public string? Login { get; set; }
    [JsonPropertyName("studentName")] public string? StudentName { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }

    /// <summary>Either an array of scope names or an empty string.</summary>
    [JsonPropertyName("scopes")] public JsonElement Scopes { get; set; }

    public Child ToChild() => new(
        Id,
        Login ?? "",
        StudentName ?? "",
        AccountIdentifier ?? "",
        Group ?? "",
        State ?? "",
        Scopes.ValueKind == JsonValueKind.Array
            ? Scopes.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray()
            : []);
}

internal sealed class SynergiaAccountsDto
{
    [JsonPropertyName("lastModification")] public long LastModification { get; set; }
    [JsonPropertyName("accounts")] public List<SynergiaAccountDto> Accounts { get; set; } = [];
}

/// <summary><c>portal.librus.pl/api/v3/Me</c>.</summary>
public sealed class PortalMe
{
    [JsonPropertyName("identifier")] public long Identifier { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

[JsonSerializable(typeof(SynergiaAccountsDto))]
[JsonSerializable(typeof(SynergiaAccountDto))]
[JsonSerializable(typeof(PortalMe))]
internal sealed partial class PortalJsonContext : JsonSerializerContext;
