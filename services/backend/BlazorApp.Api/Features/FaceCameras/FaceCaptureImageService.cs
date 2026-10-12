using System.Text.RegularExpressions;
using BlazorApp.Api.Data;
using SqlSugar;

namespace BlazorApp.Api.Features.FaceCameras;

/// <summary>
/// 读取人脸抓拍图片（敏感生物识别信息）。保护措施，逐条都有测试：
/// 1. 权限：调用方必须持有 FaceCapture.Images.View（由控制器判定后传入），否则不查库、不碰 COS；
/// 2. 对象键只来自数据库：入参只有三个编号，先按 ImageId → CaptureId → CameraId 三者归属一次性查库，
///    任一不一致都返回同一个 404（不泄露某个编号是否存在）；编号格式不对在查库前就拒绝；
/// 3. Storage 必须是 cos（local 返回 409），ContentType 只认 image/jpeg、image/png 且字节头必须与之相符；
/// 4. 最大 5MB（库里声明的大小、COS 声明的长度、实际读到的字节三处都卡）；
/// 5. 审计：每次成功读取与每次被拒绝的尝试都记结构化日志（操作人、三个编号、结果、UTC 时间、字节数），
///    日志里不含对象键 / 地址 / 密钥。审计目前只在容器日志里，尚无持久化审计表。
/// </summary>
public sealed class FaceCaptureImageService(
    SqlSugarContext context,
    IFaceImageStore store,
    TimeProvider timeProvider,
    ILogger<FaceCaptureImageService> logger
) : IFaceCaptureImageService
{
    /// <summary>单张图片最大读取字节数。</summary>
    public const int MaxImageBytes = 5 * 1024 * 1024;

    private const int MaxAuditTextLength = 120;
    private const int MaxAuditIdLength = 64;

    // ImageId / CaptureId 都是 32 位十六进制；这里放宽到 1~32 位字母数字，只为在查库前挡掉明显的垃圾输入与注入串。
    private static readonly Regex IdPattern = new(@"^[A-Za-z0-9]{1,32}\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly ISqlSugarClient _db = context.Db;

    public async Task<FaceImageContent> GetContentAsync(
        string cameraId,
        string captureId,
        string imageId,
        FaceCameraActor actor,
        bool callerHasImagesViewPermission,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (!callerHasImagesViewPermission)
            {
                throw FaceImageErrors.Forbidden();
            }

            // 路由值原样校验（不 trim）：多余的空白或换行直接按格式错误处理，不会被「修正」成别的编号。
            if (!FaceCameraRules.IsValidCameraId(cameraId) || !IdPattern.IsMatch(captureId ?? string.Empty) || !IdPattern.IsMatch(imageId ?? string.Empty))
            {
                throw FaceImageErrors.NotFound();
            }

            var row = await FindImageAsync(cameraId!, captureId!, imageId!, cancellationToken) ?? throw FaceImageErrors.NotFound();

            if (!string.Equals(row.Storage?.Trim(), "cos", StringComparison.OrdinalIgnoreCase))
            {
                throw FaceImageErrors.NotInCos();
            }

            if (row.ByteSize > MaxImageBytes)
            {
                throw FaceImageErrors.TooLarge();
            }

            var contentType = NormalizeContentType(row.ContentType) ?? throw FaceImageErrors.Unsupported();
            var bytes = await store.ReadAsync(row.ObjectKey, MaxImageBytes, cancellationToken);
            if (!MatchesMagicBytes(bytes, contentType))
            {
                throw FaceImageErrors.Unsupported();
            }

            AuditSuccess(actor, cameraId!, captureId!, imageId!, bytes.Length);
            return new FaceImageContent(bytes, contentType);
        }
        catch (FaceCameraException ex)
        {
            AuditRejected(actor, cameraId, captureId, imageId, ex.Code);
            throw;
        }
    }

    /// <summary>
    /// 一次查询同时校验三者归属：只有 ImageId、CaptureId、CameraId 都对得上才返回行。
    /// 两个 char(32) 参数显式声明为定长 ANSI，避免 SQL Server 按 nvarchar 做隐式转换而放弃主键 / 索引查找。
    /// </summary>
    private async Task<ImageRow?> FindImageAsync(string cameraId, string captureId, string imageId, CancellationToken cancellationToken)
    {
        const string sql = """
SELECT i.Storage AS Storage, i.ObjectKey AS ObjectKey, i.ContentType AS ContentType, i.ByteSize AS ByteSize
FROM FaceCaptureImage i
INNER JOIN FaceCaptureEvent e ON e.CaptureId = i.CaptureId
WHERE i.ImageId = @imageId AND i.CaptureId = @captureId AND e.CameraId = @cameraId
""";
        var parameters = new List<SugarParameter>
        {
            new("@imageId", imageId, System.Data.DbType.AnsiStringFixedLength) { Size = 32 },
            new("@captureId", captureId, System.Data.DbType.AnsiStringFixedLength) { Size = 32 },
            new("@cameraId", cameraId),
        };
        var rows = await _db.Ado.SqlQueryAsync<ImageRow>(sql, parameters);
        return rows.FirstOrDefault();
    }

    /// <summary>
    /// 库里的 ContentType 只认 image/jpeg、image/png（忽略参数与大小写）；其余一律拒绝（不会按任何别的类型回给浏览器）。
    /// 为空时按 image/jpeg：接收服务在摄像机没给类型时就是按 image/jpeg 上传的，随后由字节头校验兜底。
    /// </summary>
    internal static string? NormalizeContentType(string? raw)
    {
        var text = string.IsNullOrWhiteSpace(raw) ? "image/jpeg" : raw;
        var semicolon = text.IndexOf(';');
        var media = (semicolon >= 0 ? text[..semicolon] : text).Trim().ToLowerInvariant();
        return media is "image/jpeg" or "image/png" ? media : null;
    }

    /// <summary>字节头必须与声明的类型一致（JPEG: FF D8 FF；PNG: 89 50 4E 47 0D 0A 1A 0A）。</summary>
    internal static bool MatchesMagicBytes(ReadOnlySpan<byte> bytes, string contentType) => contentType switch
    {
        "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
        "image/png" => bytes.Length >= 8 && bytes[..8].SequenceEqual(PngSignature),
        _ => false,
    };

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // ---------- 审计 ----------

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    private void AuditSuccess(FaceCameraActor actor, string cameraId, string captureId, string imageId, int bytes)
    {
        logger.LogInformation(
            "人脸抓拍图片查看审计: Result={Result}, ActorId={ActorId}, ActorName={ActorName}, CameraId={CameraId}, CaptureId={CaptureId}, ImageId={ImageId}, Bytes={Bytes}, AtUtc={AtUtc:O}",
            "ok",
            Sanitize(actor.Id, MaxAuditTextLength),
            Sanitize(actor.Name, MaxAuditTextLength),
            Sanitize(cameraId, MaxAuditIdLength),
            Sanitize(captureId, MaxAuditIdLength),
            Sanitize(imageId, MaxAuditIdLength),
            bytes,
            Now());
    }

    /// <summary>被拒绝的尝试（无权限、编号不匹配 / 不存在、不在 COS、配置缺失、COS 失败等）记 Warning，Result 为错误码。</summary>
    private void AuditRejected(FaceCameraActor actor, string? cameraId, string? captureId, string? imageId, string result)
    {
        logger.LogWarning(
            "人脸抓拍图片查看审计: Result={Result}, ActorId={ActorId}, ActorName={ActorName}, CameraId={CameraId}, CaptureId={CaptureId}, ImageId={ImageId}, Bytes={Bytes}, AtUtc={AtUtc:O}",
            result,
            Sanitize(actor.Id, MaxAuditTextLength),
            Sanitize(actor.Name, MaxAuditTextLength),
            Sanitize(cameraId, MaxAuditIdLength),
            Sanitize(captureId, MaxAuditIdLength),
            Sanitize(imageId, MaxAuditIdLength),
            0,
            Now());
    }

    /// <summary>去控制字符（防日志注入）并截断；入参可能是客户端传来的任意字符串，所以拒绝类审计也经过它。</summary>
    private static string Sanitize(string? value, int maxLength)
    {
        var text = new string((value ?? string.Empty).Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (text.Length == 0) return "unknown";
        return text.Length > maxLength ? text[..maxLength] : text;
    }

    private sealed class ImageRow
    {
        public string? Storage { get; set; }
        public string ObjectKey { get; set; } = string.Empty;
        public string? ContentType { get; set; }
        public int ByteSize { get; set; }
    }
}
