using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services;

/// <summary>
/// 「不允许登录 Web / App」标记的读写。有记录＝禁止登录；调用方负责事务（建号与标记写入应同事务）。
/// </summary>
internal static class UserLoginRestrictions
{
    /// <summary>禁止该账号登录；已存在则保持原记录不变（幂等）。</summary>
    public static async Task RestrictAsync(
        ISqlSugarClient db,
        string userGuid,
        string? actor,
        DateTime nowUtc
    )
    {
        if (await IsRestrictedAsync(db, userGuid))
        {
            return;
        }
        await db.Insertable(new UserLoginRestriction
        {
            UserGUID = userGuid,
            RestrictedAtUtc = nowUtc,
            RestrictedBy = actor,
        }).ExecuteCommandAsync();
    }

    /// <summary>允许该账号登录（删除标记）；没有标记时什么都不做。</summary>
    public static async Task AllowAsync(ISqlSugarClient db, string userGuid)
    {
        await db.Deleteable<UserLoginRestriction>()
            .Where(item => item.UserGUID == userGuid)
            .ExecuteCommandAsync();
    }

    public static Task<bool> IsRestrictedAsync(ISqlSugarClient db, string userGuid) =>
        db.Queryable<UserLoginRestriction>().AnyAsync(item => item.UserGUID == userGuid);

    /// <summary>批量查询，供员工列表 / 详情标注「是否允许登录」。</summary>
    public static async Task<HashSet<string>> GetRestrictedUserGuidsAsync(
        ISqlSugarClient db,
        IReadOnlyCollection<string> userGuids
    )
    {
        if (userGuids.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
        var ids = userGuids.Distinct(StringComparer.Ordinal).ToList();
        var restricted = await db.Queryable<UserLoginRestriction>()
            .Where(item => ids.Contains(item.UserGUID))
            .Select(item => item.UserGUID)
            .ToListAsync();
        return restricted.ToHashSet(StringComparer.Ordinal);
    }
}
