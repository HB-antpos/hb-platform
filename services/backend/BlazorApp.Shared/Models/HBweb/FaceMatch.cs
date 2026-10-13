using SqlSugar;

namespace BlazorApp.Shared.Models;

// 门店人脸比对服务（docs/design/face-matching-service-20261012.md 第 13、14 节）的表映射。
// 表由迁移 20261014.001-face-match 创建；本文件的实体只用于主 API 后台读写，
// 刻意不进 SqlSugarContext 的自动建表清单（tableTypes），表结构以迁移为准。
// 人脸接收服务（BlazorApp.FaceIngest）用受限账号直连同名表，列名与含义是两边的契约，改动前必须同步。
// 时间一律 UTC；date 列用 DateTime（只取日期部分）。

/// <summary>FaceStoreProfile.Mode 取值（与接收端约定的数值）。</summary>
public static class FaceStoreMode
{
    public const byte Off = 0;
    public const byte Capture = 1;
    public const byte Match = 2;

    public static bool IsValid(int value) => value is Off or Capture or Match;
}

/// <summary>FaceEnrollment.Status 取值。</summary>
public static class FaceEnrollmentStatus
{
    public const byte Active = 1;
    public const byte Withdrawn = 2;
}

/// <summary>FaceUnwelcomeEntry.IncidentCategory 取值：没有「一般偷窃」这一项。</summary>
public static class FaceIncidentCategory
{
    public const byte ViolenceOrThreat = 1;
    public const byte OrganisedOrRepeatTheft = 2;
    public const byte Other = 3;

    public static bool IsValid(int value) => value is ViolenceOrThreat or OrganisedOrRepeatTheft or Other;
}

/// <summary>FaceUnwelcomeEntry.Status 取值。接收端只使用 Status=Active 的条目。</summary>
public static class FaceUnwelcomeEntryStatus
{
    public const byte Pending = 0;
    public const byte Active = 1;
    public const byte Expired = 2;
    public const byte Withdrawn = 3;
}

/// <summary>FaceMatchTask.ReviewStatus 取值。接收端新建任务时写 Pending。</summary>
public static class FaceMatchReviewStatus
{
    public const byte Pending = 0;
    public const byte Dismissed = 1;
    /// <summary>已确认：一个复核人判断「是同一个人」即结案（2026-10-13 用户决定；原设计要第二复核人确认，数值不变，库里旧数据照旧有效）。</summary>
    public const byte Confirmed = 2;
}

/// <summary>门店档案：每店一行，决定处理模式与各开关的前置条件记录。</summary>
[SugarTable("FaceStoreProfile")]
public sealed class FaceStoreProfile
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 32)]
    public string StoreCode { get; set; } = string.Empty;

    /// <summary>0 Off、1 Capture、2 Match，见 <see cref="FaceStoreMode"/>。</summary>
    [SugarColumn(IsNullable = false)]
    public byte Mode { get; set; }

    /// <summary>Capture 模式下评估匹配并记分数（阈值标定用）。</summary>
    [SugarColumn(IsNullable = false)]
    public bool EvaluateMatch { get; set; }

    [SugarColumn(IsNullable = false)]
    public bool StaffExclusionEnabled { get; set; }

    [SugarColumn(IsNullable = false)]
    public bool TrafficDedupEnabled { get; set; }

    [SugarColumn(IsNullable = false)]
    public bool UnwelcomeListEnabled { get; set; }

    [SugarColumn(IsNullable = true, Length = 200)]
    public string? AllowedSubjects { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? PiaReference { get; set; }

    [SugarColumn(IsNullable = true, ColumnDataType = "date")]
    public DateTime? SignageInstalledOn { get; set; }

    [SugarColumn(IsNullable = true, ColumnDataType = "date")]
    public DateTime? StaffNoticeDate { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? LegalSignoffBy { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? LegalSignoffAt { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? DedupLegalSignoffBy { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? DedupLegalSignoffAt { get; set; }

    /// <summary>实际处理地域（如 ap-guangzhou）；必须等于当前配置地域，地域改动后旧审查作废。</summary>
    [SugarColumn(IsNullable = true, Length = 32)]
    public string? CrossBorderRegion { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? CrossBorderReviewRef { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? CrossBorderReviewedBy { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? CrossBorderReviewedAt { get; set; }

    /// <summary>门店本地营业日所用的 IANA 时区；空则取门店表。</summary>
    [SugarColumn(IsNullable = true, Length = 64)]
    public string? TimeZoneId { get; set; }

    // 以下为不受欢迎人员名单启用闸门（设计 14.1 / 14.4 追加列）。
    [SugarColumn(IsNullable = true, Length = 100)]
    public string? UnwelcomeAssessmentRef { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? UnwelcomeLegalSignoffBy { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? UnwelcomeLegalSignoffAt { get; set; }

    [SugarColumn(IsNullable = true, ColumnDataType = "date")]
    public DateTime? StaffTrainingDate { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? PrivacyContact { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime UpdatedAtUtc { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? UpdatedBy { get; set; }
}

/// <summary>员工同意记录：同意书扫描件在私有桶独立前缀，撤回后记录保留（证明同意与撤回的时间线）。</summary>
[SugarTable("FaceConsentRecord")]
public sealed class FaceConsentRecord
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 32)]
    public string ConsentId { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 50)]
    public string UserGuid { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 32)]
    public string ConsentVersion { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public DateTime SignedAtUtc { get; set; }

    /// <summary>paper / electronic。</summary>
    [SugarColumn(IsNullable = false, Length = 16)]
    public string Method { get; set; } = string.Empty;

    /// <summary>同意书扫描件的对象键（纸质必填；电子签可空）。撤回时不删除，作为同意与撤回的证据。</summary>
    [SugarColumn(IsNullable = true, Length = 400)]
    public string? EvidenceObjectKey { get; set; }

    [SugarColumn(IsNullable = true, ColumnDataType = "date")]
    public DateTime? NswNoticeDate { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? WithdrawnAtUtc { get; set; }

    [SugarColumn(IsNullable = true, Length = 200)]
    public string? WithdrawnReason { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime CreatedAtUtc { get; set; }

    [SugarColumn(IsNullable = false, Length = 100)]
    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary>员工登记：每人同时只能有一条 Active（过滤唯一索引 UX_FaceEnrollment_UserGuid_Active）。</summary>
[SugarTable("FaceEnrollment")]
public sealed class FaceEnrollment
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 32)]
    public string EnrollmentId { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 50)]
    public string UserGuid { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 32)]
    public string ConsentId { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 64)]
    public string TencentPersonId { get; set; } = string.Empty;

    /// <summary>登记照在私有桶 face-match/enrollment/{EnrollmentId}/ 下的对象键（JSON 数组）；撤回后清空为 []。</summary>
    [SugarColumn(IsNullable = false, Length = 2000)]
    public string PhotoObjectKeysJson { get; set; } = "[]";

    /// <summary>1 Active、2 Withdrawn，见 <see cref="FaceEnrollmentStatus"/>。</summary>
    [SugarColumn(IsNullable = false)]
    public byte Status { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime EnrolledAtUtc { get; set; }

    [SugarColumn(IsNullable = false, Length = 100)]
    public string EnrolledBy { get; set; } = string.Empty;

    [SugarColumn(IsNullable = true)]
    public DateTime? WithdrawnAtUtc { get; set; }
}

/// <summary>当日访客库登记（接收端建、更新计数；主 API 负责到期删库）。</summary>
[SugarTable("FaceVisitGroup")]
public sealed class FaceVisitGroup
{
    /// <summary>腾讯云访客库编号，形如 v{门店}_{yyyyMMdd}。</summary>
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 64)]
    public string GroupId { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 32)]
    public string StoreCode { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, ColumnDataType = "date")]
    public DateTime BusinessDate { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime CreatedAtUtc { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime DeleteAfterUtc { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? DeletedAtUtc { get; set; }

    [SugarColumn(IsNullable = false)]
    public int VisitorCount { get; set; }
}

/// <summary>客流计数桶（每店每小时一行），只有计数，没有任何生物信息。</summary>
[SugarTable("FaceTrafficBucket")]
public sealed class FaceTrafficBucket
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 32)]
    public string StoreCode { get; set; } = string.Empty;

    /// <summary>整点（UTC）。</summary>
    [SugarColumn(IsPrimaryKey = true, IsNullable = false)]
    public DateTime BucketStartUtc { get; set; }

    [SugarColumn(IsNullable = false)]
    public int CaptureCount { get; set; }

    [SugarColumn(IsNullable = false)]
    public int StaffMatchCount { get; set; }

    /// <summary>当日访客库里新建的人数：估算值。</summary>
    [SugarColumn(IsNullable = false)]
    public int UniqueVisitorCount { get; set; }

    [SugarColumn(IsNullable = false)]
    public int RepeatCount { get; set; }

    [SugarColumn(IsNullable = false)]
    public int LowQualityCount { get; set; }

    [SugarColumn(IsNullable = false)]
    public int ApiFailureCount { get; set; }
}

/// <summary>阈值标定评估记录（仅 Capture 且 EvaluateMatch 的门店，接收端写入）。</summary>
[SugarTable("FaceMatchEvaluation")]
public sealed class FaceMatchEvaluation
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 32)]
    public string CaptureId { get; set; } = string.Empty;

    [SugarColumn(IsNullable = true, Length = 9, DecimalDigits = 4)]
    public decimal? StaffScore { get; set; }

    [SugarColumn(IsNullable = true, Length = 9, DecimalDigits = 4)]
    public decimal? VisitScore { get; set; }

    [SugarColumn(IsNullable = false)]
    public byte Decision { get; set; }
}

/// <summary>持久审计：每个写动作与每次查看一行；不含图片、对象键、密钥。</summary>
[SugarTable("FaceAuditLog")]
public sealed class FaceAuditLog
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long AuditId { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime AtUtc { get; set; }

    [SugarColumn(IsNullable = false, Length = 100)]
    public string ActorId { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 100)]
    public string ActorName { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 48)]
    public string Action { get; set; } = string.Empty;

    [SugarColumn(IsNullable = true, Length = 32)]
    public string? StoreCode { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? TargetRef { get; set; }

    [SugarColumn(IsNullable = false, Length = 32)]
    public string Result { get; set; } = string.Empty;
}

/// <summary>不受欢迎人员名单条目。Pending 时只有私有桶里的参考照片；批准后才会在腾讯云建人。</summary>
[SugarTable("FaceUnwelcomeEntry")]
public sealed class FaceUnwelcomeEntry
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 32)]
    public string EntryId { get; set; } = string.Empty;

    /// <summary>腾讯云 PersonId：提交时预先生成（uw_{EntryId}），批准时才真正建人。</summary>
    [SugarColumn(IsNullable = false, Length = 64)]
    public string TencentPersonId { get; set; } = string.Empty;

    /// <summary>1 ViolenceOrThreat、2 OrganisedOrRepeatTheft、3 Other，见 <see cref="FaceIncidentCategory"/>。</summary>
    [SugarColumn(IsNullable = false)]
    public byte IncidentCategory { get; set; }

    /// <summary>事件记录编号或警方报告编号，不放图片与个人隐私细节。</summary>
    [SugarColumn(IsNullable = false, Length = 100)]
    public string EvidenceRef { get; set; } = string.Empty;

    /// <summary>生效门店编码，英文逗号分隔、无空格（如 1013,2015）；应用层总是写具体值。</summary>
    [SugarColumn(IsNullable = false, Length = 500)]
    public string StoreScope { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 100)]
    public string SubmittedBy { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public DateTime SubmittedAtUtc { get; set; }

    /// <summary>批准人（Other 类别为第一批准人）；Pending 且无人批准时为空。</summary>
    [SugarColumn(IsNullable = true, Length = 100)]
    public string? ApprovedBy { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? ApprovedAtUtc { get; set; }

    /// <summary>到期前 30 天提醒复核的日期（到期日减 30 天，不早于提交时间）。</summary>
    [SugarColumn(IsNullable = false)]
    public DateTime ReviewDueAtUtc { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>0 Pending、1 Active、2 Expired、3 Withdrawn，见 <see cref="FaceUnwelcomeEntryStatus"/>。</summary>
    [SugarColumn(IsNullable = false)]
    public byte Status { get; set; }

    /// <summary>参考照片在私有桶 face-match/unwelcome/{EntryId}/ 下的对象键（JSON 数组）；条目失效即清空为 []。</summary>
    [SugarColumn(IsNullable = false, Length = 2000)]
    public string PhotoObjectKeysJson { get; set; } = "[]";

    [SugarColumn(IsNullable = true, Length = 500)]
    public string? DisputeNote { get; set; }

    // 以下三列是主 API 自己用的追加列（接收端不读）：Other 类别的额外说明与第二批准人。
    [SugarColumn(IsNullable = true, Length = 200)]
    public string? CategoryNote { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? SecondApprovedBy { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? SecondApprovedAtUtc { get; set; }
}

/// <summary>命中的复核任务（接收端写入）；员工命中不建任务。</summary>
[SugarTable("FaceMatchTask")]
public sealed class FaceMatchTask
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 32)]
    public string TaskId { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 32)]
    public string StoreCode { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 64)]
    public string CameraId { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public DateTime CapturedAtUtc { get; set; }

    [SugarColumn(IsNullable = false, Length = 32)]
    public string EntryId { get; set; } = string.Empty;

    /// <summary>腾讯云相似度（0–100），不是「同一个人的概率」。</summary>
    [SugarColumn(IsNullable = false, Length = 9, DecimalDigits = 4)]
    public decimal Score { get; set; }

    /// <summary>命中抓拍图的对象键（FaceCapture:Cos 桶内）；图片到期删除后置为空串。</summary>
    [SugarColumn(IsNullable = false, Length = 400)]
    public string ImageObjectKey { get; set; } = string.Empty;

    /// <summary>0 Pending、1 Dismissed、2 Confirmed，见 <see cref="FaceMatchReviewStatus"/>。</summary>
    [SugarColumn(IsNullable = false)]
    public byte ReviewStatus { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? FirstReviewerId { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? FirstReviewedAtUtc { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? SecondReviewerId { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? SecondReviewedAtUtc { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime ImageDeleteAtUtc { get; set; }

    /// <summary>主 API 后台任务发出命中提示后写入；为空表示还没发。</summary>
    [SugarColumn(IsNullable = true)]
    public DateTime? NotifiedAtUtc { get; set; }
}
