using System.Net;
using System.Text.Json;
using Librus.Api.Messages;
using Librus.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace Librus.Tests;

public class McpTests
{
    private static readonly LibrusCredentials Creds = new("parent@example.com", "pw");

    [Fact]
    public void Read_only_server_does_not_advertise_reply()
    {
        var tools = new LibrusTools(new FixedSessions(null), new LibrusMcpOptions()).Definitions;
        Assert.DoesNotContain(tools, t => t.Tool.Name == "reply_to_message");
        Assert.Equal(
            ["list_children", "get_grades", "get_attendance", "get_timetable", "list_messages", "get_message", "list_notes", "list_announcements", "get_homework", "get_lucky_number", "get_notifications"],
            tools.Select(t => t.Tool.Name));
        Assert.All(tools, t =>
        {
            Assert.True(t.Tool.Annotations!.ReadOnlyHint);
            Assert.False(t.Tool.Annotations.DestructiveHint);
            Assert.True(t.Tool.Annotations.IdempotentHint);
        });
    }

    [Fact]
    public void Send_flag_registers_a_destructive_reply_tool()
    {
        var tools = new LibrusTools(new FixedSessions(null), new LibrusMcpOptions { EnableSend = true }).Definitions;
        var reply = Assert.Single(tools, t => t.Tool.Name == "reply_to_message");
        Assert.True(reply.Tool.Annotations!.DestructiveHint);
        Assert.False(reply.Tool.Annotations.ReadOnlyHint);
        Assert.Contains("confirm", reply.Tool.InputSchema.GetRawText());
    }

    [Fact]
    public void Send_flag_is_off_by_default()
    {
        Environment.SetEnvironmentVariable(LibrusMcpOptions.EnableSendEnv, null);
        Assert.False(LibrusMcpOptions.FromEnvironment().EnableSend);
    }

    [Fact]
    public async Task Reply_requires_preview_then_confirm()
    {
        var fake = new FakeLibrus().WithPortal()
            .OnJson("GET", "api.librus.pl/3.0/Messages/9", """{"Resources":{},"Url":"u","Message":{"Id":9,"Subject":"Kartkówka","Sender":{"Id":5,"Url":"x"}}}""");
        using var session = await LibrusSession.LoginAsync(Creds, fake.Options());
        var tools = new LibrusTools(new FixedSessions(session), new LibrusMcpOptions { EnableSend = true }).Definitions;
        var reply = tools.Single(t => t.Tool.Name == "reply_to_message");

        var preview = await reply.Handler(Args("""{"child":"11","threadOrMessageId":"9","body":"Dziękuję"}"""), default);
        using (var doc = JsonDocument.Parse(preview))
        {
            Assert.Equal("preview", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal("Re: Kartkówka", doc.RootElement.GetProperty("subject").GetString());
            Assert.Equal("5", doc.RootElement.GetProperty("to").GetString());
            Assert.Equal("Dziękuję", doc.RootElement.GetProperty("body").GetString());
        }

        var ex = await Assert.ThrowsAsync<LibrusException>(() => reply.Handler(Args("""{"child":"11","threadOrMessageId":"9","body":"Dziękuję","confirm":true}"""), default));
        Assert.Equal(LibrusErrorCodes.SendNotImplemented, ex.Code);
    }

    [Fact]
    public async Task List_tools_handler_returns_the_catalog()
    {
        var options = LibrusMcpServer.CreateOptions(new FixedSessions(null), new LibrusMcpOptions(), NullLogger.Instance);
        var list = await options.Handlers.ListToolsHandler!(null!, default);
        Assert.Equal(11, list.Tools.Count);
    }

    [Fact]
    public async Task Grades_tool_returns_resolved_names()
    {
        var fake = new FakeLibrus().WithPortal()
            .OnJson("GET", "api.librus.pl/3.0/Grades", """{"Resources":{},"Url":"u","Grades":[{"Id":1,"Grade":"5","Date":"2026-10-01","Semester":1,"Subject":{"Id":9,"Url":"s"},"Category":{"Id":2,"Url":"c"},"AddedBy":{"Id":3,"Url":"t"}}]}""")
            .OnJson("GET", "api.librus.pl/3.0/Subjects", """{"Resources":{},"Url":"u","Subjects":[{"Id":9,"Name":"Fizyka"}]}""")
            .OnJson("GET", "api.librus.pl/3.0/Users", """{"Resources":{},"Url":"u","Users":[{"Id":3,"FirstName":"Jan","LastName":"Nowak"}]}""");
        using var session = await LibrusSession.LoginAsync(Creds, fake.Options());
        var tool = new LibrusTools(new FixedSessions(session), new LibrusMcpOptions()).Definitions.Single(t => t.Tool.Name == "get_grades");
        var json = await tool.Handler(Args("""{"child":"Ania"}"""), default);
        using var doc = JsonDocument.Parse(json);
        var grade = doc.RootElement[0];
        Assert.Equal("Fizyka", grade.GetProperty("subject").GetString());
        Assert.Equal("Jan Nowak", grade.GetProperty("teacher").GetString());
        Assert.DoesNotContain(fake.Requests, r => r.Uri.Host == "api.librus.pl" && r.Method != "GET");
    }

    [Fact]
    public async Task Missing_child_with_two_children_is_a_clear_error()
    {
        var fake = new FakeLibrus().WithPortal();
        using var session = await LibrusSession.LoginAsync(Creds, fake.Options());
        var tool = new LibrusTools(new FixedSessions(session), new LibrusMcpOptions()).Definitions.Single(t => t.Tool.Name == "get_grades");
        Environment.SetEnvironmentVariable("LIBRUS_CHILD", null);
        var ex = await Assert.ThrowsAsync<LibrusException>(() => tool.Handler(Args("{}"), default));
        Assert.Equal(LibrusErrorCodes.ChildRequired, ex.Code);
    }

    private static ToolArgs Args(string json) =>
        new(JsonSerializer.Deserialize(json, McpTestJson.Default.DictionaryStringJsonElement));

    private sealed class FixedSessions(LibrusSession? session) : ISessionProvider
    {
        public Task<LibrusSession> GetAsync(CancellationToken ct) =>
            Task.FromResult(session ?? throw new LibrusException(LibrusErrorCodes.ConfigurationError, "no session"));
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, JsonElement>))]
internal sealed partial class McpTestJson : System.Text.Json.Serialization.JsonSerializerContext;
