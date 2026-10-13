using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.FaceMatch;

/// <summary>
/// 业务错误构造：状态码与错误码是 Web 文案映射的契约，消息固定，不含图片、对象键、密钥、完整地址、腾讯云错误原文。
/// </summary>
internal static class FaceMatchErrors
{
    public static FaceMatchException Disabled(string? reason) =>
        new(503, "FACE_MATCH_DISABLED", "人脸比对功能尚未开启（地域或腾讯云管理凭据未配置）。", new { reason });

    public static FaceMatchException StoreNotConfigured() =>
        new(503, "FACE_MATCH_STORE_NOT_CONFIGURED", "私有图片存储尚未配置，暂时不能上传或查看照片。");

    public static FaceMatchException NotFound(string what = "记录") =>
        new(404, "FACE_MATCH_NOT_FOUND", $"{what}不存在。");

    public static FaceMatchException Invalid(string field, string message) =>
        new(400, "FACE_MATCH_FIELD_INVALID", message, new { field });

    public static FaceMatchException ImageGone() =>
        new(404, "FACE_MATCH_IMAGE_GONE", "图片不存在或已被清理。");

    public static FaceMatchException FromLibrary(FaceLibraryException ex) => ex.Kind switch
    {
        FaceLibraryFailureKind.Disabled => new(503, "FACE_MATCH_DISABLED", "人脸比对功能尚未开启（地域或腾讯云管理凭据未配置）。"),
        FaceLibraryFailureKind.Auth => new(502, "FACE_MATCH_TENCENT_AUTH", "腾讯云拒绝了请求，请联系管理员检查管理子账号、权限策略与账号状态。"),
        FaceLibraryFailureKind.RateLimited => new(503, "FACE_MATCH_TENCENT_BUSY", "腾讯云人脸库繁忙，请稍后重试。"),
        FaceLibraryFailureKind.InvalidImage => new(422, "FACE_MATCH_PHOTO_UNUSABLE", "有照片无法使用（没有检测到人脸、过小、模糊或质量不达标），请更换后重试。"),
        FaceLibraryFailureKind.LimitExceeded => new(409, "FACE_MATCH_TENCENT_LIMIT", "已超过腾讯云人脸库的数量上限。"),
        FaceLibraryFailureKind.AlreadyExists => new(409, "FACE_MATCH_TENCENT_CONFLICT", "腾讯云人脸库里已存在相同编号的记录，请稍后重试。"),
        FaceLibraryFailureKind.NotFound => new(409, "FACE_MATCH_TENCENT_CONFLICT", "腾讯云人脸库里找不到对应记录，请稍后重试。"),
        FaceLibraryFailureKind.Unavailable => new(502, "FACE_MATCH_TENCENT_UNAVAILABLE", "腾讯云人脸库暂时不可用或超时，请稍后重试。"),
        _ => new(502, "FACE_MATCH_TENCENT_ERROR", "腾讯云人脸库调用失败，请稍后重试。"),
    };

    public static FaceMatchException FromStore(FaceMatchStoreException ex) => ex.Kind switch
    {
        FaceMatchStoreFailureKind.NotConfigured => StoreNotConfigured(),
        FaceMatchStoreFailureKind.NotFound => ImageGone(),
        FaceMatchStoreFailureKind.TooLarge => new(502, "FACE_MATCH_IMAGE_TOO_LARGE", "图片超过允许读取的大小上限，已拒绝。"),
        FaceMatchStoreFailureKind.Denied => new(502, "FACE_MATCH_STORE_DENIED", "图片存储拒绝了请求，请联系管理员检查读写权限配置。"),
        FaceMatchStoreFailureKind.Timeout => new(502, "FACE_MATCH_STORE_TIMEOUT", "图片存储超时，请稍后重试。"),
        _ => new(502, "FACE_MATCH_STORE_ERROR", "图片存储调用失败，请稍后重试。"),
    };

    /// <summary>用户展示名：全名优先，其次用户名。</summary>
    public static string DisplayName(User user) =>
        !string.IsNullOrWhiteSpace(user.FullName) ? user.FullName!.Trim() : user.Username;

    /// <summary>SQL Server 唯一约束 / 主键冲突（2601、2627）或 SQLite 的 UNIQUE 冲突。</summary>
    public static bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
            {
                return true;
            }

            if (current.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>批量取用户展示名（编号 → 名字）；找不到的编号不在结果里。</summary>
    public static async Task<Dictionary<string, string>> LoadUserNamesAsync(
        ISqlSugarClient db, IEnumerable<string?> userGuids, CancellationToken cancellationToken)
    {
        var ids = userGuids.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var users = await db.Queryable<User>().Where(u => ids.Contains(u.UserGUID)).ToListAsync(cancellationToken);
        return users.ToDictionary(u => u.UserGUID, DisplayName, StringComparer.OrdinalIgnoreCase);
    }
}
