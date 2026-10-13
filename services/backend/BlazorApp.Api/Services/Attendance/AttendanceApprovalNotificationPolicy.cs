namespace BlazorApp.Api.Services.Attendance;

/// <summary>
/// 考勤审批推送的口径：哪些来源类型要推、幂等键怎么拼、英文文案怎么写。
/// 推送范围只认「员工自己发起」的三类审批；Punch / Overtime / MissingClockOut 是系统按打卡和排班派生出来的待审记录，
/// 数量多、随打卡反复生成与取消，一律不推。以后要加类型，只改 <see cref="ShouldNotify"/> 与文案分支。
/// 文案一律英文，且不含原因、备注、审批意见等个人信息（这些只在详情里由客户端凭登录态另行拉取）。
/// </summary>
public static class AttendanceApprovalNotificationPolicy
{
    public const string SourceTypeLeave = "Leave";
    public const string SourceTypePunchAdjustment = "PunchAdjustment";
    public const string SourceTypeMealBreak = "MealBreak";

    /// <summary>该审批来源是否需要推送（提交 / 结果 / 失效撤回共用同一口径）。</summary>
    public static bool ShouldNotify(string? sourceType) =>
        sourceType is SourceTypeLeave or SourceTypePunchAdjustment or SourceTypeMealBreak;

    /// <summary>提交通知的幂等键。带审批 Id：终态后重新提交会生成新审批，不会撞上已撤回的旧键。</summary>
    public static string SubmittedDedupeKey(string approvalGuid) => $"approval:submitted:{approvalGuid}";

    /// <summary>结果通知的幂等键；重复审批 / 重试不会重复推给申请人。</summary>
    public static string DecidedDedupeKey(string approvalGuid) => $"approval:decided:{approvalGuid}";

    public static string SubmittedTitle(string sourceType) => sourceType switch
    {
        SourceTypeLeave => "Leave request awaiting approval",
        SourceTypePunchAdjustment => "Punch correction awaiting approval",
        SourceTypeMealBreak => "Meal break report awaiting approval",
        _ => throw UnsupportedSourceType(sourceType),
    };

    /// <param name="sourceType">审批来源类型，决定句子里的名词（leave request / punch correction / meal break report）。</param>
    /// <param name="employeeName">申请人姓名；缺失时由调用方传入 "An employee"。</param>
    /// <param name="dateText">yyyy-MM-dd 或 "yyyy-MM-dd to yyyy-MM-dd"；取不到时为空，句子里省略日期。</param>
    public static string SubmittedBody(string sourceType, string employeeName, string? dateText) =>
        $"{employeeName} submitted a {Noun(sourceType)}{ForDate(dateText)}.";

    public static string DecidedTitle(string sourceType, bool approved) =>
        $"Your {Noun(sourceType)} was {Verdict(approved)}";

    public static string DecidedBody(string sourceType, bool approved, string? dateText) =>
        $"Your {Noun(sourceType)}{ForDate(dateText)} has been {Verdict(approved)}.";

    private static string Noun(string sourceType) => sourceType switch
    {
        SourceTypeLeave => "leave request",
        SourceTypePunchAdjustment => "punch correction",
        SourceTypeMealBreak => "meal break report",
        _ => throw UnsupportedSourceType(sourceType),
    };

    private static string Verdict(bool approved) => approved ? "approved" : "rejected";

    private static string ForDate(string? dateText) =>
        string.IsNullOrWhiteSpace(dateText) ? string.Empty : $" for {dateText}";

    private static ArgumentOutOfRangeException UnsupportedSourceType(string sourceType) =>
        new(nameof(sourceType), sourceType, "该审批类型不推送通知。");
}
