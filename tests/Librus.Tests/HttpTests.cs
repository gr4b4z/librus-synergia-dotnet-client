using System.Net;
using Librus.Api;
using Librus.Http;

namespace Librus.Tests;

public class HttpTests
{
    private static readonly LibrusCredentials Creds = new("parent@example.com", "pw");

    [Fact]
    public async Task Transient_get_failures_are_retried_with_capped_backoff()
    {
        var calls = 0;
        var delays = new List<TimeSpan>();
        var fake = new FakeLibrus().WithPortal();
        fake.On("GET", "api.librus.pl/3.0/Grades", (_, _) => ++calls < 3
            ? FakeLibrus.Status(HttpStatusCode.BadGateway)
            : FakeLibrus.Json("""{"Resources":{},"Url":"u","Grades":[]}"""));
        using var session = await LibrusSession.LoginAsync(Creds, fake.Options(delays));
        await (await session.ForChildAsync("11")).GetGradesAsync();
        Assert.Equal(3, calls);
        Assert.Equal(2, delays.Count);
        Assert.All(delays, d => Assert.InRange(d, TimeSpan.Zero, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Retries_stop_after_max_attempts_and_503_is_maintenance()
    {
        var calls = 0;
        var fake = new FakeLibrus().WithPortal();
        fake.On("GET", "api.librus.pl/3.0/Grades", (_, _) =>
        {
            calls++;
            return FakeLibrus.Json("""{"Status":"Maintenance"}""", HttpStatusCode.ServiceUnavailable);
        });
        using var session = await LibrusSession.LoginAsync(Creds, fake.Options());
        var ex = await Assert.ThrowsAsync<LibrusException>(async () => await (await session.ForChildAsync("11")).GetGradesAsync());
        Assert.Equal(3, calls);
        Assert.Equal(LibrusErrorCodes.ServiceMaintenance, ex.Code);
    }

    [Fact]
    public async Task Retry_after_is_honored_on_429()
    {
        var calls = 0;
        var delays = new List<TimeSpan>();
        var fake = new FakeLibrus().WithPortal();
        fake.On("GET", "api.librus.pl/3.0/Grades", (_, _) =>
        {
            if (++calls == 1)
            {
                var r = FakeLibrus.Status(HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return r;
            }

            return FakeLibrus.Json("""{"Resources":{},"Url":"u","Grades":[]}""");
        });
        using var session = await LibrusSession.LoginAsync(Creds, fake.Options(delays));
        await (await session.ForChildAsync("11")).GetGradesAsync();
        Assert.Equal([TimeSpan.FromSeconds(7)], delays);
    }

    [Fact]
    public async Task Non_idempotent_requests_are_never_retried()
    {
        var posts = 0;
        var fake = new FakeLibrus().WithPortal();
        fake.On("POST", "portal.librus.pl/konto-librus/login/action", (_, _) =>
        {
            posts++;
            return FakeLibrus.Status(HttpStatusCode.ServiceUnavailable);
        });
        var ex = await Assert.ThrowsAsync<LibrusException>(() => LibrusSession.LoginAsync(Creds, fake.Options()));
        Assert.Equal(1, posts);
        Assert.Equal(LibrusErrorCodes.AuthenticationFailed, ex.Code);
    }

    [Fact]
    public async Task Forbidden_and_not_found_have_stable_codes()
    {
        var fake = new FakeLibrus().WithPortal().OnJson("GET", "api.librus.pl/3.0/Notes", "{}", HttpStatusCode.Forbidden);
        using var session = await LibrusSession.LoginAsync(Creds, fake.Options());
        var client = await session.ForChildAsync("11");
        Assert.Equal(LibrusErrorCodes.AccessDenied, (await Assert.ThrowsAsync<LibrusException>(() => client.ListNotesAsync())).Code);
        Assert.Equal(LibrusErrorCodes.NotFound, (await Assert.ThrowsAsync<LibrusException>(() => client.ListSubjectsAsync())).Code);
    }

    [Fact]
    public async Task Unexpected_json_is_a_validation_error_without_echoing_values()
    {
        var fake = new FakeLibrus().WithPortal().OnJson("GET", "api.librus.pl/3.0/Grades", """{"Resources":{},"Url":"u","Grades":[{"Id":{"secret":"x"}}]}""");
        using var session = await LibrusSession.LoginAsync(Creds, fake.Options());
        var ex = await Assert.ThrowsAsync<LibrusException>(async () => await (await session.ForChildAsync("11")).GetGradesAsync());
        Assert.Equal(LibrusErrorCodes.ResponseValidationFailed, ex.Code);
        Assert.DoesNotContain("secret", ex.Message);
    }

    [Theory]
    [InlineData("https://synergia.librus.pl/loguj/token/abc.def/przenies/uczen", "https://synergia.librus.pl/loguj/token/REDACTED/przenies/uczen")]
    [InlineData("https://api.telegram.org/bot123:ABC/sendMessage", "https://api.telegram.org/botREDACTED/sendMessage")]
    [InlineData("https://api.librus.pl/3.0/Grades", "https://api.librus.pl/3.0/Grades")]
    public void Urls_are_redacted(string input, string expected) => Assert.Equal(expected, UrlRedactor.Redact(input));

    [Fact]
    public void Generated_query_helpers_format_invariantly()
    {
        Assert.Equal("2026-10-05", LibrusChildClient.Format(new DateOnly(2026, 10, 5)));
        Assert.Equal("true", LibrusChildClient.Format((bool?)true));
        Assert.Null(LibrusChildClient.Query(("a", null)));
    }
}
