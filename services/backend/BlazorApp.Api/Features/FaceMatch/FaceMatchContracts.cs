namespace BlazorApp.Api.Features.FaceMatch;

// 门店人脸比对服务（管理端）的接口契约：/api/react/v1/face-match/...
// 设计依据 docs/design/face-matching-service-20261012.md 第 13.4、14.5 节。
// 约定：所有 *Utc 字段为带 Z 的 UTC ISO 字符串；日期（date）字段为 yyyy-MM-dd；枚举同时给数值与小驼峰名称；
// 响应不含任何对象键、COS 地址、腾讯云凭据、PersonId 或 FaceId；图片只经专用内容接口由服务端转发字节。

/// <summary>操作人：只用于审计与「提交人 ≠ 批准人 / 复核人」判断。IsAdmin 决定是否不受门店范围限制。</summary>
public sealed record FaceMatchActor(string Id, string Name, bool IsAdmin = false);

/// <summary>业务错误：控制器转成 HTTP 状态码与 ApiResponse 错误码；消息固定构造，不含图片、对象键、密钥。</summary>
public sealed class FaceMatchException(int statusCode, string code, string message, object? details = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;

    /// <summary>放进 ApiResponse.details（例如闸门缺项列表）；不得含敏感内容。</summary>
    public object? Details { get; } = details;
}

/// <summary>分页结果。</summary>
public sealed record FaceMatchPageDto<T>
{
    public IReadOnlyList<T> Items { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; }
    public bool HasMore { get; init; }
}

// ---------- 功能开关与权限 ----------

public sealed record FaceMatchPermissionFlagsDto
{
    public bool Manage { get; init; }
    public bool TrafficView { get; init; }
    public bool UnwelcomeManage { get; init; }
    public bool UnwelcomeApprove { get; init; }
    public bool UnwelcomeReview { get; init; }

    /// <summary>可看「抓拍与比对结果」页（持有 FaceCapture.Images.View：结果里的照片是生物识别信息）。</summary>
    public bool ResultsView { get; init; }
}

/// <summary>
/// GET /config：页面据此决定是否显示「功能未开启」提示与各页签。
/// DisabledReason 取值：region-missing / region-not-allowed / credentials-missing / credentials-invalid（Enabled=false 时才有）。
/// </summary>
public sealed record FaceMatchConfigDto
{
    public bool Enabled { get; init; }
    public string? DisabledReason { get; init; }

    /// <summary>当前配置的处理地域（仅 Enabled 时返回；地域改动后所有门店的跨境审查记录作废）。</summary>
    public string? Region { get; init; }

    public IReadOnlyList<string> AllowedRegions { get; init; } = [];

    /// <summary>登记照 / 同意书 / 参考照片所用的私有桶是否已配置（FaceCapture:Cos）；未配置时上传与查看类接口返回 503。</summary>
    public bool ObjectStoreConfigured { get; init; }

    /// <summary>「较高」档位的相似度下限（0–100），配置 FaceMatch:Unwelcome:HighBandScore。</summary>
    public decimal HighBandScore { get; init; }

    public int CaptureRetentionDays { get; init; }
    public FaceMatchPermissionFlagsDto Permissions { get; init; } = new();
}

// ---------- 门店档案与闸门 ----------

/// <summary>
/// 闸门单项（facet 取值 capture / match / staffExclusion / trafficDedup / unwelcome）。
/// Requested=false 表示该项当前没有被请求启用，Missing 与 Warnings 都为空。
/// <para>
/// Missing 是<b>仍然阻塞保存</b>的结构性 / 功能性前提（缺了 PUT 返回 409 FACE_MATCH_GATE_NOT_MET）：
/// allowedSubjects（Capture 必须声明只拍知情的内部人员）、evaluateMatchRequiresCapture、
/// modeOff（开关要求模式不是 Off）、activeEnrollment（门店成员中没有有效的同意与登记）。
/// </para>
/// <para>
/// Warnings 是<b>不阻塞保存但必须始终可见</b>的合规记录缺口（2026-10-13 用户决定：Match 的全部合规闸门改为警告，风险由用户承担）：
/// 字段键（小驼峰，与请求体字段同名，如 piaReference / signageInstalledOn / legalSignoffBy / crossBorderReviewRef …）
/// 或特殊键 regionMismatch（CrossBorderRegion 不等于当前配置地域）、staffNoticeTooRecent（NSW 门店员工通知日期不满 14 天）。
/// 只在 Mode=Match 的 match / trafficDedup / unwelcome 三个 facet 里出现；Capture 下这些记录本来就不要求。
/// 同一个字段被多个 facet 要求时在各自 facet 里分别列出。
/// </para>
/// </summary>
public sealed record FaceGateFacetDto
{
    public string Facet { get; init; } = string.Empty;
    public bool Requested { get; init; }

    /// <summary>仍然阻塞保存的缺项（见类型注释）。</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];

    /// <summary>不阻塞保存的合规记录缺口（见类型注释）；老客户端不认识这个字段时忽略即可。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record FaceGateReportDto
{
    public IReadOnlyList<FaceGateFacetDto> Facets { get; init; } = [];

    /// <summary>
    /// 所有被请求的项都没有「阻塞缺项」（只看各 facet 的 Missing，不看 Warnings）。
    /// PUT 在 CanSave=false 时返回 409 FACE_MATCH_GATE_NOT_MET（details 即本对象）。
    /// </summary>
    public bool CanSave { get; init; }

    /// <summary>任一被请求的 facet 有合规记录警告。可以保存，但页面须持续显示红色提示，保存时审计会记录警告数。</summary>
    public bool HasWarnings { get; init; }
}

public sealed record FaceStoreProfileDto
{
    public string StoreCode { get; init; } = string.Empty;
    public string? StoreName { get; init; }

    /// <summary>档案行是否已存在；不存在时其余字段是默认值（Off、全关）。</summary>
    public bool Exists { get; init; }

    /// <summary>0 Off、1 Capture、2 Match。</summary>
    public int Mode { get; init; }

    public bool EvaluateMatch { get; init; }
    public bool StaffExclusionEnabled { get; init; }
    public bool TrafficDedupEnabled { get; init; }
    public bool UnwelcomeListEnabled { get; init; }

    /// <summary>名单命中自动确认阈值（生效值）：0 = 不自动确认；没有档案行或未设置时为默认 90。</summary>
    public decimal UnwelcomeAutoConfirmScore { get; init; } = BlazorApp.Shared.Models.FaceStoreProfile.DefaultUnwelcomeAutoConfirmScore;

    public string? AllowedSubjects { get; init; }
    public string? PiaReference { get; init; }
    public string? SignageInstalledOn { get; init; }
    public string? StaffNoticeDate { get; init; }
    public string? LegalSignoffBy { get; init; }
    public DateTime? LegalSignoffAt { get; init; }
    public string? DedupLegalSignoffBy { get; init; }
    public DateTime? DedupLegalSignoffAt { get; init; }
    public string? CrossBorderRegion { get; init; }
    public string? CrossBorderReviewRef { get; init; }
    public string? CrossBorderReviewedBy { get; init; }
    public DateTime? CrossBorderReviewedAt { get; init; }
    public string? TimeZoneId { get; init; }
    public string? UnwelcomeAssessmentRef { get; init; }
    public string? UnwelcomeLegalSignoffBy { get; init; }
    public DateTime? UnwelcomeLegalSignoffAt { get; init; }
    public string? StaffTrainingDate { get; init; }
    public string? PrivacyContact { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }
    public string? UpdatedBy { get; init; }

    /// <summary>实际生效的时区（档案 → 门店表 → 州推导 → Australia/Sydney）。</summary>
    public string EffectiveTimeZoneId { get; init; } = string.Empty;

    /// <summary>NSW / QLD / null（无法判断）；NSW 门店需要员工监控通知日期。</summary>
    public string? Jurisdiction { get; init; }

    /// <summary>已保存的 CrossBorderRegion 与当前配置地域一致（地域改动后为 false，须重新审查）。</summary>
    public bool CrossBorderReviewValid { get; init; }

    /// <summary>按已保存的档案重新计算的闸门状态（页面「缺哪一项」「哪些记录还没补」的数据来源）。</summary>
    public FaceGateReportDto Gates { get; init; } = new();
}

public sealed record FaceStoreProfileListItemDto
{
    public string StoreCode { get; init; } = string.Empty;
    public string? StoreName { get; init; }
    public int Mode { get; init; }
    public bool EvaluateMatch { get; init; }
    public bool StaffExclusionEnabled { get; init; }
    public bool TrafficDedupEnabled { get; init; }
    public bool UnwelcomeListEnabled { get; init; }
    public bool CrossBorderReviewValid { get; init; }

    /// <summary>
    /// 合规记录警告条数（各 facet 的 Warnings 之和，与详情页各 facet 提示的条数相加一致）；&gt; 0 时列表打红色「合规记录未齐」标记。
    /// 只算警告，不含仍然阻塞的缺项（已保存的档案不会有阻塞缺项）。
    /// </summary>
    public int GateWarningCount { get; init; }

    public DateTime? UpdatedAtUtc { get; init; }
}

/// <summary>PUT /profiles/{storeCode} 与 POST /profiles/{storeCode}/check 共用：整体替换可编辑字段。</summary>
public sealed record FaceStoreProfileUpdateRequest
{
    public int? Mode { get; init; }
    public bool EvaluateMatch { get; init; }
    public bool StaffExclusionEnabled { get; init; }
    public bool TrafficDedupEnabled { get; init; }
    public bool UnwelcomeListEnabled { get; init; }

    /// <summary>名单命中自动确认阈值：省略 / null = 保持库里原值（旧版 App 的保存不带它）；0 = 关闭；50–100（最多两位小数）= 阈值。</summary>
    public decimal? UnwelcomeAutoConfirmScore { get; init; }

    public string? AllowedSubjects { get; init; }
    public string? PiaReference { get; init; }
    public string? SignageInstalledOn { get; init; }
    public string? StaffNoticeDate { get; init; }
    public string? LegalSignoffBy { get; init; }
    public DateTime? LegalSignoffAt { get; init; }
    public string? DedupLegalSignoffBy { get; init; }
    public DateTime? DedupLegalSignoffAt { get; init; }
    public string? CrossBorderRegion { get; init; }
    public string? CrossBorderReviewRef { get; init; }
    public string? CrossBorderReviewedBy { get; init; }
    public DateTime? CrossBorderReviewedAt { get; init; }
    public string? TimeZoneId { get; init; }
    public string? UnwelcomeAssessmentRef { get; init; }
    public string? UnwelcomeLegalSignoffBy { get; init; }
    public DateTime? UnwelcomeLegalSignoffAt { get; init; }
    public string? StaffTrainingDate { get; init; }
    public string? PrivacyContact { get; init; }
}

// ---------- 员工同意与登记 ----------

public sealed record FaceStaffCandidateDto
{
    public string UserGuid { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Email { get; init; }

    /// <summary>该员工当前有效登记的状态：none / active。</summary>
    public string EnrollmentState { get; init; } = "none";

    /// <summary>该员工所在门店里有 NSW 门店（需要 14 天书面通知）。</summary>
    public bool RequiresNswNotice { get; init; }
}

public sealed record FaceConsentDto
{
    public string ConsentId { get; init; } = string.Empty;
    public string UserGuid { get; init; } = string.Empty;
    public string? UserName { get; init; }
    public string ConsentVersion { get; init; } = string.Empty;
    public DateTime SignedAtUtc { get; init; }

    /// <summary>paper / electronic。</summary>
    public string Method { get; init; } = string.Empty;

    public bool HasEvidence { get; init; }
    public string? NswNoticeDate { get; init; }
    public DateTime? WithdrawnAtUtc { get; init; }
    public string? WithdrawnReason { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public string CreatedBy { get; init; } = string.Empty;

    /// <summary>未撤回。</summary>
    public bool Active { get; init; }
}

public sealed record FaceConsentWithdrawRequest
{
    public string? Reason { get; init; }
}

/// <summary>撤回同意的结果：PendingRemoteDeletionCount &gt; 0 表示腾讯云删除尚未完成（凭据缺失或调用失败），后台任务会继续重试。</summary>
public sealed record FaceConsentWithdrawResultDto
{
    public FaceConsentDto Consent { get; init; } = new();
    public int WithdrawnEnrollmentCount { get; init; }
    public int PendingRemoteDeletionCount { get; init; }
}

public sealed record FaceEnrollmentDto
{
    public string EnrollmentId { get; init; } = string.Empty;
    public string UserGuid { get; init; } = string.Empty;
    public string? UserName { get; init; }
    public string ConsentId { get; init; } = string.Empty;
    public int PhotoCount { get; init; }

    /// <summary>1 Active、2 Withdrawn。</summary>
    public int Status { get; init; }

    public string StatusName { get; init; } = string.Empty;
    public DateTime EnrolledAtUtc { get; init; }
    public string EnrolledBy { get; init; } = string.Empty;
    public DateTime? WithdrawnAtUtc { get; init; }

    /// <summary>登记仍有效但同意已撤回：腾讯云侧删除待完成，后台任务会重试。</summary>
    public bool PendingRemoteDeletion { get; init; }
}

public sealed record FaceEnrollmentWithdrawRequest
{
    public string? Reason { get; init; }
}

// ---------- 客流统计与审计 ----------

public sealed record FaceTrafficCountsDto
{
    public int CaptureCount { get; init; }
    public int StaffMatchCount { get; init; }

    /// <summary>估算值：摄像机角度、距离、遮挡会造成同一人被认成两人（偏高）或两人认成一人（偏低）。</summary>
    public int UniqueVisitorCount { get; init; }

    public int RepeatCount { get; init; }
    public int LowQualityCount { get; init; }
    public int ApiFailureCount { get; init; }
}

public sealed record FaceTrafficDayDto
{
    /// <summary>门店本地营业日 yyyy-MM-dd。</summary>
    public string Date { get; init; } = string.Empty;

    public FaceTrafficCountsDto Counts { get; init; } = new();
}

public sealed record FaceTrafficHourDto
{
    /// <summary>门店本地小时 0–23。</summary>
    public int Hour { get; init; }

    public FaceTrafficCountsDto Counts { get; init; } = new();
}

public sealed record FaceTrafficDto
{
    public string StoreCode { get; init; } = string.Empty;
    public string? StoreName { get; init; }
    public string TimeZoneId { get; init; } = string.Empty;
    public string From { get; init; } = string.Empty;
    public string To { get; init; } = string.Empty;
    public IReadOnlyList<FaceTrafficDayDto> Days { get; init; } = [];

    /// <summary>仅当 from == to（单日）时返回分小时明细，否则为空数组。</summary>
    public IReadOnlyList<FaceTrafficHourDto> Hours { get; init; } = [];

    public FaceTrafficCountsDto Total { get; init; } = new();

    /// <summary>恒为 true：页面必须标注「唯一访客数为估算」。</summary>
    public bool UniqueVisitorIsEstimate { get; init; } = true;
}

public sealed record FaceAuditItemDto
{
    public long AuditId { get; init; }
    public DateTime AtUtc { get; init; }
    public string ActorId { get; init; } = string.Empty;
    public string ActorName { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string? StoreCode { get; init; }
    public string? TargetRef { get; init; }
    public string Result { get; init; } = string.Empty;
}

// ---------- 不受欢迎人员名单 ----------

/// <summary>名单条目。不含姓名、不含照片本身；照片只能经 reference 内容接口并排查看（仅 Review 权限）。</summary>
public sealed record FaceUnwelcomeEntryDto
{
    public string EntryId { get; init; } = string.Empty;

    /// <summary>1 ViolenceOrThreat、2 OrganisedOrRepeatTheft、3 Other。</summary>
    public int IncidentCategory { get; init; }

    public string IncidentCategoryName { get; init; } = string.Empty;
    public string? CategoryNote { get; init; }
    public string EvidenceRef { get; init; } = string.Empty;
    public IReadOnlyList<string> StoreScope { get; init; } = [];
    public string SubmittedById { get; init; } = string.Empty;
    public string SubmittedByName { get; init; } = string.Empty;
    public DateTime SubmittedAtUtc { get; init; }
    public string? ApprovedByName { get; init; }
    public DateTime? ApprovedAtUtc { get; init; }
    public string? SecondApprovedByName { get; init; }
    public DateTime? SecondApprovedAtUtc { get; init; }

    /// <summary>需要几位不同的批准人（Other 类别为 2，其余为 1）与已完成几位。</summary>
    public int ApprovalsNeeded { get; init; }

    public int ApprovalsDone { get; init; }
    public DateTime ReviewDueAtUtc { get; init; }
    public DateTime ExpiresAtUtc { get; init; }

    /// <summary>0 Pending、1 Active、2 Expired、3 Withdrawn。</summary>
    public int Status { get; init; }

    public string StatusName { get; init; } = string.Empty;
    public int PhotoCount { get; init; }
    public string? DisputeNote { get; init; }

    /// <summary>Active 且到期不足 30 天：页面显示到期提醒。</summary>
    public bool ExpiringSoon { get; init; }

    public int DaysToExpiry { get; init; }

    /// <summary>当前登录人是否可以批准（持有 Approve 权限、不是提交人、尚未批准过、条目仍是 Pending）。</summary>
    public bool CanApprove { get; init; }

    /// <summary>条目已撤回 / 过期，但腾讯云人员或参考照片还没删干净（后台任务会继续重试）。</summary>
    public bool CleanupPending { get; init; }
}

/// <summary>入名单的事件类别（没有「一般偷窃」）。</summary>
public sealed record FaceUnwelcomeDisputeRequest
{
    public string? Note { get; init; }
}

public sealed record FaceUnwelcomeWithdrawRequest
{
    public string? Reason { get; init; }
}

/// <summary>复核任务。不含姓名与事件细节；Score 是相似度（0–100），不是「同一个人的概率」。</summary>
public sealed record FaceUnwelcomeTaskDto
{
    public string TaskId { get; init; } = string.Empty;
    public string StoreCode { get; init; } = string.Empty;
    public string? StoreName { get; init; }
    public string CameraId { get; init; } = string.Empty;
    public DateTime CapturedAtUtc { get; init; }
    public string EntryId { get; init; } = string.Empty;
    public decimal Score { get; init; }

    /// <summary>四舍五入的整数百分比，页面显示「相似度 NN%（不是同一个人的概率）」。</summary>
    public int ScorePercent { get; init; }

    /// <summary>high（较高）/ normal（一般），由配置 FaceMatch:Unwelcome:HighBandScore 划分。</summary>
    public string Band { get; init; } = "normal";

    /// <summary>0 Pending、1 Dismissed、2 Confirmed。</summary>
    public int ReviewStatus { get; init; }

    public string ReviewStatusName { get; init; } = string.Empty;
    public string? FirstReviewerName { get; init; }
    public DateTime? FirstReviewedAtUtc { get; init; }
    public string? SecondReviewerName { get; init; }
    public DateTime? SecondReviewedAtUtc { get; init; }

    /// <summary>true = 系统按门店阈值自动确认的（ReviewStatus 为 Confirmed，FirstReviewerName 为空，不可再复核）。</summary>
    public bool AutoConfirmed { get; init; }

    /// <summary>命中图是否还在（到期删除后为 false，任务不能再复核）。</summary>
    public bool ImageAvailable { get; init; }

    public DateTime ImageDeleteAtUtc { get; init; }

    /// <summary>条目参考照片张数（并排显示用）；条目已撤回 / 过期时为 0。</summary>
    public int ReferencePhotoCount { get; init; }

    /// <summary>first（待复核；也包括旧版遗留的「已一审待二审」任务）/ none（已结束）。2026-10-13 起一个人复核即可，不再返回 second。</summary>
    public string NextStep { get; init; } = "none";

    /// <summary>当前登录人是否能复核（Review 权限、门店范围内、任务待复核、命中图还在）。</summary>
    public bool CanReview { get; init; }

    /// <summary>
    /// 命中通知（经通知平台）的发送状态：sent = 已发布给收件人；pending = 尚未发布（排队中或暂时没有收件人，会重试）；
    /// skipped = 发布前任务已被复核或图片已到期，系统不再发。
    /// </summary>
    public string NotifyState { get; init; } = FaceHitNotifyStates.Pending;

    /// <summary>通知发布时间（UTC）；未发布为 null。</summary>
    public DateTime? NotifiedAtUtc { get; init; }

    /// <summary>通知的收件人数（不含姓名）；未发布为 0。</summary>
    public int NotifyRecipientCount { get; init; }

    /// <summary>已读收件人数。</summary>
    public int NotifyReadCount { get; init; }
}

/// <summary>命中通知发送状态取值（Web / 移动端按字符串映射文案，新增取值时客户端需兜底）。</summary>
public static class FaceHitNotifyStates
{
    public const string Sent = "sent";
    public const string Pending = "pending";
    public const string Skipped = "skipped";
}

/// <summary>「抓拍与比对结果」列表的查询条件；时间按抓拍时间（UTC）。</summary>
public sealed record FaceCaptureResultQuery
{
    public string? StoreCode { get; init; }
    public string? CameraId { get; init; }

    /// <summary>staff / repeatVisitor / newVisitor / lowQuality / noMatch / notEvaluated，空 = 不限。</summary>
    public string? Decision { get; init; }

    /// <summary>true = 只看命中不受欢迎名单的抓拍。</summary>
    public bool HitOnly { get; init; }

    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}

/// <summary>抓拍对应的名单命中任务摘要（不含姓名与事件细节）。</summary>
public sealed record FaceCaptureResultHitDto
{
    public string TaskId { get; init; } = string.Empty;
    public int ScorePercent { get; init; }

    /// <summary>high（较高）/ normal（一般），与复核任务列表同一口径（配置 FaceMatch:Unwelcome:HighBandScore）。</summary>
    public string Band { get; init; } = "normal";

    public int ReviewStatus { get; init; }
    public string ReviewStatusName { get; init; } = string.Empty;
    public bool ImageAvailable { get; init; }
    public string NotifyState { get; init; } = FaceHitNotifyStates.Pending;
    public DateTime? NotifiedAtUtc { get; init; }
    public int NotifyRecipientCount { get; init; }
    public int NotifyReadCount { get; init; }
}

/// <summary>
/// 一次抓拍及其比对结果。分数是相似度（0–100），不是「同一个人的概率」；没有评估行（开关未开、重复抓拍、接口失败）时
/// Evaluated 为 false，Decision 为 null。图片 ID 只在调用者有看图权限时返回，图片内容另走接口按需读取并写审计。
/// </summary>
public sealed record FaceCaptureResultDto
{
    public string CaptureId { get; init; } = string.Empty;
    public string StoreCode { get; init; } = string.Empty;
    public string? StoreName { get; init; }
    public string CameraId { get; init; } = string.Empty;
    public DateTime CapturedAtUtc { get; init; }
    public int FaceCount { get; init; }
    public bool IsDuplicate { get; init; }
    public bool Evaluated { get; init; }

    /// <summary>staff / repeatVisitor / newVisitor / lowQuality / noMatch；未评估为 null。</summary>
    public string? Decision { get; init; }

    public int? StaffScorePercent { get; init; }
    public int? VisitScorePercent { get; init; }
    public string? FaceImageId { get; init; }
    public string? SceneImageId { get; init; }
    public FaceCaptureResultHitDto? Hit { get; init; }
}

public interface IFaceCaptureResultService
{
    Task<FaceMatchPageDto<FaceCaptureResultDto>> ListAsync(
        FaceCaptureResultQuery query, FaceMatchActor actor, bool canViewImages, CancellationToken cancellationToken = default);

    /// <summary>读一张抓拍图：先校验门店范围，再走与摄像机管理页相同的受保护读取（含审计）。</summary>
    Task<FaceMatchImageContent> GetImageAsync(
        string captureId, string imageId, FaceMatchActor actor, bool canViewImages, CancellationToken cancellationToken = default);
}

public sealed record FaceUnwelcomeReviewRequest
{
    /// <summary>match（是同一个人）/ notMatch（不是）。任何一方 notMatch 即 Dismissed。</summary>
    public string? Verdict { get; init; }
}

public sealed record FaceUnwelcomeStatsEntryDto
{
    public string EntryId { get; init; } = string.Empty;
    public string StatusName { get; init; } = string.Empty;
    public int TaskCount { get; init; }
    public int PendingCount { get; init; }
    public int DismissedCount { get; init; }

    /// <summary>人工确认的任务数（不含系统自动确认的）。</summary>
    public int ConfirmedCount { get; init; }

    /// <summary>系统按门店阈值自动确认的任务数：没有人判断过，不参与驳回率与「建议复核」。</summary>
    public int AutoConfirmedCount { get; init; }

    /// <summary>驳回率 = 已驳回 / (已驳回 + 人工确认)；没有人工结案的任务时为 null。</summary>
    public decimal? DismissRate { get; init; }

    /// <summary>已结束任务不少于 5 条且驳回率不低于 80%：提示复核该条目是否应出名单。</summary>
    public bool SuggestReview { get; init; }
}

public sealed record FaceUnwelcomeStatsBucketDto
{
    public int From { get; init; }
    public int To { get; init; }
    public int Total { get; init; }
    public int DismissedCount { get; init; }

    /// <summary>人工确认的任务数（不含自动确认）。</summary>
    public int ConfirmedCount { get; init; }

    /// <summary>系统自动确认的任务数（没有人判断过，不进驳回率）。</summary>
    public int AutoConfirmedCount { get; init; }
}

public sealed record FaceUnwelcomeStatsDto
{
    public int TaskCount { get; init; }
    public int PendingCount { get; init; }
    public int DismissedCount { get; init; }

    /// <summary>人工确认的任务数（不含自动确认）。</summary>
    public int ConfirmedCount { get; init; }

    /// <summary>系统自动确认的任务数；TaskCount = 待复核 + 驳回 + 人工确认 + 自动确认。</summary>
    public int AutoConfirmedCount { get; init; }
    public decimal? DismissRate { get; init; }
    public IReadOnlyList<FaceUnwelcomeStatsEntryDto> Entries { get; init; } = [];

    /// <summary>按相似度每 5 分一档（0–4、5–9 … 95–100）的驳回情况，用于调阈值。</summary>
    public IReadOnlyList<FaceUnwelcomeStatsBucketDto> ScoreBuckets { get; init; } = [];
}

/// <summary>图片字节与对外声明的 Content-Type（只会是 image/jpeg 或 image/png）。</summary>
public sealed record FaceMatchImageContent(byte[] Bytes, string ContentType);

/// <summary>上传的文件（已读入内存，大小在控制器入口处限定）。</summary>
public sealed record FaceMatchUpload(string FileName, string? ContentType, byte[] Bytes);

public sealed record FaceConsentCreateRequest
{
    public string? UserGuid { get; init; }
    public string? ConsentVersion { get; init; }
    public DateTime? SignedAtUtc { get; init; }
    public string? Method { get; init; }
    public string? NswNoticeDate { get; init; }
}

public sealed record FaceEnrollmentCreateRequest
{
    public string? UserGuid { get; init; }
    public string? ConsentId { get; init; }
}

public sealed record FaceUnwelcomeEntryCreateRequest
{
    public int? IncidentCategory { get; init; }
    public string? CategoryNote { get; init; }
    public string? EvidenceRef { get; init; }

    /// <summary>生效门店编码（至少一个）。</summary>
    public IReadOnlyList<string>? StoreScope { get; init; }

    public DateTime? ExpiresAtUtc { get; init; }
}

// ---------- 服务接口 ----------

public interface IFaceStoreProfileService
{
    Task<FaceMatchConfigDto> GetConfigAsync(FaceMatchPermissionFlagsDto permissions, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FaceStoreProfileListItemDto>> ListAsync(CancellationToken cancellationToken = default);
    Task<FaceStoreProfileDto> GetAsync(string storeCode, CancellationToken cancellationToken = default);
    Task<FaceGateReportDto> CheckAsync(string storeCode, FaceStoreProfileUpdateRequest request, CancellationToken cancellationToken = default);
    Task<FaceStoreProfileDto> UpdateAsync(string storeCode, FaceStoreProfileUpdateRequest request, FaceMatchActor actor, CancellationToken cancellationToken = default);
}

public interface IFaceStaffService
{
    Task<IReadOnlyList<FaceStaffCandidateDto>> ListStaffAsync(string storeCode, string? keyword, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FaceConsentDto>> ListConsentsAsync(string? userGuid, bool includeWithdrawn, CancellationToken cancellationToken = default);

    Task<FaceConsentDto> CreateConsentAsync(
        FaceConsentCreateRequest request, FaceMatchUpload? scan, FaceMatchActor actor, CancellationToken cancellationToken = default);

    Task<FaceConsentWithdrawResultDto> WithdrawConsentAsync(
        string consentId, FaceConsentWithdrawRequest request, FaceMatchActor actor, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FaceEnrollmentDto>> ListEnrollmentsAsync(string? userGuid, bool activeOnly, CancellationToken cancellationToken = default);

    Task<FaceEnrollmentDto> CreateEnrollmentAsync(
        FaceEnrollmentCreateRequest request, IReadOnlyList<FaceMatchUpload> photos, FaceMatchActor actor, CancellationToken cancellationToken = default);

    Task<FaceEnrollmentDto> WithdrawEnrollmentAsync(
        string enrollmentId, FaceEnrollmentWithdrawRequest request, FaceMatchActor actor, CancellationToken cancellationToken = default);
}

public interface IFaceTrafficService
{
    Task<FaceTrafficDto> GetTrafficAsync(string storeCode, string? from, string? to, FaceMatchActor actor, CancellationToken cancellationToken = default);

    Task<FaceMatchPageDto<FaceAuditItemDto>> ListAuditAsync(
        string? storeCode, string? action, DateTime? fromUtc, DateTime? toUtc, int page, int pageSize, CancellationToken cancellationToken = default);
}

public interface IFaceUnwelcomeService
{
    Task<FaceMatchPageDto<FaceUnwelcomeEntryDto>> ListEntriesAsync(
        string? status, string? storeCode, int page, int pageSize, FaceMatchActor actor, bool canApprove, CancellationToken cancellationToken = default);

    Task<FaceUnwelcomeEntryDto> CreateEntryAsync(
        FaceUnwelcomeEntryCreateRequest request, IReadOnlyList<FaceMatchUpload> photos, FaceMatchActor actor, CancellationToken cancellationToken = default);

    Task<FaceUnwelcomeEntryDto> ApproveEntryAsync(string entryId, FaceMatchActor actor, CancellationToken cancellationToken = default);

    Task<FaceUnwelcomeEntryDto> WithdrawEntryAsync(
        string entryId, FaceUnwelcomeWithdrawRequest request, FaceMatchActor actor, CancellationToken cancellationToken = default);

    Task<FaceUnwelcomeEntryDto> SetDisputeNoteAsync(
        string entryId, FaceUnwelcomeDisputeRequest request, FaceMatchActor actor, CancellationToken cancellationToken = default);

    Task<FaceMatchPageDto<FaceUnwelcomeTaskDto>> ListTasksAsync(
        string? status, string? storeCode, int page, int pageSize, FaceMatchActor actor, CancellationToken cancellationToken = default);

    Task<FaceUnwelcomeTaskDto> ReviewTaskAsync(
        string taskId, FaceUnwelcomeReviewRequest request, FaceMatchActor actor, CancellationToken cancellationToken = default);

    Task<FaceMatchImageContent> GetTaskImageAsync(string taskId, FaceMatchActor actor, CancellationToken cancellationToken = default);

    Task<FaceMatchImageContent> GetReferencePhotoAsync(string taskId, int index, FaceMatchActor actor, CancellationToken cancellationToken = default);

    Task<FaceUnwelcomeStatsDto> GetStatsAsync(FaceMatchActor actor, CancellationToken cancellationToken = default);
}
