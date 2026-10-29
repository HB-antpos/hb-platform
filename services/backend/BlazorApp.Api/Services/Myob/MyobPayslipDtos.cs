namespace BlazorApp.Api.Services.Myob;

// MYOB 历史工资单（方案 docs/plans/2026-10-29-myob-payslip-history.md）。全局 JSON 为 camelCase；全部返回 ApiResponse<T>。
// 隐私：这里的 DTO 只含展示字段。年薪、生日、雇佣分类在 MyobPayrollAdviceRecord 里就已被丢弃，不可能出现在响应里；
// 时薪（Hourly Rate）与支票号（Reference Number）只在明细 / PDF 里出现（还原 MYOB 默认 Pay Slip 页头），列表 DTO 不含；
// 银行账号 / TFN 本功能根本不读取。

public static class MyobPayslipErrorCodes
{
    public const string Forbidden = "MYOB_PAYSLIP_FORBIDDEN";
    public const string InvalidRange = "MYOB_PAYSLIP_INVALID_RANGE";
    public const string NotFound = "MYOB_PAYSLIP_NOT_FOUND";
    public const string ViewLogFailed = "MYOB_PAYSLIP_VIEW_LOG_FAILED";
    public const string PdfFailed = "MYOB_PAYSLIP_PDF_FAILED";
    public const string EmailInvalid = "MYOB_PAYSLIP_EMAIL_INVALID";
    public const string RecipientUnconfirmed = "MYOB_PAYSLIP_RECIPIENT_UNCONFIRMED";
    public const string EmailNotConfigured = "MYOB_PAYSLIP_EMAIL_NOT_CONFIGURED";
    public const string EmailSendFailed = "MYOB_PAYSLIP_EMAIL_SEND_FAILED";
}

/// <summary>查看记录（MyobPayslipViewLog.Action）的动作取值。</summary>
public static class MyobPayslipLogActions
{
    public const string View = "View";
    public const string Download = "Download";
    public const string Email = "Email";

    /// <summary>邮件发送失败（记录先写、发送后失败时改成这个值）。</summary>
    public const string EmailFailed = "EmailFailed";
}

/// <summary>「该公司文件里这一张工资单属于哪些分店」的归类结果。</summary>
public static class MyobPayslipPlacements
{
    /// <summary>该周期有考勤 / 请假 / 带薪公假数据的分店（支付日当天发薪公司 = 本公司）。</summary>
    public const string Attendance = "Attendance";

    /// <summary>该周期没有考勤数据，按员工当前所属分店推断（界面用虚线标签提示）。</summary>
    public const string Inferred = "Inferred";

    /// <summary>员工已绑定，但既没有考勤数据，当前所属分店里也没有由本公司发薪的分店。仅「全部分店」权限可见。</summary>
    public const string Unknown = "Unknown";

    /// <summary>MYOB 员工卡没有绑定我方员工。仅「全部分店」权限可见。</summary>
    public const string Unbound = "Unbound";
}

public sealed class MyobPayslipStoreDto
{
    public string StoreCode { get; set; } = string.Empty;
    public string StoreLabel { get; set; } = string.Empty;
}

public sealed class MyobPayslipCompanyDto
{
    public string ConnectionGuid { get; set; } = string.Empty;
    public string ConnectionName { get; set; } = string.Empty;
}

/// <summary>某公司文件这次读取的状态：一家失败不拖垮另一家。</summary>
public sealed class MyobPayslipCompanyStatusDto
{
    public string ConnectionGuid { get; set; } = string.Empty;
    public string ConnectionName { get; set; } = string.Empty;
    public bool Ok { get; set; }

    /// <summary>读到的工资单张数（未按分店 / 员工过滤前）。</summary>
    public int Count { get; set; }

    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class MyobPayslipRowDto
{
    public string ConnectionGuid { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;

    /// <summary>MYOB 员工卡 UID：只用于打开抽屉的路径参数，页面不显示。</summary>
    public string EmployeeUid { get; set; } = string.Empty;

    public string EmployeeName { get; set; } = string.Empty;
    public string? DisplayId { get; set; }

    /// <summary>我方员工 GUID；未绑定时为空（前端据此给「去绑定」）。</summary>
    public string? UserGuid { get; set; }

    public string PaymentDate { get; set; } = string.Empty;
    public string? PeriodStart { get; set; }
    public string? PeriodEnd { get; set; }
    public string? PayFrequency { get; set; }
    public decimal? GrossPay { get; set; }

    /// <summary>税行（Type = Tax）金额合计，MYOB 里为负数。</summary>
    public decimal TaxTotal { get; set; }

    public decimal? NetPay { get; set; }

    /// <summary><see cref="MyobPayslipPlacements"/>。</summary>
    public string Placement { get; set; } = MyobPayslipPlacements.Unknown;

    public List<MyobPayslipStoreDto> Stores { get; set; } = [];
}

public sealed class MyobPayslipListDto
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public List<MyobPayslipCompanyStatusDto> Companies { get; set; } = [];
    public List<MyobPayslipRowDto> Rows { get; set; } = [];

    // 合计只统计当前过滤结果。
    public int TotalCount { get; set; }
    public int EmployeeCount { get; set; }
    public decimal GrossTotal { get; set; }
    public decimal NetTotal { get; set; }

    /// <summary>
    /// 无法归到分店的工资单张数（未绑定 + 无法确定分店），不受分店 / 关键字过滤影响。
    /// 只有「全部分店」权限才会有值；仅「管辖分店」权限恒为 0（这些工资单对他们不可见）。
    /// </summary>
    public int UnassignedCount { get; set; }

    /// <summary>当前账号是否具备「全部分店」口径（前端据此决定是否显示「未绑定 / 无法确定」入口）。</summary>
    public bool CanViewAllStores { get; set; }

    /// <summary>数据读取时间（UTC，ISO 8601）；命中缓存时为首次读取时间。</summary>
    public string FetchedAt { get; set; } = string.Empty;
}

public sealed class MyobPayslipLineDto
{
    public string? Description { get; set; }

    /// <summary>MYOB 原始类别类型（Wage / Tax / Deduction / Superannuation / Entitlement / Expense…）。</summary>
    public string? Type { get; set; }

    /// <summary>MYOB 默认工资单 TYPE 列的显示文字（Wage → Wages，其余原样）。</summary>
    public string? TypeLabel { get; set; }

    public decimal? Hours { get; set; }
    public decimal? Rate { get; set; }
    public decimal? Amount { get; set; }
    public decimal? YearToDate { get; set; }
}

public sealed class MyobPayslipDetailDto
{
    public string ConnectionGuid { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string? CompanyAbn { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string? DisplayId { get; set; }
    public string? UserGuid { get; set; }
    public string PaymentDate { get; set; } = string.Empty;
    public string? PeriodStart { get; set; }
    public string? PeriodEnd { get; set; }
    public string? PayFrequency { get; set; }
    public decimal? GrossPay { get; set; }
    public decimal? NetPay { get; set; }
    public string? SuperFundName { get; set; }

    /// <summary>MYOB 默认工资单页头的 Hourly Rate（还原版式用，不进列表）。</summary>
    public decimal? HourlyRate { get; set; }

    /// <summary>MYOB 默认工资单页头的 Reference Number（报表里叫 ChequeNumber）。</summary>
    public string? ReferenceNumber { get; set; }

    public string Placement { get; set; } = MyobPayslipPlacements.Unknown;
    public List<MyobPayslipStoreDto> Stores { get; set; } = [];

    /// <summary>按 MYOB 原始顺序的扁平明细（与默认工资单通栏表一致，含 Type 列）；下面按类型分组的数组为兼容保留。</summary>
    public List<MyobPayslipLineDto> Lines { get; set; } = [];

    /// <summary>当前账号能否下载 PDF / 邮件发送（Myob.Payslip.Download / Myob.Payslip.Email）；前端据此显示按钮，接口里仍会重新判断。</summary>
    public bool CanDownloadPdf { get; set; }

    public bool CanSendEmail { get; set; }

    // 按 MYOB 原始 Type 分区块；名称与英文用词保持 MYOB 原文。
    public List<MyobPayslipLineDto> Wages { get; set; } = [];
    public List<MyobPayslipLineDto> Taxes { get; set; } = [];
    public List<MyobPayslipLineDto> Deductions { get; set; } = [];
    public List<MyobPayslipLineDto> Superannuation { get; set; } = [];

    /// <summary>假期累计等（Type = Entitlement）：Amount 是小时数。</summary>
    public List<MyobPayslipLineDto> Entitlements { get; set; } = [];

    /// <summary>Expense 与未知 Type 的行：探针只见过 Wage / Entitlement / Superannuation / Tax，其余类型兜底放这里。</summary>
    public List<MyobPayslipLineDto> Others { get; set; } = [];

    /// <summary>该员工同一支付日在本公司文件里是否还有其它周期的工资单（用于提示切换）。</summary>
    public int SamePaymentDateCount { get; set; }
}

public sealed class MyobPayslipDetailRequest
{
    public string ConnectionGuid { get; set; } = string.Empty;
    public string EmployeeUid { get; set; } = string.Empty;
    public string PaymentDate { get; set; } = string.Empty;

    /// <summary>同一支付日有多张工资单时用周期起始日区分（yyyy-MM-dd，可空）。</summary>
    public string? PeriodStart { get; set; }
}

public sealed class MyobPayslipQuery
{
    public string? ConnectionGuid { get; set; }
    public string? StoreCode { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    public string? Keyword { get; set; }

    /// <summary>只看无法归到分店的工资单（仅「全部分店」权限）。</summary>
    public bool OnlyUnassigned { get; set; }

    /// <summary>绕过 10 分钟缓存重新读 MYOB。</summary>
    public bool Refresh { get; set; }
}

/// <summary>某分店在这张工资单周期里的我方考勤计薪工时（仅供参考，与 MYOB 工资单里的小时数可能不同）。</summary>
public sealed class MyobPayslipStoreHoursDto
{
    public string StoreCode { get; set; } = string.Empty;
    public string StoreLabel { get; set; } = string.Empty;

    /// <summary>实际上班计薪工时（工资表口径，已扣用餐）。</summary>
    public decimal WorkHours { get; set; }

    /// <summary>带薪假工时：病假 + 年假 + 带薪公假。</summary>
    public decimal LeaveHours { get; set; }

    /// <summary>该分店读取失败（只标这一行，其它分店照常）。</summary>
    public bool Failed { get; set; }
}

public sealed class MyobPayslipHoursDto
{
    public string? PeriodStart { get; set; }
    public string? PeriodEnd { get; set; }
    public List<MyobPayslipStoreHoursDto> Stores { get; set; } = [];
    public decimal TotalWorkHours { get; set; }
    public decimal TotalLeaveHours { get; set; }
}

/// <summary>邮件发送弹窗打开时的预填信息。</summary>
public sealed class MyobPayslipEmailTargetDto
{
    /// <summary>员工档案邮箱；没有则为空。</summary>
    public string? Email { get; set; }

    public string? MaskedEmail { get; set; }

    /// <summary>"profile" = 来自员工档案；"none" = 档案里没有邮箱（收件人须手填，仅用于本次，不回写档案）。</summary>
    public string Source { get; set; } = "none";

    public string Subject { get; set; } = string.Empty;
    public string Filename { get; set; } = string.Empty;

    /// <summary>当前登录人的邮箱，供「抄送给我」；没有则为空。</summary>
    public string? MyEmail { get; set; }
}

public sealed class MyobPayslipEmailRequest
{
    public string ConnectionGuid { get; set; } = string.Empty;
    public string EmployeeUid { get; set; } = string.Empty;
    public string PaymentDate { get; set; } = string.Empty;
    public string? PeriodStart { get; set; }

    /// <summary>收件人（单个邮箱；每次只发一个员工、一个收件人）。</summary>
    public string To { get; set; } = string.Empty;

    public bool CopyToMe { get; set; }

    /// <summary>给员工的一句话（选填，≤500 字；会进邮件正文，提示用户不要写金额）。</summary>
    public string? Note { get; set; }

    /// <summary>档案邮箱存在且与 To 不同时，必须为 true（防止发错人）。</summary>
    public bool RecipientChangeConfirmed { get; set; }
}

public sealed class MyobPayslipEmailResultDto
{
    /// <summary>已脱敏的收件邮箱（a***@example.com）。</summary>
    public string SentTo { get; set; } = string.Empty;

    public bool CopiedToMe { get; set; }
}
