using System.Diagnostics;
using System.Globalization;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 驱动 Core 方法走到哪一步的稳定英文标识。写入中心日志的是这些标识而不是本地化文案，
/// 这样中文界面的收银机和英文界面的收银机产生的日志可以用同一个条件检索。
/// </summary>
internal static class PrinterStages
{
    internal const string InitSdk = "init-sdk";
    internal const string OpenPort = "open-port";
    internal const string Initialize = "initialize";
    internal const string LineSpace = "line-space";
    /// <summary>打印机状态读取本身失败（端口能打开，但打印机没有应答），松动的线缆、断电、USB 异常常停在这里。</summary>
    internal const string StatusRead = "status-read";
    /// <summary>状态读取成功，但打印机报告盖子打开、缺纸、故障或未就绪。</summary>
    internal const string StatusFlags = "status-flags";
    internal const string Barcode = "barcode";
    internal const string QrCode = "qr-code";
    internal const string Text = "text";
    internal const string Cut = "cut";
    internal const string DrawerPulse = "drawer-pulse";
}

/// <summary>
/// 一次打印机 SDK 调用的现场记录：驱动的 Core 方法每走一步就更新 <see cref="Stage"/>，
/// 失败时把“卡在哪一步、SDK 返回码、打印机状态字节”连同结果一起上报中心日志。
/// </summary>
internal sealed class PrinterCallTrace(string operation, string port, int elementCount = 0)
{
    /// <summary>print / cash-drawer。</summary>
    public string Operation { get; } = operation;

    public string Port { get; } = port;

    public int ElementCount { get; } = elementCount;

    public string Stage { get; set; } = PrinterStages.InitSdk;

    public int? SdkResult { get; set; }

    public int? PrinterStatus { get; set; }
}

/// <summary>
/// 打印机连接的中心日志状态机：每次失败记一条 Warning（带卡住的步骤、SDK 返回码、连续失败次数），
/// 失败之后第一次成功记一条 Information（带停机时长），平时成功不记，避免每张小票都写日志。
/// 驱动是单例且收银机只有一台打印机，所以状态是进程级静态的，并用 lock 保证并发调用下计数一致。
/// 日志投递是 best-effort：任何日志环节出错都不能影响出纸结果，否则会出现“纸已打出来、界面却报错、收银员又重打一张”。
/// </summary>
internal static class PrinterLinkLogState
{
    private const string Category = "Printer";
    private static readonly object Gate = new();
    private static int _consecutiveFailures;
    private static DateTimeOffset? _firstFailureUtc;
    private static DateTimeOffset? _lastSuccessUtc;

    /// <summary>
    /// 在线程池上执行 SDK 调用并按结果记日志。取消不算打印机故障；其它异常（例如缺少 printer.sdk.dll）
    /// 记一条带堆栈的 Error 后原样抛出，调用方现有的异常处理保持不变。
    /// </summary>
    internal static async Task<ReceiptPrinterDriverResult> RunAsync(
        PrinterCallTrace trace,
        Func<ReceiptPrinterDriverResult> core,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await Task.Run(core, cancellationToken);
            Record(trace, result, stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordException(trace, ex, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    internal static void Record(
        PrinterCallTrace trace,
        ReceiptPrinterDriverResult result,
        long elapsedMs,
        DateTimeOffset? nowUtc = null)
    {
        try
        {
            var now = nowUtc ?? DateTimeOffset.UtcNow;
            if (result.Succeeded)
            {
                RecordSuccess(trace, elapsedMs, now);
            }
            else
            {
                RecordFailure(trace, result.Message, exception: null, elapsedMs, now);
            }
        }
        catch (Exception)
        {
            // 日志环节出错不能反向影响出纸结果。
        }
    }

    internal static void RecordException(
        PrinterCallTrace trace,
        Exception exception,
        long elapsedMs,
        DateTimeOffset? nowUtc = null)
    {
        try
        {
            RecordFailure(trace, exception.Message, exception, elapsedMs, nowUtc ?? DateTimeOffset.UtcNow);
        }
        catch (Exception)
        {
            // 同上：不能让日志的异常盖住真正的打印异常。
        }
    }

    private static void RecordFailure(
        PrinterCallTrace trace,
        string? resultMessage,
        Exception? exception,
        long elapsedMs,
        DateTimeOffset now)
    {
        int consecutiveFailures;
        long downSeconds;
        long? lastSuccessSecondsAgo;
        lock (Gate)
        {
            _consecutiveFailures++;
            // 停机时长从第一次失败算起；lastSuccessSecondsAgo 则指向最后一次确认正常的时刻，两者之间就是故障发生的窗口。
            _firstFailureUtc ??= now;
            consecutiveFailures = _consecutiveFailures;
            downSeconds = Seconds(now - _firstFailureUtc.Value);
            lastSuccessSecondsAgo = _lastSuccessUtc is { } lastSuccess ? Seconds(now - lastSuccess) : null;
        }

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["status"] = "failed",
            ["operation"] = trace.Operation,
            ["stage"] = trace.Stage,
            ["port"] = trace.Port,
            ["elapsedMs"] = elapsedMs,
            ["consecutiveFailures"] = consecutiveFailures,
            ["downSeconds"] = downSeconds,
            ["resultMessage"] = resultMessage
        };
        if (trace.ElementCount > 0)
        {
            properties["elementCount"] = trace.ElementCount;
        }

        if (trace.SdkResult is { } sdkResult)
        {
            properties["sdkResult"] = sdkResult;
        }

        if (trace.PrinterStatus is { } printerStatus)
        {
            properties["printerStatus"] = printerStatus;
        }

        if (lastSuccessSecondsAgo is { } secondsAgo)
        {
            properties["lastSuccessSecondsAgo"] = secondsAgo;
        }

        var message =
            $"printer {trace.Operation} failed stage={trace.Stage} sdkResult={Format(trace.SdkResult)} " +
            $"printerStatus={FormatHex(trace.PrinterStatus)} port={trace.Port} elapsedMs={elapsedMs} " +
            $"consecutiveFailures={consecutiveFailures} downSeconds={downSeconds}" +
            (exception is null ? string.Empty : $" error={exception.GetType().Name}");
        var context = new ApplicationLogContext(Properties: properties);
        if (exception is null)
        {
            ConsoleLog.WriteWarning(Category, message, context);
        }
        else
        {
            ConsoleLog.WriteError(Category, message, context, exception);
        }
    }

    private static void RecordSuccess(PrinterCallTrace trace, long elapsedMs, DateTimeOffset now)
    {
        int failedAttempts;
        long downSeconds;
        lock (Gate)
        {
            _lastSuccessUtc = now;
            if (_consecutiveFailures == 0)
            {
                return;
            }

            failedAttempts = _consecutiveFailures;
            downSeconds = _firstFailureUtc is { } firstFailure ? Seconds(now - firstFailure) : 0;
            _consecutiveFailures = 0;
            _firstFailureUtc = null;
        }

        ConsoleLog.WriteInformation(
            Category,
            $"printer recovered operation={trace.Operation} failedAttempts={failedAttempts} downSeconds={downSeconds} port={trace.Port} elapsedMs={elapsedMs}",
            new ApplicationLogContext(
                Properties: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["status"] = "recovered",
                    ["operation"] = trace.Operation,
                    ["port"] = trace.Port,
                    ["elapsedMs"] = elapsedMs,
                    ["failedAttempts"] = failedAttempts,
                    ["downSeconds"] = downSeconds
                }));
    }

    /// <summary>仅供测试复位进程级状态。</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _consecutiveFailures = 0;
            _firstFailureUtc = null;
            _lastSuccessUtc = null;
        }
    }

    private static long Seconds(TimeSpan span) => (long)Math.Max(0, span.TotalSeconds);

    private static string Format(int? value) =>
        value is { } number ? number.ToString(CultureInfo.InvariantCulture) : "-";

    private static string FormatHex(int? value) =>
        value is { } number ? "0x" + number.ToString("X2", CultureInfo.InvariantCulture) : "-";
}
