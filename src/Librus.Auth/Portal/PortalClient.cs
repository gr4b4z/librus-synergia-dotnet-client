using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Librus.Http;
using Microsoft.Extensions.Logging;

namespace Librus.Portal;

/// <summary>
/// Konto LIBRUS portal session (cookie-backed): login, <c>/Me</c>, <c>/SynergiaAccounts</c>.
/// Ported from librus-sdk <c>PortalClient</c>.
/// </summary>
public sealed class PortalClient : IDisposable
{
    internal const string LoginPath = "konto-librus/login";
    internal const string LoginActionPath = "konto-librus/login/action";

    private readonly LibrusHttpOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public PortalClient(LibrusHttpOptions options)
    {
        _options = options;
        Cookies = new CookieContainer();
        _http = options.CreateClient(Cookies, "Librus.Portal");
        _logger = options.LoggerFactory.CreateLogger("Librus.Portal");
    }

    internal CookieContainer Cookies { get; }

    public bool IsLoggedIn { get; private set; }

    public async Task LoginAsync(LibrusCredentials credentials, CancellationToken ct = default)
    {
        var loginPage = new Uri(_options.Endpoints.Portal, LoginPath);
        string html;
        using (var page = await SendAsync(new HttpRequestMessage(HttpMethod.Get, loginPage), ct).ConfigureAwait(false))
        {
            if (!page.IsSuccessStatusCode)
            {
                throw new LibrusException(LibrusErrorCodes.AuthenticationFailed, "Portal login page could not be loaded.", loginPage.ToString(), (int)page.StatusCode);
            }

            html = await page.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        var token = CsrfTokenExtractor.Extract(html);
        var action = new Uri(_options.Endpoints.Portal, LoginActionPath);
        using var post = new HttpRequestMessage(HttpMethod.Post, action)
        {
            Content = new FormUrlEncodedContent(
            [
                new("email", credentials.Email),
                new("password", credentials.Password),
                new(CsrfTokenExtractor.FieldName, token),
            ]),
        };
        post.Headers.Referrer = loginPage;

        using (var response = await SendAsync(post, ct).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new LibrusException(LibrusErrorCodes.AuthenticationFailed, "Portal authentication failed.", action.ToString(), (int)response.StatusCode);
            }
        }

        // The login action redirects to an HTML page on both success and failure; /Me is the real check.
        try
        {
            await GetMeAsync(ct).ConfigureAwait(false);
        }
        catch (LibrusException ex) when (ex.Status is 401 or 403)
        {
            throw new LibrusException(LibrusErrorCodes.AuthenticationFailed, "Portal authentication failed (check e-mail/password).", ex.Endpoint, ex.Status);
        }

        IsLoggedIn = true;
        _logger.LogInformation("portal.login.success");
    }

    public Task<PortalMe> GetMeAsync(CancellationToken ct = default) =>
        GetJsonAsync("Me", PortalJsonContext.Default.PortalMe, ct);

    internal async Task<IReadOnlyList<SynergiaAccountDto>> GetSynergiaAccountsAsync(CancellationToken ct = default)
    {
        var dto = await GetJsonAsync("SynergiaAccounts", PortalJsonContext.Default.SynergiaAccountsDto, ct).ConfigureAwait(false);
        return dto.Accounts;
    }

    /// <summary>Re-issues a bearer token for one child (librus-sdk <c>getFreshSynergiaAccount</c>).</summary>
    internal Task<SynergiaAccountDto> GetFreshSynergiaAccountAsync(string login, CancellationToken ct = default) =>
        GetJsonAsync($"SynergiaAccounts/fresh/{Uri.EscapeDataString(login)}", PortalJsonContext.Default.SynergiaAccountDto, ct);

    internal void MarkLoggedOut() => IsLoggedIn = false;

    private async Task<T> GetJsonAsync<T>(string path, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        var endpoint = new Uri(_options.Endpoints.PortalApi, path);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw LibrusException.FromStatus(endpoint.ToString(), (int)response.StatusCode, "Portal API request failed");
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync(stream, typeInfo, ct).ConfigureAwait(false)
                ?? throw new LibrusException(LibrusErrorCodes.ResponseValidationFailed, "Portal returned an empty response.", endpoint.ToString());
        }
        catch (JsonException)
        {
            throw new LibrusException(LibrusErrorCodes.ResponseValidationFailed, "Received an unexpected response from the Librus portal.", endpoint.ToString());
        }
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        HttpErrors.SendAsync(_http, request, ct);

    public void Dispose() => _http.Dispose();
}
