using Microsoft.Extensions.Logging;

namespace Librus.Cli;

/// <summary>Minimal structured logger writing to stderr (stdout is reserved for results / MCP JSON-RPC).</summary>
internal sealed class StderrLoggerProvider(LogLevel minLevel) : ILoggerProvider, ILoggerFactory
{
    private static readonly Lock WriteLock = new();

    public ILogger CreateLogger(string categoryName) => new StderrLogger(categoryName, minLevel);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    public static LogLevel ParseLevel(string? value, LogLevel fallback) => value?.ToLowerInvariant() switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "info" or "information" => LogLevel.Information,
        "warn" or "warning" => LogLevel.Warning,
        "error" => LogLevel.Error,
        "none" or "off" => LogLevel.None,
        _ => fallback,
    };

    private sealed class StderrLogger(string category, LogLevel minLevel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            // Exceptions are reduced to their type: messages of transport exceptions can embed URLs.
            var line = $"{DateTimeOffset.Now:HH:mm:ss} {Short(logLevel)} {category}: {formatter(state, exception)}"
                + (exception is null ? "" : $" ({exception.GetType().Name})");
            lock (WriteLock)
            {
                Console.Error.WriteLine(line);
            }
        }

        private static string Short(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            _ => "crit",
        };
    }
}
