using Microsoft.Extensions.Logging;

namespace Hbpos.Client.Wpf.Services.Zeller.Sdk;

/// <summary>把 Zeller SDK 自己的日志转到收银端日志（ConsoleLog：本机日志文件 + 中心日志）。</summary>
/// <remarks>
/// 中文注释：ConsoleLog 写的每一行都会进中心日志。SDK 0.1.0 的日志全是 Debug 级，
/// 一次测试就有上千行「preparing directories」「received message」，所以默认只转 Information 及以上；
/// 需要排查 SDK 内部问题时把最低级别调到 Debug。SDK 的日志里没有令牌、卡号或小票内容（2026-11-05 核对过）。
/// </remarks>
public static class ZellerSdkLogging
{
    public const string Category = "ZellerSdk";

    /// <summary>给 <see cref="ZellerSdkTerminalFactory"/> 用的日志工厂。</summary>
    public static ILoggerFactory CreateLoggerFactory(LogLevel minimumLevel = LogLevel.Information) =>
        new LoggerFactory(
            [new ZellerSdkLoggerProvider(minimumLevel)],
            new LoggerFilterOptions { MinLevel = minimumLevel });

    internal static void WriteToConsoleLog(LogLevel level, string message, Exception? exception)
    {
        switch (level)
        {
            case LogLevel.Critical:
                ConsoleLog.WriteCritical(Category, message, exception: exception);
                break;
            case LogLevel.Error:
                ConsoleLog.WriteError(Category, message, exception: exception);
                break;
            case LogLevel.Warning:
                ConsoleLog.WriteWarning(Category, message, exception: exception);
                break;
            default:
                // ConsoleLog 没有 Debug 级，调低最低级别时 Debug / Trace 也按 Information 写，行首带上原级别。
                ConsoleLog.WriteInformation(Category, level >= LogLevel.Information ? message : $"[{level}] {message}");
                break;
        }
    }
}

/// <summary>SDK 日志的转接。sink 可替换，测试用它收集日志行。</summary>
internal sealed class ZellerSdkLoggerProvider : ILoggerProvider
{
    private readonly LogLevel _minimumLevel;
    private readonly Action<LogLevel, string, Exception?> _sink;

    public ZellerSdkLoggerProvider(LogLevel minimumLevel = LogLevel.Information, Action<LogLevel, string, Exception?>? sink = null)
    {
        _minimumLevel = minimumLevel;
        _sink = sink ?? ZellerSdkLogging.WriteToConsoleLog;
    }

    public ILogger CreateLogger(string categoryName) => new SdkLogger(categoryName, _minimumLevel, _sink);

    public void Dispose()
    {
    }

    private sealed class SdkLogger(string category, LogLevel minimumLevel, Action<LogLevel, string, Exception?> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= minimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            try
            {
                sink(logLevel, $"{category}: {formatter(state, exception)}", exception);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 写日志失败不能影响 SDK 的调用。
            }
        }
    }
}
