using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Solo.Ai.Tests;

internal sealed class GenerationLog : ILoggerProvider
{
    public ConcurrentQueue<string> Entries { get; } = new();
    public Action<string>? OnEntry { get; set; }
    public ILogger CreateLogger(string categoryName) => new Capture(this);
    public void Dispose() { }

    private sealed class Capture(GenerationLog owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var entry = formatter(state, exception) + (exception is null ? "" : "\n" + exception);
            owner.Entries.Enqueue(entry);
            owner.OnEntry?.Invoke(entry);
        }
    }
}
