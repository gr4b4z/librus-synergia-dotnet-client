using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Librus;
using Librus.Api;
using Librus.Api.Models;
using Librus.Api.Views;
using Librus.Cli;
using Librus.Http;
using Librus.Mcp;
using Librus.Poller;
using Librus.Poller.Notifiers;
using Librus.Poller.Sources;
using Librus.Poller.State;
using Microsoft.Extensions.Logging;

return await Cli.RunAsync(args).ConfigureAwait(false);

internal static class Cli
{
    private const string Usage = """
        librus-net — unofficial Librus Synergia client (portal login → per-child bearer → api.librus.pl/3.0)

        Usage: librus-net <command> [options]

        Commands:
          login                       Verify credentials (portal login + /Me)
          children [list]             List linked children
          me            --child C     Child account context (/Me)
          grades        --child C     Grades (names resolved)
          attendance    --child C     Attendance [--since YYYY-MM-DD]
          timetable     --child C     Week plan  [--week-start YYYY-MM-DD]
          messages      --child C     Inbox      [--unread] [--limit N]
          message       --child C --id ID
          notes         --child C     Behaviour notes
          announcements --child C     School notices
          homework      --child C     Homework / tests [--since YYYY-MM-DD]
          lucky-number  --child C
          notifications --child C     ChangeRegister feed (raw JSON)
          poll                        Notification poller (see README)
                [--once] [--strategy diff|changeregister|auto] [--notifier ntfy,telegram,console]
                [--child C ...] [--interval-min 12] [--interval-max 15] [--dry-run]
          mcp                         Start the stdio MCP server

        Options:
          --child C        Child id, login or first name (default: $LIBRUS_CHILD, or the only child)
          --format F       text (default) | json
          --raw            With --format json: print the raw API DTO instead of the compact view
          --verbose        Debug logging to stderr (or LIBRUS_LOG_LEVEL=debug|info|warn|error)

        Credentials: LIBRUS_PORTAL_EMAIL / LIBRUS_PORTAL_PASSWORD, or ~/.config/librus-net/secrets.json (mode 600).
        """;

    public static async Task<int> RunAsync(string[] argv)
    {
        CliArgs args;
        try
        {
            args = new CliArgs(argv);
        }
        catch (LibrusException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        if (args.Has("--version"))
        {
            Console.WriteLine(typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0");
            return 0;
        }

        var command = args.Positional.FirstOrDefault();
        if (command is null || command is "help" || args.Has("--help") || args.Has("-h"))
        {
            Console.WriteLine(Usage);
            return command is null && !args.Has("--help") && !args.Has("-h") ? 2 : 0;
        }

        var json = string.Equals(args.Get("--format"), "json", StringComparison.OrdinalIgnoreCase);
        var defaultLevel = command == "poll" ? LogLevel.Information : LogLevel.Warning;
        var level = args.Has("--verbose") ? LogLevel.Debug : StderrLoggerProvider.ParseLevel(Environment.GetEnvironmentVariable("LIBRUS_LOG_LEVEL"), defaultLevel);
        using var loggerFactory = new StderrLoggerProvider(level);
        var http = new LibrusHttpOptions
        {
            LoggerFactory = loggerFactory,
            Timeout = Environment.GetEnvironmentVariable("LIBRUS_TIMEOUT_MS") is { } t && int.TryParse(t, out var ms) && ms > 0
                ? TimeSpan.FromMilliseconds(ms)
                : TimeSpan.FromSeconds(30),
        };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            if (command == "mcp")
            {
                using var provider = new LazySessionProvider(() => LibrusCredentials.Resolve(), http);
                await LibrusMcpServer.RunStdioAsync(provider, LibrusMcpOptions.FromEnvironment(), loggerFactory, cts.Token).ConfigureAwait(false);
                return 0;
            }

            var credentials = LibrusCredentials.Resolve();
            using var session = await LibrusSession.LoginAsync(credentials, http, cts.Token).ConfigureAwait(false);
            return await DispatchAsync(command, args, json, session, http, loggerFactory, cts.Token).ConfigureAwait(false);
        }
        catch (LibrusException ex)
        {
            WriteError(ex, json);
            return ex.Code is LibrusErrorCodes.InvalidArgument or LibrusErrorCodes.ChildRequired ? 2 : 1;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return 130;
        }
    }

    private static async Task<int> DispatchAsync(string command, CliArgs args, bool json, LibrusSession session, LibrusHttpOptions http, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var raw = json && args.Has("--raw");
        switch (command)
        {
            case "login":
                {
                    var me = await session.Portal.GetMeAsync(ct).ConfigureAwait(false);
                    var children = await session.ListChildrenAsync(ct: ct).ConfigureAwait(false);
                    Console.WriteLine(json
                        ? $"{{\"ok\":true,\"children\":{children.Count}}}"
                        : $"OK: zalogowano jako {me.Email}; powiązane dzieci: {children.Count}");
                    return 0;
                }

            case "children":
                {
                    var children = await session.ListChildrenAsync(ct: ct).ConfigureAwait(false);
                    Output(json, children, LibrusViewsJsonContext.Default.IReadOnlyListChild, TextRenderer.Children);
                    return 0;
                }

            case "me":
                {
                    var me = await (await ChildAsync(session, args, ct)).GetMeAsync(ct).ConfigureAwait(false);
                    Console.WriteLine(JsonSerializer.Serialize(me, LibrusApiJsonContext.Default.SynergiaMeResponse));
                    return 0;
                }

            case "grades":
                {
                    var child = await ChildAsync(session, args, ct).ConfigureAwait(false);
                    if (raw)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(await child.GetGradesAsync(ct).ConfigureAwait(false), LibrusApiJsonContext.Default.GradesResponse));
                        return 0;
                    }

                    Output(json, await child.GetGradeViewsAsync(ct).ConfigureAwait(false), LibrusViewsJsonContext.Default.IReadOnlyListGradeView, TextRenderer.Grades);
                    return 0;
                }

            case "attendance":
                {
                    var child = await ChildAsync(session, args, ct).ConfigureAwait(false);
                    if (raw)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(await child.GetAttendancesAsync(ct).ConfigureAwait(false), LibrusApiJsonContext.Default.AttendancesResponse));
                        return 0;
                    }

                    var since = args.GetDate("--since") ?? DateOnly.FromDateTime(DateTime.Today.AddDays(-30));
                    Output(json, await child.GetAttendanceViewsAsync(since, ct).ConfigureAwait(false), LibrusViewsJsonContext.Default.IReadOnlyListAttendanceView, TextRenderer.Attendance);
                    return 0;
                }

            case "timetable":
                {
                    var child = await ChildAsync(session, args, ct).ConfigureAwait(false);
                    var week = ChildViews.WeekStart(args.GetDate("--week-start") ?? DateOnly.FromDateTime(DateTime.Today));
                    if (raw)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(await child.GetTimetablesAsync(weekStart: week, cancellationToken: ct).ConfigureAwait(false), LibrusApiJsonContext.Default.TimetablesResponse));
                        return 0;
                    }

                    Output(json, await child.GetTimetableViewAsync(week, ct).ConfigureAwait(false), LibrusViewsJsonContext.Default.IReadOnlyListTimetableDayView, TextRenderer.Timetable);
                    return 0;
                }

            case "messages":
                {
                    var child = await ChildAsync(session, args, ct).ConfigureAwait(false);
                    var page = await child.ListInboxAsync(args.Has("--unread"), 1, Math.Clamp(args.GetInt("--limit") ?? 30, 1, 300), includeBody: false, ct).ConfigureAwait(false);
                    Output(json, page.Messages, LibrusViewsJsonContext.Default.IReadOnlyListInboxMessage, TextRenderer.Messages);
                    return 0;
                }

            case "message":
                {
                    var child = await ChildAsync(session, args, ct).ConfigureAwait(false);
                    var id = args.Get("--id") ?? args.Positional.ElementAtOrDefault(1)
                        ?? throw new LibrusException(LibrusErrorCodes.InvalidArgument, "--id is required.");
                    Output(json, await child.GetInboxMessageAsync(id, ct).ConfigureAwait(false), LibrusViewsJsonContext.Default.InboxMessage, TextRenderer.Message);
                    return 0;
                }

            case "notes":
                Output(json, await (await ChildAsync(session, args, ct)).GetNoteViewsAsync(ct).ConfigureAwait(false), LibrusViewsJsonContext.Default.IReadOnlyListNoteView, TextRenderer.Notes);
                return 0;

            case "announcements":
                Output(json, await (await ChildAsync(session, args, ct)).GetAnnouncementViewsAsync(ct).ConfigureAwait(false), LibrusViewsJsonContext.Default.IReadOnlyListAnnouncementView, TextRenderer.Announcements);
                return 0;

            case "homework":
                {
                    var since = args.GetDate("--since") ?? DateOnly.FromDateTime(DateTime.Today);
                    Output(json, await (await ChildAsync(session, args, ct)).GetHomeworkViewsAsync(since, ct).ConfigureAwait(false), LibrusViewsJsonContext.Default.IReadOnlyListHomeworkView, TextRenderer.Homework);
                    return 0;
                }

            case "lucky-number":
                Output(json, await (await ChildAsync(session, args, ct)).GetLuckyNumberViewAsync(ct).ConfigureAwait(false), LibrusViewsJsonContext.Default.LuckyNumberView,
                    v => v.Number is null ? "(brak szczęśliwego numerka)" : $"Szczęśliwy numerek: {v.Number} ({v.Day})");
                return 0;

            case "notifications":
                {
                    var feed = await (await ChildAsync(session, args, ct)).GetChangeRegisterAsync(cancellationToken: ct).ConfigureAwait(false);
                    Console.WriteLine(JsonSerializer.Serialize(feed, LibrusApiJsonContext.Default.JsonElement));
                    return 0;
                }

            case "poll":
                return await PollAsync(args, session, http, loggerFactory, ct).ConfigureAwait(false);

            default:
                throw new LibrusException(LibrusErrorCodes.InvalidArgument, $"Unknown command \"{command}\". Run `librus-net help`.");
        }
    }

    private static async Task<int> PollAsync(CliArgs args, LibrusSession session, LibrusHttpOptions http, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Librus.Poller");
        var diff = new CollectionDiffSource(logger: logger);
        IChangeSource source = (args.Get("--strategy") ?? Environment.GetEnvironmentVariable("LIBRUS_POLL_STRATEGY") ?? "diff") switch
        {
            "diff" => diff,
            "changeregister" => new ChangeRegisterSource(),
            "auto" => new AutoChangeSource(new ChangeRegisterSource(), diff, logger),
            var other => throw new LibrusException(LibrusErrorCodes.InvalidArgument, $"Unknown --strategy \"{other}\" (diff|changeregister|auto)."),
        };

        var notifiers = new List<INotifier>();
        var requested = args.GetAll("--notifier");
        if (args.Has("--dry-run"))
        {
            requested = ["console"];
        }
        else if (requested.Count == 0)
        {
            requested = (Environment.GetEnvironmentVariable("LIBRUS_NOTIFIERS") ?? "ntfy,telegram")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        foreach (var name in requested)
        {
            INotifier? notifier = name switch
            {
                "console" => new ConsoleNotifier(),
                "ntfy" => NtfyNotifier.FromEnvironment(http),
                "telegram" => TelegramNotifier.FromEnvironment(http),
                _ => throw new LibrusException(LibrusErrorCodes.InvalidArgument, $"Unknown notifier \"{name}\" (ntfy|telegram|console)."),
            };
            if (notifier is null)
            {
                logger.LogWarning("poll.notifier.unconfigured {Notifier} (see README for the required environment variables)", name);
                continue;
            }

            notifiers.Add(notifier);
        }

        if (notifiers.Count == 0)
        {
            throw new LibrusException(LibrusErrorCodes.ConfigurationError,
                "No notifier configured. Set LIBRUS_NTFY_TOPIC, or LIBRUS_TELEGRAM_BOT_TOKEN + LIBRUS_TELEGRAM_CHAT_ID, or use --notifier console.");
        }

        var options = new PollerOptions
        {
            Children = args.GetAll("--child"),
            MinInterval = TimeSpan.FromMinutes(args.GetInt("--interval-min") ?? 12),
            MaxInterval = TimeSpan.FromMinutes(args.GetInt("--interval-max") ?? 15),
        };
        var poller = new PollerService(session, source, new FileStateStore(), new CompositeNotifier(notifiers), options, logger);
        if (args.Has("--once") || args.Has("--dry-run"))
        {
            var delivered = await poller.PollOnceAsync(ct).ConfigureAwait(false);
            logger.LogInformation("poll.once delivered={Delivered}", delivered);
            return 0;
        }

        await poller.RunAsync(ct).ConfigureAwait(false);
        return 0;
    }

    private static async Task<LibrusChildClient> ChildAsync(LibrusSession session, CliArgs args, CancellationToken ct)
    {
        var selector = args.Get("--child") ?? Environment.GetEnvironmentVariable("LIBRUS_CHILD");
        if (!string.IsNullOrWhiteSpace(selector))
        {
            return await session.ForChildAsync(selector, ct).ConfigureAwait(false);
        }

        var children = await session.ListChildrenAsync(ct: ct).ConfigureAwait(false);
        return children.Count == 1
            ? session.ForChild(children[0])
            : throw new LibrusException(LibrusErrorCodes.ChildRequired, "--child is required (id, login or first name); see `librus-net children`.");
    }

    private static void Output<T>(bool json, T value, JsonTypeInfo<T> typeInfo, Func<T, string> text) =>
        Console.WriteLine(json ? JsonSerializer.Serialize(value, typeInfo) : text(value).TrimEnd());

    private static void WriteError(LibrusException ex, bool json)
    {
        if (json)
        {
            using var stream = Console.OpenStandardError();
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            writer.WriteStartObject("error");
            writer.WriteString("code", ex.Code);
            writer.WriteString("message", ex.Message);
            if (ex.Endpoint is not null)
            {
                writer.WriteString("endpoint", UrlRedactor.Redact(ex.Endpoint));
            }

            if (ex.Status is { } status)
            {
                writer.WriteNumber("status", status);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.Flush();
            stream.WriteByte((byte)'\n');
            return;
        }

        Console.Error.WriteLine($"error [{ex.Code}]: {ex.Message}" + (ex.Status is { } s ? $" (HTTP {s})" : ""));
    }
}
