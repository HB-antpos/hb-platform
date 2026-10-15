using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React;

/// <summary>改价发起人信息，冗余写入任务表供移动端与 Web 直接展示。</summary>
public sealed record PriceTaskInitiator(
    string Name,
    string Source,
    string? Reference = null,
    DateTime? OccurredAtUtc = null
);

/// <summary>分店价格被覆盖前的旧值（即货架标签上的价格）。必须在覆盖发生时采集，事后无法推断。</summary>
public sealed record StorePriceOverwrite(
    string StoreCode,
    string ProductCode,
    decimal? OldRetailPrice,
    decimal? OldDiscountRate
);

/// <summary>
/// 请求级（scoped）的通知汇总收集器：任务服务写入，controller 在响应前读取。
/// 这样改价链路上的方法签名都不需要为了"回传通知了几家店"而改动。
/// </summary>
public interface IPriceNotificationSummaryAccessor
{
    void Record(string storeCode, string productCode, PriceNotificationOutcome outcome);

    /// <summary>有任何评估发生过就返回汇总（可能全为 0，表示"未产生通知"）；从未评估过返回 null。</summary>
    PriceNotificationSummaryDto? GetSummary();
}

public enum PriceNotificationOutcome
{
    /// <summary>评估过但无需通知。</summary>
    None = 0,
    NeedsPriceUpdate = 1,
    LabelOnly = 2,
    Cancelled = 3,
    SkippedSpecial = 4,
}

public interface IStorePriceUpdateTaskService
{
    // ---------- 任务生成 ----------

    /// <summary>仓库零售价或建议折扣变化后，逐店比对并新建/刷新/取消任务。</summary>
    Task OnWarehousePriceChangedAsync(
        IReadOnlyCollection<string> productCodes,
        PriceTaskInitiator initiator,
        CancellationToken cancellationToken = default
    );

    /// <summary>分店价被覆盖（仓库自动下发、同步其它分店）后登记「待换标签」。须在覆盖写库之后调用。</summary>
    Task RecordStoreOverwritesAsync(
        IReadOnlyCollection<StorePriceOverwrite> overwrites,
        PriceTaskInitiator initiator,
        CancellationToken cancellationToken = default
    );

    /// <summary>用实时价格复核未完成任务（形态流转、自动取消）。storeCode 为空表示全部分店。</summary>
    Task<int> ReconcilePendingAsync(string? storeCode, CancellationToken cancellationToken = default);

    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);

    Task<PriceNotificationPreviewDto> PreviewAsync(
        string productCode,
        decimal? retailPrice,
        bool suggestedDiscountSpecified,
        decimal? suggestedDiscountRate,
        CancellationToken cancellationToken = default
    );

    // ---------- 建议折扣 ----------

    Task<IReadOnlyDictionary<string, decimal?>> GetSuggestedDiscountsAsync(
        IReadOnlyCollection<string> productCodes,
        CancellationToken cancellationToken = default
    );

    /// <summary>写入建议折扣；须在采集 after 快照之前调用，变更才会进入审计历史。返回值表示是否发生变化。</summary>
    Task<bool> SetSuggestedDiscountAsync(
        string productCode,
        decimal? suggestedDiscountRate,
        string updatedBy,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// 批量设置建议折扣并走完整审计流程（快照 → 写入 → 记录历史），历史收口会同步生成分店通知。
    /// 返回实际发生变化的商品数。
    /// </summary>
    Task<int> SetSuggestedDiscountsWithHistoryAsync(
        IReadOnlyCollection<string> productCodes,
        decimal? suggestedDiscountRate,
        string updatedBy,
        string source,
        CancellationToken cancellationToken = default
    );

    // ---------- 移动端 ----------

    Task<StorePriceUpdateTaskPageDto> GetPageAsync(
        StorePriceUpdateTaskQueryDto query,
        IReadOnlyCollection<string>? accessibleStoreCodes,
        CancellationToken cancellationToken = default
    );

    Task<int> GetPendingCountAsync(
        string storeCode,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// 按分店汇总未完成数量与超期数量（超期从任务首次发起起算）。
    /// accessibleStoreCodes 为 null 表示不限分店（管理员），否则只返回名单内的分店。
    /// 移动端每小时提醒、分店切换条、工作台角标、报表锁定都读这一份，避免逐店查询。
    /// </summary>
    Task<StorePriceUpdateTaskStoreSummaryListDto> GetStoreSummariesAsync(
        IReadOnlyCollection<string>? accessibleStoreCodes,
        CancellationToken cancellationToken = default
    );

    Task<StorePriceUpdateTaskBatchResultDto> ApplyAsync(
        ApplyStorePriceUpdateTasksRequestDto request,
        string updatedBy,
        string actorDisplayName,
        List<string>? accessibleStoreCodes,
        CancellationToken cancellationToken = default
    );

    Task<StorePriceUpdateTaskBatchResultDto> KeepStorePriceAsync(
        StorePriceUpdateTaskIdsRequestDto request,
        string actorDisplayName,
        CancellationToken cancellationToken = default
    );

    Task<StorePriceUpdateTaskBatchResultDto> MarkLabelsAsync(
        MarkStorePriceUpdateTaskLabelsRequestDto request,
        string actorDisplayName,
        CancellationToken cancellationToken = default
    );

    // ---------- Web 监控 ----------

    Task<StorePriceUpdateTaskSummaryDto> GetSummaryAsync(
        StorePriceUpdateTaskQueryDto query,
        CancellationToken cancellationToken = default
    );

    Task<List<StorePriceUpdateTaskStoreRowDto>> GetByStoreAsync(
        StorePriceUpdateTaskQueryDto query,
        CancellationToken cancellationToken = default
    );

    Task<StorePriceUpdateTaskProductPageDto> GetByProductAsync(
        StorePriceUpdateTaskQueryDto query,
        bool onlyIncomplete,
        CancellationToken cancellationToken = default
    );

    bool IsHqSyncEnabled { get; }
}
