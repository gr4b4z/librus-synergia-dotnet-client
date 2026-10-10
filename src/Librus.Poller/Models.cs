using System.Text.Json.Serialization;

namespace Librus.Poller;

/// <summary>Something observed in a child's data that may or may not be new.</summary>
public sealed record ObservedItem(string Category, string Id, string Title, string? Body = null, string? Url = null);

/// <summary>A notification ready to be pushed to a sink.</summary>
public sealed record PendingNotification
{
    public required string Category { get; init; }
    public required string ItemId { get; init; }
    public required string Title { get; init; }
    public string? Body { get; init; }
    public string? Url { get; init; }
    public DateTimeOffset DetectedAt { get; init; }

    [JsonIgnore]
    public string Key => $"{Category}:{ItemId}";
}

public static class Categories
{
    public const string Messages = "messages";
    public const string Grades = "grades";
    public const string Notes = "notes";
    public const string Announcements = "announcements";
    public const string Homework = "homework";
    public const string ChangeRegister = "changeregister";

    public static readonly IReadOnlyList<string> Default = [Messages, Grades, Notes, Announcements, Homework];
}
