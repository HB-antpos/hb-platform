namespace BlazorApp.Api.Features.FaceMatch;

// 「人脸比对」合并入口的后端契约：命中流（以命中任务为主表）与抓拍缩略图批量接口。
// 字段名、取值与前后端共同遵守的契约文档一致，改动前必须三端同步。

/// <summary>命中流的筛选取值（<see cref="FaceHitResultQuery.HitStatus"/>）。其它值一律 400。</summary>
public static class FaceHitStatuses
{
    public const string All = "all";

    /// <summary>当前登录人的命中通知未读。</summary>
    public const string Unread = "unread";

    /// <summary>ReviewStatus = 0。</summary>
    public const string Pending = "pending";

    /// <summary>已确认且复核人是 system（系统按阈值自动确认，没有人判断过）。</summary>
    public const string AutoConfirmed = "autoConfirmed";

    /// <summary>人工确认：已确认且不是系统自动确认。</summary>
    public const string Confirmed = "confirmed";

    /// <summary>ReviewStatus = 1（已驳回）。</summary>
    public const string Dismissed = "dismissed";
}

/// <summary>命中流查询条件；时间按任务的 <c>CapturedAtUtc</c>，左闭右开。</summary>
public sealed record FaceHitResultQuery
{
    public string? StoreCode { get; init; }

    /// <summary>all（默认）/ unread / pending / autoConfirmed / confirmed / dismissed。</summary>
    public string? HitStatus { get; init; }

    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}

/// <summary>
/// 命中流里的一条：以命中任务（FaceMatchTask）为主，Match 模式不留存抓拍也有这一行。
/// 不含姓名、事件细节、对象键与图片；分数是相似度（0–100），不是「同一个人的概率」。
/// </summary>
public sealed record FaceHitResultDto
{
    public string TaskId { get; init; } = string.Empty;

    /// <summary>对应的留存抓拍（同摄像机 + 同接收时间）；Match 模式不留存 → null。</summary>
    public string? CaptureId { get; init; }

    public string StoreCode { get; init; } = string.Empty;
    public string? StoreName { get; init; }
    public string CameraId { get; init; } = string.Empty;
    public DateTime CapturedAtUtc { get; init; }
    public int ScorePercent { get; init; }

    /// <summary>high（较高）/ normal（一般），由配置 FaceMatch:Unwelcome:HighBandScore 划分。</summary>
    public string Band { get; init; } = "normal";

    /// <summary>0 Pending、1 Dismissed、2 Confirmed。</summary>
    public int ReviewStatus { get; init; }

    public string ReviewStatusName { get; init; } = string.Empty;

    /// <summary>ReviewStatus 为 Confirmed 且复核人记为 system。</summary>
    public bool AutoConfirmed { get; init; }

    /// <summary>命中图是否还在保留期（ImageObjectKey 非空）。</summary>
    public bool ImageAvailable { get; init; }

    /// <summary>当前登录人对应的命中通知是否未读（口径同 App 红色数字）；没有通知行 = false。</summary>
    public bool Unread { get; init; }

    /// <summary>当前登录人的那条命中通知编号；没有则为 null。</summary>
    public string? NotificationId { get; init; }

    public string NotifyState { get; init; } = FaceHitNotifyStates.Pending;
    public DateTime? NotifiedAtUtc { get; init; }
    public int NotifyRecipientCount { get; init; }
    public int NotifyReadCount { get; init; }

    /// <summary>持有 Review 权限 + 在门店范围 + 任务待复核 + 命中图还在（详情里「去复核」用）。</summary>
    public bool CanReview { get; init; }

    // 以下四项只有「有对应留存抓拍（与评估）」才有，否则为 null。
    public string? Decision { get; init; }
    public int? StaffScorePercent { get; init; }
    public int? VisitScorePercent { get; init; }
    public int? FaceCount { get; init; }
}

/// <summary>命中流各状态的数量；<c>Total = Pending + AutoConfirmed + Confirmed + Dismissed</c>，<c>Unread</c> 与之正交。</summary>
public sealed record FaceHitSummaryDto
{
    public int Total { get; init; }
    public int Unread { get; init; }
    public int Pending { get; init; }
    public int AutoConfirmed { get; init; }
    public int Confirmed { get; init; }
    public int Dismissed { get; init; }
}

/// <summary>标已读之后该类型（命中通知）的剩余未读数，App 直接用它写回红色数字的缓存。</summary>
public sealed record FaceHitReadResultDto
{
    public int UnreadCount { get; init; }
}

public interface IFaceHitResultService
{
    /// <summary>
    /// 命中流列表。门店范围与抓拍流一致（管理员不限，其他人只看自己关联的门店）；
    /// <paramref name="canReview"/> 是调用者是否持有 FaceMatch.Unwelcome.Review（由控制器判定后传入）。
    /// </summary>
    Task<FaceMatchPageDto<FaceHitResultDto>> ListAsync(
        FaceHitResultQuery query, FaceMatchActor actor, bool canReview, CancellationToken cancellationToken = default);

    /// <summary>命中流各状态的数量（只看 StoreCode / FromUtc / ToUtc；<c>HitStatus</c> 与分页条件被忽略）。</summary>
    Task<FaceHitSummaryDto> GetSummaryAsync(FaceHitResultQuery query, FaceMatchActor actor, CancellationToken cancellationToken = default);

    /// <summary>把<b>当前登录人</b>的那条命中通知标为已读（幂等，只改本人的未读行）；任务不存在或不在门店范围 → 404。</summary>
    Task<FaceHitReadResultDto> MarkReadAsync(string taskId, FaceMatchActor actor, CancellationToken cancellationToken = default);
}

// ---------- 缩略图 ----------

/// <summary>缩略图请求项的种类。</summary>
public static class FaceThumbnailKinds
{
    /// <summary>留存抓拍的人脸小图（FaceCaptureImage.Kind = 0）；id 是抓拍编号。</summary>
    public const string Capture = "capture";

    /// <summary>命中任务的命中图；id 是任务编号。</summary>
    public const string Hit = "hit";
}

/// <summary>缩略图结果状态。</summary>
public static class FaceThumbnailStatuses
{
    /// <summary>已生成缩略图（imageBase64 有值）。</summary>
    public const string Ok = "ok";

    /// <summary>没有可用的留存图（抓拍没有人脸小图 / 任务没有命中图键 / 图片还在接收端本地）。</summary>
    public const string None = "none";

    /// <summary>已过保留期或读取不到（对象已被清理、存储暂时失败、内容不是合法图片）。</summary>
    public const string Gone = "gone";

    /// <summary>编号格式不对、不存在或不在自己的门店范围（对外同一口径，不泄露编号是否存在）。</summary>
    public const string Denied = "denied";
}

public sealed record FaceThumbnailRequestItem
{
    /// <summary>capture / hit。</summary>
    public string? Kind { get; init; }

    /// <summary>capture → 抓拍编号；hit → 任务编号。</summary>
    public string? Id { get; init; }
}

public sealed record FaceThumbnailRequest
{
    /// <summary>1–30 项。</summary>
    public IReadOnlyList<FaceThumbnailRequestItem>? Items { get; init; }
}

public sealed record FaceThumbnailItemDto
{
    public string Kind { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;

    /// <summary>ok / none / gone / denied。</summary>
    public string Status { get; init; } = FaceThumbnailStatuses.Denied;

    /// <summary>仅 ok 时有值：JPEG（最长边不超过 128 像素、质量约 70、不带元数据）的 base64，不带 data: 前缀。</summary>
    public string? ImageBase64 { get; init; }
}

public sealed record FaceThumbnailResponse
{
    /// <summary>与请求同序。</summary>
    public IReadOnlyList<FaceThumbnailItemDto> Items { get; init; } = [];
}

public interface IFaceThumbnailService
{
    /// <summary>
    /// 批量取缩略图（权限 FaceCapture.Images.View 由控制器的策略保证）。整批 1–30 项，否则 400；
    /// 单项失败不拖垮整批；<b>一次请求恰好写一条审计</b>（<see cref="FaceMatchAuditActions.ThumbnailsView"/>，不含编号 / 对象键 / 图片）。
    /// </summary>
    Task<FaceThumbnailResponse> GetThumbnailsAsync(
        FaceThumbnailRequest request, FaceMatchActor actor, CancellationToken cancellationToken = default);
}
