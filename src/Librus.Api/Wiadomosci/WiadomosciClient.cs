using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Librus.Api.Wiadomosci.Models;
using Librus.Http;
using Librus.Portal;

namespace Librus.Api.Wiadomosci;

/// <summary>
/// Cookie-backed reader for <c>wiadomosci.librus.pl/api</c> (librus-sdk <c>forChildWiadomosci</c>).
/// Authentication is a handoff: fresh child bearer → <c>POST api.librus.pl/2.0/AutoLoginToken</c> →
/// <c>synergia.librus.pl/loguj/token/{token}/…</c> → <c>/wiadomosci3</c>, after which the cookie jar is
/// valid for the inbox API. One instance (and cookie jar) per child.
/// </summary>
public sealed class WiadomosciClient : IDisposable
{
    private readonly LibrusSession _session;
    private readonly Child _child;
    private readonly LibrusEndpoints _endpoints;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private bool _authenticated;

    internal WiadomosciClient(LibrusSession session, Child child)
    {
        _session = session;
        _child = child;
        _endpoints = session.Options.Endpoints;
        Cookies = new CookieContainer();
        _http = session.Options.CreateClient(Cookies, "Librus.Wiadomosci");
    }

    internal CookieContainer Cookies { get; }

    public Task<WiadomosciMessagesResponse> ListInboxAsync(int page = 1, int limit = 300, string? afterId = null, CancellationToken ct = default)
    {
        var query = $"page={page}&limit={limit}" + (afterId is null ? "" : "&afterId=" + Uri.EscapeDataString(afterId));
        return GetJsonAsync($"api/inbox/messages?{query}", WiadomosciJsonContext.Default.WiadomosciMessagesResponse, ct);
    }

    public Task<WiadomosciMessageResponse> GetInboxMessageAsync(string id, CancellationToken ct = default) =>
        GetJsonAsync($"api/inbox/messages/{Uri.EscapeDataString(id)}", WiadomosciJsonContext.Default.WiadomosciMessageResponse, ct);

    public Task<WiadomosciUnreadMessagesCountResponse> GetUnreadCountAsync(CancellationToken ct = default) =>
        GetJsonAsync("api/inbox/unreadMessagesCount", WiadomosciJsonContext.Default.WiadomosciUnreadMessagesCountResponse, ct);

    private async Task<T> GetJsonAsync<T>(string relative, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        var uri = new Uri(_endpoints.Wiadomosci, relative);
        for (var attempt = 0; ; attempt++)
        {
            await EnsureAuthenticatedAsync(ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", LibrusUserAgents.Desktop);
            using var response = await HttpErrors.SendAsync(_http, request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                _authenticated = false;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw await LibrusChildClient.MapFailureAsync(response, uri, ct).ConfigureAwait(false);
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return await JsonSerializer.DeserializeAsync(stream, typeInfo, ct).ConfigureAwait(false)
                    ?? throw new LibrusException(LibrusErrorCodes.ResponseValidationFailed, "Wiadomosci returned an empty response.", uri.ToString());
            }
            catch (JsonException ex)
            {
                throw new LibrusException(LibrusErrorCodes.ResponseValidationFailed,
                    $"Received an unexpected response from wiadomosci at {ex.Path ?? "$"}.", uri.ToString());
            }
        }
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken ct)
    {
        if (_authenticated)
        {
            return;
        }

        await _authLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_authenticated)
            {
                return;
            }

            var fresh = await _session.WithPortalReloginAsync(c => _session.Portal.GetFreshSynergiaAccountAsync(_child.Login, c), ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(fresh.AccessToken))
            {
                throw new LibrusException(LibrusErrorCodes.AuthenticationFailed, "Portal did not return a bearer token for the wiadomosci handoff.");
            }

            _session.StoreBearer(_child, fresh.AccessToken);
            var autoLogin = await GetAutoLoginTokenAsync(fresh.AccessToken, ct).ConfigureAwait(false);

            // Path carries a one-time secret: UrlRedactor masks it in logs and errors.
            await GetHtmlAsync(new Uri(_endpoints.Synergia, $"loguj/token/{Uri.EscapeDataString(autoLogin)}/przenies/uczen/widok/centrum_powiadomien"),
                LibrusUserAgents.Mobile, ct).ConfigureAwait(false);
            await GetHtmlAsync(new Uri(_endpoints.Synergia, "wiadomosci3"), LibrusUserAgents.Desktop, ct).ConfigureAwait(false);
            _authenticated = true;
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task<string> GetAutoLoginTokenAsync(string bearer, CancellationToken ct)
    {
        var uri = new Uri(_endpoints.Api20, "AutoLoginToken");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", LibrusUserAgents.Mobile);
        using var response = await HttpErrors.SendAsync(_http, request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw LibrusException.FromStatus(uri.ToString(), (int)response.StatusCode, "Wiadomosci authentication failed");
        }

        try
        {
            var payload = await response.Content.ReadFromJsonAsync(WiadomosciAuthJsonContext.Default.AutoLoginTokenResponse, ct).ConfigureAwait(false);
            return string.IsNullOrEmpty(payload?.Token)
                ? throw new LibrusException(LibrusErrorCodes.AuthenticationFailed, "Wiadomosci authentication response was invalid.", uri.ToString())
                : payload.Token;
        }
        catch (JsonException)
        {
            throw new LibrusException(LibrusErrorCodes.AuthenticationFailed, "Wiadomosci authentication response was invalid.", uri.ToString());
        }
    }

    private async Task GetHtmlAsync(Uri uri, string userAgent, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        using var response = await HttpErrors.SendAsync(_http, request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw LibrusException.FromStatus(UrlRedactor.Redact(uri), (int)response.StatusCode, "Wiadomosci Synergia handoff failed");
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _authLock.Dispose();
    }
}

internal sealed class AutoLoginTokenResponse
{
    [JsonPropertyName("Token")] public string? Token { get; set; }
}

[JsonSerializable(typeof(AutoLoginTokenResponse))]
internal sealed partial class WiadomosciAuthJsonContext : JsonSerializerContext;
