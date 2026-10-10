using System.Net;
using System.Text.Json;
using Librus.Api;
using Librus.Portal;

namespace Librus.Tests;

public class AuthTests
{
    private static readonly LibrusCredentials Creds = new("parent@example.com", "s3cret!");

    [Fact]
    public void Csrf_is_extracted_from_login_form()
    {
        var html = """<form id="login"><input type="email" name="email"><input type="hidden" name="_token" value="abc123" autocomplete="off"></form>""";
        Assert.Equal("abc123", CsrfTokenExtractor.Extract(html));
    }

    [Fact]
    public void Csrf_falls_back_to_meta_tag()
    {
        var html = """<html><head><meta name="csrf-token" content="meta-token"></head><body></body></html>""";
        Assert.Equal("meta-token", CsrfTokenExtractor.Extract(html));
    }

    [Fact]
    public void Missing_csrf_has_stable_error_code()
    {
        var ex = Assert.Throws<LibrusException>(() => CsrfTokenExtractor.Extract("<html><body>maintenance</body></html>"));
        Assert.Equal(LibrusErrorCodes.PortalLoginPageInvalid, ex.Code);
    }

    [Fact]
    public void SynergiaAccounts_parses_scopes_array_or_empty_string()
    {
        var json = """{"lastModification":1,"accounts":[{"id":1,"login":"a","accessToken":"t","scopes":["x","y"]},{"id":2,"login":"b","accessToken":"t2","scopes":""}]}""";
        var dto = JsonSerializer.Deserialize(json, PortalJsonContext.Default.SynergiaAccountsDto)!;
        Assert.Equal(["x", "y"], dto.Accounts[0].ToChild().Scopes);
        Assert.Empty(dto.Accounts[1].ToChild().Scopes);
    }

    [Fact]
    public async Task Login_posts_credentials_with_csrf_and_lists_children()
    {
        var fake = new FakeLibrus().WithPortal();
        using var session = await LibrusSession.LoginAsync(Creds, fake.Options());

        var post = Assert.Single(fake.Requests, r => r.Method == "POST");
        Assert.Equal("portal.librus.pl/konto-librus/login/action", post.Path);
        Assert.Contains("email=parent%40example.com", post.Body);
        Assert.Contains("password=s3cret%21", post.Body);
        Assert.Contains("_token=csrf-123", post.Body);

        var children = await session.ListChildrenAsync();
        Assert.Equal([11, 22], children.Select(c => c.Id));
        Assert.Equal("Ania Kowalska", children[0].StudentName);
    }

    [Fact]
    public async Task Child_model_never_serializes_the_bearer()
    {
        var fake = new FakeLibrus().WithPortal(tokenA: "SECRET-BEARER");
        using var session = await LibrusSession.LoginAsync(Creds, fake.Options());
        var children = await session.ListChildrenAsync();
        var json = JsonSerializer.Serialize(children, Librus.Api.Views.LibrusViewsJsonContext.Default.IReadOnlyListChild);
        Assert.DoesNotContain("SECRET-BEARER", json);
        Assert.DoesNotContain("SECRET-BEARER", children[0].ToString());
    }

    [Fact]
    public async Task Bad_password_surfaces_authentication_failed()
    {
        var fake = new FakeLibrus().WithPortal();
        fake.OnJson("GET", "portal.librus.pl/api/v3/Me", "{}", HttpStatusCode.Unauthorized);
        var ex = await Assert.ThrowsAsync<LibrusException>(() => LibrusSession.LoginAsync(Creds, fake.Options()));
        Assert.Equal(LibrusErrorCodes.AuthenticationFailed, ex.Code);
        Assert.DoesNotContain("s3cret", ex.Message);
    }

    [Fact]
    public async Task Child_requests_use_that_childs_bearer()
    {
        var fake = new FakeLibrus().WithPortal().OnJson("GET", "api.librus.pl/3.0/Grades", """{"Resources":{},"Url":"u","Grades":[]}""");
        using var session = await LibrusSession.LoginAsync(Creds, fake.Options());
        var children = await session.ListChildrenAsync();
        await session.ForChild(children[1]).GetGradesAsync();
        Assert.Equal("Bearer bearer-b", fake.Requests.Last().Authorization);
    }

    [Fact]
    public async Task Expired_bearer_is_refreshed_once_without_relogin()
    {
        var fake = new FakeLibrus().WithPortal(tokenA: "old");
        fake.On("GET", "api.librus.pl/3.0/Grades", (req, _) => req.Headers.Authorization?.Parameter == "new"
            ? FakeLibrus.Json("""{"Resources":{},"Url":"u","Grades":[{"Id":5,"Grade":"5","Date":"2026-10-01","Semester":1}]}""")
            : FakeLibrus.Status(HttpStatusCode.Unauthorized));
        fake.OnJson("GET", "portal.librus.pl/api/v3/SynergiaAccounts/fresh/1111111u",
            """{"id":11,"login":"1111111u","accessToken":"new","studentName":"Ania Kowalska","accountIdentifier":"A1","group":"student","state":"active"}""");
        using var session = await LibrusSession.LoginAsync(Creds, fake.Options());
        var client = await session.ForChildAsync("11");

        var grades = await client.GetGradesAsync();
        Assert.Equal("5", Assert.Single(grades.Grades).GradeValue);

        var second = await client.GetGradesAsync();
        Assert.Single(second.Grades);
        Assert.Equal(1, fake.Requests.Count(r => r.Method == "POST")); // logged in exactly once
        Assert.Equal(1, fake.Requests.Count(r => r.Path.EndsWith("/fresh/1111111u", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Persistent_401_after_refresh_is_auth_refresh_failed()
    {
        var fake = new FakeLibrus().WithPortal();
        fake.OnJson("GET", "api.librus.pl/3.0/Grades", "{}", HttpStatusCode.Unauthorized);
        fake.OnJson("GET", "portal.librus.pl/api/v3/SynergiaAccounts/fresh/1111111u", """{"id":11,"login":"1111111u","accessToken":"new2"}""");
        using var session = await LibrusSession.LoginAsync(Creds, fake.Options());
        var client = await session.ForChildAsync("1111111u");
        var ex = await Assert.ThrowsAsync<LibrusException>(() => client.GetGradesAsync());
        Assert.Equal(LibrusErrorCodes.AuthRefreshFailed, ex.Code);
    }

    [Fact]
    public async Task Dead_portal_session_triggers_a_single_relogin_during_refresh()
    {
        var fake = new FakeLibrus().WithPortal(tokenA: "old");
        var freshCalls = 0;
        fake.On("GET", "portal.librus.pl/api/v3/SynergiaAccounts/fresh/1111111u", (_, _) => ++freshCalls == 1
            ? FakeLibrus.Status(HttpStatusCode.Unauthorized)
            : FakeLibrus.Json("""{"id":11,"login":"1111111u","accessToken":"new"}"""));
        fake.On("GET", "api.librus.pl/3.0/LuckyNumbers", (req, _) => req.Headers.Authorization?.Parameter == "new"
            ? FakeLibrus.Json("""{"Resources":{},"Url":"u","LuckyNumber":{"LuckyNumber":13,"LuckyNumberDay":"2026-10-10"}}""")
            : FakeLibrus.Status(HttpStatusCode.Unauthorized));
        using var session = await LibrusSession.LoginAsync(Creds, fake.Options());
        var lucky = await (await session.ForChildAsync("Ania")).GetLuckyNumberAsync();
        Assert.Equal("13", lucky.LuckyNumber.LuckyNumberValue?.Value);
        Assert.Equal(2, fake.Requests.Count(r => r.Method == "POST"));
    }

    [Theory]
    [InlineData("11", 11)]
    [InlineData("2222222u", 22)]
    [InlineData("ania", 11)]
    [InlineData("Bartek Kowalski", 22)]
    public void Children_resolve_by_id_login_or_name(string selector, int expected)
    {
        var children = new[]
        {
            new Child(11, "1111111u", "Ania Kowalska", "", "", "", []),
            new Child(22, "2222222u", "Bartek Kowalski", "", "", "", []),
        };
        Assert.Equal(expected, LibrusSession.ResolveChild(children, selector).Id);
    }

    [Fact]
    public void Unknown_and_ambiguous_children_have_stable_codes()
    {
        var children = new[]
        {
            new Child(11, "x", "Ola Nowak", "", "", "", []),
            new Child(22, "y", "Ola Kowalska", "", "", "", []),
        };
        Assert.Equal(LibrusErrorCodes.ChildNotFound, Assert.Throws<LibrusException>(() => LibrusSession.ResolveChild(children, "Zosia")).Code);
        Assert.Equal(LibrusErrorCodes.AmbiguousChild, Assert.Throws<LibrusException>(() => LibrusSession.ResolveChild(children, "Ola")).Code);
    }

    [Fact]
    public void Credentials_prefer_environment_and_hide_password()
    {
        var env = new Dictionary<string, string> { ["LIBRUS_PORTAL_EMAIL"] = "e@x.pl", ["LIBRUS_PORTAL_PASSWORD"] = "pw" };
        var creds = LibrusCredentials.Resolve(k => env.GetValueOrDefault(k));
        Assert.Equal("e@x.pl", creds.Email);
        Assert.DoesNotContain("pw", creds.ToString().Replace("Password", "", StringComparison.Ordinal));
    }

    [Fact]
    public void Credentials_fall_back_to_secrets_file()
    {
        var dir = Directory.CreateTempSubdirectory();
        var path = Path.Combine(dir.FullName, "secrets.json");
        File.WriteAllText(path, """{"email":"f@x.pl","password":"filepw","telegramBotToken":"tg"}""");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var env = new Dictionary<string, string> { ["LIBRUS_SECRETS_FILE"] = path };
        Assert.Equal("f@x.pl", LibrusCredentials.Resolve(k => env.GetValueOrDefault(k)).Email);
        Assert.Equal("tg", LibrusSecrets.Get("LIBRUS_TELEGRAM_BOT_TOKEN", "telegramBotToken", k => env.GetValueOrDefault(k)));
    }

    [Fact]
    public void Missing_credentials_is_a_configuration_error()
    {
        var env = new Dictionary<string, string> { ["LIBRUS_SECRETS_FILE"] = "/nonexistent/secrets.json" };
        var ex = Assert.Throws<LibrusException>(() => LibrusCredentials.Resolve(k => env.GetValueOrDefault(k)));
        Assert.Equal(LibrusErrorCodes.ConfigurationError, ex.Code);
    }
}
