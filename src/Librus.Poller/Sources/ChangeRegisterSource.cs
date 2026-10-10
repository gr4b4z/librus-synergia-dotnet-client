using System.Globalization;
using System.Text.Json;
using Librus.Api;
using Librus.Poller.State;

namespace Librus.Poller.Sources;

/// <summary>
/// Strategy A: the official app's change feed (<c>GET /ChangeRegister</c>), one cheap call per child.
/// The app keeps the last seen entry <c>Id</c> as a cursor and treats higher ids as new.
///
/// TODO(verify): the response shape and whether the portal bearer may read it are unconfirmed. This
/// parser accepts any object with an array of entries carrying a numeric <c>Id</c>; anything else throws
/// <see cref="LibrusErrorCodes.ResponseValidationFailed"/> so <see cref="AutoChangeSource"/> falls back
/// to Strategy B.
/// </summary>
public sealed class ChangeRegisterSource : IChangeSource
{
    private readonly TimeProvider _time;

    public ChangeRegisterSource(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public string Name => "changeregister";

    public async Task<IReadOnlyList<PendingNotification>> DetectAsync(LibrusChildClient client, ChildState state, CancellationToken ct = default)
    {
        var root = await client.GetChangeRegisterAsync(cancellationToken: ct).ConfigureAwait(false);
        var entries = ParseEntries(root);
        var who = client.Child.StudentName;
        var cursor = state.ChangeRegisterCursor;
        var max = entries.Count == 0 ? cursor ?? 0 : Math.Max(cursor ?? 0, entries.Max(e => e.Id));
        state.ChangeRegisterCursor = max;
        if (cursor is null)
        {
            return [];
        }

        return entries
            .Where(e => e.Id > cursor)
            .OrderBy(e => e.Id)
            .Select(e => new PendingNotification
            {
                Category = Categories.ChangeRegister,
                ItemId = e.Id.ToString(CultureInfo.InvariantCulture),
                Title = $"{who}: zmiana w dzienniku ({e.Kind})",
                Body = e.Description,
                Url = "https://synergia.librus.pl/",
                DetectedAt = _time.GetUtcNow(),
            })
            .ToList();
    }

    internal static IReadOnlyList<ChangeEntry> ParseEntries(JsonElement root)
    {
        var array = root.ValueKind switch
        {
            JsonValueKind.Array => root,
            JsonValueKind.Object => root.EnumerateObject()
                .Select(p => p.Value)
                .FirstOrDefault(v => v.ValueKind == JsonValueKind.Array && v.EnumerateArray().All(i => i.ValueKind == JsonValueKind.Object)),
            _ => default,
        };

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new LibrusException(LibrusErrorCodes.ResponseValidationFailed, "ChangeRegister response has no entry array.");
        }

        var result = new List<ChangeEntry>();
        foreach (var entry in array.EnumerateArray())
        {
            if (!entry.TryGetProperty("Id", out var idElement) || !TryGetLong(idElement, out var id))
            {
                throw new LibrusException(LibrusErrorCodes.ResponseValidationFailed, "ChangeRegister entry without a numeric Id.");
            }

            var kind = FirstString(entry, "Type", "Resource", "Provider", "Name") ?? "zmiana";
            result.Add(new ChangeEntry(id, kind, FirstString(entry, "Description", "Message", "Title")));
        }

        return result;
    }

    private static bool TryGetLong(JsonElement element, out long value)
    {
        value = 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    private static string? FirstString(JsonElement entry, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (entry.TryGetProperty(key, out var v))
            {
                if (v.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(v.GetString()))
                {
                    return v.GetString();
                }

                if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("Url", out var url) && url.ValueKind == JsonValueKind.String)
                {
                    var s = url.GetString()!;
                    return s[(s.TrimEnd('/').LastIndexOf('/') + 1)..];
                }
            }
        }

        return null;
    }

    internal sealed record ChangeEntry(long Id, string Kind, string? Description);
}

/// <summary>Tries Strategy A per child; on any Librus error permanently falls back to Strategy B for that child.</summary>
public sealed class AutoChangeSource : IChangeSource
{
    private readonly ChangeRegisterSource _primary;
    private readonly CollectionDiffSource _fallback;
    private readonly HashSet<int> _fallbackChildren = [];
    private readonly Microsoft.Extensions.Logging.ILogger _logger;

    public AutoChangeSource(ChangeRegisterSource primary, CollectionDiffSource fallback, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        _primary = primary;
        _fallback = fallback;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public string Name => "auto";

    public async Task<IReadOnlyList<PendingNotification>> DetectAsync(LibrusChildClient client, ChildState state, CancellationToken ct = default)
    {
        if (!_fallbackChildren.Contains(client.Child.Id))
        {
            try
            {
                return await _primary.DetectAsync(client, state, ct).ConfigureAwait(false);
            }
            catch (LibrusException ex) when (ex.Code != LibrusErrorCodes.NetworkError && ex.Code != LibrusErrorCodes.NetworkTimeout && ex.Code != LibrusErrorCodes.ServiceMaintenance)
            {
                Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(_logger,
                    "poll.changeregister.unavailable child={ChildId} code={Code} status={Status}; falling back to collection diff",
                    client.Child.Id, ex.Code, ex.Status);
                _fallbackChildren.Add(client.Child.Id);
            }
        }

        return await _fallback.DetectAsync(client, state, ct).ConfigureAwait(false);
    }
}
