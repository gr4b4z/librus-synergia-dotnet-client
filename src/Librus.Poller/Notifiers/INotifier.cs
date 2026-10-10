namespace Librus.Poller.Notifiers;

/// <summary>A push sink for poller notifications.</summary>
public interface INotifier
{
    string Name { get; }

    Task SendAsync(PendingNotification notification, CancellationToken ct = default);
}

/// <summary>Fans a notification out to several sinks; fails if any sink fails (so it is retried).</summary>
public sealed class CompositeNotifier(IReadOnlyList<INotifier> notifiers) : INotifier
{
    public string Name => string.Join("+", notifiers.Select(n => n.Name));

    public async Task SendAsync(PendingNotification notification, CancellationToken ct = default)
    {
        foreach (var notifier in notifiers)
        {
            await notifier.SendAsync(notification, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>Writes notifications to stdout (dry-run / cron logs).</summary>
public sealed class ConsoleNotifier(TextWriter? writer = null) : INotifier
{
    private readonly TextWriter _writer = writer ?? Console.Out;

    public string Name => "console";

    public Task SendAsync(PendingNotification notification, CancellationToken ct = default)
    {
        _writer.WriteLine($"[{notification.Category}] {notification.Title}");
        if (!string.IsNullOrEmpty(notification.Body))
        {
            _writer.WriteLine("    " + notification.Body.ReplaceLineEndings(" "));
        }

        if (!string.IsNullOrEmpty(notification.Url))
        {
            _writer.WriteLine("    " + notification.Url);
        }

        return Task.CompletedTask;
    }
}
