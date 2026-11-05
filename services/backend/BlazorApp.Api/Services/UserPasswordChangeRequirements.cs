using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services;

/// <summary>
/// 「须先改密」标记的读写。调用方负责事务：建号、重置密码与标记写入应处在同一事务内。
/// </summary>
internal static class UserPasswordChangeRequirements
{
    /// <summary>
    /// 系统生成并邮件发出的临时密码的有效期（小时），与验证码邀请的 72 小时一致。
    /// 随机密码只有 4 位，不设失效时间就等于一直可被在线猜测。
    /// </summary>
    public const int EmailedPasswordTtlHours = 72;

    /// <summary>是否为「系统生成并邮件发出的临时密码」（受有效期限制）。</summary>
    public static bool IsEmailedReason(string? reason) =>
        reason is UserPasswordChangeRequirement.ReasonCreatedEmailed or UserPasswordChangeRequirement.ReasonResetEmailed;

    /// <summary>
    /// 该标记对应的邮件临时密码是否已过期：必须同时满足「原因是邮件临时密码」「已超过有效期」。
    /// 店长手输的密码（created / reset）与没有标记的账号都不会过期。
    /// </summary>
    internal static bool IsEmailedPasswordExpired(UserPasswordChangeRequirement? row, DateTime nowUtc) =>
        row != null
        && IsEmailedReason(row.Reason)
        && nowUtc >= row.RequiredAtUtc.AddHours(EmailedPasswordTtlHours);

    public static async Task<bool> IsEmailedPasswordExpiredAsync(ISqlSugarClient db, string userGuid, DateTime nowUtc) =>
        IsEmailedPasswordExpired(await GetAsync(db, userGuid), nowUtc);

    /// <summary>读取标记整行（没有则 null）；发信失败回滚时用来还原写入前的原样。</summary>
    public static Task<UserPasswordChangeRequirement?> GetAsync(ISqlSugarClient db, string userGuid) =>
        db.Queryable<UserPasswordChangeRequirement>()
            .Where(item => item.UserGUID == userGuid)
            .FirstAsync()!;

    /// <summary>
    /// 把标记还原成 <paramref name="previous"/>：原来没有就删除，原来有就按原行（原因、时间、操作人）写回，
    /// 不能只保留「有标记」这个事实——否则原本不过期的手输密码会被换成会过期的临时密码标记。
    /// </summary>
    public static async Task RestoreAsync(ISqlSugarClient db, string userGuid, UserPasswordChangeRequirement? previous)
    {
        await ClearAsync(db, userGuid);
        if (previous != null)
        {
            await db.Insertable(previous).ExecuteCommandAsync();
        }
    }

    /// <summary>写入或刷新标记；同一账号重复重置时只保留最新一条。</summary>
    public static async Task RequireAsync(
        ISqlSugarClient db,
        string userGuid,
        string reason,
        string? actor,
        DateTime nowUtc
    )
    {
        await db.Deleteable<UserPasswordChangeRequirement>()
            .Where(item => item.UserGUID == userGuid)
            .ExecuteCommandAsync();
        await db.Insertable(new UserPasswordChangeRequirement
        {
            UserGUID = userGuid,
            Reason = reason,
            RequiredAtUtc = nowUtc,
            RequiredBy = actor,
        }).ExecuteCommandAsync();
    }

    public static async Task ClearAsync(ISqlSugarClient db, string userGuid)
    {
        await db.Deleteable<UserPasswordChangeRequirement>()
            .Where(item => item.UserGUID == userGuid)
            .ExecuteCommandAsync();
    }

    public static Task<bool> IsRequiredAsync(ISqlSugarClient db, string userGuid) =>
        db.Queryable<UserPasswordChangeRequirement>().AnyAsync(item => item.UserGUID == userGuid);

    /// <summary>批量查询，供员工列表标注「待首次登录」。</summary>
    public static async Task<HashSet<string>> GetRequiredUserGuidsAsync(
        ISqlSugarClient db,
        IReadOnlyCollection<string> userGuids
    )
    {
        if (userGuids.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
        var ids = userGuids.Distinct(StringComparer.Ordinal).ToList();
        var required = await db.Queryable<UserPasswordChangeRequirement>()
            .Where(item => ids.Contains(item.UserGUID))
            .Select(item => item.UserGUID)
            .ToListAsync();
        return required.ToHashSet(StringComparer.Ordinal);
    }
}
