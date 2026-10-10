using System.Globalization;
using Librus.Api;
using Librus.Api.Views;
using Librus.Poller.State;
using Microsoft.Extensions.Logging;

namespace Librus.Poller.Sources;

/// <summary>
/// Strategy B: fetch a few small collections (unread-capable inbox, grades, notes, notices, homework)
/// and diff their ids against stored state. Categories are fetched sequentially; a failing category is
/// logged and skipped without touching its stored state.
/// </summary>
public sealed class CollectionDiffSource : IChangeSource
{
    private readonly IReadOnlyCollection<string> _categories;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public CollectionDiffSource(IReadOnlyCollection<string>? categories = null, ILogger? logger = null, TimeProvider? time = null)
    {
        _categories = categories ?? Categories.Default;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    public string Name => "diff";

    public async Task<IReadOnlyList<PendingNotification>> DetectAsync(LibrusChildClient client, ChildState state, CancellationToken ct = default)
    {
        var result = new List<PendingNotification>();
        foreach (var category in _categories)
        {
            IReadOnlyList<ObservedItem> observed;
            try
            {
                observed = await ObserveAsync(client, category, ct).ConfigureAwait(false);
            }
            catch (LibrusException ex)
            {
                _logger.LogWarning("poll.category.failed child={ChildId} category={Category} code={Code} status={Status}",
                    client.Child.Id, category, ex.Code, ex.Status);
                continue;
            }

            if (!state.Categories.TryGetValue(category, out var categoryState))
            {
                categoryState = new CategoryState();
                state.Categories[category] = categoryState;
            }

            var baseline = !categoryState.Initialized;
            var fresh = SnapshotDiff.Apply(categoryState, observed);
            if (baseline)
            {
                _logger.LogInformation("poll.baseline child={ChildId} category={Category} items={Count}", client.Child.Id, category, observed.Count);
            }

            result.AddRange(fresh.Select(item => new PendingNotification
            {
                Category = category,
                ItemId = item.Id,
                Title = item.Title,
                Body = item.Body,
                Url = item.Url,
                DetectedAt = _time.GetUtcNow(),
            }));
        }

        return result;
    }

    internal async Task<IReadOnlyList<ObservedItem>> ObserveAsync(LibrusChildClient client, string category, CancellationToken ct)
    {
        var who = FirstName(client.Child.StudentName);
        switch (category)
        {
            case Categories.Messages:
                {
                    var page = await client.ListInboxAsync(limit: 30, ct: ct).ConfigureAwait(false);
                    return page.Messages
                        .Where(m => !m.IsRead)
                        .Select(m => new ObservedItem(category, m.Id,
                            $"{who}: wiadomość — {m.Subject ?? "(bez tematu)"}",
                            m.SenderName is null ? null : $"Od: {m.SenderName}",
                            m.WebUrl))
                        .ToList();
                }

            case Categories.Grades:
                {
                    var grades = await client.GetGradesAsync(ct).ConfigureAwait(false);
                    var lookups = await client.GetLookupsAsync(ct).ConfigureAwait(false);
                    return grades.Grades.Select(g =>
                    {
                        var subject = lookups.Subject(g.Subject?.Id?.Value);
                        var category = lookups.GradeCategory(g.Category?.Id?.Value);
                        var kind = g.IsFinal ? " (końcowa)" : g.IsSemester ? " (semestralna)" : g.IsFinalProposition || g.IsSemesterProposition ? " (propozycja)" : "";
                        return new ObservedItem(Categories.Grades, g.Id.ToString(CultureInfo.InvariantCulture),
                            $"{who}: ocena {g.GradeValue}{kind} — {subject ?? "przedmiot"}",
                            string.Join(" · ", new[] { category, g.Date }.Where(s => !string.IsNullOrEmpty(s))),
                            "https://synergia.librus.pl/przegladaj_oceny/uczen");
                    }).ToList();
                }

            case Categories.Notes:
                {
                    var notes = await client.ListNotesAsync(ct).ConfigureAwait(false);
                    return notes.Notes.Select(n => new ObservedItem(category, n.Id.Value,
                        $"{who}: nowa uwaga",
                        Text(n.AdditionalProperties, "Text", "Content"),
                        "https://synergia.librus.pl/uwagi")).ToList();
                }

            case Categories.Announcements:
                {
                    var notices = await client.ListSchoolNoticesAsync(ct).ConfigureAwait(false);
                    return notices.SchoolNotices.Select(n => new ObservedItem(category, n.Id.Value,
                        $"{who}: ogłoszenie — {Text(n.AdditionalProperties, "Subject") ?? "(bez tytułu)"}",
                        Truncate(Text(n.AdditionalProperties, "Content"), 300),
                        "https://synergia.librus.pl/ogloszenia")).ToList();
                }

            case Categories.Homework:
                {
                    var homework = await client.GetHomeWorksAsync(ct).ConfigureAwait(false);
                    var lookups = await client.GetLookupsAsync(ct).ConfigureAwait(false);
                    return homework.HomeWorks.Select(h => new ObservedItem(category, h.Id.ToString(CultureInfo.InvariantCulture),
                        $"{who}: {lookups.Subject(h.Subject?.Id?.Value) ?? "zadanie"} na {h.Date}",
                        Truncate(h.Content, 300),
                        "https://synergia.librus.pl/terminarz")).ToList();
                }

            default:
                throw new LibrusException(LibrusErrorCodes.ConfigurationError, $"Unknown poll category \"{category}\".");
        }
    }

    private static string FirstName(string studentName)
    {
        var trimmed = studentName.Trim();
        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 ? trimmed[..space] : trimmed.Length > 0 ? trimmed : "Librus";
    }

    private static string? Text(Dictionary<string, System.Text.Json.JsonElement>? data, params string[] keys) =>
        Librus.Api.Models.SynergiaEntityText.Get(data, keys);

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max] + "…";
}
