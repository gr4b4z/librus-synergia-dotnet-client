using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Librus.Http;

/// <summary>Base URLs of every Librus origin the client talks to (overridable for tests).</summary>
public sealed record LibrusEndpoints
{
    public Uri Portal { get; init; } = new("https://portal.librus.pl/");
    public Uri PortalApi { get; init; } = new("https://portal.librus.pl/api/v3/");
    public Uri Api { get; init; } = new("https://api.librus.pl/3.0/");
    public Uri Api20 { get; init; } = new("https://api.librus.pl/2.0/");
    public Uri Synergia { get; init; } = new("https://synergia.librus.pl/");
    public Uri Wiadomosci { get; init; } = new("https://wiadomosci.librus.pl/");
}

/// <summary>Retry policy for idempotent requests (spec §9).</summary>
public sealed record RetryOptions
{
    /// <summary>Total attempts including the first one.</summary>
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Upper bound applied to a server supplied <c>Retry-After</c>.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(60);
    public IReadOnlySet<int> RetriableStatuses { get; init; } = new HashSet<int> { 408, 425, 429, 500, 502, 503, 504 };
    public static RetryOptions None => new() { MaxAttempts = 1 };
}

/// <summary>Shared HTTP configuration used by every Librus client.</summary>
public sealed class LibrusHttpOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public RetryOptions Retry { get; init; } = new();
    public LibrusEndpoints Endpoints { get; init; } = new();
    public ILoggerFactory LoggerFactory { get; init; } = NullLoggerFactory.Instance;

    /// <summary>
    /// Creates the innermost handler. The argument is the cookie jar for cookie-backed sessions
    /// (portal, wiadomosci) or <c>null</c> for bearer-only clients. Tests replace this with a fake.
    /// </summary>
    public Func<CookieContainer?, HttpMessageHandler>? PrimaryHandlerFactory { get; init; }

    /// <summary>Injectable delay used by the retry handler (tests use a no-op).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    internal HttpClient CreateClient(CookieContainer? cookies, string category)
    {
        var primary = PrimaryHandlerFactory?.Invoke(cookies) ?? CreateDefaultHandler(cookies);
        var logging = new LoggingHandler(LoggerFactory.CreateLogger(category)) { InnerHandler = primary };
        var retry = new RetryHandler(Retry, Delay, LoggerFactory.CreateLogger(category)) { InnerHandler = logging };
        return new HttpClient(retry, disposeHandler: true) { Timeout = Timeout };
    }

    private static SocketsHttpHandler CreateDefaultHandler(CookieContainer? cookies) => new()
    {
        UseCookies = cookies is not null,
        CookieContainer = cookies ?? new CookieContainer(),
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 10,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };
}

internal static class LibrusUserAgents
{
    /// <summary>The official iOS app UA used by librus-sdk for api.librus.pl.</summary>
    public const string Mobile = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_7 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 LibrusMobileApp";
    public const string MobileOrigin = "app://librus";
    /// <summary>Desktop UA librus-sdk uses for synergia.librus.pl / wiadomosci.librus.pl.</summary>
    public const string Desktop = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Gecko/20100101 Firefox/62.0";
}
