using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.StoreSpecialProductCategories;

// 同步到其他分店：来源店的分类（可选连同「已归类的特殊商品」）复制到目标店；预览只算不写，执行每家目标店一个事务。
public sealed partial class StoreSpecialProductCategoryService
{
    /// <summary>来源店快照：分类（按顺序）、每个分类里「仍是特殊商品」的商品（按分类内顺序）、全部特殊商品。</summary>
    private sealed class SyncSource
    {
        public List<StoreSpecialProductCategory> Categories { get; init; } = new();

        /// <summary>来源分类 Guid → 该分类里仍是特殊商品的商品编码（SortOrder、编码稳定排序）。</summary>
        public Dictionary<string, List<string>> ProductsByCategory { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>来源店全部特殊商品（含未分类的）。</summary>
        public HashSet<string> SpecialCodes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public int ProductCount => ProductsByCategory.Values.Sum(list => list.Count);
    }

    /// <summary>一家目标店的同步计划：计数（预览用）+ 要写的行（执行用）。</summary>
    private sealed class TargetPlan
    {
        public StoreSpecialCategorySyncTargetResultDto Result { get; set; } = new();
        public List<StoreSpecialProductCategory> CategoryInserts { get; } = new();
        public List<StoreSpecialProductCategory> CategoryUpdates { get; } = new();
        public List<StoreSpecialProductCategory> CategoryDeletes { get; } = new();
        public List<StoreSpecialProductCategoryItem> ItemInserts { get; } = new();
        public List<StoreSpecialProductCategoryItem> ItemUpdates { get; } = new();
        public List<StoreSpecialProductCategoryItem> ItemDeletes { get; } = new();
        public List<StoreRetailPrice> RowsToMark { get; } = new();
        public List<StoreRetailPrice> RowsToUnmark { get; } = new();
    }

    private async Task<SyncSource> LoadSyncSourceAsync(string storeCode)
    {
        var categories = await LoadCategoriesAsync(storeCode);
        var items = await LoadItemsAsync(storeCode);
        var specialCodes = SpecialCodesOf(await LoadSpecialRowsAsync(storeCode));
        var source = new SyncSource { Categories = categories, SpecialCodes = specialCodes };
        foreach (var category in categories)
        {
            // 只同步「仍是特殊商品且已归类」的商品；来源店未分类的特殊商品不处理。
            source.ProductsByCategory[category.Guid] = items
                .Where(i => string.Equals(i.CategoryGuid, category.Guid, StringComparison.OrdinalIgnoreCase)
                    && specialCodes.Contains(i.ProductCode))
                .OrderBy(i => i.SortOrder)
                .ThenBy(i => i.ProductCode, StringComparer.Ordinal)
                .Select(i => i.ProductCode)
                .ToList();
        }

        return source;
    }

    public Task<ApiResponse<StoreSpecialCategorySyncTargetsDto>> GetSyncTargetsAsync(string? sourceStoreCode) =>
        RunAsync(async () =>
        {
            var source = await RequireStoreAsync(sourceStoreCode, "来源分店");
            var src = await LoadSyncSourceAsync(source.StoreCode);

            // 目标只含「管理的分店」中启用且未删除、不含来源的店。
            var stores = (await _db.Queryable<Store>().Where(s => !s.IsDeleted && s.IsActive).ToListAsync())
                .Where(s => !string.Equals(s.StoreCode, source.StoreCode, StringComparison.OrdinalIgnoreCase)
                    && source.Scope.CanAccessStoreCode(s.StoreCode))
                .OrderBy(s => s.StoreName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.StoreCode, StringComparer.Ordinal)
                .ToList();
            var storeCodes = stores.Select(s => s.StoreCode).ToList();

            var categoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var specialCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            await ForEachChunkAsync(storeCodes, QueryChunkSize, async chunk =>
            {
                var categories = await _db.Queryable<StoreSpecialProductCategory>()
                    .Where(c => chunk.Contains(c.StoreCode))
                    .Select(c => c.StoreCode)
                    .ToListAsync();
                foreach (var group in categories.GroupBy(code => code, StringComparer.OrdinalIgnoreCase))
                {
                    categoryCounts[group.Key] = group.Count();
                }
            });

            // 特殊商品数必须与 POS / 列表同口径（启用行里最新一行带标记），逐店用统一的读取方法。
            foreach (var code in storeCodes)
            {
                specialCounts[code] = SpecialCodesOf(await LoadSpecialRowsAsync(code)).Count;
            }

            return new StoreSpecialCategorySyncTargetsDto
            {
                Source = new StoreSpecialCategorySyncSourceDto
                {
                    StoreCode = source.StoreCode,
                    CategoryCount = src.Categories.Count,
                    ProductCount = src.ProductCount,
                },
                Targets = stores.Select(s => new StoreSpecialCategorySyncTargetDto
                {
                    StoreCode = s.StoreCode,
                    StoreName = s.StoreName,
                    CategoryCount = categoryCounts.GetValueOrDefault(s.StoreCode),
                    SpecialProductCount = specialCounts.GetValueOrDefault(s.StoreCode),
                }).ToList(),
            };
        });

    public Task<ApiResponse<StoreSpecialCategorySyncPreviewDto>> PreviewSyncAsync(StoreSpecialCategorySyncRequestDto? request) =>
        RunAsync(async () =>
        {
            var (scope, mode) = ParseScopeAndMode(request);
            var (source, targets) = await ResolveSyncStoresAsync(request!);
            var src = await LoadSyncSourceAsync(source.StoreCode);

            var results = new List<StoreSpecialCategorySyncTargetResultDto>();
            foreach (var target in targets)
            {
                // 预览只算不写：计划构建过程不触库写入。
                results.Add((await BuildPlanAsync(target, src, scope, mode)).Result);
            }

            return new StoreSpecialCategorySyncPreviewDto { Targets = results };
        });

    public Task<ApiResponse<StoreSpecialCategorySyncExecuteDto>> ExecuteSyncAsync(StoreSpecialCategorySyncRequestDto? request) =>
        RunAsync(async () =>
        {
            var (scope, mode) = ParseScopeAndMode(request);
            // replace 会删除目标店分类、取消特殊标记，风险高：必须显式确认。
            if (mode == StoreSpecialCategorySyncModes.Replace && !request!.ConfirmReplace)
            {
                throw new CategoryException(
                    StoreSpecialCategoryErrorCodes.ReplaceNotConfirmed,
                    "替换模式会删除目标分店的分类并取消部分特殊商品，需要确认后才能执行");
            }

            var (source, targets) = await ResolveSyncStoresAsync(request!);
            var src = await LoadSyncSourceAsync(source.StoreCode);

            var results = new List<StoreSpecialCategorySyncExecuteTargetDto>();
            foreach (var target in targets)
            {
                StoreSpecialCategorySyncExecuteTargetDto outcome;
                try
                {
                    var plan = await BuildPlanAsync(target, src, scope, mode);
                    if (plan.Result.Error is not null)
                    {
                        outcome = ToExecuteResult(plan.Result, ok: false);
                    }
                    else
                    {
                        // 每家目标店一个事务：这家失败只回滚这家，不影响其他店。
                        await ApplyPlanAsync(plan, target, source.Actor);
                        outcome = ToExecuteResult(plan.Result, ok: true);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        ex,
                        "StoreSpecialProductCategory sync failed actor={Actor} source={Source} target={Target}",
                        source.Actor, source.StoreCode, target.StoreCode);
                    outcome = new StoreSpecialCategorySyncExecuteTargetDto
                    {
                        StoreCode = target.StoreCode,
                        StoreName = target.StoreName,
                        Ok = false,
                        // 对外只给固定稳定码，不回传 SQL 异常细节（约束名、键值）；完整异常见上面的日志。
                        Error = StoreSpecialCategoryErrorCodes.SyncStoreFailed,
                    };
                }

                results.Add(outcome);

                // 操作日志：谁、何时（日志时间戳）、来源店 → 目标店、各计数。
                _logger.LogInformation(
                    "StoreSpecialProductCategory sync actor={Actor} source={Source} target={Target} scope={Scope} mode={Mode} ok={Ok} createdCategories={Created} mergedCategories={Merged} removedCategories={Removed} newlyMarked={NewlyMarked} reassigned={Reassigned} unchanged={Unchanged} unmarked={Unmarked} skipped={Skipped} error={Error}",
                    source.Actor, source.StoreCode, target.StoreCode, scope, mode, outcome.Ok,
                    outcome.CreatedCategories, outcome.MergedCategories, outcome.RemovedCategories,
                    outcome.NewlyMarkedProducts, outcome.ReassignedProducts, outcome.UnchangedProducts,
                    outcome.UnmarkedProducts, outcome.Skipped.Count, outcome.Error);
            }

            return new StoreSpecialCategorySyncExecuteDto { Results = results };
        });

    // ------------------------------------------------------------------ 请求校验

    private static (string Scope, string Mode) ParseScopeAndMode(StoreSpecialCategorySyncRequestDto? request)
    {
        if (request is null)
        {
            throw Validation("缺少同步参数");
        }

        var scope = (request.Scope ?? string.Empty).Trim().ToLowerInvariant();
        if (scope != StoreSpecialCategorySyncScopes.Categories
            && scope != StoreSpecialCategorySyncScopes.CategoriesAndProducts)
        {
            throw Validation("同步范围必须是 categories 或 categories-and-products");
        }

        var mode = (request.Mode ?? string.Empty).Trim().ToLowerInvariant();
        if (mode != StoreSpecialCategorySyncModes.Merge && mode != StoreSpecialCategorySyncModes.Replace)
        {
            throw Validation("同步方式必须是 merge 或 replace");
        }

        return (scope, mode);
    }

    /// <summary>来源与全部目标都必须在「管理的分店」范围内；目标去重、剔除来源、至少一家、必须是启用的分店。</summary>
    private async Task<(StoreContext Source, List<StoreContext> Targets)> ResolveSyncStoresAsync(
        StoreSpecialCategorySyncRequestDto request)
    {
        var source = await RequireStoreAsync(request.SourceStoreCode, "来源分店");
        var targetCodes = (request.TargetStoreCodes ?? new List<string>())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Where(code => !string.Equals(code, source.StoreCode, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (targetCodes.Count == 0)
        {
            throw Validation("请至少选择一个目标分店");
        }

        if (targetCodes.Count > MaxSyncTargets)
        {
            throw Validation($"一次最多同步 {MaxSyncTargets} 家分店");
        }

        var targets = new List<StoreContext>();
        foreach (var code in targetCodes)
        {
            var target = await RequireStoreAsync(code, "目标分店");
            if (!target.IsActive)
            {
                throw Validation($"目标分店 {target.StoreCode} 已停用");
            }

            targets.Add(target);
        }

        // 目标去重（输入大小写不同但指向同一家店）。
        targets = targets
            .GroupBy(t => t.StoreCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        return (source, targets);
    }

    // ------------------------------------------------------------------ 计划构建（只读）

    private static StoreSpecialCategorySyncExecuteTargetDto ToExecuteResult(
        StoreSpecialCategorySyncTargetResultDto result, bool ok) => new()
    {
        StoreCode = result.StoreCode,
        StoreName = result.StoreName,
        Ok = ok,
        Error = result.Error,
        CreatedCategories = result.CreatedCategories,
        MergedCategories = result.MergedCategories,
        RemovedCategories = result.RemovedCategories,
        NewlyMarkedProducts = result.NewlyMarkedProducts,
        ReassignedProducts = result.ReassignedProducts,
        UnchangedProducts = result.UnchangedProducts,
        UnmarkedProducts = result.UnmarkedProducts,
        Skipped = result.Skipped,
    };

    /// <summary>
    /// 计算一家目标店的同步计划（只读）：
    /// 1) 分类：按名称（不区分大小写）对上的目标分类保留并采用来源的颜色与顺序，新名称新建；来源分类按来源顺序排在前面，
    ///    目标店独有的分类 merge 时保留并排在后面，replace 时删除（其归属行一并删除，商品回到未分类）。
    /// 2) 商品（仅 categories-and-products）：来源店「仍是特殊商品且已归类」的商品，目标店没有有效门店价行 → 跳过（NO_STORE_PRICE）；
    ///    否则标记为特殊商品并放进同名分类，保持来源的分类内顺序，与目标店已有归属冲突时以来源为准。
    /// 3) replace + categories-and-products：目标店里「来源店并不是特殊商品」的特殊商品取消标记（不删归属行）。
    /// </summary>
    private async Task<TargetPlan> BuildPlanAsync(StoreContext target, SyncSource src, string scope, string mode)
    {
        var plan = new TargetPlan
        {
            Result = new StoreSpecialCategorySyncTargetResultDto { StoreCode = target.StoreCode, StoreName = target.StoreName },
        };
        var result = plan.Result;
        var replace = mode == StoreSpecialCategorySyncModes.Replace;
        var withProducts = scope == StoreSpecialCategorySyncScopes.CategoriesAndProducts;
        var now = DateTime.UtcNow;
        var actor = target.Actor;

        // ---- 分类
        var targetCategories = await LoadCategoriesAsync(target.StoreCode);
        var targetByName = new Dictionary<string, StoreSpecialProductCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in targetCategories)
        {
            targetByName.TryAdd(category.Name, category);
        }

        var destBySourceGuid = new Dictionary<string, StoreSpecialProductCategory>(StringComparer.OrdinalIgnoreCase);
        var matchedGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var position = 0;
        foreach (var sourceCategory in src.Categories)
        {
            StoreSpecialProductCategory dest;
            if (targetByName.TryGetValue(sourceCategory.Name, out var existing) && matchedGuids.Add(existing.Guid))
            {
                if (existing.SortOrder != position || !string.Equals(existing.Color, sourceCategory.Color, StringComparison.Ordinal))
                {
                    existing.SortOrder = position;
                    existing.Color = sourceCategory.Color;
                    existing.UpdatedAt = now;
                    existing.UpdatedBy = actor;
                    plan.CategoryUpdates.Add(existing);
                }

                result.MergedCategories++;
                dest = existing;
            }
            else
            {
                dest = new StoreSpecialProductCategory
                {
                    StoreCode = target.StoreCode,
                    Name = sourceCategory.Name,
                    Color = sourceCategory.Color,
                    SortOrder = position,
                    CreatedAt = now,
                    CreatedBy = actor,
                    UpdatedAt = now,
                    UpdatedBy = actor,
                };
                plan.CategoryInserts.Add(dest);
                result.CreatedCategories++;
            }

            destBySourceGuid[sourceCategory.Guid] = dest;
            position++;
        }

        var targetOnly = targetCategories.Where(c => !matchedGuids.Contains(c.Guid)).ToList();
        if (replace)
        {
            plan.CategoryDeletes.AddRange(targetOnly);
            result.RemovedCategories = targetOnly.Count;
        }
        else
        {
            foreach (var category in targetOnly)
            {
                if (category.SortOrder != position)
                {
                    category.SortOrder = position;
                    category.UpdatedAt = now;
                    category.UpdatedBy = actor;
                    plan.CategoryUpdates.Add(category);
                }

                position++;
            }
        }

        if (position > MaxCategoriesPerStore)
        {
            // 同步后分类数会超过上限：这家店无法同步，预览与执行都给出同一个原因，不动这家店。
            return new TargetPlan
            {
                Result = new StoreSpecialCategorySyncTargetResultDto
                {
                    StoreCode = target.StoreCode,
                    StoreName = target.StoreName,
                    Error = $"同步后分类数会达到 {position} 个，超过每家分店 {MaxCategoriesPerStore} 个的上限",
                },
            };
        }

        // ---- 归属行与特殊标记
        var targetItems = await LoadItemsAsync(target.StoreCode);
        var syncedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (withProducts)
        {
            var targetItemByCode = targetItems.ToDictionary(i => i.ProductCode, StringComparer.OrdinalIgnoreCase);
            var sourceCodes = src.ProductsByCategory.Values.SelectMany(list => list).ToList();
            var targetRows = await LoadRowsByCodeAsync(target.StoreCode, sourceCodes);
            var skippedCodes = new List<string>();
            var syncedByDest = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var sourceCategory in src.Categories)
            {
                var dest = destBySourceGuid[sourceCategory.Guid];
                var list = new List<string>();
                foreach (var code in src.ProductsByCategory[sourceCategory.Guid])
                {
                    // 目标店没有该商品的有效（未删除且启用）价格行：本同步不新建价格行，跳过。
                    if (!targetRows.TryGetValue(code, out var rows) || LatestActiveRow(rows) is null)
                    {
                        skippedCodes.Add(code);
                        continue;
                    }

                    var canonical = rows[0].ProductCode!;
                    var isSpecial = IsCurrentlySpecial(rows);   // 统一口径：启用行里最新一行带标记
                    if (!isSpecial)
                    {
                        plan.RowsToMark.AddRange(rows);
                        result.NewlyMarkedProducts++;
                    }

                    targetItemByCode.TryGetValue(canonical, out var existingItem);
                    var sameCategory = existingItem is not null
                        && string.Equals(existingItem.CategoryGuid, dest.Guid, StringComparison.OrdinalIgnoreCase);
                    if (isSpecial && sameCategory)
                    {
                        result.UnchangedProducts++;
                    }
                    else if (isSpecial && existingItem is not null)
                    {
                        // 目标店里这个特殊商品原来归属别的分类：以来源为准改到同名分类。
                        result.ReassignedProducts++;
                    }

                    list.Add(canonical);
                    syncedCodes.Add(canonical);
                }

                syncedByDest[dest.Guid] = list;
            }

            foreach (var sourceCategory in src.Categories)
            {
                var dest = destBySourceGuid[sourceCategory.Guid];
                var list = syncedByDest[dest.Guid];

                // 目标店该分类里原有、这次没被同步覆盖的归属行：排在同步来的商品之后，保持各自原有相对顺序。
                var rest = targetItems
                    .Where(i => string.Equals(i.CategoryGuid, dest.Guid, StringComparison.OrdinalIgnoreCase)
                        && !syncedCodes.Contains(i.ProductCode))
                    .OrderBy(i => i.SortOrder)
                    .ThenBy(i => i.ProductCode, StringComparer.Ordinal)
                    .ToList();

                var sort = 0;
                foreach (var code in list)
                {
                    if (targetItemByCode.TryGetValue(code, out var item))
                    {
                        if (!string.Equals(item.CategoryGuid, dest.Guid, StringComparison.OrdinalIgnoreCase) || item.SortOrder != sort)
                        {
                            item.CategoryGuid = dest.Guid;
                            item.SortOrder = sort;
                            item.UpdatedAt = now;
                            item.UpdatedBy = actor;
                            plan.ItemUpdates.Add(item);
                        }
                    }
                    else
                    {
                        plan.ItemInserts.Add(new StoreSpecialProductCategoryItem
                        {
                            StoreCode = target.StoreCode,
                            ProductCode = code,
                            CategoryGuid = dest.Guid,
                            SortOrder = sort,
                            CreatedAt = now,
                            CreatedBy = actor,
                            UpdatedAt = now,
                            UpdatedBy = actor,
                        });
                    }

                    sort++;
                }

                foreach (var item in rest)
                {
                    if (item.SortOrder != sort)
                    {
                        item.SortOrder = sort;
                        item.UpdatedAt = now;
                        item.UpdatedBy = actor;
                        plan.ItemUpdates.Add(item);
                    }

                    sort++;
                }
            }

            if (skippedCodes.Count > 0)
            {
                var products = await LoadProductsAsync(skippedCodes);
                result.Skipped = skippedCodes.Select(code => new StoreSpecialCategorySyncSkippedDto
                {
                    ProductCode = code,
                    DisplayName = DisplayNameOf(products.GetValueOrDefault(code), code),
                    Reason = StoreSpecialCategorySkipReasons.NoStorePrice,
                }).ToList();
            }

            if (replace)
            {
                // 目标店里「来源店并不是特殊商品」的特殊商品取消标记（只改标记，不删归属行）。
                // 两边的「特殊商品」都按统一口径（启用行里最新一行带标记）；清标记时连旧行 / 停用行上残留的标记一并清掉。
                var toUnmarkCodes = SpecialCodesOf(await LoadSpecialRowsAsync(target.StoreCode))
                    .Where(code => !src.SpecialCodes.Contains(code))
                    .ToList();
                if (toUnmarkCodes.Count > 0)
                {
                    var allRows = await LoadRowsByCodeAsync(target.StoreCode, toUnmarkCodes);
                    foreach (var code in toUnmarkCodes)
                    {
                        plan.RowsToUnmark.AddRange(allRows.GetValueOrDefault(code) ?? new List<StoreRetailPrice>());
                        result.UnmarkedProducts++;
                    }
                }
            }
        }

        if (replace)
        {
            // 被删分类的归属行一并删除（商品回到未分类）；已被同步改挂到来源分类的商品不删。
            var deletedGuids = plan.CategoryDeletes.Select(c => c.Guid).ToHashSet(StringComparer.OrdinalIgnoreCase);
            plan.ItemDeletes.AddRange(targetItems.Where(i =>
                deletedGuids.Contains(i.CategoryGuid) && !syncedCodes.Contains(i.ProductCode)));
        }

        return plan;
    }

    // ------------------------------------------------------------------ 执行（单店事务）

    private async Task ApplyPlanAsync(TargetPlan plan, StoreContext target, string actor)
    {
        await InTransactionAsync(async () =>
        {
            await ForEachChunkAsync(plan.ItemDeletes, WriteChunkSize, chunk => _db.Deleteable(chunk).ExecuteCommandAsync());
            await ForEachChunkAsync(plan.CategoryDeletes.Select(c => c.Guid).ToList(), QueryChunkSize, chunk =>
                _db.Deleteable<StoreSpecialProductCategory>()
                    .Where(c => c.StoreCode == target.StoreCode && chunk.Contains(c.Guid))
                    .ExecuteCommandAsync());
            await ForEachChunkAsync(plan.CategoryInserts, WriteChunkSize, chunk => _db.Insertable(chunk).ExecuteCommandAsync());
            // 只写颜色、顺序与审计列：不用旧值覆盖并发的改名。
            await ForEachChunkAsync(plan.CategoryUpdates, WriteChunkSize, chunk =>
                _db.Updateable(chunk)
                    .UpdateColumns(c => new { c.Color, c.SortOrder, c.UpdatedAt, c.UpdatedBy })
                    .ExecuteCommandAsync());
            await ForEachChunkAsync(plan.ItemInserts, WriteChunkSize, chunk => _db.Insertable(chunk).ExecuteCommandAsync());
            await ForEachChunkAsync(plan.ItemUpdates, WriteChunkSize, chunk => _db.Updateable(chunk).ExecuteCommandAsync());
            await SetSpecialFlagAsync(plan.RowsToMark, true, actor);
            await SetSpecialFlagAsync(plan.RowsToUnmark, false, actor);

            if (BeforeSyncCommitForTesting is not null)
            {
                await BeforeSyncCommitForTesting(target.StoreCode);
            }
        });
    }
}
