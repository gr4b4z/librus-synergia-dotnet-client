using Librus.Api.Messages;
using Librus.Api.Wiadomosci;

namespace Librus.Api;

public sealed partial class LibrusChildClient
{
    private volatile bool _api3MessagesForbidden;

    /// <summary>The wiadomosci.librus.pl reader for this child (lazy; authenticates on first use).</summary>
    public WiadomosciClient Wiadomosci =>
        Session.GetOrAddComponent($"wiadomosci:{Child.Id}", () => new WiadomosciClient(Session, Child));

    /// <summary>
    /// Lists inbox messages. Tries <c>api.librus.pl/3.0/Messages</c> first; when Librus answers 403
    /// (common for portal tokens) it falls back to <c>wiadomosci.librus.pl/api</c> and remembers that.
    /// </summary>
    public async Task<InboxPage> ListInboxAsync(bool unreadOnly = false, int page = 1, int limit = 50, bool includeBody = false, CancellationToken ct = default)
    {
        IReadOnlyList<InboxMessage> messages;
        MessageSource source;
        if (!_api3MessagesForbidden)
        {
            try
            {
                var response = await ListMessagesAsync(alternativeBody: true, changeNewLine: 1, getAllTypes: 1, page: page, limit: limit, cancellationToken: ct).ConfigureAwait(false);
                messages = response.Messages.Select(m => MessageNormalizer.FromApi3(m, includeBody)).ToList();
                source = MessageSource.Api3;
                return Filter(messages, source);
            }
            catch (LibrusException ex) when (ex.Status == 403)
            {
                _api3MessagesForbidden = true;
            }
        }

        var inbox = await Wiadomosci.ListInboxAsync(page, limit, ct: ct).ConfigureAwait(false);
        messages = inbox.Data.Select(m => MessageNormalizer.FromWiadomosci(m, includeBody)).ToList();
        source = MessageSource.Wiadomosci;
        return Filter(messages, source);

        InboxPage Filter(IReadOnlyList<InboxMessage> list, MessageSource s) =>
            new(unreadOnly ? list.Where(m => !m.IsRead).ToList() : list, s);
    }

    /// <summary>Gets one inbox message with its body (same fallback rules as <see cref="ListInboxAsync"/>).</summary>
    public async Task<InboxMessage> GetInboxMessageAsync(string id, CancellationToken ct = default)
    {
        if (!_api3MessagesForbidden)
        {
            try
            {
                var response = await GetMessageAsync(id, ct).ConfigureAwait(false);
                return MessageNormalizer.FromApi3(response.Message, includeBody: true);
            }
            catch (LibrusException ex) when (ex.Status == 403)
            {
                _api3MessagesForbidden = true;
            }
        }

        var detail = await Wiadomosci.GetInboxMessageAsync(id, ct).ConfigureAwait(false);
        return detail.Data is null
            ? throw new LibrusException(LibrusErrorCodes.NotFound, $"Message {id} was not found.")
            : MessageNormalizer.FromWiadomosci(detail.Data, includeBody: true);
    }

    /// <summary>Unread inbox count (same fallback rules).</summary>
    public async Task<int> GetUnreadCountAsync(CancellationToken ct = default)
    {
        if (!_api3MessagesForbidden)
        {
            try
            {
                return (int)(await GetUnreadMessagesAsync(ct).ConfigureAwait(false)).UnreadMessages;
            }
            catch (LibrusException ex) when (ex.Status == 403)
            {
                _api3MessagesForbidden = true;
            }
        }

        var count = await Wiadomosci.GetUnreadCountAsync(ct).ConfigureAwait(false);
        return int.TryParse(count.Data.Value, out var n) ? n : 0;
    }
}
