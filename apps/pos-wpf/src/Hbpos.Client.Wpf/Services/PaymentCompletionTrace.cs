using System.Diagnostics;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 「刷卡成功后完成订单」的分步追踪。
/// 1031 店 2026-10-24 多次出现：Square 已扣款、付款明细已加入，但订单迟迟不落盘且中心日志一行都没有，
/// 无法判断卡在哪一步。每个关键步骤前调用 <see cref="Mark"/>：写一条带耗时的日志，
/// 并记下“最后到达的步骤”，供付款页看门狗在超时时写入诊断。
/// 追踪对象通过 AsyncLocal 向下传递（Task.Run 会携带 ExecutionContext），
/// 因此工作流服务无需改任何公开签名；没有追踪对象时 <see cref="Mark"/> 什么也不做。
/// </summary>
internal sealed class PaymentCompletionTrace
{
    private static readonly AsyncLocal<PaymentCompletionTrace?> CurrentTrace = new();

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly string _label;
    private long _lastStepAtMs;
    private volatile string _lastStep = "start";

    private PaymentCompletionTrace(string label)
    {
        _label = label;
    }

    public static PaymentCompletionTrace? Current => CurrentTrace.Value;

    /// <summary>最后到达的步骤名；步骤开始前就记录，所以它就是“当前正在做/卡住的步骤”。</summary>
    public string LastStep => _lastStep;

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    /// <summary>距最后一步开始已过去的时间。</summary>
    public TimeSpan SinceLastStep =>
        TimeSpan.FromMilliseconds(Math.Max(0, _stopwatch.ElapsedMilliseconds - Interlocked.Read(ref _lastStepAtMs)));

    /// <summary>开始一次追踪并绑定到当前异步上下文；调用方在 finally 中调用 <see cref="Detach"/>。</summary>
    public static PaymentCompletionTrace Begin(string label)
    {
        var trace = new PaymentCompletionTrace(label);
        CurrentTrace.Value = trace;
        trace.MarkStep("begin");
        return trace;
    }

    public static void Mark(string step) => CurrentTrace.Value?.MarkStep(step);

    public void Detach()
    {
        if (ReferenceEquals(CurrentTrace.Value, this))
        {
            CurrentTrace.Value = null;
        }
    }

    public void MarkStep(string step)
    {
        var now = _stopwatch.ElapsedMilliseconds;
        var previousAt = Interlocked.Exchange(ref _lastStepAtMs, now);
        _lastStep = step;
        try
        {
            ConsoleLog.Write(
                "PaymentCompletion",
                $"{_label} step={step} elapsedMs={now} sinceLastMs={Math.Max(0, now - previousAt)}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 诊断日志失败绝不能影响付款流程。
        }
    }
}
