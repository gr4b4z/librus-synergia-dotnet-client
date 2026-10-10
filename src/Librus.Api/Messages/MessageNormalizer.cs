using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Librus.Api.Wiadomosci.Models;

namespace Librus.Api.Messages;

/// <summary>Maps raw api3 / wiadomosci payloads onto <see cref="InboxMessage"/> (port of librus-sdk normalizeMessage).</summary>
public static partial class MessageNormalizer
{
    /// <summary>
    /// Web deep link to one message in the Wiadomości UI.
    /// TODO(verify): the route below follows the current wiadomosci.librus.pl SPA; confirm on a live account.
    /// </summary>
    public static string WebUrlFor(string messageId) =>
        $"https://wiadomosci.librus.pl/module/inbox/details/{Uri.EscapeDataString(messageId)}";

    public static InboxMessage FromWiadomosci(WiadomosciMessage raw, bool includeBody)
    {
        var id = (raw.MessageId ?? raw.Id)?.Value ?? "";
        var extra = raw.AdditionalProperties;
        var senderName = extra.GetStringOrNumber("senderName", "sender", "senderFullName")
            ?? JoinName(extra.GetStringOrNumber("senderFirstName"), extra.GetStringOrNumber("senderLastName"));
        var readAt = ParseDate(raw.ReadDate_?.Value ?? raw.ReadDate?.Value);
        var isRead = readAt is not null
            || extra.GetStringOrNumber("isRead", "read") is "1" or "true";
        return new InboxMessage
        {
            Id = id,
            Subject = raw.Topic ?? raw.Subject,
            SenderId = raw.SenderId?.Value,
            SenderName = senderName,
            SentAt = ParseDate(raw.SendDate_?.Value ?? raw.SendDate?.Value),
            ReadAt = readAt,
            IsRead = isRead,
            Body = includeBody ? DecodeBody(raw.Content ?? raw.Body ?? extra.GetStringOrNumber("Message")) : null,
            HasAttachments = extra is not null && extra.TryGetValue("attachments", out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0,
            Source = MessageSource.Wiadomosci,
            WebUrl = id.Length > 0 ? WebUrlFor(id) : null,
        };
    }

    public static InboxMessage FromApi3(Models.Message raw, bool includeBody)
    {
        var extra = raw.AdditionalProperties;
        var readAt = ParseDate(extra.GetStringOrNumber("ReadDate"));
        var id = raw.Id.Value ?? "";
        return new InboxMessage
        {
            Id = id,
            Subject = extra.GetStringOrNumber("Subject", "Topic"),
            SenderId = raw.Sender?.Id?.Value,
            SentAt = ParseDate(extra.GetStringOrNumber("SendDate")),
            ReadAt = readAt,
            IsRead = readAt is not null,
            Body = includeBody ? DecodeBody(extra.GetStringOrNumber("Body")) : null,
            HasAttachments = extra.GetStringOrNumber("Attachments") is not null,
            Source = MessageSource.Api3,
            WebUrl = id.Length > 0 ? WebUrlFor(id) : null,
        };
    }

    /// <summary>
    /// Message bodies are plain text/HTML, or base64 of a <c>&lt;Message&gt;&lt;Content&gt;&lt;![CDATA[…]]&gt;</c> XML envelope.
    /// </summary>
    public static string? DecodeBody(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var decoded = TryDecodeBase64(value);
        if (decoded is null)
        {
            return value;
        }

        if (!decoded.Contains("<Message", StringComparison.Ordinal))
        {
            return decoded;
        }

        var match = ContentCData().Match(decoded);
        return match.Success ? match.Groups[1].Value : decoded;
    }

    internal static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
        {
            // librus-sdk normalizes to epoch milliseconds; raw payloads may also use seconds.
            return epoch > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : DateTimeOffset.FromUnixTimeSeconds(epoch);
        }

        // Librus sends local Polish time without an offset ("2026-10-01 12:30:00").
        if (DateTime.TryParse(value.Replace(' ', 'T'), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt))
        {
            return dt.Kind == DateTimeKind.Unspecified ? new DateTimeOffset(dt, PolishOffset(dt)) : new DateTimeOffset(dt);
        }

        return null;
    }

    private static TimeSpan PolishOffset(DateTime local)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
            return tz.GetUtcOffset(local);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeSpan.FromHours(1);
        }
    }

    private static string? TryDecodeBase64(string value)
    {
        var compact = WhiteSpace().Replace(value, "");
        if (compact.Length == 0 || compact.Length % 4 == 1 || !Base64Chars().IsMatch(compact))
        {
            return null;
        }

        var padded = compact.PadRight(compact.Length + ((4 - (compact.Length % 4)) % 4), '=');
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return null;
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        // Round-trip check (as librus-sdk) so short plain words like "test" are not mangled.
        var reencoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=');
        return reencoded == compact.TrimEnd('=') ? text : null;
    }

    private static string? JoinName(string? first, string? last)
    {
        var name = $"{first} {last}".Trim();
        return name.Length == 0 ? null : name;
    }

    [GeneratedRegex(@"<Content><!\[CDATA\[([\s\S]*?)\]\]></Content>")]
    private static partial Regex ContentCData();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex("^[A-Za-z0-9+/]*={0,2}$")]
    private static partial Regex Base64Chars();
}
