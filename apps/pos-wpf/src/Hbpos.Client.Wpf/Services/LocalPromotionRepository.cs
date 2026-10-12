using System.Globalization;
using Hbpos.Contracts.Promotions;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Wpf.Services;

public interface ILocalPromotionRepository
{
    Task ReplaceStoreRulesAsync(
        string storeCode,
        PromotionRulesResponse response,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PromotionRuleDto>> GetActiveRulesAsync(
        string storeCode,
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default);
}

public sealed class LocalPromotionRepository(LocalSqliteStore store) : ILocalPromotionRepository
{
    // 中文注释：扫码、改数量后每次重算促销都会读规则。这里按（当前活动库路径，门店码）缓存该门店“未按时间过滤”的
    // 全部规则行与商品行，GetActiveRulesAsync 在内存里按 asOf 过滤，命中时不再新开连接读库。
    // 失效：本仓储写促销表（ReplaceStoreRulesAsync，无论成败）与活动库路径变化都会清空缓存；促销表没有其他写入方，
    // TestSalesDataResetService 只清销售表、不碰促销表。_cacheVersion 防止“失效前开始、失效后才返回”的读取把旧数据写回缓存。
    // 并发：字典只在 _cacheGate 锁内读写；缓存快照是只读数组，不直接交给调用方，每次返回新建的规则与商品数组。
    private readonly object _cacheGate = new();
    private readonly Dictionary<PromotionCacheKey, StoreRulesSnapshot> _rulesCache = [];
    private string? _cachedDatabasePath;
    private long _cacheVersion;

    // 测试钩子：未命中缓存、即将读库时在读库所在线程调用（用于断言读库不在调用方 UI 线程，以及命中后不再读库）。
    internal Action? StoreRulesLoadingForTests { get; set; }

    public async Task ReplaceStoreRulesAsync(
        string storeCode,
        PromotionRulesResponse response,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await ReplaceStoreRulesCoreAsync(storeCode, response, cancellationToken);
        }
        finally
        {
            // 写事务结束（提交或回滚）后再失效：此后的读取一定读到已提交的数据；
            // 写入期间已开始的读取因版本号变化不会把旧数据写回缓存。
            InvalidateRulesCache();
        }
    }

    private async Task ReplaceStoreRulesCoreAsync(
        string storeCode,
        PromotionRulesResponse response,
        CancellationToken cancellationToken)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrEmpty(normalizedStoreCode))
        {
            throw new ArgumentException("Promotion store code is required.", nameof(storeCode));
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        // 先在同一个事务里清掉当前门店旧规则，再写入新快照；只要中途失败，事务就不会提交，其他门店和旧缓存都会原样保留。
        await DeleteStoreRulesAsync(connection, transaction, normalizedStoreCode, cancellationToken);

        await using var promotionCommand = connection.CreateCommand();
        promotionCommand.Transaction = transaction;
        promotionCommand.CommandText = InsertPromotionSql;
        AddPromotionParameters(promotionCommand);
        promotionCommand.Prepare();

        await using var productCommand = connection.CreateCommand();
        productCommand.Transaction = transaction;
        productCommand.CommandText = InsertPromotionProductSql;
        AddPromotionProductParameters(productCommand);
        productCommand.Prepare();

        var syncedAt = response.GeneratedAt.ToUniversalTime();
        foreach (var rule in response.Rules)
        {
            SetPromotionParameters(promotionCommand, normalizedStoreCode, rule, syncedAt);
            await promotionCommand.ExecuteNonQueryAsync(cancellationToken);

            foreach (var product in rule.Products)
            {
                SetPromotionProductParameters(productCommand, normalizedStoreCode, rule.Id, product);
                await productCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PromotionRuleDto>> GetActiveRulesAsync(
        string storeCode,
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrEmpty(normalizedStoreCode))
        {
            return [];
        }

        var databasePath = store.ActiveDatabasePath;
        var cacheKey = new PromotionCacheKey(databasePath, normalizedStoreCode);
        StoreRulesSnapshot? cached;
        long cacheVersion;
        lock (_cacheGate)
        {
            if (!string.Equals(_cachedDatabasePath, databasePath, StringComparison.Ordinal))
            {
                // 活动库已切换（或首次使用）：旧库的缓存全部作废。
                ClearRulesCacheLocked();
                _cachedDatabasePath = databasePath;
            }

            _rulesCache.TryGetValue(cacheKey, out cached);
            cacheVersion = _cacheVersion;
        }

        if (cached is not null)
        {
            // 命中：快照不可变，在锁外按 asOf 过滤，同步返回、不切线程。
            return cached.SelectActiveRules(asOf);
        }

        // 未命中：首次读库放进线程池，扫码后在 UI 线程发起的促销重算不必等 SQLite（*Async 实为同步执行）。
        var loadingHook = StoreRulesLoadingForTests;
        var snapshot = await Task.Run(
            () =>
            {
                loadingHook?.Invoke();
                return LoadStoreRulesAsync(normalizedStoreCode, cancellationToken);
            },
            cancellationToken);

        lock (_cacheGate)
        {
            if (_cacheVersion == cacheVersion &&
                string.Equals(_cachedDatabasePath, databasePath, StringComparison.Ordinal))
            {
                _rulesCache[cacheKey] = snapshot;
            }
        }

        return snapshot.SelectActiveRules(asOf);
    }

    private async Task<StoreRulesSnapshot> LoadStoreRulesAsync(string storeCode, CancellationToken cancellationToken)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        var promotionRows = await ReadPromotionRowsAsync(connection, storeCode, cancellationToken);
        IReadOnlyList<LocalPromotionProductRow> productRows = promotionRows.Count == 0
            ? []
            : await ReadStoreProductRowsAsync(connection, storeCode, cancellationToken);
        return new StoreRulesSnapshot([.. promotionRows], [.. productRows]);
    }

    private void InvalidateRulesCache()
    {
        lock (_cacheGate)
        {
            ClearRulesCacheLocked();
        }
    }

    private void ClearRulesCacheLocked()
    {
        _rulesCache.Clear();
        _cacheVersion++;
    }

    private static async Task DeleteStoreRulesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string storeCode,
        CancellationToken cancellationToken)
    {
        await using (var deleteProducts = connection.CreateCommand())
        {
            deleteProducts.Transaction = transaction;
            deleteProducts.CommandText = "DELETE FROM LocalPromotionRuleProducts WHERE StoreCode = $StoreCode;";
            deleteProducts.Parameters.AddWithValue("$StoreCode", storeCode);
            await deleteProducts.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var deletePromotions = connection.CreateCommand();
        deletePromotions.Transaction = transaction;
        deletePromotions.CommandText = "DELETE FROM LocalPromotions WHERE StoreCode = $StoreCode;";
        deletePromotions.Parameters.AddWithValue("$StoreCode", storeCode);
        await deletePromotions.ExecuteNonQueryAsync(cancellationToken);
    }

    // 读取门店全部规则（不按时间过滤），排序与原来按 asOf 过滤的查询一致；过滤只保留子集、不改变相对顺序。
    // 起止时间保留库里的原始文本，过滤时与 asOf 按同一格式做序数比较，与 SQL 的 TEXT 比较完全等价。
    private static async Task<List<LocalPromotionRow>> ReadPromotionRowsAsync(
        SqliteConnection connection,
        string storeCode,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                PromotionId,
                Name,
                EffectiveStart,
                EffectiveEnd,
                IsExclusive,
                Priority,
                ApplyQuantity,
                FixedPrice,
                MaxApplicationsPerOrder
            FROM LocalPromotions
            WHERE StoreCode = $StoreCode
            ORDER BY Priority DESC, EffectiveStart ASC, PromotionId ASC;
            """;
        command.Parameters.AddWithValue("$StoreCode", storeCode);

        var rows = new List<LocalPromotionRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LocalPromotionRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4) == 1,
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8)));
        }

        return rows;
    }

    private static async Task<List<LocalPromotionProductRow>> ReadStoreProductRowsAsync(
        SqliteConnection connection,
        string storeCode,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT PromotionId, ProductCode, UnitWeight
            FROM LocalPromotionRuleProducts
            WHERE StoreCode = $StoreCode
            ORDER BY PromotionId ASC, ProductCode ASC;
            """;
        command.Parameters.AddWithValue("$StoreCode", storeCode);

        var rows = new List<LocalPromotionProductRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LocalPromotionProductRow(
                reader.GetString(0),
                new PromotionRuleProductDto(
                    reader.GetString(1),
                    reader.GetInt32(2))));
        }

        return rows;
    }

    private static void AddPromotionParameters(SqliteCommand command)
    {
        command.Parameters.Add("$StoreCode", SqliteType.Text);
        command.Parameters.Add("$PromotionId", SqliteType.Text);
        command.Parameters.Add("$Name", SqliteType.Text);
        command.Parameters.Add("$EffectiveStart", SqliteType.Text);
        command.Parameters.Add("$EffectiveEnd", SqliteType.Text);
        command.Parameters.Add("$IsExclusive", SqliteType.Integer);
        command.Parameters.Add("$Priority", SqliteType.Integer);
        command.Parameters.Add("$ApplyQuantity", SqliteType.Integer);
        command.Parameters.Add("$FixedPrice", SqliteType.Text);
        command.Parameters.Add("$MaxApplicationsPerOrder", SqliteType.Integer);
        command.Parameters.Add("$SyncedAt", SqliteType.Text);
    }

    private static void SetPromotionParameters(
        SqliteCommand command,
        string storeCode,
        PromotionRuleDto rule,
        DateTimeOffset syncedAt)
    {
        command.Parameters["$StoreCode"].Value = storeCode;
        command.Parameters["$PromotionId"].Value = NormalizeRequiredValue(rule.Id, nameof(rule.Id));
        command.Parameters["$Name"].Value = NormalizeRequiredValue(rule.Name, nameof(rule.Name));
        command.Parameters["$EffectiveStart"].Value = FormatUtc(ToUtc(rule.EffectiveStart));
        command.Parameters["$EffectiveEnd"].Value = FormatUtc(ToUtc(rule.EffectiveEnd));
        command.Parameters["$IsExclusive"].Value = rule.IsExclusive ? 1 : 0;
        command.Parameters["$Priority"].Value = rule.Priority;
        command.Parameters["$ApplyQuantity"].Value = rule.ApplyQuantity;
        command.Parameters["$FixedPrice"].Value = rule.FixedPrice.ToString(CultureInfo.InvariantCulture);
        command.Parameters["$MaxApplicationsPerOrder"].Value = rule.MaxApplicationsPerOrder.HasValue
            ? rule.MaxApplicationsPerOrder.Value
            : DBNull.Value;
        command.Parameters["$SyncedAt"].Value = FormatUtc(syncedAt);
    }

    private static void AddPromotionProductParameters(SqliteCommand command)
    {
        command.Parameters.Add("$StoreCode", SqliteType.Text);
        command.Parameters.Add("$PromotionId", SqliteType.Text);
        command.Parameters.Add("$ProductCode", SqliteType.Text);
        command.Parameters.Add("$UnitWeight", SqliteType.Integer);
    }

    private static void SetPromotionProductParameters(
        SqliteCommand command,
        string storeCode,
        string promotionId,
        PromotionRuleProductDto product)
    {
        command.Parameters["$StoreCode"].Value = storeCode;
        command.Parameters["$PromotionId"].Value = NormalizeRequiredValue(promotionId, nameof(promotionId));
        command.Parameters["$ProductCode"].Value = NormalizeRequiredValue(product.ProductCode, nameof(product.ProductCode));
        command.Parameters["$UnitWeight"].Value = product.UnitWeight;
    }

    private static string NormalizeStoreCode(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static string NormalizeRequiredValue(string? value, string paramName)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            throw new ArgumentException("Promotion value is required.", paramName);
        }

        return normalized;
    }

    private static DateTimeOffset ToUtc(DateTime value)
    {
        var utcValue = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        return new DateTimeOffset(utcValue, TimeSpan.Zero);
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTime ParseUtcDateTime(string value)
    {
        return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    }

    private readonly record struct PromotionCacheKey(string DatabasePath, string StoreCode);

    private sealed record LocalPromotionRow(
        string PromotionId,
        string Name,
        string EffectiveStartText,
        string EffectiveEndText,
        bool IsExclusive,
        int Priority,
        int ApplyQuantity,
        string FixedPriceText,
        int? MaxApplicationsPerOrder);

    private sealed record LocalPromotionProductRow(
        string PromotionId,
        PromotionRuleProductDto Product);

    // 一个门店的规则快照（只读）。SelectActiveRules 复刻原 SQL 的口径：
    // 规则 EffectiveStart <= asOf <= EffectiveEnd（TEXT 序数比较，含边界）；
    // 商品只取 PromotionId 与生效规则完全相同（SQLite BINARY 比较）的行，再按忽略大小写分组挂到规则上。
    private sealed class StoreRulesSnapshot(
        LocalPromotionRow[] promotionRows,
        LocalPromotionProductRow[] productRows)
    {
        public IReadOnlyList<PromotionRuleDto> SelectActiveRules(DateTimeOffset asOf)
        {
            var asOfText = FormatUtc(asOf);
            var activeRows = promotionRows
                .Where(row =>
                    string.CompareOrdinal(row.EffectiveStartText, asOfText) <= 0 &&
                    string.CompareOrdinal(row.EffectiveEndText, asOfText) >= 0)
                .ToArray();
            if (activeRows.Length == 0)
            {
                return [];
            }

            var activeIds = activeRows
                .Select(row => row.PromotionId)
                .ToHashSet(StringComparer.Ordinal);
            var productsByPromotionId = productRows
                .Where(row => activeIds.Contains(row.PromotionId))
                .GroupBy(row => row.PromotionId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<PromotionRuleProductDto>)group.Select(item => item.Product).ToArray(),
                    StringComparer.OrdinalIgnoreCase);

            // 每次都新建规则对象与商品数组：调用方拿到的集合改不到缓存。
            return activeRows
                .Select(row => new PromotionRuleDto(
                    row.PromotionId,
                    row.Name,
                    ParseUtcDateTime(row.EffectiveStartText),
                    ParseUtcDateTime(row.EffectiveEndText),
                    row.IsExclusive,
                    row.Priority,
                    row.ApplyQuantity,
                    decimal.Parse(row.FixedPriceText, CultureInfo.InvariantCulture),
                    row.MaxApplicationsPerOrder,
                    productsByPromotionId.TryGetValue(row.PromotionId, out var products) ? products : []))
                .ToArray();
        }
    }

    private const string InsertPromotionSql = """
        INSERT INTO LocalPromotions (
            StoreCode,
            PromotionId,
            Name,
            EffectiveStart,
            EffectiveEnd,
            IsExclusive,
            Priority,
            ApplyQuantity,
            FixedPrice,
            MaxApplicationsPerOrder,
            SyncedAt)
        VALUES (
            $StoreCode,
            $PromotionId,
            $Name,
            $EffectiveStart,
            $EffectiveEnd,
            $IsExclusive,
            $Priority,
            $ApplyQuantity,
            $FixedPrice,
            $MaxApplicationsPerOrder,
            $SyncedAt);
        """;

    private const string InsertPromotionProductSql = """
        INSERT INTO LocalPromotionRuleProducts (
            StoreCode,
            PromotionId,
            ProductCode,
            UnitWeight)
        VALUES (
            $StoreCode,
            $PromotionId,
            $ProductCode,
            $UnitWeight);
        """;
}
