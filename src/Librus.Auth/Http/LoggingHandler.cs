using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Librus.Http;

/// <summary>
/// Structured, secret-safe request logging: method, redacted URL, status and duration only.
/// Never logs headers (Authorization/Cookie), request bodies or response bodies.
/// </summary>
internal sealed class LoggingHandler : DelegatingHandler
{
    private readonly ILogger _logger;

    public LoggingHandler(ILogger logger) => _logger = logger;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var url = UrlRedactor.Redact(request.RequestUri);
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var level = response.IsSuccessStatusCode ? LogLevel.Debug : LogLevel.Information;
            _logger.Log(level, "http {Method} {Url} -> {Status} in {DurationMs}ms",
                request.Method.Method, url, (int)response.StatusCode, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("http {Method} {Url} failed after {DurationMs}ms: {ErrorType}",
                request.Method.Method, url, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, ex.GetType().Name);
            throw;
        }
    }
}

/// <summary>Removes secrets that Librus puts in URL paths (e.g. the Synergia auto-login token).</summary>
public static partial class UrlRedactor
{
    public static string Redact(Uri? uri) => uri is null ? "" : Redact(uri.ToString());

    public static string Redact(string url) =>
        TelegramBot().Replace(TokenSegment().Replace(url, "/loguj/token/REDACTED/"), "/botREDACTED/");

    [GeneratedRegex("/loguj/token/[^/]+/", RegexOptions.IgnoreCase)]
    private static partial Regex TokenSegment();

    [GeneratedRegex("/bot[^/]+/", RegexOptions.IgnoreCase)]
    private static partial Regex TelegramBot();
}
