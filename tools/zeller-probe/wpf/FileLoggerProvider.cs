using Microsoft.Extensions.Logging;

namespace ZellerWpfSpike;

/// <summary>
/// 把 SDK 自己的诊断日志（类别 zeller.sdk.dotnet.*）写进文件；警告及以上同时回显到界面。
/// </summary>
/// <remarks>
/// 中文注释：正式接入时会把 Terminal.LoggerFactory 接到收银端的日志，这里先验证这条路走得通，
/// 并在调用挂起时留下 SDK 内部的线索。
/// </remarks>
internal sealed class FileLoggerProvider(string path, Action<string> onWarning) : ILoggerProvider
{
    private readonly object _gate = new();

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {category}: {message}{(exception is null ? string.Empty : " | " + exception)}";
        lock (_gate)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }

        if (level >= LogLevel.Warning)
        {
            onWarning($"SDK {level} {category}: {message}");
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            owner.Write(category, logLevel, formatter(state, exception), exception);
    }
}
