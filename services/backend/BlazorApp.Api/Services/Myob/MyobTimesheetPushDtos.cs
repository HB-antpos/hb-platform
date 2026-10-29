namespace BlazorApp.Api.Services.Myob;

// 阶段 2 第二步：Timesheet 推送与撤销（方案 docs/plans/2026-10-29-myob-timesheet-push.md 第 4 节）。
// 会往 MYOB 写生产数据：总闸 Myob__TimesheetPushEnabled 默认关闭；新权限码 Myob.Timesheet.Push 默认不授予任何角色。
// 全局 JSON 为 camelCase；全部返回 ApiResponse<T>；日期一律 yyyy-MM-dd 字符串；响应头 Cache-Control: no-store。
//
// 路由与权限（均在 api/react/v1/myob/timesheet 下，权限 Myob.Timesheet.Push）：
//   POST   push                                  Body MyobPushRequestDto   → MyobPushResultDto（≤10 人一块，同一个 batchGuid 分块调用）
//   GET    batches?connectionGuid=&periodStart=                             → List<MyobPushBatchSummaryDto>
//   GET    batches/{batchGuid}                                              → MyobPushBatchDetailDto
//   POST   batches/{batchGuid}/undo                                         → MyobUndoResultDto
// 预览（GET preview，权限仍是 Attendance.Payroll.Edit）的每一行多一个 push 字段（MyobPreviewPushStateDto）。
//
// 实测结论（2026-10-30，Robinson Road 的 test test 卡）：PUT 是整体覆盖该员工该日期区间；日期写 yyyy-MM-dd；小时保留 3 位小数；
// 区间外日期 400（DateOutOfRange 221）；DELETE 405 不可用；空 Lines 可清空——撤销就是把推送前的快照 PUT 回去（原来没有则 PUT 空 Lines）。

/// <summary>推送记录状态（MyobTimesheetPush.Status）与预览里的「推送状态」。</summary>
public static class MyobPushStatuses
{
    /// <summary>正在写（插入记录到写完之间）。同一 (公司文件, 周期) 同一时刻最多一行 Running。</summary>
    public const string Running = "Running";

    /// <summary>已写入并回读校验一致。</summary>
    public const string Pushed = "Pushed";

    /// <summary>MYOB 里已经是我们要写的内容，没有发 PUT。</summary>
    public const string UpToDate = "UpToDate";

    /// <summary>MYOB 里该周期已有已发薪（Processed）的记录，没有发 PUT。</summary>
    public const string SkippedProcessed = "SkippedProcessed";

    /// <summary>MYOB 里已有未发薪的记录且没有勾选「覆盖」，没有发 PUT。</summary>
    public const string SkippedHasEntries = "SkippedHasEntries";

    /// <summary>被服务端重新校验的拦截项挡住（只在返回里出现，不落库）。</summary>
    public const string Blocked = "Blocked";

    /// <summary>写失败（网络 / 5xx / 4xx）；只记状态码与错误码。</summary>
    public const string Failed = "Failed";

    /// <summary>PUT 成功但回读与目标不一致（或回读失败）；不自动再写，须人工在 MYOB 核对。</summary>
    public const string Mismatch = "Mismatch";

    /// <summary>已撤销（MYOB 已恢复为推送前的内容）。</summary>
    public const string Undone = "Undone";

    /// <summary>撤销时发现 MYOB 里的内容已被人手改过，没有覆盖。</summary>
    public const string UndoConflict = "UndoConflict";

    /// <summary>该员工在 MYOB 里已发薪，撤销被拒绝，不改动。</summary>
    public const string UndoBlockedProcessed = "UndoBlockedProcessed";

    /// <summary>该员工在该周期有更新的批次，撤销本批会覆盖掉后来的重推，被拒绝。</summary>
    public const string UndoBlockedNewer = "UndoBlockedNewer";

    /// <summary>可能写过 MYOB 的状态（含结果不确定的 Mismatch）：撤销只处理这些。UpToDate 没有改动 MYOB，不在其中。</summary>
    public static readonly string[] WroteToMyob = [Pushed, Mismatch];
}

public static class MyobPushErrorCodes
{
    /// <summary>总闸关闭，或该公司文件不在灰度白名单里。</summary>
    public const string Disabled = "MYOB_PUSH_DISABLED";

    public const string InvalidRequest = "MYOB_PUSH_INVALID_REQUEST";

    /// <summary>周期还没结束，不推未结束的周期。</summary>
    public const string PeriodNotEnded = "MYOB_PUSH_PERIOD_NOT_ENDED";

    /// <summary>同一 (公司文件, 周期) 已有一个推送在跑。</summary>
    public const string InProgress = "MYOB_PUSH_IN_PROGRESS";

    public const string BatchNotFound = "MYOB_PUSH_BATCH_NOT_FOUND";

    /// <summary>预览里没有这个员工，或状态不是 Ready。</summary>
    public const string NotReady = "MYOB_PUSH_NOT_READY";

    /// <summary>员工命中的提示项没有全部勾选「已知晓」。</summary>
    public const string HintNotAcknowledged = "MYOB_PUSH_HINT_NOT_ACKNOWLEDGED";

    /// <summary>读 MYOB 访问令牌失败（连接需要重新授权等），本块全部停止。</summary>
    public const string AuthFailed = "MYOB_PUSH_AUTH_FAILED";

    /// <summary>落库失败：写不进记录就不推。</summary>
    public const string RecordFailed = "MYOB_PUSH_RECORD_FAILED";

    /// <summary>读 MYOB 现状失败。</summary>
    public const string ReadFailed = "MYOB_PUSH_READ_FAILED";

    /// <summary>PUT 之后回读失败，无法确认写入结果。</summary>
    public const string ReadbackFailed = "MYOB_PUSH_READBACK_FAILED";

    /// <summary>PUT 之后回读与目标内容不一致。</summary>
    public const string ReadbackMismatch = "MYOB_PUSH_READBACK_MISMATCH";

    /// <summary>上一次推送遗留的 Running 记录超过 10 分钟：中断时 MYOB 里的状态不确定，记成 Mismatch 并带这个码，提醒人工核对 / 撤销。</summary>
    public const string Stale = "MYOB_PUSH_STALE";

    public const string NothingToUndo = "MYOB_PUSH_NOTHING_TO_UNDO";
}

public sealed class MyobPushRequestDto
{
    public string ConnectionGuid { get; set; } = string.Empty;

    /// <summary>工资周期起始日 yyyy-MM-dd，必须是某个周期的起始日。</summary>
    public string PeriodStart { get; set; } = string.Empty;

    /// <summary>本块要推送的员工，1–10 人；客户端只传「谁」，不传任何工时数字。</summary>
    public List<string> UserGuids { get; set; } = [];

    /// <summary>省略 = 新批次；前端分块调用时第二块起带上第一块返回的批次号。</summary>
    public string? BatchGuid { get; set; }

    /// <summary>
    /// 用户在确认弹窗里勾选「覆盖」的员工（预览里 MYOB 已有未发薪记录的那些人）。**按人确认**：不在名单里的员工，
    /// 即使推送时 MYOB 里新出现了记录也不会被覆盖，而是记 SkippedHasEntries。
    /// </summary>
    public List<string> OverwriteUserGuids { get; set; } = [];

    /// <summary>已知晓的提示项（MyobHintCodes）；员工命中的提示必须全部被覆盖。</summary>
    public List<string> AcknowledgedHintCodes { get; set; } = [];
}

public sealed class MyobPushUserResultDto
{
    public string UserGuid { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;

    /// <summary><see cref="MyobPushStatuses"/>。</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Blocked / Failed / Mismatch / Skipped 时的原因码（<see cref="MyobPushErrorCodes"/>、拦截码或 MYOB 错误类型名）。</summary>
    public string? Code { get; set; }

    /// <summary>给人看的说明，不含姓名以外的个人信息，也不含 MYOB 响应体。</summary>
    public string? Message { get; set; }

    /// <summary>规划里要推送的合计小时。</summary>
    public decimal PushedHours { get; set; }
}

public sealed class MyobPushResultDto
{
    public string BatchGuid { get; set; } = string.Empty;
    public string ConnectionGuid { get; set; } = string.Empty;
    public string PeriodStart { get; set; } = string.Empty;
    public List<MyobPushUserResultDto> Results { get; set; } = [];
}

public sealed class MyobPushBatchSummaryDto
{
    public string BatchGuid { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public string? ActorUserGuid { get; set; }
    public string? ActorName { get; set; }

    public int Total { get; set; }
    public int Pushed { get; set; }
    public int UpToDate { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public int Mismatch { get; set; }
    public int Undone { get; set; }

    /// <summary>本批真正写过 MYOB、且还没撤销的员工数（可撤销的上限）。</summary>
    public int Undoable { get; set; }
}

public sealed class MyobPushBatchItemDto
{
    public string UserGuid { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? ErrorCode { get; set; }
    public int? ErrorStatus { get; set; }
    public decimal PushedHours { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public DateTime? UndoneAt { get; set; }
}

public sealed class MyobPushBatchDetailDto
{
    public MyobPushBatchSummaryDto Summary { get; set; } = new();
    public string ConnectionGuid { get; set; } = string.Empty;
    public string PeriodStart { get; set; } = string.Empty;
    public List<MyobPushBatchItemDto> Items { get; set; } = [];
}

public sealed class MyobUndoResultDto
{
    public string BatchGuid { get; set; } = string.Empty;
    public List<MyobPushUserResultDto> Results { get; set; } = [];
}

/// <summary>预览里的「MYOB 状态」：未推送 / 已推送 / 需重新推送，以及最近一次的结果。</summary>
public static class MyobPushStates
{
    public const string NotPushed = "NotPushed";
    public const string Pushed = "Pushed";

    /// <summary>有成功记录，但规划内容变了（工时 / 时薪拆分 / 映射），或周期已被解锁：MYOB 里还留着旧工时。</summary>
    public const string NeedsRepush = "NeedsRepush";

    public const string ProcessedSkipped = "ProcessedSkipped";
    public const string Failed = "Failed";
    public const string Mismatch = "Mismatch";
}

public sealed class MyobPreviewPushStateDto
{
    /// <summary><see cref="MyobPushStates"/>。</summary>
    public string State { get; set; } = MyobPushStates.NotPushed;

    public bool NeedsRepush { get; set; }

    /// <summary>最近一次记录的状态（<see cref="MyobPushStatuses"/>）；没有记录为 null。</summary>
    public string? LastStatus { get; set; }

    public string? LastBatchGuid { get; set; }
    public DateTime? LastPushedAt { get; set; }
}
