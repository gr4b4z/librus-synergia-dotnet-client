using System.Text.Json;
using Librus.Api.Models;
using Librus.Api.Views;

namespace Librus.Tests;

public class DtoTests
{
    [Fact]
    public void LibrusId_accepts_numbers_and_strings()
    {
        var json = """{"Resources":{},"Url":"u","Notes":[{"Id":12,"Text":"a"},{"Id":"13","Text":"b","Category":{"Id":"4","Url":"x"}}]}""";
        var notes = JsonSerializer.Deserialize(json, LibrusApiJsonContext.Default.NotesResponse)!;
        Assert.Equal(["12", "13"], notes.Notes.Select(n => n.Id.Value));
        Assert.Equal("4", notes.Notes[1].Category?.Id?.Value);
        Assert.Equal("b", notes.Notes[1].AdditionalProperties!["Text"].GetString());
    }

    [Fact]
    public void Timetable_is_a_dictionary_of_lesson_slots()
    {
        var json = """
            {"Resources":{},"Url":"u","Timetable":{
              "2026-10-05":[[],[{"LessonNo":"1","HourFrom":"08:00","HourTo":"08:45","IsCanceled":false,"Subject":{"Id":"9","Name":"Matematyka","Url":"s"},"Teacher":{"Id":"3","FirstName":"Jan","LastName":"Nowak","Url":"t"}}]],
              "2026-10-06":[]
            }}
            """;
        var tt = JsonSerializer.Deserialize(json, LibrusApiJsonContext.Default.TimetablesResponse)!;
        var lesson = tt.Timetable["2026-10-05"][1][0];
        Assert.Equal("Matematyka", lesson.Subject!.AdditionalProperties!["Name"].GetString());
        Assert.Equal("9", lesson.Subject.Id?.Value);
    }

    [Fact]
    public void Grade_numbers_may_arrive_as_strings()
    {
        var json = """{"Resources":{},"Url":"u","Grades":[{"Id":"77","Grade":"4+","Semester":"1","IsFinal":false,"Subject":{"Id":1,"Url":"x"}}]}""";
        var grades = JsonSerializer.Deserialize(json, LibrusApiJsonContext.Default.GradesResponse)!;
        Assert.Equal(77, grades.Grades[0].Id);
        Assert.Equal("4+", grades.Grades[0].GradeValue);
    }

    [Fact]
    public void Unknown_fields_survive_a_round_trip()
    {
        var json = """{"Resources":{},"Url":"u","LuckyNumber":{"LuckyNumber":7,"LuckyNumberDay":"2026-10-10","Extra":"kept"}}""";
        var lucky = JsonSerializer.Deserialize(json, LibrusApiJsonContext.Default.LuckyNumberResponse)!;
        var output = JsonSerializer.Serialize(lucky, LibrusApiJsonContext.Default.LuckyNumberResponse);
        Assert.Contains("\"Extra\": \"kept\"", output);
    }

    [Theory]
    [InlineData(2026, 10, 10, 2026, 10, 5)] // Saturday
    [InlineData(2026, 10, 5, 2026, 10, 5)] // Monday
    [InlineData(2026, 10, 11, 2026, 10, 5)] // Sunday
    public void Week_start_is_monday(int y, int m, int d, int ey, int em, int ed) =>
        Assert.Equal(new DateOnly(ey, em, ed), ChildViews.WeekStart(new DateOnly(y, m, d)));
}
