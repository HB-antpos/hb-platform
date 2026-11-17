namespace BlazorApp.Api.Services.Myob;

// 「从 MYOB 同步转账时薪」（方案 docs/plans/2026-11-16-myob-hourly-rate-sync.md）。全局 JSON 为 camelCase；全部返回 ApiResponse<T>。
// 只读 MYOB，只往我方 EmployeePayRate 新增记录（只新增、不改历史）；只对「已绑定且 MYOB 卡仍存在」的员工生效。
//
// 路由与权限（类级同时要求 Attendance.Payroll.Edit 与 Myob.Employee.Bind，两个策略是「且」的关系）：
//   POST /api/react/v1/myob/pay-rate-sync/preview   Body MyobPayRateSyncPreviewRequest → MyobPayRateSyncPreviewDto
//   POST /api/react/v1/myob/pay-rate-sync/apply     Body MyobPayRateSyncApplyRequest   → MyobPayRateSyncApplyResultDto
//
// 隐私：响应只含我方员工名与时薪数；不含 MYOB 员工 UID、年薪、任何 MYOB 个人资料。

/// <summary>时薪同步里一行的类别。</summary>
public static class MyobPayRateSyncKinds
{
    /// <summary>我方还没有任何时薪记录：新建一条（全转账）。</summary>
    public const string Create = "Create";

    /// <summary>我方有记录但转账平日价为空：补空。</summary>
    public const string Fill = "Fill";

    /// <summary>我方转账平日价与 MYOB 不同：覆盖（默认不勾）。</summary>
    public const string Differ = "Differ";

    /// <summary>平日价已一致（周六 / 周日 / 公假仍可能有可选的建议值）。</summary>
    public const string Same = "Same";

    /// <summary>不参与同步，原因见 SkipReason。</summary>
    public const string Skip = "Skip";
}

/// <summary>可以单独勾选的价格格（apply 请求里每个员工勾了哪几格）。</summary>
public static class MyobPayRateSyncCells
{
    public const string Weekday = "weekday";
    public const string Saturday = "saturday";
    public const string Sunday = "sunday";
    public const string PublicHoliday = "publicHoliday";
}

/// <summary>Skip 原因与「无法推算」原因（同一套码，前端按码出文案）。</summary>
public static class MyobPayRateSyncReasons
{
    /// <summary>MYOB 卡不是时薪制（PayBasis 非 Hourly），或基础时薪没填 / ≤ 0。</summary>
    public const string NotHourly = "MYOB_RATE_NOT_HOURLY";

    /// <summary>MYOB 卡已有离职日期且已过。</summary>
    public const string Terminated = "MYOB_EMPLOYEE_TERMINATED";

    /// <summary>已绑定，但 MYOB 里已经找不到这张员工卡（被删除或绑错公司文件）。</summary>
    public const string CardMissing = "MYOB_CARD_MISSING";

    /// <summary>我方是全现金：转账价不参与计算，MYOB 值没有意义。</summary>
    public const string PayModeCash = "PAY_MODE_CASH";

    /// <summary>映射页没配这一档（临时工的周末 / 公假是可选映射，没配就无法推算）。</summary>
    public const string CategoryNotMapped = "CATEGORY_NOT_MAPPED";

    /// <summary>映射里选的类别在 MYOB 里已经不存在。</summary>
    public const string CategoryNotFound = "CATEGORY_NOT_FOUND";

    /// <summary>该员工的 MYOB 卡没有挂这个类别（没挂的类别推送时 MYOB 也不会按它发薪）。</summary>
    public const string CategoryNotOnCard = "CATEGORY_NOT_ON_CARD";

    /// <summary>类别不是「基础时薪 × 倍数」：PayRate 不是 RegularRate（如固定时薪），或倍数缺失 / ≤ 0。</summary>
    public const string CategoryNotRateMultiple = "CATEGORY_NOT_RATE_MULTIPLE";
}

public static class MyobPayRateSyncErrorCodes
{
    public const string InvalidRequest = "MYOB_RATE_SYNC_INVALID_REQUEST";
    public const string NoItems = "MYOB_RATE_SYNC_NO_ITEMS";

    // 逐员工失败原因（MyobPayRateSyncFailureDto.Code）；其余原因直接沿用 CreatePayRateAsync 的错误码（PERIOD_LOCKED、RATE_EFFECTIVE_DATE_EXISTS 等）。
    public const string NotInPreview = "MYOB_RATE_SYNC_NOT_IN_PREVIEW";
    public const string NotBound = "MYOB_RATE_SYNC_NOT_BOUND";
    public const string ValueChanged = "MYOB_RATE_SYNC_VALUE_CHANGED";
    public const string NotApplicable = "MYOB_RATE_SYNC_NOT_APPLICABLE";
}

public sealed class MyobPayRateSyncPreviewRequest
{
    public string ConnectionGuid { get; set; } = string.Empty;

    /// <summary>员工范围 = 该分店的员工（走现有时薪表的分店权限）。</summary>
    public string StoreCode { get; set; } = string.Empty;

    /// <summary>生效日期；不传则用「下一个未锁定周期起始日」。</summary>
    public DateTime? EffectiveFrom { get; set; }

    /// <summary>true = 绕过 10 分钟缓存重新读 MYOB。</summary>
    public bool Refresh { get; set; }
}

/// <summary>一个价格格：我方现值、MYOB 推算出的建议值、能否应用、默认是否勾上。</summary>
public sealed class MyobPayRateSyncCellDto
{
    public decimal? OurRate { get; set; }

    /// <summary>平日格 = MYOB 基础时薪取 2 位小数；周六 / 周日 / 公假格 = 基础价 × 映射类别倍数取 2 位小数。无法推算时为 null。</summary>
    public decimal? Suggested { get; set; }

    /// <summary>用到的倍数（平日格为 null）。</summary>
    public decimal? Multiplier { get; set; }

    /// <summary>有建议值、且与我方现值不同（我方为空也算不同）：这一格才有东西可写。</summary>
    public bool CanApply { get; set; }

    /// <summary>无法推算的原因码（MyobPayRateSyncReasons）；能推算时为 null。</summary>
    public string? UnavailableReason { get; set; }

    public bool DefaultSelected { get; set; }
}

public sealed class MyobPayRateSyncRowDto
{
    public string UserGuid { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;

    /// <summary>我方员工类型：FullTime / PartTime / Casual；没填为 null（按全职兼职处理）。</summary>
    public string? EmploymentType { get; set; }

    public string Kind { get; set; } = MyobPayRateSyncKinds.Skip;

    /// <summary>Kind = Skip 时的原因码。</summary>
    public string? SkipReason { get; set; }

    /// <summary>我方现行记录的发放方式；还没有记录为 null。</summary>
    public string? PayMode { get; set; }

    /// <summary>MYOB 基础时薪原值（可能有 4 位小数）。Skip 且不是因为 PayBasis 时也带上，方便核对；年薪制不带。</summary>
    public decimal? MyobHourlyRate { get; set; }

    /// <summary>原值取整到 2 位后与原值不同。</summary>
    public bool Rounded { get; set; }

    public MyobPayRateSyncCellDto Weekday { get; set; } = new();
    public MyobPayRateSyncCellDto Saturday { get; set; } = new();
    public MyobPayRateSyncCellDto Sunday { get; set; } = new();
    public MyobPayRateSyncCellDto PublicHoliday { get; set; } = new();

    /// <summary>覆盖「我方现值 + MYOB 值 + 三个建议值」；apply 时重新计算并比对，不一致就不写。</summary>
    public string Hash { get; set; } = string.Empty;
}

public sealed class MyobPayRateSyncPreviewDto
{
    public string ConnectionGuid { get; set; } = string.Empty;
    public string ConnectionName { get; set; } = string.Empty;
    public string StoreCode { get; set; } = string.Empty;
    public DateTime EffectiveFrom { get; set; }

    /// <summary>MYOB 员工卡与工资类别最近一次读取时间（UTC）。</summary>
    public DateTime FetchedAt { get; set; }

    /// <summary>该分店里没有绑定这个 MYOB 公司文件的员工数（不出现在 Rows 里）。</summary>
    public int NotBoundCount { get; set; }

    public List<MyobPayRateSyncRowDto> Rows { get; set; } = [];
}

/// <summary>apply 里一个员工：预览时的哈希 + 勾选的格（用 MyobPayRateSyncCells 的值）。</summary>
public sealed class MyobPayRateSyncApplyItem
{
    public string UserGuid { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public List<string> Cells { get; set; } = [];
}

public sealed class MyobPayRateSyncApplyRequest
{
    public string ConnectionGuid { get; set; } = string.Empty;
    public string StoreCode { get; set; } = string.Empty;
    public DateTime EffectiveFrom { get; set; }
    public List<MyobPayRateSyncApplyItem> Items { get; set; } = [];
}

public sealed class MyobPayRateSyncWrittenDto
{
    public string UserGuid { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string RateGuid { get; set; } = string.Empty;
    public List<string> Cells { get; set; } = [];
}

public sealed class MyobPayRateSyncFailureDto
{
    public string UserGuid { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class MyobPayRateSyncApplyResultDto
{
    public DateTime EffectiveFrom { get; set; }
    public List<MyobPayRateSyncWrittenDto> Written { get; set; } = [];
    public List<MyobPayRateSyncFailureDto> Failed { get; set; } = [];
}
