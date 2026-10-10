namespace Librus.Api.Views;

/// <summary>
/// Dictionary data (subjects, teachers, categories, attendance types, lessons) used to turn id references
/// into names. Loaded lazily per child and cached for 24 h so polling stays cheap.
/// </summary>
public sealed class ChildLookups
{
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    private readonly Dictionary<string, string> _subjects;
    private readonly Dictionary<string, string> _teachers;
    private readonly Dictionary<string, string> _gradeCategories;
    private readonly Dictionary<string, (string Name, string? Short, bool? Presence)> _attendanceTypes;
    private readonly Dictionary<string, string?> _lessonSubjects;
    private readonly Dictionary<string, string> _noteCategories;
    private readonly Dictionary<string, string> _homeworkCategories;

    internal ChildLookups(
        Dictionary<string, string> subjects,
        Dictionary<string, string> teachers,
        Dictionary<string, string> gradeCategories,
        Dictionary<string, (string Name, string? Short, bool? Presence)> attendanceTypes,
        Dictionary<string, string?> lessonSubjects,
        Dictionary<string, string> noteCategories,
        Dictionary<string, string> homeworkCategories,
        DateTimeOffset loadedAt)
    {
        _subjects = subjects;
        _teachers = teachers;
        _gradeCategories = gradeCategories;
        _attendanceTypes = attendanceTypes;
        _lessonSubjects = lessonSubjects;
        _noteCategories = noteCategories;
        _homeworkCategories = homeworkCategories;
        LoadedAt = loadedAt;
    }

    public DateTimeOffset LoadedAt { get; }

    public static ChildLookups Empty { get; } = new([], [], [], [], [], [], [], DateTimeOffset.MinValue);

    public string? Subject(string? id) => Get(_subjects, id);

    public string? Teacher(string? id) => Get(_teachers, id);

    public string? GradeCategory(string? id) => Get(_gradeCategories, id);

    public string? NoteCategory(string? id) => Get(_noteCategories, id);

    public string? HomeworkCategory(string? id) => Get(_homeworkCategories, id);

    public (string Name, string? Short, bool? Presence)? AttendanceType(string? id) =>
        id is not null && _attendanceTypes.TryGetValue(id, out var t) ? t : null;

    public string? SubjectOfLesson(string? lessonId) =>
        lessonId is not null && _lessonSubjects.TryGetValue(lessonId, out var s) ? Subject(s) : null;

    private static string? Get(Dictionary<string, string> map, string? id) => id is not null && map.TryGetValue(id, out var v) ? v : null;

    internal static async Task<ChildLookups> LoadAsync(LibrusChildClient client, CancellationToken ct)
    {
        // Sequential on purpose (spec §9: no bursts). Each dictionary is optional.
        var subjects = await TryAsync(async () => (await client.ListSubjectsAsync(ct).ConfigureAwait(false)).Subjects
            .Select(s => (Id: s.GetString("Id"), Name: s.GetString("Name"))).Where(x => x.Id is not null && x.Name is not null)
            .ToDictionary(x => x.Id!, x => x.Name!)).ConfigureAwait(false);
        var teachers = await TryAsync(async () => (await client.ListUsersAsync(ct).ConfigureAwait(false)).Users
            .Select(u => (Id: u.GetString("Id"), Name: $"{u.GetString("FirstName")} {u.GetString("LastName")}".Trim()))
            .Where(x => x.Id is not null && x.Name.Length > 0).ToDictionary(x => x.Id!, x => x.Name)).ConfigureAwait(false);
        var gradeCategories = await TryAsync(async () => Names((await client.GetGradeCategoriesAsync(ct).ConfigureAwait(false)).Categories)).ConfigureAwait(false);
        var attendanceTypes = await TryAsync(async () => (await client.GetAttendanceTypesAsync(ct).ConfigureAwait(false)).Types
            .Where(t => t.GetString("Id") is not null)
            .ToDictionary(t => t.GetString("Id")!, t => (t.GetString("Name") ?? t.GetString("Short") ?? "?", t.GetString("Short"), t.AdditionalProperties.GetBool("IsPresenceKind")))).ConfigureAwait(false);
        var lessons = await TryAsync(async () => (await client.ListLessonsAsync(ct).ConfigureAwait(false)).Lessons
            .Where(l => !l.Id.IsEmpty).ToDictionary(l => l.Id.Value, l => l.Subject?.Id?.Value)).ConfigureAwait(false);
        var noteCategories = await TryAsync(async () => Names((await client.ListNoteCategoriesAsync(ct).ConfigureAwait(false)).Categories)).ConfigureAwait(false);
        var homeworkCategories = await TryAsync(async () => Names((await client.GetHomeworkCategoriesAsync(ct).ConfigureAwait(false)).Categories)).ConfigureAwait(false);
        return new ChildLookups(subjects, teachers, gradeCategories, attendanceTypes, lessons, noteCategories, homeworkCategories, DateTimeOffset.UtcNow);

        static Dictionary<string, string> Names(IEnumerable<SynergiaEntity> items) => items
            .Select(c => (Id: c.GetString("Id"), Name: c.GetString("Name"))).Where(x => x.Id is not null && x.Name is not null)
            .GroupBy(x => x.Id!).ToDictionary(g => g.Key, g => g.First().Name!);
    }

    private static async Task<Dictionary<string, T>> TryAsync<T>(Func<Task<Dictionary<string, T>>> load)
    {
        try
        {
            return await load().ConfigureAwait(false);
        }
        catch (LibrusException ex) when (ex.Status is 403 or 404)
        {
            return [];
        }
    }
}
