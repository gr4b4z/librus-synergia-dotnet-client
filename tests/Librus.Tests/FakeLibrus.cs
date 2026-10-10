using System.Net;
using System.Text;
using Librus.Http;

namespace Librus.Tests;

/// <summary>In-memory stand-in for every Librus origin. Routes by "METHOD host/path".</summary>
internal sealed class FakeLibrus : HttpMessageHandler
{
    private readonly List<(string Method, string PathPrefix, Func<HttpRequestMessage, string?, HttpResponseMessage> Respond)> _routes = [];

    public List<RecordedRequest> Requests { get; } = [];

    public FakeLibrus On(string method, string hostAndPath, Func<HttpRequestMessage, string?, HttpResponseMessage> respond)
    {
        _routes.Insert(0, (method, hostAndPath, respond));
        return this;
    }

    public FakeLibrus OnJson(string method, string hostAndPath, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        On(method, hostAndPath, (_, _) => Json(json, status));

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Html(string html) =>
        new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent("") };

    /// <summary>Standard portal: login page with CSRF, login action, /Me and two children.</summary>
    public FakeLibrus WithPortal(string tokenA = "bearer-a", string tokenB = "bearer-b")
    {
        On("GET", "portal.librus.pl/konto-librus/login", (_, _) => Html(
            "<html><body><form id=\"login\" action=\"/konto-librus/login/action\"><input name=\"email\"><input type=\"hidden\" name=\"_token\" value=\"csrf-123\"></form></body></html>"));
        On("POST", "portal.librus.pl/konto-librus/login/action", (_, _) => Html("<html>ok</html>"));
        OnJson("GET", "portal.librus.pl/api/v3/Me", """{"identifier": 7, "email": "parent@example.com"}""");
        OnJson("GET", "portal.librus.pl/api/v3/SynergiaAccounts", $$"""
            {"lastModification": 1, "accounts": [
              {"id": 11, "accountIdentifier": "A1", "group": "student", "accessToken": "{{tokenA}}", "login": "1111111u", "studentName": "Ania Kowalska", "state": "active", "scopes": ["grades", "messages"]},
              {"id": 22, "accountIdentifier": "B2", "group": "student", "accessToken": "{{tokenB}}", "login": "2222222u", "studentName": "Bartek Kowalski", "state": "active", "scopes": ""}
            ]}
            """);
        return this;
    }

    public LibrusHttpOptions Options(List<TimeSpan>? delays = null) => new()
    {
        PrimaryHandlerFactory = _ => this,
        Delay = (d, _) =>
        {
            delays?.Add(d);
            return Task.CompletedTask;
        },
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));
        var key = request.RequestUri!.Host + request.RequestUri.AbsolutePath;
        foreach (var (method, prefix, respond) in _routes)
        {
            if (method == request.Method.Method && (key == prefix || key.StartsWith(prefix + "?", StringComparison.Ordinal)))
            {
                var response = respond(request, body);
                response.RequestMessage = request;
                return response;
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request, Content = new StringContent("") };
    }

    protected override void Dispose(bool disposing)
    {
        // Shared across clients in tests; nothing to release.
    }
}

internal sealed record RecordedRequest(string Method, Uri Uri, string? Authorization, string? Body)
{
    public string Path => Uri.Host + Uri.AbsolutePath;
}
