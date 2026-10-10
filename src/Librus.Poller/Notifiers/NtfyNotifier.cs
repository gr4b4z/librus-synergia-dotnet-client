using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Librus.Http;

namespace Librus.Poller.Notifiers;

/// <summary>
/// Publishes to an ntfy topic using the JSON publish API (UTF-8 safe titles).
/// Env: <c>LIBRUS_NTFY_TOPIC</c> (required), <c>LIBRUS_NTFY_URL</c> (default https://ntfy.sh),
/// <c>LIBRUS_NTFY_TOKEN</c> (optional access token, or <c>"ntfyToken"</c> in secrets.json).
/// </summary>
public sealed class NtfyNotifier : INotifier, IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _server;
    private readonly string _topic;
    private readonly string? _token;

    public NtfyNotifier(LibrusHttpOptions http, string topic, Uri? server = null, string? token = null)
    {
        _http = http.CreateClient(cookies: null, "Librus.Notifier.Ntfy");
        _server = server ?? new Uri("https://ntfy.sh/");
        _topic = topic;
        _token = token;
    }

    public string Name => "ntfy";

    public static NtfyNotifier? FromEnvironment(LibrusHttpOptions http)
    {
        var topic = Environment.GetEnvironmentVariable("LIBRUS_NTFY_TOPIC");
        if (string.IsNullOrEmpty(topic))
        {
            return null;
        }

        var url = Environment.GetEnvironmentVariable("LIBRUS_NTFY_URL");
        return new NtfyNotifier(http, topic, string.IsNullOrEmpty(url) ? null : new Uri(url), LibrusSecrets.Get("LIBRUS_NTFY_TOKEN", "ntfyToken"));
    }

    public async Task SendAsync(PendingNotification notification, CancellationToken ct = default)
    {
        var payload = new NtfyMessage
        {
            Topic = _topic,
            Title = notification.Title,
            Message = string.IsNullOrEmpty(notification.Body) ? notification.Title : notification.Body,
            Click = notification.Url,
            Tags = [notification.Category switch
            {
                Categories.Messages => "envelope",
                Categories.Grades => "pencil",
                Categories.Notes => "warning",
                Categories.Announcements => "loudspeaker",
                Categories.Homework => "books",
                _ => "school",
            }],
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, _server)
        {
            Content = JsonContent.Create(payload, NotifierJsonContext.Default.NtfyMessage),
        };
        if (!string.IsNullOrEmpty(_token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        using var response = await HttpErrors.SendAsync(_http, request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw LibrusException.FromStatus(_server.ToString(), (int)response.StatusCode, "ntfy publish failed");
        }
    }

    public void Dispose() => _http.Dispose();
}

internal sealed class NtfyMessage
{
    [JsonPropertyName("topic")] public string Topic { get; set; } = "";
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("click")] public string? Click { get; set; }
    [JsonPropertyName("tags")] public string[]? Tags { get; set; }
}

internal sealed class TelegramMessage
{
    [JsonPropertyName("chat_id")] public string ChatId { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("disable_web_page_preview")] public bool DisableWebPagePreview { get; set; } = true;
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(NtfyMessage))]
[JsonSerializable(typeof(TelegramMessage))]
internal sealed partial class NotifierJsonContext : JsonSerializerContext;
