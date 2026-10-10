using Librus.Api;
using Librus.Poller.Notifiers;
using Librus.Poller.Sources;
using Librus.Poller.State;
using Librus.Portal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Librus.Poller;

public sealed record PollerOptions
{
    /// <summary>Lower bound of the jittered interval between polls (spec: 12–15 min).</summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromMinutes(12);

    public TimeSpan MaxInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Optional child selectors (id/login); empty = all linked children.</summary>
    public IReadOnlyList<string> Children { get; init; } = [];
}

/// <summary>
/// Sequential, jittered poll loop. Children are processed one after another (never in parallel), and the
/// session — hence the portal cookies and bearer tokens — is reused across ticks.
/// </summary>
public sealed class PollerService
{
    private readonly LibrusSession _session;
    private readonly IChangeSource _source;
    private readonly IStateStore _store;
    private readonly INotifier _notifier;
    private readonly PollerOptions _options;
    private readonly ILogger _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public PollerService(LibrusSession session, IChangeSource source, IStateStore store, INotifier notifier, PollerOptions? options = null,
        ILogger? logger = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _session = session;
        _source = source;
        _store = store;
        _notifier = notifier;
        _options = options ?? new PollerOptions();
        _logger = logger ?? NullLogger.Instance;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Runs until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (LibrusException ex) when (ex.Code == LibrusErrorCodes.ServiceMaintenance)
            {
                _logger.LogWarning("poll.maintenance: Librus is in maintenance, skipping this tick");
            }
            catch (LibrusException ex)
            {
                _logger.LogError("poll.tick.failed code={Code} status={Status} message={Message}", ex.Code, ex.Status, ex.Message);
            }

            var wait = NextInterval(Random.Shared.NextDouble());
            _logger.LogInformation("poll.sleep minutes={Minutes:F1}", wait.TotalMinutes);
            try
            {
                await _delay(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal TimeSpan NextInterval(double random)
    {
        var min = _options.MinInterval;
        var max = _options.MaxInterval < min ? min : _options.MaxInterval;
        return min + TimeSpan.FromTicks((long)((max - min).Ticks * random));
    }

    /// <summary>One pass over all selected children. Returns the number of delivered notifications.</summary>
    public async Task<int> PollOnceAsync(CancellationToken ct = default)
    {
        var children = await SelectChildrenAsync(ct).ConfigureAwait(false);
        var delivered = 0;
        foreach (var child in children)
        {
            delivered += await PollChildAsync(_session.ForChild(child), ct).ConfigureAwait(false);
        }

        return delivered;
    }

    internal async Task<int> PollChildAsync(LibrusChildClient client, CancellationToken ct)
    {
        var state = await _store.LoadAsync(client.Child.Id, ct).ConfigureAwait(false);

        // 1. Deliver anything a previous (interrupted) run already detected.
        var delivered = await DrainOutboxAsync(state, ct).ConfigureAwait(false);

        // 2. Detect, then checkpoint *before* notifying.
        IReadOnlyList<PendingNotification> fresh;
        try
        {
            fresh = await _source.DetectAsync(client, state, ct).ConfigureAwait(false);
        }
        catch (LibrusException ex) when (ex.Code != LibrusErrorCodes.ServiceMaintenance)
        {
            _logger.LogWarning("poll.child.failed child={ChildId} code={Code} status={Status}", client.Child.Id, ex.Code, ex.Status);
            return delivered;
        }

        var queued = new HashSet<string>(state.Outbox.Select(o => o.Key), StringComparer.Ordinal);
        state.Outbox.AddRange(fresh.Where(n => queued.Add(n.Key)));
        await _store.SaveAsync(state, ct).ConfigureAwait(false);
        _logger.LogInformation("poll.child.done child={ChildId} source={Source} new={New}", client.Child.Id, _source.Name, fresh.Count);

        // 3. Deliver.
        delivered += await DrainOutboxAsync(state, ct).ConfigureAwait(false);
        return delivered;
    }

    private async Task<int> DrainOutboxAsync(ChildState state, CancellationToken ct)
    {
        var delivered = 0;
        while (state.Outbox.Count > 0)
        {
            var next = state.Outbox[0];
            try
            {
                await _notifier.SendAsync(next, ct).ConfigureAwait(false);
            }
            catch (LibrusException ex)
            {
                _logger.LogWarning("poll.notify.failed child={ChildId} notifier={Notifier} code={Code}; will retry next tick",
                    state.ChildId, _notifier.Name, ex.Code);
                break;
            }

            state.Outbox.RemoveAt(0);
            await _store.SaveAsync(state, ct).ConfigureAwait(false);
            delivered++;
        }

        return delivered;
    }

    private async Task<IReadOnlyList<Child>> SelectChildrenAsync(CancellationToken ct)
    {
        if (_options.Children.Count == 0)
        {
            return await _session.ListChildrenAsync(ct: ct).ConfigureAwait(false);
        }

        var list = new List<Child>();
        foreach (var selector in _options.Children)
        {
            list.Add(await _session.ResolveChildAsync(selector, ct).ConfigureAwait(false));
        }

        return list;
    }
}
