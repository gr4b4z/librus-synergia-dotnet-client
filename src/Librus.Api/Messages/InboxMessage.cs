using System.Text.Json.Serialization;

namespace Librus.Api.Messages;

/// <summary>Where a message was read from.</summary>
public enum MessageSource
{
    /// <summary><c>api.librus.pl/3.0/Messages</c> (bearer).</summary>
    Api3,

    /// <summary><c>wiadomosci.librus.pl/api</c> (portal handoff cookies).</summary>
    Wiadomosci,
}

/// <summary>Backend-independent view of an inbox message used by the CLI, poller and MCP tools.</summary>
public sealed record InboxMessage
{
    public required string Id { get; init; }
    public string? Subject { get; init; }
    public string? SenderId { get; init; }
    public string? SenderName { get; init; }
    public DateTimeOffset? SentAt { get; init; }
    public DateTimeOffset? ReadAt { get; init; }
    public bool IsRead { get; init; }
    public string? Body { get; init; }
    public bool HasAttachments { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter<MessageSource>))]
    public MessageSource Source { get; init; }

    /// <summary>Deep link for a human to open (and reply to) the thread in Synergia.</summary>
    public string? WebUrl { get; init; }
}

public sealed record InboxPage(IReadOnlyList<InboxMessage> Messages, MessageSource Source);
