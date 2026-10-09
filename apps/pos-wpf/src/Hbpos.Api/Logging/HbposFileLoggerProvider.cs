using System.Collections.Concurrent;
using System.Globalization;

namespace Hbpos.Api.Logging;

public static class HbposFileLoggingExtensions
{
    public static ILoggingBuilder AddHbposFileLogging(
        this ILoggingBuilder logging,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var enabled = configuration.GetValue("FileLogging:Enabled", true);
        if (!enabled)
        {
            return logging;
        }

        var path = Environment.GetEnvironmentVariable("HBPOS_API_LOG_FILE");
        if (string.IsNullOrWhiteSpace(path))
        {
            path = configuration.GetValue<string>("FileLogging:Path");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(environment.ContentRootPath, "logs", "hbpos-api.log");
        }
        else if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(environment.ContentRootPath, path);
        }

        var minimumLevel = ParseLevel(
            configuration.GetValue<string>("FileLogging:MinimumLevel"),
            LogLevel.Information);
        // 文件日志按大小轮转：生产上曾长成约 600 MB 的单文件，磁盘写满后写入被静默吞掉，排障证据整体丢失。
        var maxFileBytes = Math.Max(1, configuration.GetValue("FileLogging:MaxFileSizeMb", 50)) * 1024L * 1024L;
        var retainedFiles = Math.Max(1, configuration.GetValue("FileLogging:RetainedFiles", 5));
        logging.AddProvider(new HbposFileLoggerProvider(path, minimumLevel, maxFileBytes, retainedFiles));
        return logging;
    }

    private static LogLevel ParseLevel(string? value, LogLevel fallback)
    {
        return Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level)
            ? level
            : fallback;
    }
}

public sealed class HbposFileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, HbposFileLogger> loggers = new(StringComparer.Ordinal);
    private readonly object syncRoot = new();
    private readonly string path;
    private readonly LogLevel minimumLevel;
    private readonly long maxFileBytes;
    private readonly int retainedFiles;

    public HbposFileLoggerProvider(
        string path,
        LogLevel minimumLevel,
        long maxFileBytes = DefaultMaxFileBytes,
        int retainedFiles = DefaultRetainedFiles)
    {
        this.path = Path.GetFullPath(path);
        this.minimumLevel = minimumLevel;
        this.maxFileBytes = maxFileBytes;
        this.retainedFiles = Math.Max(1, retainedFiles);
    }

    public const long DefaultMaxFileBytes = 50L * 1024 * 1024;
    public const int DefaultRetainedFiles = 5;

    public ILogger CreateLogger(string categoryName)
    {
        return loggers.GetOrAdd(categoryName, category => new HbposFileLogger(category, this));
    }

    public void Dispose()
    {
        loggers.Clear();
    }

    private bool IsEnabled(LogLevel logLevel)
    {
        return logLevel != LogLevel.None && logLevel >= minimumLevel;
    }

    private void WriteLine(string line)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            lock (syncRoot)
            {
                RotateIfNeeded();
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // 文件日志不能影响 API 主流程。
        }
    }

    // 当前文件达到上限时滚动：path -> path.1 -> ... -> path.N，最旧的一个被删除。调用方已持有 syncRoot。
    private void RotateIfNeeded()
    {
        try
        {
            var current = new FileInfo(path);
            if (!current.Exists || current.Length < maxFileBytes)
            {
                return;
            }

            var oldest = $"{path}.{retainedFiles}";
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (var index = retainedFiles - 1; index >= 1; index--)
            {
                var source = $"{path}.{index}";
                if (File.Exists(source))
                {
                    File.Move(source, $"{path}.{index + 1}");
                }
            }

            File.Move(path, $"{path}.1");
        }
        catch
        {
            // 轮转失败（例如文件被占用）时继续追加，宁可文件变大也不能丢日志或影响主流程。
        }
    }

    private sealed class HbposFileLogger(string categoryName, HbposFileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return provider.IsEnabled(logLevel);
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception is null)
            {
                return;
            }

            var timestamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
            var eventText = eventId.Id == 0
                ? string.Empty
                : $" eventId={eventId.Id}";
            var line = $"[{timestamp}] [{logLevel}] [{categoryName}]{eventText} {message}";
            provider.WriteLine(line);
            if (exception is not null)
            {
                provider.WriteLine(exception.ToString());
            }
        }
    }
}
