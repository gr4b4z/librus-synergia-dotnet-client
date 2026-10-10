using System.Text;
using Librus.Api.Messages;
using Librus.Api.Views;
using Librus.Portal;

namespace Librus.Cli;

/// <summary>Human-readable renderings for <c>--format text</c> (the default).</summary>
internal static class TextRenderer
{
    public static string Children(IReadOnlyList<Child> children) => Table(
        ["ID", "LOGIN", "NAME", "STATE"],
        children.Select(c => new[] { c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), c.Login, c.StudentName, c.State }));

    public static string Grades(IReadOnlyList<GradeView> grades) => Table(
        ["DATE", "SUBJECT", "GRADE", "CATEGORY", "KIND"],
        grades.Select(g => new[] { g.Date ?? "", g.Subject ?? "?", g.Grade, g.Category ?? "", g.Kind == "regular" ? "" : g.Kind }));

    public static string Attendance(IReadOnlyList<AttendanceView> items) => Table(
        ["DATE", "NO", "TYPE", "SUBJECT"],
        items.Select(a => new[] { a.Date, a.LessonNo.ToString(System.Globalization.CultureInfo.InvariantCulture), a.Type, a.Subject ?? "" }));

    public static string Timetable(IReadOnlyList<TimetableDayView> days)
    {
        var sb = new StringBuilder();
        foreach (var day in days)
        {
            sb.AppendLine(day.Date);
            if (day.Lessons.Count == 0)
            {
                sb.AppendLine("  (brak lekcji)");
            }

            foreach (var l in day.Lessons)
            {
                var flags = (l.Canceled ? " [ODWOŁANA]" : "") + (l.Substitution ? " [ZASTĘPSTWO]" : "");
                sb.AppendLine($"  {l.LessonNo,2}. {l.From}-{l.To}  {l.Subject}  ({l.Teacher}{(l.Classroom is null ? "" : ", s. " + l.Classroom)}){flags}");
            }
        }

        return sb.ToString();
    }

    public static string Messages(IReadOnlyList<InboxMessage> messages) => Table(
        ["ID", "SENT", "", "FROM", "SUBJECT"],
        messages.Select(m => new[] { m.Id, m.SentAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "", m.IsRead ? "" : "*", m.SenderName ?? m.SenderId ?? "", m.Subject ?? "" }));

    public static string Message(InboxMessage m) =>
        $"Od:     {m.SenderName ?? m.SenderId}\nData:   {m.SentAt?.ToLocalTime():yyyy-MM-dd HH:mm}\nTemat:  {m.Subject}\nLink:   {m.WebUrl}\n\n{m.Body}\n";

    public static string Notes(IReadOnlyList<NoteView> notes) => string.Join("\n", notes.Select(n =>
        $"{n.Date}  {n.Category}{(n.Positive == true ? " (+)" : "")}  {n.Teacher}\n    {n.Text}"));

    public static string Announcements(IReadOnlyList<AnnouncementView> items) => string.Join("\n", items.Select(a =>
        $"{a.StartDate}  {a.Subject}  ({a.Author})\n    {a.Content?.ReplaceLineEndings(" ")}"));

    public static string Homework(IReadOnlyList<HomeworkView> items) => Table(
        ["DATE", "SUBJECT", "CATEGORY", "CONTENT"],
        items.Select(h => new[] { h.Date, h.Subject ?? "", h.Category ?? "", (h.Content ?? "").ReplaceLineEndings(" ") }));

    private static string Table(string[] headers, IEnumerable<string[]> rows)
    {
        var all = rows.ToList();
        if (all.Count == 0)
        {
            return "(brak danych)";
        }

        var widths = headers.Select((h, i) => Math.Min(60, Math.Max(h.Length, all.Max(r => r[i].Length)))).ToArray();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))).TrimEnd());
        foreach (var row in all)
        {
            sb.AppendLine(string.Join("  ", row.Select((c, i) => (c.Length > widths[i] ? c[..(widths[i] - 1)] + "…" : c).PadRight(widths[i]))).TrimEnd());
        }

        return sb.ToString();
    }
}
