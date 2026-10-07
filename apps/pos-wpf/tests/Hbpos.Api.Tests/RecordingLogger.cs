using Microsoft.Extensions.Logging;

namespace Hbpos.Api.Tests;

/// <summary>记录级别、EventId 与渲染后消息的测试日志器，用于断言上传接口的受理/拒绝日志。</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, EventId EventId, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, eventId, formatter(state, exception)));
    }
}
