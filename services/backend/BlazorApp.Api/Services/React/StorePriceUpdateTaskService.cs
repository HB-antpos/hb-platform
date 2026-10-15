using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Services.React;

/// <summary>
/// 分店价格更新任务的核心服务。
/// 设计要点：任务 = "当前差异"的物化。形态（需改价 / 待换标签）与取消都由实时值推导，
/// 因此无论价格从哪个入口被改，任务都会自己落到正确状态，"改回去自动取消"是规则的自然结果。
/// </summary>
public sealed class StorePriceUpdateTaskService : IStorePriceUpdateTaskService
{
    private const int CodeBatchSize = 500;
    private const int MaxPageSize = 200;
    private const string HqSyncBlockedStatus = "blocked";
    // 分店标记换标签 / 打印完成时入 HQ 同步队列的来源标识。
    private const string PriceTaskLabelDoneHqSource = "price-task-label-done";

    private readonly ISqlSugarClient _db;
    private readonly ILogger<StorePriceUpdateTaskService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IPriceNotificationSummaryAccessor _summary;
    private readonly IServiceProvider _serviceProvider;
    private bool? _schemaReady;

    public StorePriceUpdateTaskService(
        SqlSugarContext context,
        ILogger<StorePriceUpdateTaskService> logger,
        IConfiguration configuration,
        IPriceNotificationSummaryAccessor summary,
        IServiceProvider serviceProvider
    )
    {
        _db = context.Db;
        _logger = logger;
        _configuration = configuration;
        _summary = summary;
        _serviceProvider = serviceProvider;
    }

    /// <summary>通知页改价是否同步 HQ 数据库。后期取消同步时只需改配置，无需发版。</summary>
    public bool IsHqSyncEnabled =>
        _configuration.GetValue("StorePriceUpdateTasks:SyncToHq", true);

    private int OverdueDays =>
        Math.Max(1, _configuration.GetValue("StorePriceUpdateTasks:OverdueDays", 3));

    private int RetentionDays =>
        Math.Max(7, _configuration.GetValue("StorePriceUpdateTasks:RetentionDays", 90));

    // =====================================================================
    // 纯函数：比较规则。预告、建任务、对账全部共用，保证"预告说 6 家，保存后就是 6 家"。
    // =====================================================================

    internal readonly record struct PriceEvaluation(bool NeedsRetail, bool NeedsDiscount, bool LabelStale)
    {
        public bool NeedsPrice => NeedsRetail || NeedsDiscount;
    }

    internal static decimal? Money(decimal? value) =>
        value.HasValue ? Math.Round(value.Value, 2, MidpointRounding.AwayFromZero) : null;

    /// <summary>折扣 null 视同 0（分店未设折扣即无折扣）。</summary>
    internal static decimal Rate(decimal? value) =>
        Math.Round(value ?? 0m, 4, MidpointRounding.AwayFromZero);

    internal static PriceEvaluation Evaluate(
        decimal? storeRetail,
        decimal? storeDiscount,
        bool isAutoPricing,
        decimal? targetRetail,
        decimal? targetDiscount,
        decimal? shelfRetail,
        decimal? shelfDiscount
    )
    {
        // 自动定价的分店零售价由进价策略推导，写入仓库价也会被立刻重算，比较它只会产生永远完不成的任务。
        var needsRetail =
            !isAutoPricing && targetRetail.HasValue && Money(storeRetail) != Money(targetRetail);
        // 建议折扣为 null = 未设置，不与分店折扣比较；0 = 明确无折扣。
        var needsDiscount = targetDiscount.HasValue && Rate(storeDiscount) != Rate(targetDiscount);
        var labelStale =
            Money(shelfRetail) != Money(storeRetail) || Rate(shelfDiscount) != Rate(storeDiscount);
        return new PriceEvaluation(needsRetail, needsDiscount, labelStale);
    }

    // =====================================================================
    // 任务生成
    // =====================================================================

    public Task OnWarehousePriceChangedAsync(
        IReadOnlyCollection<string> productCodes,
        PriceTaskInitiator initiator,
        CancellationToken cancellationToken = default
    ) => EvaluateProductsAsync(productCodes, null, initiator, null, allowCreate: true, cancellationToken);

    public Task RecordStoreOverwritesAsync(
        IReadOnlyCollection<StorePriceOverwrite> overwrites,
        PriceTaskInitiator initiator,
        CancellationToken cancellationToken = default
    )
    {
        if (overwrites.Count == 0)
        {
            return Task.CompletedTask;
        }

        var byKey = new Dictionary<(string, string), StorePriceOverwrite>();
        foreach (var overwrite in overwrites)
        {
            if (string.IsNullOrWhiteSpace(overwrite.StoreCode) || string.IsNullOrWhiteSpace(overwrite.ProductCode))
            {
                continue;
            }
            byKey[Key(overwrite.StoreCode, overwrite.ProductCode)] = overwrite;
        }

        var codes = byKey.Values.Select(item => item.ProductCode.Trim()).Distinct().ToList();
        return EvaluateProductsAsync(codes, null, initiator, byKey, allowCreate: true, cancellationToken);
    }

    public async Task<int> ReconcilePendingAsync(string? storeCode, CancellationToken cancellationToken = default)
    {
        if (!await IsSchemaReadyAsync())
        {
            return 0;
        }

        var pendingStatus = StorePriceUpdateTaskStatuses.Pending;
        var query = _db.Queryable<StorePriceUpdateTask>().Where(task => task.Status == pendingStatus);
        if (!string.IsNullOrWhiteSpace(storeCode))
        {
            var normalizedStore = storeCode.Trim();
            query = query.Where(task => task.StoreCode == normalizedStore);
        }

        var codes = await query.Select(task => task.ProductCode).Distinct().ToListAsync();
        if (codes.Count == 0)
        {
            return 0;
        }

        await EvaluateProductsAsync(codes, storeCode, null, null, allowCreate: false, cancellationToken);
        return codes.Count;
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsSchemaReadyAsync())
        {
            return 0;
        }

        // 只清理已结束的任务；Pending 永不按时间清理。范围由状态 + 截止时间双重限定。
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        var pendingStatus = StorePriceUpdateTaskStatuses.Pending;
        return await _db.Deleteable<StorePriceUpdateTask>()
            .Where(task => task.Status != pendingStatus && task.UpdatedAtUtc < cutoff)
            .ExecuteCommandAsync();
    }

    private async Task EvaluateProductsAsync(
        IReadOnlyCollection<string> productCodes,
        string? storeCodeFilter,
        PriceTaskInitiator? initiator,
        IReadOnlyDictionary<(string, string), StorePriceOverwrite>? overwrites,
        bool allowCreate,
        CancellationToken cancellationToken
    )
    {
        var codes = productCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codes.Count == 0 || !await IsSchemaReadyAsync())
        {
            return;
        }

        var activeStoreCodes = (
            await _db.Queryable<Store>()
                .Where(store => store.IsActive && !store.IsDeleted)
                .Select(store => store.StoreCode)
                .ToListAsync()
        )
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct()
            .ToList();
        if (!string.IsNullOrWhiteSpace(storeCodeFilter))
        {
            var only = storeCodeFilter.Trim();
            activeStoreCodes = activeStoreCodes
                .Where(code => string.Equals(code, only, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        if (activeStoreCodes.Count == 0)
        {
            return;
        }

        foreach (var batch in codes.Chunk(CodeBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EvaluateBatchAsync(batch.ToList(), activeStoreCodes, initiator, overwrites, allowCreate);
        }
    }

    private async Task EvaluateBatchAsync(
        List<string> codes,
        List<string> activeStoreCodes,
        PriceTaskInitiator? initiator,
        IReadOnlyDictionary<(string, string), StorePriceOverwrite>? overwrites,
        bool allowCreate
    )
    {
        var targets = await LoadTargetsAsync(codes);
        var storeRows = await _db.Queryable<StoreRetailPrice>()
            .Where(row =>
                row.ProductCode != null
                && codes.Contains(row.ProductCode)
                && row.StoreCode != null
                && activeStoreCodes.Contains(row.StoreCode)
                && !row.IsDeleted
            )
            .ToListAsync();
        var pendingStatus = StorePriceUpdateTaskStatuses.Pending;
        var pendingTasks = await _db.Queryable<StorePriceUpdateTask>()
            .Where(task =>
                task.Status == pendingStatus
                && codes.Contains(task.ProductCode)
                && activeStoreCodes.Contains(task.StoreCode)
            )
            .ToListAsync();
        var pendingByKey = pendingTasks
            .GroupBy(task => Key(task.StoreCode, task.ProductCode))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(task => task.Id).First());

        var now = DateTime.UtcNow;
        var inserts = new List<StorePriceUpdateTask>();
        var updates = new List<StorePriceUpdateTask>();
        var seenKeys = new HashSet<(string, string)>();

        foreach (var row in storeRows)
        {
            var key = Key(row.StoreCode!, row.ProductCode!);
            if (!seenKeys.Add(key))
            {
                continue;
            }

            pendingByKey.TryGetValue(key, out var task);
            if (!targets.TryGetValue(row.ProductCode!.Trim(), out var target))
            {
                continue;
            }

            if (row.IsSpecialProduct)
            {
                // 特殊商品由分店刻意定价：不建任务；已有任务也随之失效。
                if (task != null)
                {
                    Close(task, StorePriceUpdateTaskStatuses.Cancelled, now);
                    task.CancelReason = StorePriceUpdateTaskCancelReasons.NoLongerApplicable;
                    updates.Add(task);
                }
                else if (allowCreate)
                {
                    var wouldNotify = Evaluate(
                        row.StoreRetailPriceValue, row.DiscountRate, row.IsAutoPricing,
                        target.RetailPrice, target.DiscountRate,
                        row.StoreRetailPriceValue, row.DiscountRate
                    ).NeedsPrice;
                    if (wouldNotify)
                    {
                        _summary.Record(row.StoreCode!, row.ProductCode!, PriceNotificationOutcome.SkippedSpecial);
                    }
                }
                continue;
            }

            StorePriceOverwrite? overwrite = null;
            overwrites?.TryGetValue(key, out overwrite);
            // 货架基准：已有任务沿用任务里的；新任务优先用覆盖前的旧值，否则就是分店现值。
            decimal? shelfRetail;
            decimal? shelfDiscount;
            if (task != null)
            {
                shelfRetail = task.ShelfRetailPrice;
                shelfDiscount = task.ShelfDiscountRate;
            }
            else if (overwrite != null)
            {
                shelfRetail = overwrite.OldRetailPrice;
                shelfDiscount = overwrite.OldDiscountRate;
            }
            else
            {
                shelfRetail = row.StoreRetailPriceValue;
                shelfDiscount = row.DiscountRate;
            }

            var evaluation = Evaluate(
                row.StoreRetailPriceValue, row.DiscountRate, row.IsAutoPricing,
                target.RetailPrice, target.DiscountRate,
                shelfRetail, shelfDiscount
            );

            if (task == null)
            {
                if (!allowCreate || (!evaluation.NeedsPrice && !evaluation.LabelStale))
                {
                    if (allowCreate)
                    {
                        _summary.Record(row.StoreCode!, row.ProductCode!, PriceNotificationOutcome.None);
                    }
                    continue;
                }

                var kind = evaluation.NeedsPrice
                    ? StorePriceUpdateTaskKinds.PriceUpdate
                    : StorePriceUpdateTaskKinds.LabelOnly;
                inserts.Add(
                    new StorePriceUpdateTask
                    {
                        StoreCode = row.StoreCode!.Trim(),
                        ProductCode = row.ProductCode!.Trim(),
                        Status = StorePriceUpdateTaskStatuses.Pending,
                        Kind = kind,
                        ShelfRetailPrice = Money(shelfRetail),
                        ShelfDiscountRate = Rate(shelfDiscount),
                        TargetRetailPrice = Money(target.RetailPrice),
                        TargetDiscountRate = target.DiscountRate.HasValue ? Rate(target.DiscountRate) : null,
                        StoreRetailPrice = Money(row.StoreRetailPriceValue),
                        StoreDiscountRate = Rate(row.DiscountRate),
                        InitiatorName = Truncate(initiator?.Name, 100) ?? "System",
                        InitiatorSource = Truncate(initiator?.Source, 80) ?? "Unknown",
                        InitiatorReference = Truncate(initiator?.Reference, 200),
                        InitiatedAtUtc = initiator?.OccurredAtUtc ?? now,
                        ChangeCount = 1,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                    }
                );
                _summary.Record(
                    row.StoreCode!,
                    row.ProductCode!,
                    evaluation.NeedsPrice
                        ? PriceNotificationOutcome.NeedsPriceUpdate
                        : PriceNotificationOutcome.LabelOnly
                );
                continue;
            }

            // ---- 已有未完成任务：刷新目标、推导形态，差异消失则结束 ----
            var targetChanged =
                Money(task.TargetRetailPrice) != Money(target.RetailPrice)
                || (task.TargetDiscountRate.HasValue ? Rate(task.TargetDiscountRate) : (decimal?)null)
                    != (target.DiscountRate.HasValue ? Rate(target.DiscountRate) : (decimal?)null);
            task.TargetRetailPrice = Money(target.RetailPrice);
            task.TargetDiscountRate = target.DiscountRate.HasValue ? Rate(target.DiscountRate) : null;
            task.StoreRetailPrice = Money(row.StoreRetailPriceValue);
            task.StoreDiscountRate = Rate(row.DiscountRate);
            task.UpdatedAtUtc = now;
            if (targetChanged && initiator != null)
            {
                task.ChangeCount += 1;
                task.InitiatorName = Truncate(initiator.Name, 100) ?? task.InitiatorName;
                task.InitiatorSource = Truncate(initiator.Source, 80) ?? task.InitiatorSource;
                task.InitiatorReference = Truncate(initiator.Reference, 200);
                task.InitiatedAtUtc = initiator.OccurredAtUtc ?? now;
            }

            if (!evaluation.NeedsPrice && !evaluation.LabelStale)
            {
                if (task.PriceAppliedAtUtc.HasValue)
                {
                    // 店员在通知页改过价，且改完恰好等于货架价：标签本来就对，直接完成。
                    Close(task, StorePriceUpdateTaskStatuses.Completed, now);
                    task.CompletionMode = StorePriceUpdateTaskCompletionModes.PriceAligned;
                    task.CompletedBy = task.PriceAppliedBy;
                }
                else
                {
                    // 目标价回到货架价（仓库改回去）：通知自动取消。
                    Close(task, StorePriceUpdateTaskStatuses.Cancelled, now);
                    task.CancelReason = StorePriceUpdateTaskCancelReasons.Reverted;
                    if (initiator != null)
                    {
                        _summary.Record(task.StoreCode, task.ProductCode, PriceNotificationOutcome.Cancelled);
                    }
                }
            }
            else
            {
                task.Kind = evaluation.NeedsPrice
                    ? StorePriceUpdateTaskKinds.PriceUpdate
                    : StorePriceUpdateTaskKinds.LabelOnly;
                if (initiator != null)
                {
                    _summary.Record(
                        task.StoreCode,
                        task.ProductCode,
                        evaluation.NeedsPrice
                            ? PriceNotificationOutcome.NeedsPriceUpdate
                            : PriceNotificationOutcome.LabelOnly
                    );
                }
            }
            updates.Add(task);
        }

        // 分店商品记录已不存在（被删除/分店停用）的任务不再有意义。
        foreach (var orphan in pendingTasks.Where(task => !seenKeys.Contains(Key(task.StoreCode, task.ProductCode))))
        {
            Close(orphan, StorePriceUpdateTaskStatuses.Cancelled, now);
            orphan.CancelReason = StorePriceUpdateTaskCancelReasons.NoLongerApplicable;
            updates.Add(orphan);
        }

        if (updates.Count > 0)
        {
            await _db.Updateable(updates).ExecuteCommandAsync();
        }
        if (inserts.Count > 0)
        {
            await _db.Insertable(inserts).PageSize(100).ExecuteCommandAsync();
        }
    }

    private static void Close(StorePriceUpdateTask task, string status, DateTime now)
    {
        task.Status = status;
        task.UpdatedAtUtc = now;
        if (status == StorePriceUpdateTaskStatuses.Completed)
        {
            task.CompletedAtUtc = now;
        }
    }

    private sealed record PriceTarget(decimal? RetailPrice, decimal? DiscountRate);

    private async Task<Dictionary<string, PriceTarget>> LoadTargetsAsync(List<string> codes)
    {
        // 与审计快照同一口径：仓库零售价优先取 WarehouseProduct.OEMPrice，其次 Product.RetailPrice。
        var warehouse = await _db.Queryable<WarehouseProduct>()
            .Where(item => codes.Contains(item.ProductCode) && !item.IsDeleted)
            .Select(item => new { item.ProductCode, item.OEMPrice })
            .ToListAsync();
        var products = await _db.Queryable<Product>()
            .Where(item => item.ProductCode != null && codes.Contains(item.ProductCode) && !item.IsDeleted)
            .Select(item => new { item.ProductCode, item.RetailPrice })
            .ToListAsync();
        var discounts = await GetSuggestedDiscountsAsync(codes);

        var result = new Dictionary<string, PriceTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in codes)
        {
            var warehouseRetail = warehouse
                .FirstOrDefault(item => string.Equals(item.ProductCode, code, StringComparison.OrdinalIgnoreCase))
                ?.OEMPrice;
            var productRow = products
                .FirstOrDefault(item => string.Equals(item.ProductCode, code, StringComparison.OrdinalIgnoreCase));
            if (warehouseRetail == null && productRow == null)
            {
                continue;
            }

            discounts.TryGetValue(code, out var discount);
            result[code] = new PriceTarget(warehouseRetail ?? productRow?.RetailPrice, discount);
        }
        return result;
    }

    public async Task<PriceNotificationPreviewDto> PreviewAsync(
        string productCode,
        decimal? retailPrice,
        bool suggestedDiscountSpecified,
        decimal? suggestedDiscountRate,
        CancellationToken cancellationToken = default
    )
    {
        var preview = new PriceNotificationPreviewDto();
        var code = productCode?.Trim();
        if (string.IsNullOrWhiteSpace(code) || !await IsSchemaReadyAsync())
        {
            return preview;
        }

        var targets = await LoadTargetsAsync(new List<string> { code });
        targets.TryGetValue(code, out var current);
        var targetRetail = retailPrice ?? current?.RetailPrice;
        var targetDiscount = suggestedDiscountSpecified ? suggestedDiscountRate : current?.DiscountRate;

        var activeStoreCodes = await _db.Queryable<Store>()
            .Where(store => store.IsActive && !store.IsDeleted)
            .Select(store => store.StoreCode)
            .ToListAsync();
        var rows = await _db.Queryable<StoreRetailPrice>()
            .Where(row =>
                row.ProductCode == code
                && row.StoreCode != null
                && activeStoreCodes.Contains(row.StoreCode)
                && !row.IsDeleted
            )
            .ToListAsync();
        foreach (var row in rows)
        {
            var needsPrice = Evaluate(
                row.StoreRetailPriceValue, row.DiscountRate, row.IsAutoPricing,
                targetRetail, targetDiscount,
                row.StoreRetailPriceValue, row.DiscountRate
            ).NeedsPrice;
            if (!needsPrice)
            {
                continue;
            }
            if (row.IsSpecialProduct)
            {
                preview.SkippedSpecialStores += 1;
            }
            else
            {
                preview.AffectedStores += 1;
            }
        }
        return preview;
    }

    // =====================================================================
    // 建议折扣
    // =====================================================================

    public async Task<IReadOnlyDictionary<string, decimal?>> GetSuggestedDiscountsAsync(
        IReadOnlyCollection<string> productCodes,
        CancellationToken cancellationToken = default
    )
    {
        var result = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase);
        var codes = productCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codes.Count == 0 || !await IsSchemaReadyAsync())
        {
            return result;
        }

        foreach (var batch in codes.Chunk(CodeBatchSize))
        {
            var batchCodes = batch.ToList();
            var rows = await _db.Queryable<ProductSuggestedDiscount>()
                .Where(item => batchCodes.Contains(item.ProductCode))
                .ToListAsync();
            foreach (var row in rows)
            {
                result[row.ProductCode] = row.SuggestedDiscountRate;
            }
        }
        return result;
    }

    public async Task<bool> SetSuggestedDiscountAsync(
        string productCode,
        decimal? suggestedDiscountRate,
        string updatedBy,
        CancellationToken cancellationToken = default
    )
    {
        var code = productCode?.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("商品编码不能为空", nameof(productCode));
        }
        if (suggestedDiscountRate is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(suggestedDiscountRate), "建议折扣必须在 0 到 1 之间");
        }
        if (!await IsSchemaReadyAsync())
        {
            return false;
        }

        var normalized = suggestedDiscountRate.HasValue ? Rate(suggestedDiscountRate) : (decimal?)null;
        var existing = await _db.Queryable<ProductSuggestedDiscount>()
            .Where(item => item.ProductCode == code)
            .FirstAsync();
        if (existing == null)
        {
            if (normalized == null)
            {
                return false;
            }
            await _db.Insertable(
                    new ProductSuggestedDiscount
                    {
                        ProductCode = code,
                        SuggestedDiscountRate = normalized,
                        UpdatedAtUtc = DateTime.UtcNow,
                        UpdatedBy = Truncate(updatedBy, 255),
                    }
                )
                .ExecuteCommandAsync();
            return true;
        }

        var previous = existing.SuggestedDiscountRate.HasValue ? Rate(existing.SuggestedDiscountRate) : (decimal?)null;
        if (previous == normalized)
        {
            return false;
        }

        existing.SuggestedDiscountRate = normalized;
        existing.UpdatedAtUtc = DateTime.UtcNow;
        existing.UpdatedBy = Truncate(updatedBy, 255);
        await _db.Updateable(existing).ExecuteCommandAsync();
        return true;
    }

    // =====================================================================
    // 移动端
    // =====================================================================

    public async Task<int> SetSuggestedDiscountsWithHistoryAsync(
        IReadOnlyCollection<string> productCodes,
        decimal? suggestedDiscountRate,
        string updatedBy,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        var codes = productCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codes.Count == 0 || !await IsSchemaReadyAsync())
        {
            return 0;
        }

        // 延迟解析：审计服务依赖本服务，构造期互相注入会形成依赖环。
        var history = _serviceProvider.GetRequiredService<IWarehouseProductChangeHistoryService>();
        var ownsTransaction = _db.Ado.Transaction == null;
        if (ownsTransaction)
        {
            await _db.Ado.BeginTranAsync();
        }

        try
        {
            var before = await history.CaptureSnapshotsAsync(codes, cancellationToken);
            var changed = 0;
            // 只处理真实存在的商品：快照里没有的编码直接忽略，避免给不存在的商品留下孤儿折扣。
            foreach (var code in before.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await SetSuggestedDiscountAsync(code, suggestedDiscountRate, updatedBy, cancellationToken))
                {
                    changed += 1;
                }
            }

            if (changed > 0)
            {
                var after = await history.CaptureSnapshotsAsync(codes, cancellationToken);
                await history.RecordChangesAsync(
                    before,
                    after,
                    new WarehouseProductChangeHistoryContextDto
                    {
                        Action = "Update",
                        Source = source,
                        ActorName = updatedBy,
                        BatchGuid = codes.Count > 1 ? Guid.NewGuid() : null,
                    },
                    cancellationToken
                );
            }

            if (ownsTransaction)
            {
                await _db.Ado.CommitTranAsync();
            }
            return changed;
        }
        catch
        {
            if (ownsTransaction)
            {
                await _db.Ado.RollbackTranAsync();
            }
            throw;
        }
    }

    public async Task<int> GetPendingCountAsync(string storeCode, CancellationToken cancellationToken = default)
    {
        var code = storeCode?.Trim();
        if (string.IsNullOrWhiteSpace(code) || !await IsSchemaReadyAsync())
        {
            return 0;
        }
        var pendingStatus = StorePriceUpdateTaskStatuses.Pending;
        return await _db.Queryable<StorePriceUpdateTask>()
            .Where(task => task.StoreCode == code && task.Status == pendingStatus)
            .CountAsync();
    }

    public async Task<StorePriceUpdateTaskPageDto> GetPageAsync(
        StorePriceUpdateTaskQueryDto query,
        IReadOnlyCollection<string>? accessibleStoreCodes,
        CancellationToken cancellationToken = default
    )
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var result = new StorePriceUpdateTaskPageDto
        {
            Page = page,
            PageSize = pageSize,
            HqSyncEnabled = IsHqSyncEnabled,
        };
        if (!await IsSchemaReadyAsync())
        {
            return result;
        }

        var storeCodes = query.ResolveStoreCodes();
        var status = string.IsNullOrWhiteSpace(query.Status)
            ? StorePriceUpdateTaskStatuses.Pending
            : query.Status.Trim();
        if (storeCodes.Count == 1 && status == StorePriceUpdateTaskStatuses.Pending && page == 1)
        {
            // 兜底对账：返回前先用实时价格复核，保证"改回去自动取消"即使漏了事件也成立。
            // 仅单店时触发（移动端按店查看的场景）；多店/全部与原先「不按店过滤」一致，交给后台对账任务。
            await ReconcilePendingAsync(storeCodes[0], cancellationToken);
        }

        var filtered = await BuildFilteredQueryAsync(query, accessibleStoreCodes, applyStatus: true);
        RefAsync<int> total = 0;
        var ordered = status == StorePriceUpdateTaskStatuses.Pending
            ? filtered.OrderBy(task => task.InitiatedAtUtc, OrderByType.Desc).OrderBy(task => task.Id, OrderByType.Desc)
            : filtered.OrderBy(task => task.CompletedAtUtc, OrderByType.Desc).OrderBy(task => task.Id, OrderByType.Desc);
        var tasks = await ordered.ToPageListAsync(page, pageSize, total);
        result.Total = total;
        result.Items = await MapTasksAsync(tasks);

        if (storeCodes.Count > 0)
        {
            var pendingStatus = StorePriceUpdateTaskStatuses.Pending;
            var completedStatus = StorePriceUpdateTaskStatuses.Completed;
            var priceKind = StorePriceUpdateTaskKinds.PriceUpdate;
            var scope = _db.Queryable<StorePriceUpdateTask>().Where(task => storeCodes.Contains(task.StoreCode));
            result.PendingCount = await scope.Clone().Where(task => task.Status == pendingStatus).CountAsync();
            result.PendingPriceUpdateCount = await scope.Clone()
                .Where(task => task.Status == pendingStatus && task.Kind == priceKind)
                .CountAsync();
            result.PendingLabelOnlyCount = result.PendingCount - result.PendingPriceUpdateCount;
            result.CompletedCount = await scope.Clone().Where(task => task.Status == completedStatus).CountAsync();
        }
        return result;
    }

    private async Task<ISugarQueryable<StorePriceUpdateTask>> BuildFilteredQueryAsync(
        StorePriceUpdateTaskQueryDto query,
        IReadOnlyCollection<string>? accessibleStoreCodes,
        bool applyStatus
    )
    {
        var filtered = _db.Queryable<StorePriceUpdateTask>();
        if (accessibleStoreCodes != null)
        {
            var accessible = accessibleStoreCodes.ToList();
            filtered = filtered.Where(task => accessible.Contains(task.StoreCode));
        }

        var storeCodes = query.ResolveStoreCodes();
        if (storeCodes.Count > 0)
        {
            filtered = filtered.Where(task => storeCodes.Contains(task.StoreCode));
        }

        if (applyStatus)
        {
            var status = string.IsNullOrWhiteSpace(query.Status)
                ? StorePriceUpdateTaskStatuses.Pending
                : query.Status.Trim();
            if (!string.Equals(status, "All", StringComparison.OrdinalIgnoreCase))
            {
                filtered = filtered.Where(task => task.Status == status);
            }
        }

        var kind = query.Kind?.Trim();
        if (!string.IsNullOrWhiteSpace(kind))
        {
            filtered = filtered.Where(task => task.Kind == kind);
        }

        var initiator = query.InitiatorName?.Trim();
        if (!string.IsNullOrWhiteSpace(initiator))
        {
            filtered = filtered.Where(task => task.InitiatorName == initiator);
        }

        if (query.FromUtc.HasValue)
        {
            var from = query.FromUtc.Value;
            filtered = filtered.Where(task => task.InitiatedAtUtc >= from);
        }
        if (query.ToUtc.HasValue)
        {
            var to = query.ToUtc.Value;
            filtered = filtered.Where(task => task.InitiatedAtUtc <= to);
        }

        if (query.HqSyncFailedOnly == true)
        {
            var blocked = HqSyncBlockedStatus;
            filtered = filtered.Where(task =>
                task.HqSyncOperationKey != null
                && SqlFunc.Subqueryable<ProductHqSyncOutbox>()
                    .Where(outbox => outbox.OperationKey == task.HqSyncOperationKey && outbox.Status == blocked)
                    .Any()
            );
        }

        var keyword = query.Keyword?.Trim();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            // 任务表不冗余品名/货号：先按关键字找商品编码（有上限），再过滤任务。
            var matchedCodes = await _db.Queryable<Product>()
                .Where(product =>
                    product.ProductCode != null
                    && !product.IsDeleted
                    && (
                        product.ProductCode.Contains(keyword)
                        || product.ItemNumber.Contains(keyword)
                        || product.ProductName.Contains(keyword)
                        || product.Barcode.Contains(keyword)
                    )
                )
                .Select(product => product.ProductCode)
                .Take(500)
                .ToListAsync();
            var codes = matchedCodes.Where(code => code != null).Select(code => code!).Distinct().ToList();
            filtered = filtered.Where(task => codes.Contains(task.ProductCode));
        }

        return filtered;
    }

    private async Task<List<StorePriceUpdateTaskDto>> MapTasksAsync(List<StorePriceUpdateTask> tasks)
    {
        if (tasks.Count == 0)
        {
            return new List<StorePriceUpdateTaskDto>();
        }

        var productCodes = tasks.Select(task => task.ProductCode).Distinct().ToList();
        var storeCodes = tasks.Select(task => task.StoreCode).Distinct().ToList();
        var products = await _db.Queryable<Product>()
            .Where(product => product.ProductCode != null && productCodes.Contains(product.ProductCode))
            .Select(product => new
            {
                product.ProductCode,
                product.ProductName,
                product.ItemNumber,
                product.Barcode,
                product.ProductImage,
                product.IsDeleted,
            })
            .ToListAsync();
        var productByCode = products
            .GroupBy(product => product.ProductCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.IsDeleted).First(), StringComparer.OrdinalIgnoreCase);
        var stores = await _db.Queryable<Store>()
            .Where(store => storeCodes.Contains(store.StoreCode))
            .Select(store => new { store.StoreCode, store.StoreName })
            .ToListAsync();
        var storeNames = stores
            .GroupBy(store => store.StoreCode)
            .ToDictionary(group => group.Key, group => group.First().StoreName);
        var priceRows = await _db.Queryable<StoreRetailPrice>()
            .Where(row =>
                row.ProductCode != null
                && productCodes.Contains(row.ProductCode)
                && row.StoreCode != null
                && storeCodes.Contains(row.StoreCode)
                && !row.IsDeleted
            )
            .Select(row => new { row.UUID, row.StoreCode, row.ProductCode })
            .ToListAsync();
        var uuidByKey = priceRows
            .GroupBy(row => Key(row.StoreCode!, row.ProductCode!))
            .ToDictionary(group => group.Key, group => group.First().UUID);

        var operationKeys = tasks
            .Where(task => !string.IsNullOrWhiteSpace(task.HqSyncOperationKey))
            .Select(task => task.HqSyncOperationKey!)
            .Distinct()
            .ToList();
        var hqStatuses = new Dictionary<string, string>();
        if (operationKeys.Count > 0 && IsHqSyncEnabled)
        {
            var rows = await _db.Queryable<ProductHqSyncOutbox>()
                .Where(outbox => operationKeys.Contains(outbox.OperationKey))
                .Select(outbox => new { outbox.OperationKey, outbox.Status })
                .ToListAsync();
            foreach (var row in rows)
            {
                hqStatuses[row.OperationKey] = row.Status;
            }
        }

        return tasks.Select(task =>
        {
            productByCode.TryGetValue(task.ProductCode, out var product);
            storeNames.TryGetValue(task.StoreCode, out var storeName);
            uuidByKey.TryGetValue(Key(task.StoreCode, task.ProductCode), out var uuid);
            string? hqStatus = null;
            if (task.HqSyncOperationKey != null)
            {
                hqStatuses.TryGetValue(task.HqSyncOperationKey, out hqStatus);
            }

            var changedFields = new List<string>();
            var compareRetail = task.Kind == StorePriceUpdateTaskKinds.PriceUpdate ? task.StoreRetailPrice : task.ShelfRetailPrice;
            var compareDiscount = task.Kind == StorePriceUpdateTaskKinds.PriceUpdate ? task.StoreDiscountRate : task.ShelfDiscountRate;
            var endRetail = task.Kind == StorePriceUpdateTaskKinds.PriceUpdate ? task.TargetRetailPrice : task.StoreRetailPrice;
            var endDiscount = task.Kind == StorePriceUpdateTaskKinds.PriceUpdate ? task.TargetDiscountRate : task.StoreDiscountRate;
            if (endRetail.HasValue && Money(compareRetail) != Money(endRetail))
            {
                changedFields.Add("retailPrice");
            }
            if (endDiscount.HasValue && Rate(compareDiscount) != Rate(endDiscount))
            {
                changedFields.Add("discountRate");
            }

            return new StorePriceUpdateTaskDto
            {
                Id = task.Id,
                StoreCode = task.StoreCode,
                StoreName = storeName,
                ProductCode = task.ProductCode,
                StoreRetailPriceUuid = uuid,
                ProductName = product?.ProductName,
                ItemNumber = product?.ItemNumber,
                Barcode = product?.Barcode,
                ProductImage = product?.ProductImage,
                Status = task.Status,
                Kind = task.Kind,
                ChangedFields = changedFields,
                ShelfRetailPrice = task.ShelfRetailPrice,
                ShelfDiscountRate = task.ShelfDiscountRate,
                StoreRetailPrice = task.StoreRetailPrice,
                StoreDiscountRate = task.StoreDiscountRate,
                TargetRetailPrice = task.TargetRetailPrice,
                TargetDiscountRate = task.TargetDiscountRate,
                InitiatorName = task.InitiatorName,
                InitiatorSource = task.InitiatorSource,
                InitiatorReference = task.InitiatorReference,
                InitiatedAtUtc = DateTime.SpecifyKind(task.InitiatedAtUtc, DateTimeKind.Utc),
                ChangeCount = task.ChangeCount,
                PriceAppliedBy = task.PriceAppliedBy,
                PriceAppliedAtUtc = AsUtc(task.PriceAppliedAtUtc),
                CompletionMode = task.CompletionMode,
                CompletedBy = task.CompletedBy,
                CompletedAtUtc = AsUtc(task.CompletedAtUtc),
                LabelPrintCount = task.LabelPrintCount,
                CancelReason = task.Status == StorePriceUpdateTaskStatuses.Cancelled ? task.CancelReason : null,
                CancelledBy = task.CancelledBy,
                CancelledAtUtc = AsUtc(task.CancelledAtUtc),
                CancelNote = task.CancelNote,
                HqSyncOperationId = IsHqSyncEnabled ? task.HqSyncOperationKey : null,
                HqSyncStatus = hqStatus,
            };
        }).ToList();
    }

    public async Task<StorePriceUpdateTaskBatchResultDto> ApplyAsync(
        ApplyStorePriceUpdateTasksRequestDto request,
        string updatedBy,
        string actorDisplayName,
        List<string>? accessibleStoreCodes,
        CancellationToken cancellationToken = default
    )
    {
        var result = new StorePriceUpdateTaskBatchResultDto { HqSyncEnabled = IsHqSyncEnabled };
        var storeCode = request.StoreCode?.Trim() ?? string.Empty;
        if (storeCode.Length == 0 || request.Items.Count == 0 || !await IsSchemaReadyAsync())
        {
            return result;
        }

        // 先对账一次，确保下面的乐观并发校验基于最新的仓库目标值。
        await ReconcilePendingAsync(storeCode, cancellationToken);

        // 延迟解析：商品维护服务 → 审计服务 → 本服务 构成依赖环，构造期注入会死循环。
        var maintenance = _serviceProvider.GetRequiredService<IStoreProductMaintenanceReactService>();
        var ids = request.Items.Select(item => item.TaskId).Distinct().ToList();
        var tasks = await _db.Queryable<StorePriceUpdateTask>()
            .Where(task => ids.Contains(task.Id) && task.StoreCode == storeCode)
            .ToListAsync();
        var taskById = tasks.ToDictionary(task => task.Id);

        foreach (var item in request.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!taskById.TryGetValue(item.TaskId, out var task))
            {
                result.Items.Add(Fail(item.TaskId, StorePriceUpdateTaskResultCodes.NotFound, "任务不存在"));
                continue;
            }
            if (task.Status != StorePriceUpdateTaskStatuses.Pending)
            {
                result.Items.Add(Fail(item.TaskId, StorePriceUpdateTaskResultCodes.NotPending, "任务已处理或已取消"));
                continue;
            }
            if (task.Kind != StorePriceUpdateTaskKinds.PriceUpdate)
            {
                // 价格已一致（例如刚被仓库自动下发），无需再改价；对调用方而言视为成功，继续走打印。
                result.Items.Add(new StorePriceUpdateTaskItemResultDto { TaskId = task.Id, Success = true });
                continue;
            }

            var expectedDiscount = item.ExpectedTargetDiscountRate.HasValue ? Rate(item.ExpectedTargetDiscountRate) : (decimal?)null;
            var currentDiscount = task.TargetDiscountRate.HasValue ? Rate(task.TargetDiscountRate) : (decimal?)null;
            if (Money(item.ExpectedTargetRetailPrice) != Money(task.TargetRetailPrice) || expectedDiscount != currentDiscount)
            {
                result.Items.Add(Fail(item.TaskId, StorePriceUpdateTaskResultCodes.TargetChanged, "仓库价格已再次变化，请刷新后重试"));
                continue;
            }

            var row = await _db.Queryable<StoreRetailPrice>()
                .Where(price => price.StoreCode == storeCode && price.ProductCode == task.ProductCode && !price.IsDeleted)
                .FirstAsync();
            if (row == null)
            {
                result.Items.Add(Fail(item.TaskId, StorePriceUpdateTaskResultCodes.NotApplicable, "分店商品记录不存在"));
                continue;
            }

            // 该接口会整字段覆盖进价/零售价/折扣，因此未参与本次变更的字段必须原样回填。
            var response = await maintenance.UpdateStorePriceAsync(
                row.UUID,
                new UpdateStoreProductPriceDto
                {
                    PurchasePrice = row.PurchasePrice,
                    RetailPrice = row.IsAutoPricing ? row.StoreRetailPriceValue : task.TargetRetailPrice ?? row.StoreRetailPriceValue,
                    DiscountRate = task.TargetDiscountRate ?? row.DiscountRate,
                },
                updatedBy,
                accessibleStoreCodes,
                enqueueHqProjection: IsHqSyncEnabled
            );
            if (!response.Success)
            {
                result.Items.Add(Fail(item.TaskId, StorePriceUpdateTaskResultCodes.Failed, response.Message ?? "更新失败"));
                continue;
            }

            var now = DateTime.UtcNow;
            task.PriceAppliedBy = Truncate(actorDisplayName, 100);
            task.PriceAppliedAtUtc = now;
            task.UpdatedAtUtc = now;
            var operationKey = response.Data?.HqSync?.OperationId;
            if (!string.IsNullOrWhiteSpace(operationKey))
            {
                task.HqSyncOperationKey = Truncate(operationKey, 200);
                result.HqSyncSubmittedCount += 1;
            }
            await _db.Updateable(task)
                .UpdateColumns(t => new { t.PriceAppliedBy, t.PriceAppliedAtUtc, t.UpdatedAtUtc, t.HqSyncOperationKey })
                .ExecuteCommandAsync();
            result.Items.Add(new StorePriceUpdateTaskItemResultDto { TaskId = task.Id, Success = true });
        }

        // 改价后重新推导形态：通常转为「待换标签」，恰好等于货架价则直接完成。
        var appliedCodes = result.Items
            .Where(item => item.Success && taskById.ContainsKey(item.TaskId))
            .Select(item => taskById[item.TaskId].ProductCode)
            .Distinct()
            .ToList();
        if (appliedCodes.Count > 0)
        {
            await EvaluateProductsAsync(appliedCodes, storeCode, null, null, allowCreate: false, cancellationToken);
        }

        await AttachTasksAsync(result);
        return result;
    }

    public async Task<StorePriceUpdateTaskBatchResultDto> KeepStorePriceAsync(
        StorePriceUpdateTaskIdsRequestDto request,
        string actorDisplayName,
        CancellationToken cancellationToken = default
    )
    {
        var result = new StorePriceUpdateTaskBatchResultDto { HqSyncEnabled = IsHqSyncEnabled };
        var storeCode = request.StoreCode?.Trim() ?? string.Empty;
        if (storeCode.Length == 0 || request.TaskIds.Count == 0 || !await IsSchemaReadyAsync())
        {
            return result;
        }

        var ids = request.TaskIds.Distinct().ToList();
        var tasks = await _db.Queryable<StorePriceUpdateTask>()
            .Where(task => ids.Contains(task.Id) && task.StoreCode == storeCode)
            .ToListAsync();
        var now = DateTime.UtcNow;
        foreach (var id in ids)
        {
            var task = tasks.FirstOrDefault(item => item.Id == id);
            if (task == null)
            {
                result.Items.Add(Fail(id, StorePriceUpdateTaskResultCodes.NotFound, "任务不存在"));
                continue;
            }
            if (task.Status != StorePriceUpdateTaskStatuses.Pending)
            {
                result.Items.Add(Fail(id, StorePriceUpdateTaskResultCodes.NotPending, "任务已处理或已取消"));
                continue;
            }

            Close(task, StorePriceUpdateTaskStatuses.Completed, now);
            task.CompletionMode = StorePriceUpdateTaskCompletionModes.KeptStorePrice;
            task.CompletedBy = Truncate(actorDisplayName, 100);
            await _db.Updateable(task)
                .UpdateColumns(t => new { t.Status, t.CompletionMode, t.CompletedBy, t.CompletedAtUtc, t.UpdatedAtUtc })
                .ExecuteCommandAsync();
            result.Items.Add(new StorePriceUpdateTaskItemResultDto { TaskId = id, Success = true });
        }

        await AttachTasksAsync(result);
        return result;
    }

    public async Task<StorePriceUpdateTaskBatchResultDto> MarkLabelsAsync(
        MarkStorePriceUpdateTaskLabelsRequestDto request,
        string actorDisplayName,
        CancellationToken cancellationToken = default
    )
    {
        var result = new StorePriceUpdateTaskBatchResultDto { HqSyncEnabled = IsHqSyncEnabled };
        var storeCode = request.StoreCode?.Trim() ?? string.Empty;
        if (storeCode.Length == 0 || request.TaskIds.Count == 0 || !await IsSchemaReadyAsync())
        {
            return result;
        }

        var printed = !string.Equals(
            request.Mode,
            StorePriceUpdateTaskCompletionModes.MarkedReplaced,
            StringComparison.OrdinalIgnoreCase
        );
        await ReconcilePendingAsync(storeCode, cancellationToken);

        var ids = request.TaskIds.Distinct().ToList();
        var tasks = await _db.Queryable<StorePriceUpdateTask>()
            .Where(task => ids.Contains(task.Id) && task.StoreCode == storeCode)
            .ToListAsync();
        var now = DateTime.UtcNow;
        foreach (var id in ids)
        {
            var task = tasks.FirstOrDefault(item => item.Id == id);
            if (task == null)
            {
                result.Items.Add(Fail(id, StorePriceUpdateTaskResultCodes.NotFound, "任务不存在"));
                continue;
            }

            if (task.Status == StorePriceUpdateTaskStatuses.Completed)
            {
                // 已完成任务的"再打一张"：只累加打印次数。
                if (printed)
                {
                    task.LabelPrintCount += 1;
                    task.UpdatedAtUtc = now;
                    await _db.Updateable(task)
                        .UpdateColumns(t => new { t.LabelPrintCount, t.UpdatedAtUtc })
                        .ExecuteCommandAsync();
                }
                result.Items.Add(new StorePriceUpdateTaskItemResultDto { TaskId = id, Success = true });
                continue;
            }
            if (task.Status != StorePriceUpdateTaskStatuses.Pending)
            {
                result.Items.Add(Fail(id, StorePriceUpdateTaskResultCodes.NotPending, "任务已取消"));
                continue;
            }
            if (task.Kind != StorePriceUpdateTaskKinds.LabelOnly)
            {
                // 价格还没改就换标签，会把旧价印到新标签上。
                result.Items.Add(Fail(id, StorePriceUpdateTaskResultCodes.NotApplicable, "请先更新价格再处理标签"));
                continue;
            }

            // 价格变更任务口径：执行过（换标签 / 打印）的「分店 + 商品」才同步 HQ，未执行的分店 HQ 不变。
            // 通知页已改过价的任务当时就入过 HQ 队列，这里不再重复入队。
            // 入队与标记完成放同一事务：入队失败则任务保持待办、提示重试，不会出现「已完成但 HQ 永远不知道」。
            var hqEnqueuer = IsHqSyncEnabled && !(task.PriceAppliedAtUtc.HasValue && !string.IsNullOrEmpty(task.HqSyncOperationKey))
                ? _serviceProvider.GetService<IWarehouseStorePriceHqSyncEnqueuer>()
                : null;
            if (hqEnqueuer != null)
            {
                await _db.Ado.BeginTranAsync();
            }
            try
            {
                if (hqEnqueuer != null)
                {
                    var operation = await hqEnqueuer.EnqueueAsync(
                        _db,
                        task.ProductCode,
                        new[] { task.StoreCode },
                        new[]
                        {
                            ProductMaintenanceHqFieldMasks.StoreRetailPrice,
                            ProductMaintenanceHqFieldMasks.StoreDiscountRate,
                        },
                        actorDisplayName,
                        PriceTaskLabelDoneHqSource,
                        cancellationToken
                    );
                    if (!string.IsNullOrWhiteSpace(operation?.OperationId))
                    {
                        task.HqSyncOperationKey = Truncate(operation.OperationId, 200);
                        result.HqSyncSubmittedCount += 1;
                    }
                }

                Close(task, StorePriceUpdateTaskStatuses.Completed, now);
                task.CompletionMode = printed
                    ? StorePriceUpdateTaskCompletionModes.Printed
                    : StorePriceUpdateTaskCompletionModes.MarkedReplaced;
                task.CompletedBy = Truncate(actorDisplayName, 100);
                if (printed)
                {
                    task.LabelPrintCount += 1;
                }
                await _db.Updateable(task)
                    .UpdateColumns(t => new
                    {
                        t.Status,
                        t.CompletionMode,
                        t.CompletedBy,
                        t.CompletedAtUtc,
                        t.LabelPrintCount,
                        t.UpdatedAtUtc,
                        t.HqSyncOperationKey,
                    })
                    .ExecuteCommandAsync();
                if (hqEnqueuer != null)
                {
                    await _db.Ado.CommitTranAsync();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (hqEnqueuer != null)
                {
                    await _db.Ado.RollbackTranAsync();
                }
                _logger.LogError(ex, "标记换标签时同步 HQ 失败: TaskId={TaskId}", id);
                result.Items.Add(Fail(id, StorePriceUpdateTaskResultCodes.Failed, "同步总部 HQ 失败，请稍后重试"));
                continue;
            }
            result.Items.Add(new StorePriceUpdateTaskItemResultDto { TaskId = id, Success = true });
        }

        await AttachTasksAsync(result);
        return result;
    }

    private async Task AttachTasksAsync(StorePriceUpdateTaskBatchResultDto result)
    {
        result.SuccessCount = result.Items.Count(item => item.Success);
        result.FailedCount = result.Items.Count - result.SuccessCount;
        var ids = result.Items.Select(item => item.TaskId).Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var tasks = await _db.Queryable<StorePriceUpdateTask>().Where(task => ids.Contains(task.Id)).ToListAsync();
        var mapped = (await MapTasksAsync(tasks)).ToDictionary(task => task.Id);
        foreach (var item in result.Items)
        {
            if (mapped.TryGetValue(item.TaskId, out var dto))
            {
                item.Task = dto;
            }
        }
    }

    private static StorePriceUpdateTaskItemResultDto Fail(long taskId, string code, string message) =>
        new() { TaskId = taskId, Success = false, Code = code, Message = message };

    // =====================================================================
    // Web 监控
    // =====================================================================

    public async Task<CancelStorePriceUpdateTasksResultDto> CancelAsync(
        CancelStorePriceUpdateTasksRequestDto request,
        string actorDisplayName,
        CancellationToken cancellationToken = default
    )
    {
        var result = new CancelStorePriceUpdateTasksResultDto();
        if (!await IsSchemaReadyAsync())
        {
            return result;
        }

        var pendingStatus = StorePriceUpdateTaskStatuses.Pending;
        var cancelledStatus = StorePriceUpdateTaskStatuses.Cancelled;
        var manualReason = StorePriceUpdateTaskCancelReasons.ManualCancel;
        var actor = Truncate(actorDisplayName, 100) ?? "System";
        var note = Truncate(request.Note, CancelStorePriceUpdateTasksRequestDto.MaxNoteLength);

        // 1) 解析要撤销的任务 id：勾选 id 优先；否则按商品取其全部未完成任务（可限定分店）。
        var ids = new List<long>();
        var missingIds = new List<long>();
        var pendingBefore = new HashSet<long>();
        if (request.TaskIds.Count > 0)
        {
            ids = request.TaskIds.Distinct().ToList();
            var existing = await _db.Queryable<StorePriceUpdateTask>()
                .Where(task => ids.Contains(task.Id))
                .Select(task => new { task.Id, task.Status })
                .ToListAsync();
            var existingIds = existing.Select(row => row.Id).ToHashSet();
            missingIds = ids.Where(id => !existingIds.Contains(id)).ToList();
            foreach (var row in existing.Where(row => row.Status == pendingStatus))
            {
                pendingBefore.Add(row.Id);
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.ProductCode))
        {
            var productCode = request.ProductCode.Trim();
            var storeCodes = (request.StoreCodes ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var query = _db.Queryable<StorePriceUpdateTask>()
                .Where(task => task.ProductCode == productCode && task.Status == pendingStatus);
            if (storeCodes.Count > 0)
            {
                query = query.Where(task => storeCodes.Contains(task.StoreCode));
            }
            ids = await query.Select(task => task.Id).ToListAsync();
            pendingBefore = ids.ToHashSet();
        }

        foreach (var id in missingIds)
        {
            result.Items.Add(Fail(id, StorePriceUpdateTaskResultCodes.NotFound, "任务不存在"));
        }
        foreach (var id in ids.Where(id => !missingIds.Contains(id) && !pendingBefore.Contains(id)))
        {
            result.Items.Add(Fail(id, StorePriceUpdateTaskResultCodes.NotPending, "任务已处理或已取消"));
        }

        // 2) 条件更新：带 Status = Pending 守卫，分店恰好同时处理时先到先得，已完成的任务绝不会被改写。
        var toCancel = ids.Where(pendingBefore.Contains).ToList();
        var now = DateTime.UtcNow;
        foreach (var chunk in toCancel.Chunk(CancelStorePriceUpdateTasksRequestDto.MaxTaskIds))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkIds = chunk.ToList();
            await _db.Updateable<StorePriceUpdateTask>()
                .SetColumns(task => new StorePriceUpdateTask
                {
                    Status = cancelledStatus,
                    CancelReason = manualReason,
                    CancelledBy = actor,
                    CancelledAtUtc = now,
                    CancelNote = note,
                    UpdatedAtUtc = now,
                })
                .Where(task => chunkIds.Contains(task.Id) && task.Status == pendingStatus)
                .ExecuteCommandAsync();
        }

        // 3) 以更新后的真实状态逐条回报（而不是信任受影响行数），竞争落败的算「已处理」。
        var cancelledNow = toCancel.Count == 0
            ? new HashSet<long>()
            : (
                await _db.Queryable<StorePriceUpdateTask>()
                    .Where(task => toCancel.Contains(task.Id) && task.Status == cancelledStatus && task.CancelReason == manualReason)
                    .Select(task => task.Id)
                    .ToListAsync()
            ).ToHashSet();
        foreach (var id in toCancel)
        {
            result.Items.Add(
                cancelledNow.Contains(id)
                    ? new StorePriceUpdateTaskItemResultDto { TaskId = id, Success = true }
                    : Fail(id, StorePriceUpdateTaskResultCodes.NotPending, "任务已处理或已取消")
            );
        }

        result.CancelledCount = result.Items.Count(item => item.Success);
        result.SkippedCount = result.Items.Count - result.CancelledCount;
        _logger.LogInformation(
            "价格更新任务手动撤销：操作人 {Actor}，撤销 {Cancelled}/{Total}，商品 {ProductCode}，备注 {Note}",
            actor,
            result.CancelledCount,
            result.Items.Count,
            request.ProductCode,
            note
        );
        return result;
    }

    public async Task<StorePriceUpdateTaskSummaryDto> GetSummaryAsync(
        StorePriceUpdateTaskQueryDto query,
        CancellationToken cancellationToken = default
    )
    {
        var summary = new StorePriceUpdateTaskSummaryDto
        {
            HqSyncEnabled = IsHqSyncEnabled,
            OverdueDays = OverdueDays,
        };
        if (!await IsSchemaReadyAsync())
        {
            return summary;
        }

        var rows = await GetByStoreAsync(query, cancellationToken);
        summary.PendingCount = rows.Sum(row => row.PendingCount);
        summary.PendingPriceUpdateCount = rows.Sum(row => row.PendingPriceUpdateCount);
        summary.PendingLabelOnlyCount = rows.Sum(row => row.PendingLabelOnlyCount);
        summary.CompletedCount = rows.Sum(row => row.CompletedCount);
        var denominator = summary.PendingCount + summary.CompletedCount;
        summary.CompletionRate = denominator == 0
            ? 0m
            : Math.Round((decimal)summary.CompletedCount / denominator, 4);

        var overdueCutoff = DateTime.UtcNow.AddDays(-OverdueDays);
        var overdueRows = rows.Where(row => row.OldestPendingAtUtc.HasValue && row.OldestPendingAtUtc < overdueCutoff).ToList();
        summary.OverdueStoreCount = overdueRows.Count;
        var pendingStatus = StorePriceUpdateTaskStatuses.Pending;
        var overdueQuery = _db.Queryable<StorePriceUpdateTask>()
            .Where(task => task.Status == pendingStatus && task.InitiatedAtUtc < overdueCutoff);
        var overdueStoreCodes = query.ResolveStoreCodes();
        if (overdueStoreCodes.Count > 0)
        {
            overdueQuery = overdueQuery.Where(task => overdueStoreCodes.Contains(task.StoreCode));
        }
        summary.OverdueCount = await overdueQuery.CountAsync();

        if (IsHqSyncEnabled)
        {
            var blocked = HqSyncBlockedStatus;
            summary.HqSyncFailedCount = await _db.Queryable<StorePriceUpdateTask>()
                .Where(task =>
                    task.HqSyncOperationKey != null
                    && SqlFunc.Subqueryable<ProductHqSyncOutbox>()
                        .Where(outbox => outbox.OperationKey == task.HqSyncOperationKey && outbox.Status == blocked)
                        .Any()
                )
                .CountAsync();
        }
        return summary;
    }

    public async Task<List<StorePriceUpdateTaskStoreRowDto>> GetByStoreAsync(
        StorePriceUpdateTaskQueryDto query,
        CancellationToken cancellationToken = default
    )
    {
        if (!await IsSchemaReadyAsync())
        {
            return new List<StorePriceUpdateTaskStoreRowDto>();
        }

        var pendingStatus = StorePriceUpdateTaskStatuses.Pending;
        var completedStatus = StorePriceUpdateTaskStatuses.Completed;
        var from = query.FromUtc ?? DateTime.UtcNow.AddDays(-7);
        var to = query.ToUtc ?? DateTime.UtcNow.AddDays(1);

        // 未完成 = 当前积压，不受时间范围限制；已完成 = 时间范围内完成的。
        // 已取消不计入分母：仓库改回去既不是分店的功劳也不是过失。
        var baseQuery = await BuildFilteredQueryAsync(
            new StorePriceUpdateTaskQueryDto
            {
                StoreCode = query.StoreCode,
                StoreCodes = query.StoreCodes,
                Kind = query.Kind,
                InitiatorName = query.InitiatorName,
                Keyword = query.Keyword,
            },
            null,
            applyStatus: false
        );
        var pendingGroups = await baseQuery.Clone()
            .Where(task => task.Status == pendingStatus)
            .GroupBy(task => new { task.StoreCode, task.Kind })
            .Select(task => new
            {
                task.StoreCode,
                task.Kind,
                Count = SqlFunc.AggregateCount(task.Id),
                Oldest = SqlFunc.AggregateMin(task.InitiatedAtUtc),
            })
            .ToListAsync();
        var completedGroups = await baseQuery.Clone()
            .Where(task => task.Status == completedStatus && task.CompletedAtUtc >= from && task.CompletedAtUtc <= to)
            .GroupBy(task => task.StoreCode)
            .Select(task => new
            {
                task.StoreCode,
                Count = SqlFunc.AggregateCount(task.Id),
                Latest = SqlFunc.AggregateMax(task.CompletedAtUtc),
            })
            .ToListAsync();

        var storesQuery = _db.Queryable<Store>().Where(store => store.IsActive && !store.IsDeleted);
        var storeFilter = query.ResolveStoreCodes();
        if (storeFilter.Count > 0)
        {
            storesQuery = storesQuery.Where(store => storeFilter.Contains(store.StoreCode));
        }
        var stores = await storesQuery.Select(store => new { store.StoreCode, store.StoreName }).ToListAsync();

        var rows = new List<StorePriceUpdateTaskStoreRowDto>();
        foreach (var store in stores)
        {
            var pending = pendingGroups.Where(group => group.StoreCode == store.StoreCode).ToList();
            var completed = completedGroups.FirstOrDefault(group => group.StoreCode == store.StoreCode);
            var row = new StorePriceUpdateTaskStoreRowDto
            {
                StoreCode = store.StoreCode,
                StoreName = store.StoreName,
                PendingPriceUpdateCount = pending
                    .Where(group => group.Kind == StorePriceUpdateTaskKinds.PriceUpdate)
                    .Sum(group => group.Count),
                PendingLabelOnlyCount = pending
                    .Where(group => group.Kind == StorePriceUpdateTaskKinds.LabelOnly)
                    .Sum(group => group.Count),
                CompletedCount = completed?.Count ?? 0,
                OldestPendingAtUtc = pending.Count == 0 ? null : AsUtc(pending.Min(group => group.Oldest)),
                LastCompletedAtUtc = AsUtc(completed?.Latest),
            };
            row.PendingCount = row.PendingPriceUpdateCount + row.PendingLabelOnlyCount;
            var denominator = row.PendingCount + row.CompletedCount;
            row.CompletionRate = denominator == 0 ? 0m : Math.Round((decimal)row.CompletedCount / denominator, 4);
            if (row.PendingCount == 0 && row.CompletedCount == 0)
            {
                continue;
            }

            if (completed?.Latest != null)
            {
                // 分店数量有限（十几家），逐店取最近处理人即可，避免写窗口函数兼容两种数据库。
                var storeCode = store.StoreCode;
                row.LastCompletedBy = await _db.Queryable<StorePriceUpdateTask>()
                    .Where(task => task.StoreCode == storeCode && task.Status == completedStatus)
                    .OrderBy(task => task.CompletedAtUtc, OrderByType.Desc)
                    .Select(task => task.CompletedBy)
                    .FirstAsync();
            }
            rows.Add(row);
        }

        return rows
            .OrderByDescending(row => row.PendingCount)
            .ThenBy(row => row.StoreCode, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<StorePriceUpdateTaskProductPageDto> GetByProductAsync(
        StorePriceUpdateTaskQueryDto query,
        bool onlyIncomplete,
        CancellationToken cancellationToken = default
    )
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var result = new StorePriceUpdateTaskProductPageDto { Page = page, PageSize = pageSize };
        if (!await IsSchemaReadyAsync())
        {
            return result;
        }

        var pendingStatus = StorePriceUpdateTaskStatuses.Pending;
        var completedStatus = StorePriceUpdateTaskStatuses.Completed;
        var from = query.FromUtc ?? DateTime.UtcNow.AddDays(-7);
        var baseQuery = await BuildFilteredQueryAsync(
            new StorePriceUpdateTaskQueryDto
            {
                StoreCode = query.StoreCode,
                StoreCodes = query.StoreCodes,
                Kind = query.Kind,
                InitiatorName = query.InitiatorName,
                Keyword = query.Keyword,
            },
            null,
            applyStatus: false
        );
        var scoped = baseQuery.Where(task =>
            task.Status == pendingStatus || (task.Status == completedStatus && task.CompletedAtUtc >= from)
        );

        // 每个商品只取"当前这一轮"：同店同商品可能有多条历史任务，按最新一条代表该店状态。
        var allTasks = await scoped.Clone()
            .OrderBy(task => task.Id, OrderByType.Desc)
            .Take(20000)
            .ToListAsync();
        var latestPerStore = allTasks
            .GroupBy(task => Key(task.StoreCode, task.ProductCode))
            .Select(group => group.First())
            .ToList();
        var groups = latestPerStore
            .GroupBy(task => task.ProductCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                ProductCode = group.Key,
                Tasks = group.ToList(),
                Pending = group.Count(task => task.Status == pendingStatus),
                Latest = group.Max(task => task.InitiatedAtUtc),
            })
            .Where(group => !onlyIncomplete || group.Pending > 0)
            .OrderByDescending(group => group.Pending > 0)
            .ThenByDescending(group => group.Latest)
            .ToList();

        result.Total = groups.Count;
        var pageGroups = groups.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        if (pageGroups.Count == 0)
        {
            return result;
        }

        var mapped = (await MapTasksAsync(pageGroups.SelectMany(group => group.Tasks).ToList()))
            .ToDictionary(task => task.Id);
        var pageCodes = pageGroups.Select(group => group.ProductCode).ToList();
        var activeStores = await _db.Queryable<Store>()
            .Where(store => store.IsActive && !store.IsDeleted)
            .Select(store => new { store.StoreCode, store.StoreName })
            .ToListAsync();
        var activeStoreCodes = activeStores.Select(store => store.StoreCode).ToList();
        var specialRows = await _db.Queryable<StoreRetailPrice>()
            .Where(row =>
                row.ProductCode != null
                && pageCodes.Contains(row.ProductCode)
                && row.StoreCode != null
                && activeStoreCodes.Contains(row.StoreCode)
                && row.IsSpecialProduct
                && !row.IsDeleted
            )
            .Select(row => new { row.StoreCode, row.ProductCode, row.StoreRetailPriceValue, row.DiscountRate })
            .ToListAsync();

        foreach (var group in pageGroups)
        {
            var newest = group.Tasks.OrderByDescending(task => task.InitiatedAtUtc).First();
            var newestDto = mapped[newest.Id];
            var row = new StorePriceUpdateTaskProductRowDto
            {
                ProductCode = group.ProductCode,
                ProductName = newestDto.ProductName,
                ItemNumber = newestDto.ItemNumber,
                ProductImage = newestDto.ProductImage,
                TargetRetailPrice = newest.TargetRetailPrice,
                TargetDiscountRate = newest.TargetDiscountRate,
                InitiatorName = newest.InitiatorName,
                InitiatorSource = newest.InitiatorSource,
                InitiatorReference = newest.InitiatorReference,
                InitiatedAtUtc = DateTime.SpecifyKind(newest.InitiatedAtUtc, DateTimeKind.Utc),
                ChangeCount = group.Tasks.Max(task => task.ChangeCount),
            };

            foreach (var task in group.Tasks.OrderBy(task => task.StoreCode, StringComparer.Ordinal))
            {
                var dto = mapped[task.Id];
                row.Stores.Add(
                    new StorePriceUpdateTaskProductStoreDto
                    {
                        StoreCode = task.StoreCode,
                        StoreName = dto.StoreName,
                        State = task.Status == completedStatus ? "Completed" : task.Kind,
                        ShelfRetailPrice = task.ShelfRetailPrice,
                        StoreRetailPrice = task.StoreRetailPrice,
                        StoreDiscountRate = task.StoreDiscountRate,
                        CompletionMode = task.CompletionMode,
                        CompletedBy = task.CompletedBy,
                        CompletedAtUtc = AsUtc(task.CompletedAtUtc),
                        InitiatedAtUtc = DateTime.SpecifyKind(task.InitiatedAtUtc, DateTimeKind.Utc),
                        HqSyncStatus = dto.HqSyncStatus,
                    }
                );
            }

            // 特殊商品分店按规则未建任务：显式列出，避免管理者误以为系统漏了。
            var taskStores = group.Tasks.Select(task => task.StoreCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var special in specialRows.Where(item =>
                string.Equals(item.ProductCode, group.ProductCode, StringComparison.OrdinalIgnoreCase)
                && !taskStores.Contains(item.StoreCode!)))
            {
                row.Stores.Add(
                    new StorePriceUpdateTaskProductStoreDto
                    {
                        StoreCode = special.StoreCode!,
                        StoreName = activeStores.FirstOrDefault(store => store.StoreCode == special.StoreCode)?.StoreName,
                        State = "Skipped",
                        StoreRetailPrice = special.StoreRetailPriceValue,
                        StoreDiscountRate = special.DiscountRate,
                    }
                );
            }

            row.StoreCount = row.Stores.Count(store => store.State != "Skipped");
            row.CompletedStoreCount = row.Stores.Count(store => store.State == "Completed");
            result.Items.Add(row);
        }
        return result;
    }

    // =====================================================================
    // 辅助
    // =====================================================================

    /// <summary>
    /// 任务表/建议折扣表不存在时整个功能静默降级为 no-op。
    /// 这条挂在所有仓库改价入口的审计收口上，绝不能因为某个环境（如只建了部分表的测试库）缺表而打断改价。
    /// </summary>
    private Task<bool> IsSchemaReadyAsync()
    {
        if (_schemaReady.HasValue)
        {
            return Task.FromResult(_schemaReady.Value);
        }

        try
        {
            _schemaReady =
                _db.DbMaintenance.IsAnyTable("StorePriceUpdateTask", false)
                && _db.DbMaintenance.IsAnyTable("ProductSuggestedDiscount", false)
                // 手动撤销新增的留痕列：迁移前降级为「无任务」，避免实体查询直接抛错。
                && _db.DbMaintenance.IsAnyColumn("StorePriceUpdateTask", "CancelledBy", false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "检查价格更新任务表失败，功能降级为不生成任务");
            _schemaReady = false;
        }
        return Task.FromResult(_schemaReady.Value);
    }

    private static (string, string) Key(string storeCode, string productCode) =>
        (storeCode.Trim().ToUpperInvariant(), productCode.Trim().ToUpperInvariant());

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static DateTime? AsUtc(DateTime? value) =>
        value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null;
}
