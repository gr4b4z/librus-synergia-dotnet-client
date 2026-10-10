using System.Net;
using System.Text;
using Librus.Api;
using Librus.Api.Messages;

namespace Librus.Tests;

public class MessagesTests
{
    private static readonly LibrusCredentials Creds = new("parent@example.com", "pw");

    private static FakeLibrus WithWiadomosci(FakeLibrus fake)
    {
        fake.OnJson("GET", "portal.librus.pl/api/v3/SynergiaAccounts/fresh/1111111u", """{"id":11,"login":"1111111u","accessToken":"fresh-a"}""");
        fake.OnJson("POST", "api.librus.pl/2.0/AutoLoginToken", """{"Token":"one-time"}""");
        fake.On("GET", "synergia.librus.pl/loguj/token/one-time/przenies/uczen/widok/centrum_powiadomien", (_, _) => FakeLibrus.Html("ok"));
        fake.On("GET", "synergia.librus.pl/wiadomosci3", (_, _) => FakeLibrus.Html("ok"));
        fake.OnJson("GET", "wiadomosci.librus.pl/api/inbox/messages", """
            {"data":[
              {"messageId":"501","senderId":"77","senderFirstName":"Anna","senderLastName":"Nauczycielska","topic":"Wycieczka","sendDate":"2026-10-09 12:30:00","readDate":null},
              {"messageId":"500","senderId":"78","topic":"Zebranie","sendDate":"2026-10-01 08:00:00","readDate":"2026-10-01 09:00:00"}
            ]}
            """);
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes("<Message><Content><![CDATA[Proszę o zgodę.]]></Content></Message>"));
        fake.OnJson("GET", "wiadomosci.librus.pl/api/inbox/messages/501", $$$"""{"data":{"messageId":"501","senderId":"77","topic":"Wycieczka","Message":"{{{body}}}"}}""");
        return fake;
    }

    [Fact]
    public async Task Api3_403_falls_back_to_wiadomosci_and_sticks()
    {
        var fake = WithWiadomosci(new FakeLibrus().WithPortal());
        fake.OnJson("GET", "api.librus.pl/3.0/Messages", "{}", HttpStatusCode.Forbidden);
        using var session = await LibrusSession.LoginAsync(Creds, fake.Options());
        var client = await session.ForChildAsync("11");

        var page = await client.ListInboxAsync();
        Assert.Equal(MessageSource.Wiadomosci, page.Source);
        Assert.Equal(["501", "500"], page.Messages.Select(m => m.Id));
        Assert.Equal("Anna Nauczycielska", page.Messages[0].SenderName);
        Assert.False(page.Messages[0].IsRead);
        Assert.True(page.Messages[1].IsRead);

        var unread = await client.ListInboxAsync(unreadOnly: true);
        Assert.Equal(["501"], unread.Messages.Select(m => m.Id));

        Assert.Equal(1, fake.Requests.Count(r => r.Path == "api.librus.pl/3.0/Messages"));
        Assert.Equal(1, fake.Requests.Count(r => r.Path == "api.librus.pl/2.0/AutoLoginToken"));
        var autoLogin = fake.Requests.Single(r => r.Path == "api.librus.pl/2.0/AutoLoginToken");
        Assert.Equal("Bearer fresh-a", autoLogin.Authorization);
    }

    [Fact]
    public async Task Message_body_is_decoded_from_base64_xml()
    {
        var fake = WithWiadomosci(new FakeLibrus().WithPortal());
        fake.OnJson("GET", "api.librus.pl/3.0/Messages/501", "{}", HttpStatusCode.Forbidden);
        using var session = await LibrusSession.LoginAsync(Creds, fake.Options());
        var message = await (await session.ForChildAsync("11")).GetInboxMessageAsync("501");
        Assert.Equal("Proszę o zgodę.", message.Body);
        Assert.Equal("Wycieczka", message.Subject);
        Assert.Contains("501", message.WebUrl);
    }

    [Fact]
    public async Task Wiadomosci_401_reauthenticates_once()
    {
        var fake = WithWiadomosci(new FakeLibrus().WithPortal());
        fake.OnJson("GET", "api.librus.pl/3.0/Messages/Unread", "{}", HttpStatusCode.Forbidden);
        var calls = 0;
        fake.On("GET", "wiadomosci.librus.pl/api/inbox/unreadMessagesCount", (_, _) => ++calls == 1
            ? FakeLibrus.Status(HttpStatusCode.Unauthorized)
            : FakeLibrus.Json("""{"data":"3"}"""));
        using var session = await LibrusSession.LoginAsync(Creds, fake.Options());
        Assert.Equal(3, await (await session.ForChildAsync("11")).GetUnreadCountAsync());
        Assert.Equal(2, fake.Requests.Count(r => r.Path == "api.librus.pl/2.0/AutoLoginToken"));
    }

    [Fact]
    public async Task Api3_messages_are_used_when_permitted()
    {
        var fake = new FakeLibrus().WithPortal().OnJson("GET", "api.librus.pl/3.0/Messages",
            """{"Resources":{},"Url":"u","Messages":[{"Id":9,"Subject":"Hej","Sender":{"Id":5,"Url":"x"},"SendDate":"2026-10-02 10:00:00","ReadDate":null}]}""");
        using var session = await LibrusSession.LoginAsync(Creds, fake.Options());
        var page = await (await session.ForChildAsync("11")).ListInboxAsync();
        Assert.Equal(MessageSource.Api3, page.Source);
        var m = Assert.Single(page.Messages);
        Assert.Equal(("9", "Hej", "5", false), (m.Id, m.Subject, m.SenderId, m.IsRead));
        Assert.Contains("alternativeBody=true", fake.Requests.Last().Uri.Query);
    }

    [Theory]
    [InlineData("test", "test")]
    [InlineData("Zwykły tekst wiadomości", "Zwykły tekst wiadomości")]
    [InlineData("SGVsbG8=", "Hello")]
    public void Plain_bodies_are_left_untouched_and_base64_decoded(string input, string expected) =>
        Assert.Equal(expected, MessageNormalizer.DecodeBody(input));

    [Fact]
    public void Polish_local_times_get_an_offset()
    {
        var parsed = MessageNormalizer.ParseDate("2026-07-01 12:00:00");
        Assert.NotNull(parsed);
        Assert.Equal(12, parsed.Value.Hour);
    }

    [Fact]
    public async Task Reply_draft_echoes_recipient_subject_body_and_sender_refuses()
    {
        var fake = WithWiadomosci(new FakeLibrus().WithPortal());
        fake.OnJson("GET", "api.librus.pl/3.0/Messages/501", "{}", HttpStatusCode.Forbidden);
        using var session = await LibrusSession.LoginAsync(Creds, fake.Options());
        var client = await session.ForChildAsync("11");
        var draft = await ReplyComposer.PrepareAsync(client, "501", "  Wyrażam zgodę.  ");
        Assert.Equal(("77", "Re: Wycieczka", "Wyrażam zgodę."), (draft.ToId, draft.Subject, draft.Body));

        var ex = await Assert.ThrowsAsync<LibrusException>(() => new NotImplementedReplySender().SendReplyAsync(client, draft));
        Assert.Equal(LibrusErrorCodes.SendNotImplemented, ex.Code);
        Assert.DoesNotContain(fake.Requests, r => r.Method == "POST" && r.Uri.Host == "wiadomosci.librus.pl");
    }
}
