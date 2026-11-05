using System.Collections.Concurrent;

namespace Hbpos.Client.Wpf.Services.Zeller;

/// <summary>
/// 记录本进程里正在处理哪些 Zeller 尝试。扣款客户端、迟到复核、恢复服务、主管结案都要先登记，
/// 同一条尝试同一时刻只有一方在改，避免恢复中心在扣款还在轮询时把记录改掉。
/// </summary>
/// <remarks>
/// 中文注释：整个程序只能有一个实例（依赖注入里注册成单例），扣款客户端和恢复服务共用它。
/// 只管本进程；收银端同一台电脑只运行一个实例。
/// </remarks>
public sealed class ZellerAttemptActivity
{
    private readonly ConcurrentDictionary<Guid, byte> _active = new();

    public bool IsActive(Guid attemptGuid) => _active.ContainsKey(attemptGuid);

    /// <summary>登记一条尝试。已经有别人在处理时返回 null。释放返回的对象即解除登记。</summary>
    public IDisposable? TryEnter(Guid attemptGuid) =>
        _active.TryAdd(attemptGuid, 0) ? new Lease(this, attemptGuid) : null;

    private sealed class Lease(ZellerAttemptActivity owner, Guid attemptGuid) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner._active.TryRemove(attemptGuid, out _);
            }
        }
    }
}
