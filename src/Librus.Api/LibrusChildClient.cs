using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Librus.Http;
using Librus.Portal;

namespace Librus.Api;

/// <summary>
/// Bearer-scoped client for one child against <c>https://api.librus.pl/3.0</c>.
/// Every call is a GET; on 401 the child's bearer is refreshed once through the portal session
/// (never a full re-login per call). Obtain via <c>session.ForChild(child)</c>.
/// </summary>
public sealed partial class LibrusChildClient : IDisposable
{
    private readonly LibrusSession _session;
    private readonly HttpClient _http;
    private readonly Uri _baseUrl;

    internal LibrusChildClient(LibrusSession session, Child child)
    {
        _session = session;
        Child = child;
        _baseUrl = session.Options.Endpoints.Api;
        _http = session.Options.CreateClient(cookies: null, "Librus.Api");
    }

    public Child Child { get; }

    internal LibrusSession Session => _session;

    /// <summary>
    /// Raw change feed used by the official app timeline. Shape is not described by the OpenAPI document.
    /// TODO(verify): confirm that <c>/ChangeRegister</c> is reachable with the portal bearer and pin its schema.
    /// </summary>
    public Task<JsonElement> GetChangeRegisterAsync(string? provider = null, CancellationToken cancellationToken = default) =>
        GetJsonAsync("ChangeRegister", Query(("provider", provider)), LibrusApiJsonContext.Default.JsonElement, cancellationToken);

    internal async Task<T> GetJsonAsync<T>(string path, IReadOnlyList<KeyValuePair<string, string>>? query, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        using var response = await SendWithRefreshAsync(path, query, ct).ConfigureAwait(false);
        var endpoint = UrlRedactor.Redact(response.RequestMessage?.RequestUri);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync(stream, typeInfo, ct).ConfigureAwait(false)
                ?? throw new LibrusException(LibrusErrorCodes.ResponseValidationFailed, "Librus returned an empty response.", endpoint);
        }
        catch (JsonException ex)
        {
            // Path only (e.g. "$.Grades[3].Id"), never the offending value.
            throw new LibrusException(LibrusErrorCodes.ResponseValidationFailed,
                $"Received an unexpected response from Librus at {ex.Path ?? "$"}.", endpoint);
        }
    }

    internal async Task<LibrusBinary> GetBinaryAsync(string path, IReadOnlyList<KeyValuePair<string, string>>? query, CancellationToken ct)
    {
        using var response = await SendWithRefreshAsync(path, query, ct).ConfigureAwait(false);
        var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return new LibrusBinary(
            data,
            response.Content.Headers.ContentType?.MediaType,
            response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
    }

    private async Task<HttpResponseMessage> SendWithRefreshAsync(string path, IReadOnlyList<KeyValuePair<string, string>>? query, CancellationToken ct)
    {
        var uri = BuildUri(path, query);
        var token = await _session.GetBearerAsync(Child, ct).ConfigureAwait(false);
        var response = await SendOnceAsync(uri, token, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            var fresh = await _session.RefreshBearerAsync(Child, token, ct).ConfigureAwait(false);
            response = await SendOnceAsync(uri, fresh, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                throw new LibrusException(LibrusErrorCodes.AuthRefreshFailed,
                    "Synergia request failed with 401 after refreshing the bearer token.", uri.ToString(), 401);
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                throw await MapFailureAsync(response, uri, ct).ConfigureAwait(false);
            }
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(Uri uri, string token, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", LibrusUserAgents.Mobile);
        request.Headers.TryAddWithoutValidation("Origin", LibrusUserAgents.MobileOrigin);
        return await HttpErrors.SendAsync(_http, request, ct).ConfigureAwait(false);
    }

    internal static async Task<LibrusException> MapFailureAsync(HttpResponseMessage response, Uri uri, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var endpoint = UrlRedactor.Redact(uri);
        if (status == 503 || status == 500)
        {
            // Librus answers maintenance windows with {"Status":"Maintenance"}; inspect only that field.
            try
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("Status", out var s)
                    && s.ValueKind == JsonValueKind.String
                    && s.GetString() == "Maintenance")
                {
                    return new LibrusException(LibrusErrorCodes.ServiceMaintenance,
                        "Librus API is temporarily unavailable due to maintenance.", endpoint, status);
                }
            }
            catch (JsonException)
            {
            }
        }

        if (status == 503)
        {
            return new LibrusException(LibrusErrorCodes.ServiceMaintenance, "Librus API is temporarily unavailable (HTTP 503).", endpoint, status);
        }

        return LibrusException.FromStatus(endpoint, status, "Synergia API request failed");
    }

    private Uri BuildUri(string path, IReadOnlyList<KeyValuePair<string, string>>? query)
    {
        var builder = new UriBuilder(new Uri(_baseUrl, path.TrimStart('/')));
        if (query is { Count: > 0 })
        {
            builder.Query = string.Join('&', query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
        }

        return builder.Uri;
    }

    internal static IReadOnlyList<KeyValuePair<string, string>>? Query(params (string Key, string? Value)[] pairs)
    {
        var list = pairs.Where(p => p.Value is not null).Select(p => new KeyValuePair<string, string>(p.Key, p.Value!)).ToList();
        return list.Count == 0 ? null : list;
    }

    internal static string? Format(string? value) => value;

    internal static string? Format(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    internal static string? Format(bool? value) => value switch { null => null, true => "true", false => "false" };

    internal static string? Format(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public void Dispose() => _http.Dispose();
}

/// <summary>Entry points from a <see cref="LibrusSession"/> to child-scoped clients.</summary>
public static class LibrusSessionExtensions
{
    /// <summary>Returns the (cached) bearer-scoped API client for <paramref name="child"/>.</summary>
    public static LibrusChildClient ForChild(this LibrusSession session, Child child) =>
        session.GetOrAddComponent($"api:{child.Id}", () => new LibrusChildClient(session, child));

    /// <summary>Resolves <paramref name="selector"/> (id or login) and returns its client.</summary>
    public static async Task<LibrusChildClient> ForChildAsync(this LibrusSession session, string selector, CancellationToken ct = default) =>
        session.ForChild(await session.ResolveChildAsync(selector, ct).ConfigureAwait(false));
}
