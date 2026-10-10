namespace Librus.Http;

internal static class HttpErrors
{
    /// <summary>Sends a request and maps transport failures to stable <see cref="LibrusException"/> codes.</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        try
        {
            return await http.SendAsync(request, completion, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LibrusException(LibrusErrorCodes.NetworkTimeout,
                $"Librus request timed out after {(long)http.Timeout.TotalMilliseconds}ms.", UrlRedactor.Redact(request.RequestUri), inner: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new LibrusException(LibrusErrorCodes.NetworkError,
                $"Network error talking to Librus ({ex.HttpRequestError}).", UrlRedactor.Redact(request.RequestUri), inner: ex);
        }
    }
}
