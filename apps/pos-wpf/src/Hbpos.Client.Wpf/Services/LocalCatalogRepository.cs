using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Hbpos.Contracts.Catalog;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Wpf.Services;

public sealed record LocalSellableItemCompareRow(
    string StoreCode,
    string LookupCodeNormalized,
    string ContentHash,
    DateTimeOffset? SyncedAt);

public sealed record LocalCatalogStoreReplaceCommitResult(
    int InsertedCount,
    int DeletedCount);

/// <summary>
/// 全量替换对应的服务端目录版本；提交时暂存条数必须等于 ExpectedItemCount 才会生效。
/// </summary>
public sealed record LocalCatalogVersionStamp(
    string CatalogVersion,
    int ExpectedItemCount);

public sealed record LocalCatalogDeltaApplyResult(
    int UpsertedCount,
    int DeletedCount,
    int LocalItemCount);

/// <summary>
/// 本地目录与服务端版本对不上（暂存条数不符、增量基准已变），调用方应改走全量下载。
/// </summary>
public sealed class LocalCatalogVersionConflictException(string message) : InvalidOperationException(message);

public interface ILocalCatalogStoreReplaceSession : IAsyncDisposable
{
    Task StageAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default);

    Task<LocalCatalogStoreReplaceCommitResult> CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 与版本记录在同一事务里提交全量替换；暂存条数与版本期望不符时整体回滚。
    /// </summary>
    Task<LocalCatalogStoreReplaceCommitResult> CommitAsync(
        LocalCatalogVersionStamp versionStamp,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Versioned store replace is not supported by this session.");
    }
}

public interface ILocalCatalogRepository
{
    Task ReplaceSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default);

    Task UpsertSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default);

    Task<int> DeleteByLookupCodesAsync(string storeCode, IEnumerable<string> lookupCodes, CancellationToken cancellationToken = default);

    Task<SellableItemDto?> FindByLookupCodeAsync(string storeCode, string lookupCode, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SellableItemDto>> LoadSpecialProductItemsAsync(string storeCode, CancellationToken cancellationToken = default);

    Task SaveSpecialProductOrderAsync(string storeCode, IEnumerable<string> productCodes, CancellationToken cancellationToken = default);

    Task<int> UpdateSpecialProductFlagAsync(
        string storeCode,
        string productCode,
        bool isSpecialProduct,
        CancellationToken cancellationToken = default);

    Task<int> ClearSpecialProductFlagsExceptAsync(
        string storeCode,
        IEnumerable<string> productCodesToKeep,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalSellableItemCompareRow>> LoadSellableItemComparePageAsync(
        string storeCode,
        string? afterLookupCodeNormalized,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(string storeCode, CancellationToken cancellationToken = default);

    Task ReplacePromotionRulesAsync(
        string storeCode,
        IEnumerable<CatalogPromotionRuleDto> rules,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Promotion rule caching is not supported by this repository.");
    }

    Task<IReadOnlyList<CatalogPromotionRuleDto>> LoadPromotionRulesAsync(
        string storeCode,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<CatalogPromotionRuleDto>>([]);
    }

    Task<ILocalCatalogStoreReplaceSession> BeginStoreReplaceSessionAsync(
        string storeCode,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Store replace sessions are not supported by this repository.");
    }

    Task ReplaceCodeConflictItemsAsync(
        string storeCode,
        IEnumerable<SellableItemDto> items,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Code conflict caching is not supported by this repository.");
    }

    Task<IReadOnlyList<SellableItemDto>> LoadCodeConflictItemsAsync(
        string storeCode,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<SellableItemDto>>([]);
    }

    /// <summary>
    /// 内容与本地一致时不写库并返回 false，调用方据此跳过内存扫码索引重建。
    /// </summary>
    async Task<bool> ReplaceCodeConflictItemsIfChangedAsync(
        string storeCode,
        IEnumerable<SellableItemDto> items,
        CancellationToken cancellationToken = default)
    {
        await ReplaceCodeConflictItemsAsync(storeCode, items, cancellationToken);
        return true;
    }

    Task<string?> GetCatalogVersionAsync(
        string storeCode,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(null);
    }

    Task ClearCatalogVersionAsync(
        string storeCode,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// 在一个事务里应用 base→target 的增量并把版本记录改为 target；
    /// 本地记录的版本不是 base 时抛 <see cref="LocalCatalogVersionConflictException"/> 且不做任何修改。
    /// </summary>
    Task<LocalCatalogDeltaApplyResult> ApplyCatalogDeltaAsync(
        string storeCode,
        string baseCatalogVersion,
        string targetCatalogVersion,
        IReadOnlyList<SellableItemDto> upsertedItems,
        IReadOnlyList<string> deletedLookupCodes,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Catalog delta apply is not supported by this repository.");
    }
}

public sealed class LocalCatalogRepository(LocalSqliteStore store) : ILocalCatalogRepository
{
    public Task ReplaceSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
    {
        return UpsertSellableItemsAsync(items, cancellationToken);
    }

    public async Task UpsertSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
    {
        var materializedItems = items.ToList();
        if (materializedItems.Count == 0)
        {
            return;
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var syncedAt = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UpsertSellableItemSql;
        AddUpsertParameters(command);
        command.Prepare();

        foreach (var item in materializedItems)
        {
            var storeCode = NormalizeStoreCode(item.StoreCode);
            var lookupCodeNormalized = NormalizeLookupCode(item.LookupCode);
            if (string.IsNullOrEmpty(storeCode))
            {
                throw new ArgumentException("Sellable item store code is required.", nameof(items));
            }

            if (string.IsNullOrEmpty(lookupCodeNormalized))
            {
                throw new ArgumentException("Sellable item lookup code is required.", nameof(items));
            }

            var contentHash = CreateContentHash(item, storeCode, lookupCodeNormalized);
            SetItemParameters(command, item, storeCode, lookupCodeNormalized, contentHash, syncedAt);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> DeleteByLookupCodesAsync(
        string storeCode,
        IEnumerable<string> lookupCodes,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrEmpty(normalizedStoreCode))
        {
            return 0;
        }

        var normalizedLookupCodes = lookupCodes
            .Select(NormalizeLookupCode)
            .Where(code => !string.IsNullOrEmpty(code))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedLookupCodes.Length == 0)
        {
            return 0;
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var deleted = 0;

        foreach (var lookupCodeNormalized in normalizedLookupCodes)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM LocalSellableItemIndex
                WHERE StoreCode = $StoreCode
                  AND LookupCodeNormalized = $LookupCodeNormalized;
                """;
            command.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
            command.Parameters.AddWithValue("$LookupCodeNormalized", lookupCodeNormalized);
            deleted += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    public async Task<SellableItemDto?> FindByLookupCodeAsync(
        string storeCode,
        string lookupCode,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        var lookupCodeNormalized = NormalizeLookupCode(lookupCode);
        if (string.IsNullOrEmpty(normalizedStoreCode) || string.IsNullOrEmpty(lookupCodeNormalized))
        {
            return null;
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            {SelectSellableItemSql}
            WHERE StoreCode = $StoreCode
              AND LookupCodeNormalized = $LookupCodeNormalized
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
        command.Parameters.AddWithValue("$LookupCodeNormalized", lookupCodeNormalized);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSellableItem(reader) : null;
    }

    public async Task<IReadOnlyList<SellableItemDto>> LoadSpecialProductItemsAsync(
        string storeCode,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrEmpty(normalizedStoreCode))
        {
            return [];
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                l.StoreCode,
                l.ProductCode,
                l.ReferenceCode,
                l.DisplayName,
                l.LookupCode,
                l.ItemNumber,
                l.Barcode,
                l.ProductImage,
                l.DiscountRate,
                l.IsSpecialProduct,
                l.RetailPrice,
                l.PriceSource,
                l.PriceSourceLabel,
                l.QuantityFactor,
                l.UpdatedAt,
                s.SortOrder
            FROM LocalSellableItemIndex l
            LEFT JOIN LocalSpecialProductSortOrder s
              ON s.StoreCode = l.StoreCode
             AND s.ProductCode = l.ProductCode
            WHERE l.StoreCode = $StoreCode
              AND l.IsSpecialProduct = 1
            ORDER BY
                CASE WHEN s.SortOrder IS NULL THEN 1 ELSE 0 END,
                s.SortOrder,
                l.DisplayName,
                l.ProductCode,
                l.LookupCodeNormalized;
            """;
        command.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);

        var rows = new List<(SellableItemDto Item, int? SortOrder)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((ReadSellableItem(reader), ReadNullableInt32(reader, "SortOrder")));
        }

        return rows
            .GroupBy(row => NormalizeProductCode(row.Item.ProductCode), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(row => row.SortOrder ?? int.MaxValue)
                .ThenBy(row => PreferredSpecialLookupRank(row.Item))
                .ThenBy(row => row.Item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.Item.LookupCode, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(row => row.SortOrder ?? int.MaxValue)
            .ThenBy(row => row.Item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.Item.ProductCode, StringComparer.OrdinalIgnoreCase)
            .Select(row => row.Item)
            .ToArray();
    }

    public async Task SaveSpecialProductOrderAsync(
        string storeCode,
        IEnumerable<string> productCodes,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrEmpty(normalizedStoreCode))
        {
            return;
        }

        var normalizedProductCodes = productCodes
            .Select(NormalizeProductCode)
            .Where(code => !string.IsNullOrEmpty(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM LocalSpecialProductSortOrder WHERE StoreCode = $StoreCode;";
            deleteCommand.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var updatedAt = DateTimeOffset.UtcNow.ToString("O");
        for (var index = 0; index < normalizedProductCodes.Length; index++)
        {
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO LocalSpecialProductSortOrder (StoreCode, ProductCode, SortOrder, UpdatedAt)
                VALUES ($StoreCode, $ProductCode, $SortOrder, $UpdatedAt);
                """;
            insertCommand.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
            insertCommand.Parameters.AddWithValue("$ProductCode", normalizedProductCodes[index]);
            insertCommand.Parameters.AddWithValue("$SortOrder", index);
            insertCommand.Parameters.AddWithValue("$UpdatedAt", updatedAt);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> UpdateSpecialProductFlagAsync(
        string storeCode,
        string productCode,
        bool isSpecialProduct,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        var normalizedProductCode = NormalizeProductCode(productCode);
        if (string.IsNullOrEmpty(normalizedStoreCode) || string.IsNullOrEmpty(normalizedProductCode))
        {
            return 0;
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE LocalSellableItemIndex
            SET IsSpecialProduct = $IsSpecialProduct
            WHERE StoreCode = $StoreCode
              AND ProductCode = $ProductCode;
            """;
        command.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
        command.Parameters.AddWithValue("$ProductCode", normalizedProductCode);
        command.Parameters.AddWithValue("$IsSpecialProduct", isSpecialProduct ? 1 : 0);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);

        if (!isSpecialProduct)
        {
            await using var deleteOrderCommand = connection.CreateCommand();
            deleteOrderCommand.Transaction = transaction;
            deleteOrderCommand.CommandText = """
                DELETE FROM LocalSpecialProductSortOrder
                WHERE StoreCode = $StoreCode
                  AND ProductCode = $ProductCode;
                """;
            deleteOrderCommand.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
            deleteOrderCommand.Parameters.AddWithValue("$ProductCode", normalizedProductCode);
            await deleteOrderCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<int> ClearSpecialProductFlagsExceptAsync(
        string storeCode,
        IEnumerable<string> productCodesToKeep,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrEmpty(normalizedStoreCode))
        {
            return 0;
        }

        var normalizedProductCodes = productCodesToKeep
            .Select(NormalizeProductCode)
            .Where(code => !string.IsNullOrEmpty(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        var exclusionClause = normalizedProductCodes.Length == 0
            ? string.Empty
            : $" AND ProductCode NOT IN ({string.Join(", ", normalizedProductCodes.Select((_, index) => $"$ProductCode{index}"))})";

        await using var updateCommand = connection.CreateCommand();
        updateCommand.Transaction = transaction;
        updateCommand.CommandText = $"""
            UPDATE LocalSellableItemIndex
            SET IsSpecialProduct = 0
            WHERE StoreCode = $StoreCode
              AND IsSpecialProduct = 1
              {exclusionClause};
            """;
        updateCommand.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
        for (var index = 0; index < normalizedProductCodes.Length; index++)
        {
            updateCommand.Parameters.AddWithValue($"$ProductCode{index}", normalizedProductCodes[index]);
        }

        var updated = await updateCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var deleteSortCommand = connection.CreateCommand();
        deleteSortCommand.Transaction = transaction;
        deleteSortCommand.CommandText = $"""
            DELETE FROM LocalSpecialProductSortOrder
            WHERE StoreCode = $StoreCode
              {exclusionClause};
            """;
        deleteSortCommand.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
        for (var index = 0; index < normalizedProductCodes.Length; index++)
        {
            deleteSortCommand.Parameters.AddWithValue($"$ProductCode{index}", normalizedProductCodes[index]);
        }

        await deleteSortCommand.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<IReadOnlyList<LocalSellableItemCompareRow>> LoadSellableItemComparePageAsync(
        string storeCode,
        string? afterLookupCodeNormalized,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrEmpty(normalizedStoreCode))
        {
            return [];
        }

        var cursor = string.IsNullOrWhiteSpace(afterLookupCodeNormalized)
            ? null
            : NormalizeLookupCode(afterLookupCodeNormalized);

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT StoreCode, LookupCodeNormalized, ContentHash, SyncedAt
            FROM LocalSellableItemIndex
            WHERE StoreCode = $StoreCode
              AND ($AfterLookupCodeNormalized IS NULL OR LookupCodeNormalized > $AfterLookupCodeNormalized)
            ORDER BY StoreCode, LookupCodeNormalized
            LIMIT $PageSize;
            """;
        command.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
        command.Parameters.AddWithValue("$AfterLookupCodeNormalized", (object?)cursor ?? DBNull.Value);
        command.Parameters.AddWithValue("$PageSize", Math.Clamp(pageSize, 1, 2000));

        var rows = new List<LocalSellableItemCompareRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LocalSellableItemCompareRow(
                ReadString(reader, "StoreCode"),
                ReadString(reader, "LookupCodeNormalized"),
                ReadString(reader, "ContentHash"),
                ReadNullableDateTimeOffset(reader, "SyncedAt")));
        }

        return rows;
    }

    public async Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            {SelectSellableItemSql}
            ORDER BY StoreCode, LookupCodeNormalized;
            """;

        var items = new List<SellableItemDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadSellableItem(reader));
        }

        return items;
    }

    public async Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(string storeCode, CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrWhiteSpace(normalizedStoreCode))
        {
            return [];
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            {SelectSellableItemSql}
            WHERE StoreCode = $StoreCode
            ORDER BY LookupCodeNormalized;
            """;
        command.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);

        var items = new List<SellableItemDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadSellableItem(reader));
        }

        return items;
    }

    public async Task ReplaceCodeConflictItemsAsync(
        string storeCode,
        IEnumerable<SellableItemDto> items,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrEmpty(normalizedStoreCode))
        {
            throw new ArgumentException("Store code is required.", nameof(storeCode));
        }

        var materializedItems = items.ToList();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await ReplaceCodeConflictItemsCoreAsync(connection, transaction, normalizedStoreCode, materializedItems, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> ReplaceCodeConflictItemsIfChangedAsync(
        string storeCode,
        IEnumerable<SellableItemDto> items,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrEmpty(normalizedStoreCode))
        {
            throw new ArgumentException("Store code is required.", nameof(storeCode));
        }

        var materializedItems = items.ToList();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        // 按"码 + 顺序 + 内容"比较：服务端下发的冲突与本地完全相同时不重写，让同步调用方跳过扫码索引重建。
        var incoming = materializedItems
            .Select(item => (Item: item, LookupCodeNormalized: NormalizeLookupCode(item.LookupCode)))
            .Where(entry =>
                string.Equals(NormalizeStoreCode(entry.Item.StoreCode), normalizedStoreCode, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(entry.LookupCodeNormalized))
            .Select((entry, index) => (entry.LookupCodeNormalized, Index: index, Key: CreateCodeConflictComparisonKey(
                entry.Item.ProductCode,
                CreateContentHash(entry.Item, normalizedStoreCode, entry.LookupCodeNormalized),
                entry.Item.LookupCode,
                entry.Item.UpdatedAt?.ToString("O"))))
            .OrderBy(entry => entry.LookupCodeNormalized, StringComparer.Ordinal)
            .ThenBy(entry => entry.Index)
            .Select(entry => string.Concat(entry.LookupCodeNormalized, "|", entry.Key))
            .ToList();

        var existing = new List<string>();
        await using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.Transaction = transaction;
            selectCommand.CommandText = """
                SELECT LookupCodeNormalized, ProductCode, ContentHash, LookupCode, UpdatedAt
                FROM LocalSellableItemCodeConflict
                WHERE StoreCode = $StoreCode
                ORDER BY LookupCodeNormalized, SortOrder;
                """;
            selectCommand.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existing.Add(string.Concat(
                    ReadString(reader, "LookupCodeNormalized"),
                    "|",
                    CreateCodeConflictComparisonKey(
                        ReadString(reader, "ProductCode"),
                        ReadString(reader, "ContentHash"),
                        ReadString(reader, "LookupCode"),
                        ReadNullableString(reader, "UpdatedAt"))));
            }
        }

        if (existing.SequenceEqual(incoming, StringComparer.Ordinal))
        {
            return false;
        }

        await ReplaceCodeConflictItemsCoreAsync(connection, transaction, normalizedStoreCode, materializedItems, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static string CreateCodeConflictComparisonKey(
        string productCode,
        string contentHash,
        string lookupCode,
        string? updatedAt)
    {
        // 内容摘要不含原始码与更新时间，这里补上，避免只改大小写或时间时漏写。
        return string.Join("|", productCode, contentHash, lookupCode, updatedAt ?? string.Empty);
    }

    private static async Task ReplaceCodeConflictItemsCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string normalizedStoreCode,
        IReadOnlyList<SellableItemDto> materializedItems,
        CancellationToken cancellationToken)
    {
        // 冲突候选按门店全量替换：服务端数据已修复的码必须随之消失，不能残留旧的备选商品。
        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = """
                DELETE FROM LocalSellableItemCodeConflict
                WHERE StoreCode = $StoreCode;
                """;
            deleteCommand.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var syncedAt = DateTimeOffset.UtcNow;
        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = InsertCodeConflictItemSql;
            AddUpsertParameters(insertCommand);
            insertCommand.Parameters.AddWithValue("$SortOrder", 0);
            insertCommand.Prepare();

            for (var index = 0; index < materializedItems.Count; index++)
            {
                var item = materializedItems[index];
                var lookupCodeNormalized = NormalizeLookupCode(item.LookupCode);
                if (!string.Equals(NormalizeStoreCode(item.StoreCode), normalizedStoreCode, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrEmpty(lookupCodeNormalized))
                {
                    continue;
                }

                var contentHash = CreateContentHash(item, normalizedStoreCode, lookupCodeNormalized);
                SetItemParameters(insertCommand, item, normalizedStoreCode, lookupCodeNormalized, contentHash, syncedAt);
                // 保留服务端决胜顺序，读取时按它排序。
                insertCommand.Parameters["$SortOrder"].Value = index;
                await insertCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    public async Task<string?> GetCatalogVersionAsync(
        string storeCode,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrEmpty(normalizedStoreCode))
        {
            return null;
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        return await ReadCatalogVersionAsync(command, normalizedStoreCode, cancellationToken);
    }

    public async Task ClearCatalogVersionAsync(
        string storeCode,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrEmpty(normalizedStoreCode))
        {
            return;
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        await DeleteCatalogVersionAsync(command, normalizedStoreCode, cancellationToken);
    }

    public async Task<LocalCatalogDeltaApplyResult> ApplyCatalogDeltaAsync(
        string storeCode,
        string baseCatalogVersion,
        string targetCatalogVersion,
        IReadOnlyList<SellableItemDto> upsertedItems,
        IReadOnlyList<string> deletedLookupCodes,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrEmpty(normalizedStoreCode))
        {
            throw new ArgumentException("Store code is required.", nameof(storeCode));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(baseCatalogVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetCatalogVersion);
        ArgumentNullException.ThrowIfNull(upsertedItems);
        ArgumentNullException.ThrowIfNull(deletedLookupCodes);

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        // 基准版本必须在同一事务里核对：期间若被全量替换或清掉版本，这批增量就不再适用。
        await using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.Transaction = transaction;
            var currentVersion = await ReadCatalogVersionAsync(versionCommand, normalizedStoreCode, cancellationToken);
            if (!string.Equals(currentVersion, baseCatalogVersion, StringComparison.Ordinal))
            {
                throw new LocalCatalogVersionConflictException(
                    $"Local catalog version changed before applying delta. expected={baseCatalogVersion} actual={currentVersion ?? "<none>"}");
            }
        }

        var syncedAt = DateTimeOffset.UtcNow;
        var upsertedCount = 0;
        if (upsertedItems.Count > 0)
        {
            await using var upsertCommand = connection.CreateCommand();
            upsertCommand.Transaction = transaction;
            upsertCommand.CommandText = UpsertSellableItemSql;
            AddUpsertParameters(upsertCommand);
            upsertCommand.Prepare();
            foreach (var item in upsertedItems)
            {
                var itemStoreCode = NormalizeStoreCode(item.StoreCode);
                var lookupCodeNormalized = NormalizeLookupCode(item.LookupCode);
                if (!string.Equals(itemStoreCode, normalizedStoreCode, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Delta item store code must match the target store.", nameof(upsertedItems));
                }

                if (string.IsNullOrEmpty(lookupCodeNormalized))
                {
                    throw new ArgumentException("Sellable item lookup code is required.", nameof(upsertedItems));
                }

                var contentHash = CreateContentHash(item, normalizedStoreCode, lookupCodeNormalized);
                SetItemParameters(upsertCommand, item, normalizedStoreCode, lookupCodeNormalized, contentHash, syncedAt);
                await upsertCommand.ExecuteNonQueryAsync(cancellationToken);
                upsertedCount++;
            }
        }

        var deletedCount = 0;
        var normalizedDeletes = deletedLookupCodes
            .Select(NormalizeLookupCode)
            .Where(code => !string.IsNullOrEmpty(code))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedDeletes.Length > 0)
        {
            await using var deleteCommand = connection.CreateCommand();
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = """
                DELETE FROM LocalSellableItemIndex
                WHERE StoreCode = $StoreCode
                  AND LookupCodeNormalized = $LookupCodeNormalized;
                """;
            deleteCommand.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
            deleteCommand.Parameters.AddWithValue("$LookupCodeNormalized", string.Empty);
            deleteCommand.Prepare();
            foreach (var lookupCodeNormalized in normalizedDeletes)
            {
                deleteCommand.Parameters["$LookupCodeNormalized"].Value = lookupCodeNormalized;
                deletedCount += await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        int localItemCount;
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.Transaction = transaction;
            countCommand.CommandText = """
                SELECT COUNT(*)
                FROM LocalSellableItemIndex
                WHERE StoreCode = $StoreCode;
                """;
            countCommand.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
            localItemCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        await using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.Transaction = transaction;
            await UpsertCatalogVersionAsync(versionCommand, normalizedStoreCode, targetCatalogVersion, localItemCount, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new LocalCatalogDeltaApplyResult(upsertedCount, deletedCount, localItemCount);
    }

    private static async Task<string?> ReadCatalogVersionAsync(
        SqliteCommand command,
        string normalizedStoreCode,
        CancellationToken cancellationToken)
    {
        command.CommandText = """
            SELECT CatalogVersion
            FROM LocalCatalogSyncState
            WHERE StoreCode = $StoreCode;
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string version && !string.IsNullOrWhiteSpace(version) ? version : null;
    }

    private static async Task UpsertCatalogVersionAsync(
        SqliteCommand command,
        string normalizedStoreCode,
        string catalogVersion,
        int itemCount,
        CancellationToken cancellationToken)
    {
        command.CommandText = """
            INSERT INTO LocalCatalogSyncState (StoreCode, CatalogVersion, ItemCount, UpdatedAt)
            VALUES ($StoreCode, $CatalogVersion, $ItemCount, $UpdatedAt)
            ON CONFLICT(StoreCode) DO UPDATE SET
                CatalogVersion = excluded.CatalogVersion,
                ItemCount = excluded.ItemCount,
                UpdatedAt = excluded.UpdatedAt;
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
        command.Parameters.AddWithValue("$CatalogVersion", catalogVersion);
        command.Parameters.AddWithValue("$ItemCount", itemCount);
        command.Parameters.AddWithValue("$UpdatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteCatalogVersionAsync(
        SqliteCommand command,
        string normalizedStoreCode,
        CancellationToken cancellationToken)
    {
        command.CommandText = """
            DELETE FROM LocalCatalogSyncState
            WHERE StoreCode = $StoreCode;
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SellableItemDto>> LoadCodeConflictItemsAsync(
        string storeCode,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrWhiteSpace(normalizedStoreCode))
        {
            return [];
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT StoreCode, ProductCode, ReferenceCode, DisplayName, LookupCode, ItemNumber, Barcode, ProductImage, DiscountRate, IsSpecialProduct, RetailPrice, PriceSource, PriceSourceLabel, QuantityFactor, UpdatedAt
            FROM LocalSellableItemCodeConflict
            WHERE StoreCode = $StoreCode
            ORDER BY LookupCodeNormalized, SortOrder;
            """;
        command.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);

        var items = new List<SellableItemDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadSellableItem(reader));
        }

        return items;
    }

    public async Task ReplacePromotionRulesAsync(
        string storeCode,
        IEnumerable<CatalogPromotionRuleDto> rules,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrEmpty(normalizedStoreCode))
        {
            throw new ArgumentException("Store code is required.", nameof(storeCode));
        }

        var materializedRules = rules.ToArray();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        // 促销规则按门店全量替换，确保后台删除或停用后本地缓存不会继续生效。
        await using (var deleteProductsCommand = connection.CreateCommand())
        {
            deleteProductsCommand.Transaction = transaction;
            deleteProductsCommand.CommandText = """
                DELETE FROM LocalPromotionProducts
                WHERE StoreCode = $StoreCode;
                """;
            deleteProductsCommand.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
            await deleteProductsCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteRulesCommand = connection.CreateCommand())
        {
            deleteRulesCommand.Transaction = transaction;
            deleteRulesCommand.CommandText = """
                DELETE FROM LocalPromotionRules
                WHERE StoreCode = $StoreCode;
                """;
            deleteRulesCommand.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
            await deleteRulesCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var syncedAt = DateTimeOffset.UtcNow.ToString("O");
        foreach (var rule in materializedRules)
        {
            var promotionId = NormalizePromotionId(rule.PromotionId);
            if (string.IsNullOrEmpty(promotionId))
            {
                continue;
            }

            await using (var insertRuleCommand = connection.CreateCommand())
            {
                insertRuleCommand.Transaction = transaction;
                insertRuleCommand.CommandText = """
                    INSERT INTO LocalPromotionRules
                    (
                        StoreCode,
                        PromotionId,
                        Name,
                        IsExclusive,
                        Priority,
                        ApplyQuantity,
                        FixedPrice,
                        MaxApplicationsPerOrder,
                        EffectiveStart,
                        EffectiveEnd,
                        UpdatedAt,
                        SyncedAt
                    )
                    VALUES
                    (
                        $StoreCode,
                        $PromotionId,
                        $Name,
                        $IsExclusive,
                        $Priority,
                        $ApplyQuantity,
                        $FixedPrice,
                        $MaxApplicationsPerOrder,
                        $EffectiveStart,
                        $EffectiveEnd,
                        $UpdatedAt,
                        $SyncedAt
                    );
                    """;
                insertRuleCommand.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
                insertRuleCommand.Parameters.AddWithValue("$PromotionId", promotionId);
                insertRuleCommand.Parameters.AddWithValue("$Name", rule.Name);
                insertRuleCommand.Parameters.AddWithValue("$IsExclusive", rule.IsExclusive ? 1 : 0);
                insertRuleCommand.Parameters.AddWithValue("$Priority", rule.Priority);
                insertRuleCommand.Parameters.AddWithValue("$ApplyQuantity", rule.ApplyQuantity);
                insertRuleCommand.Parameters.AddWithValue("$FixedPrice", rule.FixedPrice.ToString("0.#############################", CultureInfo.InvariantCulture));
                insertRuleCommand.Parameters.AddWithValue("$MaxApplicationsPerOrder", (object?)rule.MaxApplicationsPerOrder ?? DBNull.Value);
                insertRuleCommand.Parameters.AddWithValue("$EffectiveStart", rule.EffectiveStart.ToString("O"));
                insertRuleCommand.Parameters.AddWithValue("$EffectiveEnd", rule.EffectiveEnd.ToString("O"));
                insertRuleCommand.Parameters.AddWithValue("$UpdatedAt", (object?)rule.UpdatedAt?.ToString("O") ?? DBNull.Value);
                insertRuleCommand.Parameters.AddWithValue("$SyncedAt", syncedAt);
                await insertRuleCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var product in rule.Products
                .GroupBy(product => NormalizeProductCode(product.ProductCode), StringComparer.OrdinalIgnoreCase)
                .Where(group => !string.IsNullOrEmpty(group.Key))
                .Select(group => group.Last()))
            {
                var productCode = NormalizeProductCode(product.ProductCode);
                await using var insertProductCommand = connection.CreateCommand();
                insertProductCommand.Transaction = transaction;
                insertProductCommand.CommandText = """
                    INSERT INTO LocalPromotionProducts
                    (
                        StoreCode,
                        PromotionId,
                        ProductCode,
                        UnitWeight
                    )
                    VALUES
                    (
                        $StoreCode,
                        $PromotionId,
                        $ProductCode,
                        $UnitWeight
                    );
                    """;
                insertProductCommand.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
                insertProductCommand.Parameters.AddWithValue("$PromotionId", promotionId);
                insertProductCommand.Parameters.AddWithValue("$ProductCode", productCode);
                insertProductCommand.Parameters.AddWithValue("$UnitWeight", Math.Max(1, product.UnitWeight));
                await insertProductCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CatalogPromotionRuleDto>> LoadPromotionRulesAsync(
        string storeCode,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrWhiteSpace(normalizedStoreCode))
        {
            return [];
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        var productsByPromotion = new Dictionary<string, List<CatalogPromotionProductDto>>(StringComparer.Ordinal);
        await using (var productsCommand = connection.CreateCommand())
        {
            productsCommand.CommandText = """
                SELECT PromotionId, ProductCode, UnitWeight
                FROM LocalPromotionProducts
                WHERE StoreCode = $StoreCode
                ORDER BY PromotionId, ProductCode;
                """;
            productsCommand.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);
            await using var productReader = await productsCommand.ExecuteReaderAsync(cancellationToken);
            while (await productReader.ReadAsync(cancellationToken))
            {
                var promotionId = ReadString(productReader, "PromotionId");
                if (!productsByPromotion.TryGetValue(promotionId, out var products))
                {
                    products = [];
                    productsByPromotion[promotionId] = products;
                }

                products.Add(new CatalogPromotionProductDto(
                    ReadString(productReader, "ProductCode"),
                    Math.Max(1, ReadInt32(productReader, "UnitWeight"))));
            }
        }

        await using var rulesCommand = connection.CreateCommand();
        rulesCommand.CommandText = """
            SELECT
                PromotionId,
                Name,
                IsExclusive,
                Priority,
                ApplyQuantity,
                FixedPrice,
                MaxApplicationsPerOrder,
                EffectiveStart,
                EffectiveEnd,
                UpdatedAt
            FROM LocalPromotionRules
            WHERE StoreCode = $StoreCode
            ORDER BY IsExclusive DESC, Priority DESC, PromotionId;
            """;
        rulesCommand.Parameters.AddWithValue("$StoreCode", normalizedStoreCode);

        var rules = new List<CatalogPromotionRuleDto>();
        await using var ruleReader = await rulesCommand.ExecuteReaderAsync(cancellationToken);
        while (await ruleReader.ReadAsync(cancellationToken))
        {
            var promotionId = ReadString(ruleReader, "PromotionId");
            rules.Add(new CatalogPromotionRuleDto(
                promotionId,
                ReadString(ruleReader, "Name"),
                ReadBool(ruleReader, "IsExclusive"),
                ReadInt32(ruleReader, "Priority"),
                ReadInt32(ruleReader, "ApplyQuantity"),
                ReadDecimal(ruleReader, "FixedPrice"),
                ReadNullableInt32(ruleReader, "MaxApplicationsPerOrder"),
                ReadNullableDateTimeOffset(ruleReader, "EffectiveStart") ?? DateTimeOffset.MinValue,
                ReadNullableDateTimeOffset(ruleReader, "EffectiveEnd") ?? DateTimeOffset.MinValue,
                ReadNullableDateTimeOffset(ruleReader, "UpdatedAt"),
                productsByPromotion.TryGetValue(promotionId, out var products) ? products : []));
        }

        return rules;
    }

    public async Task<ILocalCatalogStoreReplaceSession> BeginStoreReplaceSessionAsync(
        string storeCode,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrEmpty(normalizedStoreCode))
        {
            throw new ArgumentException("Store code is required.", nameof(storeCode));
        }

        var connection = await store.OpenConnectionAsync(cancellationToken);
        try
        {
            var session = new LocalCatalogStoreReplaceSession(connection, normalizedStoreCode);
            await session.InitializeAsync(cancellationToken);
            return session;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private const string SelectSellableItemSql = """
        SELECT StoreCode, ProductCode, ReferenceCode, DisplayName, LookupCode, ItemNumber, Barcode, ProductImage, DiscountRate, IsSpecialProduct, RetailPrice, PriceSource, PriceSourceLabel, QuantityFactor, UpdatedAt
        FROM LocalSellableItemIndex
        """;

    private const string UpsertSellableItemSql = """
        INSERT INTO LocalSellableItemIndex
        (
            StoreCode,
            ProductCode,
            ReferenceCode,
            DisplayName,
            LookupCode,
            LookupCodeNormalized,
            ItemNumber,
            Barcode,
            ProductImage,
            DiscountRate,
            IsSpecialProduct,
            RetailPrice,
            PriceSource,
            PriceSourceLabel,
            QuantityFactor,
            UpdatedAt,
            ContentHash,
            SyncedAt
        )
        VALUES
        (
            $StoreCode,
            $ProductCode,
            $ReferenceCode,
            $DisplayName,
            $LookupCode,
            $LookupCodeNormalized,
            $ItemNumber,
            $Barcode,
            $ProductImage,
            $DiscountRate,
            $IsSpecialProduct,
            $RetailPrice,
            $PriceSource,
            $PriceSourceLabel,
            $QuantityFactor,
            $UpdatedAt,
            $ContentHash,
            $SyncedAt
        )
        ON CONFLICT(StoreCode, LookupCodeNormalized) DO UPDATE SET
            ProductCode = excluded.ProductCode,
            ReferenceCode = excluded.ReferenceCode,
            DisplayName = excluded.DisplayName,
            LookupCode = excluded.LookupCode,
            ItemNumber = excluded.ItemNumber,
            Barcode = excluded.Barcode,
            ProductImage = excluded.ProductImage,
            DiscountRate = excluded.DiscountRate,
            IsSpecialProduct = excluded.IsSpecialProduct,
            RetailPrice = excluded.RetailPrice,
            PriceSource = excluded.PriceSource,
            PriceSourceLabel = excluded.PriceSourceLabel,
            QuantityFactor = excluded.QuantityFactor,
            UpdatedAt = excluded.UpdatedAt,
            ContentHash = excluded.ContentHash,
            SyncedAt = excluded.SyncedAt;
        """;

    private const string InsertCodeConflictItemSql = """
        INSERT INTO LocalSellableItemCodeConflict
        (
            StoreCode,
            ProductCode,
            ReferenceCode,
            DisplayName,
            LookupCode,
            LookupCodeNormalized,
            ItemNumber,
            Barcode,
            ProductImage,
            DiscountRate,
            IsSpecialProduct,
            RetailPrice,
            PriceSource,
            PriceSourceLabel,
            QuantityFactor,
            UpdatedAt,
            ContentHash,
            SyncedAt,
            SortOrder
        )
        VALUES
        (
            $StoreCode,
            $ProductCode,
            $ReferenceCode,
            $DisplayName,
            $LookupCode,
            $LookupCodeNormalized,
            $ItemNumber,
            $Barcode,
            $ProductImage,
            $DiscountRate,
            $IsSpecialProduct,
            $RetailPrice,
            $PriceSource,
            $PriceSourceLabel,
            $QuantityFactor,
            $UpdatedAt,
            $ContentHash,
            $SyncedAt,
            $SortOrder
        )
        ON CONFLICT(StoreCode, LookupCodeNormalized, ProductCode) DO NOTHING;
        """;

    private const string StageSellableItemSql = """
        INSERT INTO TempLocalSellableItemIndexReplace
        (
            StoreCode,
            ProductCode,
            ReferenceCode,
            DisplayName,
            LookupCode,
            LookupCodeNormalized,
            ItemNumber,
            Barcode,
            ProductImage,
            DiscountRate,
            IsSpecialProduct,
            RetailPrice,
            PriceSource,
            PriceSourceLabel,
            QuantityFactor,
            UpdatedAt,
            ContentHash,
            SyncedAt
        )
        VALUES
        (
            $StoreCode,
            $ProductCode,
            $ReferenceCode,
            $DisplayName,
            $LookupCode,
            $LookupCodeNormalized,
            $ItemNumber,
            $Barcode,
            $ProductImage,
            $DiscountRate,
            $IsSpecialProduct,
            $RetailPrice,
            $PriceSource,
            $PriceSourceLabel,
            $QuantityFactor,
            $UpdatedAt,
            $ContentHash,
            $SyncedAt
        )
        ON CONFLICT(StoreCode, LookupCodeNormalized) DO UPDATE SET
            ProductCode = excluded.ProductCode,
            ReferenceCode = excluded.ReferenceCode,
            DisplayName = excluded.DisplayName,
            LookupCode = excluded.LookupCode,
            ItemNumber = excluded.ItemNumber,
            Barcode = excluded.Barcode,
            ProductImage = excluded.ProductImage,
            DiscountRate = excluded.DiscountRate,
            IsSpecialProduct = excluded.IsSpecialProduct,
            RetailPrice = excluded.RetailPrice,
            PriceSource = excluded.PriceSource,
            PriceSourceLabel = excluded.PriceSourceLabel,
            QuantityFactor = excluded.QuantityFactor,
            UpdatedAt = excluded.UpdatedAt,
            ContentHash = excluded.ContentHash,
            SyncedAt = excluded.SyncedAt;
        """;

    private static void AddUpsertParameters(SqliteCommand command)
    {
        command.Parameters.AddWithValue("$StoreCode", string.Empty);
        command.Parameters.AddWithValue("$ProductCode", string.Empty);
        command.Parameters.AddWithValue("$ReferenceCode", DBNull.Value);
        command.Parameters.AddWithValue("$DisplayName", string.Empty);
        command.Parameters.AddWithValue("$LookupCode", string.Empty);
        command.Parameters.AddWithValue("$LookupCodeNormalized", string.Empty);
        command.Parameters.AddWithValue("$ItemNumber", DBNull.Value);
        command.Parameters.AddWithValue("$Barcode", DBNull.Value);
        command.Parameters.AddWithValue("$ProductImage", DBNull.Value);
        command.Parameters.AddWithValue("$DiscountRate", DBNull.Value);
        command.Parameters.AddWithValue("$IsSpecialProduct", 0);
        command.Parameters.AddWithValue("$RetailPrice", 0m);
        command.Parameters.AddWithValue("$PriceSource", 0);
        command.Parameters.AddWithValue("$PriceSourceLabel", string.Empty);
        command.Parameters.AddWithValue("$QuantityFactor", 1m);
        command.Parameters.AddWithValue("$UpdatedAt", DBNull.Value);
        command.Parameters.AddWithValue("$ContentHash", string.Empty);
        command.Parameters.AddWithValue("$SyncedAt", string.Empty);
    }

    private static void SetItemParameters(
        SqliteCommand command,
        SellableItemDto item,
        string storeCode,
        string lookupCodeNormalized,
        string contentHash,
        DateTimeOffset syncedAt)
    {
        command.Parameters["$StoreCode"].Value = storeCode;
        command.Parameters["$ProductCode"].Value = item.ProductCode;
        command.Parameters["$ReferenceCode"].Value = (object?)item.ReferenceCode ?? DBNull.Value;
        command.Parameters["$DisplayName"].Value = item.DisplayName;
        command.Parameters["$LookupCode"].Value = item.LookupCode;
        command.Parameters["$LookupCodeNormalized"].Value = lookupCodeNormalized;
        command.Parameters["$ItemNumber"].Value = (object?)item.ItemNumber ?? DBNull.Value;
        command.Parameters["$Barcode"].Value = (object?)item.Barcode ?? DBNull.Value;
        command.Parameters["$ProductImage"].Value = (object?)item.ProductImage ?? DBNull.Value;
        command.Parameters["$DiscountRate"].Value = (object?)item.DiscountRate ?? DBNull.Value;
        command.Parameters["$IsSpecialProduct"].Value = item.IsSpecialProduct ? 1 : 0;
        command.Parameters["$RetailPrice"].Value = item.RetailPrice;
        command.Parameters["$PriceSource"].Value = (int)item.PriceSource;
        command.Parameters["$PriceSourceLabel"].Value = item.PriceSourceLabel;
        command.Parameters["$QuantityFactor"].Value = item.QuantityFactor;
        command.Parameters["$UpdatedAt"].Value = (object?)item.UpdatedAt?.ToString("O") ?? DBNull.Value;
        command.Parameters["$ContentHash"].Value = contentHash;
        command.Parameters["$SyncedAt"].Value = syncedAt.ToString("O");
    }

    private static SellableItemDto ReadSellableItem(SqliteDataReader reader)
    {
        return new SellableItemDto(
            ReadString(reader, "StoreCode"),
            ReadString(reader, "ProductCode"),
            ReadNullableString(reader, "ReferenceCode"),
            ReadString(reader, "DisplayName"),
            ReadString(reader, "LookupCode"),
            ReadNullableString(reader, "ItemNumber"),
            ReadNullableString(reader, "Barcode"),
            ReadDecimal(reader, "RetailPrice"),
            (PriceSourceKind)ReadInt32(reader, "PriceSource"),
            ReadString(reader, "PriceSourceLabel"),
            ReadDecimal(reader, "QuantityFactor"),
            ReadNullableDateTimeOffset(reader, "UpdatedAt"),
            ReadNullableString(reader, "ProductImage"),
            ReadNullableDecimal(reader, "DiscountRate"),
            ReadBool(reader, "IsSpecialProduct"));
    }

    private static string CreateContentHash(SellableItemDto item, string storeCode, string lookupCodeNormalized)
    {
        var builder = new StringBuilder();
        AppendCanonical(builder, storeCode);
        AppendCanonical(builder, item.ProductCode.Trim());
        AppendCanonical(builder, item.ReferenceCode?.Trim() ?? string.Empty);
        AppendCanonical(builder, item.DisplayName.Trim());
        AppendCanonical(builder, lookupCodeNormalized);
        AppendCanonical(builder, item.ItemNumber?.Trim() ?? string.Empty);
        AppendCanonical(builder, item.Barcode?.Trim() ?? string.Empty);
        AppendCanonical(builder, item.RetailPrice.ToString("0.#############################", CultureInfo.InvariantCulture));
        AppendCanonical(builder, ((int)item.PriceSource).ToString(CultureInfo.InvariantCulture));
        AppendCanonical(builder, item.PriceSourceLabel.Trim());
        AppendCanonical(builder, item.QuantityFactor.ToString("0.#############################", CultureInfo.InvariantCulture));
        AppendCanonical(builder, item.ProductImage ?? string.Empty);
        AppendCanonical(builder, FormatNullableDecimal(item.DiscountRate));
        AppendCanonical(builder, item.IsSpecialProduct ? "1" : "0");

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hashBytes);
    }

    private static void AppendCanonical(StringBuilder builder, string value)
    {
        builder
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('|');
    }

    private static string NormalizeStoreCode(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static string NormalizeProductCode(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static string NormalizePromotionId(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static string NormalizeLookupCode(string? value)
    {
        return (value ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static string ReadString(SqliteDataReader reader, string name)
    {
        return reader.GetString(reader.GetOrdinal(name));
    }

    private static string? ReadNullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static int ReadInt32(SqliteDataReader reader, string name)
    {
        var value = reader.GetValue(reader.GetOrdinal(name));
        return value switch
        {
            int intValue => intValue,
            long longValue => (int)longValue,
            string stringValue => int.Parse(stringValue, CultureInfo.InvariantCulture),
            _ => Convert.ToInt32(value, CultureInfo.InvariantCulture)
        };
    }

    private static int? ReadNullableInt32(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            int intValue => intValue,
            long longValue => (int)longValue,
            string stringValue when string.IsNullOrWhiteSpace(stringValue) => null,
            string stringValue => int.Parse(stringValue, CultureInfo.InvariantCulture),
            _ => Convert.ToInt32(value, CultureInfo.InvariantCulture)
        };
    }

    private static bool ReadBool(SqliteDataReader reader, string name)
    {
        var value = reader.GetValue(reader.GetOrdinal(name));
        return value switch
        {
            bool boolValue => boolValue,
            int intValue => intValue != 0,
            long longValue => longValue != 0,
            string stringValue when int.TryParse(stringValue, CultureInfo.InvariantCulture, out var parsed) => parsed != 0,
            string stringValue => bool.Parse(stringValue),
            _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture)
        };
    }

    private static decimal ReadDecimal(SqliteDataReader reader, string name)
    {
        var value = reader.GetValue(reader.GetOrdinal(name));
        return value switch
        {
            decimal decimalValue => decimalValue,
            double doubleValue => Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture),
            long longValue => longValue,
            int intValue => intValue,
            string stringValue => decimal.Parse(stringValue, CultureInfo.InvariantCulture),
            _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture)
        };
    }

    private static decimal? ReadNullableDecimal(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            decimal decimalValue => decimalValue,
            double doubleValue => Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture),
            long longValue => longValue,
            int intValue => intValue,
            string stringValue when string.IsNullOrWhiteSpace(stringValue) => null,
            string stringValue => decimal.Parse(stringValue, CultureInfo.InvariantCulture),
            _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture)
        };
    }

    private static string FormatNullableDecimal(decimal? value)
    {
        return value?.ToString("0.#############################", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqliteDataReader reader, string name)
    {
        var value = ReadNullableString(reader, name);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    private static int PreferredSpecialLookupRank(SellableItemDto item)
    {
        if (!string.IsNullOrWhiteSpace(item.Barcode) &&
            string.Equals(NormalizeLookupCode(item.LookupCode), NormalizeLookupCode(item.Barcode), StringComparison.Ordinal))
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(item.ItemNumber) &&
            string.Equals(NormalizeLookupCode(item.LookupCode), NormalizeLookupCode(item.ItemNumber), StringComparison.Ordinal))
        {
            return 1;
        }

        return 2;
    }

    private sealed class LocalCatalogStoreReplaceSession(
        SqliteConnection connection,
        string storeCode) : ILocalCatalogStoreReplaceSession
    {
        private bool _committed;
        private bool _disposed;

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TEMP TABLE TempLocalSellableItemIndexReplace (
                    StoreCode TEXT NOT NULL,
                    ProductCode TEXT NOT NULL,
                    ReferenceCode TEXT NULL,
                    DisplayName TEXT NOT NULL,
                    LookupCode TEXT NOT NULL,
                    LookupCodeNormalized TEXT NOT NULL,
                    ItemNumber TEXT NULL,
                    Barcode TEXT NULL,
                    ProductImage TEXT NULL,
                    DiscountRate TEXT NULL,
                    IsSpecialProduct INTEGER NOT NULL DEFAULT 0,
                    RetailPrice TEXT NOT NULL,
                    PriceSource INTEGER NOT NULL,
                    PriceSourceLabel TEXT NOT NULL,
                    QuantityFactor TEXT NOT NULL,
                    UpdatedAt TEXT NULL,
                    ContentHash TEXT NOT NULL,
                    SyncedAt TEXT NOT NULL,
                    PRIMARY KEY (StoreCode, LookupCodeNormalized)
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task StageAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (_committed)
            {
                throw new InvalidOperationException("The store replace session has already been committed.");
            }

            var materializedItems = items.ToList();
            if (materializedItems.Count == 0)
            {
                return;
            }

            using var transaction = connection.BeginTransaction();
            var syncedAt = DateTimeOffset.UtcNow;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = StageSellableItemSql;
            AddUpsertParameters(command);
            command.Prepare();

            foreach (var item in materializedItems)
            {
                var itemStoreCode = NormalizeStoreCode(item.StoreCode);
                var lookupCodeNormalized = NormalizeLookupCode(item.LookupCode);
                if (!string.Equals(itemStoreCode, storeCode, StringComparison.Ordinal))
                {
                    throw new ArgumentException("Staged sellable item store code must match the replace session store.", nameof(items));
                }

                if (string.IsNullOrEmpty(lookupCodeNormalized))
                {
                    throw new ArgumentException("Sellable item lookup code is required.", nameof(items));
                }

                var contentHash = CreateContentHash(item, itemStoreCode, lookupCodeNormalized);
                SetItemParameters(command, item, itemStoreCode, lookupCodeNormalized, contentHash, syncedAt);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        public Task<LocalCatalogStoreReplaceCommitResult> CommitAsync(CancellationToken cancellationToken = default)
        {
            return CommitCoreAsync(versionStamp: null, cancellationToken);
        }

        public Task<LocalCatalogStoreReplaceCommitResult> CommitAsync(
            LocalCatalogVersionStamp versionStamp,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(versionStamp);
            ArgumentException.ThrowIfNullOrWhiteSpace(versionStamp.CatalogVersion);
            return CommitCoreAsync(versionStamp, cancellationToken);
        }

        private async Task<LocalCatalogStoreReplaceCommitResult> CommitCoreAsync(
            LocalCatalogVersionStamp? versionStamp,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (_committed)
            {
                throw new InvalidOperationException("The store replace session has already been committed.");
            }

            using var transaction = connection.BeginTransaction();
            var deletedCount = await CountDeletedLookupsAsync(transaction, cancellationToken);
            var insertedCount = await CountStagedItemsAsync(transaction, cancellationToken);
            if (versionStamp is not null && insertedCount != versionStamp.ExpectedItemCount)
            {
                // 暂存条数与服务端版本总数不符说明下载不完整或有重复码，保留旧目录不做替换。
                throw new LocalCatalogVersionConflictException(
                    $"Staged catalog item count does not match the pinned version. version={versionStamp.CatalogVersion} expected={versionStamp.ExpectedItemCount} staged={insertedCount}");
            }

            await using (var deleteCommand = connection.CreateCommand())
            {
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = """
                    DELETE FROM LocalSellableItemIndex
                    WHERE StoreCode = $StoreCode;
                    """;
                deleteCommand.Parameters.AddWithValue("$StoreCode", storeCode);
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var insertCommand = connection.CreateCommand())
            {
                insertCommand.Transaction = transaction;
                insertCommand.CommandText = """
                    INSERT INTO LocalSellableItemIndex
                    (
                        StoreCode,
                        ProductCode,
                        ReferenceCode,
                        DisplayName,
                        LookupCode,
                        LookupCodeNormalized,
                        ItemNumber,
                        Barcode,
                        ProductImage,
                        DiscountRate,
                        IsSpecialProduct,
                        RetailPrice,
                        PriceSource,
                        PriceSourceLabel,
                        QuantityFactor,
                        UpdatedAt,
                        ContentHash,
                        SyncedAt
                    )
                    SELECT
                        StoreCode,
                        ProductCode,
                        ReferenceCode,
                        DisplayName,
                        LookupCode,
                        LookupCodeNormalized,
                        ItemNumber,
                        Barcode,
                        ProductImage,
                        DiscountRate,
                        IsSpecialProduct,
                        RetailPrice,
                        PriceSource,
                        PriceSourceLabel,
                        QuantityFactor,
                        UpdatedAt,
                        ContentHash,
                        SyncedAt
                    FROM TempLocalSellableItemIndexReplace
                    WHERE StoreCode = $StoreCode;
                    """;
                insertCommand.Parameters.AddWithValue("$StoreCode", storeCode);
                await insertCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var versionCommand = connection.CreateCommand())
            {
                versionCommand.Transaction = transaction;
                if (versionStamp is null)
                {
                    // 未锁定版本的旧协议全量替换不对应任何已知版本，下次同步必须重新全量。
                    await DeleteCatalogVersionAsync(versionCommand, storeCode, cancellationToken);
                }
                else
                {
                    await UpsertCatalogVersionAsync(versionCommand, storeCode, versionStamp.CatalogVersion, insertedCount, cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            _committed = true;
            return new LocalCatalogStoreReplaceCommitResult(insertedCount, deletedCount);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await connection.DisposeAsync();
        }

        private async Task<int> CountDeletedLookupsAsync(
            SqliteTransaction transaction,
            CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT COUNT(*)
                FROM LocalSellableItemIndex l
                WHERE l.StoreCode = $StoreCode
                  AND NOT EXISTS (
                      SELECT 1
                      FROM TempLocalSellableItemIndexReplace s
                      WHERE s.StoreCode = l.StoreCode
                        AND s.LookupCodeNormalized = l.LookupCodeNormalized
                  );
                """;
            command.Parameters.AddWithValue("$StoreCode", storeCode);
            return await ExecuteScalarInt32Async(command, cancellationToken);
        }

        private async Task<int> CountStagedItemsAsync(
            SqliteTransaction transaction,
            CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT COUNT(*)
                FROM TempLocalSellableItemIndexReplace
                WHERE StoreCode = $StoreCode;
                """;
            command.Parameters.AddWithValue("$StoreCode", storeCode);
            return await ExecuteScalarInt32Async(command, cancellationToken);
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        private static async Task<int> ExecuteScalarInt32Async(
            SqliteCommand command,
            CancellationToken cancellationToken)
        {
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value switch
            {
                int intValue => intValue,
                long longValue => (int)longValue,
                null or DBNull => 0,
                _ => Convert.ToInt32(value, CultureInfo.InvariantCulture)
            };
        }
    }
}
