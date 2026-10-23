using System.Text.Json;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.React.SupplierPreorders;

/// <summary>
/// 去年历史快照的计算与读取（方案第 5 节）。
/// - 进货：总仓配货（AllocQuantity，排除草稿，日期取出库 → 订单 → 创建）+ 分店供应商进货单（日期取入库 → 订单 → 创建，
///   明细缺商品编码时按零售价 UUID 回查），与 #882 批量货号进货量、#886 商品抽屉进货明细同一口径；
/// - 销量：ProductStoreDailySalesStatistic（复用 BatchProductSalesStatisticReader）；
/// - 折扣：读调度器按 batch-product-sales-discount-v3 规则发布的日快照（复用 BatchProductSalesDiscountSnapshotReader），
///   日快照缺失或对不上日统计的日子记为「未判定」，不猜；
/// - 窗口：订单表的 HistoryFrom / HistoryTo（为空按节日默认）。
/// 订单表规模几千款，按商品分批读取；一次计算在后台任务里跑，列表接口只读快照。
/// </summary>
public sealed class SupplierPreorderHistoryService
{
    /// <summary>每批读取的商品数：日快照按商品 × 天展开，100 个商品 × 140 天 × 十几家店在内存里可控。</summary>
    private const int SalesProductBatchSize = 100;
    private const int SqlInBatchSize = 500;
    /// <summary>计算中超过这么久还没结束（进程重启、崩溃）视为卡住，重新计算。</summary>
    internal static readonly TimeSpan RunningStaleAfter = TimeSpan.FromHours(2);
    /// <summary>每天悉尼时间这个钟点之后，进行中的订单表重算一次（截止前每晚刷新）。</summary>
    internal const int NightlyHourSydney = 3;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 同供应商候选（含窗口内销量 / 进货）的进程内缓存：KFC 约 4,900 个商品，按 500 个一批合计销量与进货，
    /// 生产库服务器端约十几秒，不能每次打开「选择参考商品」弹窗都现算。后台整表计算时会顺手刷新。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Supplier, DateTime From, DateTime To), (DateTime ExpiresUtc, List<SupplierPreorderRefCandidate> Candidates)> CandidateCache = new();
    internal static readonly TimeSpan CandidateCacheTtl = TimeSpan.FromMinutes(30);
    private const int CandidateCacheMaxEntries = 20;

    private readonly ISqlSugarClient _db;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    public SupplierPreorderHistoryService(ISqlSugarClient db, ILogger logger, TimeProvider? timeProvider = null)
    {
        _db = db;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private DateTime NowUtc => _timeProvider.GetUtcNow().UtcDateTime;

    // ---------------- 表头行（计算状态） ----------------

    /// <summary>表头行的内容：计算状态、窗口与计数。存在 StoreCode = ProductCode = "*" 的那一行。</summary>
    public sealed class HistoryMeta
    {
        /// <summary>Queued / Running / Ready / Failed / NoWindow。</summary>
        public string Status { get; set; } = "Queued";
        public DateTime? RequestedAtUtc { get; set; }
        public DateTime? LastAttemptUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public string? Error { get; set; }
        public DateTime? WindowFrom { get; set; }
        public DateTime? WindowTo { get; set; }
        public int ProductCount { get; set; }
        public int RowCount { get; set; }
        public string? RuleVersion { get; set; }
    }

    public async Task<HistoryMeta?> ReadMetaAsync(string formGuid)
    {
        var row = await _db.Queryable<SupplierPreorderHistorySnapshot>()
            .FirstAsync(s => s.FormGuid == formGuid && s.StoreCode == SupplierPreorderHistorySnapshot.FormMetaKey && s.ProductCode == SupplierPreorderHistorySnapshot.FormMetaKey);
        return row is null ? null : DeserializeMeta(row.PayloadJson);
    }

    private static HistoryMeta? DeserializeMeta(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<HistoryMeta>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task WriteMetaAsync(string formGuid, HistoryMeta meta)
    {
        var json = JsonSerializer.Serialize(meta, JsonOptions);
        var now = NowUtc;
        var existing = await _db.Queryable<SupplierPreorderHistorySnapshot>()
            .FirstAsync(s => s.FormGuid == formGuid && s.StoreCode == SupplierPreorderHistorySnapshot.FormMetaKey && s.ProductCode == SupplierPreorderHistorySnapshot.FormMetaKey);
        if (existing is not null)
        {
            existing.PayloadJson = json;
            existing.ComputedAtUtc = meta.CompletedAtUtc ?? existing.ComputedAtUtc;
            existing.RuleVersion = meta.RuleVersion ?? existing.RuleVersion;
            await _db.Updateable(existing).UpdateColumns(s => new { s.PayloadJson, s.ComputedAtUtc, s.RuleVersion }).ExecuteCommandAsync();
            return;
        }

        try
        {
            await _db.Insertable(new SupplierPreorderHistorySnapshot
            {
                FormGuid = formGuid,
                StoreCode = SupplierPreorderHistorySnapshot.FormMetaKey,
                ProductCode = SupplierPreorderHistorySnapshot.FormMetaKey,
                PayloadJson = json,
                ComputedAtUtc = meta.CompletedAtUtc ?? now,
                RuleVersion = meta.RuleVersion ?? string.Empty,
            }).ExecuteCommandAsync();
        }
        catch (Exception)
        {
            // 并发下另一处刚插入了表头行（撞唯一索引）：改为更新；不是这个原因就原样抛出
            var inserted = await _db.Queryable<SupplierPreorderHistorySnapshot>()
                .AnyAsync(s => s.FormGuid == formGuid && s.StoreCode == SupplierPreorderHistorySnapshot.FormMetaKey && s.ProductCode == SupplierPreorderHistorySnapshot.FormMetaKey);
            if (!inserted)
            {
                throw;
            }

            await WriteMetaAsync(formGuid, meta);
        }
    }

    /// <summary>排队重算（导入建表、发布、改窗口、手动「重新计算」）。保留上次完成时间，列表在新结果出来前继续显示旧快照。</summary>
    public async Task RequestAsync(string formGuid)
    {
        var meta = await ReadMetaAsync(formGuid) ?? new HistoryMeta();
        meta.Status = "Queued";
        meta.RequestedAtUtc = NowUtc;
        meta.Error = null;
        await WriteMetaAsync(formGuid, meta);
    }

    /// <summary>
    /// 需要计算的订单表：排队中的；计算中但卡住超过 2 小时的；还没算过的草稿 / 进行中订单表；
    /// 进行中、未过截止、今天悉尼 3 点之后还没尝试过（或口径版本变了）的（每晚刷新）。
    /// </summary>
    public static bool IsDue(SupplierPreorderForm form, HistoryMeta? meta, DateTime nowUtc, DateTime nightlyCutoffUtc)
    {
        if (form.IsDeleted)
        {
            return false;
        }

        if (meta is null)
        {
            return form.Status is SupplierPreorderFormStatuses.Draft or SupplierPreorderFormStatuses.Open;
        }

        switch (meta.Status)
        {
            case "Queued":
                return true;
            case "Running":
                return meta.LastAttemptUtc is null || nowUtc - meta.LastAttemptUtc.Value > RunningStaleAfter;
        }

        var beforeDeadline = form.DeadlineAtUtc is null || nowUtc < DateTime.SpecifyKind(form.DeadlineAtUtc.Value, DateTimeKind.Utc);
        if (form.Status != SupplierPreorderFormStatuses.Open || !beforeDeadline)
        {
            return false;
        }

        var attemptedSinceCutoff = meta.LastAttemptUtc is not null && meta.LastAttemptUtc.Value >= nightlyCutoffUtc;
        return !attemptedSinceCutoff
               || (meta.Status == "Ready" && !string.Equals(meta.RuleVersion, SupplierPreorderHistoryRules.RuleVersion, StringComparison.Ordinal));
    }

    /// <summary>最近一次（≤ now）悉尼时间 03:00 对应的 UTC 时刻。</summary>
    public static DateTime NightlyCutoffUtc(DateTime nowUtc)
    {
        var zone = ResolveSydney();
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone);
        var cutoffLocal = local.Date.AddHours(NightlyHourSydney);
        if (local < cutoffLocal)
        {
            cutoffLocal = cutoffLocal.AddDays(-1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(cutoffLocal, DateTimeKind.Unspecified), zone);
    }

    private static TimeZoneInfo ResolveSydney()
    {
        foreach (var id in new[] { "Australia/Sydney", "AUS Eastern Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("AEST", TimeSpan.FromHours(10), "AEST", "AEST");
    }

    /// <summary>后台任务入口：逐张计算到期的订单表，返回算了几张。</summary>
    public async Task<int> RunDueAsync(CancellationToken cancellationToken)
    {
        if (!_db.DbMaintenance.IsAnyTable("SupplierPreorderHistorySnapshot", false)
            || !_db.DbMaintenance.IsAnyTable("SupplierPreorderForm", false))
        {
            return 0;
        }

        var now = NowUtc;
        var cutoff = NightlyCutoffUtc(now);
        var forms = await _db.Queryable<SupplierPreorderForm>().Where(f => !f.IsDeleted).ToListAsync();
        var metaRows = await _db.Queryable<SupplierPreorderHistorySnapshot>()
            .Where(s => s.StoreCode == SupplierPreorderHistorySnapshot.FormMetaKey && s.ProductCode == SupplierPreorderHistorySnapshot.FormMetaKey)
            .ToListAsync();
        var metas = metaRows.ToDictionary(r => r.FormGuid, r => DeserializeMeta(r.PayloadJson), StringComparer.OrdinalIgnoreCase);
        var due = forms
            .Where(f => IsDue(f, metas.GetValueOrDefault(f.FormGuid), now, cutoff))
            // 手动排队的优先，其次是新建的
            .OrderByDescending(f => metas.GetValueOrDefault(f.FormGuid)?.Status == "Queued")
            .ThenByDescending(f => f.CreatedAtUtc)
            .ToList();
        var count = 0;
        foreach (var form in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ComputeFormAsync(form.FormGuid, cancellationToken);
            count++;
        }

        return count;
    }

    // ---------------- 整张订单表计算 ----------------

    public async Task ComputeFormAsync(string formGuid, CancellationToken cancellationToken)
    {
        var form = await _db.Queryable<SupplierPreorderForm>().FirstAsync(f => f.FormGuid == formGuid && !f.IsDeleted);
        if (form is null)
        {
            return;
        }

        var meta = await ReadMetaAsync(formGuid) ?? new HistoryMeta();
        var requestedAt = meta.RequestedAtUtc;
        var startedAt = NowUtc;
        meta.Status = "Running";
        meta.LastAttemptUtc = startedAt;
        meta.RuleVersion = SupplierPreorderHistoryRules.RuleVersion;
        await WriteMetaAsync(formGuid, meta);

        try
        {
            var window = SupplierPreorderHistoryRules.ResolveWindow(form.HistoryFrom, form.HistoryTo, form.HolidayType, form.SeasonYear);
            if (window is null)
            {
                await ReplaceRowsAsync(formGuid, null, new List<SupplierPreorderHistorySnapshot>());
                await FinishAsync(formGuid, requestedAt, "NoWindow", null, null, 0, 0);
                return;
            }

            var (from, to) = window.Value;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var items = await _db.Queryable<SupplierPreorderFormItem>().Where(i => i.FormGuid == formGuid && i.IsActive).ToListAsync();
            var stores = await LoadStoreCodesAsync();

            // 1) 本品：先读销量，判定哪些商品「自己在窗口内没有销量」
            var ownCodes = items.Select(i => i.MatchedProductCode).Where(c => !string.IsNullOrWhiteSpace(c)).Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var sales = await ReadSalesAsync(ownCodes, from, to, stores, cancellationToken);
            // 与 ResolveSource 同一判定：窗口内全部分店净销量 > 0
            var ownWithSales = sales.GroupBy(s => s.ProductCode, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Sum(s => s.Quantity) > 0)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 2) 系统推荐参考商品（人工指定 / 人工清除的不动）
            await RecommendRefsAsync(form, items, ownCodes, ownWithSales, from, to, cancellationToken);

            // 3) 参考商品的销量 + 本品与参考的进货
            var refCodes = items.Select(i => i.RefProductCode).Where(c => !string.IsNullOrWhiteSpace(c)).Cast<string>()
                .Where(c => !ownCodes.Contains(c, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            sales.AddRange(await ReadSalesAsync(refCodes, from, to, stores, cancellationToken));
            var allCodes = ownCodes.Concat(refCodes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var purchases = await ReadPurchasesAsync(allCodes, from, to, stores, cancellationToken);

            var rows = BuildSnapshotRows(formGuid, from, to, sales, purchases, startedAt);
            await ReplaceRowsAsync(formGuid, null, rows);
            await FinishAsync(formGuid, requestedAt, "Ready", from, to, allCodes.Count, rows.Count);
            _logger.LogInformation(
                "供应商预订单去年历史已计算：{FormGuid}，窗口 {From:yyyy-MM-dd}~{To:yyyy-MM-dd}，商品 {Products} 个，快照 {Rows} 行，耗时 {ElapsedMs}ms",
                formGuid, from, to, allCodes.Count, rows.Count, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 停机：回到排队，下次启动接着算
            var current = await ReadMetaAsync(formGuid) ?? new HistoryMeta();
            current.Status = "Queued";
            await WriteMetaAsync(formGuid, current);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "供应商预订单去年历史计算失败：{FormGuid}", formGuid);
            var current = await ReadMetaAsync(formGuid) ?? new HistoryMeta();
            current.Status = RequestedSince(current, requestedAt) ? "Queued" : "Failed";
            current.Error = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
            await WriteMetaAsync(formGuid, current);
        }
    }

    /// <summary>计算开始之后是否又有人排队。</summary>
    private static bool RequestedSince(HistoryMeta current, DateTime? requestedAtWhenStarted) =>
        current.RequestedAtUtc.HasValue && (!requestedAtWhenStarted.HasValue || current.RequestedAtUtc.Value > requestedAtWhenStarted.Value);

    private async Task FinishAsync(string formGuid, DateTime? requestedAt, string status, DateTime? from, DateTime? to, int productCount, int rowCount)
    {
        var current = await ReadMetaAsync(formGuid) ?? new HistoryMeta();
        // 计算期间又有人排队（例如改了窗口）：结果先用上，但保持排队，马上再算一遍
        current.Status = RequestedSince(current, requestedAt) ? "Queued" : status;
        current.CompletedAtUtc = NowUtc;
        current.Error = null;
        current.WindowFrom = from;
        current.WindowTo = to;
        current.ProductCount = productCount;
        current.RowCount = rowCount;
        current.RuleVersion = SupplierPreorderHistoryRules.RuleVersion;
        await WriteMetaAsync(formGuid, current);
    }

    /// <summary>
    /// 人工指定参考商品后，只补算这一个商品的快照（几十毫秒），不必等整表重算。
    /// 窗口必须用现有快照算时的窗口（表头行里记的），否则日销下标会和别的行对不上。
    /// </summary>
    public async Task RefreshProductAsync(string formGuid, DateTime from, DateTime to, string productCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(productCode))
        {
            return;
        }

        var codes = new List<string> { productCode.Trim() };
        var stores = await LoadStoreCodesAsync();
        var sales = await ReadSalesAsync(codes, from, to, stores, cancellationToken);
        var purchases = await ReadPurchasesAsync(codes, from, to, stores, cancellationToken);
        var rows = BuildSnapshotRows(formGuid, from, to, sales, purchases, NowUtc);
        await ReplaceRowsAsync(formGuid, codes[0], rows);
    }

    /// <summary>全部分店代码（含停用分店；对比表里照样显示）。</summary>
    public Task<List<string>> GetStoreCodesAsync() => LoadStoreCodesAsync();

    /// <summary>按分店与「全部分店（StoreCode = *）」各生成一份快照行。</summary>
    internal static List<SupplierPreorderHistorySnapshot> BuildSnapshotRows(
        string formGuid,
        DateTime from,
        DateTime to,
        IReadOnlyList<SupplierPreorderSalesFact> sales,
        IReadOnlyList<SupplierPreorderPurchaseFact> purchases,
        DateTime computedAtUtc)
    {
        var perStore = SupplierPreorderHistoryRules.Build(from, to, sales, purchases);
        var allStores = SupplierPreorderHistoryRules.Build(
            from,
            to,
            sales.Select(s => s with { StoreCode = SupplierPreorderHistorySnapshot.FormMetaKey }),
            purchases.Select(p => p with { StoreCode = SupplierPreorderHistorySnapshot.FormMetaKey }));
        var rows = new List<SupplierPreorderHistorySnapshot>(perStore.Count + allStores.Count);
        foreach (var (key, payload) in perStore.Concat(allStores))
        {
            if (key.StoreCode == SupplierPreorderHistorySnapshot.FormMetaKey)
            {
                // 全部分店合计只用于判定「本品有没有历史」和管理端显示销量，不存逐批进货明细
                payload.Batches = new();
            }

            rows.Add(new SupplierPreorderHistorySnapshot
            {
                FormGuid = formGuid,
                StoreCode = key.StoreCode,
                ProductCode = key.ProductCode,
                PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
                ComputedAtUtc = computedAtUtc,
                RuleVersion = SupplierPreorderHistoryRules.RuleVersion,
            });
        }

        return rows;
    }

    /// <summary>事务内替换：productCode 为空时替换整张订单表（保留表头行），否则只替换这一个商品。</summary>
    private async Task ReplaceRowsAsync(string formGuid, string? productCode, List<SupplierPreorderHistorySnapshot> rows)
    {
        var result = await _db.Ado.UseTranAsync(async () =>
        {
            if (productCode is null)
            {
                await _db.Deleteable<SupplierPreorderHistorySnapshot>()
                    .Where(s => s.FormGuid == formGuid
                                && (s.StoreCode != SupplierPreorderHistorySnapshot.FormMetaKey || s.ProductCode != SupplierPreorderHistorySnapshot.FormMetaKey))
                    .ExecuteCommandAsync();
            }
            else
            {
                await _db.Deleteable<SupplierPreorderHistorySnapshot>()
                    .Where(s => s.FormGuid == formGuid && s.ProductCode == productCode)
                    .ExecuteCommandAsync();
            }

            foreach (var chunk in rows.Chunk(200))
            {
                await _db.Insertable(chunk.ToList()).ExecuteCommandAsync();
            }
        });
        if (!result.IsSuccess)
        {
            throw result.ErrorException ?? new InvalidOperationException("写入去年历史快照失败");
        }
    }

    // ---------------- 读快照 ----------------

    /// <summary>读一张订单表某几家店（含 "*" 全部分店）的快照，按（分店, 商品）索引；表头行不返回。</summary>
    public async Task<Dictionary<(string StoreCode, string ProductCode), SupplierPreorderHistoryPayload>> LoadPayloadsAsync(
        string formGuid,
        IReadOnlyCollection<string> storeCodes,
        IReadOnlyCollection<string>? productCodes = null)
    {
        var result = new Dictionary<(string StoreCode, string ProductCode), SupplierPreorderHistoryPayload>(new SupplierPreorderHistoryRules.StoreProductComparer());
        if (storeCodes.Count == 0 || productCodes is { Count: 0 })
        {
            return result;
        }

        var storeList = storeCodes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var rows = new List<SupplierPreorderHistorySnapshot>();
        if (productCodes is null)
        {
            rows = await _db.Queryable<SupplierPreorderHistorySnapshot>()
                .Where(s => s.FormGuid == formGuid && storeList.Contains(s.StoreCode) && s.ProductCode != SupplierPreorderHistorySnapshot.FormMetaKey)
                .ToListAsync();
        }
        else
        {
            foreach (var chunk in productCodes.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(SqlInBatchSize))
            {
                var list = chunk.ToList();
                rows.AddRange(await _db.Queryable<SupplierPreorderHistorySnapshot>()
                    .Where(s => s.FormGuid == formGuid && storeList.Contains(s.StoreCode) && list.Contains(s.ProductCode))
                    .ToListAsync());
            }
        }

        foreach (var row in rows)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<SupplierPreorderHistoryPayload>(row.PayloadJson, JsonOptions);
                if (payload is not null)
                {
                    result[(row.StoreCode.Trim(), row.ProductCode.Trim())] = payload;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "去年历史快照解析失败：{FormGuid} {StoreCode} {ProductCode}", formGuid, row.StoreCode, row.ProductCode);
            }
        }

        return result;
    }

    // ---------------- 参考商品 ----------------

    private async Task RecommendRefsAsync(
        SupplierPreorderForm form,
        List<SupplierPreorderFormItem> items,
        List<string> ownCodes,
        HashSet<string> ownWithSales,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        var now = NowUtc;
        var changed = new List<SupplierPreorderFormItem>();
        var needing = new List<SupplierPreorderFormItem>();
        foreach (var item in items)
        {
            if (item.RefSource is SupplierPreorderRefSources.Manual or SupplierPreorderRefSources.Cleared)
            {
                continue;
            }

            if (SupplierPreorderRefDecisions.NeedsReference(item.MatchedProductCode, ownWithSales))
            {
                needing.Add(item);
            }
            else if (item.RefProductCode is not null || item.RefSource is not null)
            {
                // 本品自己有了销量：系统推荐的参考撤掉
                SupplierPreorderRefDecisions.ClearAuto(item);
                changed.Add(item);
            }
        }

        if (needing.Count > 0)
        {
            var candidates = await LoadSupplierCandidatesAsync(form.LocalSupplierCode, from, to, cancellationToken);
            PutCandidateCache(form.LocalSupplierCode, from, to, candidates);
            var exclude = ownCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var index = new SupplierPreorderRefMatcher.CandidateIndex(candidates.Where(c => !exclude.Contains(c.ProductCode)));
            foreach (var item in needing)
            {
                var pick = index.Recommend(item.Name, item.Price);
                if (SupplierPreorderRefDecisions.ApplyAuto(item, pick, now))
                {
                    changed.Add(item);
                }
            }
        }

        foreach (var chunk in changed.Chunk(200))
        {
            await _db.Updateable(chunk.ToList())
                .UpdateColumns(i => new { i.RefProductCode, i.RefSource, i.RefScore, i.RefSetBy, i.RefSetAtUtc })
                .ExecuteCommandAsync();
        }
    }

    /// <summary>同一本地供应商、历史窗口内（全部分店）有销量或进货的我方商品。</summary>
    public async Task<List<SupplierPreorderRefCandidate>> LoadSupplierCandidatesAsync(string localSupplierCode, DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        var products = await _db.Queryable<Product>()
            .Where(p => p.LocalSupplierCode == localSupplierCode && !p.IsDeleted && p.ProductCode != null)
            .Select(p => new SupplierPreorderRefCandidateRow
            {
                ProductCode = p.ProductCode,
                ItemNumber = p.ItemNumber,
                Barcode = p.Barcode,
                ProductName = p.ProductName,
                PurchasePrice = p.PurchasePrice,
                LocalSupplierCode = p.LocalSupplierCode,
                ProductImage = p.ProductImage,
            })
            .ToListAsync();
        return await AttachActivityAsync(products, from, to, requireActivity: true, cancellationToken);
    }

    /// <summary>弹窗用：先查缓存，没有或过期再读库（读完放进缓存）。</summary>
    public async Task<List<SupplierPreorderRefCandidate>> GetSupplierCandidatesCachedAsync(string localSupplierCode, DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        var key = (localSupplierCode.Trim().ToUpperInvariant(), from.Date, to.Date);
        if (CandidateCache.TryGetValue(key, out var cached) && cached.ExpiresUtc > NowUtc)
        {
            return cached.Candidates;
        }

        var candidates = await LoadSupplierCandidatesAsync(localSupplierCode, from, to, cancellationToken);
        PutCandidateCache(localSupplierCode, from, to, candidates);
        return candidates;
    }

    private void PutCandidateCache(string localSupplierCode, DateTime from, DateTime to, List<SupplierPreorderRefCandidate> candidates)
    {
        var now = NowUtc;
        // 超过上限时先清掉过期的，仍然超就整体清空（订单表一年就几张，正常碰不到）
        if (CandidateCache.Count >= CandidateCacheMaxEntries)
        {
            foreach (var expired in CandidateCache.Where(e => e.Value.ExpiresUtc <= now).Select(e => e.Key).ToList())
            {
                CandidateCache.TryRemove(expired, out _);
            }

            if (CandidateCache.Count >= CandidateCacheMaxEntries)
            {
                CandidateCache.Clear();
            }
        }

        CandidateCache[(localSupplierCode.Trim().ToUpperInvariant(), from.Date, to.Date)] = (now + CandidateCacheTtl, candidates);
    }

    /// <summary>手工搜索：按货号 / 条码 / 名称（不限供应商），最多 limit 个，并带上窗口内销量。</summary>
    public async Task<List<SupplierPreorderRefCandidate>> SearchProductsAsync(string keyword, DateTime? from, DateTime? to, int limit, CancellationToken cancellationToken = default)
    {
        var k = keyword.Trim();
        if (k.Length == 0)
        {
            return new();
        }

        var products = await _db.Queryable<Product>()
            .Where(p => !p.IsDeleted && p.ProductCode != null
                        && (p.ItemNumber == k || p.Barcode == k || p.ProductCode == k
                            || (p.ItemNumber != null && p.ItemNumber.Contains(k))
                            || p.ProductName.Contains(k)))
            .OrderBy(p => p.ItemNumber)
            .Take(limit)
            .Select(p => new SupplierPreorderRefCandidateRow
            {
                ProductCode = p.ProductCode,
                ItemNumber = p.ItemNumber,
                Barcode = p.Barcode,
                ProductName = p.ProductName,
                PurchasePrice = p.PurchasePrice,
                LocalSupplierCode = p.LocalSupplierCode,
                ProductImage = p.ProductImage,
            })
            .ToListAsync();
        if (from is null || to is null)
        {
            return products.Select(p => p.ToCandidate(0, 0)).ToList();
        }

        return await AttachActivityAsync(products, from.Value, to.Value, requireActivity: false, cancellationToken);
    }

    /// <summary>按商品编码取我方商品（指定参考时校验存在、显示名称图片）。</summary>
    public async Task<Dictionary<string, SupplierPreorderRefCandidate>> LoadProductsAsync(IEnumerable<string> productCodes)
    {
        var result = new Dictionary<string, SupplierPreorderRefCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in productCodes.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).Chunk(SqlInBatchSize))
        {
            var list = chunk.ToList();
            var rows = await _db.Queryable<Product>()
                .Where(p => list.Contains(p.ProductCode!))
                .Select(p => new SupplierPreorderRefCandidateRow
                {
                    ProductCode = p.ProductCode,
                    ItemNumber = p.ItemNumber,
                    Barcode = p.Barcode,
                    ProductName = p.ProductName,
                    PurchasePrice = p.PurchasePrice,
                    LocalSupplierCode = p.LocalSupplierCode,
                    ProductImage = p.ProductImage,
                })
                .ToListAsync();
            foreach (var row in rows.Where(r => !string.IsNullOrWhiteSpace(r.ProductCode)))
            {
                result[row.ProductCode!] = row.ToCandidate(0, 0);
            }
        }

        return result;
    }

    internal sealed class SupplierPreorderRefCandidateRow
    {
        public string? ProductCode { get; set; }
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public decimal? PurchasePrice { get; set; }
        public string? LocalSupplierCode { get; set; }
        public string? ProductImage { get; set; }

        public SupplierPreorderRefCandidate ToCandidate(decimal salesQty, decimal purchaseQty) => new()
        {
            ProductCode = ProductCode!.Trim(),
            ItemNumber = ItemNumber,
            Barcode = Barcode,
            Name = ProductName,
            PurchasePrice = PurchasePrice,
            LocalSupplierCode = LocalSupplierCode,
            ProductImage = ProductImage,
            SalesQty = salesQty,
            PurchaseQty = purchaseQty,
        };
    }

    private async Task<List<SupplierPreorderRefCandidate>> AttachActivityAsync(
        List<SupplierPreorderRefCandidateRow> products,
        DateTime from,
        DateTime to,
        bool requireActivity,
        CancellationToken cancellationToken)
    {
        var codes = products.Where(p => !string.IsNullOrWhiteSpace(p.ProductCode)).Select(p => p.ProductCode!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var salesTotals = await ReadSalesTotalsAsync(codes, from, to, cancellationToken);
        var purchaseTotals = await ReadPurchaseTotalsAsync(codes, from, to, cancellationToken);
        return products
            .Where(p => !string.IsNullOrWhiteSpace(p.ProductCode))
            .Select(p =>
            {
                var code = p.ProductCode!.Trim();
                return p.ToCandidate(salesTotals.GetValueOrDefault(code), purchaseTotals.GetValueOrDefault(code));
            })
            .Where(c => !requireActivity || c.SalesQty > 0 || c.PurchaseQty > 0)
            .GroupBy(c => c.ProductCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    // ---------------- 事实读取 ----------------

    private async Task<List<string>> LoadStoreCodesAsync()
    {
        var codes = await _db.Queryable<Store>()
            .Where(s => !s.IsDeleted && s.StoreCode != null && s.StoreCode != "")
            .Select(s => s.StoreCode)
            .ToListAsync();
        // 停用的分店去年也可能有数据（对比表里照样显示），所以不按 IsActive 过滤
        return codes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>窗口内每天的日期列表（读日统计与折扣日快照用）。</summary>
    private static List<DateTime> Days(DateTime from, DateTime to) =>
        Enumerable.Range(0, (to.Date - from.Date).Days + 1).Select(offset => from.Date.AddDays(offset)).ToList();

    /// <summary>销量 + 折扣分类：日统计给数量，折扣日快照给正价 / 折扣 / 未判定拆分；快照缺失的日子整天记为未判定。</summary>
    internal async Task<List<SupplierPreorderSalesFact>> ReadSalesAsync(
        IReadOnlyList<string> productCodes,
        DateTime from,
        DateTime to,
        IReadOnlyList<string> stores,
        CancellationToken cancellationToken)
    {
        var result = new List<SupplierPreorderSalesFact>();
        if (productCodes.Count == 0 || stores.Count == 0)
        {
            return result;
        }

        var days = Days(from, to);
        var statisticReader = new BatchProductSalesStatisticReader(_db);
        var discountReader = new BatchProductSalesDiscountSnapshotReader(_db);
        foreach (var chunk in productCodes.Chunk(SalesProductBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var statistics = await statisticReader.ReadAsync(chunk, days, stores, cancellationToken);
            var withSales = statistics.Select(s => s.ProductCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (withSales.Count == 0)
            {
                continue;
            }

            var discounts = await discountReader.ReadManyAsync(withSales, days, stores, statistics, cancellationToken);
            foreach (var (code, read) in discounts)
            {
                foreach (var row in read.Rows)
                {
                    if (row.Quantity == 0 && row.RegularQuantity == 0 && row.DiscountQuantity == 0)
                    {
                        continue;
                    }

                    result.Add(new SupplierPreorderSalesFact(
                        row.BranchCode.Trim(),
                        code,
                        row.Date.Date,
                        row.Quantity,
                        row.RegularQuantity,
                        row.DiscountQuantity,
                        row.UnknownQuantity,
                        row.OriginalPriceMax,
                        row.DiscountPriceMin));
                }
            }
        }

        return result;
    }

    private sealed class PurchaseSqlRow
    {
        public string? StoreCode { get; set; }
        public string? ProductCode { get; set; }
        public string? DocumentNo { get; set; }
        public DateTime? PrimaryDate { get; set; }
        public DateTime? OrderDate { get; set; }
        public DateTime CreatedAt { get; set; }
        public decimal? Quantity { get; set; }
    }

    /// <summary>
    /// 逐批进货（与 #886 商品抽屉 GetSalesDetailProductPurchasesAsync 同一口径，改为按多个商品批量读）。
    /// 日期回退在过滤里拆成三支，各日期列的索引都能用。
    /// </summary>
    internal async Task<List<SupplierPreorderPurchaseFact>> ReadPurchasesAsync(
        IReadOnlyList<string> productCodes,
        DateTime from,
        DateTime to,
        IReadOnlyList<string> stores,
        CancellationToken cancellationToken)
    {
        var result = new List<SupplierPreorderPurchaseFact>();
        if (productCodes.Count == 0)
        {
            return result;
        }

        var start = from.Date;
        var endExclusive = to.Date.AddDays(1);
        var storeSet = stores.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in productCodes.Chunk(SqlInBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var codes = chunk.ToList();
            var warehouseRows = await _db.Queryable<WareHouseOrderDetails>()
                .InnerJoin<WareHouseOrder>((detail, header) => detail.OrderGUID == header.OrderGUID)
                .Where((detail, header) =>
                    detail.IsDeleted == false
                    && header.IsDeleted == false
                    && (header.FlowStatus == null || header.FlowStatus != 0)
                    && detail.AllocQuantity != null
                    && detail.AllocQuantity != 0
                    && codes.Contains(detail.ProductCode!)
                    && header.StoreCode != null
                    && (
                        (header.OutboundDate >= start && header.OutboundDate < endExclusive)
                        || (header.OutboundDate == null && header.OrderDate >= start && header.OrderDate < endExclusive)
                        || (header.OutboundDate == null && header.OrderDate == null && header.CreatedAt >= start && header.CreatedAt < endExclusive)
                    ))
                .GroupBy((detail, header) => new { header.StoreCode, detail.ProductCode, header.OrderNo, header.OutboundDate, header.OrderDate, header.CreatedAt })
                .Select((detail, header) => new PurchaseSqlRow
                {
                    StoreCode = header.StoreCode,
                    ProductCode = detail.ProductCode,
                    DocumentNo = header.OrderNo,
                    PrimaryDate = header.OutboundDate,
                    OrderDate = header.OrderDate,
                    CreatedAt = header.CreatedAt,
                    Quantity = SqlFunc.AggregateSum(detail.AllocQuantity),
                })
                .ToListAsync();
            AddPurchaseFacts(result, warehouseRows, "warehouse", start, to.Date, storeSet);

            var supplierRows = await ReadSupplierInvoiceRowsAsync(
                query => query.Where((detail, header) => codes.Contains(detail.ProductCode!)), start, endExclusive);
            AddPurchaseFacts(result, supplierRows, "supplier", start, to.Date, storeSet);

            // 明细缺商品编码时按零售价 UUID 回查（同批量货号销量、商品抽屉）
            var prices = await _db.Queryable<StoreRetailPrice>()
                .Where(p => codes.Contains(p.ProductCode!) && p.IsDeleted == false)
                .Select(p => new { p.UUID, p.ProductCode })
                .ToListAsync();
            var codeByUuid = prices
                .Where(p => !string.IsNullOrWhiteSpace(p.ProductCode))
                .GroupBy(p => p.UUID, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().ProductCode!.Trim(), StringComparer.OrdinalIgnoreCase);
            foreach (var uuidChunk in codeByUuid.Keys.Chunk(SqlInBatchSize))
            {
                var uuids = uuidChunk.ToList();
                var fallbackRows = await ReadSupplierInvoiceRowsAsync(
                    query => query.Where((detail, header) =>
                        (detail.ProductCode == null || detail.ProductCode == "")
                        && detail.StoreProductCode != null
                        && uuids.Contains(detail.StoreProductCode)),
                    start,
                    endExclusive,
                    groupByStoreProductCode: true);
                foreach (var row in fallbackRows)
                {
                    row.ProductCode = row.ProductCode is not null && codeByUuid.TryGetValue(row.ProductCode, out var mapped) ? mapped : null;
                }

                AddPurchaseFacts(result, fallbackRows, "supplier", start, to.Date, storeSet);
            }
        }

        return result;
    }

    /// <summary>分店进货单明细（groupByStoreProductCode = true 时 ProductCode 字段先放 StoreProductCode，由调用方换成商品编码）。</summary>
    private async Task<List<PurchaseSqlRow>> ReadSupplierInvoiceRowsAsync(
        Func<ISugarQueryable<StoreLocalSupplierInvoiceDetails, StoreLocalSupplierInvoice>, ISugarQueryable<StoreLocalSupplierInvoiceDetails, StoreLocalSupplierInvoice>> narrow,
        DateTime start,
        DateTime endExclusive,
        bool groupByStoreProductCode = false)
    {
        var query = narrow(_db.Queryable<StoreLocalSupplierInvoiceDetails>()
            .InnerJoin<StoreLocalSupplierInvoice>((detail, header) => detail.InvoiceGUID == header.InvoiceGUID)
            .Where((detail, header) =>
                detail.IsDeleted == false
                && header.IsDeleted == false
                && detail.Quantity != null
                && detail.Quantity != 0
                && (
                    (header.InboundDate >= start && header.InboundDate < endExclusive)
                    || (header.InboundDate == null && header.OrderDate >= start && header.OrderDate < endExclusive)
                    || (header.InboundDate == null && header.OrderDate == null && header.CreatedAt >= start && header.CreatedAt < endExclusive)
                )));
        if (groupByStoreProductCode)
        {
            return await query
                .GroupBy((detail, header) => new { header.StoreCode, detail.StoreProductCode, header.InvoiceNo, header.InboundDate, header.OrderDate, header.CreatedAt })
                .Select((detail, header) => new PurchaseSqlRow
                {
                    StoreCode = header.StoreCode,
                    ProductCode = detail.StoreProductCode,
                    DocumentNo = header.InvoiceNo,
                    PrimaryDate = header.InboundDate,
                    OrderDate = header.OrderDate,
                    CreatedAt = header.CreatedAt,
                    Quantity = SqlFunc.AggregateSum(detail.Quantity),
                })
                .ToListAsync();
        }

        return await query
            .GroupBy((detail, header) => new { header.StoreCode, detail.ProductCode, header.InvoiceNo, header.InboundDate, header.OrderDate, header.CreatedAt })
            .Select((detail, header) => new PurchaseSqlRow
            {
                StoreCode = header.StoreCode,
                ProductCode = detail.ProductCode,
                DocumentNo = header.InvoiceNo,
                PrimaryDate = header.InboundDate,
                OrderDate = header.OrderDate,
                CreatedAt = header.CreatedAt,
                Quantity = SqlFunc.AggregateSum(detail.Quantity),
            })
            .ToListAsync();
    }

    private static void AddPurchaseFacts(List<SupplierPreorderPurchaseFact> target, IEnumerable<PurchaseSqlRow> rows, string source, DateTime start, DateTime end, HashSet<string> stores)
    {
        foreach (var row in rows)
        {
            if (row.Quantity is null or 0m || string.IsNullOrWhiteSpace(row.StoreCode) || string.IsNullOrWhiteSpace(row.ProductCode))
            {
                continue;
            }

            var store = row.StoreCode.Trim();
            var date = (row.PrimaryDate ?? row.OrderDate ?? row.CreatedAt).Date;
            if (date < start || date > end || (stores.Count > 0 && !stores.Contains(store)))
            {
                continue;
            }

            target.Add(new SupplierPreorderPurchaseFact(store, row.ProductCode.Trim(), date, source,
                row.Quantity.Value, string.IsNullOrWhiteSpace(row.DocumentNo) ? null : row.DocumentNo.Trim()));
        }
    }

    private sealed class ProductTotalRow
    {
        public string? ProductCode { get; set; }
        public decimal? Quantity { get; set; }
    }

    /// <summary>候选打分用：窗口内全部分店按商品合计的净销量。</summary>
    private async Task<Dictionary<string, decimal>> ReadSalesTotalsAsync(List<string> productCodes, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var start = from.Date;
        var endExclusive = to.Date.AddDays(1);
        foreach (var chunk in productCodes.Chunk(SqlInBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var codes = chunk.ToList();
            var rows = await _db.Queryable<ProductStoreDailySalesStatistic>()
                .Where(s => codes.Contains(s.ProductCode) && s.Date >= start && s.Date < endExclusive)
                .GroupBy(s => s.ProductCode)
                .Select(s => new ProductTotalRow { ProductCode = s.ProductCode, Quantity = SqlFunc.AggregateSum(s.TotalQuantity) })
                .ToListAsync();
            foreach (var row in rows.Where(r => !string.IsNullOrWhiteSpace(r.ProductCode)))
            {
                result[row.ProductCode!.Trim()] = result.GetValueOrDefault(row.ProductCode!.Trim()) + (row.Quantity ?? 0m);
            }
        }

        return result;
    }

    /// <summary>候选打分用：窗口内全部分店按商品合计的进货量（总仓配货 + 有商品编码的供应商进货单；不做 UUID 回查，只用来判断「有没有记录」）。</summary>
    private async Task<Dictionary<string, decimal>> ReadPurchaseTotalsAsync(List<string> productCodes, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var start = from.Date;
        var endExclusive = to.Date.AddDays(1);
        foreach (var chunk in productCodes.Chunk(SqlInBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var codes = chunk.ToList();
            var warehouse = await _db.Queryable<WareHouseOrderDetails>()
                .InnerJoin<WareHouseOrder>((detail, header) => detail.OrderGUID == header.OrderGUID)
                .Where((detail, header) =>
                    detail.IsDeleted == false
                    && header.IsDeleted == false
                    && (header.FlowStatus == null || header.FlowStatus != 0)
                    && detail.AllocQuantity != null
                    && detail.AllocQuantity != 0
                    && codes.Contains(detail.ProductCode!)
                    && (
                        (header.OutboundDate >= start && header.OutboundDate < endExclusive)
                        || (header.OutboundDate == null && header.OrderDate >= start && header.OrderDate < endExclusive)
                        || (header.OutboundDate == null && header.OrderDate == null && header.CreatedAt >= start && header.CreatedAt < endExclusive)
                    ))
                .GroupBy((detail, header) => detail.ProductCode)
                .Select((detail, header) => new ProductTotalRow { ProductCode = detail.ProductCode, Quantity = SqlFunc.AggregateSum(detail.AllocQuantity) })
                .ToListAsync();
            var supplier = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .InnerJoin<StoreLocalSupplierInvoice>((detail, header) => detail.InvoiceGUID == header.InvoiceGUID)
                .Where((detail, header) =>
                    detail.IsDeleted == false
                    && header.IsDeleted == false
                    && detail.Quantity != null
                    && detail.Quantity != 0
                    && codes.Contains(detail.ProductCode!)
                    && (
                        (header.InboundDate >= start && header.InboundDate < endExclusive)
                        || (header.InboundDate == null && header.OrderDate >= start && header.OrderDate < endExclusive)
                        || (header.InboundDate == null && header.OrderDate == null && header.CreatedAt >= start && header.CreatedAt < endExclusive)
                    ))
                .GroupBy((detail, header) => detail.ProductCode)
                .Select((detail, header) => new ProductTotalRow { ProductCode = detail.ProductCode, Quantity = SqlFunc.AggregateSum(detail.Quantity) })
                .ToListAsync();
            foreach (var row in warehouse.Concat(supplier).Where(r => !string.IsNullOrWhiteSpace(r.ProductCode)))
            {
                result[row.ProductCode!.Trim()] = result.GetValueOrDefault(row.ProductCode!.Trim()) + (row.Quantity ?? 0m);
            }
        }

        return result;
    }
}

/// <summary>参考商品的取舍（纯函数，便于单测）。</summary>
public static class SupplierPreorderRefDecisions
{
    /// <summary>需要参考 = 没匹配到我方商品，或匹配到了但本品在窗口内（全部分店）没有销量。</summary>
    public static bool NeedsReference(string? matchedProductCode, IReadOnlySet<string> ownWithSales) =>
        string.IsNullOrWhiteSpace(matchedProductCode) || !ownWithSales.Contains(matchedProductCode);

    /// <summary>把系统推荐写到商品上；返回是否有变化。人工指定 / 人工清除的不应传进来。</summary>
    public static bool ApplyAuto(SupplierPreorderFormItem item, SupplierPreorderRefPick? pick, DateTime nowUtc)
    {
        if (pick is null)
        {
            if (item.RefProductCode is null && item.RefSource is null)
            {
                return false;
            }

            ClearAuto(item);
            return true;
        }

        var score = Math.Round(pick.Score.Score, 3);
        if (string.Equals(item.RefProductCode, pick.Candidate.ProductCode, StringComparison.OrdinalIgnoreCase)
            && item.RefSource == SupplierPreorderRefSources.Auto
            && item.RefScore == score)
        {
            return false;
        }

        item.RefProductCode = pick.Candidate.ProductCode;
        item.RefSource = SupplierPreorderRefSources.Auto;
        item.RefScore = score;
        item.RefSetBy = "System";
        item.RefSetAtUtc = nowUtc;
        return true;
    }

    public static void ClearAuto(SupplierPreorderFormItem item)
    {
        item.RefProductCode = null;
        item.RefSource = null;
        item.RefScore = null;
        item.RefSetBy = null;
        item.RefSetAtUtc = null;
    }

    /// <summary>
    /// 这一行用谁的数据：本品在窗口内（全部分店）有销量 → 本品；否则有参考 → 参考；否则本品有进货也算本品（只有进货、没卖出去）；都没有 → 无。
    /// </summary>
    public static (string Source, string ProductCode)? ResolveSource(
        string? matchedProductCode,
        string? refProductCode,
        Func<string, SupplierPreorderHistoryPayload?> allStoresPayload)
    {
        var own = string.IsNullOrWhiteSpace(matchedProductCode) ? null : allStoresPayload(matchedProductCode);
        if (own?.HasSales == true && own.SalesQty > 0)
        {
            return ("Own", matchedProductCode!.Trim());
        }

        if (!string.IsNullOrWhiteSpace(refProductCode))
        {
            return ("Ref", refProductCode.Trim());
        }

        return own?.HasAny == true ? ("Own", matchedProductCode!.Trim()) : null;
    }
}
