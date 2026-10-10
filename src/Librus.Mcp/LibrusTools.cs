using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Librus.Api;
using Librus.Api.Messages;
using Librus.Api.Views;
using ModelContextProtocol.Protocol;

namespace Librus.Mcp;

/// <summary>Options controlling which tools the server advertises.</summary>
public sealed record LibrusMcpOptions
{
    public const string EnableSendEnv = "LIBRUS_ENABLE_SEND";

    /// <summary>Registers <c>reply_to_message</c>. Default false (read-only server).</summary>
    public bool EnableSend { get; init; }

    public IMessageReplySender ReplySender { get; init; } = new NotImplementedReplySender();

    public static LibrusMcpOptions FromEnvironment() => new()
    {
        EnableSend = Environment.GetEnvironmentVariable(EnableSendEnv) is { } v
            && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("yes", StringComparison.OrdinalIgnoreCase)),
    };
}

internal delegate Task<string> ToolHandler(ToolArgs args, CancellationToken ct);

internal sealed record ToolDefinition(Tool Tool, ToolHandler Handler);

/// <summary>The librus-net tool catalog (spec §7).</summary>
internal sealed class LibrusTools
{
    private readonly ISessionProvider _sessions;
    private readonly LibrusMcpOptions _options;

    public LibrusTools(ISessionProvider sessions, LibrusMcpOptions options)
    {
        _sessions = sessions;
        _options = options;
        Definitions = Build();
    }

    public IReadOnlyList<ToolDefinition> Definitions { get; }

    private List<ToolDefinition> Build()
    {
        var tools = new List<ToolDefinition>
        {
            Read("list_children", "List children linked to the Konto LIBRUS account (id, login, name). Use id or login as the `child` argument of other tools.",
                Schema.Object(), async (_, ct) =>
                {
                    var session = await _sessions.GetAsync(ct).ConfigureAwait(false);
                    return Json(await session.ListChildrenAsync(ct: ct).ConfigureAwait(false), LibrusViewsJsonContext.Default.IReadOnlyListChild);
                }),
            Read("get_grades", "Grades of one child, newest first, with subject/category/teacher names resolved.",
                Schema.Object(Schema.Child()), async (a, ct) =>
                    Json(await (await ChildAsync(a, ct)).GetGradeViewsAsync(ct).ConfigureAwait(false), LibrusViewsJsonContext.Default.IReadOnlyListGradeView)),
            Read("get_attendance", "Attendance entries (absences, lateness, presence) of one child, newest first.",
                Schema.Object(Schema.Child(), Schema.Date("since", "Only entries on or after this date (YYYY-MM-DD). Default: 30 days ago.")),
                async (a, ct) =>
                {
                    var since = a.Date("since") ?? DateOnly.FromDateTime(DateTime.Today.AddDays(-30));
                    return Json(await (await ChildAsync(a, ct)).GetAttendanceViewsAsync(since, ct).ConfigureAwait(false), LibrusViewsJsonContext.Default.IReadOnlyListAttendanceView);
                }),
            Read("get_timetable", "Lesson plan for one week (with cancellations and substitutions).",
                Schema.Object(Schema.Child(), Schema.Date("weekStart", "Any date in the requested week (YYYY-MM-DD); normalized to Monday. Default: current week.")),
                async (a, ct) =>
                {
                    var week = ChildViews.WeekStart(a.Date("weekStart") ?? DateOnly.FromDateTime(DateTime.Today));
                    return Json(await (await ChildAsync(a, ct)).GetTimetableViewAsync(week, ct).ConfigureAwait(false), LibrusViewsJsonContext.Default.IReadOnlyListTimetableDayView);
                }),
            Read("list_messages", "Inbox messages of one child (teacher messages), newest first. Bodies are omitted; use get_message.",
                Schema.Object(
                    Schema.Child(),
                    Schema.String("folder", "Message folder. Only \"inbox\" is supported.", ["inbox"]),
                    Schema.Bool("unreadOnly", "Only unread messages."),
                    Schema.Int("limit", "Maximum number of messages (1-100, default 30).")),
                async (a, ct) =>
                {
                    var folder = a.String("folder") ?? "inbox";
                    if (!folder.Equals("inbox", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new LibrusException(LibrusErrorCodes.InvalidArgument, "Only the \"inbox\" folder is supported.");
                    }

                    var limit = Math.Clamp(a.Int("limit") ?? 30, 1, 100);
                    var page = await (await ChildAsync(a, ct)).ListInboxAsync(a.Bool("unreadOnly") ?? false, 1, limit, includeBody: false, ct).ConfigureAwait(false);
                    return Json(page.Messages, LibrusViewsJsonContext.Default.IReadOnlyListInboxMessage);
                }),
            Read("get_message", "One message with its full body.",
                Schema.Object(Schema.Child(), Schema.String("id", "Message id from list_messages.", required: true)),
                async (a, ct) => Json(await (await ChildAsync(a, ct)).GetInboxMessageAsync(a.RequireString("id"), ct).ConfigureAwait(false), LibrusViewsJsonContext.Default.InboxMessage)),
            Read("list_notes", "Behaviour notes (uwagi) of one child, newest first.",
                Schema.Object(Schema.Child()), async (a, ct) =>
                    Json(await (await ChildAsync(a, ct)).GetNoteViewsAsync(ct).ConfigureAwait(false), LibrusViewsJsonContext.Default.IReadOnlyListNoteView)),
            Read("list_announcements", "School notices (ogłoszenia) visible to one child, newest first.",
                Schema.Object(Schema.Child()), async (a, ct) =>
                    Json(await (await ChildAsync(a, ct)).GetAnnouncementViewsAsync(ct).ConfigureAwait(false), LibrusViewsJsonContext.Default.IReadOnlyListAnnouncementView)),
            Read("get_homework", "Homework and scheduled tests/events (terminarz) of one child.",
                Schema.Object(Schema.Child(), Schema.Date("since", "Only items on or after this date (YYYY-MM-DD). Default: today.")),
                async (a, ct) =>
                {
                    var since = a.Date("since") ?? DateOnly.FromDateTime(DateTime.Today);
                    return Json(await (await ChildAsync(a, ct)).GetHomeworkViewsAsync(since, ct).ConfigureAwait(false), LibrusViewsJsonContext.Default.IReadOnlyListHomeworkView);
                }),
            Read("get_lucky_number", "Today's lucky number (szczęśliwy numerek) for the child's school.",
                Schema.Object(Schema.Child()), async (a, ct) =>
                    Json(await (await ChildAsync(a, ct)).GetLuckyNumberViewAsync(ct).ConfigureAwait(false), LibrusViewsJsonContext.Default.LuckyNumberView)),
            Read("get_notifications", "Recent changes feed (ChangeRegister, the official app's timeline) for one child. If the feed is unavailable for this account, returns {\"available\": false} with the unread message count.",
                Schema.Object(Schema.Child()), GetNotificationsAsync),
        };

        if (_options.EnableSend)
        {
            tools.Add(new ToolDefinition(
                new Tool
                {
                    Name = "reply_to_message",
                    Title = "Reply to a teacher message",
                    Description = "Reply to a RECEIVED message (reply-only; no new recipients). Call first without `confirm` to get a preview {to, subject, body}; "
                        + "show it to the human, and only after explicit approval call again with confirm=true.",
                    InputSchema = Schema.Object(
                        Schema.Child(),
                        Schema.String("threadOrMessageId", "Id of the received message to reply to.", required: true),
                        Schema.String("body", "Reply text (plain text).", required: true),
                        Schema.Bool("confirm", "Must be true to actually send; otherwise only a preview is returned.")),
                    Annotations = new ToolAnnotations
                    {
                        Title = "Reply to a teacher message",
                        ReadOnlyHint = false,
                        DestructiveHint = true,
                        IdempotentHint = false,
                        OpenWorldHint = true,
                    },
                },
                ReplyAsync));
        }

        return tools;
    }

    private async Task<string> GetNotificationsAsync(ToolArgs args, CancellationToken ct)
    {
        var client = await ChildAsync(args, ct).ConfigureAwait(false);
        try
        {
            var feed = await client.GetChangeRegisterAsync(cancellationToken: ct).ConfigureAwait(false);
            return Json(feed, LibrusViewsJsonContext.Default.JsonElement);
        }
        catch (LibrusException ex) when (ex.Status is 400 or 403 or 404 || ex.Code == LibrusErrorCodes.ResponseValidationFailed)
        {
            var unread = await client.GetUnreadCountAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteBoolean("available", false);
                writer.WriteString("reason", ex.Code);
                writer.WriteNumber("unreadMessages", unread);
                writer.WriteString("hint", "Use list_messages, get_grades, list_notes and list_announcements for details.");
                writer.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
    }

    private async Task<string> ReplyAsync(ToolArgs args, CancellationToken ct)
    {
        var client = await ChildAsync(args, ct).ConfigureAwait(false);
        var draft = await ReplyComposer.PrepareAsync(client, args.RequireString("threadOrMessageId"), args.RequireString("body"), ct).ConfigureAwait(false);
        if (args.Bool("confirm") != true)
        {
            var preview = new ReplyPreview(
                "preview",
                draft.ToName ?? draft.ToId,
                draft.ToId,
                draft.Subject,
                draft.Body,
                draft.InReplyToMessageId,
                draft.ChildLogin,
                "Show to/subject/body to the user. Send only after explicit approval, by calling again with the same arguments and confirm=true.");
            return Json(preview, McpJsonContext.Default.ReplyPreview);
        }

        await _options.ReplySender.SendReplyAsync(client, draft, ct).ConfigureAwait(false);
        return "{\"status\":\"sent\"}";
    }

    private async Task<LibrusChildClient> ChildAsync(ToolArgs args, CancellationToken ct)
    {
        var session = await _sessions.GetAsync(ct).ConfigureAwait(false);
        var selector = args.String("child") ?? Environment.GetEnvironmentVariable("LIBRUS_CHILD");
        if (string.IsNullOrWhiteSpace(selector))
        {
            var children = await session.ListChildrenAsync(ct: ct).ConfigureAwait(false);
            if (children.Count == 1)
            {
                return session.ForChild(children[0]);
            }

            throw new LibrusException(LibrusErrorCodes.ChildRequired,
                "Argument `child` is required (id, login or first name); call list_children to see the options.");
        }

        return await session.ForChildAsync(selector, ct).ConfigureAwait(false);
    }

    private static ToolDefinition Read(string name, string description, JsonElement schema, ToolHandler handler) => new(
        new Tool
        {
            Name = name,
            Description = description,
            InputSchema = schema,
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = true,
                IdempotentHint = true,
                DestructiveHint = false,
                OpenWorldHint = true,
            },
        },
        handler);

    private static string Json<T>(T value, JsonTypeInfo<T> typeInfo) => JsonSerializer.Serialize(value, typeInfo);
}

/// <summary>Typed access to <c>tools/call</c> arguments.</summary>
internal sealed class ToolArgs(IReadOnlyDictionary<string, JsonElement>? values)
{
    private readonly IReadOnlyDictionary<string, JsonElement> _values = values ?? new Dictionary<string, JsonElement>();

    public string? String(string name) => _values.TryGetValue(name, out var v) ? v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        _ => null,
    } : null;

    public string RequireString(string name) => String(name) is { Length: > 0 } s
        ? s
        : throw new LibrusException(LibrusErrorCodes.InvalidArgument, $"Argument `{name}` is required.");

    public bool? Bool(string name) => _values.TryGetValue(name, out var v) ? v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => v.GetString() is "true" or "1" ? true : v.GetString() is "false" or "0" ? false : null,
        _ => null,
    } : null;

    public int? Int(string name) => _values.TryGetValue(name, out var v)
        ? v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i
        : v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i) ? i : null
        : null;

    public DateOnly? Date(string name)
    {
        var s = String(name);
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }

        return DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw new LibrusException(LibrusErrorCodes.InvalidArgument, $"Argument `{name}` must be a date in YYYY-MM-DD format.");
    }
}
