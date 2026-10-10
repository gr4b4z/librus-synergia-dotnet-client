using System.Reflection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Librus.Mcp;

/// <summary>stdio MCP server exposing Librus reads (and, opt-in, one reply tool).</summary>
public static class LibrusMcpServer
{
    public const string ServerName = "librus-net";

    public static async Task RunStdioAsync(ISessionProvider sessions, LibrusMcpOptions options, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var serverOptions = CreateOptions(sessions, options, loggerFactory.CreateLogger("Librus.Mcp"));
        await using var transport = new StdioServerTransport(serverOptions, loggerFactory);
        await using var server = McpServer.Create(transport, serverOptions, loggerFactory);
        await server.RunAsync(ct).ConfigureAwait(false);
    }

    internal static McpServerOptions CreateOptions(ISessionProvider sessions, LibrusMcpOptions options, ILogger logger)
    {
        var tools = new LibrusTools(sessions, options);
        var byName = tools.Definitions.ToDictionary(t => t.Tool.Name, StringComparer.Ordinal);
        return new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = ServerName,
                Version = typeof(LibrusMcpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0",
            },
            ServerInstructions = "Unofficial, read-mostly access to Librus Synergia (Polish school e-register) for a parent. "
                + "Call list_children first; every other tool takes a `child` selector. Data is personal: do not share it outside the conversation."
                + (options.EnableSend ? " reply_to_message requires a preview round-trip and explicit human approval (confirm=true)." : ""),
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = false } },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = tools.Definitions.Select(t => t.Tool).ToList() }),
                CallToolHandler = async (request, ct) =>
                {
                    var name = request.Params?.Name ?? "";
                    if (!byName.TryGetValue(name, out var tool))
                    {
                        return Error(LibrusErrorCodes.InvalidArgument, $"Unknown tool \"{name}\".");
                    }

                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    try
                    {
                        var json = await tool.Handler(new ToolArgs(request.Params?.Arguments?.AsReadOnly()), ct).ConfigureAwait(false);
                        logger.LogInformation("mcp.tool {Tool} ok in {DurationMs}ms", name, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        return new CallToolResult { Content = [new TextContentBlock { Text = json }] };
                    }
                    catch (LibrusException ex)
                    {
                        logger.LogWarning("mcp.tool {Tool} failed code={Code} status={Status}", name, ex.Code, ex.Status);
                        return Error(ex.Code, ex.Message);
                    }
                },
            },
        };
    }

    private static CallToolResult Error(string code, string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = System.Text.Json.JsonSerializer.Serialize(new McpError(code, message), McpJsonContext.Default.McpError) }],
    };
}

internal sealed record McpError(string Code, string Message);

internal sealed record ReplyPreview(string Status, string? To, string? ToId, string Subject, string Body, string InReplyTo, string Child, string Next);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(McpError))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ReplyPreview))]
internal sealed partial class McpJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
