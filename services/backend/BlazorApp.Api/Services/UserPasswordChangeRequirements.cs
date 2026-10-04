using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services;

/// <summary>
/// 「须先改密」标记的读写。调用方负责事务：建号、重置密码与标记写入应处在同一事务内。
/// </summary>
internal static class UserPasswordChangeRequirements
{
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
