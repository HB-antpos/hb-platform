namespace BlazorApp.Api.Services.Myob;

// 阶段 1：员工绑定 + 发薪公司（方案 docs/plans/2026-10-27-myob-payroll-integration.md 第 6、7 节）。
// 全局 JSON 为 camelCase；全部返回 ApiResponse<T>。
//
// 路由与权限：
//   GET  /api/react/v1/myob/employee-bindings?connectionGuid=&refresh=       Myob.Employee.Bind  → MyobBindingSnapshotDto
//   GET  /api/react/v1/myob/employee-bindings/candidates?connectionGuid=&userGuid=&keyword=   → MyobBindingCandidatesDto
//   POST /api/react/v1/myob/employee-bindings/confirm      Body MyobConfirmBindingsRequest → MyobConfirmBindingsResult
//   POST /api/react/v1/myob/employee-bindings/{linkGuid}/unbind                              → 空
//   POST /api/react/v1/myob/employee-bindings/ignore       Body MyobIgnoreEmployeeRequest    → 空（忽略 / 取消忽略「MYOB 有、我方没有」的人）
//   GET  /api/react/v1/myob/companies                                     Myob.Connection.Manage / Myob.Employee.Bind / Attendance.Payroll.Edit 任一 → List<MyobPayrollCompanyOptionDto>（仅 Active 连接，给绑定 / 发薪公司页签的公司选择用）
//   GET  /api/react/v1/myob/payroll-employers?keyword=&storeCode=&crossCompanyOnly=          Attendance.Payroll.Edit → MyobPayrollEmployerListDto
//   GET  /api/react/v1/myob/payroll-employers/{userGuid}                                      Attendance.Payroll.Edit → MyobPayrollEmployerDetailDto
//   PUT  /api/react/v1/myob/payroll-employers/{userGuid}   Body MyobSavePayrollEmployersRequest → MyobPayrollEmployerDetailDto

public static class MyobBindingStatuses
{
    /// <summary>已绑定。</summary>
    public const string Bound = "Bound";

    /// <summary>高分建议（邮箱 + 生日都一致，且无冲突）：可批量确认。</summary>
    public const string Suggested = "Suggested";

    /// <summary>需人工确认（有候选但证据不足 / 有差异）。</summary>
    public const string NeedsReview = "NeedsReview";

    /// <summary>冲突：最佳候选卡已被别人绑定。</summary>
    public const string Conflict = "Conflict";

    /// <summary>没有相近候选。</summary>
    public const string Unmatched = "Unmatched";
}

/// <summary>单项匹配信号的结果。</summary>
public static class MyobSignalResults
{
    public const string Match = "match";
    public const string Mismatch = "mismatch";

    /// <summary>任一侧没有数据，无法比对。</summary>
    public const string Unknown = "unknown";
}

public static class MyobMatchMethods
{
    /// <summary>从高分建议一键确认。</summary>
    public const string Suggested = "Suggested";

    /// <summary>人工在抽屉里选卡。</summary>
    public const string Manual = "Manual";
}

public static class MyobTypeIssueCodes
{
    /// <summary>我方全职 / 兼职，但 MYOB 没挂年假 / 病假累计：假期余额拉不到。</summary>
    public const string MissingLeaveEntitlement = "MISSING_LEAVE_ENTITLEMENT";

    /// <summary>我方临时工，但 MYOB 挂了年假 / 病假累计。</summary>
    public const string TemporaryHasLeaveEntitlement = "TEMPORARY_HAS_LEAVE_ENTITLEMENT";
}

/// <summary>MYOB 员工卡（Contact/Employee + EmployeePayrollDetails 合并后的只读视图）。</summary>
public sealed class MyobEmployeeCardDto
{
    public string Uid { get; set; } = string.Empty;
    public string? DisplayId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }

    /// <summary>手机号只给末 4 位的掩码，如「•••• 4821」。</summary>
    public string? PhoneMasked { get; set; }

    /// <summary>MYOB 生日，格式 yyyy-MM-dd；没有为 null。</summary>
    public string? DateOfBirth { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>MYOB EmploymentStatus：FullTime / PartTime / Casual / Other。</summary>
    public string? EmploymentStatus { get; set; }

    /// <summary>已分配（IsAssigned）的年假类累计。</summary>
    public bool HasAnnualEntitlement { get; set; }

    /// <summary>已分配（IsAssigned）的病假类累计。</summary>
    public bool HasSickEntitlement { get; set; }
}

/// <summary>四项匹配信号：match / mismatch / unknown。</summary>
public sealed class MyobMatchBasisDto
{
    public string Email { get; set; } = MyobSignalResults.Unknown;
    public string Birthday { get; set; } = MyobSignalResults.Unknown;
    public string Name { get; set; } = MyobSignalResults.Unknown;
    public string Phone { get; set; } = MyobSignalResults.Unknown;
}

/// <summary>某个我方员工与某张 MYOB 卡的匹配评估。</summary>
public sealed class MyobMatchCandidateDto
{
    public MyobEmployeeCardDto Card { get; set; } = new();

    /// <summary>0–100。高分 ≥ 85 且邮箱、生日都 match；其余有候选的为中分。</summary>
    public int Score { get; set; }

    public MyobMatchBasisDto Basis { get; set; } = new();

    /// <summary>给人看的差异说明（中文），如「手机不一致，其余都对上」「姓名写法不同（Tom / Thomas）」「生日与我方不一致」。</summary>
    public List<string> Notes { get; set; } = [];

    /// <summary>这张卡已绑定给谁（绑定给别人时前端显示「不可选」）。</summary>
    public string? BoundToUserGuid { get; set; }

    public string? BoundToUserName { get; set; }
}

/// <summary>已生效的绑定。</summary>
public sealed class MyobLinkDto
{
    public string LinkGuid { get; set; } = string.Empty;
    public string MyobEmployeeUid { get; set; } = string.Empty;

    /// <summary>绑定时记下的 MYOB 姓名；MYOB 里已不存在这张卡时也能显示。</summary>
    public string? MyobName { get; set; }

    public string MatchMethod { get; set; } = MyobMatchMethods.Manual;
    public int? MatchScore { get; set; }
    public string? BoundByName { get; set; }
    public DateTime BoundAt { get; set; }

    /// <summary>MYOB 里找不到这张卡了（被删除 / 被停用）。</summary>
    public bool CardMissing { get; set; }
}

/// <summary>绑定列表的一行：一个我方员工。</summary>
public sealed class MyobBindingRowDto
{
    public string UserGuid { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string? Email { get; set; }

    /// <summary>FullTime / PartTime / Temporary；资料里没填为 null。</summary>
    public string? EmployeeType { get; set; }

    /// <summary>上班分店，如「1014 Kawana」。</summary>
    public List<string> StoreLabels { get; set; } = [];

    /// <summary>在别家公司的分店上班、由本公司发薪的员工（前端显示「跨公司」角标）。</summary>
    public bool IsCrossCompany { get; set; }

    /// <summary>Bound / Suggested / NeedsReview / Conflict / Unmatched。</summary>
    public string Status { get; set; } = MyobBindingStatuses.Unmatched;

    /// <summary>Status=Bound 时有值。</summary>
    public MyobLinkDto? Link { get; set; }

    /// <summary>未绑定时的最佳候选；Unmatched 为 null。已绑定时为 null。</summary>
    public MyobMatchCandidateDto? Candidate { get; set; }
}

/// <summary>「我方有、MYOB 没有」。</summary>
public sealed class MyobOnlyInOursDto
{
    public string UserGuid { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? EmployeeType { get; set; }
    public List<string> StoreLabels { get; set; } = [];
    public DateTime? JoinedAt { get; set; }

    /// <summary>「复制资料」要放进剪贴板的文本：姓名、邮箱、生日、手机（完整号码，仅有绑定权限的人拿得到）。</summary>
    public string CopyText { get; set; } = string.Empty;
}

/// <summary>「MYOB 有、我方没有」。</summary>
public sealed class MyobOnlyInMyobDto
{
    public MyobEmployeeCardDto Card { get; set; } = new();

    /// <summary>已被忽略（只在 includeIgnored 时返回；默认不返回）。</summary>
    public bool Ignored { get; set; }
}

/// <summary>用工类型不一致。</summary>
public sealed class MyobTypeIssueDto
{
    public string UserGuid { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public List<string> StoreLabels { get; set; } = [];
    public string OurType { get; set; } = string.Empty;

    /// <summary>MYOB 员工卡的 EmploymentStatus；取不到为 null。</summary>
    public string? MyobStatus { get; set; }

    /// <summary>MYOB 里已分配的年假累计 / 病假累计。</summary>
    public bool HasAnnualEntitlement { get; set; }

    public bool HasSickEntitlement { get; set; }

    /// <summary>MyobTypeIssueCodes。</summary>
    public string IssueCode { get; set; } = string.Empty;
}

public sealed class MyobBindingCountsDto
{
    public int All { get; set; }
    public int Bound { get; set; }
    public int Suggested { get; set; }

    /// <summary>NeedsReview + Conflict。</summary>
    public int NeedsReview { get; set; }

    public int Unmatched { get; set; }
    public int OnlyInOurs { get; set; }
    public int OnlyInMyob { get; set; }
    public int TypeIssues { get; set; }

    /// <summary>可批量确认的高分建议条数（= Suggested）。</summary>
    public int BatchConfirmable { get; set; }
}

/// <summary>GET employee-bindings 的响应：某个公司文件的完整绑定视图（一次返回，前端分页 / 筛选）。</summary>
public sealed class MyobBindingSnapshotDto
{
    public string ConnectionGuid { get; set; } = string.Empty;
    public string ConnectionName { get; set; } = string.Empty;

    /// <summary>MYOB 员工列表最近一次拉取时间（UTC）。</summary>
    public DateTime FetchedAt { get; set; }

    public MyobBindingCountsDto Counts { get; set; } = new();

    /// <summary>已按「高分建议 → 需确认 / 冲突 → 未匹配 → 已绑定」排序。</summary>
    public List<MyobBindingRowDto> Rows { get; set; } = [];

    public List<MyobOnlyInOursDto> OnlyInOurs { get; set; } = [];
    public List<MyobOnlyInMyobDto> OnlyInMyob { get; set; } = [];
    public List<MyobTypeIssueDto> TypeIssues { get; set; } = [];
}

/// <summary>GET employee-bindings/candidates 的响应：为某员工手动选卡。</summary>
public sealed class MyobBindingCandidatesDto
{
    public string UserGuid { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? PhoneMasked { get; set; }
    public string? Birthday { get; set; }
    public string? EmployeeType { get; set; }
    public List<string> StoreLabels { get; set; } = [];

    /// <summary>按得分降序，最多 30 条；keyword 命中姓名 / 邮箱 / 手机末 4 位。</summary>
    public List<MyobMatchCandidateDto> Candidates { get; set; } = [];
}

public sealed class MyobConfirmBindingsRequest
{
    public string ConnectionGuid { get; set; } = string.Empty;
    public List<MyobConfirmBindingItem> Items { get; set; } = [];
}

public sealed class MyobConfirmBindingItem
{
    public string UserGuid { get; set; } = string.Empty;
    public string MyobEmployeeUid { get; set; } = string.Empty;

    /// <summary>true = 人工在抽屉里选的；false = 确认高分建议（服务端会重新打分，不是高分则拒绝该条）。</summary>
    public bool Manual { get; set; }
}

public sealed class MyobConfirmBindingsResult
{
    public int Confirmed { get; set; }
    public List<MyobConfirmFailure> Failed { get; set; } = [];
}

public sealed class MyobConfirmFailure
{
    public string UserGuid { get; set; } = string.Empty;

    /// <summary>MyobBindErrorCodes。</summary>
    public string Code { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}

public static class MyobBindErrorCodes
{
    public const string UserNotFound = "MYOB_BIND_USER_NOT_FOUND";
    public const string CardNotFound = "MYOB_BIND_CARD_NOT_FOUND";
    public const string CardAlreadyBound = "MYOB_BIND_CARD_ALREADY_BOUND";
    public const string UserAlreadyBound = "MYOB_BIND_USER_ALREADY_BOUND";
    public const string NotHighScore = "MYOB_BIND_NOT_HIGH_SCORE";
    public const string LinkNotFound = "MYOB_LINK_NOT_FOUND";
}

public sealed class MyobIgnoreEmployeeRequest
{
    public string ConnectionGuid { get; set; } = string.Empty;
    public string MyobEmployeeUid { get; set; } = string.Empty;

    /// <summary>true 忽略；false 取消忽略。</summary>
    public bool Ignored { get; set; } = true;
}

// ---------------- 发薪公司 ----------------

/// <summary>列表一行：员工 × 上班分店。</summary>
public sealed class MyobPayrollEmployerRowDto
{
    public string UserGuid { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;

    /// <summary>该分店所属公司（MyobStoreMapping）；分店还没映射为 null。</summary>
    public string? DefaultConnectionGuid { get; set; }

    public string? DefaultConnectionName { get; set; }

    /// <summary>今天实际生效的发薪公司（有覆盖用覆盖，否则默认）。</summary>
    public string? EffectiveConnectionGuid { get; set; }

    public string? EffectiveConnectionName { get; set; }

    /// <summary>是否由「改由其他公司发」覆盖。</summary>
    public bool IsOverride { get; set; }

    /// <summary>覆盖的生效日期 yyyy-MM-dd；非覆盖为 null。</summary>
    public string? EffectiveFrom { get; set; }

    /// <summary>该员工的上班分店分属不同发薪公司（前端显示「跨公司」角标）。</summary>
    public bool IsCrossCompany { get; set; }
}

public sealed class MyobPayrollEmployerListDto
{
    public List<MyobPayrollEmployerRowDto> Rows { get; set; } = [];

    /// <summary>可选的公司文件（Active 连接）。</summary>
    public List<MyobPayrollCompanyOptionDto> Companies { get; set; } = [];
}

public sealed class MyobPayrollCompanyOptionDto
{
    public string ConnectionGuid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>公司 ABN（只含数字）；没有为 null。</summary>
    public string? Abn { get; set; }
}

/// <summary>一个员工的发薪公司详情（抽屉）。</summary>
public sealed class MyobPayrollEmployerDetailDto
{
    public string UserGuid { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;

    /// <summary>员工每个上班分店一行；Rows[].EffectiveFrom 为该分店当前覆盖的生效日。</summary>
    public List<MyobPayrollEmployerRowDto> Rows { get; set; } = [];

    public List<MyobPayrollCompanyOptionDto> Companies { get; set; } = [];

    /// <summary>各公司文件里这个员工是否已绑定（新发薪公司没绑定时前端提示「去绑定」）。</summary>
    public List<MyobPayrollBindingStateDto> BindingStates { get; set; } = [];

    /// <summary>可选的生效日期：当前期及之后几期的起始日（yyyy-MM-dd），升序；第一项为当前期。</summary>
    public List<string> AllowedEffectiveFrom { get; set; } = [];

    /// <summary>变更历史，最新在前。</summary>
    public List<MyobPayrollEmployerHistoryDto> History { get; set; } = [];
}

public sealed class MyobPayrollBindingStateDto
{
    public string ConnectionGuid { get; set; } = string.Empty;
    public bool Bound { get; set; }
}

public sealed class MyobPayrollEmployerHistoryDto
{
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;

    /// <summary>覆盖到的公司；null 表示「改回按分店」。</summary>
    public string? ConnectionGuid { get; set; }

    public string? ConnectionName { get; set; }
    public string EffectiveFrom { get; set; } = string.Empty;
    public string? CreatedByName { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class MyobSavePayrollEmployersRequest
{
    /// <summary>必须是工资周期起始日，且不早于当前期。</summary>
    public string EffectiveFrom { get; set; } = string.Empty;

    public List<MyobSavePayrollEmployerItem> Items { get; set; } = [];
}

public sealed class MyobSavePayrollEmployerItem
{
    public string StoreCode { get; set; } = string.Empty;

    /// <summary>null = 按分店所属公司（默认）。</summary>
    public string? ConnectionGuid { get; set; }
}

public static class MyobPayrollErrorCodes
{
    public const string InvalidEffectiveFrom = "MYOB_PAYROLL_INVALID_EFFECTIVE_FROM";
    public const string StoreNotWorked = "MYOB_PAYROLL_STORE_NOT_WORKED";
    public const string ConnectionNotActive = "MYOB_PAYROLL_CONNECTION_NOT_ACTIVE";
    public const string Forbidden = "MYOB_PAYROLL_FORBIDDEN";
}
