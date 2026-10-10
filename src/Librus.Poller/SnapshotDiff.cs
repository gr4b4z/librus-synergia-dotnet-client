using Librus.Poller.State;

namespace Librus.Poller;

/// <summary>Pure diff of an observed snapshot against stored seen-ids (Strategy B).</summary>
public static class SnapshotDiff
{
    /// <summary>
    /// Returns items whose id was not seen before and records all observed ids. The first observation
    /// of a category only seeds the baseline and returns nothing, so enabling the poller does not
    /// replay the whole school year.
    /// </summary>
    public static IReadOnlyList<ObservedItem> Apply(CategoryState state, IReadOnlyList<ObservedItem> observed)
    {
        var seen = new HashSet<string>(state.Seen, StringComparer.Ordinal);
        var fresh = new List<ObservedItem>();
        foreach (var item in observed)
        {
            if (seen.Add(item.Id))
            {
                state.Seen.Add(item.Id);
                if (state.Initialized)
                {
                    fresh.Add(item);
                }
            }
        }

        state.Initialized = true;
        if (state.Seen.Count > ChildState.MaxSeenPerCategory)
        {
            state.Seen.RemoveRange(0, state.Seen.Count - ChildState.MaxSeenPerCategory);
        }

        return fresh;
    }
}
