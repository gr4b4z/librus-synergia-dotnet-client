using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Librus.Api.Views;

public sealed record GradeView(string Id, string? Subject, string Grade, string? Category, string? Date, long Semester, string Kind, string? Teacher, string? Comment);

public sealed record AttendanceView(string Id, string Date, long LessonNo, string Type, string? TypeShort, bool? Presence, string? Subject);

public sealed record TimetableLessonView(string? LessonNo, string? From, string? To, string? Subject, string? Teacher, string? Classroom, bool Canceled, bool Substitution);

public sealed record TimetableDayView(string Date, IReadOnlyList<TimetableLessonView> Lessons);

public sealed record HomeworkView(string Id, string Date, string? Subject, string? Category, string? Content, string? Teacher, string? AddDate);

public sealed record NoteView(string Id, string? Date, string? Category, string? Text, string? Teacher, bool? Positive);

public sealed record AnnouncementView(string Id, string? Subject, string? Content, string? StartDate, string? EndDate, string? Author);

public sealed record LuckyNumberView(string? Number, string? Day);

/// <summary>Compact, name-resolved projections for humans and LLMs (raw DTOs remain available).</summary>
public static class ChildViews
{
    public static async Task<ChildLookups> GetLookupsAsync(this LibrusChildClient client, CancellationToken ct = default)
    {
        var holder = client.Session.GetOrAddComponent($"lookups:{client.Child.Id}", () => new LookupsHolder());
        await holder.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (holder.Value is null || DateTimeOffset.UtcNow - holder.Value.LoadedAt > ChildLookups.Ttl)
            {
                holder.Value = await ChildLookups.LoadAsync(client, ct).ConfigureAwait(false);
            }

            return holder.Value;
        }
        finally
        {
            holder.Gate.Release();
        }
    }

    public static async Task<IReadOnlyList<GradeView>> GetGradeViewsAsync(this LibrusChildClient client, CancellationToken ct = default)
    {
        var grades = await client.GetGradesAsync(ct).ConfigureAwait(false);
        var l = await client.GetLookupsAsync(ct).ConfigureAwait(false);
        return grades.Grades
            .OrderByDescending(g => g.Date, StringComparer.Ordinal)
            .Select(g => new GradeView(
                g.Id.ToString(CultureInfo.InvariantCulture),
                l.Subject(g.Subject?.Id?.Value),
                g.GradeValue,
                l.GradeCategory(g.Category?.Id?.Value),
                g.Date,
                g.Semester,
                g.IsFinal ? "final" : g.IsFinalProposition ? "final-proposition" : g.IsSemester ? "semester" : g.IsSemesterProposition ? "semester-proposition" : "regular",
                l.Teacher(g.AddedBy?.Id?.Value),
                null))
            .ToList();
    }

    public static async Task<IReadOnlyList<AttendanceView>> GetAttendanceViewsAsync(this LibrusChildClient client, DateOnly? since = null, CancellationToken ct = default)
    {
        var data = await client.GetAttendancesAsync(ct).ConfigureAwait(false);
        var l = await client.GetLookupsAsync(ct).ConfigureAwait(false);
        var sinceText = since?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return data.Attendances
            .Where(a => sinceText is null || string.CompareOrdinal(a.Date, sinceText) >= 0)
            .OrderByDescending(a => a.Date, StringComparer.Ordinal).ThenBy(a => a.LessonNo)
            .Select(a =>
            {
                var type = l.AttendanceType(a.Type?.Id?.Value);
                return new AttendanceView(a.Id.Value, a.Date, a.LessonNo, type?.Name ?? a.Type?.Id?.Value ?? "?", type?.Short, type?.Presence, l.SubjectOfLesson(a.Lesson?.Id?.Value));
            })
            .ToList();
    }

    public static async Task<IReadOnlyList<TimetableDayView>> GetTimetableViewAsync(this LibrusChildClient client, DateOnly weekStart, CancellationToken ct = default)
    {
        var data = await client.GetTimetablesAsync(weekStart: weekStart, cancellationToken: ct).ConfigureAwait(false);
        return data.Timetable
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new TimetableDayView(kv.Key, kv.Value
                .SelectMany(slot => slot)
                .Select(lesson =>
                {
                    var extra = lesson.AdditionalProperties;
                    var teacher = lesson.Teacher?.AdditionalProperties;
                    return new TimetableLessonView(
                        extra.GetStringOrNumber("LessonNo"),
                        extra.GetStringOrNumber("HourFrom"),
                        extra.GetStringOrNumber("HourTo"),
                        lesson.Subject?.AdditionalProperties.GetStringOrNumber("Name"),
                        $"{teacher.GetStringOrNumber("FirstName")} {teacher.GetStringOrNumber("LastName")}".Trim() is { Length: > 0 } t ? t : null,
                        extra.GetObject("Classroom").GetStringOrNumber("Name", "Symbol"),
                        extra.GetBool("IsCanceled") ?? false,
                        extra.GetBool("IsSubstitutionClass") ?? false);
                })
                .ToList()))
            .ToList();
    }

    public static async Task<IReadOnlyList<HomeworkView>> GetHomeworkViewsAsync(this LibrusChildClient client, DateOnly? since = null, CancellationToken ct = default)
    {
        var data = await client.GetHomeWorksAsync(ct).ConfigureAwait(false);
        var l = await client.GetLookupsAsync(ct).ConfigureAwait(false);
        var sinceText = since?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return data.HomeWorks
            .Where(h => sinceText is null || string.CompareOrdinal(h.Date, sinceText) >= 0)
            .OrderBy(h => h.Date, StringComparer.Ordinal)
            .Select(h => new HomeworkView(
                h.Id.ToString(CultureInfo.InvariantCulture),
                h.Date,
                l.Subject(h.Subject?.Id?.Value),
                l.HomeworkCategory(h.Category?.Id?.Value) ?? h.Category?.AdditionalProperties.GetStringOrNumber("Name"),
                h.Content,
                l.Teacher(h.CreatedBy?.Id?.Value),
                h.AddDate))
            .ToList();
    }

    public static async Task<IReadOnlyList<NoteView>> GetNoteViewsAsync(this LibrusChildClient client, CancellationToken ct = default)
    {
        var data = await client.ListNotesAsync(ct).ConfigureAwait(false);
        var l = await client.GetLookupsAsync(ct).ConfigureAwait(false);
        return data.Notes
            .Select(n => new NoteView(
                n.Id.Value,
                n.AdditionalProperties.GetStringOrNumber("Date", "AddDate"),
                l.NoteCategory(n.Category?.Id?.Value),
                n.AdditionalProperties.GetStringOrNumber("Text", "Content"),
                l.Teacher((n.Teacher ?? n.AddedBy)?.Id?.Value),
                n.AdditionalProperties.GetStringOrNumber("Positive") is { } p ? p is "1" or "true" : null))
            .OrderByDescending(n => n.Date, StringComparer.Ordinal)
            .ToList();
    }

    public static async Task<IReadOnlyList<AnnouncementView>> GetAnnouncementViewsAsync(this LibrusChildClient client, CancellationToken ct = default)
    {
        var data = await client.ListSchoolNoticesAsync(ct).ConfigureAwait(false);
        var l = await client.GetLookupsAsync(ct).ConfigureAwait(false);
        return data.SchoolNotices
            .Select(n => new AnnouncementView(
                n.Id.Value,
                n.AdditionalProperties.GetStringOrNumber("Subject"),
                n.AdditionalProperties.GetStringOrNumber("Content"),
                n.StartDate,
                n.EndDate,
                l.Teacher(n.AddedBy?.Id?.Value)))
            .OrderByDescending(n => n.StartDate, StringComparer.Ordinal)
            .ToList();
    }

    public static async Task<LuckyNumberView> GetLuckyNumberViewAsync(this LibrusChildClient client, CancellationToken ct = default)
    {
        var data = await client.GetLuckyNumberAsync(cancellationToken: ct).ConfigureAwait(false);
        return new LuckyNumberView(data.LuckyNumber.LuckyNumberValue?.Value, data.LuckyNumber.LuckyNumberDay);
    }

    /// <summary>Monday of the week containing <paramref name="date"/>.</summary>
    public static DateOnly WeekStart(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private sealed class LookupsHolder : IDisposable
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public ChildLookups? Value { get; set; }

        public void Dispose() => Gate.Dispose();
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IReadOnlyList<GradeView>))]
[JsonSerializable(typeof(IReadOnlyList<AttendanceView>))]
[JsonSerializable(typeof(IReadOnlyList<TimetableDayView>))]
[JsonSerializable(typeof(IReadOnlyList<HomeworkView>))]
[JsonSerializable(typeof(IReadOnlyList<NoteView>))]
[JsonSerializable(typeof(IReadOnlyList<AnnouncementView>))]
[JsonSerializable(typeof(LuckyNumberView))]
[JsonSerializable(typeof(IReadOnlyList<Messages.InboxMessage>))]
[JsonSerializable(typeof(Messages.InboxMessage))]
[JsonSerializable(typeof(Messages.ReplyDraft))]
[JsonSerializable(typeof(IReadOnlyList<Portal.Child>))]
[JsonSerializable(typeof(Portal.Child))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class LibrusViewsJsonContext : JsonSerializerContext;
