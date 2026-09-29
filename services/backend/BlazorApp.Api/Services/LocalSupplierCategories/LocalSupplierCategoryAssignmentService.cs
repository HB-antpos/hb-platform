using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.LocalSupplierCategories;

/// <summary>
/// 商品与供应商分类的归属维护：按采集自动归类、人工指定锁定、商品身份变化联动。
/// 所有方法都使用调用方传入的数据库客户端，可在调用方事务内执行。
/// </summary>
public sealed class LocalSupplierCategoryAssignmentService
{
    // SQL Server 单语句参数上限 2100；IN 列表与批量写入按块拆分，留足余量。
    private const int InListChunkSize = 500;
    private const int WriteChunkSize = 200;
    private const string GfaSupplierCode = "236";

    private readonly ISqlSugarClient _db;
    private readonly TimeProvider _timeProvider;

    public LocalSupplierCategoryAssignmentService(ISqlSugarClient db, TimeProvider? timeProvider = null)
    {
        _db = db;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public sealed class ReconcileResult
    {
        public int Matched { get; set; }
        public int Assigned { get; set; }
        public int Updated { get; set; }
        public int Cleared { get; set; }
        public int Unchanged { get; set; }
        public int ManualSkipped { get; set; }
        public List<string> UnmatchedKeys { get; set; } = new();
    }

    /// <summary>
    /// 商品编辑时的归属联动参数。旧值在修改商品前读取。
    /// </summary>
    public sealed record ProductEditContext(
        string ProductCode,
        string? OldProductCode,
        string? OldSupplierCode,
        string? NewSupplierCode,
        string? OldItemNumber,
        string? NewItemNumber,
        string? RequestedCategoryGuid,
        bool ClearRequested,
        string? Actor
    );

    private sealed class ProductKeyRow
    {
        public string ProductCode { get; set; } = string.Empty;
        public string? ItemNumber { get; set; }
    }

    public static string NormalizeSupplierCode(string? supplierCode) =>
        string.IsNullOrWhiteSpace(supplierCode)
            ? LocalSupplierCategoryConstants.HotBargainSupplierCode
            : supplierCode.Trim();

    /// <summary>
    /// 采集货号与商品的匹配键：与浏览器扩展采购摘要同一口径，GFA（236）页面编码对应商品编码，其余对应货号。
    /// </summary>
    public static string? BuildMatchKey(string supplierCode, string? productCode, string? itemNumber)
    {
        var raw = string.Equals(supplierCode, GfaSupplierCode, StringComparison.OrdinalIgnoreCase)
            ? productCode
            : itemNumber;
        var key = raw?.Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(key) ? null : key;
    }

    /// <summary>
    /// 按采集到的货号重算对应商品的归属（采集回传后调用）。
    /// </summary>
    public async Task<ReconcileResult> ReconcileByItemNumbersAsync(
        string supplierCode,
        IReadOnlyCollection<string> itemNumbers,
        string? actor
    )
    {
        var supplier = NormalizeSupplierCode(supplierCode);
        var keys = itemNumbers
            .Select(value => value?.Trim().ToUpperInvariant())
            .Where(value => !string.IsNullOrEmpty(value))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (keys.Count == 0 || LocalSupplierCategoryConstants.IsHotBargain(supplier))
        {
            return new ReconcileResult { UnmatchedKeys = keys };
        }

        var products = await LoadProductsByKeysAsync(supplier, keys);
        var result = await ReconcileProductsAsync(supplier, products, actor, wholeSupplier: false);
        var matchedKeys = products
            .Select(product => BuildMatchKey(supplier, product.ProductCode, product.ItemNumber))
            .Where(key => key != null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        result.UnmatchedKeys = keys.Where(key => !matchedKeys.Contains(key)).ToList();
        return result;
    }

    /// <summary>
    /// 按全部采集记录重算某供应商全部商品，并清理商品已换供应商或已删除留下的陈旧归属。
    /// </summary>
    public async Task<LocalSupplierCategoryResolveResultDto> ResolveSupplierAsync(
        string supplierCode,
        string? actor
    )
    {
        var supplier = NormalizeSupplierCode(supplierCode);
        if (LocalSupplierCategoryConstants.IsHotBargain(supplier))
        {
            throw new LocalSupplierCategoryValidationException(
                LocalSupplierCategoryErrorCodes.SupplierNotCapturable,
                "Hot Bargain 自营商品的供应商分类即仓库分类，无需重新解析。"
            );
        }

        var products = await _db.Queryable<Product>()
            .Where(product =>
                product.LocalSupplierCode == supplier
                && product.IsDeleted == false
                && product.ProductCode != null
            )
            .Select(product => new ProductKeyRow
            {
                ProductCode = product.ProductCode!,
                ItemNumber = product.ItemNumber,
            })
            .ToListAsync();
        var reconcile = await ReconcileProductsAsync(supplier, products, actor, wholeSupplier: true);

        // 归属仍记在本供应商名下、但商品已不属于本供应商（HQ 改了供应商或已删除）的行一律清除。
        var liveCodes = products
            .Select(product => product.ProductCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assignedCodes = await _db.Queryable<LocalSupplierCategoryProductAssignment>()
            .Where(assignment => assignment.LocalSupplierCode == supplier)
            .Select(assignment => assignment.ProductCode)
            .ToListAsync();
        var staleCodes = assignedCodes.Where(code => !liveCodes.Contains(code)).ToList();
        await DeleteAssignmentsAsync(staleCodes);

        return new LocalSupplierCategoryResolveResultDto
        {
            ProductsScanned = products.Count,
            Assigned = reconcile.Assigned,
            Updated = reconcile.Updated,
            Cleared = reconcile.Cleared,
            Unchanged = reconcile.Unchanged,
            ManualSkipped = reconcile.ManualSkipped,
            StaleRemoved = staleCodes.Count,
        };
    }

    /// <summary>
    /// 校验人工指定的分类：必须存在、未删除，且属于商品（更新后）的供应商。
    /// </summary>
    public async Task<LocalSupplierCategory> ValidateManualCategoryAsync(
        string? supplierCode,
        string categoryGuid
    )
    {
        var supplier = NormalizeSupplierCode(supplierCode);
        var guid = categoryGuid.Trim();
        var category = await _db.Queryable<LocalSupplierCategory>()
            .Where(item => item.CategoryGUID == guid && item.IsDeleted == false)
            .FirstAsync();
        if (category == null)
        {
            throw new LocalSupplierCategoryValidationException(
                LocalSupplierCategoryErrorCodes.CategoryNotFound,
                "供应商分类不存在或已删除。"
            );
        }

        if (!string.Equals(category.LocalSupplierCode, supplier, StringComparison.OrdinalIgnoreCase))
        {
            throw new LocalSupplierCategoryValidationException(
                LocalSupplierCategoryErrorCodes.CategorySupplierMismatch,
                "所选供应商分类不属于该商品的供应商。"
            );
        }

        return category;
    }

    /// <summary>
    /// 商品编辑后的归属联动：人工指定 → 锁定；清空 → 恢复自动；改供应商 → 旧归属作废并重算；改货号 → 非人工归属重算。
    /// </summary>
    public async Task ApplyProductEditAsync(ProductEditContext context)
    {
        var productCode = context.ProductCode.Trim();
        if (
            !string.IsNullOrWhiteSpace(context.OldProductCode)
            && !string.Equals(context.OldProductCode.Trim(), productCode, StringComparison.OrdinalIgnoreCase)
        )
        {
            await RenameProductCodeAsync(context.OldProductCode.Trim(), productCode);
        }

        var newSupplier = NormalizeSupplierCode(context.NewSupplierCode);
        var existing = await _db.Queryable<LocalSupplierCategoryProductAssignment>()
            .Where(assignment => assignment.ProductCode == productCode)
            .FirstAsync();

        if (LocalSupplierCategoryConstants.IsHotBargain(newSupplier))
        {
            // 200 的供应商分类即仓库分类；请求中的供应商分类被忽略，残留归属清除。
            if (existing != null)
            {
                await DeleteAssignmentsAsync(new[] { productCode });
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(context.RequestedCategoryGuid))
        {
            var category = await ValidateManualCategoryAsync(newSupplier, context.RequestedCategoryGuid);
            await UpsertAsync(
                existing,
                productCode,
                newSupplier,
                category.CategoryGUID,
                LocalSupplierCategorySources.Manual,
                BuildMatchKey(newSupplier, productCode, context.NewItemNumber),
                context.Actor
            );
            return;
        }

        var supplierChanged = !string.Equals(
            NormalizeSupplierCode(context.OldSupplierCode),
            newSupplier,
            StringComparison.OrdinalIgnoreCase
        );
        var oldKey = BuildMatchKey(newSupplier, context.OldProductCode ?? productCode, context.OldItemNumber);
        var newKey = BuildMatchKey(newSupplier, productCode, context.NewItemNumber);
        var keyChanged = !string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase);
        var isManual = existing != null
            && string.Equals(existing.Source, LocalSupplierCategorySources.Manual, StringComparison.OrdinalIgnoreCase);

        if (context.ClearRequested || supplierChanged)
        {
            // 清空恢复自动；换供应商时旧分类不属于新供应商，人工归属也一并作废。
            if (existing != null)
            {
                await DeleteAssignmentsAsync(new[] { productCode });
            }
            await ReconcileProductsAsync(
                newSupplier,
                new List<ProductKeyRow> { new() { ProductCode = productCode, ItemNumber = context.NewItemNumber } },
                context.Actor,
                wholeSupplier: false
            );
            return;
        }

        if (keyChanged && !isManual)
        {
            await ReconcileProductsAsync(
                newSupplier,
                new List<ProductKeyRow> { new() { ProductCode = productCode, ItemNumber = context.NewItemNumber } },
                context.Actor,
                wholeSupplier: false
            );
        }
    }

    public async Task RenameProductCodeAsync(string oldProductCode, string newProductCode)
    {
        if (string.Equals(oldProductCode, newProductCode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var old = await _db.Queryable<LocalSupplierCategoryProductAssignment>()
            .Where(assignment => assignment.ProductCode == oldProductCode)
            .FirstAsync();
        if (old == null)
        {
            return;
        }

        // ORM 不允许更新主键：删除旧行后以新编码插入。新编码理论上不会已有归属；若有，以旧商品的归属为准。
        await DeleteAssignmentsAsync(new[] { oldProductCode, newProductCode });
        old.ProductCode = newProductCode;
        await _db.Insertable(old).ExecuteCommandAsync();
    }

    public async Task DeleteAssignmentsAsync(IEnumerable<string> productCodes)
    {
        var codes = productCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var chunk in codes.Chunk(InListChunkSize))
        {
            var chunkList = chunk.ToList();
            await _db.Deleteable<LocalSupplierCategoryProductAssignment>()
                .Where(assignment => chunkList.Contains(assignment.ProductCode))
                .ExecuteCommandAsync();
        }
    }

    private async Task<List<ProductKeyRow>> LoadProductsByKeysAsync(string supplier, IReadOnlyList<string> keys)
    {
        var matchByProductCode = string.Equals(supplier, GfaSupplierCode, StringComparison.OrdinalIgnoreCase);
        var keySet = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new List<ProductKeyRow>();
        foreach (var chunk in keys.Chunk(InListChunkSize))
        {
            var chunkList = chunk.ToList();
            var query = _db.Queryable<Product>()
                .Where(product => product.LocalSupplierCode == supplier && product.IsDeleted == false);
            query = matchByProductCode
                ? query.Where(product => chunkList.Contains(product.ProductCode!))
                : query.Where(product => chunkList.Contains(product.ItemNumber!));
            rows.AddRange(
                await query
                    .Select(product => new ProductKeyRow
                    {
                        ProductCode = product.ProductCode!,
                        ItemNumber = product.ItemNumber,
                    })
                    .ToListAsync()
            );
        }

        // SQL Server 排序规则不区分大小写；内存再按归一化键过滤一次，保证各数据库口径一致。
        return rows
            .Where(row => !string.IsNullOrWhiteSpace(row.ProductCode))
            .Where(row =>
            {
                var key = BuildMatchKey(supplier, row.ProductCode, row.ItemNumber);
                return key != null && keySet.Contains(key);
            })
            .GroupBy(row => row.ProductCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private async Task<ReconcileResult> ReconcileProductsAsync(
        string supplier,
        IReadOnlyList<ProductKeyRow> products,
        string? actor,
        bool wholeSupplier
    )
    {
        var result = new ReconcileResult();
        var distinctProducts = products
            .Where(product => !string.IsNullOrWhiteSpace(product.ProductCode))
            .GroupBy(product => product.ProductCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (distinctProducts.Count == 0)
        {
            return result;
        }

        result.Matched = distinctProducts.Count;
        var keys = distinctProducts
            .Select(product => BuildMatchKey(supplier, product.ProductCode, product.ItemNumber))
            .Where(key => key != null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var captures = new List<LocalSupplierCategoryCapture>();
        if (wholeSupplier)
        {
            captures = await _db.Queryable<LocalSupplierCategoryCapture>()
                .Where(capture => capture.LocalSupplierCode == supplier)
                .ToListAsync();
        }
        else
        {
            foreach (var chunk in keys.Chunk(InListChunkSize))
            {
                var chunkList = chunk.ToList();
                captures.AddRange(
                    await _db.Queryable<LocalSupplierCategoryCapture>()
                        .Where(capture =>
                            capture.LocalSupplierCode == supplier
                            && chunkList.Contains(capture.ItemNumber)
                        )
                        .ToListAsync()
                );
            }
        }

        var categories = await LoadCategoriesAsync(
            supplier,
            captures.Select(capture => capture.CategoryGUID).Distinct().ToList(),
            wholeSupplier
        );
        var candidatesByKey = captures
            .Where(capture => categories.ContainsKey(capture.CategoryGUID))
            .GroupBy(capture => capture.ItemNumber, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(capture =>
                    {
                        var category = categories[capture.CategoryGUID];
                        return new LocalSupplierCategoryCandidate(
                            category.CategoryGUID,
                            category.Depth,
                            category.IsPromotional,
                            category.IsActive,
                            category.IsDeleted,
                            capture.LastSeenAt
                        );
                    })
                    .ToList(),
                StringComparer.OrdinalIgnoreCase
            );

        var existingByCode = new Dictionary<string, LocalSupplierCategoryProductAssignment>(
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var chunk in distinctProducts.Select(product => product.ProductCode.Trim()).Chunk(InListChunkSize))
        {
            var chunkList = chunk.ToList();
            var rows = await _db.Queryable<LocalSupplierCategoryProductAssignment>()
                .Where(assignment => chunkList.Contains(assignment.ProductCode))
                .ToListAsync();
            foreach (var row in rows)
            {
                existingByCode.TryAdd(row.ProductCode, row);
            }
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var toInsert = new List<LocalSupplierCategoryProductAssignment>();
        var toUpdate = new List<LocalSupplierCategoryProductAssignment>();
        var toDelete = new List<string>();
        foreach (var product in distinctProducts)
        {
            var productCode = product.ProductCode.Trim();
            var key = BuildMatchKey(supplier, productCode, product.ItemNumber);
            var desired = key != null && candidatesByKey.TryGetValue(key, out var candidates)
                ? LocalSupplierCategoryResolver.Resolve(candidates)
                : null;
            existingByCode.TryGetValue(productCode, out var existing);
            var sameSupplier = existing != null
                && string.Equals(existing.LocalSupplierCode, supplier, StringComparison.OrdinalIgnoreCase);

            // 人工指定的归属只要仍属于当前供应商就锁定，自动重算不覆盖。
            if (
                existing != null
                && sameSupplier
                && string.Equals(existing.Source, LocalSupplierCategorySources.Manual, StringComparison.OrdinalIgnoreCase)
            )
            {
                result.ManualSkipped++;
                continue;
            }

            if (desired == null)
            {
                if (existing != null)
                {
                    toDelete.Add(productCode);
                    result.Cleared++;
                }
                continue;
            }

            if (existing == null)
            {
                toInsert.Add(new LocalSupplierCategoryProductAssignment
                {
                    ProductCode = productCode,
                    LocalSupplierCode = supplier,
                    CategoryGUID = desired,
                    Source = LocalSupplierCategorySources.Website,
                    ItemNumberKey = key,
                    AssignedAt = now,
                    AssignedBy = actor,
                });
                result.Assigned++;
                continue;
            }

            if (
                !sameSupplier
                || !string.Equals(existing.CategoryGUID, desired, StringComparison.Ordinal)
                || !string.Equals(existing.Source, LocalSupplierCategorySources.Website, StringComparison.Ordinal)
            )
            {
                existing.LocalSupplierCode = supplier;
                existing.CategoryGUID = desired;
                existing.Source = LocalSupplierCategorySources.Website;
                existing.ItemNumberKey = key;
                existing.AssignedAt = now;
                existing.AssignedBy = actor;
                toUpdate.Add(existing);
                result.Updated++;
                continue;
            }

            result.Unchanged++;
        }

        await DeleteAssignmentsAsync(toDelete);
        foreach (var chunk in toInsert.Chunk(WriteChunkSize))
        {
            await _db.Insertable(chunk.ToList()).ExecuteCommandAsync();
        }
        foreach (var chunk in toUpdate.Chunk(WriteChunkSize))
        {
            await _db.Updateable(chunk.ToList())
                .UpdateColumns(assignment => new
                {
                    assignment.LocalSupplierCode,
                    assignment.CategoryGUID,
                    assignment.Source,
                    assignment.ItemNumberKey,
                    assignment.AssignedAt,
                    assignment.AssignedBy,
                })
                .ExecuteCommandAsync();
        }

        return result;
    }

    private async Task<Dictionary<string, LocalSupplierCategory>> LoadCategoriesAsync(
        string supplier,
        IReadOnlyList<string> categoryGuids,
        bool wholeSupplier
    )
    {
        var categories = new List<LocalSupplierCategory>();
        if (wholeSupplier)
        {
            categories = await _db.Queryable<LocalSupplierCategory>()
                .Where(category => category.LocalSupplierCode == supplier)
                .ToListAsync();
        }
        else
        {
            foreach (var chunk in categoryGuids.Chunk(InListChunkSize))
            {
                var chunkList = chunk.ToList();
                categories.AddRange(
                    await _db.Queryable<LocalSupplierCategory>()
                        .Where(category => chunkList.Contains(category.CategoryGUID))
                        .ToListAsync()
                );
            }
        }

        return categories
            .Where(category => string.Equals(category.LocalSupplierCode, supplier, StringComparison.OrdinalIgnoreCase))
            .GroupBy(category => category.CategoryGUID, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    private async Task UpsertAsync(
        LocalSupplierCategoryProductAssignment? existing,
        string productCode,
        string supplier,
        string categoryGuid,
        string source,
        string? itemNumberKey,
        string? actor
    )
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (existing == null)
        {
            await _db.Insertable(new LocalSupplierCategoryProductAssignment
            {
                ProductCode = productCode,
                LocalSupplierCode = supplier,
                CategoryGUID = categoryGuid,
                Source = source,
                ItemNumberKey = itemNumberKey,
                AssignedAt = now,
                AssignedBy = actor,
            }).ExecuteCommandAsync();
            return;
        }

        existing.LocalSupplierCode = supplier;
        existing.CategoryGUID = categoryGuid;
        existing.Source = source;
        existing.ItemNumberKey = itemNumberKey;
        existing.AssignedAt = now;
        existing.AssignedBy = actor;
        await _db.Updateable(existing)
            .UpdateColumns(assignment => new
            {
                assignment.LocalSupplierCode,
                assignment.CategoryGUID,
                assignment.Source,
                assignment.ItemNumberKey,
                assignment.AssignedAt,
                assignment.AssignedBy,
            })
            .ExecuteCommandAsync();
    }
}
