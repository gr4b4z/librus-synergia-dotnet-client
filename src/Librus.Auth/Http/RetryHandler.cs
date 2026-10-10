using System.Net;
using Microsoft.Extensions.Logging;

namespace Librus.Http;

/// <summary>
/// Retries idempotent (GET/HEAD) requests on transient statuses and network errors with
/// exponential backoff + full jitter, honoring <c>Retry-After</c> on 429. Non-idempotent
/// requests (the portal login POST, AutoLoginToken, a future reply POST) are never retried.
/// </summary>
internal sealed class RetryHandler : DelegatingHandler
{
    private readonly RetryOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _logger;

    public RetryHandler(RetryOptions options, Func<TimeSpan, CancellationToken, Task> delay, ILogger logger)
    {
        _options = options;
        _delay = delay;
        _logger = logger;
    }

    internal Func<double> Random { get; init; } = System.Random.Shared.NextDouble;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var idempotent = request.Method == HttpMethod.Get || request.Method == HttpMethod.Head;
        var maxAttempts = idempotent ? Math.Max(1, _options.MaxAttempts) : 1;

        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                var delay = ComputeBackoff(attempt);
                Log(request, attempt, delay, "network_error", ex.HttpRequestError.ToString());
                await _delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (attempt >= maxAttempts || !_options.RetriableStatuses.Contains((int)response.StatusCode))
            {
                return response;
            }

            var wait = response.StatusCode == HttpStatusCode.TooManyRequests
                ? ParseRetryAfter(response, DateTimeOffset.UtcNow) ?? ComputeBackoff(attempt)
                : ComputeBackoff(attempt);
            if (wait > _options.MaxRetryAfter)
            {
                wait = _options.MaxRetryAfter;
            }

            Log(request, attempt, wait, $"status_{(int)response.StatusCode}", null);
            response.Dispose();
            await _delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    internal TimeSpan ComputeBackoff(int attempt)
    {
        var exponential = _options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var capped = Math.Min(_options.MaxDelay.TotalMilliseconds, exponential);
        return TimeSpan.FromMilliseconds(Random() * capped);
    }

    internal static TimeSpan? ParseRetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var diff = date - now;
            return diff < TimeSpan.Zero ? TimeSpan.Zero : diff;
        }

        return null;
    }

    private void Log(HttpRequestMessage request, int attempt, TimeSpan delay, string reason, string? detail)
    {
        _logger.LogWarning(
            "http.retry {Method} {Url} attempt={Attempt} delayMs={DelayMs} reason={Reason} detail={Detail}",
            request.Method.Method, UrlRedactor.Redact(request.RequestUri), attempt, (long)delay.TotalMilliseconds, reason, detail);
    }
}
