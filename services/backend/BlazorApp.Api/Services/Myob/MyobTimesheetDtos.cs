namespace BlazorApp.Api.Services.Myob;

// 阶段 2：Timesheet 推送（方案 docs/plans/2026-10-27-myob-payroll-integration.md 第 8、11.1 节）。
// 本批只做「工资类别映射」与「只读推送预览」：预览不会往 MYOB 写任何东西；推送接口后续单独上线，PushEnabled 先恒为 false。
// 全局 JSON 为 camelCase；全部返回 ApiResponse<T>；日期一律 yyyy-MM-dd 字符串。
//
// 路由与权限：
//   GET /api/react/v1/myob/pay-categories?connectionGuid=&refresh=            Myob.Connection.Manage → MyobPayCategoriesDto
//   PUT /api/react/v1/myob/pay-categories/{connectionGuid}  Body MyobPayCategoryMappingDto → MyobPayCategoriesDto
//   GET /api/react/v1/myob/timesheet/preview?connectionGuid=&periodStart=&checkMyob=   Attendance.Payroll.Edit → MyobTimesheetPreviewDto
//        periodStart 省略 = 上一个已结束的工资周期；checkMyob=true 时才逐个员工去 MYOB 读 Timesheet 判断「已有记录 / 已发薪」（会产生 MYOB 读请求）。

/// <summary>我方转账工时对应的 MYOB 工资类别（5 项）。</summary>
public static class MyobPayCategoryKeys
{
    /// <summary>平日（含公假上班、带薪公假）。</summary>
    public const string Weekday = "weekday";

    public const string Saturday = "saturday";
    public const string Sunday = "sunday";

    /// <summary>病假 → MYOB 的 Personal Leave Pay。</summary>
    public const string PersonalLeave = "personalLeave";

    /// <summary>年假 → MYOB 的 Holiday Leave Pay。</summary>
    public const string HolidayLeave = "holidayLeave";

    public static readonly string[] All = [Weekday, Saturday, Sunday, PersonalLeave, HolidayLeave];
}

/// <summary>MYOB 里的一个工资类别（Payroll/PayrollCategory/Wage）。</summary>
public sealed class MyobWageCategoryDto
{
    public string Uid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Hourly / Salary；读不到为 null。</summary>
    public string? WageType { get; set; }
}

/// <summary>5 项映射：每项是 MYOB 工资类别的 UID，null = 未设置。同一个结构用于「当前映射」「建议映射」与保存请求。</summary>
public sealed class MyobPayCategoryMappingDto
{
    public string? WeekdayUid { get; set; }
    public string? SaturdayUid { get; set; }
    public string? SundayUid { get; set; }
    public string? PersonalLeaveUid { get; set; }
    public string? HolidayLeaveUid { get; set; }
}

public sealed class MyobPayCategoriesDto
{
    public string ConnectionGuid { get; set; } = string.Empty;
    public string ConnectionName { get; set; } = string.Empty;

    /// <summary>MYOB 工资类别最近一次读取时间（UTC）。</summary>
    public DateTime FetchedAt { get; set; }

    /// <summary>MYOB 里可选的工资类别（已按名称排序）。</summary>
    public List<MyobWageCategoryDto> Categories { get; set; } = [];

    /// <summary>已保存的映射。</summary>
    public MyobPayCategoryMappingDto Mapping { get; set; } = new();

    /// <summary>按名称相近给出的建议（只对尚未设置的项给出；已设置的项为 null）。</summary>
    public MyobPayCategoryMappingDto Suggestions { get; set; } = new();

    /// <summary>已设置的项数（0–5）。</summary>
    public int MappedCount { get; set; }

    /// <summary>5 项都已设置，且都还在 MYOB 的类别里。</summary>
    public bool Complete { get; set; }

    /// <summary>已保存的映射里，MYOB 现在已找不到的类别键（MyobPayCategoryKeys），需要重新选择。</summary>
    public List<string> MissingKeys { get; set; } = [];

    public DateTime? UpdatedAt { get; set; }
}

public static class MyobTimesheetErrorCodes
{
    public const string InvalidPeriod = "MYOB_TIMESHEET_INVALID_PERIOD";
    public const string CategoryNotFound = "MYOB_PAY_CATEGORY_NOT_FOUND";
    public const string CategoryDuplicate = "MYOB_PAY_CATEGORY_DUPLICATE";
    public const string Forbidden = "MYOB_TIMESHEET_FORBIDDEN";
}

// ───────────────────────── 推送预览 ─────────────────────────

/// <summary>预览行状态。</summary>
public static class MyobPreviewStatuses
{
    /// <summary>可推送。</summary>
    public const string Ready = "Ready";

    /// <summary>本公司分到的转账工时为 0（全现金，或额度被其他公司用完）：不推送。</summary>
    public const string NoTransfer = "NoTransfer";

    /// <summary>有拦截项（Blockers 非空）：推送时会被拒绝并列出原因。</summary>
    public const string Blocked = "Blocked";
}

public static class MyobBlockerCodes
{
    /// <summary>员工还没在这个公司文件里绑定员工卡。</summary>
    public const string NotBound = "NOT_BOUND";

    /// <summary>涉及的分店本期还没提交锁定。</summary>
    public const string StoreNotLocked = "STORE_NOT_LOCKED";

    /// <summary>涉及的分店还没映射到任何公司文件，算不出发薪公司。</summary>
    public const string StoreNotMapped = "STORE_NOT_MAPPED";

    /// <summary>要推送的工时用到了尚未映射的 MYOB 工资类别。</summary>
    public const string CategoryNotMapped = "CATEGORY_NOT_MAPPED";

    /// <summary>员工在本期没有生效的时薪记录，不知道转账 / 现金怎么分。</summary>
    public const string NoRate = "NO_RATE";

    /// <summary>当前账号没有权限查看涉及的分店，数据不完整。</summary>
    public const string NoStoreAccess = "NO_STORE_ACCESS";

    /// <summary>MYOB 里该周期已有已发薪（Processed）的记录，不覆盖。</summary>
    public const string MyobProcessed = "MYOB_PROCESSED";
}

public static class MyobHintCodes
{
    /// <summary>未成年员工过了生日：请先在我方时薪表新增生效记录，并同时在 MYOB 里改时薪。</summary>
    public const string BirthdayReview = "BIRTHDAY_REVIEW";

    /// <summary>本期用到的时薪低于员工的最低时薪。</summary>
    public const string BelowMinimumRate = "BELOW_MINIMUM_RATE";

    /// <summary>本期有公假工时 / 带薪公假，但公假价与平日价不一致。</summary>
    public const string PublicHolidayRateMismatch = "PUBLIC_HOLIDAY_RATE_MISMATCH";

    /// <summary>本期有年假折现工时：不推送，财务在 MYOB 手工处理。</summary>
    public const string AnnualCashOutNotPushed = "ANNUAL_CASH_OUT_NOT_PUSHED";
}

/// <summary>MYOB 对该员工该周期 Timesheet 的现状（只有 checkMyob=true 时才会读）。</summary>
public static class MyobTimesheetStates
{
    /// <summary>没有检查。</summary>
    public const string Unknown = "Unknown";

    /// <summary>MYOB 里本周期没有任何记录。</summary>
    public const string Clear = "Clear";

    /// <summary>已有未发薪的记录（推送会覆盖或合并，行为待实测确认）。</summary>
    public const string HasEntries = "HasEntries";

    /// <summary>已有已发薪（Processed）的记录。</summary>
    public const string Processed = "Processed";

    /// <summary>读取失败（网络 / 权限等）。</summary>
    public const string CheckFailed = "CheckFailed";
}

public sealed class MyobPreviewPeriodOptionDto
{
    /// <summary>周期起始日 yyyy-MM-dd。</summary>
    public string Start { get; set; } = string.Empty;

    public string End { get; set; } = string.Empty;
}

public sealed class MyobUnlockedStoreDto
{
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;

    /// <summary>本公司员工里在这家店有工时的人数。</summary>
    public int EmployeeCount { get; set; }
}

public sealed class MyobPreviewReadinessDto
{
    /// <summary>已提交锁定的分店（有本公司员工工时的分店）。</summary>
    public List<string> LockedStores { get; set; } = [];

    public List<MyobUnlockedStoreDto> UnlockedStores { get; set; } = [];

    /// <summary>工资类别已映射的项数（0–5）与是否完整。</summary>
    public int CategoryMappedCount { get; set; }

    public bool CategoryComplete { get; set; }

    /// <summary>是否读过 MYOB 的 Timesheet 状态（checkMyob=true）。</summary>
    public bool MyobChecked { get; set; }

    /// <summary>MYOB 里已发薪的员工数（仅 MyobChecked 时有意义）。</summary>
    public int MyobProcessedCount { get; set; }

    /// <summary>读 MYOB 失败的员工数（仅 MyobChecked 时有意义）。</summary>
    public int MyobCheckFailedCount { get; set; }
}

public sealed class MyobPreviewHintDto
{
    /// <summary>MyobHintCodes。</summary>
    public string Code { get; set; } = string.Empty;

    public string UserGuid { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;

    /// <summary>给人看的中文说明（含日期 / 工时等细节）。</summary>
    public string Message { get; set; } = string.Empty;
}

public sealed class MyobPreviewBlockerDto
{
    /// <summary>MyobBlockerCodes。</summary>
    public string Code { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    /// <summary>相关分店（STORE_NOT_LOCKED / STORE_NOT_MAPPED / NO_STORE_ACCESS）或类别键（CATEGORY_NOT_MAPPED）；没有为 null。</summary>
    public string? Ref { get; set; }
}

/// <summary>要推送的转账工时，按 5 个 MYOB 类别汇总（小时，2 位小数）。</summary>
public sealed class MyobPushHoursDto
{
    public decimal Weekday { get; set; }
    public decimal Saturday { get; set; }
    public decimal Sunday { get; set; }
    public decimal PersonalLeave { get; set; }
    public decimal HolidayLeave { get; set; }
}

/// <summary>日网格里的一行：Key 是类别键（MyobPayCategoryKeys）或工时种类键（见 MyobKindKeys），Hours 与 Days 一一对应。</summary>
public sealed class MyobDayGridRowDto
{
    public string Key { get; set; } = string.Empty;

    /// <summary>「将推送」网格里该行对应的 MYOB 工资类别名（如 Base Hourly）；只有读过 MYOB 类别列表（缓存里有）才有，没有为 null，前端回退显示我方叫法。</summary>
    public string? Label { get; set; }

    public List<decimal> Hours { get; set; } = [];
    public decimal Total { get; set; }
}

/// <summary>实际工时的种类键（日网格里「本公司实际工时」用）。</summary>
public static class MyobKindKeys
{
    public const string Weekday = "weekday";
    public const string Saturday = "saturday";
    public const string Sunday = "sunday";
    public const string PublicHolidayWork = "publicHolidayWork";
    public const string PublicHolidayPay = "publicHolidayPay";
    public const string Sick = "sick";
    public const string Annual = "annual";
    public const string AnnualCashOut = "annualCashOut";
}

/// <summary>跨公司时各公司分到的份额（只有员工有 ≥2 家公司的工时才有 ≥2 项）。</summary>
public sealed class MyobCompanyShareDto
{
    public string ConnectionGuid { get; set; } = string.Empty;
    public string ConnectionName { get; set; } = string.Empty;

    /// <summary>该公司名下的实际工时（小时）。</summary>
    public decimal ActualHours { get; set; }

    /// <summary>该公司分到的转账额度（小时）；全转账时等于实际工时，全现金时为 0。</summary>
    public decimal AllocatedTransferHours { get; set; }
}

public sealed class MyobPreviewDetailDto
{
    /// <summary>周期内每一天 yyyy-MM-dd（7 或 14 个）。</summary>
    public List<string> Days { get; set; } = [];

    /// <summary>本公司实际工时（不含现金 / 转账区分），行键 MyobKindKeys，只含有工时的种类。</summary>
    public List<MyobDayGridRowDto> Actual { get; set; } = [];

    /// <summary>将推送的转账工时，行键 MyobPayCategoryKeys，只含有工时的类别。</summary>
    public List<MyobDayGridRowDto> Pushed { get; set; } = [];

    /// <summary>不推送的现金工时，行键 MyobKindKeys（含占用转账额度的年假折现不在此，见 AnnualCashOutTransferHours）。</summary>
    public List<MyobDayGridRowDto> Cash { get; set; } = [];

    public List<MyobCompanyShareDto> Shares { get; set; } = [];

    /// <summary>年假折现里占用了转账额度、但不推送的小时（财务在 MYOB 手工处理）。</summary>
    public decimal AnnualCashOutTransferHours { get; set; }

    /// <summary>校验：推送合计 + 折现占用 = 本公司分到的转账额度（≤ 0.01 h 误差）。</summary>
    public bool CheckOk { get; set; }

    public string CheckMessage { get; set; } = string.Empty;
}

public sealed class MyobPreviewRowDto
{
    public string UserGuid { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;

    /// <summary>Transfer / Cash / Mixed；没有时薪记录为 null。</summary>
    public string? PayMode { get; set; }

    public decimal FixedTransferHoursPerWeek { get; set; }

    /// <summary>员工本期有 ≥2 家公司的工时。</summary>
    public bool IsCrossCompany { get; set; }

    /// <summary>本公司分到的转账额度（小时）；全转账时等于本公司实际工时。</summary>
    public decimal AllocatedTransferHours { get; set; }

    /// <summary>本公司名下的实际工时合计（小时）。</summary>
    public decimal ActualHours { get; set; }

    /// <summary>有工时的分店，如「1013 Orion」。</summary>
    public List<string> StoreLabels { get; set; } = [];

    /// <summary>MyobPreviewStatuses。</summary>
    public string Status { get; set; } = MyobPreviewStatuses.Ready;

    /// <summary>将推送的转账工时（按类别）。</summary>
    public MyobPushHoursDto Pushed { get; set; } = new();

    public decimal PushedTotal { get; set; }

    /// <summary>本期年假折现工时（不推送，财务手工处理）。</summary>
    public decimal CashOutHours { get; set; }

    public List<MyobPreviewBlockerDto> Blockers { get; set; } = [];

    /// <summary>该员工命中的提示码（详情见 Hints）。</summary>
    public List<string> HintCodes { get; set; } = [];

    /// <summary>已绑定的 MYOB 员工卡姓名；未绑定为 null。</summary>
    public string? MyobName { get; set; }

    /// <summary>MyobTimesheetStates。</summary>
    public string MyobState { get; set; } = MyobTimesheetStates.Unknown;

    public MyobPreviewDetailDto Detail { get; set; } = new();
}

public sealed class MyobPreviewTotalsDto
{
    public int ReadyCount { get; set; }
    public decimal ReadyHours { get; set; }
    public int BlockedCount { get; set; }
    public int NoTransferCount { get; set; }
}

public sealed class MyobTimesheetPreviewDto
{
    public string ConnectionGuid { get; set; } = string.Empty;
    public string ConnectionName { get; set; } = string.Empty;
    public string PeriodStart { get; set; } = string.Empty;
    public string PeriodEnd { get; set; } = string.Empty;
    public int Weeks { get; set; } = 1;

    /// <summary>可选的工资周期：最近 6 期，新的在前，第一项是当前期。</summary>
    public List<MyobPreviewPeriodOptionDto> Periods { get; set; } = [];

    public MyobPreviewReadinessDto Readiness { get; set; } = new();

    /// <summary>不阻止推送的提示（需勾选「已知晓」）。</summary>
    public List<MyobPreviewHintDto> Hints { get; set; } = [];

    /// <summary>已按「可推送 → 被拦截 → 不推送」排序，同档按姓名。</summary>
    public List<MyobPreviewRowDto> Rows { get; set; } = [];

    public MyobPreviewTotalsDto Totals { get; set; } = new();

    /// <summary>推送功能是否已开放。本批恒为 false：前端「推送到 MYOB」按钮显示为禁用并提示「推送功能稍后开放」。</summary>
    public bool PushEnabled { get; set; }
}
