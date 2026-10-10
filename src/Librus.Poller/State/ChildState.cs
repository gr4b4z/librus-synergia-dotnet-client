using System.Text.Json.Serialization;

namespace Librus.Poller.State;

/// <summary>Persisted per-child poller state (<c>~/.config/librus-net/state/&lt;childId&gt;.json</c>).</summary>
public sealed class ChildState
{
    public const int MaxSeenPerCategory = 5000;

    [JsonPropertyName("childId")] public int ChildId { get; set; }

    [JsonPropertyName("categories")] public Dictionary<string, CategoryState> Categories { get; set; } = new(StringComparer.Ordinal);

    /// <summary>ChangeRegister cursor (last seen entry id), when Strategy A is used.</summary>
    [JsonPropertyName("changeRegisterCursor")] public long? ChangeRegisterCursor { get; set; }

    /// <summary>
    /// Notifications detected but not yet delivered. Written to disk <em>before</em> any sink is called
    /// (checkpoint), removed one by one after successful delivery: an interrupted run neither loses
    /// nor re-detects items (delivery is at-least-once only for the single in-flight item).
    /// </summary>
    [JsonPropertyName("outbox")] public List<PendingNotification> Outbox { get; set; } = [];

    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CategoryState
{
    /// <summary>False until the first successful observation; the first one only seeds a baseline.</summary>
    [JsonPropertyName("initialized")] public bool Initialized { get; set; }

    /// <summary>Seen ids, oldest first (capped at <see cref="ChildState.MaxSeenPerCategory"/>).</summary>
    [JsonPropertyName("seen")] public List<string> Seen { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ChildState))]
internal sealed partial class PollerJsonContext : JsonSerializerContext;
