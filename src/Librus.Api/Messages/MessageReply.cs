namespace Librus.Api.Messages;

/// <summary>A reply prepared from a received message; echoed back to the human before sending.</summary>
public sealed record ReplyDraft(
    string ChildLogin,
    string InReplyToMessageId,
    string? ToId,
    string? ToName,
    string Subject,
    string Body);

/// <summary>Transport for the single write path (spec §8). Reply-only by construction.</summary>
public interface IMessageReplySender
{
    Task SendReplyAsync(LibrusChildClient child, ReplyDraft draft, CancellationToken ct = default);
}

/// <summary>Builds reply drafts from received messages. Never sends anything.</summary>
public static class ReplyComposer
{
    public const int MaxBodyLength = 10_000;

    public static async Task<ReplyDraft> PrepareAsync(LibrusChildClient child, string messageId, string body, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new LibrusException(LibrusErrorCodes.InvalidArgument, "Reply body must not be empty.");
        }

        if (body.Length > MaxBodyLength)
        {
            throw new LibrusException(LibrusErrorCodes.InvalidArgument, $"Reply body is longer than {MaxBodyLength} characters.");
        }

        var original = await child.GetInboxMessageAsync(messageId, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(original.SenderId))
        {
            throw new LibrusException(LibrusErrorCodes.InvalidArgument, $"Message {messageId} has no sender to reply to.");
        }

        var subject = original.Subject ?? "";
        if (!subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase))
        {
            subject = "Re: " + subject;
        }

        return new ReplyDraft(child.Child.Login, original.Id, original.SenderId, original.SenderName, subject.Trim(), body.Trim());
    }
}

/// <summary>
/// Placeholder sender. The <c>wiadomosci.librus.pl/api</c> POST contract for replying to a thread
/// could not be confirmed against a live account (no reference library implements it), so per spec §8
/// the project ships read-only and this sender always refuses.
///
/// TODO(verify): capture the request the Wiadomości web UI issues when replying (DevTools → Network
/// on wiadomosci.librus.pl) and document: endpoint path, JSON vs multipart body, field names for
/// recipients / topic / content / parent-message id, and any anti-forgery header. Implement it here
/// as a single non-retried POST, reusing <see cref="Wiadomosci.WiadomosciClient"/>'s cookie session.
/// </summary>
public sealed class NotImplementedReplySender : IMessageReplySender
{
    public Task SendReplyAsync(LibrusChildClient child, ReplyDraft draft, CancellationToken ct = default) =>
        throw new LibrusException(LibrusErrorCodes.SendNotImplemented,
            "Sending replies is not implemented: the wiadomosci reply request shape has not been verified. Reply in Synergia instead: "
            + MessageNormalizer.WebUrlFor(draft.InReplyToMessageId));
}
