using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Models;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace BlazorApp.Api.Services.LocalSupplierCategories;

/// <summary>
/// 采集写入：沿分类路径逐级 find-or-create 分类，登记货号观察，并即时重算相关商品归属。
/// 同一供应商的写入在 SQL Server 上以事务级应用锁串行；唯一键冲突时整体重试一次。
/// </summary>
public sealed class LocalSupplierCategoryCaptureService : ILocalSupplierCategoryCaptureService
{
    private const int ApplockTimeoutMilliseconds = 10000;
    private const int MaxUnmatchedSamples = 10;

    private readonly ISqlSugarClient _db;
    private readonly IOptionsSnapshot<BrowserExtensionOptions> _options;
    private readonly ILogger<LocalSupplierCategoryCaptureService> _logger;
    private readonly TimeProvider _timeProvider;

    public LocalSupplierCategoryCaptureService(
        SqlSugarContext context,
        IOptionsSnapshot<BrowserExtensionOptions> options,
        ILogger<LocalSupplierCategoryCaptureService> logger,
        TimeProvider? timeProvider = null
    )
    {
        _db = context.Db;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<BrowserExtensionCategoryCaptureResultDto> CaptureAsync(
        BrowserExtensionCategoryCaptureRequestDto request,
        string? actor,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var (supplier, profile, category) = ResolveCaptureProfile(request.SupplierCode);
        var pageUrl = RequireProfileUrl(profile, request.PageUrl, "页面地址");
        var mode = NormalizeMode(request.Mode);
        var path = NormalizePath(profile, category, request.CategoryPath);
        IReadOnlyList<string> itemNumbers;
        try
        {
            itemNumbers = BrowserExtensionPurchaseCycleSqlBuilder.NormalizeItemNumbers(request.ItemNumbers);
        }
        catch (ArgumentException ex)
        {
            throw new LocalSupplierCategoryValidationException(
                LocalSupplierCategoryErrorCodes.InvalidRequest,
                ex.Message
            );
        }

        var patterns = LocalSupplierCategoryPromotionRule.Combine(category.PromotionalPatterns);
        var actorName = NormalizeActor(actor);

        return await RunWithRetryAsync(
            supplier,
            async () =>
            {
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                var writer = await CategoryTreeWriter.LoadAsync(_db, supplier, patterns, actorName, now);
                string? parentGuid = null;
                var names = new List<string>();
                LocalSupplierCategory? leaf = null;
                for (var index = 0; index < path.Count; index++)
                {
                    var node = path[index];
                    names.Add(node.Name);
                    var isLeaf = index == path.Count - 1;
                    leaf = writer.Upsert(
                        node.Key,
                        node.Name,
                        parentGuid,
                        node.Url ?? (isLeaf ? pageUrl : null),
                        sortOrder: null
                    );
                    parentGuid = leaf.CategoryGUID;
                }

                await writer.FlushAsync();
                var leafGuid = leaf!.CategoryGUID;
                await UpsertCapturesAsync(supplier, leafGuid, itemNumbers, pageUrl, mode, actorName, now);

                var assignments = new LocalSupplierCategoryAssignmentService(_db, _timeProvider);
                var reconcile = await assignments.ReconcileByItemNumbersAsync(supplier, itemNumbers, actorName);
                // 规则变化导致已有分类促销标记改变时，同步重算这些分类下曾出现过的货号。
                await ReconcilePromotionChangesAsync(assignments, supplier, writer.PromotionChangedGuids, actorName);

                return new BrowserExtensionCategoryCaptureResultDto
                {
                    CategoryGuid = leafGuid,
                    FullPath = leaf.FullPath,
                    Depth = leaf.Depth,
                    IsPromotional = leaf.IsPromotional,
                    CategoriesCreated = writer.CreatedCount,
                    MatchedProducts = reconcile.Matched,
                    AssignedProducts = reconcile.Assigned + reconcile.Updated,
                    UnchangedProducts = reconcile.Unchanged,
                    SkippedManual = reconcile.ManualSkipped,
                    UnmatchedItemNumberCount = reconcile.UnmatchedKeys.Count,
                    UnmatchedSamples = reconcile.UnmatchedKeys.Take(MaxUnmatchedSamples).ToList(),
                };
            },
            cancellationToken
        );
    }

    public async Task<BrowserExtensionCategoryTreeSnapshotResultDto> ApplyTreeSnapshotAsync(
        BrowserExtensionCategoryTreeSnapshotRequestDto request,
        string? actor,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var (supplier, profile, category) = ResolveCaptureProfile(request.SupplierCode);
        var sourceUrl = RequireProfileUrl(profile, request.SourceUrl, "来源地址");
        if (request.Nodes == null || request.Nodes.Count == 0 || request.Nodes.Count > 2000)
        {
            throw new LocalSupplierCategoryValidationException(
                LocalSupplierCategoryErrorCodes.InvalidRequest,
                "分类节点数量必须在 1 到 2000 之间。"
            );
        }

        var keyParams = category.KeyQueryParams;
        var nodes = new Dictionary<string, SnapshotNode>(StringComparer.Ordinal);
        foreach (var raw in request.Nodes)
        {
            var name = LocalSupplierCategoryResolver.NormalizeName(raw?.Name);
            if (raw == null || name.Length == 0 || !TryNormalizeKey(raw.Key, keyParams, out var key))
            {
                continue;
            }

            string? parentKey = null;
            if (!string.IsNullOrWhiteSpace(raw.ParentKey) && TryNormalizeKey(raw.ParentKey, keyParams, out var normalizedParent))
            {
                parentKey = normalizedParent == key ? null : normalizedParent;
            }

            var url = raw.Url != null && BrowserExtensionProfileCatalog.UrlMatchesProfileOrigins(profile, raw.Url)
                ? raw.Url.Trim()
                : null;
            nodes.TryAdd(key, new SnapshotNode(key, name, parentKey, url, raw.SortOrder));
        }

        if (nodes.Count == 0)
        {
            throw new LocalSupplierCategoryValidationException(
                LocalSupplierCategoryErrorCodes.InvalidRequest,
                "没有可用的分类节点。"
            );
        }

        var patterns = LocalSupplierCategoryPromotionRule.Combine(category.PromotionalPatterns);
        var actorName = NormalizeActor(actor);
        var ordered = OrderParentsFirst(nodes);

        return await RunWithRetryAsync(
            supplier,
            async () =>
            {
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                var writer = await CategoryTreeWriter.LoadAsync(_db, supplier, patterns, actorName, now);
                var guidByKey = new Dictionary<string, string>(StringComparer.Ordinal);
                var orphanCount = 0;
                var promotionalCount = 0;
                foreach (var node in ordered)
                {
                    string? parentGuid = null;
                    if (node.ParentKey != null)
                    {
                        if (guidByKey.TryGetValue(node.ParentKey, out var snapshotParent))
                        {
                            parentGuid = snapshotParent;
                        }
                        else if (writer.TryGet(node.ParentKey, out var existingParent))
                        {
                            parentGuid = existingParent.CategoryGUID;
                        }
                        else
                        {
                            // 父节点既不在本次快照也不在库中：按根节点保存，计入孤儿数供诊断。
                            orphanCount++;
                        }
                    }

                    var entity = writer.Upsert(node.Key, node.Name, parentGuid, node.Url ?? sourceUrl, node.SortOrder);
                    guidByKey[node.Key] = entity.CategoryGUID;
                    if (entity.IsPromotional)
                    {
                        promotionalCount++;
                    }
                }

                await writer.FlushAsync();
                var assignments = new LocalSupplierCategoryAssignmentService(_db, _timeProvider);
                await ReconcilePromotionChangesAsync(assignments, supplier, writer.PromotionChangedGuids, actorName);
                return new BrowserExtensionCategoryTreeSnapshotResultDto
                {
                    Created = writer.CreatedCount,
                    Updated = writer.UpdatedCount,
                    Unchanged = Math.Max(0, ordered.Count - writer.CreatedCount - writer.UpdatedCount),
                    OrphanCount = orphanCount,
                    PromotionalCount = promotionalCount,
                };
            },
            cancellationToken
        );
    }

    private (string Supplier, BrowserExtensionSupplierProfileDto Profile, BrowserExtensionSupplierCategoryProfileDto Category)
        ResolveCaptureProfile(string? supplierCode)
    {
        var options = _options.Value;
        if (!options.CategoryCaptureEnabled)
        {
            throw new LocalSupplierCategoryFeatureDisabledException();
        }

        var supplier = supplierCode?.Trim().ToUpperInvariant() ?? string.Empty;
        if (supplier.Length == 0 || supplier.Length > 50)
        {
            throw new LocalSupplierCategoryValidationException(
                LocalSupplierCategoryErrorCodes.InvalidRequest,
                "供应商代码无效。"
            );
        }

        if (LocalSupplierCategoryConstants.IsHotBargain(supplier))
        {
            throw new LocalSupplierCategoryValidationException(
                LocalSupplierCategoryErrorCodes.SupplierNotCapturable,
                "Hot Bargain 自营商品的供应商分类即仓库分类，不采集网站分类。"
            );
        }

        var profile = BrowserExtensionProfileCatalog.FindProfile(options, supplier)
            ?? throw new KeyNotFoundException("供应商配置不存在或已停用。");
        var category = profile.Category;
        if (category == null || !category.Enabled)
        {
            throw new KeyNotFoundException("该供应商未启用分类采集。");
        }

        return (profile.SupplierCode, profile, category);
    }

    private static string RequireProfileUrl(BrowserExtensionSupplierProfileDto profile, string? url, string fieldName)
    {
        if (!BrowserExtensionProfileCatalog.UrlMatchesProfileOrigins(profile, url))
        {
            throw new LocalSupplierCategoryValidationException(
                LocalSupplierCategoryErrorCodes.InvalidRequest,
                $"{fieldName}不属于该供应商网站。"
            );
        }

        var value = url!.Trim();
        return value.Length <= 1000 ? value : value[..1000];
    }

    private static string NormalizeMode(string? mode)
    {
        var value = mode?.Trim().ToLowerInvariant();
        return value switch
        {
            BrowserExtensionCategoryCaptureModes.Passive => BrowserExtensionCategoryCaptureModes.Passive,
            BrowserExtensionCategoryCaptureModes.Crawl => BrowserExtensionCategoryCaptureModes.Crawl,
            _ => throw new LocalSupplierCategoryValidationException(
                LocalSupplierCategoryErrorCodes.InvalidRequest,
                "采集模式无效。"
            ),
        };
    }

    private static List<PathNode> NormalizePath(
        BrowserExtensionSupplierProfileDto profile,
        BrowserExtensionSupplierCategoryProfileDto category,
        IReadOnlyList<BrowserExtensionCategoryPathNodeDto>? rawPath
    )
    {
        if (rawPath == null || rawPath.Count == 0 || rawPath.Count > LocalSupplierCategoryConstants.MaxPathDepth)
        {
            throw new LocalSupplierCategoryValidationException(
                LocalSupplierCategoryErrorCodes.InvalidRequest,
                $"分类路径层级必须在 1 到 {LocalSupplierCategoryConstants.MaxPathDepth} 之间。"
            );
        }

        var result = new List<PathNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in rawPath)
        {
            var name = LocalSupplierCategoryResolver.NormalizeName(raw?.Name);
            if (raw == null || name.Length == 0 || !TryNormalizeKey(raw.Key, category.KeyQueryParams, out var key))
            {
                throw new LocalSupplierCategoryValidationException(
                    LocalSupplierCategoryErrorCodes.InvalidRequest,
                    "分类路径包含无效节点。"
                );
            }

            if (!seen.Add(key))
            {
                // 面包屑偶尔重复同一链接（如叶子与父级同 URL），保留首个即可。
                continue;
            }

            if (raw.Url != null && !BrowserExtensionProfileCatalog.UrlMatchesProfileOrigins(profile, raw.Url))
            {
                throw new LocalSupplierCategoryValidationException(
                    LocalSupplierCategoryErrorCodes.InvalidRequest,
                    "分类链接不属于该供应商网站。"
                );
            }

            result.Add(new PathNode(key, name, raw.Url?.Trim()));
        }

        return result;
    }

    private static bool TryNormalizeKey(string? raw, IReadOnlyCollection<string> keyParams, out string key)
    {
        try
        {
            key = LocalSupplierCategoryKeyNormalizer.Normalize(raw, keyParams);
            return true;
        }
        catch (ArgumentException)
        {
            key = string.Empty;
            return false;
        }
    }

    private static string? NormalizeActor(string? actor)
    {
        var value = actor?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value.Length <= 100 ? value : value[..100];
    }

    /// <summary>
    /// 快照节点按父先子后排序；有环或父节点缺失的节点按根处理。
    /// </summary>
    private static List<SnapshotNode> OrderParentsFirst(Dictionary<string, SnapshotNode> nodes)
    {
        var depthCache = new Dictionary<string, int>(StringComparer.Ordinal);
        int DepthOf(SnapshotNode node)
        {
            if (depthCache.TryGetValue(node.Key, out var cached))
            {
                return cached;
            }

            var depth = 0;
            var visited = new HashSet<string>(StringComparer.Ordinal) { node.Key };
            var current = node;
            while (current.ParentKey != null && nodes.TryGetValue(current.ParentKey, out var parent) && visited.Add(parent.Key))
            {
                depth++;
                current = parent;
            }

            depthCache[node.Key] = depth;
            return depth;
        }

        return nodes.Values
            .OrderBy(DepthOf)
            .ThenBy(node => node.SortOrder ?? int.MaxValue)
            .ThenBy(node => node.Key, StringComparer.Ordinal)
            .ToList();
    }

    private async Task UpsertCapturesAsync(
        string supplier,
        string categoryGuid,
        IReadOnlyList<string> itemNumbers,
        string pageUrl,
        string mode,
        string? actor,
        DateTime now
    )
    {
        var itemList = itemNumbers.ToList();
        var existing = await _db.Queryable<LocalSupplierCategoryCapture>()
            .Where(capture =>
                capture.LocalSupplierCode == supplier
                && capture.CategoryGUID == categoryGuid
                && itemList.Contains(capture.ItemNumber)
            )
            .Select(capture => capture.ItemNumber)
            .ToListAsync();
        var existingSet = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toInsert = itemList
            .Where(item => !existingSet.Contains(item))
            .Select(item => new LocalSupplierCategoryCapture
            {
                LocalSupplierCode = supplier,
                ItemNumber = item,
                CategoryGUID = categoryGuid,
                FirstSeenAt = now,
                LastSeenAt = now,
                SeenCount = 1,
                LastSourceUrl = pageUrl,
                LastMode = mode,
                CapturedBy = actor,
            })
            .ToList();
        if (toInsert.Count > 0)
        {
            await _db.Insertable(toInsert).ExecuteCommandAsync();
        }

        var existingItems = itemList.Where(existingSet.Contains).ToList();
        if (existingItems.Count > 0)
        {
            // 已见过的观察只累加计数并刷新最近时间，重复采集幂等。
            await _db.Updateable<LocalSupplierCategoryCapture>()
                .SetColumns(capture => new LocalSupplierCategoryCapture
                {
                    SeenCount = capture.SeenCount + 1,
                    LastSeenAt = now,
                    LastSourceUrl = pageUrl,
                    LastMode = mode,
                    CapturedBy = actor,
                })
                .Where(capture =>
                    capture.LocalSupplierCode == supplier
                    && capture.CategoryGUID == categoryGuid
                    && existingItems.Contains(capture.ItemNumber)
                )
                .ExecuteCommandAsync();
        }
    }

    private async Task ReconcilePromotionChangesAsync(
        LocalSupplierCategoryAssignmentService assignments,
        string supplier,
        IReadOnlyCollection<string> changedGuids,
        string? actor
    )
    {
        if (changedGuids.Count == 0)
        {
            return;
        }

        var guidList = changedGuids.ToList();
        var items = await _db.Queryable<LocalSupplierCategoryCapture>()
            .Where(capture => capture.LocalSupplierCode == supplier && guidList.Contains(capture.CategoryGUID))
            .Select(capture => capture.ItemNumber)
            .ToListAsync();
        foreach (var chunk in items.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(500))
        {
            await assignments.ReconcileByItemNumbersAsync(supplier, chunk, actor);
        }
    }

    private async Task<T> RunWithRetryAsync<T>(string supplier, Func<Task<T>> work, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            T? value = default;
            var transaction = await _db.Ado.UseTranAsync(async () =>
            {
                await AcquireSupplierLockAsync(supplier);
                value = await work();
            });
            if (transaction.IsSuccess)
            {
                return value!;
            }

            var exception = transaction.ErrorException ?? new InvalidOperationException("供应商分类写入失败。");
            if (attempt == 1 && IsUniqueViolation(exception))
            {
                // 两个请求同时新建同一分类或同一观察：回滚后重读再写一次即可收敛。
                _logger.LogInformation("供应商分类采集遇到唯一键冲突，重试一次 Supplier={Supplier}", supplier);
                continue;
            }

            throw exception;
        }
    }

    private async Task AcquireSupplierLockAsync(string supplier)
    {
        if (_db.CurrentConnectionConfig.DbType != DbType.SqlServer)
        {
            return;
        }

        var result = await _db.Ado.GetIntAsync(
            """
            DECLARE @Result int;
            EXEC @Result = sys.sp_getapplock
                @Resource = @LockResource,
                @LockMode = N'Exclusive',
                @LockOwner = N'Transaction',
                @LockTimeout = @LockTimeout;
            SELECT @Result;
            """,
            new SugarParameter("@LockResource", $"HB:LocalSupplierCategory:{supplier}"),
            new SugarParameter("@LockTimeout", ApplockTimeoutMilliseconds)
        );
        if (result < 0)
        {
            throw new LocalSupplierCategoryBusyException();
        }
    }

    internal static bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is Microsoft.Data.SqlClient.SqlException sql && (sql.Number == 2627 || sql.Number == 2601))
            {
                return true;
            }

            if (current.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record PathNode(string Key, string Name, string? Url);

    private sealed record SnapshotNode(string Key, string Name, string? ParentKey, string? Url, int? SortOrder);

    /// <summary>
    /// 单供应商分类树的内存写入器：一次载入全部分类，沿路径 find-or-create，最后批量落库。
    /// 父级或名称变化时就地重算子树的完整路径与深度。
    /// </summary>
    private sealed class CategoryTreeWriter
    {
        private readonly ISqlSugarClient _db;
        private readonly IReadOnlyList<string> _patterns;
        private readonly string? _actor;
        private readonly DateTime _now;
        private readonly Dictionary<string, LocalSupplierCategory> _byKey;
        private readonly Dictionary<string, LocalSupplierCategory> _byGuid;
        private readonly HashSet<string> _created = new(StringComparer.Ordinal);
        private readonly HashSet<string> _changed = new(StringComparer.Ordinal);
        private readonly HashSet<string> _touched = new(StringComparer.Ordinal);
        private readonly HashSet<string> _promotionChanged = new(StringComparer.Ordinal);
        private readonly string _supplier;

        private CategoryTreeWriter(
            ISqlSugarClient db,
            string supplier,
            List<LocalSupplierCategory> categories,
            IReadOnlyList<string> patterns,
            string? actor,
            DateTime now
        )
        {
            _db = db;
            _supplier = supplier;
            _patterns = patterns;
            _actor = actor;
            _now = now;
            _byKey = categories
                .GroupBy(category => category.ExternalKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            _byGuid = categories.ToDictionary(category => category.CategoryGUID, StringComparer.Ordinal);
        }

        public int CreatedCount => _created.Count;
        public int UpdatedCount => _changed.Count(guid => !_created.Contains(guid));
        public IReadOnlyCollection<string> PromotionChangedGuids => _promotionChanged;

        public static async Task<CategoryTreeWriter> LoadAsync(
            ISqlSugarClient db,
            string supplier,
            IReadOnlyList<string> patterns,
            string? actor,
            DateTime now
        )
        {
            // 含已软删行：唯一索引覆盖全部行，再次看到时直接复活，避免插入冲突。
            var categories = await db.Queryable<LocalSupplierCategory>()
                .Where(category => category.LocalSupplierCode == supplier)
                .ToListAsync();
            return new CategoryTreeWriter(db, supplier, categories, patterns, actor, now);
        }

        public bool TryGet(string key, out LocalSupplierCategory category) =>
            _byKey.TryGetValue(key, out category!);

        public LocalSupplierCategory Upsert(string key, string name, string? parentGuid, string? url, int? sortOrder)
        {
            var parent = parentGuid != null && _byGuid.TryGetValue(parentGuid, out var found) ? found : null;
            var depth = parent == null ? 0 : parent.Depth + 1;
            var fullPath = parent == null
                ? LocalSupplierCategoryResolver.BuildFullPath(new[] { name })
                : LocalSupplierCategoryResolver.BuildFullPath(new[] { parent.FullPath, name });

            if (!_byKey.TryGetValue(key, out var category))
            {
                category = new LocalSupplierCategory
                {
                    LocalSupplierCode = _supplier,
                    ExternalKey = key,
                    CategoryName = name,
                    ParentGUID = parent?.CategoryGUID,
                    Depth = depth,
                    FullPath = fullPath,
                    SourceUrl = Truncate(url, 1000),
                    IsPromotional = LocalSupplierCategoryPromotionRule.IsPromotional(name, key, _patterns),
                    PromotionalSource = LocalSupplierCategoryPromotionalSources.Pattern,
                    SortOrder = sortOrder,
                    IsActive = true,
                    FirstSeenAt = _now,
                    LastSeenAt = _now,
                    CreatedAt = _now,
                    CreatedBy = _actor,
                    UpdatedAt = _now,
                    UpdatedBy = _actor,
                    IsDeleted = false,
                };
                _byKey[key] = category;
                _byGuid[category.CategoryGUID] = category;
                _created.Add(category.CategoryGUID);
                return category;
            }

            var structureChanged = false;
            if (!string.Equals(category.CategoryName, name, StringComparison.Ordinal))
            {
                category.CategoryName = name;
                structureChanged = true;
            }

            // 防环：新父级不能是自身或自身的后代。
            if (parent != null && !IsDescendantOrSelf(parent, category) && category.ParentGUID != parent.CategoryGUID)
            {
                category.ParentGUID = parent.CategoryGUID;
                structureChanged = true;
            }

            if (category.IsDeleted)
            {
                category.IsDeleted = false;
                MarkChanged(category);
            }

            if (sortOrder.HasValue && category.SortOrder != sortOrder)
            {
                category.SortOrder = sortOrder;
                MarkChanged(category);
            }

            var trimmedUrl = Truncate(url, 1000);
            if (trimmedUrl != null && !string.Equals(category.SourceUrl, trimmedUrl, StringComparison.Ordinal))
            {
                category.SourceUrl = trimmedUrl;
                MarkChanged(category);
            }

            if (
                string.Equals(category.PromotionalSource, LocalSupplierCategoryPromotionalSources.Pattern, StringComparison.Ordinal)
                && category.IsPromotional != LocalSupplierCategoryPromotionRule.IsPromotional(name, key, _patterns)
            )
            {
                category.IsPromotional = !category.IsPromotional;
                _promotionChanged.Add(category.CategoryGUID);
                MarkChanged(category);
            }

            if (structureChanged)
            {
                RecomputeSubtree(category);
            }

            category.LastSeenAt = _now;
            _touched.Add(category.CategoryGUID);
            return category;
        }

        public async Task FlushAsync()
        {
            var inserts = _created.Select(guid => _byGuid[guid]).ToList();
            foreach (var chunk in inserts.Chunk(100))
            {
                await _db.Insertable(chunk.ToList()).ExecuteCommandAsync();
            }

            var updates = _changed
                .Where(guid => !_created.Contains(guid))
                .Select(guid => _byGuid[guid])
                .ToList();
            foreach (var category in updates)
            {
                category.UpdatedAt = _now;
                category.UpdatedBy = _actor;
            }
            foreach (var chunk in updates.Chunk(100))
            {
                await _db.Updateable(chunk.ToList())
                    .UpdateColumns(category => new
                    {
                        category.CategoryName,
                        category.ParentGUID,
                        category.Depth,
                        category.FullPath,
                        category.SourceUrl,
                        category.IsPromotional,
                        category.SortOrder,
                        category.IsDeleted,
                        category.LastSeenAt,
                        category.UpdatedAt,
                        category.UpdatedBy,
                    })
                    .ExecuteCommandAsync();
            }

            // 只被看到、结构未变的分类只刷新最近看到时间，一条语句完成。
            var seenOnly = _touched
                .Where(guid => !_created.Contains(guid) && !_changed.Contains(guid))
                .ToList();
            foreach (var chunk in seenOnly.Chunk(500))
            {
                var chunkList = chunk.ToList();
                await _db.Updateable<LocalSupplierCategory>()
                    .SetColumns(category => category.LastSeenAt == _now)
                    .Where(category => chunkList.Contains(category.CategoryGUID))
                    .ExecuteCommandAsync();
            }
        }

        private void MarkChanged(LocalSupplierCategory category) => _changed.Add(category.CategoryGUID);

        private bool IsDescendantOrSelf(LocalSupplierCategory candidate, LocalSupplierCategory ancestor)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (var current = candidate; current != null && visited.Add(current.CategoryGUID);)
            {
                if (current.CategoryGUID == ancestor.CategoryGUID)
                {
                    return true;
                }

                current = current.ParentGUID != null && _byGuid.TryGetValue(current.ParentGUID, out var parent)
                    ? parent
                    : null;
            }

            return false;
        }

        private void RecomputeSubtree(LocalSupplierCategory root)
        {
            var queue = new Queue<LocalSupplierCategory>();
            queue.Enqueue(root);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                if (!visited.Add(node.CategoryGUID))
                {
                    continue;
                }

                var parent = node.ParentGUID != null && _byGuid.TryGetValue(node.ParentGUID, out var found) ? found : null;
                var depth = parent == null ? 0 : parent.Depth + 1;
                var fullPath = parent == null
                    ? LocalSupplierCategoryResolver.BuildFullPath(new[] { node.CategoryName })
                    : LocalSupplierCategoryResolver.BuildFullPath(new[] { parent.FullPath, node.CategoryName });
                if (node.Depth != depth || !string.Equals(node.FullPath, fullPath, StringComparison.Ordinal) || node == root)
                {
                    node.Depth = depth;
                    node.FullPath = fullPath;
                    MarkChanged(node);
                }

                foreach (var child in _byGuid.Values.Where(item => item.ParentGUID == node.CategoryGUID))
                {
                    queue.Enqueue(child);
                }
            }
        }

        private static string? Truncate(string? value, int maxLength)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return null;
            }

            return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
        }
    }
}
