using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Services;

public sealed class LocalSellableItemIndex
{
    private const int CancellationCheckInterval = 256;
    private readonly object _gate = new();
    private readonly List<SellableItemDto> _items = [];
    private readonly Dictionary<ExactLookupKey, List<SellableItemDto>> _exactLookupIndex = [];
    private readonly Dictionary<ExactLookupKey, List<SellableItemDto>> _metadataLookupIndex = [];

    internal Action<string, int>? SearchProgressForTests { get; set; }
    internal Action? ExactLookupGateWaitForTests { get; set; }

    public IReadOnlyList<SellableItemDto> Items
    {
        get
        {
            lock (_gate)
            {
                return _items.ToArray();
            }
        }
    }

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

    public IReadOnlyList<SellableItemDto> GetFirst(string? storeCode, int take = 8)
    {
        if (take <= 0)
        {
            return [];
        }

        var normalizedStoreCode = Normalize(storeCode);
        lock (_gate)
        {
            return _items
                .Where(item => normalizedStoreCode.Length == 0 || Normalize(item.StoreCode) == normalizedStoreCode)
                .Take(take)
                .ToArray();
        }
    }

    public void ReplaceAll(IEnumerable<SellableItemDto> items)
    {
        var orderedItems = items
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var (exactLookupIndex, metadataLookupIndex) = BuildIndexes(orderedItems);

        lock (_gate)
        {
            ReplaceLocked(orderedItems, exactLookupIndex, metadataLookupIndex);
        }
    }

    public void Upsert(SellableItemDto item)
    {
        var normalizedStoreCode = Normalize(item.StoreCode);
        var normalizedLookupCode = Normalize(item.LookupCode);
        if (normalizedStoreCode.Length == 0 || normalizedLookupCode.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            // 单商品回写只维护受影响的索引项，避免持锁重建全量目录阻塞扫码。
            var key = new ExactLookupKey(normalizedStoreCode, normalizedLookupCode);
            if (_exactLookupIndex.TryGetValue(key, out var existingItems) && HasMultipleProducts(existingItems))
            {
                // 码冲突：服务端回查只返回胜出商品，整码替换会把收银员可选的其它商品冲掉，
                // 下次扫码就不再弹出选择；因此只替换同一商品的条目。
                RemoveProductItemsLocked(existingItems, Normalize(item.ProductCode));
            }
            else
            {
                RemoveLookupLocked(key);
            }

            InsertItemLocked(item);
        }
    }

    private static bool HasMultipleProducts(List<SellableItemDto> items)
    {
        for (var index = 1; index < items.Count; index++)
        {
            if (Normalize(items[index].ProductCode) != Normalize(items[0].ProductCode))
            {
                return true;
            }
        }

        return false;
    }

    private void RemoveProductItemsLocked(List<SellableItemDto> existingItems, string normalizedProductCode)
    {
        // RemoveItemLocked 会修改 existingItems，先拷贝再逐个移除。
        foreach (var existing in existingItems.ToArray())
        {
            if (Normalize(existing.ProductCode) == normalizedProductCode)
            {
                RemoveItemLocked(existing);
            }
        }
    }

    public bool RemoveLookup(string storeCode, string lookupCode)
    {
        var normalizedStoreCode = Normalize(storeCode);
        var normalizedLookupCode = Normalize(lookupCode);
        if (normalizedStoreCode.Length == 0 || normalizedLookupCode.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            return RemoveLookupLocked(new ExactLookupKey(normalizedStoreCode, normalizedLookupCode));
        }
    }

    private static (
        Dictionary<ExactLookupKey, List<SellableItemDto>> ExactLookupIndex,
        Dictionary<ExactLookupKey, List<SellableItemDto>> MetadataLookupIndex) BuildIndexes(
        IReadOnlyList<SellableItemDto> orderedItems)
    {
        var exactLookupIndex = new Dictionary<ExactLookupKey, List<SellableItemDto>>();
        var metadataLookupIndex = new Dictionary<ExactLookupKey, List<SellableItemDto>>();

        foreach (var item in orderedItems)
        {
            AddLookup(exactLookupIndex, item, item.LookupCode);
            AddLookup(metadataLookupIndex, item, item.Barcode);
            AddLookup(metadataLookupIndex, item, item.ItemNumber);
            AddLookup(metadataLookupIndex, item, item.ProductCode);
        }

        return (exactLookupIndex, metadataLookupIndex);
    }

    private void ReplaceLocked(
        IEnumerable<SellableItemDto> orderedItems,
        Dictionary<ExactLookupKey, List<SellableItemDto>> exactLookupIndex,
        Dictionary<ExactLookupKey, List<SellableItemDto>> metadataLookupIndex)
    {
        _items.Clear();
        _items.AddRange(orderedItems);
        _exactLookupIndex.Clear();
        foreach (var pair in exactLookupIndex)
        {
            _exactLookupIndex[pair.Key] = pair.Value;
        }

        _metadataLookupIndex.Clear();
        foreach (var pair in metadataLookupIndex)
        {
            _metadataLookupIndex[pair.Key] = pair.Value;
        }
    }

    private bool RemoveLookupLocked(ExactLookupKey key)
    {
        if (!_exactLookupIndex.TryGetValue(key, out var existingItems))
        {
            return false;
        }

        while (existingItems.Count > 0)
        {
            RemoveItemLocked(existingItems[0]);
        }

        return true;
    }

    private void RemoveItemLocked(SellableItemDto item)
    {
        for (var index = 0; index < _items.Count; index++)
        {
            if (ReferenceEquals(_items[index], item))
            {
                _items.RemoveAt(index);
                break;
            }
        }

        RemoveLookup(_exactLookupIndex, item, item.LookupCode);
        RemoveLookup(_metadataLookupIndex, item, item.Barcode);
        RemoveLookup(_metadataLookupIndex, item, item.ItemNumber);
        RemoveLookup(_metadataLookupIndex, item, item.ProductCode);
    }

    private void InsertItemLocked(SellableItemDto item)
    {
        _items.Insert(FindInsertionIndex(_items, item), item);
        AddLookup(_exactLookupIndex, item, item.LookupCode, keepSorted: true);
        AddLookup(_metadataLookupIndex, item, item.Barcode, keepSorted: true);
        AddLookup(_metadataLookupIndex, item, item.ItemNumber, keepSorted: true);
        AddLookup(_metadataLookupIndex, item, item.ProductCode, keepSorted: true);
    }

    private static int FindInsertionIndex(List<SellableItemDto> items, SellableItemDto item)
    {
        var low = 0;
        var high = items.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (StringComparer.CurrentCultureIgnoreCase.Compare(items[middle].DisplayName, item.DisplayName) <= 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    public IReadOnlyList<SellableItemDto> Search(string query, int take = 20)
    {
        return Search(null, query, CancellationToken.None, take);
    }

    public IReadOnlyList<SellableItemDto> Search(string query, CancellationToken cancellationToken, int take = 20)
    {
        return Search(null, query, cancellationToken, take);
    }

    public IReadOnlyList<SellableItemDto> Search(string? storeCode, string query, int take = 20)
    {
        return Search(storeCode, query, CancellationToken.None, take);
    }

    public Task<IReadOnlyList<SellableItemDto>> SearchAsync(
        string? storeCode,
        string query,
        CancellationToken cancellationToken,
        int take = 20) =>
        Task.Run(() => Search(storeCode, query, cancellationToken, take), cancellationToken);

    public IReadOnlyList<SellableItemDto> Search(
        string? storeCode,
        string query,
        CancellationToken cancellationToken,
        int take = 20)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query) || take <= 0)
        {
            return [];
        }

        var normalizedStoreCode = Normalize(storeCode);
        var normalized = Normalize(query);
        SellableItemDto[] snapshot;
        lock (_gate)
        {
            snapshot = new SellableItemDto[_items.Count];
            for (var index = 0; index < _items.Count; index++)
            {
                if (index % CancellationCheckInterval == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                snapshot[index] = _items[index];
            }
        }

        var matches = snapshot
            .Where((item, index) =>
            {
                if (index % CancellationCheckInterval == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SearchProgressForTests?.Invoke(query, index);
                }

                return normalizedStoreCode.Length == 0 || Normalize(item.StoreCode) == normalizedStoreCode;
            })
            .Select(item => new { Item = item, Rank = Rank(item, normalized) })
            .Where(match => match.Rank < int.MaxValue)
            .OrderBy(match => match.Rank)
            .ThenBy(match => match.Item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(take)
            .Select(match => match.Item)
            .ToList();
        cancellationToken.ThrowIfCancellationRequested();
        return matches;
    }

    public IReadOnlyList<SellableItemDto> FindExactMatches(string storeCode, string query)
    {
        var normalizedStoreCode = Normalize(storeCode);
        var normalizedQuery = Normalize(query);
        if (normalizedStoreCode.Length == 0 || normalizedQuery.Length == 0)
        {
            return [];
        }

        lock (_gate)
        {
            return _exactLookupIndex.TryGetValue(new ExactLookupKey(normalizedStoreCode, normalizedQuery), out var matches)
                ? matches.ToArray()
                : [];
        }
    }

    public Task<IReadOnlyList<SellableItemDto>> FindExactMatchesAsync(
        string storeCode,
        string query,
        CancellationToken cancellationToken) =>
        Task.Run(() => FindExactMatches(storeCode, query, cancellationToken), cancellationToken);

    private IReadOnlyList<SellableItemDto> FindExactMatches(
        string storeCode,
        string query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedStoreCode = Normalize(storeCode);
        var normalizedQuery = Normalize(query);
        if (normalizedStoreCode.Length == 0 || normalizedQuery.Length == 0)
        {
            return [];
        }

        var lockTaken = false;
        try
        {
            // 使用有限等待轮询，避免已取消的后台查找无限阻塞在索引锁上。
            while (!(lockTaken = Monitor.TryEnter(_gate, millisecondsTimeout: 50)))
            {
                ExactLookupGateWaitForTests?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
            }

            cancellationToken.ThrowIfCancellationRequested();
            var results = _exactLookupIndex.TryGetValue(new ExactLookupKey(normalizedStoreCode, normalizedQuery), out var matches)
                ? matches.ToArray()
                : [];
            cancellationToken.ThrowIfCancellationRequested();
            return results;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(_gate);
            }
        }
    }

    internal IReadOnlyList<SellableItemDto> FindMetadataExactMatches(string storeCode, string query)
    {
        var normalizedStoreCode = Normalize(storeCode);
        var normalizedQuery = Normalize(query);
        if (normalizedStoreCode.Length == 0 || normalizedQuery.Length == 0)
        {
            return [];
        }

        lock (_gate)
        {
            return _metadataLookupIndex.TryGetValue(new ExactLookupKey(normalizedStoreCode, normalizedQuery), out var matches)
                ? matches.ToArray()
                : [];
        }
    }

    private static void AddLookup(
        Dictionary<ExactLookupKey, List<SellableItemDto>> index,
        SellableItemDto item,
        string? lookupCode,
        bool keepSorted = false)
    {
        var normalizedLookupCode = Normalize(lookupCode);
        if (normalizedLookupCode.Length == 0)
        {
            return;
        }

        var key = new ExactLookupKey(Normalize(item.StoreCode), normalizedLookupCode);
        if (!index.TryGetValue(key, out var matches))
        {
            matches = [];
            index[key] = matches;
        }

        if (!matches.Contains(item))
        {
            if (keepSorted)
            {
                matches.Insert(FindInsertionIndex(matches, item), item);
            }
            else
            {
                matches.Add(item);
            }
        }
    }

    private static void RemoveLookup(
        Dictionary<ExactLookupKey, List<SellableItemDto>> index,
        SellableItemDto item,
        string? lookupCode)
    {
        var normalizedLookupCode = Normalize(lookupCode);
        if (normalizedLookupCode.Length == 0)
        {
            return;
        }

        var key = new ExactLookupKey(Normalize(item.StoreCode), normalizedLookupCode);
        if (!index.TryGetValue(key, out var matches))
        {
            return;
        }

        matches.Remove(item);
        if (matches.Count == 0)
        {
            index.Remove(key);
        }
    }

    private static int Rank(SellableItemDto item, string query)
    {
        if (EqualsNormalized(item.Barcode, query) || EqualsNormalized(item.LookupCode, query))
        {
            return 0;
        }

        if (EqualsNormalized(item.ItemNumber, query) || EqualsNormalized(item.ProductCode, query))
        {
            return 1;
        }

        if (ContainsNormalized(item.DisplayName, query))
        {
            return 2;
        }

        if (ContainsNormalized(item.LookupCode, query) || ContainsNormalized(item.ReferenceCode, query))
        {
            return 3;
        }

        return int.MaxValue;
    }

    private static bool EqualsNormalized(string? value, string query)
    {
        return Normalize(value) == query;
    }

    private static bool ContainsNormalized(string? value, string query)
    {
        return Normalize(value).Contains(query, StringComparison.Ordinal);
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim().ToUpperInvariant();
    }

    private sealed record ExactLookupKey(string StoreCode, string LookupCode);
}
