namespace BlazorApp.Shared.DTOs;

// 分店现金管理（存银行 / 现金支出 / 现金池）的接口契约。基础路径 api/react/v1/cash，
// 响应统一包在 ApiResponse<T> 里（success / data / message / errorCode），JSON 为 camelCase。
// 金额一律是澳元 decimal；日期 DateOnly 序列化为 yyyy-MM-dd（门店本地日期）；时间 DateTime 为 UTC。
// T2 在类别码与界面里只叫 T2。

/// <summary>可操作的分店：店长是自己关联的分店，有全部分店权限者是全部分店。</summary>
public sealed class CashStoreOptionDto
{
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = string.Empty;

    /// <summary>该店当前的本地日期，客户端的日期选择、补录范围都以它为准，不用手机时区。</summary>
    public DateOnly StoreToday { get; set; }

    /// <summary>该店是否启用收银系统（Store.IsActive）；Web 总览默认只显示启用的分店。</summary>
    public bool CashRegisterEnabled { get; set; }
}

public sealed class CashCapabilitiesDto
{
    public bool CanCreateDeposit { get; set; }
    public bool CanCreateExpense { get; set; }

    /// <summary>有全部分店权限：看全部分店、全部历史 T2，补录不受回溯天数限制。</summary>
    public bool CanViewAllStores { get; set; }

    /// <summary>有作废权限：可作废范围内任何记录；没有时只能在时限内作废自己录入的。</summary>
    public bool CanVoid { get; set; }
}

/// <summary>GET cash/context：进入「现金」入口时一次取回的范围、权限与业务阈值。</summary>
public sealed class CashContextDto
{
    public List<CashStoreOptionDto> Stores { get; set; } = [];
    public CashCapabilitiesDto Capabilities { get; set; } = new();

    /// <summary>后端日结数据是否已接入。未接入时现金池余额、日结流入都不可算，页面应提示而不是显示 0。</summary>
    public bool DailyCloseConnected { get; set; }

    public int T2VisibleDays { get; set; }
    public int MaxBackfillDays { get; set; }
    public int SelfVoidHours { get; set; }
    public int DepositOverdueDays { get; set; }
    public decimal DepositDifferenceReasonThreshold { get; set; }
    public int MaxSlipsPerDeposit { get; set; }
    public int MaxImagesPerSlip { get; set; }
    public int MaxImagesPerExpense { get; set; }
}

public sealed class CashPagedDto<T>
{
    public List<T> Items { get; set; } = [];
    public int Total { get; set; }
}

// ───────────────────────── 现金池总览 ─────────────────────────

/// <summary>期初或盘点记录。Difference = Amount − ExpectedAmount，仅盘点且当时日结已接入才有。</summary>
public sealed class CashBalanceEntryDto
{
    public string EntryGuid { get; set; } = string.Empty;
    public string StoreCode { get; set; } = string.Empty;

    /// <summary>Opening / Count。</summary>
    public string EntryType { get; set; } = string.Empty;

    public DateOnly EntryDate { get; set; }
    public decimal Amount { get; set; }
    public decimal? ExpectedAmount { get; set; }
    public decimal? Difference { get; set; }
    public string? Note { get; set; }

    /// <summary>Active / Voided。</summary>
    public string Status { get; set; } = string.Empty;

    public string? CreatedByName { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public bool CanVoid { get; set; }
}

public sealed class CashExpenseCategoryTotalDto
{
    /// <summary>Salary / Purchase / T2 / Other。</summary>
    public string Category { get; set; } = string.Empty;

    public decimal Amount { get; set; }
}

/// <summary>GET cash/summary?storeCode=：单店现金池总览。</summary>
public sealed class CashStoreSummaryDto
{
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;

    /// <summary>统计截止日 = 门店今天（本地日期），不是最近有日结的那天。</summary>
    public DateOnly AsOfDate { get; set; }

    public bool DailyCloseConnected { get; set; }

    /// <summary>最近一个有日结存档的营业日（期初日起，没有期初时为最近 60 天内）；存款表单用它作默认覆盖范围的终点。</summary>
    public DateOnly? LatestCloseDate { get; set; }

    /// <summary>没有有效的期初记录：现金池余额无法计算，页面引导店长先录入期初现金。</summary>
    public bool OpeningMissing { get; set; }

    public CashBalanceEntryDto? Opening { get; set; }

    /// <summary>现金池余额 = 期初 + 日结现金 − 存款 − 支出；日结未接入或没有期初时为 null。</summary>
    public decimal? PoolBalance { get; set; }

    /// <summary>期初日起纳入的日结实点现金合计；日结未接入时为 null。</summary>
    public decimal? InflowTotal { get; set; }

    public decimal DepositTotal { get; set; }

    /// <summary>期初日起的支出真实合计（含店长看不到明细的旧 T2）。</summary>
    public decimal ExpenseTotal { get; set; }

    /// <summary>分类合计。无全部分店权限者的 T2 只含最近 T2VisibleDays 天，所以分类之和可能小于 ExpenseTotal。</summary>
    public List<CashExpenseCategoryTotalDto> ExpenseByCategory { get; set; } = [];

    /// <summary>当前账号看到的 T2 受 14 天窗口限制。</summary>
    public bool T2Restricted { get; set; }

    /// <summary>有日结现金、却还没被任何有效存款覆盖的营业日个数；日结未接入时为 0。</summary>
    public int UncoveredDayCount { get; set; }

    public DateOnly? OldestUncoveredDate { get; set; }

    /// <summary>未覆盖营业日的日结现金合计。</summary>
    public decimal? UncoveredCash { get; set; }

    /// <summary>最久未存的营业日距今超过 DepositOverdueDays 天。</summary>
    public bool DepositOverdue { get; set; }

    /// <summary>建议存款额 = 现金池余额（不含备用金，已在点钱前取出），余额不为正或不可算时为 null。</summary>
    public decimal? SuggestedDepositAmount { get; set; }

    public DateOnly? LastDepositDate { get; set; }
    public CashBalanceEntryDto? LastCount { get; set; }

    /// <summary>最近 14 天里没有任何日结存档的营业日（可能是休息日，仅提示）。</summary>
    public List<DateOnly> MissingCloseDates { get; set; } = [];
}

// ───────────────────────── 多店总览（Web） ─────────────────────────

/// <summary>
/// GET cash/overview?from=&amp;to=&amp;storeCodes=：每个可见分店一行。
/// 「区间」字段只统计 [From, To]（门店本地营业日 / 存款日 / 支出日）；现金池余额、未存天数等「当前」字段截止到门店今天。
/// </summary>
public sealed class CashOverviewDto
{
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public bool DailyCloseConnected { get; set; }

    /// <summary>当前账号受 T2 窗口限制：区间里的 T2 只含最近 14 天，分类之和可能小于支出合计。</summary>
    public bool T2Restricted { get; set; }

    public List<CashOverviewRowDto> Rows { get; set; } = [];

    /// <summary>各分店合计；任一分店的可空字段为 null 时，对应合计也为 null，避免把不可算的店当 0 加进去。</summary>
    public CashOverviewTotalsDto Totals { get; set; } = new();
}

public sealed class CashOverviewRowDto
{
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;
    public DateOnly StoreToday { get; set; }

    // 当前（截止门店今天）
    public bool OpeningMissing { get; set; }
    public decimal? PoolBalance { get; set; }
    public int UncoveredDayCount { get; set; }
    public DateOnly? OldestUncoveredDate { get; set; }
    public bool DepositOverdue { get; set; }
    public DateOnly? LastDepositDate { get; set; }

    // 区间
    /// <summary>区间内纳入的日结实点现金合计；日结未接入时为 null。</summary>
    public decimal? InflowCash { get; set; }

    /// <summary>区间内纳入日结的差异合计（实点 − 应有，正为长款、负为短款）；日结未接入时为 null。</summary>
    public decimal? CloseVariance { get; set; }

    /// <summary>区间内有日结的营业日数。</summary>
    public int CloseDayCount { get; set; }

    /// <summary>区间内（不含门店今天）没有任何日结的营业日数；可能是休息日，仅提示。日结未接入时为 0。</summary>
    public int MissingCloseDayCount { get; set; }

    public decimal DepositTotal { get; set; }
    public int DepositCount { get; set; }

    /// <summary>区间内支出真实合计。</summary>
    public decimal ExpenseTotal { get; set; }

    /// <summary>区间内按类别合计（四类固定顺序）；T2 受当前账号可见窗口限制。</summary>
    public List<CashExpenseCategoryTotalDto> ExpenseByCategory { get; set; } = [];

    /// <summary>区间内被标记为「存疑」的支出笔数。</summary>
    public int FlaggedExpenseCount { get; set; }
}

public sealed class CashOverviewTotalsDto
{
    public decimal? PoolBalance { get; set; }
    public decimal? InflowCash { get; set; }
    public decimal? CloseVariance { get; set; }
    public decimal DepositTotal { get; set; }
    public int DepositCount { get; set; }
    public decimal ExpenseTotal { get; set; }
    public List<CashExpenseCategoryTotalDto> ExpenseByCategory { get; set; } = [];
    public int UncoveredDayCount { get; set; }
    public int OverdueStoreCount { get; set; }
    public int FlaggedExpenseCount { get; set; }
}

// ───────────────────────── 按日明细与日结选择 ─────────────────────────

public sealed class CashCloseArchiveDto
{
    public string CloseId { get; set; } = string.Empty;
    public DateTime SavedAtUtc { get; set; }
    public DateTime PeriodFromUtc { get; set; }
    public DateTime PeriodToUtc { get; set; }
    public decimal CountedCash { get; set; }
    public decimal ExpectedCash { get; set; }
    public decimal Variance { get; set; }

    /// <summary>当前是否纳入现金池。</summary>
    public bool Included { get; set; }
}

public sealed class CashDailyDeviceDto
{
    public string DeviceCode { get; set; } = string.Empty;

    /// <summary>Default / Manual。</summary>
    public string SelectionMode { get; set; } = "Default";

    /// <summary>手选之后又出现了更新的存档，需要人确认。</summary>
    public bool SelectionStale { get; set; }

    public bool SelectionOverlapWarning { get; set; }
    public string? SelectionReason { get; set; }
    public string? SelectedByName { get; set; }
    public DateTime? SelectedAtUtc { get; set; }

    /// <summary>该设备当天纳入现金池的现金合计。</summary>
    public decimal IncludedCash { get; set; }

    public List<CashCloseArchiveDto> Archives { get; set; } = [];
}

public sealed class CashDailyRowDto
{
    public DateOnly BusinessDate { get; set; }

    /// <summary>当天各设备纳入的日结现金合计。</summary>
    public decimal InflowCash { get; set; }

    public bool HasClose { get; set; }

    /// <summary>被某张有效存款的覆盖范围包含。</summary>
    public bool Covered { get; set; }

    public string? CoveredByDepositGuid { get; set; }

    /// <summary>当天当前账号可见的支出合计。</summary>
    public decimal ExpenseTotal { get; set; }

    public List<CashDailyDeviceDto> Devices { get; set; } = [];
}

/// <summary>GET cash/daily?storeCode=&amp;from=&amp;to=（含两端，最多 93 天）。</summary>
public sealed class CashDailyDto
{
    public string StoreCode { get; set; } = string.Empty;
    public bool DailyCloseConnected { get; set; }
    public List<CashDailyRowDto> Rows { get; set; } = [];
}

/// <summary>PUT cash/close-selection：手选多份日结求和，或恢复默认。</summary>
public sealed class CashCloseSelectionRequest
{
    public string StoreCode { get; set; } = string.Empty;
    public DateOnly BusinessDate { get; set; }
    public string DeviceCode { get; set; } = string.Empty;

    /// <summary>Default / Manual。</summary>
    public string Mode { get; set; } = "Manual";

    /// <summary>Manual 时必填，且必须都是该设备该营业日已有的存档编号。</summary>
    public List<string> CloseIds { get; set; } = [];

    /// <summary>手选必须填原因（统计区间重叠时尤其要说明）。</summary>
    public string? Reason { get; set; }
}

// ───────────────────────── 图片上传 ─────────────────────────

/// <summary>POST cash/attachments/upload-signature：为一张存单或收据图片签发私有直传地址。</summary>
public sealed class CashAttachmentUploadRequest
{
    public string StoreCode { get; set; } = string.Empty;

    /// <summary>image/jpeg、image/png 或 image/webp。</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>字节数，不超过 5 MiB。</summary>
    public long FileSize { get; set; }
}

public sealed class CashAttachmentUploadSignatureDto
{
    /// <summary>上传完成后在提交存款或支出时带上的附件编号。</summary>
    public string AttachmentGuid { get; set; } = string.Empty;

    /// <summary>对象存储的预签名 PUT 地址，客户端必须带上 Headers 里的全部请求头。</summary>
    public string Url { get; set; } = string.Empty;

    public Dictionary<string, string> Headers { get; set; } = new();
    public DateTime ExpiresAtUtc { get; set; }
}

public sealed class CashAttachmentDto
{
    public string AttachmentGuid { get; set; } = string.Empty;

    /// <summary>带过期时间的私有下载地址（几分钟有效），每次拉详情时重新签发。</summary>
    public string Url { get; set; } = string.Empty;

    public DateTime UrlExpiresAtUtc { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

// ───────────────────────── 存款 ─────────────────────────

public sealed class CashDepositSlipInput
{
    public decimal Amount { get; set; }
    public string? SlipNo { get; set; }

    /// <summary>这张存单的照片（至少 1 张，最多 3 张），都是上传签名返回的附件编号。</summary>
    public List<string> AttachmentGuids { get; set; } = [];
}

/// <summary>POST cash/deposits。ClientRequestId 由客户端生成，重试同一次提交必须带同一个值。</summary>
public sealed class CreateCashDepositRequest
{
    public string ClientRequestId { get; set; } = string.Empty;
    public string StoreCode { get; set; } = string.Empty;
    public DateOnly DepositDate { get; set; }
    public DateOnly? CoveredFromDate { get; set; }
    public DateOnly? CoveredToDate { get; set; }
    public string? Note { get; set; }

    /// <summary>存款合计与建议存款额相差超过阈值时必填。</summary>
    public string? OverrideReason { get; set; }

    public List<CashDepositSlipInput> Slips { get; set; } = [];
}

public sealed class CashDepositSlipDto
{
    public string SlipGuid { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string? SlipNo { get; set; }
    public List<CashAttachmentDto> Attachments { get; set; } = [];
}

/// <summary>存单摘要：银行对账按存单粒度匹配入账流水，列表与导出都需要逐张的金额与存单号。</summary>
public sealed class CashDepositSlipSummaryDto
{
    public string SlipGuid { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string? SlipNo { get; set; }
    public int ImageCount { get; set; }
}

public class CashDepositListItemDto
{
    public string DepositGuid { get; set; } = string.Empty;
    public string StoreCode { get; set; } = string.Empty;
    public DateOnly DepositDate { get; set; }
    public DateOnly? CoveredFromDate { get; set; }
    public DateOnly? CoveredToDate { get; set; }
    public decimal TotalAmount { get; set; }
    public int SlipCount { get; set; }
    public int ImageCount { get; set; }

    /// <summary>按录入顺序的存单摘要。</summary>
    public List<CashDepositSlipSummaryDto> SlipSummaries { get; set; } = [];

    /// <summary>Active / Voided。</summary>
    public string Status { get; set; } = string.Empty;

    public string? Note { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public bool CanVoid { get; set; }
}

public sealed class CashDepositDetailDto : CashDepositListItemDto
{
    public string? OverrideReason { get; set; }
    public string? VoidReason { get; set; }
    public string? VoidedByName { get; set; }
    public DateTime? VoidedAtUtc { get; set; }
    public List<CashDepositSlipDto> Slips { get; set; } = [];
}

/// <summary>POST cash/deposits/{id}/void、cash/expenses/{id}/void、cash/entries/{id}/void 共用。</summary>
public sealed class CashVoidRequest
{
    public string? Reason { get; set; }
}

// ───────────────────────── 现金支出 ─────────────────────────

/// <summary>POST cash/expenses。购物必须带至少 1 张收据；发生日期不能晚于门店今天，店长最多回溯 MaxBackfillDays 天。</summary>
public sealed class CreateCashExpenseRequest
{
    public string ClientRequestId { get; set; } = string.Empty;
    public string StoreCode { get; set; } = string.Empty;
    public DateOnly ExpenseDate { get; set; }

    /// <summary>Salary / Purchase / T2 / Other。</summary>
    public string Category { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    /// <summary>工资可选：关联员工，服务端会用员工姓名补全 PayeeName。</summary>
    public string? PayeeUserGuid { get; set; }

    public string? PayeeName { get; set; }
    public string? Note { get; set; }
    public List<string> AttachmentGuids { get; set; } = [];
}

public class CashExpenseListItemDto
{
    public string ExpenseGuid { get; set; } = string.Empty;
    public string StoreCode { get; set; } = string.Empty;
    public DateOnly ExpenseDate { get; set; }
    public string Category { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string? PayeeName { get; set; }
    public string? Note { get; set; }

    /// <summary>None / Reviewed / Flagged：财务事后核对标记，不影响支出生效。</summary>
    public string ReviewStatus { get; set; } = "None";

    public string? ReviewNote { get; set; }
    public string? ReviewedByName { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }

    /// <summary>当前账号能否打核对标记（持有 Cash.Void，且记录有效）。</summary>
    public bool CanReview { get; set; }

    /// <summary>Active / Voided。</summary>
    public string Status { get; set; } = string.Empty;

    public int ImageCount { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public bool CanVoid { get; set; }
}

public sealed class CashExpenseDetailDto : CashExpenseListItemDto
{
    public string? PayeeUserGuid { get; set; }
    public string? VoidReason { get; set; }
    public string? VoidedByName { get; set; }
    public DateTime? VoidedAtUtc { get; set; }
    public List<CashAttachmentDto> Attachments { get; set; } = [];
}

/// <summary>
/// POST cash/expenses/{id}/review：财务事后核对标记（Reviewed 已核 / Flagged 存疑 / None 清除），需要 Cash.Void。
/// 存疑必须写说明；标记不影响支出生效与现金池。
/// </summary>
public sealed class CashExpenseReviewRequest
{
    public string ReviewStatus { get; set; } = string.Empty;
    public string? Note { get; set; }
}

// ───────────────────────── 期初与盘点 ─────────────────────────

/// <summary>PUT cash/opening：录入期初现金（每店只能有一条有效记录）。</summary>
public sealed class SetCashOpeningRequest
{
    public string ClientRequestId { get; set; } = string.Empty;
    public string StoreCode { get; set; } = string.Empty;

    /// <summary>期初日期：自这一天起纳入现金池，不能晚于门店今天。</summary>
    public DateOnly EntryDate { get; set; }

    public decimal Amount { get; set; }
    public string? Note { get; set; }
}

/// <summary>POST cash/counts：盘点，表示 EntryDate 当天结束时店里实际现金。</summary>
public sealed class CreateCashCountRequest
{
    public string ClientRequestId { get; set; } = string.Empty;
    public string StoreCode { get; set; } = string.Empty;
    public DateOnly EntryDate { get; set; }
    public decimal Amount { get; set; }
    public string? Note { get; set; }
}
