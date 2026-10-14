using BlazorApp.Api.Data;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.FaceMatch;

/// <summary>FaceAuditLog.Action 取值（页面按它筛选，增删需同步 Web 审计页签的说明文案与运维手册 16.10）。</summary>
public static class FaceMatchAuditActions
{
    public const string ProfileUpdate = "profile.update";
    public const string ProfileInvalidated = "profile.region-invalidated";
    public const string ConsentCreate = "consent.create";
    public const string ConsentWithdraw = "consent.withdraw";
    public const string EnrollmentCreate = "enrollment.create";
    public const string EnrollmentWithdraw = "enrollment.withdraw";
    public const string EnrollmentIntegrity = "enrollment.integrity-mismatch";
    public const string EntrySubmit = "unwelcome.entry.submit";
    public const string EntryApprove = "unwelcome.entry.approve";
    public const string EntryWithdraw = "unwelcome.entry.withdraw";
    public const string EntryExpire = "unwelcome.entry.expire";
    public const string EntryDispute = "unwelcome.entry.dispute-note";
    public const string TaskReview = "unwelcome.task.review";
    public const string TaskViewCapture = "unwelcome.task.view-capture";
    public const string TaskViewReference = "unwelcome.task.view-reference";
    public const string TaskImageDelete = "unwelcome.task.image-delete";
    public const string VisitGroupDelete = "visit-group.delete";
    public const string CaptureRetention = "capture.retention-cleanup";

    /// <summary>
    /// 抓拍 / 命中图缩略图批量查看：<b>每次批量请求记一条</b>（不逐张记）。StoreCode = 本批全部同一家店时填该店，否则为空；
    /// TargetRef 不放任何抓拍 / 任务编号；Result 是 <c>n=成功生成的张数</c>。点开详情看大图仍走各自的看图动作并单独审计。
    /// </summary>
    public const string ThumbnailsView = "capture.thumbnails.view";

    /// <summary>后台任务的操作人。</summary>
    public const string SystemActorId = "system";
}

/// <summary>
/// 持久审计写入（FaceAuditLog）。内容刻意只有「谁、何时、做了什么、对谁（编号）、结果」：
/// 不含图片、对象键、密钥、姓名或事件细节。写入失败直接抛出（审计是合规控制，不静默吞掉）。
/// </summary>
public interface IFaceMatchAudit
{
    Task WriteAsync(
        FaceMatchActor actor,
        string action,
        string? storeCode,
        string? targetRef,
        string result,
        CancellationToken cancellationToken = default);
}

public sealed class FaceMatchAudit(SqlSugarContext context, TimeProvider timeProvider, ILogger<FaceMatchAudit> logger) : IFaceMatchAudit
{
    private readonly ISqlSugarClient _db = context.Db;

    public async Task WriteAsync(
        FaceMatchActor actor,
        string action,
        string? storeCode,
        string? targetRef,
        string result,
        CancellationToken cancellationToken = default)
    {
        var row = new FaceAuditLog
        {
            AtUtc = timeProvider.GetUtcNow().UtcDateTime,
            ActorId = Sanitize(actor.Id, 100) ?? "unknown",
            ActorName = Sanitize(actor.Name, 100) ?? "unknown",
            Action = Sanitize(action, 48) ?? "unknown",
            StoreCode = Sanitize(storeCode, 32),
            TargetRef = Sanitize(targetRef, 100),
            Result = Sanitize(result, 32) ?? "unknown",
        };

        try
        {
            await _db.Insertable(row).ExecuteCommandAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 审计表写不进去时，必须让调用方知道（图片查看据此不返回字节）；日志里不带请求内容。
            logger.LogError(ex, "人脸比对审计写入失败: Action={Action}", row.Action);
            throw new FaceMatchException(500, "FACE_MATCH_AUDIT_FAILED", "审计记录写入失败，操作已中止或需要管理员核对。");
        }
    }

    /// <summary>去控制字符（防注入）并截断；空串视为 null。</summary>
    internal static string? Sanitize(string? value, int maxLength)
    {
        var text = new string((value ?? string.Empty).Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (text.Length == 0) return null;
        return text.Length > maxLength ? text[..maxLength] : text;
    }
}
