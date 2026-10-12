using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Services;

public sealed class LocalSellableItemIndex
{
    private const int CancellationCheckInterval = 256;
    private const int MaxPartialSelectTake = 64;
    private readonly object _gate = new();

    // 顺序约定：_items 按"到达顺序"保存（ReplaceAll 传入顺序，Upsert 追加在末尾），不再做全局文化排序。
    // 对外可见的"全局顺序"定义为：对 _items 按 DisplayName（CurrentCultureIgnoreCase）做稳定排序，
    // 即名称相同的商品保持到达顺序，与旧实现（OrderBy 稳定排序 + Upsert 插在相等项之后）完全等价。
    // GetFirst / Search / Items 都按这一定义在读取时选择或排序，避免启动时对几十万行做文化排序。
    // 下面三个字段在 ReplaceAll 时整体换引用（锁内只换引用），Upsert/Remove 才在锁内原地修改。
    private List<SellableItemDto> _items = [];
    private Dictionary<ExactLookupKey, List<SellableItemDto>> _exactLookupIndex = [];
    private Dictionary<ExactLookupKey, List<SellableItemDto>> _metadataLookupIndex = [];

    internal Action<string, int>? SearchProgressForTests { get; set; }
    internal Action? ExactLookupGateWaitForTests { get; set; }
    // 测试钩子：GetFirst 已释放锁、尚未开始选择时调用，用于确定性验证选择期间不占锁。
    internal Action? GetFirstSelectionStartedForTests { get; set; }

    public IReadOnlyList<SellableItemDto> Items
    {
        get
        {
            SellableItemDto[] snapshot;
            lock (_gate)
            {
                snapshot = _items.ToArray();
            }

            // 排序放在锁外：Items 只给兜底路径和测试用，按全局顺序定义做稳定排序。
            return snapshot
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
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
        // 锁内只拷贝引用数组（35 万条约 1ms）；门店过滤和文化比较都放到锁外，
        // 避免扫码线程的 FindExactMatches 在选择期间等同一把锁。
        SellableItemDto[] snapshot;
        lock (_gate)
        {
            snapshot = _items.ToArray();
        }

        GetFirstSelectionStartedForTests?.Invoke();

        var filtered = snapshot.Where(item =>
            normalizedStoreCode.Length == 0 || Normalize(item.StoreCode) == normalizedStoreCode);
        if (take > MaxPartialSelectTake)
        {
            // take 很大时插入选择不划算，直接稳定排序后截取。
            return filtered
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Take(take)
                .ToArray();
        }

        // 部分选择：保持一个按名称有序的前 take 名列表，O(N) 扫描一遍即可；
        // 相等名称视为"后到者排后"，等价于对 _items 做稳定排序后取前 take 个。
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        var top = new List<SellableItemDto>(take + 1);
        foreach (var item in filtered)
        {
            if (top.Count == take && comparer.Compare(item.DisplayName, top[^1].DisplayName) >= 0)
            {
                continue;
            }

            var position = top.Count;
            while (position > 0 && comparer.Compare(top[position - 1].DisplayName, item.DisplayName) > 0)
            {
                position--;
            }

            top.Insert(position, item);
            if (top.Count > take)
            {
                top.RemoveAt(top.Count - 1);
            }
        }

        return top.ToArray();
    }

    public void ReplaceAll(IEnumerable<SellableItemDto> items)
    {
        // 全部在锁外完成：物化、建索引、对同码多商品的桶排序；锁内只交换三个引用，
        // 扫码线程（FindExactMatches 要拿同一把锁）因此不会被整表重建阻塞。
        var arrivalItems = items as List<SellableItemDto> ?? items.ToList();
        var (exactLookupIndex, metadataLookupIndex) = BuildIndexes(arrivalItems);
        SortMultiItemBuckets(exactLookupIndex);
        SortMultiItemBuckets(metadataLookupIndex);
        // 防止调用方之后修改传入列表而影响索引，拷贝一份（仅数组拷贝，毫秒级）。
        var snapshotItems = new List<SellableItemDto>(arrivalItems);

        lock (_gate)
        {
            ReplaceLocked(snapshotItems, exactLookupIndex, metadataLookupIndex);
        }
    }

    // 桶内顺序必须与"全局稳定排序"一致：OrderBy 是稳定排序，桶按到达顺序建好后稳定排序即等价。
    // 绝大多数桶只有 1 项，只处理 Count>1 的桶。
    private static void SortMultiItemBuckets(Dictionary<ExactLookupKey, List<SellableItemDto>> index)
    {
        foreach (var bucket in index.Values)
        {
            if (bucket.Count < 2)
            {
                continue;
            }

            var sorted = bucket
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            bucket.Clear();
            bucket.AddRange(sorted);
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
        IReadOnlyList<SellableItemDto> arrivalItems)
    {
        var exactLookupIndex = new Dictionary<ExactLookupKey, List<SellableItemDto>>();
        var metadataLookupIndex = new Dictionary<ExactLookupKey, List<SellableItemDto>>();

        foreach (var item in arrivalItems)
        {
            AddLookup(exactLookupIndex, item, item.LookupCode);
            AddLookup(metadataLookupIndex, item, item.Barcode);
            AddLookup(metadataLookupIndex, item, item.ItemNumber);
            AddLookup(metadataLookupIndex, item, item.ProductCode);
        }

        return (exactLookupIndex, metadataLookupIndex);
    }

    private void ReplaceLocked(
        List<SellableItemDto> arrivalItems,
        Dictionary<ExactLookupKey, List<SellableItemDto>> exactLookupIndex,
        Dictionary<ExactLookupKey, List<SellableItemDto>> metadataLookupIndex)
    {
        // 只换引用：新对象在锁外已构建完成且之后只经由本类的锁访问，读取方要么看到旧快照要么看到新快照。
        _items = arrivalItems;
        _exactLookupIndex = exactLookupIndex;
        _metadataLookupIndex = metadataLookupIndex;
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
        // _items 按到达顺序保存，追加即可；桶内仍需保持名称有序（相等项排在后面）。
        _items.Add(item);
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
