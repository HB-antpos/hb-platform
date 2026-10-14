using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.FaceMatch;

/// <summary>人脸比对各查询共用的门店范围判定。</summary>
internal static class FaceMatchScope
{
    /// <summary>管理员返回 null（不限门店）；其他人只返回自己关联且未删除的门店编号。</summary>
    public static async Task<HashSet<string>?> GetAllowedStoresAsync(
        ISqlSugarClient db, FaceMatchActor actor, CancellationToken cancellationToken)
    {
        if (actor.IsAdmin)
        {
            return null;
        }

        var codes = await db.Queryable<UserStore>()
            .InnerJoin<Store>((link, store) => link.StoreGUID == store.StoreGUID)
            .Where((link, store) => link.UserGUID == actor.Id && !link.IsDeleted && !store.IsDeleted)
            .Select((link, store) => store.StoreCode)
            .ToListAsync(cancellationToken);
        return codes.ToHashSet(StringComparer.Ordinal);
    }
}
