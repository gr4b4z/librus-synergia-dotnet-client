using System.Collections.Concurrent;
using Librus.Http;
using Librus.Portal;
using Microsoft.Extensions.Logging;

namespace Librus;

/// <summary>
/// One Konto LIBRUS login shared by all linked children. Owns the portal cookie session and the
/// per-child bearer tokens, and refreshes a child's bearer on 401 without re-logging in
/// (spec §4). Use <c>session.ForChild(child)</c> from <c>Librus.Api</c> for data calls.
/// </summary>
public sealed class LibrusSession : IDisposable
{
    private readonly LibrusCredentials _credentials;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<int, string> _tokens = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _refreshLocks = new();
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private readonly ConcurrentDictionary<string, object> _components = new();
    private IReadOnlyList<Child>? _children;

    private LibrusSession(LibrusCredentials credentials, LibrusHttpOptions options)
    {
        _credentials = credentials;
        Options = options;
        Portal = new PortalClient(options);
        _logger = options.LoggerFactory.CreateLogger("Librus.Session");
    }

    public LibrusHttpOptions Options { get; }

    public PortalClient Portal { get; }

    /// <summary>Logs into portal.librus.pl and returns a ready session.</summary>
    public static async Task<LibrusSession> LoginAsync(LibrusCredentials credentials, LibrusHttpOptions? options = null, CancellationToken ct = default)
    {
        var session = new LibrusSession(credentials, options ?? new LibrusHttpOptions());
        try
        {
            await session.Portal.LoginAsync(credentials, ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>Lists linked children (cached for the lifetime of the session; pass <paramref name="refresh"/> to reload).</summary>
    public async Task<IReadOnlyList<Child>> ListChildrenAsync(bool refresh = false, CancellationToken ct = default)
    {
        if (_children is not null && !refresh)
        {
            return _children;
        }

        var accounts = await WithPortalReloginAsync(c => Portal.GetSynergiaAccountsAsync(c), ct).ConfigureAwait(false);
        foreach (var account in accounts)
        {
            if (!string.IsNullOrEmpty(account.AccessToken))
            {
                _tokens[account.Id] = account.AccessToken;
            }
        }

        _children = accounts.Select(a => a.ToChild()).ToArray();
        return _children;
    }

    /// <summary>Resolves a child by numeric id, login, or (unambiguous) student name / first name.</summary>
    public async Task<Child> ResolveChildAsync(string selector, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            throw new LibrusException(LibrusErrorCodes.ChildRequired, "A child id or login is required.");
        }

        var children = await ListChildrenAsync(ct: ct).ConfigureAwait(false);
        return ResolveChild(children, selector);
    }

    internal static Child ResolveChild(IReadOnlyList<Child> children, string selector)
    {
        selector = selector.Trim();
        var byId = children.FirstOrDefault(c => c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == selector);
        if (byId is not null)
        {
            return byId;
        }

        var byLogin = children.Where(c => string.Equals(c.Login, selector, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (byLogin.Length == 0)
        {
            // Convenience for humans/LLMs: full student name or first name ("Ania").
            byLogin = children.Where(c => string.Equals(c.StudentName.Trim(), selector, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            if (byLogin.Length == 0)
            {
                byLogin = children.Where(c => c.StudentName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Any(part => string.Equals(part, selector, StringComparison.CurrentCultureIgnoreCase))).ToArray();
            }
        }

        return byLogin.Length switch
        {
            1 => byLogin[0],
            0 => throw new LibrusException(LibrusErrorCodes.ChildNotFound, $"No child account matched selector \"{selector}\"."),
            _ => throw new LibrusException(LibrusErrorCodes.AmbiguousChild, $"Multiple child accounts matched selector \"{selector}\"; use the numeric id."),
        };
    }

    /// <summary>Current bearer for a child (internal: never exposed publicly or logged).</summary>
    internal async Task<string> GetBearerAsync(Child child, CancellationToken ct)
    {
        if (_tokens.TryGetValue(child.Id, out var token))
        {
            return token;
        }

        await ListChildrenAsync(refresh: true, ct).ConfigureAwait(false);
        return _tokens.TryGetValue(child.Id, out token)
            ? token
            : throw new LibrusException(LibrusErrorCodes.ChildNotFound, $"Child {child.Id} is no longer linked to this account.");
    }

    /// <summary>Records a bearer obtained out of band (e.g. by the wiadomosci handoff).</summary>
    internal void StoreBearer(Child child, string token) => _tokens[child.Id] = token;

    /// <summary>Per-session cache for higher layers (child API clients, wiadomosci sessions).</summary>
    internal T GetOrAddComponent<T>(string key, Func<T> factory)
        where T : class => (T)_components.GetOrAdd(key, _ => factory());

    /// <summary>
    /// Refreshes a child's bearer after a 401. Concurrent callers collapse onto one portal round-trip;
    /// if another caller already replaced <paramref name="staleToken"/>, the newer token is returned as is.
    /// </summary>
    internal async Task<string> RefreshBearerAsync(Child child, string staleToken, CancellationToken ct)
    {
        var gate = _refreshLocks.GetOrAdd(child.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_tokens.TryGetValue(child.Id, out var current) && current != staleToken)
            {
                return current;
            }

            var fresh = await WithPortalReloginAsync(c => Portal.GetFreshSynergiaAccountAsync(child.Login, c), ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(fresh.AccessToken))
            {
                throw new LibrusException(LibrusErrorCodes.AuthRefreshFailed, "Portal did not return a fresh bearer token.");
            }

            _tokens[child.Id] = fresh.AccessToken;
            _logger.LogInformation("session.bearer.refreshed child={ChildId}", child.Id);
            return fresh.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Runs a portal call; if the portal cookie session expired (401), logs in once more and retries.</summary>
    internal async Task<T> WithPortalReloginAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        try
        {
            return await call(ct).ConfigureAwait(false);
        }
        catch (LibrusException ex) when (ex.IsUnauthorized)
        {
            await ReloginAsync(ct).ConfigureAwait(false);
            return await call(ct).ConfigureAwait(false);
        }
    }

    private async Task ReloginAsync(CancellationToken ct)
    {
        await _loginLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _logger.LogInformation("session.portal.relogin");
            Portal.MarkLoggedOut();
            await Portal.LoginAsync(_credentials, ct).ConfigureAwait(false);
        }
        finally
        {
            _loginLock.Release();
        }
    }

    public void Dispose()
    {
        foreach (var component in _components.Values.OfType<IDisposable>())
        {
            component.Dispose();
        }

        Portal.Dispose();
        _loginLock.Dispose();
        foreach (var gate in _refreshLocks.Values)
        {
            gate.Dispose();
        }
    }
}
