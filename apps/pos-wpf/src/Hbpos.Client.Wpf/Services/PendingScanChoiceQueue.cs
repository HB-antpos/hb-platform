using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 「一码多商品」待选队列：先进先出，每次扫码一个条目，互不覆盖、互不合并。
/// 纯内存、线程安全、无 UI 依赖。队列自己不决定何时入队 / 清空，由收银工作流服务按业务规则调用；
/// 状态变化通过 <see cref="Changed"/> 通知（在锁外触发），界面据此刷新。
/// </summary>
public sealed class PendingScanChoiceQueue
{
    /// <summary>队列上限。满了拒收新的多商品扫码并明确报警，不静默丢（正常一单只会有个位数）。</summary>
    public const int Capacity = 20;

    private readonly object _gate = new();
    private readonly List<PendingScanChoice> _items = [];
    private readonly TimeProvider _timeProvider;
    private long _nextId;

    public PendingScanChoiceQueue(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>队列内容变化（入队 / 出队 / 清空）。在调用线程、锁外触发，订阅方自行切回 UI 线程。</summary>
    public event EventHandler? Changed;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>队首（最早的待选），队列为空时为 null。</summary>
    public PendingScanChoice? Current
    {
        get
        {
            lock (_gate)
            {
                return _items.Count == 0 ? null : _items[0];
            }
        }
    }

    public IReadOnlyList<PendingScanChoice> Snapshot()
    {
        lock (_gate)
        {
            return _items.ToArray();
        }
    }

    /// <summary>入队；队列已满返回 null（调用方须提示收银员，这次扫码没有被记录）。</summary>
    public PendingScanChoice? TryEnqueue(
        string barcode,
        string source,
        string? traceId,
        string storeCode,
        IReadOnlyList<SellableItemDto> candidates)
    {
        PendingScanChoice entry;
        lock (_gate)
        {
            if (_items.Count >= Capacity)
            {
                return null;
            }

            var normalized = Normalize(barcode);
            var sameAsPrevious = _items.Count > 0 &&
                string.Equals(Normalize(_items[^1].Barcode), normalized, StringComparison.OrdinalIgnoreCase);
            entry = new PendingScanChoice(
                ++_nextId,
                barcode,
                source,
                traceId,
                storeCode,
                _timeProvider.GetLocalNow(),
                candidates.ToArray(),
                sameAsPrevious);
            _items.Add(entry);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return entry;
    }

    public PendingScanChoice? TryGet(long id)
    {
        lock (_gate)
        {
            return _items.FirstOrDefault(item => item.Id == id);
        }
    }

    /// <summary>移除指定条目（选定入车 / 放弃）；条目已不存在返回 false。</summary>
    public bool Remove(long id)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(item => item.Id == id);
            if (index < 0)
            {
                return false;
            }

            _items.RemoveAt(index);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>清空并返回被清掉的条目（用于记日志）。</summary>
    public IReadOnlyList<PendingScanChoice> Clear()
    {
        PendingScanChoice[] removed;
        lock (_gate)
        {
            if (_items.Count == 0)
            {
                return [];
            }

            removed = _items.ToArray();
            _items.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    private static string Normalize(string barcode)
    {
        return barcode.Trim();
    }
}
