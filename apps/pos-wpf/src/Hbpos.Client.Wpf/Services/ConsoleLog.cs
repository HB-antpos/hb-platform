using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Hbpos.Client.Wpf.Services;

internal static class ConsoleLog
{
    private const int AttachParentProcess = -1;
    private static readonly object CenterLogGate = new();
    private static int _attachAttempted;
    private static ApplicationLogDefaults _centerDefaults = ApplicationLogDefaults.Default;
    private static IApplicationLogSink _centerSink = NoopApplicationLogSink.Instance;
    private static readonly object FileLogGate = new();
    private static FileLogWorker? _fileLogWorker;
    private static Task? _fileLogStopTask;
    private static long _droppedFileLogLines;
    private static int _fileLogStopped;

    internal static event Action<string>? LineWritten;

    internal static long DroppedFileLogLineCount => Interlocked.Read(ref _droppedFileLogLines);

    internal static void ConfigureCenterDefaults(ApplicationLogDefaults defaults)
    {
        lock (CenterLogGate)
        {
            _centerDefaults = defaults;
        }
    }

    internal static void ConfigureCenterSink(IApplicationLogSink? sink)
    {
        lock (CenterLogGate)
        {
            _centerSink = sink ?? NoopApplicationLogSink.Instance;
        }
    }

    public static void Write(string category, string message)
    {
        Write(category, message, "Information");
    }

    /// <summary>
    /// 需要人工跟进但不影响收银主流程的情况（例如上传记录被服务端永久拒绝、不再自动重试）。
    /// </summary>
    internal static void WriteWarning(
        string category,
        string message,
        ApplicationLogContext? context = null,
        Exception? exception = null)
    {
        Write(category, message, "Warning", context, exception);
    }

    internal static void WriteError(
        string category,
        string message,
        ApplicationLogContext? context = null,
        Exception? exception = null)
    {
        Write(category, message, "Error", context, exception);
    }

    internal static void WriteCritical(
        string category,
        string message,
        ApplicationLogContext? context = null,
        Exception? exception = null)
    {
        Write(category, message, "Critical", context, exception);
    }

    private static void Write(
        string category,
        string message,
        string level,
        ApplicationLogContext? context = null,
        Exception? exception = null)
    {
        EnsureConsoleAttached();
        var line = $"[HBPOS][Client][{category}] {DateTimeOffset.Now:O} {message}";
        Console.WriteLine(line);
        Debug.WriteLine(line);
        Trace.WriteLine(line);
        WriteDebuggerOutput(line);
        WriteFileLog(line);
        EnqueueCenterLog(category, message, level, context, exception);
        LineWritten?.Invoke(line);
    }

    private static void EnqueueCenterLog(
        string category,
        string message,
        string level,
        ApplicationLogContext? context,
        Exception? exception)
    {
        ApplicationLogDefaults defaults;
        IApplicationLogSink sink;
        lock (CenterLogGate)
        {
            defaults = _centerDefaults;
            sink = _centerSink;
        }

        try
        {
            // 中心日志失败不能影响收银主流程，因此这里只做 best-effort 投递。
            sink.Enqueue(new ApplicationLogEntry(
                level,
                message,
                DateTimeOffset.UtcNow,
                defaults.ProjectCode,
                defaults.Environment,
                defaults.SourceType,
                Category: category,
                ServiceName: category,
                TraceId: context?.TraceId,
                RequestPath: context?.RequestPath,
                RequestMethod: context?.RequestMethod,
                StatusCode: context?.StatusCode,
                UserId: context?.UserId,
                UserName: context?.UserName,
                ExceptionType: exception?.GetType().Name,
                ExceptionMessage: exception?.Message,
                StackTrace: exception?.StackTrace,
                Properties: BuildProperties(category, context?.Properties)));
        }
        catch (Exception)
        {
            // 日志通道不能反向打断 POS UI、支付和订单同步。
        }
    }

    private static IReadOnlyDictionary<string, object?> BuildProperties(
        string category,
        IReadOnlyDictionary<string, object?>? properties)
    {
        var result = properties is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(properties, StringComparer.OrdinalIgnoreCase);
        result["category"] = category;
        return result;
    }

    internal static async Task FlushFileLogAsync(CancellationToken cancellationToken = default)
    {
        FileLogWorker? worker;
        lock (FileLogGate)
        {
            worker = _fileLogWorker;
        }

        if (worker is not null)
        {
            await worker.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task StopFileLogAsync(CancellationToken cancellationToken = default)
    {
        Task stopTask;
        lock (FileLogGate)
        {
            if (_fileLogStopTask is null)
            {
                Interlocked.Exchange(ref _fileLogStopped, 1);
                var worker = _fileLogWorker;
                _fileLogWorker = null;
                _fileLogStopTask = worker is null
                    ? Task.CompletedTask
                    : worker.StopAsync(CancellationToken.None);
            }

            stopTask = _fileLogStopTask;
        }

        await stopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void WriteFileLog(string line)
    {
        var logPath = Environment.GetEnvironmentVariable("HBPOS_CLIENT_LOG_FILE");
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        FileLogWorker worker;
        lock (FileLogGate)
        {
            // 停止与首次写入共用同一把锁，禁止停止完成后重新创建无人回收的 writer。
            if (Volatile.Read(ref _fileLogStopped) != 0 || _fileLogStopTask is not null)
            {
                return;
            }

            if (_fileLogWorker is null)
            {
                _fileLogWorker = new FileLogWorker(logPath.Trim(), () => Interlocked.Increment(ref _droppedFileLogLines));
            }

            worker = _fileLogWorker;
        }

        if (!worker.TryWrite(line))
        {
            Interlocked.Increment(ref _droppedFileLogLines);
        }
    }

    private sealed class FileLogWorker
    {
        private const int Capacity = 4096;
        private readonly Channel<FileLogEntry> _entries = Channel.CreateBounded<FileLogEntry>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        private readonly string _logPath;
        private readonly Action _recordDrop;
        private readonly Task _consumerTask;
        private int _stopped;

        public FileLogWorker(string logPath, Action recordDrop)
        {
            _logPath = logPath;
            _recordDrop = recordDrop;
            _consumerTask = Task.Run(ConsumeAsync);
        }

        public bool TryWrite(string line)
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                return false;
            }

            return _entries.Writer.TryWrite(new FileLogEntry(line, null));
        }

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                return;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await _entries.Writer.WriteAsync(new FileLogEntry(null, completion), cancellationToken).ConfigureAwait(false);
                await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                // 停止与刷新竞争时，调用方只需等待同一个停止任务即可。
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
            {
                _entries.Writer.TryComplete();
            }

            await _consumerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task ConsumeAsync()
        {
            try
            {
                await foreach (var entry in _entries.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    if (entry.FlushCompletion is not null)
                    {
                        entry.FlushCompletion.TrySetResult();
                        continue;
                    }

                    try
                    {
                        var logDirectory = Path.GetDirectoryName(_logPath);
                        if (!string.IsNullOrWhiteSpace(logDirectory))
                        {
                            Directory.CreateDirectory(logDirectory);
                        }

                        File.AppendAllText(_logPath, entry.Line + Environment.NewLine);
                    }
                    catch (Exception)
                    {
                        // 单条文件镜像失败只丢弃当前项，后续日志和 flush 标记仍继续消费。
                        _recordDrop();
                    }
                }
            }
            finally
            {
                while (_entries.Reader.TryRead(out var entry))
                {
                    entry.FlushCompletion?.TrySetCanceled();
                }
            }
        }

        private sealed record FileLogEntry(string? Line, TaskCompletionSource? FlushCompletion);
    }

    private static void EnsureConsoleAttached()
    {
        if (!OperatingSystem.IsWindows() || Interlocked.Exchange(ref _attachAttempted, 1) != 0)
        {
            return;
        }

        _ = AttachConsole(AttachParentProcess);
    }

    private static void WriteDebuggerOutput(string line)
    {
        if (OperatingSystem.IsWindows())
        {
            OutputDebugString(line + Environment.NewLine);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern void OutputDebugString(string lpOutputString);
}
