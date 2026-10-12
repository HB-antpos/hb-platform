using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.Notifications;

/// <summary>
/// 把接收范围展开成用户列表：指定用户与门店成员取并集，统一过滤掉停用和已删除的账号。
/// 门店成员按 UserStore 关联计算（不只是主分店）；带权限要求时逐人复用 <see cref="IRoleService.UserHasPermissionAsync"/>，
/// 与接口鉴权同一口径，单店成员规模在几十人以内。
/// </summary>
internal sealed class NotificationRecipientResolver(
    SqlSugarContext context,
    IRoleService roleService,
    ILogger<NotificationRecipientResolver> logger
) : INotificationRecipientResolver
{
    private readonly ISqlSugarClient _db = context.Db;

    public async Task<IReadOnlyList<string>> ResolveAsync(
        NotificationAudience audience,
        CancellationToken cancellationToken = default
    )
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var explicitUserGuids = audience.UserGuids
            .Where(guid => !string.IsNullOrWhiteSpace(guid))
            .Select(guid => guid.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (explicitUserGuids.Count > 0)
        {
            var activeExplicit = await _db.Queryable<User>()
                .Where(user => explicitUserGuids.Contains(user.UserGUID) && user.IsActive && !user.IsDeleted)
                .Select(user => user.UserGUID)
                .ToListAsync(cancellationToken);
            candidates.UnionWith(activeExplicit);
        }

        var storeCode = audience.StoreCode?.Trim();
        if (!string.IsNullOrEmpty(storeCode))
        {
            var storeMembers = await _db.Queryable<UserStore>()
                .InnerJoin<Store>((link, store) => link.StoreGUID == store.StoreGUID)
                .InnerJoin<User>((link, store, user) => link.UserGUID == user.UserGUID)
                .Where((link, store, user) =>
                    store.StoreCode == storeCode
                    && !store.IsDeleted
                    && !link.IsDeleted
                    && user.IsActive
                    && !user.IsDeleted)
                .Select((link, store, user) => user.UserGUID)
                .Distinct()
                .ToListAsync(cancellationToken);

            var permission = audience.RequiredPermission?.Trim();
            foreach (var userGuid in storeMembers)
            {
                if (string.IsNullOrEmpty(permission) || await HasPermissionAsync(userGuid, permission))
                {
                    candidates.Add(userGuid);
                }
            }
        }

        return candidates.OrderBy(guid => guid, StringComparer.Ordinal).ToList();
    }

    private async Task<bool> HasPermissionAsync(string userGuid, string permission)
    {
        var result = await roleService.UserHasPermissionAsync(userGuid, permission);
        if (!result.Success)
        {
            // 权限查询失败按「不发」处理：宁可漏发，也不把门店消息发给无权限的人。
            logger.LogWarning(
                "通知接收人权限判断失败，跳过该用户: User={UserGuid}, Permission={Permission}, Message={Message}",
                userGuid,
                permission,
                result.Message
            );
            return false;
        }

        return result.Data;
    }
}
