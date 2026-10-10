using System.Net.Http.Json;
using Librus.Http;

namespace Librus.Poller.Notifiers;

/// <summary>
/// Sends plain-text Telegram bot messages. Env: <c>LIBRUS_TELEGRAM_BOT_TOKEN</c> (or
/// <c>"telegramBotToken"</c> in secrets.json) and <c>LIBRUS_TELEGRAM_CHAT_ID</c>.
/// The bot token sits in the URL path, which <see cref="UrlRedactor"/> masks in logs.
/// </summary>
public sealed class TelegramNotifier : INotifier, IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _chatId;

    public TelegramNotifier(LibrusHttpOptions http, string botToken, string chatId, Uri? apiBase = null)
    {
        _http = http.CreateClient(cookies: null, "Librus.Notifier.Telegram");
        _endpoint = new Uri(apiBase ?? new Uri("https://api.telegram.org/"), $"bot{botToken}/sendMessage");
        _chatId = chatId;
    }

    public string Name => "telegram";

    public static TelegramNotifier? FromEnvironment(LibrusHttpOptions http)
    {
        var token = LibrusSecrets.Get("LIBRUS_TELEGRAM_BOT_TOKEN", "telegramBotToken");
        var chat = Environment.GetEnvironmentVariable("LIBRUS_TELEGRAM_CHAT_ID");
        return string.IsNullOrEmpty(token) || string.IsNullOrEmpty(chat) ? null : new TelegramNotifier(http, token, chat);
    }

    public async Task SendAsync(PendingNotification notification, CancellationToken ct = default)
    {
        var lines = new List<string> { notification.Title };
        if (!string.IsNullOrEmpty(notification.Body))
        {
            lines.Add(notification.Body);
        }

        if (!string.IsNullOrEmpty(notification.Url))
        {
            lines.Add(notification.Url);
        }

        var payload = new TelegramMessage { ChatId = _chatId, Text = string.Join('\n', lines) };
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(payload, NotifierJsonContext.Default.TelegramMessage),
        };
        using var response = await HttpErrors.SendAsync(_http, request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw LibrusException.FromStatus(UrlRedactor.Redact(_endpoint), (int)response.StatusCode, "Telegram sendMessage failed");
        }
    }

    public void Dispose() => _http.Dispose();
}
