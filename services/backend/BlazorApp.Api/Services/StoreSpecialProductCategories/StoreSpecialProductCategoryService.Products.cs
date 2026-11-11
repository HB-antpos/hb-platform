using BlazorApp.Api.Data;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.StoreSpecialProductCategories;

// 商品侧：分类内 / 全部 / 未分类的商品列表、添加商品候选、归类（可顺带标记特殊）、取消标记、分类内排序。
public sealed partial class StoreSpecialProductCategoryService
{
    public Task<ApiResponse<StoreSpecialCategoryProductPageDto>> GetProductsAsync(
        string? storeCode, string? categoryId, string? keyword, int page, int pageSize) =>
        RunAsync(async () =>
        {
            var store = await RequireStoreAsync(storeCode);
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, MaxProductPageSize);

            var categories = await LoadCategoriesAsync(store.StoreCode);
            var categoryById = categories.ToDictionary(c => c.Guid, StringComparer.OrdinalIgnoreCase);
            var itemsByCode = IndexItemsByCode(await LoadItemsAsync(store.StoreCode), categories);
            var specialRows = await LoadSpecialRowsAsync(store.StoreCode);
            var rowsByCode = specialRows
                .GroupBy(r => r.ProductCode!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            // 先按视图筛出商品编码：分类视图按服务器顺序，全部 / 未分类视图稍后按名称排序。
            var viewKey = categoryId?.Trim();
            List<string> codes;
            var byServerOrder = false;
            if (string.IsNullOrEmpty(viewKey))
            {
                codes = rowsByCode.Keys.ToList();
            }
            else if (string.Equals(viewKey, UncategorizedKey, StringComparison.OrdinalIgnoreCase))
            {
                codes = rowsByCode.Keys.Where(code => !itemsByCode.ContainsKey(code)).ToList();
            }
            else
            {
                if (!categoryById.TryGetValue(viewKey, out var category))
                {
                    throw CategoryNotFound();
                }

                byServerOrder = true;
                // 分类内顺序由服务器统一：SortOrder，再按商品编码稳定排序；并且只含仍是特殊商品的。
                codes = rowsByCode.Keys
                    .Where(code => itemsByCode.TryGetValue(code, out var item)
                        && string.Equals(item.CategoryGuid, category.Guid, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(code => itemsByCode[code].SortOrder)
                    .ThenBy(code => code, StringComparer.Ordinal)
                    .ToList();
            }

            var products = await LoadProductsAsync(codes);

            var key = keyword?.Trim();
            if (!string.IsNullOrEmpty(key))
            {
                codes = codes.Where(code =>
                    MatchesKeyword(code, products.GetValueOrDefault(code), key)).ToList();
            }

            if (!byServerOrder)
            {
                codes = codes
                    .OrderBy(code => DisplayNameOf(products.GetValueOrDefault(code), code), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(code => code, StringComparer.Ordinal)
                    .ToList();
            }

            var total = codes.Count;
            var items = codes.Skip((page - 1) * pageSize).Take(pageSize).Select(code =>
            {
                products.TryGetValue(code, out var product);
                itemsByCode.TryGetValue(code, out var item);
                var categoryName = item is not null ? categoryById[item.CategoryGuid].Name : null;
                return new StoreSpecialCategoryProductDto
                {
                    ProductCode = code,
                    DisplayName = DisplayNameOf(product, code),
                    ItemNumber = product?.ItemNumber,
                    Barcode = product?.Barcode,
                    RetailPrice = RetailPriceOf(rowsByCode.GetValueOrDefault(code), product),
                    ProductImage = product?.ProductImage,
                    CategoryId = item?.CategoryGuid,
                    CategoryName = categoryName,
                    SortOrder = item?.SortOrder ?? 0,
                };
            }).ToList();

            return new StoreSpecialCategoryProductPageDto
            {
                Items = items,
                Page = page,
                PageSize = pageSize,
                Total = total,
            };
        });

    private static bool MatchesKeyword(string productCode, ProductLite? product, string keyword)
    {
        static bool Has(string? value, string key) =>
            !string.IsNullOrEmpty(value) && value.Contains(key, StringComparison.OrdinalIgnoreCase);

        return Has(productCode, keyword)
            || Has(product?.ProductName, keyword)
            || Has(product?.EnglishName, keyword)
            || Has(product?.ItemNumber, keyword)
            || Has(product?.Barcode, keyword);
    }

    public Task<ApiResponse<StoreSpecialCategoryCandidateResultDto>> SearchCandidatesAsync(
        string? storeCode, string? keyword, int? limit) =>
        RunAsync(async () =>
        {
            var store = await RequireStoreAsync(storeCode);
            var key = (keyword ?? string.Empty).Trim();
            if (key.Length < MinCandidateKeywordLength)
            {
                throw Validation($"请至少输入 {MinCandidateKeywordLength} 个字符");
            }

            var take = Math.Clamp(limit ?? DefaultCandidateLimit, 1, MaxCandidateLimit);
            var storeCodeValue = store.StoreCode;

            // 只搜本店有有效（未删除且启用）StoreRetailPrice 行的商品：本接口不新建价格行，没有价格行的商品没法标记。
            ISugarQueryable<Product> BaseQuery() => _db.Queryable<Product>().Where(p =>
                !p.IsDeleted
                && SqlFunc.Subqueryable<StoreRetailPrice>()
                    .Where(r => r.StoreCode == storeCodeValue
                        && r.ProductCode == p.ProductCode
                        && !r.IsDeleted
                        && r.IsActive)
                    .Any());

            // 先取货号 / 条码 / 商品编码精确命中的（扫码场景必须排最前），再补名称等模糊命中的。
            var exact = await BaseQuery()
                .Where(p => p.Barcode == key || p.ItemNumber == key || p.ProductCode == key)
                .OrderBy(p => p.ProductName)
                .Take(take)
                .ToListAsync();
            var fuzzy = await BaseQuery()
                .Where(p => p.ProductName.Contains(key)
                    || p.EnglishName!.Contains(key)
                    || p.ItemNumber!.Contains(key)
                    || p.Barcode!.Contains(key)
                    || p.ProductCode!.Contains(key))
                .OrderBy(p => p.ProductName)
                .Take(take + exact.Count)
                .ToListAsync();

            var picked = new List<Product>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var product in exact.Concat(fuzzy))
            {
                if (!string.IsNullOrWhiteSpace(product.ProductCode) && seen.Add(product.ProductCode))
                {
                    picked.Add(product);
                }

                if (picked.Count >= take)
                {
                    break;
                }
            }

            var codes = picked.Select(p => p.ProductCode!).ToList();
            var rowsByCode = await LoadRowsByCodeAsync(storeCodeValue, codes);
            var categories = await LoadCategoriesAsync(storeCodeValue);
            var categoryById = categories.ToDictionary(c => c.Guid, StringComparer.OrdinalIgnoreCase);
            var itemsByCode = IndexItemsByCode(await LoadItemsAsync(storeCodeValue), categories);

            return new StoreSpecialCategoryCandidateResultDto
            {
                Items = picked.Select(product =>
                {
                    var code = product.ProductCode!;
                    var rows = rowsByCode.GetValueOrDefault(code);
                    var isSpecial = IsCurrentlySpecial(rows);   // 统一口径：启用行里最新一行带标记
                    itemsByCode.TryGetValue(code, out var item);
                    // 取消标记但保留归属的商品：候选里 isSpecialProduct=false，归属信息仍照实给出，方便重新标记后回到原分类。
                    return new StoreSpecialCategoryCandidateDto
                    {
                        ProductCode = code,
                        DisplayName = string.IsNullOrWhiteSpace(product.ProductName)
                            ? (string.IsNullOrWhiteSpace(product.EnglishName) ? code : product.EnglishName!)
                            : product.ProductName,
                        ItemNumber = product.ItemNumber,
                        Barcode = product.Barcode,
                        RetailPrice = rows is null ? product.RetailPrice : (PickPrimaryRow(rows)?.StoreRetailPriceValue ?? product.RetailPrice),
                        ProductImage = product.ProductImage,
                        IsSpecialProduct = isSpecial,
                        CategoryId = item?.CategoryGuid,
                        CategoryName = item is null ? null : categoryById[item.CategoryGuid].Name,
                    };
                }).ToList(),
            };
        });

    /// <summary>
    /// 归类：markAsSpecial=true 时把尚不是特殊商品的先标记为特殊（只改本店已有价格行，绝不新建价格行，没有行 → 跳过）；
    /// categoryId=null 表示移到「未分类」（删归属行）；移入分类时追加到该分类末尾并保持传入顺序，
    /// 已经在目标分类里的商品原位不动（重复提交不会打乱顺序）。
    /// </summary>
    public Task<ApiResponse<StoreSpecialCategoryAssignResultDto>> AssignAsync(StoreSpecialCategoryAssignRequestDto? request) =>
        RunAsync(async () =>
        {
            var store = await RequireStoreAsync(request?.StoreCode);
            var codes = NormalizeCodes(request?.ProductCodes);
            var markAsSpecial = request!.MarkAsSpecial;

            StoreSpecialProductCategory? target = null;
            var categoryId = request.CategoryId?.Trim();
            if (!string.IsNullOrEmpty(categoryId))
            {
                target = await FindCategoryAsync(store.StoreCode, categoryId) ?? throw CategoryNotFound();
            }

            // 先读后写：两个请求并发归类同一个商品时，归属主键 (StoreCode, ProductCode) 可能撞车（SQL Server 2627 / 2601）。
            // 撞车时整个事务已回滚，重新读取最新状态再算一遍、再写一次；仍然撞车就给稳定的 409 业务错误。
            async Task<(StoreSpecialCategoryAssignResultDto Result, List<string> Assigned)> AttemptAsync()
            {
                var rowsByCode = await LoadRowsByCodeAsync(store.StoreCode, codes);
                var attempt = new StoreSpecialCategoryAssignResultDto();
                var toAssign = new List<string>();
                var rowsToMark = new List<StoreRetailPrice>();

                foreach (var requestedCode in codes)
                {
                    rowsByCode.TryGetValue(requestedCode, out var rows);
                    rows ??= new List<StoreRetailPrice>();

                    // 「已是特殊商品」= 启用行里最新一行带标记（旧行是特殊、新行不是，不算；会被真正标记）。
                    if (!IsCurrentlySpecial(rows))
                    {
                        if (!markAsSpecial)
                        {
                            attempt.Skipped.Add(new StoreSpecialCategorySkippedDto
                            {
                                ProductCode = requestedCode,
                                Reason = StoreSpecialCategorySkipReasons.NotSpecialProduct,
                            });
                            continue;
                        }

                        // 没有任何启用且未删除的价格行（含根本没有行）：本接口不插入价格行、也不恢复停用行，跳过并给稳定原因码。
                        if (LatestActiveRow(rows) is null)
                        {
                            attempt.Skipped.Add(new StoreSpecialCategorySkippedDto
                            {
                                ProductCode = requestedCode,
                                Reason = StoreSpecialCategorySkipReasons.NoStorePrice,
                            });
                            continue;
                        }

                        // 标记时该商品所有未删除行统一置位。
                        rowsToMark.AddRange(rows);
                        attempt.MarkedCount++;
                    }

                    // 归属表主键用库里的规范商品编码，避免大小写不同造成重复归属。
                    toAssign.Add(rows[0].ProductCode!);
                }

                await InTransactionAsync(async () =>
                {
                    await SetSpecialFlagAsync(rowsToMark, true, store.Actor);
                    if (target is null)
                    {
                        await RemoveAssignmentsAsync(store.StoreCode, toAssign);
                    }
                    else
                    {
                        await AppendAssignmentsAsync(store.StoreCode, target.Guid, toAssign, store.Actor);
                    }
                });

                attempt.AssignedCount = toAssign.Count;
                return (attempt, toAssign);
            }

            StoreSpecialCategoryAssignResultDto result;
            try
            {
                result = (await AttemptAsync()).Result;
            }
            catch (Exception first) when (SqlErrorClassifier.IsUniqueViolation(first))
            {
                _logger.LogInformation(
                    "StoreSpecialProductCategory assign hit unique violation, retrying once store={Store}", store.StoreCode);
                try
                {
                    result = (await AttemptAsync()).Result;
                }
                catch (Exception second) when (SqlErrorClassifier.IsUniqueViolation(second))
                {
                    throw new CategoryException(
                        StoreSpecialCategoryErrorCodes.AssignConflict,
                        "商品归类与别处的修改冲突，请刷新后重试");
                }
            }

            _logger.LogInformation(
                "StoreSpecialProductCategory assigned actor={Actor} store={Store} category={Category} assigned={Assigned} marked={Marked} skipped={Skipped}",
                store.Actor, store.StoreCode, target?.Guid ?? "(uncategorized)", result.AssignedCount, result.MarkedCount, result.Skipped.Count);
            return result;
        });

    /// <summary>取消特殊商品标记：只改标记，不删归属行（重新标记后回到原分类）。</summary>
    public Task<ApiResponse<StoreSpecialCategoryUnmarkResultDto>> UnmarkAsync(StoreSpecialCategoryUnmarkRequestDto? request) =>
        RunAsync(async () =>
        {
            var store = await RequireStoreAsync(request?.StoreCode);
            var codes = NormalizeCodes(request?.ProductCodes);
            var rowsByCode = await LoadRowsByCodeAsync(store.StoreCode, codes);

            // 计数只算「当前确实是特殊商品」的；清标记时连旧行 / 停用行上残留的标记一并清掉，避免日后又冒出来。
            var unmarkedCodes = rowsByCode
                .Where(pair => IsCurrentlySpecial(pair.Value))
                .Select(pair => pair.Key)
                .ToList();
            var rowsToUnmark = rowsByCode
                .Where(pair => pair.Value.Any(r => r.IsSpecialProduct))
                .SelectMany(pair => pair.Value)
                .ToList();

            await InTransactionAsync(() => SetSpecialFlagAsync(rowsToUnmark, false, store.Actor));

            _logger.LogInformation(
                "StoreSpecialProductCategory unmarked actor={Actor} store={Store} unmarked={Unmarked}",
                store.Actor, store.StoreCode, unmarkedCodes.Count);
            return new StoreSpecialCategoryUnmarkResultDto { UnmarkedCount = unmarkedCodes.Count };
        });

    /// <summary>
    /// 分类内排序：提交的商品必须恰好是该分类当前「仍是特殊商品」的全部商品的一个排列，否则冲突（别处刚改过）。
    /// 取消标记但保留归属的行排在这些商品之后，保持各自原有的相对顺序。
    /// </summary>
    public Task<ApiResponse<StoreSpecialCategoryOkDto>> ReorderProductsAsync(
        string id, StoreSpecialCategoryProductOrderRequestDto? request) =>
        RunAsync(async () =>
        {
            var store = await RequireStoreAsync(request?.StoreCode);
            var requested = (request?.ProductCodes ?? new List<string>())
                .Select(code => (code ?? string.Empty).Trim())
                .ToList();

            await InTransactionAsync(async () =>
            {
                var category = await FindCategoryAsync(store.StoreCode, id ?? string.Empty)
                    ?? throw CategoryNotFound();
                var specialCodes = SpecialCodesOf(await LoadSpecialRowsAsync(store.StoreCode));
                var items = (await LoadItemsAsync(store.StoreCode))
                    .Where(i => string.Equals(i.CategoryGuid, category.Guid, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(i => i.SortOrder)
                    .ThenBy(i => i.ProductCode, StringComparer.Ordinal)
                    .ToList();
                var itemByCode = items.ToDictionary(i => i.ProductCode, StringComparer.OrdinalIgnoreCase);
                var visibleCodes = items.Where(i => specialCodes.Contains(i.ProductCode)).Select(i => i.ProductCode).ToList();

                var distinct = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (requested.Count != visibleCodes.Count
                    || distinct.Count != requested.Count
                    || !distinct.SetEquals(visibleCodes))
                {
                    throw new CategoryException(
                        StoreSpecialCategoryErrorCodes.OrderConflict,
                        "分类里的商品已变化，请刷新后再排序");
                }

                var finalOrder = requested.Select(code => itemByCode[code])
                    .Concat(items.Where(i => !specialCodes.Contains(i.ProductCode)))
                    .ToList();
                var changed = new List<StoreSpecialProductCategoryItem>();
                var now = DateTime.UtcNow;
                for (var index = 0; index < finalOrder.Count; index++)
                {
                    if (finalOrder[index].SortOrder != index)
                    {
                        finalOrder[index].SortOrder = index;
                        finalOrder[index].UpdatedAt = now;
                        finalOrder[index].UpdatedBy = store.Actor;
                        changed.Add(finalOrder[index]);
                    }
                }

                // 只写 SortOrder 与审计列：不用旧值覆盖并发的其他修改。
                await ForEachChunkAsync(changed, WriteChunkSize, chunk =>
                    _db.Updateable(chunk)
                        .UpdateColumns(i => new { i.SortOrder, i.UpdatedAt, i.UpdatedBy })
                        .ExecuteCommandAsync());
            });

            return new StoreSpecialCategoryOkDto { Ok = true };
        });

    // ------------------------------------------------------------------ 写库辅助（调用方负责事务）

    /// <summary>
    /// 批量改特殊商品标记：同一商品的所有价格行都改，只在「主行」（启用且最近更新）上写最新审计时间，
    /// 与 POS API 标记接口一致，保证目录选价稳定；新标记不是改价，不产生 StorePriceUpdateTask。
    /// </summary>
    private async Task SetSpecialFlagAsync(List<StoreRetailPrice> rows, bool isSpecial, string actor)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var changed = new List<StoreRetailPrice>();
        foreach (var group in rows.GroupBy(r => r.ProductCode!, StringComparer.OrdinalIgnoreCase))
        {
            var primary = PickPrimaryRow(group);
            var groupChanged = new List<StoreRetailPrice>();
            foreach (var row in group)
            {
                if (row.IsSpecialProduct != isSpecial)
                {
                    row.IsSpecialProduct = isSpecial;
                    groupChanged.Add(row);
                }
            }

            if (groupChanged.Count == 0)
            {
                continue;
            }

            // 主行无论自己是否变化，只要这个商品有行被改，就盖上最新审计时间，让目录增量同步看得见这次变化。
            if (primary is not null)
            {
                primary.UpdatedAt = now;
                primary.UpdatedBy = actor;
                if (!groupChanged.Contains(primary))
                {
                    groupChanged.Add(primary);
                }
            }

            changed.AddRange(groupChanged);
        }

        // 全局审计 AOP 会在 UpdateByObject 时把每一行的 UpdatedAt / UpdatedBy 都改成现在，抹掉「只给主行盖时间」的意图，
        // 可能让同商品多行里「最新行」悄悄换成别的行，进而改变 POS 的取价；所以这次写入保留显式设置的审计字段。
        using (SqlSugarAuditScope.PreserveExplicitAuditFields())
        {
            await ForEachChunkAsync(changed, WriteChunkSize, chunk =>
                _db.Updateable(chunk)
                    .UpdateColumns(r => new { r.IsSpecialProduct, r.UpdatedAt, r.UpdatedBy })
                    .ExecuteCommandAsync());
        }
    }

    private async Task RemoveAssignmentsAsync(string storeCode, List<string> productCodes)
    {
        await ForEachChunkAsync(productCodes, QueryChunkSize, chunk =>
            _db.Deleteable<StoreSpecialProductCategoryItem>()
                .Where(i => i.StoreCode == storeCode && chunk.Contains(i.ProductCode))
                .ExecuteCommandAsync());
    }

    /// <summary>把商品追加到分类末尾（保持传入顺序）；已在该分类里的原位不动，在别的分类的改挂到本分类末尾。</summary>
    private async Task AppendAssignmentsAsync(string storeCode, string categoryGuid, List<string> productCodes, string actor)
    {
        var allItems = await LoadItemsAsync(storeCode);
        var existingByCode = allItems.ToDictionary(i => i.ProductCode, StringComparer.OrdinalIgnoreCase);
        var nextSort = allItems
            .Where(i => string.Equals(i.CategoryGuid, categoryGuid, StringComparison.OrdinalIgnoreCase))
            .Select(i => i.SortOrder)
            .DefaultIfEmpty(-1)
            .Max() + 1;

        var now = DateTime.UtcNow;
        var inserts = new List<StoreSpecialProductCategoryItem>();
        var updates = new List<StoreSpecialProductCategoryItem>();
        foreach (var code in productCodes)
        {
            if (existingByCode.TryGetValue(code, out var existing))
            {
                if (string.Equals(existing.CategoryGuid, categoryGuid, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                existing.CategoryGuid = categoryGuid;
                existing.SortOrder = nextSort++;
                existing.UpdatedAt = now;
                existing.UpdatedBy = actor;
                updates.Add(existing);
            }
            else
            {
                inserts.Add(new StoreSpecialProductCategoryItem
                {
                    StoreCode = storeCode,
                    ProductCode = code,
                    CategoryGuid = categoryGuid,
                    SortOrder = nextSort++,
                    CreatedAt = now,
                    CreatedBy = actor,
                    UpdatedAt = now,
                    UpdatedBy = actor,
                });
            }
        }

        if (BeforeAssignInsertForTesting is not null)
        {
            await BeforeAssignInsertForTesting();
        }

        await ForEachChunkAsync(inserts, WriteChunkSize, chunk => _db.Insertable(chunk).ExecuteCommandAsync());
        await ForEachChunkAsync(updates, WriteChunkSize, chunk => _db.Updateable(chunk).ExecuteCommandAsync());
    }
}
