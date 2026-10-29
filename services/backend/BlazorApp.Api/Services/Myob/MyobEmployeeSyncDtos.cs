namespace BlazorApp.Api.Services.Myob;

// 「从 MYOB 同步资料到我方」（设计稿：预览 → 勾选 → 写入 → 可撤销）。全局 JSON 为 camelCase；全部返回 ApiResponse<T>。
// 只读 MYOB，只写我方员工资料；只对已绑定（EmployeeMyobLink 有效）且 MYOB 卡仍存在的员工生效。
//
// 路由与权限（类级 Myob.Employee.Bind；服务端另要求当前用户有 EmployeeProfiles.Edit，写员工资料用）：
//   POST /api/react/v1/myob/employee-sync/field-stats      Body MyobSyncScopeRequest   → MyobSyncFieldStatsDto   （第 1 步：每个字段「补空 N / 不同 M」）
//   POST /api/react/v1/myob/employee-sync/preview          Body MyobSyncPreviewRequest → MyobSyncPreviewDto       （第 2 步：逐员工逐字段的差异，不写入）
//   POST /api/react/v1/myob/employee-sync/apply            Body MyobSyncApplyRequest   → MyobSyncApplyResultDto   （确认写入，留快照）
//   GET  /api/react/v1/myob/employee-sync/batches?connectionGuid=                      → List<MyobSyncBatchDto>    （同步记录，最近 30 条）
//   POST /api/react/v1/myob/employee-sync/batches/{batchGuid}/undo                     → MyobSyncUndoResultDto     （7 天内整批撤销）

/// <summary>可同步的字段键。敏感字段（生日、银行、退休金）走「敏感资料直改」，默认不勾。</summary>
public static class MyobSyncFields
{
    public const string Name = "name";
    public const string Phone = "phone";
    public const string Email = "email";
    public const string Birthday = "birthday";
    public const string Gender = "gender";
    public const string Address = "address";
    public const string EmploymentType = "employmentType";
    public const string Bank = "bank";
    public const string Superannuation = "super";

    /// <summary>展示顺序（也是预览里每个员工内部的字段顺序）。</summary>
    public static readonly string[] All =
    [
        Name, Phone, Email, Birthday, Gender, Address, EmploymentType, Bank, Superannuation,
    ];

    public static bool IsSensitive(string field) =>
        field is Birthday or Bank or Superannuation;

    /// <summary>设计稿里默认勾选的字段：邮箱（绑定匹配依据）和三个敏感字段默认不勾。</summary>
    public static bool IsDefaultSelected(string field) =>
        field is Name or Phone or Gender or Address or EmploymentType;
}

public static class MyobSyncModes
{
    /// <summary>只补空：我方为空才写；有差异的只列出，默认不勾。</summary>
    public const string FillEmpty = "FillEmpty";

    /// <summary>以 MYOB 为准：有差异的也默认勾上。</summary>
    public const string Overwrite = "Overwrite";
}

public static class MyobSyncChangeKinds
{
    /// <summary>我方为空，补上 MYOB 的值。</summary>
    public const string Fill = "Fill";

    /// <summary>我方已有值且与 MYOB 不同，覆盖。</summary>
    public const string Overwrite = "Overwrite";
}

public static class MyobSyncErrorCodes
{
    public const string NoPermission = "MYOB_SYNC_NO_PERMISSION";
    public const string NoFields = "MYOB_SYNC_NO_FIELDS";
    public const string BatchNotFound = "MYOB_SYNC_BATCH_NOT_FOUND";
    public const string BatchAlreadyUndone = "MYOB_SYNC_BATCH_ALREADY_UNDONE";
    public const string BatchExpired = "MYOB_SYNC_BATCH_EXPIRED";

    // 逐员工失败原因（MyobSyncFailureDto.Code）
    public const string NotBound = "MYOB_SYNC_NOT_BOUND";
    public const string CardMissing = "MYOB_SYNC_CARD_MISSING";
    public const string ValueChanged = "MYOB_SYNC_VALUE_CHANGED";
    public const string VersionConflict = "MYOB_SYNC_VERSION_CONFLICT";
    public const string PendingRequestNeedsConfirm = "MYOB_SYNC_PENDING_NEEDS_CONFIRM";
    public const string WriteFailed = "MYOB_SYNC_WRITE_FAILED";
}

/// <summary>范围：UserGuids 为空 / null = 该公司文件下全部已绑定员工。</summary>
public sealed class MyobSyncScopeRequest
{
    public string ConnectionGuid { get; set; } = string.Empty;
    public List<string>? UserGuids { get; set; }

    /// <summary>true = 忽略 MYOB 缓存重新拉取。</summary>
    public bool Refresh { get; set; }
}

public sealed class MyobSyncFieldStatDto
{
    /// <summary><see cref="MyobSyncFields"/>。</summary>
    public string Field { get; set; } = string.Empty;

    public bool Sensitive { get; set; }

    /// <summary>设计稿默认勾选。</summary>
    public bool DefaultSelected { get; set; }

    /// <summary>MYOB 有值的员工数。</summary>
    public int MyobHasValue { get; set; }

    /// <summary>我方为空、MYOB 有值（可补空）的员工数。</summary>
    public int Fillable { get; set; }

    /// <summary>我方有值、MYOB 也有值且不同的员工数。</summary>
    public int Differs { get; set; }
}

public sealed class MyobSyncFieldStatsDto
{
    public string ConnectionGuid { get; set; } = string.Empty;

    /// <summary>MYOB 数据拉取时间（UTC）。</summary>
    public DateTime FetchedAt { get; set; }

    /// <summary>范围内已绑定的员工数（含 MYOB 卡已不存在的）。</summary>
    public int BoundCount { get; set; }

    /// <summary>MYOB 里已找不到卡、会被跳过的员工数。</summary>
    public int MissingCardCount { get; set; }

    /// <summary>按 <see cref="MyobSyncFields.All"/> 顺序，每个字段一项。</summary>
    public List<MyobSyncFieldStatDto> Fields { get; set; } = [];

    /// <summary>当前用户有没有 EmployeeProfiles.Edit；没有则前端不显示同步入口。</summary>
    public bool CanWrite { get; set; }

    /// <summary>MYOB 数据没拉到的字段键（如付款详情接口没权限 / 暂时不可用）；这些字段的统计和预览按「MYOB 无值」处理，前端提示即可。</summary>
    public List<string> UnavailableFields { get; set; } = [];
}

public sealed class MyobSyncPreviewRequest
{
    public string ConnectionGuid { get; set; } = string.Empty;
    public List<string>? UserGuids { get; set; }
    public bool Refresh { get; set; }

    /// <summary>要同步的字段（<see cref="MyobSyncFields"/>）。</summary>
    public List<string> Fields { get; set; } = [];

    /// <summary><see cref="MyobSyncModes"/>。</summary>
    public string Mode { get; set; } = MyobSyncModes.FillEmpty;
}

public sealed class MyobSyncChangeDto
{
    public string Field { get; set; } = string.Empty;
    public bool Sensitive { get; set; }

    /// <summary>我方现值的展示文本；为空时 null（前端显示「（空）」）。银行账号只给末 4 位。</summary>
    public string? OurDisplay { get; set; }

    /// <summary>MYOB 的值的展示文本。银行账号 / 退休金会员号只给末 4 位。</summary>
    public string? MyobDisplay { get; set; }

    /// <summary><see cref="MyobSyncChangeKinds"/>。</summary>
    public string Kind { get; set; } = MyobSyncChangeKinds.Fill;

    /// <summary>该模式下默认是否勾上：Fill 恒为 true；Overwrite 仅在 Mode=Overwrite 时为 true。</summary>
    public bool DefaultChecked { get; set; }

    /// <summary>给人看的补充说明，如「MYOB 挂了年假累计」。</summary>
    public string? Note { get; set; }

    /// <summary>我方现值的规范化哈希；写入时服务端比对，不一致说明预览后被人改过。</summary>
    public string OurHash { get; set; } = string.Empty;

    /// <summary>MYOB 值的规范化哈希；写入时比对，不一致说明 MYOB 预览后变了。</summary>
    public string MyobHash { get; set; } = string.Empty;
}

public sealed class MyobSyncEmployeeDto
{
    public string UserGuid { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public List<string> StoreLabels { get; set; } = [];

    /// <summary>FullTime / PartTime / Temporary；资料里没填为 null。</summary>
    public string? EmployeeType { get; set; }

    /// <summary>员工现在有待审的敏感资料申请；写入敏感字段会让它作废（需 ConfirmSupersedePendingSensitiveRequests）。</summary>
    public bool HasPendingSensitiveRequest { get; set; }

    /// <summary>预览时的敏感资料版本号；apply 时原样带回做并发校验。</summary>
    public int SensitiveRevision { get; set; }

    /// <summary>按 <see cref="MyobSyncFields.All"/> 顺序。</summary>
    public List<MyobSyncChangeDto> Changes { get; set; } = [];
}

public sealed class MyobSyncPreviewSummaryDto
{
    /// <summary>有变更的员工数。</summary>
    public int EmployeeCount { get; set; }

    /// <summary>变更总项数（所有字段变更之和）。</summary>
    public int ChangeCount { get; set; }

    /// <summary>默认勾上的项数。</summary>
    public int DefaultCheckedCount { get; set; }

    /// <summary>其中覆盖项（Kind=Overwrite）数。</summary>
    public int OverwriteCount { get; set; }

    /// <summary>其中敏感字段项数。</summary>
    public int SensitiveCount { get; set; }

    /// <summary>有待审敏感资料申请、且本次含敏感字段变更的员工数。</summary>
    public int PendingSensitiveRequestCount { get; set; }

    /// <summary>范围内无差异的员工数。</summary>
    public int SkippedNoDiff { get; set; }

    /// <summary>MYOB 卡已不存在的员工数。</summary>
    public int SkippedMissingCard { get; set; }
}

public sealed class MyobSyncPreviewDto
{
    public string ConnectionGuid { get; set; } = string.Empty;
    public DateTime FetchedAt { get; set; }
    public string Mode { get; set; } = MyobSyncModes.FillEmpty;
    public List<string> Fields { get; set; } = [];
    public MyobSyncPreviewSummaryDto Summary { get; set; } = new();

    /// <summary>同 <see cref="MyobSyncFieldStatsDto.UnavailableFields"/>。</summary>
    public List<string> UnavailableFields { get; set; } = [];

    /// <summary>只含有变更的员工，按姓名升序。</summary>
    public List<MyobSyncEmployeeDto> Employees { get; set; } = [];
}

public sealed class MyobSyncApplyRequest
{
    public string ConnectionGuid { get; set; } = string.Empty;
    public List<MyobSyncApplyEmployeeItem> Items { get; set; } = [];

    /// <summary>员工有待审敏感资料申请时，写入敏感字段会让申请作废；前端须让用户确认后传 true。</summary>
    public bool ConfirmSupersedePendingSensitiveRequests { get; set; }
}

public sealed class MyobSyncApplyEmployeeItem
{
    public string UserGuid { get; set; } = string.Empty;

    /// <summary>预览时拿到的 MyobSyncEmployeeDto.SensitiveRevision。</summary>
    public int ExpectedSensitiveRevision { get; set; }

    /// <summary>用户勾上的变更；只带字段键与哈希，值由服务端重新从 MYOB 取，不信任前端传值。</summary>
    public List<MyobSyncApplyChangeItem> Changes { get; set; } = [];
}

public sealed class MyobSyncApplyChangeItem
{
    public string Field { get; set; } = string.Empty;
    public string OurHash { get; set; } = string.Empty;
    public string MyobHash { get; set; } = string.Empty;
}

public sealed class MyobSyncWrittenDto
{
    public string UserGuid { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public List<string> Fields { get; set; } = [];
}

public sealed class MyobSyncFailureDto
{
    public string UserGuid { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;

    /// <summary>该员工没有写入的字段（整员工失败时为其请求里的全部字段）。</summary>
    public List<string> Fields { get; set; } = [];

    /// <summary><see cref="MyobSyncErrorCodes"/>。</summary>
    public string Code { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}

public sealed class MyobSyncApplyResultDto
{
    /// <summary>没有任何写入成功时为 null（不留批次）。</summary>
    public string? BatchGuid { get; set; }

    public int WrittenEmployeeCount { get; set; }
    public int WrittenChangeCount { get; set; }
    public int WrittenSensitiveCount { get; set; }
    public List<MyobSyncWrittenDto> Written { get; set; } = [];
    public List<MyobSyncFailureDto> Failed { get; set; } = [];

    /// <summary>撤销截止时间（UTC）= 写入时间 + 7 天。</summary>
    public DateTime? UndoDeadline { get; set; }
}

public static class MyobSyncBatchStatuses
{
    public const string Active = "Active";
    public const string Undone = "Undone";

    /// <summary>超过 7 天，不能再撤销。</summary>
    public const string Expired = "Expired";
}

public sealed class MyobSyncBatchDto
{
    public string BatchGuid { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? ActorName { get; set; }
    public int EmployeeCount { get; set; }
    public int ChangeCount { get; set; }
    public int SensitiveCount { get; set; }

    /// <summary><see cref="MyobSyncBatchStatuses"/>。</summary>
    public string Status { get; set; } = MyobSyncBatchStatuses.Active;

    public bool CanUndo { get; set; }
    public DateTime UndoDeadline { get; set; }
    public DateTime? UndoneAt { get; set; }
    public string? UndoneByName { get; set; }
}

public sealed class MyobSyncUndoResultDto
{
    public int RestoredEmployeeCount { get; set; }
    public int RestoredChangeCount { get; set; }

    /// <summary>写入之后又被人改过、因此没有还原的字段。</summary>
    public List<MyobSyncWrittenDto> SkippedChanged { get; set; } = [];
}
