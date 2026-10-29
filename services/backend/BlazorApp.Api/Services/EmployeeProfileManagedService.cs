using System.Security.Claims;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services;

/// <summary>
/// 员工个人信息维护（/api/employee-profiles/managed）：Web 页面、用户管理抽屉与移动端共用。
/// 数据范围随身份走：超级管理员 = 全部分店；店长 = 自己管理的分店，且不含本人、不含管理员 / 店长 / 仓库经理。
/// 只开放非敏感白名单字段（手机、性别、职位类型、地址）；银行 / 公积金 / 证件 / 生日仍走原 admin 接口或员工提交审核。
/// </summary>
public sealed class EmployeeProfileManagedService
{
    public const string ScopeForbiddenCode = "EMPLOYEE_PROFILE_MANAGED_SCOPE_FORBIDDEN";
    public const string PositionTypeForbiddenCode = "EMPLOYEE_PROFILE_POSITION_TYPE_FORBIDDEN";

    private readonly SqlSugarContext _context;
    private readonly ICurrentUserService _currentUser;
    private readonly ICurrentUserManageableStoreScopeService _manageableStoreScope;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IRoleService? _roleService;
    private readonly ILogger<EmployeeProfileManagedService> _logger;

    public EmployeeProfileManagedService(
        SqlSugarContext context,
        ICurrentUserService currentUser,
        ICurrentUserManageableStoreScopeService manageableStoreScope,
        IHttpContextAccessor httpContextAccessor,
        ILogger<EmployeeProfileManagedService> logger,
        IRoleService? roleService = null
    )
    {
        _context = context;
        _currentUser = currentUser;
        _manageableStoreScope = manageableStoreScope;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _roleService = roleService;
    }

    public async Task<ApiResponse<EmployeeProfileManagedFilterOptionsDto>> GetFilterOptionsAsync()
    {
        var access = await ResolveAccessAsync();
        if (access is null)
        {
            return ScopeForbidden<EmployeeProfileManagedFilterOptionsDto>();
        }

        var db = _context.Db;
        var storeQuery = db.Queryable<Store>().Where(store => !store.IsDeleted);
        if (!access.IsAdmin)
        {
            var allowed = access.StoreGuids;
            storeQuery = storeQuery.Where(store => allowed.Contains(store.StoreGUID));
        }
        var stores = await storeQuery.OrderBy(store => store.StoreName).ToListAsync();

        var roleQuery = db.Queryable<Role>().Where(role => !role.IsDeleted && role.IsActive);
        if (!access.IsAdmin)
        {
            // 店长不可见管理员 / 店长 / 仓库经理角色，避免通过筛选项探到上级角色。
            var protectedRoles = EmployeeProfileSensitiveChangeService.ProtectedTargetRoleAliases;
            roleQuery = roleQuery.Where(role => !protectedRoles.Contains(role.RoleName));
        }
        var roles = await roleQuery.OrderBy(role => role.RoleName).ToListAsync();

        return ApiResponse<EmployeeProfileManagedFilterOptionsDto>.OK(
            new EmployeeProfileManagedFilterOptionsDto
            {
                Stores = stores
                    .Select(store => new EmployeeProfileFilterStoreDto
                    {
                        StoreGuid = store.StoreGUID,
                        StoreCode = store.StoreCode,
                        StoreName = store.StoreName,
                    })
                    .ToList(),
                Roles = roles
                    .Select(role => new EmployeeProfileFilterRoleDto
                    {
                        RoleGuid = role.RoleGUID,
                        RoleName = role.RoleName,
                    })
                    .ToList(),
            },
            "获取筛选项成功"
        );
    }

    public async Task<ApiResponse<PagedResult<EmployeeProfileListItemDto>>> GetListAsync(
        EmployeeProfileQueryDto query
    )
    {
        var access = await ResolveAccessAsync();
        if (access is null)
        {
            return ScopeForbidden<PagedResult<EmployeeProfileListItemDto>>();
        }

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var emptyPage = ApiResponse<PagedResult<EmployeeProfileListItemDto>>.OK(
            new PagedResult<EmployeeProfileListItemDto>
            {
                Items = new List<EmployeeProfileListItemDto>(),
                Total = 0,
                Page = page,
                PageSize = pageSize,
            },
            "获取员工个人信息列表成功"
        );

        var storeGuid = EmployeeProfileService.Normalize(query.StoreGuid);
        // 请求的分店不在调用者管理范围内：返回空页，不暴露该分店是否存在。
        if (!access.IsAdmin
            && storeGuid is not null
            && !access.StoreGuids.Contains(storeGuid, StringComparer.OrdinalIgnoreCase))
        {
            return emptyPage;
        }

        try
        {
            var db = _context.Db;
            var source = db.Queryable<User>()
                .LeftJoin<EmployeeProfile>(
                    (user, profile) => user.UserGUID == profile.UserGUID && !profile.IsDeleted
                )
                .Where((user, profile) => !user.IsDeleted);

            if (!access.IsAdmin)
            {
                var allowedStoreGuids = access.StoreGuids;
                var protectedRoles = EmployeeProfileSensitiveChangeService.ProtectedTargetRoleAliases;
                var selfGuid = access.UserGuid;
                // 关键逻辑：范围、本人和受保护角色全部在分页前由 SQL 过滤，不把整店员工拉进内存。
                source = source.Where((user, profile) =>
                    user.UserGUID != selfGuid
                    && SqlFunc.Subqueryable<UserStore>()
                        .Where(userStore =>
                            userStore.UserGUID == user.UserGUID
                            && !userStore.IsDeleted
                            && allowedStoreGuids.Contains(userStore.StoreGUID))
                        .Any()
                    && !SqlFunc.Subqueryable<UserRole>()
                        .InnerJoin<Role>((userRole, role) => userRole.RoleGUID == role.RoleGUID)
                        .Where((userRole, role) =>
                            userRole.UserGUID == user.UserGUID
                            && !userRole.IsDeleted
                            && !role.IsDeleted
                            && protectedRoles.Contains(role.RoleName))
                        .Any()
                );
            }

            if (storeGuid is not null)
            {
                source = source.Where((user, profile) =>
                    SqlFunc.Subqueryable<UserStore>()
                        .Where(userStore =>
                            userStore.UserGUID == user.UserGUID
                            && !userStore.IsDeleted
                            && userStore.StoreGUID == storeGuid)
                        .Any()
                );
            }

            var roleGuid = EmployeeProfileService.Normalize(query.RoleGuid);
            if (roleGuid is not null)
            {
                source = source.Where((user, profile) =>
                    SqlFunc.Subqueryable<UserRole>()
                        .Where(userRole =>
                            userRole.UserGUID == user.UserGUID
                            && !userRole.IsDeleted
                            && userRole.RoleGUID == roleGuid)
                        .Any()
                );
            }

            var keyword = EmployeeProfileService.Normalize(query.Search);
            if (keyword is not null)
            {
                source = source.Where((user, profile) =>
                    user.Username.Contains(keyword)
                    || user.Email.Contains(keyword)
                    || (user.FullName != null && user.FullName.Contains(keyword))
                    || (user.GivenName != null && user.GivenName.Contains(keyword))
                    || (user.MiddleName != null && user.MiddleName.Contains(keyword))
                    || (user.FamilyName != null && user.FamilyName.Contains(keyword))
                    || (user.Nickname != null && user.Nickname.Contains(keyword))
                );
            }

            if (query.HasProfile.HasValue)
            {
                source = query.HasProfile.Value
                    ? source.Where((user, profile) => profile.EmployeeInfoId > 0)
                    : source.Where((user, profile) => profile.EmployeeInfoId == 0);
            }

            var employeeType = EmployeeProfileService.ParseEmployeeType(query.EmployeeType);
            if (employeeType.HasValue)
            {
                source = source.Where((user, profile) => profile.EmployeeType == employeeType.Value);
            }

            var total = await source.CountAsync();
            var rows = await source
                .OrderBy((user, profile) => user.Username)
                .Select((user, profile) => new
                {
                    profile.EmployeeInfoId,
                    user.UserGUID,
                    user.Username,
                    user.Email,
                    user.FullName,
                    profile.Phone,
                    profile.BankBSB,
                    profile.BankACC,
                    profile.SuperannuationCompanyName,
                    profile.SuperannuationCompanyCode,
                    profile.SuperannuationAccount,
                    profile.Gender,
                    profile.EmployeeType,
                    profile.Birthday,
                    profile.AvatarUrl,
                    profile.UpdatedAt,
                })
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            // 分店 / 角色标签只对当前页的人批量查一次，避免逐行查询。
            var pageUserGuids = rows.Select(row => row.UserGUID).ToList();
            var storeLabels = await GetStoreLabelsAsync(
                pageUserGuids,
                access.IsAdmin ? null : access.StoreGuids
            );
            var roleLabels = await GetRoleLabelsAsync(pageUserGuids);

            var items = rows
                .Select(row => new EmployeeProfileListItemDto
                {
                    EmployeeInfoId = row.EmployeeInfoId > 0 ? row.EmployeeInfoId : null,
                    UserGUID = row.UserGUID,
                    Username = row.Username,
                    Email = row.Email,
                    FullName = row.FullName,
                    HasProfile = row.EmployeeInfoId > 0,
                    Phone = row.Phone,
                    BankBsb = row.BankBSB,
                    BankAccountNumber = EmployeeProfileService.MaskAccount(row.BankACC),
                    SuperannuationCompanyName = row.SuperannuationCompanyName,
                    SuperannuationCompanyCode = row.SuperannuationCompanyCode,
                    SuperannuationAccountNumber = EmployeeProfileService.MaskAccount(
                        row.SuperannuationAccount
                    ),
                    Gender = EmployeeProfileService.FormatGender(row.Gender),
                    EmployeeType = EmployeeProfileService.FormatEmployeeType(row.EmployeeType),
                    Birthday = row.Birthday,
                    AvatarUrl = row.AvatarUrl,
                    UpdatedAt = row.UpdatedAt,
                    StoreNames = storeLabels.GetValueOrDefault(row.UserGUID) ?? new List<string>(),
                    RoleNames = roleLabels.GetValueOrDefault(row.UserGUID) ?? new List<string>(),
                })
                .ToList();

            return ApiResponse<PagedResult<EmployeeProfileListItemDto>>.OK(
                new PagedResult<EmployeeProfileListItemDto>
                {
                    Items = items,
                    Total = total,
                    Page = page,
                    PageSize = pageSize,
                },
                "获取员工个人信息列表成功"
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取员工个人信息维护列表失败");
            return ApiResponse<PagedResult<EmployeeProfileListItemDto>>.Error(
                "获取员工个人信息列表失败",
                "GET_EMPLOYEE_PROFILES_FAILED"
            );
        }
    }

    public async Task<ApiResponse<EmployeeProfileManagedDetailDto>> GetDetailAsync(string userGuid)
    {
        var access = await ResolveAccessAsync();
        if (access is null || !await CanAccessTargetAsync(access, userGuid))
        {
            return ScopeForbidden<EmployeeProfileManagedDetailDto>();
        }

        try
        {
            return await BuildDetailAsync(access, userGuid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取员工个人信息维护详情失败，UserGUID: {UserGUID}", userGuid);
            return ApiResponse<EmployeeProfileManagedDetailDto>.Error(
                "获取员工个人信息失败",
                "GET_EMPLOYEE_PROFILE_FAILED"
            );
        }
    }

    public async Task<ApiResponse<EmployeeProfileManagedDetailDto>> UpdateAsync(
        string userGuid,
        EmployeeProfileManagedUpsertDto dto
    )
    {
        var access = await ResolveAccessAsync();
        if (access is null || !await CanAccessTargetAsync(access, userGuid))
        {
            return ScopeForbidden<EmployeeProfileManagedDetailDto>();
        }

        try
        {
            var db = _context.Db;
            var actor = _currentUser.GetCurrentUsername();
            var now = DateTime.UtcNow;

            // 与员工自助保存、管理员保存共用生命周期锁，避免并发创建 / 删除资料。
            await using var lifecycleLock = await EmployeeProfileMediaLock
                .AcquireProfileLifecycleAsync(db, userGuid, _logger);

            // 关键逻辑：拿到锁后重新核对范围，防止等锁期间分店 / 角色已被调整。
            access = await ResolveAccessAsync();
            if (access is null || !await CanAccessTargetAsync(access, userGuid))
            {
                return ScopeForbidden<EmployeeProfileManagedDetailDto>();
            }

            await db.Ado.BeginTranAsync();
            try
            {
                var profile = await db.Queryable<EmployeeProfile>()
                    .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);

                if (dto.HasEmploymentType
                    && EmployeeProfileService.ParseEmployeeType(dto.EmploymentType)
                        != profile?.EmployeeType
                    && !await ActorCanEditPositionTypeAsync(access.UserGuid))
                {
                    await db.Ado.RollbackTranAsync();
                    return ApiResponse<EmployeeProfileManagedDetailDto>.Error(
                        "无权修改职位类型",
                        PositionTypeForbiddenCode
                    );
                }

                if (profile is null)
                {
                    profile = new EmployeeProfile
                    {
                        UserGUID = userGuid,
                        CreatedAt = now,
                        CreatedBy = actor,
                        UpdatedAt = now,
                        UpdatedBy = actor,
                        IsDeleted = false,
                    };
                    ApplyWhitelist(profile, dto);
                    profile.EmployeeInfoId = await db.Insertable(profile).ExecuteReturnIdentityAsync();
                }
                else
                {
                    // 只更新白名单列且只更新「请求里带了」的列，绝不写敏感字段或 revision。
                    var update = db.Updateable<EmployeeProfile>()
                        .SetColumns(item => item.UpdatedAt == now)
                        .SetColumns(item => item.UpdatedBy == actor)
                        .SetColumns(item => item.IsDeleted == false);
                    if (dto.HasPhone)
                    {
                        var phone = EmployeeProfileService.Normalize(dto.Phone);
                        update = update.SetColumns(item => item.Phone == phone);
                    }
                    if (dto.HasGender)
                    {
                        var gender = EmployeeProfileService.ParseGender(dto.Gender);
                        update = update.SetColumns(item => item.Gender == gender);
                    }
                    if (dto.HasEmploymentType)
                    {
                        var employeeType = EmployeeProfileService.ParseEmployeeType(dto.EmploymentType);
                        update = update.SetColumns(item => item.EmployeeType == employeeType);
                    }
                    if (dto.HasAddress)
                    {
                        var address = EmployeeProfileService.Normalize(dto.Address);
                        update = update.SetColumns(item => item.Address == address);
                    }
                    await update
                        .Where(item => item.EmployeeInfoId == profile.EmployeeInfoId)
                        .ExecuteCommandAsync();
                }

                await db.Ado.CommitTranAsync();
            }
            catch
            {
                await db.Ado.RollbackTranAsync();
                throw;
            }

            return await BuildDetailAsync(access, userGuid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存员工个人信息失败（managed），UserGUID: {UserGUID}", userGuid);
            return ApiResponse<EmployeeProfileManagedDetailDto>.Error(
                "保存员工个人信息失败",
                "UPSERT_EMPLOYEE_PROFILE_FAILED"
            );
        }
    }

    private static void ApplyWhitelist(EmployeeProfile profile, EmployeeProfileManagedUpsertDto dto)
    {
        if (dto.HasPhone)
        {
            profile.Phone = EmployeeProfileService.Normalize(dto.Phone);
        }
        if (dto.HasGender)
        {
            profile.Gender = EmployeeProfileService.ParseGender(dto.Gender);
        }
        if (dto.HasEmploymentType)
        {
            profile.EmployeeType = EmployeeProfileService.ParseEmployeeType(dto.EmploymentType);
        }
        if (dto.HasAddress)
        {
            profile.Address = EmployeeProfileService.Normalize(dto.Address);
        }
    }

    private async Task<ApiResponse<EmployeeProfileManagedDetailDto>> BuildDetailAsync(
        ManagedAccess access,
        string userGuid
    )
    {
        var db = _context.Db;
        var user = await db.Queryable<User>()
            .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
        if (user is null)
        {
            return ApiResponse<EmployeeProfileManagedDetailDto>.Error("用户不存在", "USER_NOT_FOUND");
        }
        var profile = await db.Queryable<EmployeeProfile>()
            .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);

        var guids = new List<string> { userGuid };
        var storeLabels = await GetStoreLabelsAsync(guids, access.IsAdmin ? null : access.StoreGuids);
        var roleLabels = await GetRoleLabelsAsync(guids);

        return ApiResponse<EmployeeProfileManagedDetailDto>.OK(
            new EmployeeProfileManagedDetailDto
            {
                EmployeeInfoId = profile?.EmployeeInfoId,
                UserGUID = user.UserGUID,
                Username = user.Username,
                Email = user.Email,
                FullName = user.FullName,
                HasProfile = profile is not null,
                StoreNames = storeLabels.GetValueOrDefault(userGuid) ?? new List<string>(),
                RoleNames = roleLabels.GetValueOrDefault(userGuid) ?? new List<string>(),
                Phone = profile?.Phone,
                Gender = EmployeeProfileService.FormatGender(profile?.Gender),
                EmploymentType = EmployeeProfileService.FormatEmployeeType(profile?.EmployeeType),
                Address = profile?.Address,
                AvatarUrl = profile?.AvatarUrl,
                BankBsb = profile?.BankBSB,
                BankAccountNumber = EmployeeProfileService.MaskAccount(profile?.BankACC),
                SuperannuationCompanyName = profile?.SuperannuationCompanyName,
                SuperannuationCompanyCode = profile?.SuperannuationCompanyCode,
                SuperannuationAccountNumber = EmployeeProfileService.MaskAccount(
                    profile?.SuperannuationAccount
                ),
                IdentityType = profile?.IdentityType,
                IdentityId = EmployeeProfileService.MaskAccount(profile?.IdentityId),
                HasBirthday = profile?.Birthday is not null,
                CanEditEmploymentType = await ActorCanEditPositionTypeAsync(access.UserGuid),
                UpdatedAt = profile?.UpdatedAt,
                UpdatedBy = profile?.UpdatedBy,
            },
            "获取员工个人信息成功"
        );
    }

    private async Task<bool> ActorCanEditPositionTypeAsync(string actorGuid)
    {
        if (_roleService is null || string.IsNullOrWhiteSpace(actorGuid))
        {
            return false;
        }
        return (await _roleService.UserHasPermissionAsync(
            actorGuid,
            Permissions.EmployeeProfiles.EditPositionType
        )).Data == true;
    }

    /// <summary>
    /// 与敏感资料审核同一套身份判定：超级管理员 = 全部；店长（角色别名）且有管理分店 = 限管理分店；其他一律无权。
    /// scope.IsAdmin 含仓库经理，这里必须再核对真实角色，不能据此放宽。
    /// </summary>
    private async Task<ManagedAccess?> ResolveAccessAsync()
    {
        var scope = await _manageableStoreScope.GetScopeAsync();
        var principal = _httpContextAccessor.HttpContext?.User;
        if (!scope.IsAllowed || !scope.IsAuthenticated || principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }
        if (HasAnyRole(principal, Permissions.SuperAdminRoleNames))
        {
            return new ManagedAccess(true, scope.UserGuid, Array.Empty<string>());
        }
        if (!HasAnyRole(principal, EmployeeProfileSensitiveChangeService.StoreManagerRoleAliases)
            || scope.StoreGuids.Count == 0)
        {
            return null;
        }
        return new ManagedAccess(false, scope.UserGuid, scope.StoreGuids);
    }

    private async Task<bool> CanAccessTargetAsync(ManagedAccess access, string targetUserGuid)
    {
        if (string.IsNullOrWhiteSpace(targetUserGuid))
        {
            return false;
        }
        if (access.IsAdmin)
        {
            return true;
        }
        if (targetUserGuid.Equals(access.UserGuid, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var db = _context.Db;
        var allowed = access.StoreGuids;
        var inManagedStore = await db.Queryable<UserStore>()
            .AnyAsync(item => item.UserGUID == targetUserGuid
                && !item.IsDeleted
                && allowed.Contains(item.StoreGUID));
        if (!inManagedStore)
        {
            return false;
        }
        var protectedRoles = EmployeeProfileSensitiveChangeService.ProtectedTargetRoleAliases;
        var isProtected = await db.Queryable<UserRole, Role>((userRole, role) =>
                userRole.RoleGUID == role.RoleGUID)
            .Where((userRole, role) =>
                userRole.UserGUID == targetUserGuid
                && !userRole.IsDeleted
                && !role.IsDeleted
                && protectedRoles.Contains(role.RoleName))
            .AnyAsync();
        return !isProtected;
    }

    private async Task<Dictionary<string, List<string>>> GetStoreLabelsAsync(
        IReadOnlyCollection<string> userGuids,
        IReadOnlyList<string>? allowedStoreGuids
    )
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (userGuids.Count == 0)
        {
            return result;
        }
        var query = _context.Db.Queryable<UserStore, Store>((userStore, store) =>
                userStore.StoreGUID == store.StoreGUID)
            .Where((userStore, store) =>
                userGuids.Contains(userStore.UserGUID) && !userStore.IsDeleted && !store.IsDeleted);
        if (allowedStoreGuids is not null)
        {
            query = query.Where((userStore, store) => allowedStoreGuids.Contains(userStore.StoreGUID));
        }
        var rows = await query
            .Select((userStore, store) => new { userStore.UserGUID, store.StoreName })
            .ToListAsync();
        foreach (var group in rows.GroupBy(item => item.UserGUID, StringComparer.OrdinalIgnoreCase))
        {
            result[group.Key] = group
                .Select(item => item.StoreName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        return result;
    }

    private async Task<Dictionary<string, List<string>>> GetRoleLabelsAsync(
        IReadOnlyCollection<string> userGuids
    )
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (userGuids.Count == 0)
        {
            return result;
        }
        var rows = await _context.Db.Queryable<UserRole, Role>((userRole, role) =>
                userRole.RoleGUID == role.RoleGUID)
            .Where((userRole, role) =>
                userGuids.Contains(userRole.UserGUID) && !userRole.IsDeleted && !role.IsDeleted)
            .Select((userRole, role) => new { userRole.UserGUID, role.RoleName })
            .ToListAsync();
        foreach (var group in rows.GroupBy(item => item.UserGUID, StringComparer.OrdinalIgnoreCase))
        {
            result[group.Key] = group
                .Select(item => item.RoleName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        return result;
    }

    private static bool HasAnyRole(ClaimsPrincipal user, IReadOnlyCollection<string> roles) =>
        user.Claims.Any(claim =>
            (claim.Type.Equals(ClaimTypes.Role, StringComparison.OrdinalIgnoreCase)
                || claim.Type.Equals("role", StringComparison.OrdinalIgnoreCase))
            && roles.Contains(claim.Value, StringComparer.OrdinalIgnoreCase)
        );

    private static ApiResponse<T> ScopeForbidden<T>() =>
        ApiResponse<T>.Error("当前账号无权维护该员工的个人信息", ScopeForbiddenCode);

    private sealed record ManagedAccess(bool IsAdmin, string UserGuid, IReadOnlyList<string> StoreGuids);
}
