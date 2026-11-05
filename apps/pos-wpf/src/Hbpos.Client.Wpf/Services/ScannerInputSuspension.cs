namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 临时暂停主窗口的扫码输入（带引用计数）。
/// </summary>
/// <remarks>
/// 中文注释：原始扫码（RawScannerService）用 RIDEV_INPUTSINK 注册，窗口在后台时也会收到扫码枪的输入并交给当前页面。
/// Zeller 配对时 SDK 会弹出自己的登录窗口，店长在里面打字的同时如果有扫码枪读数，就会被主窗口当成商品扫码。
/// 配对期间持有一个暂停，主窗口据此丢弃原始扫码与键盘扫码缓冲，也不再抢焦点。
/// </remarks>
public interface IScannerInputSuspension
{
    /// <summary>是否有任何一处正持有暂停。</summary>
    bool IsSuspended { get; }

    /// <summary>暂停状态在「无 ↔ 有」之间切换时触发（不是每次持有或释放都触发）。可能在任意线程上触发。</summary>
    event EventHandler? Changed;

    /// <summary>开始暂停。释放返回的对象才会恢复；重复释放无效。</summary>
    IDisposable Suspend(string reason);
}

public sealed class ScannerInputSuspension : IScannerInputSuspension
{
    private readonly object _gate = new();
    private int _holders;

    public bool IsSuspended
    {
        get
        {
            lock (_gate)
            {
                return _holders > 0;
            }
        }
    }

    public event EventHandler? Changed;

    public IDisposable Suspend(string reason)
    {
        bool changed;
        lock (_gate)
        {
            _holders++;
            changed = _holders == 1;
        }

        ConsoleLog.Write("Scanner", $"input suspended reason={reason}");
        if (changed)
        {
            Raise();
        }

        return new Lease(this, reason);
    }

    private void Release(string reason)
    {
        bool changed;
        lock (_gate)
        {
            _holders--;
            changed = _holders == 0;
        }

        ConsoleLog.Write("Scanner", $"input resumed reason={reason}");
        if (changed)
        {
            Raise();
        }
    }

    private void Raise()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 订阅方出错不能让暂停卡在半路：状态已经变了，只记一条警告。
            ConsoleLog.WriteWarning("Scanner", $"input suspension handler failed error={ex.GetType().Name}", exception: ex);
        }
    }

    private sealed class Lease(ScannerInputSuspension owner, string reason) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(reason);
            }
        }
    }
}
