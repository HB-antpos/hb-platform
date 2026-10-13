using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.Attendance;

/// <summary>解析「谁能审批某门店的考勤申请」，用于把待办通知发给真正有权处理的人。</summary>
public interface IAttendanceApproverResolver
{
    /// <summary>
    /// 返回能审批该门店考勤申请的用户（启用且未删除，不含申请人本人），按 UserGUID 排序。
    /// </summary>
    Task<IReadOnlyList<string>> ResolveApproverUserGuidsAsync(
        string storeCode,
        string applicantUserGuid,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 与审批接口的真实授权口径保持一致，同时满足三条才算审批人：
/// (a) 持有 <see cref="Permissions.Attendance.Approval.ReviewManagedStore"/>（含超级管理员与直接授权，经
///     <see cref="IRoleService.UserHasPermissionAsync"/> 逐人判断）；
/// (b) 范围覆盖该门店——角色属于超级管理员 / 仓库经理别名（不限门店），或角色属于店长别名且在该门店有主分店关联
///     （<c>UserStore.IsPrimary</c>，与 <c>CurrentUserManageableStoreScopeService</c> 的可管理分店同口径，非主分店的店长不算）；
/// (c) 不是申请人本人（接口会以 SELF_REVIEW_FORBIDDEN 拒绝自审）。
/// 不能按「角色模板」或 <c>NotificationAudience.ForStore</c> 发：生产里这个权限码主要靠直接授权，
/// ForStore 按全部关联门店算，会把非主分店的店长和申请人自己也算进去，还会漏掉管理员与仓库经理。
/// 先用 SQL 按角色别名 + 主分店缩小到候选（通常十来人），再逐人查权限，不会全表逐人查。
/// </summary>
internal sealed class AttendanceApproverResolver(
    SqlSugarContext context,
    IRoleService roleService,
    ILogger<AttendanceApproverResolver> logger
) : IAttendanceApproverResolver
{
    private readonly ISqlSugarClient _db = context.Db;

    // 角色名按小写比较：SQL Server 默认排序规则本就不区分大小写，这里让 SQLite 等库的行为与之一致，
    // 也与 CurrentUserManageableStoreScopeService 里 OrdinalIgnoreCase 的角色匹配同口径。
    // 注意：SqlSugar 表达式里不能引用 private 字段，下面查询用的是方法内的局部变量，不要改回静态字段。
    private static string[] ToLowerDistinct(IEnumerable<string> names) => names
        .Select(name => name.ToLowerInvariant())
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    public async Task<IReadOnlyList<string>> ResolveApproverUserGuidsAsync(
        string storeCode,
        string applicantUserGuid,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = storeCode?.Trim();
        if (string.IsNullOrEmpty(normalizedStoreCode))
        {
            return [];
        }

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // (b) 不限门店的角色：超级管理员 / 仓库经理。
        candidates.UnionWith(await BuildUnrestrictedCandidateQuery(_db).ToListAsync(cancellationToken));

        // (b) 店长：只认在该门店有主分店关联的人。
        candidates.UnionWith(await BuildStoreManagerCandidateQuery(_db, normalizedStoreCode)
            .ToListAsync(cancellationToken));

        // (c) 申请人不能审自己的申请。
        var applicant = applicantUserGuid?.Trim();
        if (!string.IsNullOrEmpty(applicant))
        {
            candidates.Remove(applicant);
        }

        // (a) 逐人确认持有审批权限（候选已缩到个位数到十来人）。
        var approvers = new List<string>();
        foreach (var userGuid in candidates.OrderBy(guid => guid, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await HasReviewPermissionAsync(userGuid))
            {
                approvers.Add(userGuid);
            }
        }

        return approvers;
    }

    /// <summary>不限门店的候选：启用的超级管理员 / 仓库经理角色成员（角色与账号均启用且未删除）。</summary>
    internal static ISugarQueryable<string> BuildUnrestrictedCandidateQuery(ISqlSugarClient db)
    {
        var roleNamesLower = ToLowerDistinct(
            Permissions.SuperAdminRoleNames.Concat(Permissions.WarehouseManagerRoleNames));
        return db.Queryable<UserRole>()
            .InnerJoin<Role>((userRole, role) => userRole.RoleGUID == role.RoleGUID)
            .InnerJoin<User>((userRole, role, user) => userRole.UserGUID == user.UserGUID)
            .Where((userRole, role, user) =>
                !userRole.IsDeleted
                && !role.IsDeleted
                && role.IsActive
                && user.IsActive
                && !user.IsDeleted
                && roleNamesLower.Contains(role.RoleName.ToLower()))
            .Select((userRole, role, user) => user.UserGUID)
            .Distinct();
    }

    /// <summary>店长候选：店长角色成员，且在该门店有主分店关联（UserStore.IsPrimary），非主分店不算。</summary>
    internal static ISugarQueryable<string> BuildStoreManagerCandidateQuery(ISqlSugarClient db, string storeCode)
    {
        var roleNamesLower = ToLowerDistinct(Permissions.StoreManagerRoleNames);
        return db.Queryable<UserRole>()
            .InnerJoin<Role>((userRole, role) => userRole.RoleGUID == role.RoleGUID)
            .InnerJoin<User>((userRole, role, user) => userRole.UserGUID == user.UserGUID)
            .InnerJoin<UserStore>((userRole, role, user, link) => link.UserGUID == user.UserGUID)
            .InnerJoin<Store>((userRole, role, user, link, store) => link.StoreGUID == store.StoreGUID)
            .Where((userRole, role, user, link, store) =>
                !userRole.IsDeleted
                && !role.IsDeleted
                && role.IsActive
                && user.IsActive
                && !user.IsDeleted
                && roleNamesLower.Contains(role.RoleName.ToLower())
                && link.IsPrimary
                && !link.IsDeleted
                && !store.IsDeleted
                && store.StoreCode == storeCode)
            .Select((userRole, role, user, link, store) => user.UserGUID)
            .Distinct();
    }

    private async Task<bool> HasReviewPermissionAsync(string userGuid)
    {
        var result = await roleService.UserHasPermissionAsync(
            userGuid,
            Permissions.Attendance.Approval.ReviewManagedStore);
        if (!result.Success)
        {
            // 权限查询失败按「不发」处理：宁可少发，也不把待办发给无权审批的人。
            logger.LogWarning(
                "考勤审批人权限判断失败，跳过该用户: User={UserGuid}, Message={Message}",
                userGuid,
                result.Message);
            return false;
        }

        return result.Data;
    }
}
