using System.Data;
using System.Diagnostics;
using System.Globalization;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using Hbpos.Api.Data;
using Hbpos.Contracts.Catalog;
using Microsoft.Data.SqlClient;
using SqlSugar;

namespace Hbpos.Api.Services;

/// <summary>
/// 分店级「特殊商品分类」：收银端（WPF / iPad）读取分类快照、把特殊商品移到某分类、调整分类内顺序。
/// 分类本身的新建 / 改名 / 删除 / 排序 / 同步只在主后端（手机 App）里做，这里不提供。
/// 所有读取都实时读库、不经过目录缓存；写入只动 StoreSpecialProductCategoryItem，不碰 StoreRetailPrice，
/// 因此不需要也不应使目录缓存失效。
/// </summary>
public interface ISpecialProductCategoryService
{
    /// <summary>本店全部分类与「当前仍是特殊商品」的归属快照；门店不存在或已停用返回 null。</summary>
    Task<CatalogSpecialProductCategoriesResponse?> GetSnapshotAsync(
        string storeCode,
        CancellationToken cancellationToken);

    /// <summary>把特殊商品移到某分类（追加到末尾）或移出分类；成功返回最新全量快照。</summary>
    Task<SpecialProductCategoryServiceResult> AssignAsync(
        CatalogSpecialProductCategoryAssignRequest request,
        string updatedBy,
        CancellationToken cancellationToken);

    /// <summary>重排某分类内的商品顺序；成功返回最新全量快照。</summary>
    Task<SpecialProductCategoryServiceResult> ReorderAsync(
        CatalogSpecialProductCategoryOrderRequest request,
        string updatedBy,
        CancellationToken cancellationToken);
}

/// <summary>分类写操作的统一结果；失败时 ErrorCode 是稳定的大写英文码，控制器据此映射 HTTP 状态。</summary>
public sealed record SpecialProductCategoryServiceResult(
    bool Success,
    CatalogSpecialProductCategoriesResponse? Response,
    string? ErrorCode,
    string? Message)
{
    public static SpecialProductCategoryServiceResult Ok(CatalogSpecialProductCategoriesResponse response) =>
        new(true, response, null, null);

    public static SpecialProductCategoryServiceResult Fail(string errorCode, string message) =>
        new(false, null, errorCode, message);
}

/// <summary>分类相关的稳定错误码。</summary>
public static class SpecialProductCategoryErrorCodes
{
    public const string StoreCodeRequired = "STORE_CODE_REQUIRED";
    public const string ProductCodeRequired = "PRODUCT_CODE_REQUIRED";
    public const string CategoryIdRequired = "CATEGORY_ID_REQUIRED";
    public const string StoreNotFound = "STORE_NOT_FOUND";
    public const string CategoryNotFound = "SPECIAL_PRODUCT_CATEGORY_NOT_FOUND";
    public const string NotSpecial = "SPECIAL_PRODUCT_NOT_SPECIAL";
    public const string OrderConflict = "SPECIAL_PRODUCT_CATEGORY_ORDER_CONFLICT";
    public const string AssignConflict = "SPECIAL_PRODUCT_CATEGORY_ASSIGN_CONFLICT";
}

public sealed class SpecialProductCategoryService(HbposSqlSugarContext dbContext) : ISpecialProductCategoryService
{
    // 特殊商品编码 IN 查询的分块大小：SQL Server 单条命令最多 2100 个参数，留足余量（与 CatalogService 同值）。
    private const int InQueryChunkSize = 500;

    // assign 遇到主键冲突时的最多尝试次数（首次 + 重试一次）。
    private const int MaxAssignAttempts = 2;

    /// <summary>仅供测试：在 assign 即将 INSERT 归属行之前触发（参数为第几次尝试，从 0 开始），用来模拟并发抢先写入。</summary>
    internal Func<int, Task>? BeforeInsertForTesting { get; set; }

    public async Task<CatalogSpecialProductCategoriesResponse?> GetSnapshotAsync(
        string storeCode,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var normalizedStoreCode = Normalize(storeCode);
        var response = await RunSnapshotReadAsync(
            async token =>
            {
                if (!await StoreExistsAsync(normalizedStoreCode, token))
                {
                    return null;
                }

                return await BuildSnapshotAsync(normalizedStoreCode, token);
            },
            cancellationToken);
        Log($"categories snapshot store={normalizedStoreCode} found={response is not null} categories={response?.Categories.Count.ToString(CultureInfo.InvariantCulture) ?? "<null>"} assignments={response?.Assignments.Count.ToString(CultureInfo.InvariantCulture) ?? "<null>"} elapsedMs={stopwatch.ElapsedMilliseconds}");
        return response;
    }

    public async Task<SpecialProductCategoryServiceResult> AssignAsync(
        CatalogSpecialProductCategoryAssignRequest request,
        string updatedBy,
        CancellationToken cancellationToken)
    {
        var storeCode = Normalize(request.StoreCode);
        var productCode = Normalize(request.ProductCode);
        // categoryId 为 null / 空白 = 移出分类（回到「未分类」）。
        var categoryId = string.IsNullOrWhiteSpace(request.CategoryId) ? null : request.CategoryId.Trim();
        if (storeCode.Length == 0)
        {
            return SpecialProductCategoryServiceResult.Fail(
                SpecialProductCategoryErrorCodes.StoreCodeRequired, "storeCode is required");
        }

        if (productCode.Length == 0)
        {
            return SpecialProductCategoryServiceResult.Fail(
                SpecialProductCategoryErrorCodes.ProductCodeRequired, "productCode is required");
        }

        if (!await StoreExistsAsync(storeCode, cancellationToken))
        {
            return SpecialProductCategoryServiceResult.Fail(
                SpecialProductCategoryErrorCodes.StoreNotFound, "store was not found or inactive");
        }

        var actor = ResolveActor(updatedBy);
        var stopwatch = Stopwatch.StartNew();

        // 并发保护：两台收银机同时把同一个未归类商品放进分类时，两边都读到「无归属行」，
        // 第二个 INSERT 会撞主键 (StoreCode, ProductCode)。撞键后整体回滚并重来一次——
        // 重读时已能看到对方提交的行，于是改走 UPDATE；重试仍撞键才返回 409，让客户端刷新快照。
        for (var attempt = 0; attempt < MaxAssignAttempts; attempt++)
        {
            try
            {
                var failure = await AssignOnceAsync(storeCode, productCode, categoryId, actor, attempt, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }

                break;
            }
            catch (Exception ex) when (IsDuplicateKeyViolation(ex))
            {
                Log($"assign duplicate key store={storeCode} product={productCode} category={categoryId ?? "<none>"} attempt={attempt + 1} error={ex.Message}");
                if (attempt == MaxAssignAttempts - 1)
                {
                    return SpecialProductCategoryServiceResult.Fail(
                        SpecialProductCategoryErrorCodes.AssignConflict,
                        "the product was changed by another device at the same time; refresh and retry");
                }
            }
        }

        var snapshot = await GetSnapshotAsync(storeCode, cancellationToken);
        Log($"assign completed store={storeCode} product={productCode} category={categoryId ?? "<none>"} actor={actor} elapsedMs={stopwatch.ElapsedMilliseconds}");
        return snapshot is null
            ? SpecialProductCategoryServiceResult.Fail(
                SpecialProductCategoryErrorCodes.StoreNotFound, "store was not found or inactive")
            : SpecialProductCategoryServiceResult.Ok(snapshot);
    }

    /// <summary>
    /// assign 的单次事务尝试：成功提交返回 null，业务拒绝返回对应失败结果（已回滚）；
    /// 任何异常（包括主键冲突）先回滚再原样抛出，由调用方决定是否重试。
    /// </summary>
    private async Task<SpecialProductCategoryServiceResult?> AssignOnceAsync(
        string storeCode,
        string productCode,
        string? categoryId,
        string actor,
        int attempt,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var db = dbContext.MainDb;
        await db.Ado.BeginTranAsync();
        try
        {
            // 商品当前必须就是本店特殊商品（与特殊商品分页同口径）；移出分类同样要求，保持语义一致。
            var specialCodes = await LoadCurrentSpecialProductCodesAsync(storeCode, cancellationToken);
            if (!specialCodes.TryGetValue(productCode, out var canonicalProductCode))
            {
                await db.Ado.RollbackTranAsync();
                return SpecialProductCategoryServiceResult.Fail(
                    SpecialProductCategoryErrorCodes.NotSpecial,
                    "product is not an active special product in this store");
            }

            // 分类必须属于本店：他店分类按「不存在」处理，不泄露其他分店的分类是否存在。
            StoreSpecialProductCategory? category = null;
            if (categoryId is not null)
            {
                category = await db.Queryable<StoreSpecialProductCategory>()
                    .With(SqlWith.Null)
                    .FirstAsync(x => x.Guid == categoryId && x.StoreCode == storeCode && !x.IsDeleted, cancellationToken);
                if (category is null)
                {
                    await db.Ado.RollbackTranAsync();
                    return SpecialProductCategoryServiceResult.Fail(
                        SpecialProductCategoryErrorCodes.CategoryNotFound,
                        "special product category was not found in this store");
                }
            }

            var existing = await db.Queryable<StoreSpecialProductCategoryItem>()
                .With(SqlWith.Null)
                .FirstAsync(x => x.StoreCode == storeCode && x.ProductCode == canonicalProductCode, cancellationToken);

            if (category is null)
            {
                // 移出分类：删归属行（没有行即已经是未分类，幂等）。
                if (existing is not null)
                {
                    var existingCode = existing.ProductCode;
                    await db.Deleteable<StoreSpecialProductCategoryItem>()
                        .Where(x => x.StoreCode == storeCode && x.ProductCode == existingCode)
                        .ExecuteCommandAsync();
                }
            }
            else if (existing is null || !string.Equals(existing.CategoryGuid, category.Guid, StringComparison.Ordinal))
            {
                // 移入分类：追加到该分类「当前仍是特殊商品」的商品之后（最大序号 + 1，空分类从 0 开始）。
                var categoryRows = await db.Queryable<StoreSpecialProductCategoryItem>()
                    .With(SqlWith.Null)
                    .Where(x => x.StoreCode == storeCode && x.CategoryGuid == category.Guid)
                    .ToListAsync(cancellationToken);
                var nextSortOrder = categoryRows
                    .Where(x => specialCodes.ContainsKey(x.ProductCode))
                    .Select(x => x.SortOrder + 1)
                    .DefaultIfEmpty(0)
                    .Max();

                if (existing is null)
                {
                    if (BeforeInsertForTesting is not null)
                    {
                        await BeforeInsertForTesting(attempt);
                    }

                    await db.Insertable(new StoreSpecialProductCategoryItem
                    {
                        StoreCode = storeCode,
                        ProductCode = canonicalProductCode,
                        CategoryGuid = category.Guid,
                        SortOrder = nextSortOrder,
                        CreatedAt = now,
                        CreatedBy = actor,
                        UpdatedAt = now,
                        UpdatedBy = actor,
                        IsDeleted = false
                    }).ExecuteCommandAsync();
                }
                else
                {
                    var targetGuid = category.Guid;
                    var existingCode = existing.ProductCode;
                    await db.Updateable<StoreSpecialProductCategoryItem>()
                        .SetColumns(x => new StoreSpecialProductCategoryItem
                        {
                            CategoryGuid = targetGuid,
                            SortOrder = nextSortOrder,
                            UpdatedAt = now,
                            UpdatedBy = actor
                        })
                        .Where(x => x.StoreCode == storeCode && x.ProductCode == existingCode)
                        .ExecuteCommandAsync();
                }
            }
            // else：已经在目标分类里，保持原位置不动（幂等，也避免无意义地把商品挪到末尾）。

            await db.Ado.CommitTranAsync();
            return null;
        }
        catch (Exception ex)
        {
            await db.Ado.RollbackTranAsync();
            Log($"assign failed store={storeCode} product={productCode} category={categoryId ?? "<none>"} attempt={attempt + 1} error={ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 是否唯一 / 主键冲突：SQL Server 2627（主键 / 唯一约束）与 2601（唯一索引）；
    /// 另兼容单元测试用的 SQLite 替身（Microsoft.Data.Sqlite 的 "constraint failed" 异常，按类型名识别以免额外引用）。
    /// </summary>
    internal static bool IsDuplicateKeyViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is SqlException sqlException && sqlException.Number is 2627 or 2601)
            {
                return true;
            }

            if (current.GetType().Name == "SqliteException" &&
                current.Message.Contains("constraint failed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (current.InnerException is null)
            {
                break;
            }
        }

        return false;
    }

    public async Task<SpecialProductCategoryServiceResult> ReorderAsync(
        CatalogSpecialProductCategoryOrderRequest request,
        string updatedBy,
        CancellationToken cancellationToken)
    {
        var storeCode = Normalize(request.StoreCode);
        var categoryId = Normalize(request.CategoryId);
        if (storeCode.Length == 0)
        {
            return SpecialProductCategoryServiceResult.Fail(
                SpecialProductCategoryErrorCodes.StoreCodeRequired, "storeCode is required");
        }

        if (categoryId.Length == 0)
        {
            return SpecialProductCategoryServiceResult.Fail(
                SpecialProductCategoryErrorCodes.CategoryIdRequired, "categoryId is required");
        }

        if (!await StoreExistsAsync(storeCode, cancellationToken))
        {
            return SpecialProductCategoryServiceResult.Fail(
                SpecialProductCategoryErrorCodes.StoreNotFound, "store was not found or inactive");
        }

        var requestedCodes = (request.ProductCodes ?? []).Select(Normalize).ToArray();
        var actor = ResolveActor(updatedBy);
        var now = DateTime.UtcNow;
        var db = dbContext.MainDb;
        var stopwatch = Stopwatch.StartNew();
        await db.Ado.BeginTranAsync();
        try
        {
            var category = await db.Queryable<StoreSpecialProductCategory>()
                .With(SqlWith.Null)
                .FirstAsync(x => x.Guid == categoryId && x.StoreCode == storeCode && !x.IsDeleted, cancellationToken);
            if (category is null)
            {
                await db.Ado.RollbackTranAsync();
                return SpecialProductCategoryServiceResult.Fail(
                    SpecialProductCategoryErrorCodes.CategoryNotFound,
                    "special product category was not found in this store");
            }

            // 该分类当前「仍是特殊商品」的归属行：已取消特殊标记的商品不算，也不参与重排。
            var specialCodes = await LoadCurrentSpecialProductCodesAsync(storeCode, cancellationToken);
            var currentRows = (await db.Queryable<StoreSpecialProductCategoryItem>()
                    .With(SqlWith.Null)
                    .Where(x => x.StoreCode == storeCode && x.CategoryGuid == category.Guid)
                    .ToListAsync(cancellationToken))
                .Where(x => specialCodes.ContainsKey(x.ProductCode))
                .ToList();

            // 请求必须恰好是当前全部特殊商品的一个排列：缺一个、多一个或重复都说明数据已被别处改过，
            // 拒绝并让客户端先刷新（不做「尽力而为」的部分重排，避免顺序悄悄错位）。
            var requestedSet = new HashSet<string>(requestedCodes, StringComparer.OrdinalIgnoreCase);
            var currentSet = new HashSet<string>(currentRows.Select(x => x.ProductCode), StringComparer.OrdinalIgnoreCase);
            if (requestedCodes.Length != currentRows.Count ||
                requestedSet.Count != requestedCodes.Length ||
                !requestedSet.SetEquals(currentSet))
            {
                await db.Ado.RollbackTranAsync();
                return SpecialProductCategoryServiceResult.Fail(
                    SpecialProductCategoryErrorCodes.OrderConflict,
                    "product list does not match the current products of this category; refresh and retry");
            }

            // 序号写成 0..n-1 连续整数；只更新序号真正变化的行。
            var rowsByCode = currentRows.ToDictionary(x => x.ProductCode, StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < requestedCodes.Length; index++)
            {
                var row = rowsByCode[requestedCodes[index]];
                if (row.SortOrder == index)
                {
                    continue;
                }

                var sortOrder = index;
                var rowCode = row.ProductCode;
                await db.Updateable<StoreSpecialProductCategoryItem>()
                    .SetColumns(x => new StoreSpecialProductCategoryItem
                    {
                        SortOrder = sortOrder,
                        UpdatedAt = now,
                        UpdatedBy = actor
                    })
                    .Where(x => x.StoreCode == storeCode && x.ProductCode == rowCode)
                    .ExecuteCommandAsync();
            }

            await db.Ado.CommitTranAsync();
        }
        catch (Exception ex)
        {
            await db.Ado.RollbackTranAsync();
            Log($"reorder failed store={storeCode} category={categoryId} error={ex.Message}");
            throw;
        }

        var snapshot = await GetSnapshotAsync(storeCode, cancellationToken);
        Log($"reorder completed store={storeCode} category={categoryId} items={requestedCodes.Length} actor={actor} elapsedMs={stopwatch.ElapsedMilliseconds}");
        return snapshot is null
            ? SpecialProductCategoryServiceResult.Fail(
                SpecialProductCategoryErrorCodes.StoreNotFound, "store was not found or inactive")
            : SpecialProductCategoryServiceResult.Ok(snapshot);
    }

    private async Task<bool> StoreExistsAsync(string storeCode, CancellationToken cancellationToken)
    {
        // With(SqlWith.Null) 去掉全局 NOLOCK 表提示：在 SNAPSHOT / 写事务里它会覆盖隔离级别。
        return await dbContext.MainDb.Queryable<Store>()
            .With(SqlWith.Null)
            .AnyAsync(x => x.StoreCode == storeCode && x.IsActive && !x.IsDeleted, cancellationToken);
    }

    /// <summary>
    /// 本店「当前仍是特殊商品」的商品编码（键不区分大小写，值为门店价里的规范写法）。
    /// 口径与特殊商品分页一致：只看启用且未删除的门店价行，同一商品取最新一行（更新时间，缺失取创建时间，
    /// 并列按 UUID 升序），该行带特殊标记才算（旧行是特殊、新行不是 = 不是特殊商品）。
    /// 与分页不同的一点：不做「同码决胜」（商品的查询码被他品清货码 / 多码 / 套装码压过时，分页里不出现，
    /// 这里仍算特殊商品）——这只会让极少数在目录里不可扫的商品多保留一个归属，不影响分类展示。
    /// </summary>
    private async Task<Dictionary<string, string>> LoadCurrentSpecialProductCodesAsync(
        string storeCode,
        CancellationToken cancellationToken)
    {
        var db = dbContext.MainDb;

        // 第 1 步：带特殊标记的商品编码（超集）。
        var flaggedCodes = (await db.Queryable<StoreRetailPrice>()
                .With(SqlWith.Null)
                .Where(x =>
                    x.StoreCode == storeCode &&
                    x.IsActive &&
                    !x.IsDeleted &&
                    x.ProductCode != null &&
                    x.IsSpecialProduct)
                .Select(x => x.ProductCode)
                .ToListAsync(cancellationToken))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (flaggedCodes.Length == 0)
        {
            return result;
        }

        // 第 2 步：这些商品全部有效门店价行，按「最新一行是否带特殊标记」裁决。
        var latestByCode = new Dictionary<string, StoreRetailPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in flaggedCodes.Chunk(InQueryChunkSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = await db.Queryable<StoreRetailPrice>()
                .With(SqlWith.Null)
                .Where(x =>
                    x.StoreCode == storeCode &&
                    x.IsActive &&
                    !x.IsDeleted &&
                    x.ProductCode != null &&
                    chunk.Contains(x.ProductCode))
                .Select(x => new StoreRetailPrice
                {
                    UUID = x.UUID,
                    ProductCode = x.ProductCode,
                    IsSpecialProduct = x.IsSpecialProduct,
                    UpdatedAt = x.UpdatedAt,
                    CreatedAt = x.CreatedAt
                })
                .ToListAsync(cancellationToken);
            foreach (var group in rows.GroupBy(x => x.ProductCode!.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                var latest = group
                    .OrderByDescending(x => x.UpdatedAt ?? x.CreatedAt)
                    .ThenBy(x => x.UUID, StringComparer.Ordinal)
                    .First();
                latestByCode[group.Key] = latest;
            }
        }

        foreach (var (code, latest) in latestByCode)
        {
            if (latest.IsSpecialProduct)
            {
                result[code] = code;
            }
        }

        return result;
    }

    /// <summary>读取本店全部分类，以及与「当前特殊商品」求交集后的归属。调用方负责事务 / 隔离级别。</summary>
    private async Task<CatalogSpecialProductCategoriesResponse> BuildSnapshotAsync(
        string storeCode,
        CancellationToken cancellationToken)
    {
        var db = dbContext.MainDb;
        var categories = (await db.Queryable<StoreSpecialProductCategory>()
                .With(SqlWith.Null)
                .Where(x => x.StoreCode == storeCode && !x.IsDeleted)
                .ToListAsync(cancellationToken))
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Guid, StringComparer.Ordinal)
            .ToList();
        var itemRows = await db.Queryable<StoreSpecialProductCategoryItem>()
            .With(SqlWith.Null)
            .Where(x => x.StoreCode == storeCode && !x.IsDeleted)
            .ToListAsync(cancellationToken);

        // Revision：分类与归属两张表 UpdatedAt（缺失取 CreatedAt）的最大 ticks；两表都没数据时为 "0"。
        var ticks = categories.Select(x => (x.UpdatedAt ?? x.CreatedAt).Ticks)
            .Concat(itemRows.Select(x => (x.UpdatedAt ?? x.CreatedAt).Ticks))
            .DefaultIfEmpty(0L)
            .Max();
        var revision = ticks.ToString(CultureInfo.InvariantCulture);

        var categoryOrder = categories
            .Select((category, index) => (category.Guid, index))
            .ToDictionary(x => x.Guid, x => x.index, StringComparer.Ordinal);
        var assignments = new List<SpecialProductCategoryAssignmentDto>();
        if (categories.Count > 0 && itemRows.Count > 0)
        {
            // 没建任何分类的店不需要查特殊商品；有分类才求交集，只保留当前仍是特殊商品的归属。
            var specialCodes = await LoadCurrentSpecialProductCodesAsync(storeCode, cancellationToken);
            assignments = itemRows
                .Where(x => categoryOrder.ContainsKey(x.CategoryGuid) && specialCodes.ContainsKey(x.ProductCode))
                .OrderBy(x => categoryOrder[x.CategoryGuid])
                .ThenBy(x => x.SortOrder)
                .ThenBy(x => x.ProductCode, StringComparer.Ordinal)
                .Select(x => new SpecialProductCategoryAssignmentDto(x.ProductCode, x.CategoryGuid, x.SortOrder))
                .ToList();
        }

        return new CatalogSpecialProductCategoriesResponse(
            storeCode,
            DateTimeOffset.UtcNow,
            revision,
            categories
                .Select(x => new SpecialProductCategoryDto(x.Guid, x.Name, x.Color, x.SortOrder))
                .ToList(),
            assignments);
    }

    /// <summary>
    /// 把多张表的读取放在同一个 SNAPSHOT 事务里，读到同一时点的已提交数据（与特殊商品分页一致）；
    /// SQLite 单元测试不支持 SNAPSHOT，直接读，生产 MainDb 固定为 SQL Server。
    /// </summary>
    private async Task<T> RunSnapshotReadAsync<T>(
        Func<CancellationToken, Task<T>> readAsync,
        CancellationToken cancellationToken)
    {
        var db = dbContext.MainDb;
        var transactionStarted = false;
        if (db.CurrentConnectionConfig.DbType == SqlSugar.DbType.SqlServer)
        {
            await db.Ado.BeginTranAsync(IsolationLevel.Snapshot);
            transactionStarted = true;
        }

        try
        {
            var result = await readAsync(cancellationToken);
            if (transactionStarted)
            {
                await db.Ado.CommitTranAsync();
            }

            return result;
        }
        catch
        {
            if (transactionStarted)
            {
                await db.Ado.RollbackTranAsync();
            }

            throw;
        }
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim();

    private static string ResolveActor(string? updatedBy) =>
        string.IsNullOrWhiteSpace(updatedBy) ? "pos-device" : updatedBy.Trim();

    private static void Log(string message)
    {
        Console.WriteLine($"[HBPOS][Api][SpecialProductCategory] {DateTimeOffset.Now:O} {message}");
    }
}
