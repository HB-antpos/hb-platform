namespace BlazorApp.Shared.DTOs;

/// <summary>
/// 一次仓库改价产生的分店通知汇总，随改价接口响应返回给前端做"已发送到通知列表"的提示。
/// </summary>
public sealed class PriceNotificationSummaryDto
{
    public int ProductCount { get; set; }
    public int NeedsPriceUpdateStores { get; set; }
    public int LabelOnlyStores { get; set; }
    public int CancelledStores { get; set; }
    public int SkippedSpecialStores { get; set; }

    public bool HasAny =>
        NeedsPriceUpdateStores > 0 || LabelOnlyStores > 0 || CancelledStores > 0;
}

/// <summary>保存前的预告：当前有多少分店会被通知。</summary>
public sealed class PriceNotificationPreviewDto
{
    public int AffectedStores { get; set; }
    public int SkippedSpecialStores { get; set; }
}

public sealed class StorePriceUpdateTaskDto
{
    public long Id { get; set; }
    public string StoreCode { get; set; } = string.Empty;
    public string? StoreName { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string? StoreRetailPriceUuid { get; set; }
    public string? ProductName { get; set; }
    public string? ItemNumber { get; set; }
    public string? Barcode { get; set; }
    public string? ProductImage { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;

    /// <summary>发生差异的字段：retailPrice / discountRate。</summary>
    public List<string> ChangedFields { get; set; } = new();

    public decimal? ShelfRetailPrice { get; set; }
    public decimal? ShelfDiscountRate { get; set; }
    public decimal? StoreRetailPrice { get; set; }
    public decimal? StoreDiscountRate { get; set; }
    public decimal? TargetRetailPrice { get; set; }
    public decimal? TargetDiscountRate { get; set; }

    public string InitiatorName { get; set; } = string.Empty;
    public string InitiatorSource { get; set; } = string.Empty;
    public string? InitiatorReference { get; set; }
    public DateTime InitiatedAtUtc { get; set; }
    public int ChangeCount { get; set; }

    public string? PriceAppliedBy { get; set; }
    public DateTime? PriceAppliedAtUtc { get; set; }
    public string? CompletionMode { get; set; }
    public string? CompletedBy { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int LabelPrintCount { get; set; }

    /// <summary>取消原因：Reverted / NoLongerApplicable（系统自动）/ ManualCancel（Web 手动撤销）；非已取消任务为 null。</summary>
    public string? CancelReason { get; set; }
    public string? CancelledBy { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public string? CancelNote { get; set; }

    /// <summary>HQ 同步操作 Id（即 outbox OperationKey），可直接用于现有的 hq-sync 重试接口。</summary>
    public string? HqSyncOperationId { get; set; }

    /// <summary>HQ 同步状态（pending/processing/retrying/succeeded/blocked/superseded）；未入队时为 null。</summary>
    public string? HqSyncStatus { get; set; }
}

public sealed class StorePriceUpdateTaskQueryDto
{
    /// <summary>单个分店（链接跳转、行点击使用，保持向后兼容）。</summary>
    public string? StoreCode { get; set; }

    /// <summary>多个分店，英文逗号分隔（如 "1003,1004"）；与 <see cref="StoreCode"/> 取并集，均为空表示全部分店。</summary>
    public string? StoreCodes { get; set; }

    public string? Status { get; set; }
    public string? Kind { get; set; }
    public string? Keyword { get; set; }
    public string? InitiatorName { get; set; }
    public bool? HqSyncFailedOnly { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 30;

    /// <summary>
    /// 合并 <see cref="StoreCode"/> 与 <see cref="StoreCodes"/>，去空、去重（忽略大小写）后返回；
    /// 空列表表示不按分店过滤。统一在此解析，避免各查询各写一套。
    /// </summary>
    public List<string> ResolveStoreCodes()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? raw)
        {
            var code = raw?.Trim();
            if (!string.IsNullOrEmpty(code) && seen.Add(code))
            {
                result.Add(code);
            }
        }

        Add(StoreCode);
        foreach (var part in (StoreCodes ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            Add(part);
        }
        return result;
    }
}

public sealed class StorePriceUpdateTaskPageDto
{
    public List<StorePriceUpdateTaskDto> Items { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int PendingCount { get; set; }
    public int PendingPriceUpdateCount { get; set; }
    public int PendingLabelOnlyCount { get; set; }
    public int CompletedCount { get; set; }

    /// <summary>通知页改价是否同步 HQ 数据库；关闭后前端隐藏所有总部同步提示。</summary>
    public bool HqSyncEnabled { get; set; }
}

public sealed class StorePriceUpdateTaskCountDto
{
    public int PendingCount { get; set; }
}

public sealed class ApplyStorePriceUpdateTasksRequestDto
{
    public string StoreCode { get; set; } = string.Empty;
    public List<ApplyStorePriceUpdateTaskItemDto> Items { get; set; } = new();
}

public sealed class ApplyStorePriceUpdateTaskItemDto
{
    public long TaskId { get; set; }

    /// <summary>列表加载时看到的目标值，用于乐观并发校验：期间仓库再次改价则拒绝写入旧目标。</summary>
    public decimal? ExpectedTargetRetailPrice { get; set; }
    public decimal? ExpectedTargetDiscountRate { get; set; }
}

public sealed class StorePriceUpdateTaskIdsRequestDto
{
    public string StoreCode { get; set; } = string.Empty;
    public List<long> TaskIds { get; set; } = new();
}

public sealed class MarkStorePriceUpdateTaskLabelsRequestDto
{
    public string StoreCode { get; set; } = string.Empty;
    public List<long> TaskIds { get; set; } = new();

    /// <summary>Printed（已打印）或 MarkedReplaced（标记已换，未打印）。</summary>
    public string Mode { get; set; } = "Printed";
}

public static class StorePriceUpdateTaskResultCodes
{
    public const string Ok = "ok";
    public const string NotFound = "not_found";
    public const string NotPending = "not_pending";
    public const string TargetChanged = "target_changed";
    public const string NotApplicable = "not_applicable";
    public const string Failed = "failed";
}

public sealed class StorePriceUpdateTaskItemResultDto
{
    public long TaskId { get; set; }
    public bool Success { get; set; }
    public string Code { get; set; } = StorePriceUpdateTaskResultCodes.Ok;
    public string? Message { get; set; }
    public StorePriceUpdateTaskDto? Task { get; set; }
}

public sealed class StorePriceUpdateTaskBatchResultDto
{
    public List<StorePriceUpdateTaskItemResultDto> Items { get; set; } = new();
    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public bool HqSyncEnabled { get; set; }
    public int HqSyncSubmittedCount { get; set; }
}

// ---------- Web 监控页 ----------

/// <summary>
/// 手动撤销未完成任务。两种入参二选一：
/// <see cref="TaskIds"/>（任务明细勾选，至多 <see cref="MaxTaskIds"/> 个）或
/// <see cref="ProductCode"/>（撤销该商品所有未完成任务，可用 <see cref="StoreCodes"/> 限定分店）。
/// </summary>
public sealed class CancelStorePriceUpdateTasksRequestDto
{
    public const int MaxTaskIds = 200;
    public const int MaxNoteLength = 200;

    public List<long> TaskIds { get; set; } = new();
    public string? ProductCode { get; set; }

    /// <summary>仅与 <see cref="ProductCode"/> 搭配；英文逗号分隔，空表示该商品的全部分店。</summary>
    public string? StoreCodes { get; set; }

    /// <summary>撤销备注（可选，超过 <see cref="MaxNoteLength"/> 个字符会被截断）。</summary>
    public string? Note { get; set; }
}

public sealed class CancelStorePriceUpdateTasksResultDto
{
    /// <summary>逐条结果：成功、已处理（不再是未完成）、不存在。</summary>
    public List<StorePriceUpdateTaskItemResultDto> Items { get; set; } = new();
    public int CancelledCount { get; set; }

    /// <summary>未被撤销的条数（已完成/已取消/不存在）。</summary>
    public int SkippedCount { get; set; }
}

public sealed class StorePriceUpdateTaskSummaryDto
{
    public int PendingCount { get; set; }
    public int PendingPriceUpdateCount { get; set; }
    public int PendingLabelOnlyCount { get; set; }
    public int CompletedCount { get; set; }
    public decimal CompletionRate { get; set; }
    public int OverdueCount { get; set; }
    public int OverdueStoreCount { get; set; }
    public int OverdueDays { get; set; }
    public int HqSyncFailedCount { get; set; }
    public bool HqSyncEnabled { get; set; }
}

public sealed class StorePriceUpdateTaskStoreRowDto
{
    public string StoreCode { get; set; } = string.Empty;
    public string? StoreName { get; set; }
    public int PendingCount { get; set; }
    public int PendingPriceUpdateCount { get; set; }
    public int PendingLabelOnlyCount { get; set; }
    public int CompletedCount { get; set; }
    public decimal CompletionRate { get; set; }
    public DateTime? OldestPendingAtUtc { get; set; }
    public string? LastCompletedBy { get; set; }
    public DateTime? LastCompletedAtUtc { get; set; }
}

public sealed class StorePriceUpdateTaskProductRowDto
{
    public string ProductCode { get; set; } = string.Empty;
    public string? ProductName { get; set; }
    public string? ItemNumber { get; set; }
    public string? ProductImage { get; set; }
    public decimal? TargetRetailPrice { get; set; }
    public decimal? TargetDiscountRate { get; set; }
    public string InitiatorName { get; set; } = string.Empty;
    public string InitiatorSource { get; set; } = string.Empty;
    public string? InitiatorReference { get; set; }
    public DateTime InitiatedAtUtc { get; set; }
    public int ChangeCount { get; set; }
    public int StoreCount { get; set; }
    public int CompletedStoreCount { get; set; }
    public List<StorePriceUpdateTaskProductStoreDto> Stores { get; set; } = new();
}

public sealed class StorePriceUpdateTaskProductStoreDto
{
    public string StoreCode { get; set; } = string.Empty;
    public string? StoreName { get; set; }

    /// <summary>Completed / LabelOnly / PriceUpdate / Skipped（特殊商品未建任务）。</summary>
    public string State { get; set; } = string.Empty;
    public decimal? ShelfRetailPrice { get; set; }
    public decimal? StoreRetailPrice { get; set; }
    public decimal? StoreDiscountRate { get; set; }
    public string? CompletionMode { get; set; }
    public string? CompletedBy { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? InitiatedAtUtc { get; set; }
    public string? HqSyncStatus { get; set; }
}

public sealed class StorePriceUpdateTaskProductPageDto
{
    public List<StorePriceUpdateTaskProductRowDto> Items { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

// ---------- 仓库商品建议折扣 ----------

public sealed class SuggestedDiscountLookupRequestDto
{
    public List<string> ProductCodes { get; set; } = new();
}

public sealed class SuggestedDiscountItemDto
{
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>减免比例 0~1；null = 未设置（不与分店折扣比较），0 = 明确无折扣。</summary>
    public decimal? SuggestedDiscountRate { get; set; }
}

public sealed class SetSuggestedDiscountsRequestDto
{
    public List<string> ProductCodes { get; set; } = new();
    public decimal? SuggestedDiscountRate { get; set; }

    /// <summary>发起来源代码（WarehouseProducts / MobileWarehouse / BatchUpdate），写入审计与通知的发起来源。</summary>
    public string? Source { get; set; }
}

public sealed class SetSuggestedDiscountsResultDto
{
    public int ChangedCount { get; set; }
}

// ---------- 移动端：同步其它分店 ----------

public sealed class StoreProductSyncTargetDto
{
    public string StoreCode { get; set; } = string.Empty;
    public string? StoreName { get; set; }
    public bool HasRecord { get; set; }
    public decimal? RetailPrice { get; set; }
    public decimal? DiscountRate { get; set; }
    public bool IsSpecialProduct { get; set; }
}

public sealed class StoreProductSyncTargetsDto
{
    public string SourceStoreCode { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public decimal? SourceRetailPrice { get; set; }
    public decimal? SourceDiscountRate { get; set; }
    public decimal? SourcePurchasePrice { get; set; }
    public List<StoreProductSyncTargetDto> Targets { get; set; } = new();
}

public sealed class MobileSyncStoreProductToOtherStoresRequestDto
{
    public string ProductCode { get; set; } = string.Empty;
    public string SourceStoreCode { get; set; } = string.Empty;
    public List<string> TargetStoreCodes { get; set; } = new();
    public bool SyncRetailPrice { get; set; } = true;
    public bool SyncDiscountRate { get; set; } = true;
    public bool SyncPurchasePrice { get; set; }
}

public sealed class MobileSyncStoreProductToOtherStoresResultDto
{
    public int UpdatedStoreCount { get; set; }
    public PriceNotificationSummaryDto? PriceNotification { get; set; }
}
