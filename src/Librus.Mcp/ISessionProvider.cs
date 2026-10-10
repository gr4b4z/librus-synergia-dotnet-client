using Librus.Http;

namespace Librus.Mcp;

/// <summary>Supplies the (lazily logged-in) session to tool handlers.</summary>
public interface ISessionProvider
{
    Task<LibrusSession> GetAsync(CancellationToken ct);
}

/// <summary>
/// Logs in on first use and reuses the session for the server's lifetime (spec §4: never re-login per
/// call). A failed login is not cached, so the next tool call retries.
/// </summary>
public sealed class LazySessionProvider(Func<LibrusCredentials> credentials, LibrusHttpOptions options) : ISessionProvider, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LibrusSession? _session;

    public async Task<LibrusSession> GetAsync(CancellationToken ct)
    {
        if (_session is not null)
        {
            return _session;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _session ??= await LibrusSession.LoginAsync(credentials(), options, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _gate.Dispose();
    }
}
