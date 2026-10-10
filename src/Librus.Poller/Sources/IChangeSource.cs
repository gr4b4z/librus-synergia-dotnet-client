using Librus.Api;
using Librus.Poller.State;

namespace Librus.Poller.Sources;

/// <summary>A "what's new" strategy for one child (spec §5a/§6). Mutates <paramref name="state"/> in place.</summary>
public interface IChangeSource
{
    string Name { get; }

    Task<IReadOnlyList<PendingNotification>> DetectAsync(LibrusChildClient client, ChildState state, CancellationToken ct = default);
}
