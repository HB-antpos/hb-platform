using System.Text.Json;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.React;

/// <summary>
/// 只读取调度器发布的格式 2 日快照。查询端绝不补算、排队或扫描成交源。
/// </summary>
internal sealed class BatchProductSalesDiscountSnapshotReader(ISqlSugarClient db)
{
    private const int CurrentRuleVersion = 1;
    private const int ProductCodeQueryBatchSize = 500;

    internal bool SchemaReady
    {
        get
        {
            if (!db.DbMaintenance.IsAnyTable("BatchProductSalesDiscountSnapshot", false)
                || !db.DbMaintenance.IsAnyTable("BatchProductSalesDiscountRefreshState", false))
                return false;

            try
            {
                // 已存在旧表而新增列尚未迁移时，查询端安全返回未知，不能发出带新列的 SQL。
                return db.DbMaintenance.GetColumnInfosByTableName("BatchProductSalesDiscountSnapshot", false)
                    .Any(column => string.Equals(column.DbColumnName, "SnapshotFormat", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }
    }

    internal async Task<BatchProductSalesDiscountSnapshotReadResult> ReadAsync(
        string productCode,
        DateTime start,
        DateTime end,
        IReadOnlyList<string> stores,
        IReadOnlyList<BatchProductSalesAggregateRow> statistics,
        CancellationToken token)
    {
        var dates = Enumerable.Range(0, (end.Date - start.Date).Days + 1).Select(offset => start.Date.AddDays(offset)).ToList();
        return await ReadAsync(productCode, dates, stores, statistics, token);
    }

    internal async Task<BatchProductSalesDiscountSnapshotReadResult> ReadAsync(
        string productCode,
        IReadOnlyList<DateTime> dates,
        IReadOnlyList<string> stores,
        IReadOnlyList<BatchProductSalesAggregateRow> statistics,
        CancellationToken token)
    {
        var results = await ReadManyAsync([productCode], dates, stores, statistics, token);
        return results[productCode];
    }

    /// <summary>批量读取状态和快照各一次；每个商品只在内存中解析自己的发布 JSON。</summary>
    internal async Task<Dictionary<string, BatchProductSalesDiscountSnapshotReadResult>> ReadManyAsync(
        IReadOnlyList<string> productCodes, IReadOnlyList<DateTime> dates, IReadOnlyList<string> stores,
        IReadOnlyList<BatchProductSalesAggregateRow> statistics, CancellationToken token,
        BatchProductSalesDiscountStateSnapshot? stateSnapshot = null)
    {
        var codes = productCodes.Where(code => !string.IsNullOrWhiteSpace(code)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var days = dates.Select(date => date.Date).Distinct().OrderBy(date => date).ToList();
        if (days.Count == 0) return codes.ToDictionary(code => code, _ => new BatchProductSalesDiscountSnapshotReadResult([], "Pending", null), StringComparer.OrdinalIgnoreCase);

        var previousToken = db.Ado.CancellationToken;
        db.Ado.CancellationToken = token;
        try
        {

        // 统计行可能有数十万条；按商品和日期建一次索引，不能随商品数重复扫描整批统计。
        var statisticsByProduct = new Dictionary<string, List<BatchProductSalesAggregateRow>>(StringComparer.OrdinalIgnoreCase);
        var statisticsByProductDay = new Dictionary<(string Product, DateTime Date), List<BatchProductSalesAggregateRow>>(new ProductDayComparer());
        foreach (var statistic in statistics)
        {
            var productCode = statistic.ProductCode ?? string.Empty;
            if (!statisticsByProduct.TryGetValue(productCode, out var productStatistics))
                statisticsByProduct[productCode] = productStatistics = [];
            productStatistics.Add(statistic);

            var key = (productCode, statistic.Date.Date);
            if (!statisticsByProductDay.TryGetValue(key, out var dayStatistics))
                statisticsByProductDay[key] = dayStatistics = [];
            dayStatistics.Add(statistic);
        }

        var schemaReady = stateSnapshot?.SchemaReady ?? SchemaReady;
        if (!schemaReady)
        {
            var unavailable = new Dictionary<string, BatchProductSalesDiscountSnapshotReadResult>(StringComparer.OrdinalIgnoreCase);
            foreach (var code in codes)
            {
                token.ThrowIfCancellationRequested();
                // 与正常读取同样在每个商品/日期边界响应取消，避免大范围降级阻塞请求取消。
                foreach (var _ in days)
                    token.ThrowIfCancellationRequested();
                unavailable[code] = new(MarkUnknown(statisticsByProduct.GetValueOrDefault(code) ?? [], code, days, stores), "Unavailable", null);
            }
            return unavailable;
        }
        // SqlSugar 在 SQLite 上会把 DateTime IN 参数格式化为无法匹配的值；半开区间既保持精确日期集合，
        // 也让 SQL Server 可用 Date 列索引，避免把非连续 ready dates 之间的快照传回应用层。
        // 连续日期合并为一个区间：30 天连续范围只产生 1 组谓词而不是 30 组 OR，每个商品一次索引范围查找即可。
        var stateDates = Expressionable.Create<BatchProductSalesDiscountRefreshState>();
        var snapshotDates = Expressionable.Create<BatchProductSalesDiscountSnapshot>();
        foreach (var (rangeStart, rangeEnd) in CollapseContiguousDays(days))
        {
            stateDates = stateDates.Or(state => state.Date >= rangeStart && state.Date < rangeEnd);
            snapshotDates = snapshotDates.Or(snapshot => snapshot.StartDate >= rangeStart && snapshot.StartDate < rangeEnd);
        }
        var states = stateSnapshot?.States.SelectMany(pair => pair.Value).ToList()
            ?? await db.Queryable<BatchProductSalesDiscountRefreshState>().With(SqlWith.Null)
                .Where(stateDates.ToExpression()).ToListAsync(token);
        // SnapshotFormat 必须以字面量进入 SQL：表达式常量会被 SqlSugar 参数化为 @SnapshotFormat0，
        // SQL Server 无法为参数化谓词选用过滤索引 IX_BatchSalesDiscount_DailyProduct (WHERE SnapshotFormat = 2)，
        // 会退化为带 LOB 的全表聚集扫描，这是折扣分类晚到的主要原因。
        var snapshots = new List<BatchProductSalesDiscountSnapshot>();
        foreach (var codeBatch in codes.Chunk(ProductCodeQueryBatchSize))
        {
            token.ThrowIfCancellationRequested();
            snapshots.AddRange(await db.Queryable<BatchProductSalesDiscountSnapshot>().With(SqlWith.Null)
                .Where(SnapshotFormatLiteralPredicate)
                .Where(snapshot => codeBatch.Contains(snapshot.ProductCode) && snapshot.EndDate == snapshot.StartDate)
                .Where(snapshotDates.ToExpression()).ToListAsync(token));
        }
        token.ThrowIfCancellationRequested();
        var stateByDay = states.GroupBy(x => x.Date.Date).ToDictionary(g => g.Key, g => g.ToList());
        var snapshotMap = snapshots.GroupBy(x => (x.ProductCode ?? string.Empty, x.StartDate.Date), new ProductDayComparer()).ToDictionary(g => g.Key, g => g.ToList(), new ProductDayComparer());
        var authorizedStores = stores.Where(store => !string.IsNullOrWhiteSpace(store)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, BatchProductSalesDiscountSnapshotReadResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in codes)
        {
            var rows = new List<BatchProductSalesAggregateRow>(); var outcomes = new List<BatchProductSalesDiscountDayOutcome>(); var updated = new List<DateTime>();
            foreach (var day in days)
            {
                token.ThrowIfCancellationRequested();
                var stats = statisticsByProductDay.GetValueOrDefault((code, day)) ?? []; var dayStates = stateByDay.GetValueOrDefault(day) ?? [];
                var daySnapshots = snapshotMap.GetValueOrDefault((code, day)) ?? [];
                if (dayStates.Count != 1) { rows.AddRange(MarkUnknown(stats, code, [day], stores)); outcomes.Add(BatchProductSalesDiscountDayOutcome.Backfilling); continue; }
                var state = dayStates[0];
                if (!HasCommittedGeneration(state)) { rows.AddRange(MarkUnknown(stats, code, [day], stores)); outcomes.Add(state.RuleVersion != CurrentRuleVersion ? BatchProductSalesDiscountDayOutcome.OutOfSync : IsRetrying(state.Status) ? BatchProductSalesDiscountDayOutcome.Backfilling : BatchProductSalesDiscountDayOutcome.Unavailable); continue; }
                if (daySnapshots.Count == 0) { if (stats.Count == 0) outcomes.Add(IsFresh(state.Status) ? BatchProductSalesDiscountDayOutcome.Fresh : BatchProductSalesDiscountDayOutcome.Refreshing); else { rows.AddRange(MarkUnknown(stats, code, [day], stores)); outcomes.Add(BatchProductSalesDiscountDayOutcome.OutOfSync); } continue; }
                if (daySnapshots.Count != 1 || !IsUsable(daySnapshots[0], state)) { rows.AddRange(MarkUnknown(stats, code, [day], stores)); outcomes.Add(IsRetrying(daySnapshots[0].Status) ? BatchProductSalesDiscountDayOutcome.Backfilling : BatchProductSalesDiscountDayOutcome.OutOfSync); continue; }
                try { var parsed = JsonSerializer.Deserialize<List<BatchProductSalesAggregateRow?>>(daySnapshots[0].PayloadJson!) ?? []; if (parsed.Any(row => !IsContractRow(row, day, code))) throw new JsonException(); var authorized = parsed.Where(row => row is not null && authorizedStores.Contains(row.BranchCode)).Select(row => row!).ToList(); if (!BatchProductSalesStatisticReader.TotalsMatch(stats, authorized)) { rows.AddRange(MarkUnknown(stats, code, [day], stores)); outcomes.Add(BatchProductSalesDiscountDayOutcome.OutOfSync); continue; } rows.AddRange(authorized); updated.Add(daySnapshots[0].CompletedAtUtc!.Value); outcomes.Add(IsFresh(state.Status) ? BatchProductSalesDiscountDayOutcome.Fresh : BatchProductSalesDiscountDayOutcome.Refreshing); }
                catch (JsonException) { rows.AddRange(MarkUnknown(stats, code, [day], stores)); outcomes.Add(BatchProductSalesDiscountDayOutcome.OutOfSync); }
            }
            var status = outcomes.All(x=>x==BatchProductSalesDiscountDayOutcome.Fresh) ? "Fresh" : outcomes.All(x=>x is BatchProductSalesDiscountDayOutcome.Fresh or BatchProductSalesDiscountDayOutcome.Refreshing) && outcomes.Any(x=>x==BatchProductSalesDiscountDayOutcome.Refreshing) ? "Refreshing" : outcomes.Any(x=>x is BatchProductSalesDiscountDayOutcome.Fresh or BatchProductSalesDiscountDayOutcome.Refreshing) ? "Partial" : outcomes.Any(x=>x==BatchProductSalesDiscountDayOutcome.OutOfSync) ? "OutOfSync" : outcomes.Any(x=>x==BatchProductSalesDiscountDayOutcome.Backfilling) ? "Backfilling" : "Unavailable";
            result[code] = new(rows, status, updated.Count == 0 ? null : updated.Max());
        }
        return result;
        }
        catch (Exception exception) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException("折扣快照查询已取消。", exception, token);
        }
        finally
        {
            RestoreAdoCancellationToken(previousToken);
        }
    }

    /// <summary>与过滤索引定义逐字一致的字面量谓词；方括号标识符在 SQL Server 与测试用 SQLite 上均有效。</summary>
    internal const string SnapshotFormatLiteralPredicate = "[SnapshotFormat] = 2";

    /// <summary>把已去重升序的日期折叠为若干 [start, endExclusive) 区间；不连续的 ready dates 之间保持断开。</summary>
    internal static List<(DateTime Start, DateTime EndExclusive)> CollapseContiguousDays(IReadOnlyList<DateTime> sortedDistinctDays)
    {
        var ranges = new List<(DateTime Start, DateTime EndExclusive)>();
        foreach (var day in sortedDistinctDays)
        {
            if (ranges.Count > 0 && ranges[^1].EndExclusive == day) ranges[^1] = (ranges[^1].Start, day.AddDays(1));
            else ranges.Add((day, day.AddDays(1)));
        }
        return ranges;
    }

    internal async Task<BatchProductSalesDiscountStateSnapshot> CaptureStateAsync(
        IReadOnlyList<DateTime> dates, CancellationToken token)
    {
        var previousToken = db.Ado.CancellationToken;
        db.Ado.CancellationToken = token;
        try
        {
            var schemaReady = SchemaReady;
            var days = dates.Select(date => date.Date).Distinct().OrderBy(date => date).ToList();
            var states = schemaReady ? await ReadStatesAsync(days, token) : [];
            var grouped = states.GroupBy(state => state.Date.Date)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<BatchProductSalesDiscountRefreshState>)group.ToList());
            var fingerprint = JsonSerializer.Serialize(states
                .Select(state => JsonSerializer.Serialize(new
                {
                    state.Date, state.Status, state.RuleVersion, state.StatisticsVersion,
                    state.SourceVersion, state.CompletedAtUtc,
                }))
                .OrderBy(value => value, StringComparer.Ordinal));
            return new BatchProductSalesDiscountStateSnapshot(schemaReady, grouped, fingerprint);
        }
        finally
        {
            RestoreAdoCancellationToken(previousToken);
        }
    }

    private async Task<List<BatchProductSalesDiscountRefreshState>> ReadStatesAsync(
        IReadOnlyList<DateTime> days, CancellationToken token)
    {
        if (days.Count == 0) return [];
        var stateDates = Expressionable.Create<BatchProductSalesDiscountRefreshState>();
        foreach (var (rangeStart, rangeEnd) in CollapseContiguousDays(days))
        {
            token.ThrowIfCancellationRequested();
            stateDates = stateDates.Or(state => state.Date >= rangeStart && state.Date < rangeEnd);
        }
        return await db.Queryable<BatchProductSalesDiscountRefreshState>().With(SqlWith.Null)
            .Where(stateDates.ToExpression()).ToListAsync(token);
    }

    /// <summary>SqlSugar 的 token 查询会写入 client ADO；请求结束前必须还原嵌套调用的原令牌。</summary>
    private void RestoreAdoCancellationToken(CancellationToken? token)
    {
        if (token.HasValue) db.Ado.CancellationToken = token.Value;
        else db.Ado.RemoveCancellationToken();
    }

    private sealed class ProductDayComparer : IEqualityComparer<(string Product, DateTime Date)>
    { public bool Equals((string Product, DateTime Date) x, (string Product, DateTime Date) y) => x.Date == y.Date && string.Equals(x.Product, y.Product, StringComparison.OrdinalIgnoreCase); public int GetHashCode((string Product, DateTime Date) x) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(x.Product), x.Date); }

    private static bool HasCommittedGeneration(BatchProductSalesDiscountRefreshState state) =>
        state.RuleVersion == CurrentRuleVersion
        && state.CompletedAtUtc.HasValue
        && !string.IsNullOrWhiteSpace(state.SourceVersion)
        && !string.IsNullOrWhiteSpace(state.StatisticsVersion);

    private static bool IsUsable(BatchProductSalesDiscountSnapshot snapshot, BatchProductSalesDiscountRefreshState state) =>
        snapshot.SnapshotFormat == 2
        && snapshot.StartDate.Date == snapshot.EndDate.Date
        && snapshot.CompletedAtUtc.HasValue
        && !string.IsNullOrWhiteSpace(snapshot.PayloadJson)
        && !string.IsNullOrWhiteSpace(snapshot.SourceVersion)
        && !string.IsNullOrWhiteSpace(state.SourceVersion)
        && string.Equals(snapshot.Status, "Fresh", StringComparison.OrdinalIgnoreCase)
        && string.Equals(snapshot.SourceVersion, state.SourceVersion, StringComparison.Ordinal);

    private static bool IsFresh(string? status) => string.Equals(status, "Fresh", StringComparison.OrdinalIgnoreCase);

    private static bool IsContractRow(BatchProductSalesAggregateRow? row, DateTime day, string productCode) =>
        row is not null
        && row.Date.Date == day
        && string.Equals(row.ProductCode, productCode, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(row.BranchCode)
        && row.RegularQuantity + row.DiscountQuantity + row.UnknownQuantity == row.Quantity;

    private static bool IsRetrying(string? status) => string.Equals(status, "Queued", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "Backfilling", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, BatchProductSalesDiscountDailyStore.WaitingForCanonicalStatus, StringComparison.OrdinalIgnoreCase);

    internal static List<BatchProductSalesAggregateRow> MarkUnknown(
        IReadOnlyList<BatchProductSalesAggregateRow> statistics,
        string productCode,
        DateTime start,
        DateTime end,
        IReadOnlyList<string> stores)
    {
        var dates = Enumerable.Range(0, (end.Date - start.Date).Days + 1).Select(offset => start.Date.AddDays(offset)).ToList();
        return MarkUnknown(statistics, productCode, dates, stores);
    }

    internal static List<BatchProductSalesAggregateRow> MarkUnknown(
        IReadOnlyList<BatchProductSalesAggregateRow> statistics,
        string productCode,
        IReadOnlyList<DateTime> dates,
        IReadOnlyList<string> stores)
    {
        var authorizedStores = stores.Where(store => !string.IsNullOrWhiteSpace(store))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = statistics
            // 即使调用者失误传入范围外统计行，也不能把它放进当前授权响应。
            .Where(row => authorizedStores.Contains(row.BranchCode))
            .Select(row => new BatchProductSalesAggregateRow
        {
            Date = row.Date.Date, BranchCode = row.BranchCode, ProductCode = row.ProductCode,
            Quantity = row.Quantity, SalesAmount = row.SalesAmount,
            UnknownQuantity = row.Quantity, UnknownRowCount = Math.Max(1, row.UnknownRowCount),
        }).ToList();
        // 未完成或失配时，每个日期、每个授权分店都需要未知标记：只有部分分店有日统计也不能把其余分店补成完成零日。
        var completedStoreDays = rows.Select(row => (row.Date.Date, row.BranchCode ?? string.Empty))
            .ToHashSet(new StoreDayComparer());
        foreach (var day in dates.Select(date => date.Date).Distinct())
            foreach (var store in authorizedStores)
                if (completedStoreDays.Add((day, store)))
                    rows.Add(new BatchProductSalesAggregateRow
                {
                    Date = day, BranchCode = store, ProductCode = productCode, UnknownRowCount = 1,
                });
        return rows;
    }

    private sealed class StoreDayComparer : IEqualityComparer<(DateTime Date, string Store)>
    {
        public bool Equals((DateTime Date, string Store) x, (DateTime Date, string Store) y) =>
            x.Date == y.Date && string.Equals(x.Store, y.Store, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((DateTime Date, string Store) value) =>
            HashCode.Combine(value.Date, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Store));
    }
}

internal sealed record BatchProductSalesDiscountSnapshotReadResult(
    List<BatchProductSalesAggregateRow> Rows,
    string Status,
    DateTime? UpdatedAt);

internal sealed record BatchProductSalesDiscountStateSnapshot(
    bool SchemaReady,
    IReadOnlyDictionary<DateTime, IReadOnlyList<BatchProductSalesDiscountRefreshState>> States,
    string Fingerprint);

internal enum BatchProductSalesDiscountDayOutcome
{
    Fresh,
    Refreshing,
    Backfilling,
    OutOfSync,
    Unavailable,
}
