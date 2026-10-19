namespace Hbpos.Client.Wpf.Services;

public enum CardTerminalProgressKind
{
    /// <summary>网络不稳，正在用同一幂等键重发创建请求；Attempt 为即将发出的第几次。</summary>
    NetworkRetrying,

    /// <summary>重试后已连上，恢复为普通的「等待刷卡终端返回结果」。</summary>
    NetworkRecovered
}

/// <summary>刷卡过程中推给收银界面的实时提示。</summary>
public sealed record CardTerminalProgress(CardTerminalProgressKind Kind, int Attempt = 0, int MaxAttempts = 0);

/// <summary>
/// 把付款页的进度回调带进终端客户端的环境作用域。
/// 用 AsyncLocal 随 await 链下传：不改 ICardTerminalClient / 工作流签名，也不新增注入；
/// 没有付款页订阅时（分期、恢复中心、测试）Report 什么都不做。
/// </summary>
public static class CardTerminalProgressScope
{
    private static readonly AsyncLocal<IProgress<CardTerminalProgress>?> CurrentProgress = new();

    public static IDisposable Begin(IProgress<CardTerminalProgress> progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var previous = CurrentProgress.Value;
        CurrentProgress.Value = progress;
        return new Scope(previous);
    }

    public static void Report(CardTerminalProgress progress)
    {
        try
        {
            CurrentProgress.Value?.Report(progress);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 提示只是辅助信息：界面回调出错绝不能打断收款流程。
            ConsoleLog.WriteWarning("CardPayment", $"card terminal progress report failed kind={progress.Kind} error={ex.GetType().Name}");
        }
    }

    private sealed class Scope(IProgress<CardTerminalProgress>? previous) : IDisposable
    {
        public void Dispose()
        {
            CurrentProgress.Value = previous;
        }
    }
}
