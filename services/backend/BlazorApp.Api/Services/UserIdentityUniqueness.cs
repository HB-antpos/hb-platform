using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services;

/// <summary>新建 / 更新账号时用户名或邮箱与已有账号撞车的结果。</summary>
internal enum UserIdentityConflict
{
    None,
    Username,
    Email,
}

/// <summary>
/// 账号「用户名 / 邮箱」不得重复的统一校验（管理员路径 UserService 与店长路径 StoreUserReactService 共用）。
/// 口径与库里的唯一索引一致：IX_User_Username_Unique / IX_User_Email_Unique 覆盖整张 [User] 表（含 IsDeleted 行），
/// 生产排序规则 Chinese_PRC_90_CI_AS 不区分大小写。这里显式按「去首尾空白 + 忽略大小写」比较，
/// 这样 SQLite 测试库与生产库结论一致，也不会出现「查询命中、C# 区分大小写比对却漏判，最后撞唯一索引报笼统失败」。
/// 错误码沿用 USERNAME_EXISTS / EMAIL_EXISTS（Web 与移动端已按它们映射到对应字段）。
/// </summary>
internal static class UserIdentityUniqueness
{
    public const string UsernameTakenCode = "USERNAME_EXISTS";
    public const string EmailTakenCode = "EMAIL_EXISTS";
    public const string UsernameTakenMessage = "该用户名已被使用，请换一个用户名";
    public const string EmailTakenMessage = "该邮箱已被其他账号使用，请换一个邮箱";

    /// <summary>
    /// 查找与给定用户名 / 邮箱冲突的其他账号；用户名优先判定。
    /// <paramref name="excludeUserGuid"/> 为正在更新的账号本身：保持自己原值保存不算重复。
    /// 空白的用户名或邮箱不参与判定（必填由各自的入参校验负责）。
    /// </summary>
    public static async Task<UserIdentityConflict> FindConflictAsync(
        ISqlSugarClient db,
        string? username,
        string? email,
        string? excludeUserGuid = null
    )
    {
        var usernameKey = (username ?? string.Empty).Trim().ToLowerInvariant();
        var emailKey = (email ?? string.Empty).Trim().ToLowerInvariant();
        var excludeGuid = string.IsNullOrWhiteSpace(excludeUserGuid) ? null : excludeUserGuid;

        // 关键逻辑：不按 IsDeleted 过滤——唯一索引覆盖整表，软删除账号仍占用用户名 / 邮箱，漏判只会换成数据库异常。
        if (usernameKey.Length > 0)
        {
            var usernameTaken = await db.Queryable<User>()
                .Where(u => u.Username.Trim().ToLower() == usernameKey)
                .WhereIF(excludeGuid != null, u => u.UserGUID != excludeGuid)
                .AnyAsync();
            if (usernameTaken)
            {
                return UserIdentityConflict.Username;
            }
        }

        if (emailKey.Length > 0)
        {
            var emailTaken = await db.Queryable<User>()
                .Where(u => u.Email.Trim().ToLower() == emailKey)
                .WhereIF(excludeGuid != null, u => u.UserGUID != excludeGuid)
                .AnyAsync();
            if (emailTaken)
            {
                return UserIdentityConflict.Email;
            }
        }

        return UserIdentityConflict.None;
    }

    /// <summary>把冲突转换为面向用户的业务错误；无冲突返回 null。</summary>
    public static ApiResponse<T>? ToError<T>(UserIdentityConflict conflict) =>
        conflict switch
        {
            UserIdentityConflict.Username => ApiResponse<T>.Error(UsernameTakenMessage, UsernameTakenCode),
            UserIdentityConflict.Email => ApiResponse<T>.Error(EmailTakenMessage, EmailTakenCode),
            _ => null,
        };

    /// <summary>
    /// 写库失败后的兜底：两个请求同时通过预检、后到的撞上唯一索引时，重新查一次冲突并返回友好错误；
    /// 不是唯一约束冲突、或重查也没找到冲突（或重查本身失败）时返回 null，调用方按原逻辑报失败。
    /// 必须在事务回滚之后调用。
    /// </summary>
    public static async Task<ApiResponse<T>?> TryMapWriteFailureAsync<T>(
        ISqlSugarClient db,
        Exception exception,
        string? username,
        string? email,
        string? excludeUserGuid = null
    )
    {
        if (!EmployeeCashierBarcodeService.IsUniqueConstraintViolation(exception))
        {
            return null;
        }

        try
        {
            return ToError<T>(await FindConflictAsync(db, username, email, excludeUserGuid));
        }
        catch
        {
            return null;
        }
    }
}
