using System.Data;
using System.Security.Cryptography;
using System.Text;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Utils;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Services
{
    /// <summary>
    /// 用户管理服务实现
    /// 🧑‍💼 提供用户数据的CRUD操作和权限管理功能
    /// </summary>
    public class UserService : IUserService
    {
        private readonly SqlSugarContext _context;
        private readonly ILogger<UserService> _logger;
        private readonly ICurrentUserManageableStoreScopeService? _manageableStoreScopeService;
        private readonly EmployeeProfileSensitiveChangeService? _sensitiveChangeService;

        public UserService(
            SqlSugarContext context,
            ILogger<UserService> logger,
            ICurrentUserManageableStoreScopeService? manageableStoreScopeService = null,
            EmployeeProfileSensitiveChangeService? sensitiveChangeService = null
        )
        {
            _context = context;
            _logger = logger;
            _manageableStoreScopeService = manageableStoreScopeService;
            _sensitiveChangeService = sensitiveChangeService;
        }

        /// <summary>
        /// 获取用户列表（分页）
        /// </summary>
        public async Task<ApiResponse<PagedResult<UserDto>>> GetUsersAsync(UserQueryDto query)
        {
            try
            {
                var db = _context.Db;
                var storeScope = await ResolveUserListStoreScopeAsync(db);
                if (storeScope.ReturnEmptyPage)
                {
                    return ApiResponse<PagedResult<UserDto>>.OK(
                        CreateEmptyPagedResult(query),
                        "获取用户列表成功"
                    );
                }
                if (IsOutOfScopedStoreQuery(storeScope, query.StoreGuid))
                {
                    return ApiResponse<PagedResult<UserDto>>.OK(
                        CreateEmptyPagedResult(query),
                        "获取用户列表成功"
                    );
                }

                //使用多对多导航查询
                var userQuery = db.Queryable<User>()
                    .Where(user => !user.IsDeleted)
                    .Includes(u => u.Roles)
                    .Includes(u => u.Stores);

                if (storeScope.StoreGuids.Count > 0)
                {
                    var scopedStoreGuids = storeScope.StoreGuids.ToArray();
                    var scopedUserGuids = await db.Queryable<UserStore>()
                        .Where(us => !us.IsDeleted && scopedStoreGuids.Contains(us.StoreGUID))
                        .Select(us => us.UserGUID)
                        .Distinct()
                        .ToListAsync();

                    if (scopedUserGuids.Count == 0)
                    {
                        return ApiResponse<PagedResult<UserDto>>.OK(
                            CreateEmptyPagedResult(query),
                            "获取用户列表成功"
                        );
                    }

                    userQuery = userQuery.Where(u => scopedUserGuids.Contains(u.UserGUID));

                    var excludedHighPrivilegeGuids = await ResolveScopedHighPrivilegeUserGuidsAsync(
                        db
                    );
                    if (excludedHighPrivilegeGuids.Length > 0)
                    {
                        userQuery = userQuery.Where(u => !excludedHighPrivilegeGuids.Contains(u.UserGUID));
                    }
                }

                // 搜索条件
                if (!string.IsNullOrEmpty(query.SearchKeyword))
                {
                    userQuery = userQuery.Where(u =>
                        u.Username.Contains(query.SearchKeyword)
                        || u.Email.Contains(query.SearchKeyword)
                        || (u.FullName != null && u.FullName.Contains(query.SearchKeyword))
                    );
                }

                // 状态筛选
                if (query.IsActive.HasValue)
                {
                    userQuery = userQuery.Where(u => u.IsActive == query.IsActive.Value);
                }

                // 角色筛选 - 优化为子查询
                if (!string.IsNullOrEmpty(query.RoleGuid))
                {
                    var roleUserGuids = await db.Queryable<UserRole>()
                        .InnerJoin<Role>((userRole, role) => userRole.RoleGUID == role.RoleGUID)
                        .Where((userRole, role) =>
                            userRole.RoleGUID == query.RoleGuid
                            && !userRole.IsDeleted
                            && !role.IsDeleted
                            && role.IsActive
                        )
                        .Select((userRole, role) => userRole.UserGUID)
                        .Distinct()
                        .ToListAsync();
                    userQuery = userQuery.Where(user => roleUserGuids.Contains(user.UserGUID));
                }

                // 分店筛选 - 优化为子查询
                if (!string.IsNullOrEmpty(query.StoreGuid))
                {
                    var storeUserGuids = await db.Queryable<UserStore>()
                        .Where(userStore =>
                            userStore.StoreGUID == query.StoreGuid && !userStore.IsDeleted
                        )
                        .Select(userStore => userStore.UserGUID)
                        .Distinct()
                        .ToListAsync();
                    userQuery = userQuery.Where(user => storeUserGuids.Contains(user.UserGUID));
                }

                // 排序
                if (!string.IsNullOrEmpty(query.SortBy))
                {
                    if (query.SortDirection?.ToLower() == "asc")
                    {
                        userQuery = query.SortBy.ToLower() switch
                        {
                            "username" => userQuery.OrderBy(u => u.Username),
                            "email" => userQuery.OrderBy(u => u.Email),
                            "fullname" => userQuery.OrderBy(u => u.FullName),
                            "lastloginat" => userQuery.OrderBy(u => u.LastLoginAt),
                            "createdat" => userQuery.OrderBy(u => u.CreatedAt),
                            _ => userQuery.OrderBy(u => u.CreatedAt),
                        };
                    }
                    else
                    {
                        userQuery = query.SortBy.ToLower() switch
                        {
                            "username" => userQuery.OrderByDescending(u => u.Username),
                            "email" => userQuery.OrderByDescending(u => u.Email),
                            "fullname" => userQuery.OrderByDescending(u => u.FullName),
                            "lastloginat" => userQuery.OrderByDescending(u => u.LastLoginAt),
                            "createdat" => userQuery.OrderByDescending(u => u.CreatedAt),
                            _ => userQuery.OrderByDescending(u => u.CreatedAt),
                        };
                    }
                }
                else
                {
                    userQuery = userQuery.OrderByDescending(u => u.CreatedAt);
                }

                // 获取总数
                var total = await userQuery.CountAsync();

                // 分页查询
                var users = await userQuery
                    .Skip((query.Page - 1) * query.PageSize)
                    .Take(query.PageSize)
                    .ToListAsync();
                var pageUserGuids = users.Select(user => user.UserGUID).ToArray();
                var visibleRoleRelations = pageUserGuids.Length == 0
                    ? new List<UserRole>()
                    : await db.Queryable<UserRole>()
                        .Where(userRole =>
                            pageUserGuids.Contains(userRole.UserGUID) && !userRole.IsDeleted
                        )
                        .ToListAsync();
                var visibleRoleGuids = visibleRoleRelations
                    .Select(userRole => userRole.RoleGUID)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var visibleRolesByGuid = (visibleRoleGuids.Length == 0
                        ? new List<Role>()
                        : await db.Queryable<Role>()
                            .Where(role =>
                                visibleRoleGuids.Contains(role.RoleGUID)
                                && !role.IsDeleted
                                && role.IsActive
                            )
                            .ToListAsync())
                    .ToDictionary(role => role.RoleGUID, StringComparer.OrdinalIgnoreCase);
                var roleGuidsByUser = visibleRoleRelations
                    .GroupBy(item => item.UserGUID, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Select(item => item.RoleGUID).ToArray(),
                        StringComparer.OrdinalIgnoreCase
                    );

                var visibleStoreRelations = pageUserGuids.Length == 0
                    ? new List<UserStore>()
                    : await db.Queryable<UserStore>()
                        .Where(userStore =>
                            pageUserGuids.Contains(userStore.UserGUID) && !userStore.IsDeleted
                        )
                        .ToListAsync();
                var visibleStoreGuids = visibleStoreRelations
                    .Select(userStore => userStore.StoreGUID)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var visibleStoresByGuid = (visibleStoreGuids.Length == 0
                        ? new List<Store>()
                        : await db.Queryable<Store>()
                            .Where(store =>
                                visibleStoreGuids.Contains(store.StoreGUID) && !store.IsDeleted
                            )
                            .ToListAsync())
                    .ToDictionary(store => store.StoreGUID, StringComparer.OrdinalIgnoreCase);
                var storeRelationsByUser = visibleStoreRelations
                    .GroupBy(item => item.UserGUID, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.ToArray(),
                        StringComparer.OrdinalIgnoreCase
                    );
                //内存中将用户转换为UserDto
                var userDtos = new List<UserDto>();

                foreach (var user in users)
                {
                    var visibleRoles = roleGuidsByUser.TryGetValue(user.UserGUID, out var roleGuids)
                        ? roleGuids
                            .Where(visibleRolesByGuid.ContainsKey)
                            .Select(roleGuid => visibleRolesByGuid[roleGuid])
                            .ToList()
                        : new List<Role>();
                    var visibleStores = storeRelationsByUser.TryGetValue(
                        user.UserGUID,
                        out var storeRelations
                    )
                        ? storeRelations
                            .Where(relation => visibleStoresByGuid.ContainsKey(relation.StoreGUID))
                            .Select(relation => new
                            {
                                Store = visibleStoresByGuid[relation.StoreGUID],
                                relation.IsPrimary,
                                relation.AssignedAt,
                            })
                            .ToList()
                        : [];
                    var userDto = new UserDto
                    {
                        UserGUID = user.UserGUID,
                        Username = user.Username,
                        Email = user.Email,
                        FullName = user.FullName,
                        LastLoginAt = user.LastLoginAt,
                        LastLoginIp = user.LastLoginIp,
                        IsActive = user.IsActive,
                        CreatedAt = user.CreatedAt,
                        UpdatedAt = user.UpdatedAt ?? user.CreatedAt,
                        RoleNames = visibleRoles
                            .Select(r => r.RoleName)
                            .ToList(),
                        StoreNames = visibleStores
                            .Where(item =>
                                storeScope.StoreGuids.Count == 0
                                || storeScope.StoreGuids.Contains(item.Store.StoreGUID)
                            )
                            .Select(item => item.Store.StoreName)
                            .ToList(),
                        Roles = visibleRoles
                            .Select(r => new RoleDto
                            {
                                RoleGUID = r.RoleGUID,
                                RoleName = r.RoleName,
                                Description = r.Description,
                                IsActive = r.IsActive,
                                CreatedAt = r.CreatedAt,
                                UpdatedAt = r.UpdatedAt ?? r.CreatedAt,
                            })
                            .ToList(),
                        Stores = visibleStores
                            .Where(item =>
                                storeScope.StoreGuids.Count == 0
                                || storeScope.StoreGuids.Contains(item.Store.StoreGUID)
                            )
                            .Select(item => new UserStoreDto
                            {
                                StoreGUID = item.Store.StoreGUID,
                                StoreName = item.Store.StoreName,
                                StoreCode = item.Store.StoreCode,
                                IsActive = item.Store.IsActive,
                                IsPrimary = item.IsPrimary,
                                AssignedAt = item.AssignedAt,
                            })
                            .ToList(),
                    };
                    userDtos.Add(userDto);
                }

                var result = new PagedResult<UserDto>
                {
                    Items = userDtos,
                    Total = total,
                    Page = query.Page,
                    PageSize = query.PageSize,
                };

                return ApiResponse<PagedResult<UserDto>>.OK(result, "获取用户列表成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户列表失败");
                return ApiResponse<PagedResult<UserDto>>.Error(
                    "获取用户列表失败",
                    "GET_USERS_FAILED"
                );
            }
        }

        /// <summary>
        /// 获取用户列表（高性能版本 - 使用单一复合查询）
        /// </summary>
        public async Task<ApiResponse<PagedResult<UserDto>>> GetUsersOptimizedAsync(
            UserQueryDto query
        )
        {
            try
            {
                var db = _context.Db;
                var storeScope = await ResolveUserListStoreScopeAsync(db);
                if (storeScope.ReturnEmptyPage)
                {
                    return ApiResponse<PagedResult<UserDto>>.OK(
                        CreateEmptyPagedResult(query),
                        "获取用户列表成功"
                    );
                }
                if (IsOutOfScopedStoreQuery(storeScope, query.StoreGuid))
                {
                    return ApiResponse<PagedResult<UserDto>>.OK(
                        CreateEmptyPagedResult(query),
                        "获取用户列表成功"
                    );
                }

                // 构建复合查询，一次性获取所有需要的数据
                var baseQuery = db.Queryable<User>()
                    .LeftJoin<UserRole>((u, ur) => u.UserGUID == ur.UserGUID && !ur.IsDeleted)
                    .LeftJoin<Role>((u, ur, r) => ur.RoleGUID == r.RoleGUID && r.IsActive && !r.IsDeleted)
                    .LeftJoin<UserStore>((u, ur, r, us) => u.UserGUID == us.UserGUID && !us.IsDeleted)
                    // 用户关联分店用于身份范围展示，停用分店也必须保留；只排除已删除分店。
                    .LeftJoin<Store>(
                        (u, ur, r, us, s) => us.StoreGUID == s.StoreGUID && !s.IsDeleted
                    )
                    .Where((u, ur, r, us, s) => !u.IsDeleted);

                if (storeScope.StoreGuids.Count > 0)
                {
                    var scopedStoreGuids = storeScope.StoreGuids.ToArray();
                    baseQuery = baseQuery.Where(
                        (u, ur, r, us, s) =>
                            !us.IsDeleted && scopedStoreGuids.Contains(us.StoreGUID)
                    );

                    var excludedHighPrivilegeGuids = await ResolveScopedHighPrivilegeUserGuidsAsync(
                        db
                    );
                    if (excludedHighPrivilegeGuids.Length > 0)
                    {
                        baseQuery = baseQuery.Where(
                            (u, ur, r, us, s) => !excludedHighPrivilegeGuids.Contains(u.UserGUID)
                        );
                    }
                }

                // 搜索条件
                if (!string.IsNullOrEmpty(query.SearchKeyword))
                {
                    baseQuery = baseQuery.Where(
                        (u, ur, r, us, s) =>
                            u.Username.Contains(query.SearchKeyword)
                            || u.Email.Contains(query.SearchKeyword)
                            || (u.FullName != null && u.FullName.Contains(query.SearchKeyword))
                    );
                }

                // 状态筛选
                if (query.IsActive.HasValue)
                {
                    baseQuery = baseQuery.Where(
                        (u, ur, r, us, s) => u.IsActive == query.IsActive.Value
                    );
                }

                // 角色筛选
                if (!string.IsNullOrEmpty(query.RoleGuid))
                {
                    baseQuery = baseQuery.Where((u, ur, r, us, s) => ur.RoleGUID == query.RoleGuid);
                }

                // 分店筛选
                if (!string.IsNullOrEmpty(query.StoreGuid))
                {
                    baseQuery = baseQuery.Where(
                        (u, ur, r, us, s) => us.StoreGUID == query.StoreGuid
                    );
                }

                // 获取去重的用户数据和关联信息
                var userDataList = await baseQuery
                    .Select(
                        (u, ur, r, us, s) =>
                            new
                            {
                                User = new UserDto
                                {
                                    UserGUID = u.UserGUID,
                                    Username = u.Username,
                                    Email = u.Email,
                                    FullName = u.FullName,
                                    LastLoginAt = u.LastLoginAt,
                                    LastLoginIp = u.LastLoginIp,
                                    IsActive = u.IsActive,
                                    CreatedAt = u.CreatedAt,
                                    UpdatedAt = u.UpdatedAt ?? u.CreatedAt,
                                },
                                RoleName = r.RoleName,
                                StoreName = s.StoreName,
                            }
                    )
                    .ToListAsync();

                // 合并相同用户的角色和分店信息
                var userDict = new Dictionary<string, UserDto>();
                var roleDict = new Dictionary<string, HashSet<string>>();
                var storeDict = new Dictionary<string, HashSet<string>>();

                foreach (var item in userDataList)
                {
                    var userGuid = item.User.UserGUID;

                    // 添加用户基本信息（只添加一次）
                    if (!userDict.ContainsKey(userGuid))
                    {
                        userDict[userGuid] = item.User;
                        roleDict[userGuid] = new HashSet<string>();
                        storeDict[userGuid] = new HashSet<string>();
                    }

                    // 添加角色名称
                    if (!string.IsNullOrEmpty(item.RoleName))
                    {
                        roleDict[userGuid].Add(item.RoleName);
                    }

                    // 添加分店名称
                    if (!string.IsNullOrEmpty(item.StoreName))
                    {
                        storeDict[userGuid].Add(item.StoreName);
                    }
                }

                // 将去重后的用户转换为列表
                var distinctUsers = userDict.Values.ToList();

                // 排序
                if (!string.IsNullOrEmpty(query.SortBy))
                {
                    if (query.SortDirection?.ToLower() == "asc")
                    {
                        distinctUsers = query.SortBy.ToLower() switch
                        {
                            "username" => distinctUsers.OrderBy(u => u.Username).ToList(),
                            "email" => distinctUsers.OrderBy(u => u.Email).ToList(),
                            "fullname" => distinctUsers.OrderBy(u => u.FullName).ToList(),
                            "lastloginat" => distinctUsers.OrderBy(u => u.LastLoginAt).ToList(),
                            "createdat" => distinctUsers.OrderBy(u => u.CreatedAt).ToList(),
                            _ => distinctUsers.OrderBy(u => u.CreatedAt).ToList(),
                        };
                    }
                    else
                    {
                        distinctUsers = query.SortBy.ToLower() switch
                        {
                            "username" => distinctUsers.OrderByDescending(u => u.Username).ToList(),
                            "email" => distinctUsers.OrderByDescending(u => u.Email).ToList(),
                            "fullname" => distinctUsers.OrderByDescending(u => u.FullName).ToList(),
                            "lastloginat" => distinctUsers
                                .OrderByDescending(u => u.LastLoginAt)
                                .ToList(),
                            "createdat" => distinctUsers
                                .OrderByDescending(u => u.CreatedAt)
                                .ToList(),
                            _ => distinctUsers.OrderByDescending(u => u.CreatedAt).ToList(),
                        };
                    }
                }
                else
                {
                    distinctUsers = distinctUsers.OrderByDescending(u => u.CreatedAt).ToList();
                }

                // 分页
                var total = distinctUsers.Count;
                var pagedUsers = distinctUsers
                    .Skip((query.Page - 1) * query.PageSize)
                    .Take(query.PageSize)
                    .ToList();

                // 为分页后的用户添加角色和分店信息
                foreach (var user in pagedUsers)
                {
                    user.RoleNames = roleDict
                        .GetValueOrDefault(user.UserGUID, new HashSet<string>())
                        .ToList();
                    user.StoreNames = storeDict
                        .GetValueOrDefault(user.UserGUID, new HashSet<string>())
                        .ToList();
                }

                var result = new PagedResult<UserDto>
                {
                    Items = pagedUsers,
                    Total = total,
                    Page = query.Page,
                    PageSize = query.PageSize,
                };

                return ApiResponse<PagedResult<UserDto>>.OK(result, "获取用户列表成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户列表失败");
                return ApiResponse<PagedResult<UserDto>>.Error(
                    "获取用户列表失败",
                    "GET_USERS_FAILED"
                );
            }
        }

        private async Task<UserAccessReadDecision?> ResolveUserReadDecisionAsync(
            ISqlSugarClient db,
            string targetUserGuid
        )
        {
            // 未注入当前用户上下文的内部调用保持既有无限制行为；HTTP 请求均注入该服务，
            // 因此详情和登录记录会统一经过目标用户的读取边界校验。
            if (_manageableStoreScopeService == null)
            {
                return null;
            }

            var scope = await _manageableStoreScopeService.GetScopeAsync();
            if (!scope.IsAuthenticated || string.IsNullOrWhiteSpace(scope.UserGuid))
            {
                return UserAccessReadDecision.Forbidden;
            }

            // 以请求 claims 记录的身份类型为准；角色在 DB 中刚被撤销时也必须保持 fail closed。
            if (!scope.IsStoreManager)
            {
                return UserAccessReadDecision.Unrestricted;
            }

            return await UserAccessMutationSecurity.ValidateReadTargetAsync(
                db,
                scope.UserGuid,
                targetUserGuid
            );
        }

        private async Task<string[]> ResolveScopedHighPrivilegeUserGuidsAsync(ISqlSugarClient db)
        {
            if (_manageableStoreScopeService == null)
            {
                return Array.Empty<string>();
            }

            var scope = await _manageableStoreScopeService.GetScopeAsync();
            if (!scope.IsAuthenticated || string.IsNullOrWhiteSpace(scope.UserGuid))
            {
                return Array.Empty<string>();
            }

            var scopedStoreGuids = scope.StoreGuids
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var candidateUserGuids = scopedStoreGuids.Length == 0
                ? new List<string>()
                : await db.Queryable<UserStore>()
                    .InnerJoin<User>((userStore, user) => userStore.UserGUID == user.UserGUID)
                    .Where((userStore, user) =>
                        !userStore.IsDeleted
                        && !user.IsDeleted
                        && scopedStoreGuids.Contains(userStore.StoreGUID)
                    )
                    .Select((userStore, user) => userStore.UserGUID)
                    .Distinct()
                    .ToListAsync();
            var excluded = await UserAccessMutationSecurity.GetHighPrivilegeUserGuidsAsync(
                db,
                candidateUserGuids
            );

            // 店长本人沿用原有本人读取规则，即使其账号被额外授予高权限角色也不从列表隐藏。
            return excluded
                .Where(userGuid => !string.Equals(userGuid, scope.UserGuid, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private async Task<UserListStoreScope> ResolveUserListStoreScopeAsync(ISqlSugarClient db)
        {
            if (_manageableStoreScopeService == null)
            {
                return UserListStoreScope.Unrestricted;
            }

            var scope = await _manageableStoreScopeService.GetScopeAsync();
            if (scope.IsAdmin)
            {
                return UserListStoreScope.Unrestricted;
            }

            if (scope.IsAllowed && scope.IsStoreManager)
            {
                return new UserListStoreScope(
                    scope.StoreGuids
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    false
                );
            }

            if (!scope.IsAuthenticated || string.IsNullOrWhiteSpace(scope.UserGuid))
            {
                return UserListStoreScope.Unrestricted;
            }

            return scope.IsStoreManager
                ? new UserListStoreScope(Array.Empty<string>(), true)
                : UserListStoreScope.Unrestricted;
        }

        private static bool IsOutOfScopedStoreQuery(
            UserListStoreScope storeScope,
            string? requestedStoreGuid
        )
        {
            return storeScope.StoreGuids.Count > 0
                && !string.IsNullOrWhiteSpace(requestedStoreGuid)
                && !storeScope.StoreGuids.Contains(
                    requestedStoreGuid,
                    StringComparer.OrdinalIgnoreCase
                );
        }

        private async Task<UserStoreMutationScope> ResolveUserStoreMutationScopeAsync(
            ISqlSugarClient db
        )
        {
            var actor = await UserAccessMutationSecurity.ResolveActorAsync(
                db,
                _manageableStoreScopeService
            );
            if (!actor.IsEnforced || actor.IsSuperAdmin)
            {
                return UserStoreMutationScope.Unrestricted;
            }

            if (actor.StoreGuids.Count > 0)
            {
                return new UserStoreMutationScope(
                    actor.StoreGuids
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    true,
                    false
                );
            }

            return UserStoreMutationScope.Forbidden;
        }

        private async Task<bool> CurrentUserHasRoleAliasAsync(
            ISqlSugarClient db,
            string userGuid,
            IReadOnlyCollection<string> roleAliases
        )
        {
            var aliases = roleAliases.ToArray();
            var roleGuids = await db.Queryable<Role>()
                .Where(role => !role.IsDeleted && aliases.Contains(role.RoleName))
                .Select(role => role.RoleGUID)
                .ToListAsync();

            return roleGuids.Count > 0
                && await db.Queryable<UserRole>()
                    .AnyAsync(userRole =>
                        userRole.UserGUID == userGuid
                        && !userRole.IsDeleted
                        && roleGuids.Contains(userRole.RoleGUID)
                    );
        }

        private static PagedResult<UserDto> CreateEmptyPagedResult(UserQueryDto query)
        {
            return new PagedResult<UserDto>
            {
                Items = new List<UserDto>(),
                Total = 0,
                Page = query.Page,
                PageSize = query.PageSize,
            };
        }

        private sealed record UserListStoreScope(IReadOnlyCollection<string> StoreGuids, bool ReturnEmptyPage)
        {
            public static UserListStoreScope Unrestricted { get; } =
                new(Array.Empty<string>(), false);
        }

        private sealed record UserStoreMutationScope(
            IReadOnlyCollection<string> StoreGuids,
            bool IsRestricted,
            bool IsForbidden
        )
        {
            public static UserStoreMutationScope Unrestricted { get; } =
                new(Array.Empty<string>(), false, false);

            public static UserStoreMutationScope Forbidden { get; } =
                new(Array.Empty<string>(), false, true);
        }

        /// <summary>
        /// 性能测试方法 - 比较不同查询方式的性能
        /// </summary>
        /// <param name="query">查询参数</param>
        /// <returns>性能测试结果</returns>
        public async Task<ApiResponse<object>> PerformanceTestAsync(UserQueryDto query)
        {
            try
            {
                var results = new List<object>();

                // 测试原始方法
                var sw1 = System.Diagnostics.Stopwatch.StartNew();
                var result1 = await GetUsersAsync(query);
                sw1.Stop();

                // 测试优化方法
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                var result2 = await GetUsersOptimizedAsync(query);
                sw2.Stop();

                var performanceReport = new
                {
                    OriginalMethod = new
                    {
                        ExecutionTimeMs = sw1.ElapsedMilliseconds,
                        RecordsReturned = result1.Data?.Items?.Count ?? 0,
                        TotalRecords = result1.Data?.Total ?? 0,
                        PerformanceRating = "Baseline",
                    },
                    OptimizedMethod = new
                    {
                        ExecutionTimeMs = sw2.ElapsedMilliseconds,
                        RecordsReturned = result2.Data?.Items?.Count ?? 0,
                        TotalRecords = result2.Data?.Total ?? 0,
                        PerformanceRating = "High Performance",
                    },
                    PerformanceImprovement = new
                    {
                        TimeReductionMs = sw1.ElapsedMilliseconds - sw2.ElapsedMilliseconds,
                        SpeedupFactor = sw1.ElapsedMilliseconds > 0
                            ? Math.Round(
                                (double)sw1.ElapsedMilliseconds / sw2.ElapsedMilliseconds,
                                2
                            )
                            : 1.0,
                        PercentageImprovement = sw1.ElapsedMilliseconds > 0
                            ? Math.Round(
                                (
                                    (double)(sw1.ElapsedMilliseconds - sw2.ElapsedMilliseconds)
                                    / sw1.ElapsedMilliseconds
                                ) * 100,
                                2
                            )
                            : 0.0,
                    },
                    TestParameters = new
                    {
                        PageSize = query.PageSize,
                        Page = query.Page,
                        SearchKeyword = query.SearchKeyword,
                        HasRoleFilter = !string.IsNullOrEmpty(query.RoleGuid),
                        HasStoreFilter = !string.IsNullOrEmpty(query.StoreGuid),
                        TestTimestamp = DateTime.UtcNow,
                    },
                };

                return ApiResponse<object>.OK(performanceReport, "性能测试完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "性能测试失败");
                return ApiResponse<object>.Error("性能测试失败", "PERFORMANCE_TEST_FAILED");
            }
        }

        /// <summary>
        /// 根据GUID获取用户详情
        /// </summary>
        public async Task<ApiResponse<UserDetailDto>> GetUserByGuidAsync(string userGuid)
        {
            try
            {
                var db = _context.Db;
                var access = await ResolveUserReadDecisionAsync(db, userGuid);
                if (access is { IsNotFound: true })
                {
                    return ApiResponse<UserDetailDto>.Error("用户不存在", "USER_NOT_FOUND");
                }
                if (access is { IsAllowed: false })
                {
                    return ApiResponse<UserDetailDto>.Error("无权查看该用户", "FORBIDDEN");
                }

                var user = await db.Queryable<User>()
                    .Where(u => u.UserGUID == userGuid && !u.IsDeleted)
                    .FirstAsync();

                if (user == null)
                {
                    return ApiResponse<UserDetailDto>.Error("用户不存在", "USER_NOT_FOUND");
                }

                var userDetail = new UserDetailDto
                {
                    UserGUID = user.UserGUID,
                    Username = user.Username,
                    Email = user.Email,
                    FullName = user.FullName,
                    LastLoginAt = user.LastLoginAt,
                    LastLoginIp = user.LastLoginIp,
                    IsActive = user.IsActive,
                    CreatedAt = user.CreatedAt,
                    UpdatedAt = user.UpdatedAt ?? user.CreatedAt,
                };

                // 获取用户角色详情
                var roles = await db.Queryable<UserRole>()
                    .InnerJoin<Role>((ur, r) => ur.RoleGUID == r.RoleGUID)
                    .Where((ur, r) => ur.UserGUID == userGuid)
                    .Select(
                        (ur, r) =>
                            new RoleDto
                            {
                                RoleGUID = r.RoleGUID,
                                RoleName = r.RoleName,
                                Description = r.Description,
                                IsActive = r.IsActive,
                                CreatedAt = r.CreatedAt,
                                UpdatedAt = r.UpdatedAt ?? r.CreatedAt,
                            }
                    )
                    .ToListAsync();
                userDetail.Roles = roles;

                // 获取用户分店详情
                var stores = await db.Queryable<UserStore>()
                    .InnerJoin<Store>((us, s) => us.StoreGUID == s.StoreGUID)
                    // 用户详情也要展示历史关联的停用分店，只过滤软删除关系和软删除分店。
                    .Where((us, s) => us.UserGUID == userGuid && !us.IsDeleted && !s.IsDeleted)
                    .Select(
                        (us, s) =>
                            new UserStoreDto
                            {
                                StoreGUID = s.StoreGUID,
                                StoreName = s.StoreName,
                                StoreCode = s.StoreCode,
                                IsActive = s.IsActive,
                                IsPrimary = us.IsPrimary,
                                AssignedAt = us.CreatedAt,
                            }
                    )
                    .ToListAsync();
                userDetail.Stores = stores;

                if (access?.VisibleStoreGuids != null && access.RequiresDelegatedPermission)
                {
                    var visibleStoreGuids = access.VisibleStoreGuids.ToHashSet(
                        StringComparer.OrdinalIgnoreCase
                    );
                    userDetail.Stores = stores
                        .Where(store => visibleStoreGuids.Contains(store.StoreGUID))
                        .ToList();
                }
                stores = userDetail.Stores;

                // 填充角色名和分店名
                userDetail.RoleNames = roles.Select(r => r.RoleName).ToList();
                userDetail.StoreNames = stores.Select(s => s.StoreName).ToList();

                // 获取用户的权限（通过关联的角色）
                var roleGuids = roles.Select(r => r.RoleGUID).ToList();
                var permissions = new List<string>();
                if (roleGuids.Any())
                {
                    permissions = await db.Queryable<SysRolePermission>()
                        .Where(srp => roleGuids.Contains(srp.RoleGuid) && !srp.IsDeleted)
                        .Select(srp => srp.PermissionCode)
                        .ToListAsync();
                }
                userDetail.Permissions = permissions;

                return ApiResponse<UserDetailDto>.OK(userDetail, "获取用户详情成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户详情失败，UserGUID: {UserGUID}", userGuid);
                return ApiResponse<UserDetailDto>.Error(
                    "获取用户详情失败",
                    "GET_USER_DETAIL_FAILED"
                );
            }
        }

        /// <summary>
        /// 获取用户登录记录
        /// </summary>
        public async Task<ApiResponse<PagedResult<UserLoginRecordDto>>> GetUserLoginRecordsAsync(
            string userGuid,
            UserLoginRecordQueryDto query
        )
        {
            try
            {
                var db = _context.Db;
                var access = await ResolveUserReadDecisionAsync(db, userGuid);
                if (access is { IsNotFound: true })
                {
                    return ApiResponse<PagedResult<UserLoginRecordDto>>.Error(
                        "用户不存在",
                        "USER_NOT_FOUND"
                    );
                }
                if (access is { IsAllowed: false })
                {
                    return ApiResponse<PagedResult<UserLoginRecordDto>>.Error(
                        "无权查看该用户登录记录",
                        "FORBIDDEN"
                    );
                }

                var page = Math.Max(query.Page, 1);
                var pageSize = Math.Clamp(query.PageSize, 1, 100);

                var userExists = await db.Queryable<User>()
                    .AnyAsync(user => user.UserGUID == userGuid && !user.IsDeleted);
                if (!userExists)
                {
                    return ApiResponse<PagedResult<UserLoginRecordDto>>.Error(
                        "用户不存在",
                        "USER_NOT_FOUND"
                    );
                }

                var tokenQuery = db.Queryable<RefreshToken>()
                    .Where(token => token.UserGUID == userGuid && !token.IsDeleted)
                    .OrderByDescending(token => token.CreatedAt);

                var total = await tokenQuery.CountAsync();
                var tokens = await tokenQuery
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();
                var now = DateTime.UtcNow;

                var items = tokens
                    .Select(token =>
                    {
                        var isExpired = token.ExpiresAt < now;
                        var status = token.IsRevoked
                            ? "revoked"
                            : isExpired
                                ? "expired"
                                : "active";

                        return new UserLoginRecordDto
                        {
                            SessionId = token.RefreshTokenGUID,
                            LoginAt = token.CreatedAt,
                            IpAddress = token.IpAddress,
                            UserAgent = token.UserAgent,
                            ExpiresAt = token.ExpiresAt,
                            IsRevoked = token.IsRevoked,
                            IsExpired = isExpired,
                            Status = status,
                        };
                    })
                    .ToList();

                var result = new PagedResult<UserLoginRecordDto>
                {
                    Items = items,
                    Total = total,
                    Page = page,
                    PageSize = pageSize,
                };

                return ApiResponse<PagedResult<UserLoginRecordDto>>.OK(
                    result,
                    "获取用户登录记录成功"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户登录记录失败，UserGUID: {UserGUID}", userGuid);
                return ApiResponse<PagedResult<UserLoginRecordDto>>.Error(
                    "获取用户登录记录失败",
                    "GET_USER_LOGIN_RECORDS_FAILED"
                );
            }
        }

        /// <summary>
        /// 根据用户名获取用户
        /// </summary>
        public async Task<ApiResponse<UserDto>> GetUserByUsernameAsync(string username)
        {
            try
            {
                var db = _context.Db;
                var usernameLower = (username ?? string.Empty).Trim().ToLowerInvariant();
                var user = await db.Queryable<User>()
                    .Where(u => !u.IsDeleted && u.Username.ToLower() == usernameLower)
                    .Select(u => new UserDto
                    {
                        UserGUID = u.UserGUID,
                        Username = u.Username,
                        Email = u.Email,
                        FullName = u.FullName,
                        LastLoginAt = u.LastLoginAt,
                        LastLoginIp = u.LastLoginIp,
                        IsActive = u.IsActive,
                        CreatedAt = u.CreatedAt,
                        UpdatedAt = u.UpdatedAt ?? u.CreatedAt,
                    })
                    .FirstAsync();

                if (user == null)
                {
                    return ApiResponse<UserDto>.Error("用户不存在", "USER_NOT_FOUND");
                }

                var access = await ResolveUserReadDecisionAsync(db, user.UserGUID);
                if (access is { IsAllowed: false })
                {
                    return ApiResponse<UserDto>.Error("无权查看该用户", "FORBIDDEN");
                }

                return ApiResponse<UserDto>.OK(user, "获取用户成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "根据用户名获取用户失败，Username: {Username}", username);
                return ApiResponse<UserDto>.Error("获取用户失败", "GET_USER_BY_USERNAME_FAILED");
            }
        }

        /// <summary>
        /// 根据邮箱获取用户
        /// </summary>
        public async Task<ApiResponse<UserDto>> GetUserByEmailAsync(string email)
        {
            try
            {
                var db = _context.Db;
                var user = await db.Queryable<User>()
                    .Where(u => !u.IsDeleted && u.Email == email)
                    .Select(u => new UserDto
                    {
                        UserGUID = u.UserGUID,
                        Username = u.Username,
                        Email = u.Email,
                        FullName = u.FullName,
                        LastLoginAt = u.LastLoginAt,
                        LastLoginIp = u.LastLoginIp,
                        IsActive = u.IsActive,
                        CreatedAt = u.CreatedAt,
                        UpdatedAt = u.UpdatedAt ?? u.CreatedAt,
                    })
                    .FirstAsync();

                if (user == null)
                {
                    return ApiResponse<UserDto>.Error("用户不存在", "USER_NOT_FOUND");
                }

                var access = await ResolveUserReadDecisionAsync(db, user.UserGUID);
                if (access is { IsAllowed: false })
                {
                    return ApiResponse<UserDto>.Error("无权查看该用户", "FORBIDDEN");
                }

                return ApiResponse<UserDto>.OK(user, "获取用户成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "根据邮箱获取用户失败，Email: {Email}", email);
                return ApiResponse<UserDto>.Error("获取用户失败", "GET_USER_BY_EMAIL_FAILED");
            }
        }

        /// <summary>
        /// 创建新用户
        /// </summary>
        public async Task<ApiResponse<UserDto>> CreateUserAsync(CreateUserDto dto)
        {
            try
            {
                var db = _context.Db;
                var adminDecision = await UserAccessMutationSecurity.ValidateAdminOperationAsync(
                    db,
                    _manageableStoreScopeService
                );
                if (!adminDecision.IsAllowed)
                {
                    return ApiResponse<UserDto>.Error(
                        adminDecision.Message,
                        adminDecision.ErrorCode
                    );
                }

                var storeMutationScope = await ResolveUserStoreMutationScopeAsync(db);
                if (storeMutationScope.IsForbidden)
                {
                    return ApiResponse<UserDto>.Error(
                        "当前店长未分配任何可管理分店",
                        "STORE_SCOPE_DENIED"
                    );
                }

                if (
                    storeMutationScope.IsRestricted
                    && dto.StoreGuids.Any(storeGuid =>
                        !storeMutationScope.StoreGuids.Contains(
                            storeGuid,
                            StringComparer.OrdinalIgnoreCase
                        )
                    )
                )
                {
                    return ApiResponse<UserDto>.Error("不能分配非管辖分店", "STORE_SCOPE_DENIED");
                }

                // 检查用户名是否已存在（用户名大小写不敏感）
                var usernameLower = (dto.Username ?? string.Empty).Trim().ToLowerInvariant();
                var existingUser = await db.Queryable<User>()
                    .Where(u => u.Username.ToLower() == usernameLower || u.Email == dto.Email)
                    .FirstAsync();

                if (existingUser != null)
                {
                    if (existingUser.Username.ToLower() == usernameLower)
                        return ApiResponse<UserDto>.Error("用户名已存在", "USERNAME_EXISTS");
                    if (existingUser.Email == dto.Email)
                        return ApiResponse<UserDto>.Error("邮箱已存在", "EMAIL_EXISTS");
                }

                // 创建用户（用户名统一写入小写）
                var user = new User
                {
                    UserGUID = Guid.NewGuid().ToString(),
                    Username = usernameLower,
                    Email = dto.Email,
                    PasswordHash = PasswordHasher.HashSubmittedPassword(dto.Password, dto.PasswordFormat),
                    FullName = dto.FullName,
                    IsActive = dto.IsActive,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };

                // 开启事务
                await db.Ado.BeginTranAsync();

                try
                {
                    // 插入用户
                    await db.Insertable(user).ExecuteCommandAsync();

                    // 分配角色
                    if (dto.RoleGuids.Any())
                    {
                        var userRoles = dto
                            .RoleGuids.Select(roleGuid => new UserRole
                            {
                                UserRoleGUID = Guid.NewGuid().ToString(),
                                UserGUID = user.UserGUID,
                                RoleGUID = roleGuid,
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow,
                            })
                            .ToList();

                        await db.Insertable(userRoles).ExecuteCommandAsync();
                    }

                    // 分配分店
                    if (dto.StoreGuids.Any())
                    {
                        var userStores = dto
                            .StoreGuids.Select(storeGuid => new UserStore
                            {
                                UserStoreGUID = Guid.NewGuid().ToString(),
                                UserGUID = user.UserGUID,
                                StoreGUID = storeGuid,
                                IsPrimary = false, // 默认只是普通分店关联
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow,
                            })
                            .ToList();

                        await db.Insertable(userStores).ExecuteCommandAsync();
                    }

                    await db.Ado.CommitTranAsync();

                    var result = new UserDto
                    {
                        UserGUID = user.UserGUID,
                        Username = user.Username,
                        Email = user.Email,
                        FullName = user.FullName,
                        LastLoginAt = user.LastLoginAt,
                        LastLoginIp = user.LastLoginIp,
                        IsActive = user.IsActive,
                        CreatedAt = user.CreatedAt,
                        UpdatedAt = user.UpdatedAt ?? user.CreatedAt,
                    };

                    _logger.LogInformation(
                        "创建用户成功，UserGUID: {UserGUID}, Username: {Username}",
                        user.UserGUID,
                        user.Username
                    );
                    return ApiResponse<UserDto>.OK(result, "创建用户成功");
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建用户失败，Username: {Username}", dto.Username);
                return ApiResponse<UserDto>.Error("创建用户失败", "CREATE_USER_FAILED");
            }
        }

        /// <summary>
        /// 批量创建用户（按分店代码、用户名、密码，分配默认角色与对应分店）
        /// </summary>
        public async Task<ApiResponse<BatchCreateUserResultDto>> BatchCreateUsersAsync(
            BatchCreateUserRequestDto dto
        )
        {
            var result = new BatchCreateUserResultDto();
            try
            {
                var db = _context.Db;
                var adminDecision = await UserAccessMutationSecurity.ValidateAdminOperationAsync(
                    db,
                    _manageableStoreScopeService
                );
                if (!adminDecision.IsAllowed)
                {
                    return ApiResponse<BatchCreateUserResultDto>.Error(
                        adminDecision.Message,
                        adminDecision.ErrorCode
                    );
                }

                // 校验默认角色存在且启用
                var role = await db.Queryable<Role>()
                    .Where(r => r.RoleGUID == dto.DefaultRoleGuid && r.IsActive)
                    .FirstAsync();
                if (role == null)
                {
                    return ApiResponse<BatchCreateUserResultDto>.Error(
                        "所选默认角色不存在或已禁用，请选择有效角色",
                        "INVALID_DEFAULT_ROLE"
                    );
                }

                if (dto.Items == null || !dto.Items.Any())
                {
                    return ApiResponse<BatchCreateUserResultDto>.Error(
                        "请至少提供一条用户数据",
                        "EMPTY_ITEMS"
                    );
                }

                for (var i = 0; i < dto.Items.Count; i++)
                {
                    var item = dto.Items[i];
                    var rowIndex = i + 1;
                    try
                    {
                        // 按分店代码解析 StoreGUID
                        var store = await db.Queryable<Store>()
                            .Where(s => s.StoreCode == item.StoreCode.Trim())
                            .FirstAsync();
                        if (store == null)
                        {
                            result.FailureCount++;
                            result.Failures.Add(
                                new BatchCreateUserFailureItemDto
                                {
                                    RowIndex = rowIndex,
                                    Username = item.Username,
                                    Reason = $"分店代码不存在: {item.StoreCode}",
                                }
                            );
                            continue;
                        }

                        // 生成邮箱（唯一性用 storeCode 辅助）；用户名统一小写
                        var usernameLower = item.Username.Trim().ToLowerInvariant();
                        var email = $"{usernameLower}@{item.StoreCode.Trim()}.store.local";

                        var createDto = new CreateUserDto
                        {
                            Username = usernameLower,
                            Email = email,
                            Password = item.Password,
                            IsActive = true,
                            RoleGuids = new List<string> { dto.DefaultRoleGuid },
                            StoreGuids = new List<string> { store.StoreGUID },
                        };

                        var createResult = await CreateUserAsync(createDto);
                        if (createResult.Success)
                        {
                            result.SuccessCount++;
                        }
                        else
                        {
                            result.FailureCount++;
                            result.Failures.Add(
                                new BatchCreateUserFailureItemDto
                                {
                                    RowIndex = rowIndex,
                                    Username = item.Username,
                                    Reason = createResult.Message ?? "创建失败",
                                }
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        result.FailureCount++;
                        result.Failures.Add(
                            new BatchCreateUserFailureItemDto
                            {
                                RowIndex = rowIndex,
                                Username = item.Username,
                                Reason = ex.Message,
                            }
                        );
                        _logger.LogWarning(
                            ex,
                            "批量创建用户第 {Row} 行失败: {Username}",
                            rowIndex,
                            item.Username
                        );
                    }
                }

                _logger.LogInformation(
                    "批量创建用户完成，成功: {Success}, 失败: {Failure}",
                    result.SuccessCount,
                    result.FailureCount
                );
                return ApiResponse<BatchCreateUserResultDto>.OK(result, "批量创建完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量创建用户失败");
                return ApiResponse<BatchCreateUserResultDto>.Error(
                    "批量创建用户失败",
                    "BATCH_CREATE_USERS_FAILED"
                );
            }
        }

        /// <summary>
        /// 根据GUID更新用户
        /// </summary>
        public async Task<ApiResponse<UserDto>> UpdateUserByGuidAsync(
            string userGuid,
            UpdateUserDto dto
        )
        {
            var db = _context.Db;
            try
            {
                await db.Ado.BeginTranAsync(IsolationLevel.Serializable);
                try
                {
                    // 店长的用户编辑必须在事务内重新核对目标范围；全局显式 Users.Edit 账号保持原有能力。
                    var user = await db.Queryable<User>()
                        .Where(u => u.UserGUID == userGuid && !u.IsDeleted)
                        .FirstAsync();

                    if (user == null)
                    {
                        await db.Ado.RollbackTranAsync();
                        return ApiResponse<UserDto>.Error("用户不存在", "USER_NOT_FOUND");
                    }

                    var targetDecision = await ValidateUserProfileMutationTargetAsync(db, userGuid);
                    if (!targetDecision.IsAllowed)
                    {
                        await db.Ado.RollbackTranAsync();
                        return ApiResponse<UserDto>.Error(targetDecision.Message, targetDecision.ErrorCode);
                    }

                    var actor = await UserAccessMutationSecurity.ResolveActorAsync(
                        db,
                        _manageableStoreScopeService
                    );
                    var isSelf = actor.UserGuid.Equals(userGuid, StringComparison.OrdinalIgnoreCase);
                    if (isSelf && (dto.IsActive != user.IsActive || !string.Equals(
                        (dto.Username ?? string.Empty).Trim(), user.Username, StringComparison.OrdinalIgnoreCase)))
                    {
                        await db.Ado.RollbackTranAsync();
                        return ApiResponse<UserDto>.Error(
                            "本人只能修改个人资料，不能修改用户名或账号状态",
                            "SELF_PROFILE_FIELDS_DENIED"
                        );
                    }

                    // 检查用户名和邮箱是否被其他用户使用（用户名大小写不敏感）
                    var usernameLower = (dto.Username ?? string.Empty).Trim().ToLowerInvariant();
                    var existingUser = await db.Queryable<User>()
                        .Where(u =>
                            u.UserGUID != userGuid
                            && !u.IsDeleted
                            && (u.Username.ToLower() == usernameLower || u.Email == dto.Email)
                        )
                        .FirstAsync();

                    if (existingUser != null)
                    {
                        if (existingUser.Username.ToLower() == usernameLower)
                            return await RollbackUserUpdateAsync<UserDto>(db, "用户名已被其他用户使用", "USERNAME_EXISTS");
                        if (existingUser.Email == dto.Email)
                            return await RollbackUserUpdateAsync<UserDto>(db, "邮箱已被其他用户使用", "EMAIL_EXISTS");
                    }

                    // 更新用户信息（用户名统一写入小写）
                    user.Username = usernameLower;
                    user.Email = dto.Email;
                    user.FullName = dto.FullName;
                    user.IsActive = dto.IsActive;
                    user.UpdatedAt = DateTime.UtcNow;
                    await db.Updateable(user).ExecuteCommandAsync();
                    await db.Ado.CommitTranAsync();

                    var result = new UserDto
                    {
                        UserGUID = user.UserGUID,
                        Username = user.Username,
                        Email = user.Email,
                        FullName = user.FullName,
                        LastLoginAt = user.LastLoginAt,
                        LastLoginIp = user.LastLoginIp,
                        IsActive = user.IsActive,
                        CreatedAt = user.CreatedAt,
                        UpdatedAt = user.UpdatedAt ?? user.CreatedAt,
                    };

                    _logger.LogInformation("更新用户成功，UserGUID: {UserGUID}", userGuid);
                    return ApiResponse<UserDto>.OK(result, "更新用户成功");
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新用户失败，UserGUID: {UserGUID}", userGuid);
                return ApiResponse<UserDto>.Error("更新用户失败", "UPDATE_USER_FAILED");
            }
        }

        /// <summary>
        /// 根据GUID删除用户
        /// </summary>
        public async Task<ApiResponse<bool>> DeleteUserByGuidAsync(string userGuid)
        {
            try
            {
                var db = _context.Db;

                // 检查用户是否存在
                var user = await db.Queryable<User>()
                    .Where(u => u.UserGUID == userGuid && !u.IsDeleted)
                    .FirstAsync();

                if (user == null)
                {
                    return ApiResponse<bool>.Error("用户不存在", "USER_NOT_FOUND");
                }

                EmployeeProfileDataRemovalPlan? removalPlan = null;
                {
                    // 关键逻辑：用户删除与资料首建统一按 sensitive-change → lifecycle → transaction 排序。
                    await using var sensitiveChangeLock = await EmployeeProfileMediaLock.AcquireAsync(
                        db,
                        userGuid,
                        "sensitive-change",
                        _logger
                    );
                    await using var lifecycleLock = await EmployeeProfileMediaLock
                        .AcquireProfileLifecycleAsync(db, userGuid, _logger);
                    await db.Ado.BeginTranAsync();
                    try
                    {
                        // 等锁期间用户可能已被其他删除请求移除，必须重新确认。
                        user = await db.Queryable<User>()
                            .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
                        if (user == null)
                        {
                            await db.Ado.RollbackTranAsync();
                            return ApiResponse<bool>.Error("用户不存在", "USER_NOT_FOUND");
                        }

                        // 删除前在同一事务内重新核对店长的目标用户范围，避免绕过资料编辑接口的保护。
                        var targetDecision = await ValidateUserProfileMutationTargetAsync(
                            db,
                            userGuid,
                            allowSelf: false
                        );
                        if (!targetDecision.IsAllowed)
                        {
                            await db.Ado.RollbackTranAsync();
                            return ApiResponse<bool>.Error(
                                targetDecision.Message,
                                targetDecision.ErrorCode
                            );
                        }

                        var profile = await db.Queryable<EmployeeProfile>()
                            .FirstAsync(item => item.UserGUID == userGuid);
                        if (_sensitiveChangeService is not null)
                        {
                            removalPlan = await _sensitiveChangeService
                                .PrepareProfileDataRemovalWithinTransactionAsync(
                                    userGuid,
                                    profile,
                                    "System"
                                );
                        }

                        await db.Deleteable<UserRole>()
                            .Where(ur => ur.UserGUID == userGuid)
                            .ExecuteCommandAsync();
                        await db.Deleteable<UserStore>()
                            .Where(us => us.UserGUID == userGuid)
                            .ExecuteCommandAsync();
                        await db.Deleteable<RefreshToken>()
                            .Where(rt => rt.UserGUID == userGuid)
                            .ExecuteCommandAsync();
                        // 必须先删资料再删用户，防止资料首建与物理删除交错留下孤儿行。
                        await db.Deleteable<EmployeeProfile>()
                            .Where(item => item.UserGUID == userGuid)
                            .ExecuteCommandAsync();
                        await db.Deleteable<User>()
                            .Where(u => u.UserGUID == userGuid)
                            .ExecuteCommandAsync();
                        await db.Ado.CommitTranAsync();
                    }
                    catch
                    {
                        await db.Ado.RollbackTranAsync();
                        throw;
                    }

                }

                if (removalPlan is not null && _sensitiveChangeService is not null)
                {
                    try
                    {
                        await _sensitiveChangeService.CleanupProfileDataRemovalAsync(
                            userGuid,
                            removalPlan
                        );
                    }
                    catch (Exception cleanupException)
                    {
                        _logger.LogWarning(
                            cleanupException,
                            "清理已删除用户的员工资料对象失败，UserGUID: {UserGUID}",
                            userGuid
                        );
                    }
                }

                _logger.LogInformation("删除用户成功，UserGUID: {UserGUID}", userGuid);
                return ApiResponse<bool>.OK(true, "删除用户成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除用户失败，UserGUID: {UserGUID}", userGuid);
                return ApiResponse<bool>.Error("删除用户失败", "DELETE_USER_FAILED");
            }
        }

        /// <summary>
        /// 根据GUID更新用户状态
        /// </summary>
        public async Task<ApiResponse<bool>> UpdateUserStatusByGuidAsync(
            string userGuid,
            bool isActive
        )
        {
            var db = _context.Db;
            try
            {
                await db.Ado.BeginTranAsync(IsolationLevel.Serializable);
                try
                {
                    var user = await db.Queryable<User>()
                        .Where(u => u.UserGUID == userGuid && !u.IsDeleted)
                        .FirstAsync();
                    if (user == null)
                    {
                        await db.Ado.RollbackTranAsync();
                        return ApiResponse<bool>.Error("用户不存在", "USER_NOT_FOUND");
                    }

                    var targetDecision = await ValidateUserProfileMutationTargetAsync(
                        db,
                        userGuid,
                        allowSelf: false
                    );
                    if (!targetDecision.IsAllowed)
                    {
                        await db.Ado.RollbackTranAsync();
                        return ApiResponse<bool>.Error(
                            targetDecision.Message,
                            targetDecision.ErrorCode
                        );
                    }

                    await db.Updateable<User>()
                        .SetColumns(u => new User { IsActive = isActive, UpdatedAt = DateTime.UtcNow })
                        .Where(u => u.UserGUID == userGuid && !u.IsDeleted)
                        .ExecuteCommandAsync();
                    await db.Ado.CommitTranAsync();

                    _logger.LogInformation(
                        "更新用户状态成功，UserGUID: {UserGUID}, IsActive: {IsActive}",
                        userGuid,
                        isActive
                    );
                    return ApiResponse<bool>.OK(
                        true,
                        $"用户状态已更新为{(isActive ? "激活" : "禁用")}"
                    );
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新用户状态失败，UserGUID: {UserGUID}", userGuid);
                return ApiResponse<bool>.Error("更新用户状态失败", "UPDATE_USER_STATUS_FAILED");
            }
        }

        /// <summary>
        /// 更新用户密码
        /// </summary>
        public async Task<ApiResponse<bool>> UpdateUserPasswordByGuidAsync(
            string userGuid,
            UpdateUserPasswordDto dto
        )
        {
            var db = _context.Db;
            try
            {
                await db.Ado.BeginTranAsync(IsolationLevel.Serializable);
                try
                {
                    var user = await db.Queryable<User>()
                        .Where(u => u.UserGUID == userGuid && !u.IsDeleted)
                        .FirstAsync();
                    if (user == null)
                    {
                        await db.Ado.RollbackTranAsync();
                        return ApiResponse<bool>.Error("用户不存在", "USER_NOT_FOUND");
                    }

                    var targetDecision = await ValidateUserProfileMutationTargetAsync(db, userGuid);
                    if (!targetDecision.IsAllowed)
                    {
                        await db.Ado.RollbackTranAsync();
                        return ApiResponse<bool>.Error(targetDecision.Message, targetDecision.ErrorCode);
                    }

                    var hashedPassword = PasswordHasher.HashSubmittedPassword(dto.NewPassword, dto.PasswordFormat);
                    await db.Updateable<User>()
                        .SetColumns(u => new User
                        {
                            PasswordHash = hashedPassword,
                            UpdatedAt = DateTime.UtcNow,
                        })
                        .Where(u => u.UserGUID == userGuid && !u.IsDeleted)
                        .ExecuteCommandAsync();

                    await db.Ado.CommitTranAsync();
                    _logger.LogInformation("更新用户密码成功，UserGUID: {UserGUID}", userGuid);
                    return ApiResponse<bool>.OK(true, "密码更新成功");
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新用户密码失败，UserGUID: {UserGUID}", userGuid);
                return ApiResponse<bool>.Error("更新用户密码失败", "UPDATE_USER_PASSWORD_FAILED");
            }
        }

        private async Task<UserAccessMutationDecision> ValidateUserProfileMutationTargetAsync(
            ISqlSugarClient db,
            string targetUserGuid,
            bool allowSelf = true
        )
        {
            var actor = await UserAccessMutationSecurity.ResolveActorAsync(
                db,
                _manageableStoreScopeService
            );
            if (actor.IsSuperAdmin)
            {
                // 全局 Users.Edit/ResetPassword 的授权仍由控制器策略负责，这里不额外收窄。
                return UserAccessMutationDecision.Allow;
            }

            if (!actor.IsStoreManager)
            {
                var scope = _manageableStoreScopeService == null
                    ? null
                    : await _manageableStoreScopeService.GetScopeAsync();
                return scope?.IsStoreManager == true
                    ? new UserAccessMutationDecision(
                        false,
                        "当前店长权限已失效",
                        "ACCESS_DELEGATOR_DENIED"
                    )
                    : UserAccessMutationDecision.Allow;
            }

            if (actor.UserGuid.Equals(targetUserGuid, StringComparison.OrdinalIgnoreCase))
            {
                return allowSelf
                    ? UserAccessMutationDecision.Allow
                    : new UserAccessMutationDecision(
                        false,
                        "不能禁用、锁定或删除本人账号",
                        "SELF_PROFILE_FIELDS_DENIED"
                    );
            }

            return await UserAccessMutationSecurity.ValidateTargetAsync(db, actor, targetUserGuid);
        }

        private static async Task<ApiResponse<T>> RollbackUserUpdateAsync<T>(
            ISqlSugarClient db,
            string message,
            string errorCode
        )
        {
            await db.Ado.RollbackTranAsync();
            return ApiResponse<T>.Error(message, errorCode);
        }

        /// <summary>
        /// 为用户分配角色
        /// </summary>
        public async Task<ApiResponse<bool>> AssignRolesToUserAsync(
            string userGuid,
            UserRoleAssignmentDto dto
        )
        {
            try
            {
                var db = _context.Db;

                // 检查用户是否存在
                var userExists = await db.Queryable<User>()
                    .Where(u => u.UserGUID == userGuid && !u.IsDeleted)
                    .AnyAsync();

                if (!userExists)
                {
                    return ApiResponse<bool>.Error("用户不存在", "USER_NOT_FOUND");
                }

                var actor = await UserAccessMutationSecurity.ResolveActorAsync(
                    db,
                    _manageableStoreScopeService
                );
                var targetDecision = await UserAccessMutationSecurity.ValidateGlobalTargetAsync(
                    db,
                    actor,
                    userGuid
                );
                if (!targetDecision.IsAllowed)
                {
                    return ApiResponse<bool>.Error(
                        targetDecision.Message,
                        targetDecision.ErrorCode
                    );
                }

                var requestedRoleGuids = dto.RoleGuids
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var requestedRoles = requestedRoleGuids.Count == 0
                    ? new List<Role>()
                    : await db.Queryable<Role>()
                        .Where(role =>
                            requestedRoleGuids.Contains(role.RoleGUID)
                            && role.IsActive
                            && !role.IsDeleted
                        )
                        .ToListAsync();
                if (requestedRoles.Count != requestedRoleGuids.Count)
                {
                    return ApiResponse<bool>.Error("包含不存在的角色", "ROLE_NOT_FOUND");
                }

                // 店长角色由 IsPrimary 管理关系派生，角色接口只处理非派生角色。
                var assignableRoles = requestedRoles
                    .Where(role => !Permissions.IsStoreManagerRole(role.RoleName))
                    .ToList();
                var isRestrictedActor = actor.IsEnforced && !actor.IsSuperAdmin;
                if (isRestrictedActor)
                {
                    var existingAssignedRoleGuids = await db.Queryable<UserRole>()
                        .Where(item => item.UserGUID == userGuid && !item.IsDeleted)
                        .Select(item => item.RoleGUID)
                        .ToListAsync();
                    var newNonEmployeeRole = requestedRoles.FirstOrDefault(role =>
                        !Permissions.IsEmployeeRole(role.RoleName)
                        && !existingAssignedRoleGuids.Contains(
                            role.RoleGUID,
                            StringComparer.OrdinalIgnoreCase
                        )
                    );
                    if (newNonEmployeeRole != null)
                    {
                        return ApiResponse<bool>.Error(
                            "店长只能授予员工角色",
                            "ROLE_DELEGATION_DENIED"
                        );
                    }

                    assignableRoles = assignableRoles
                        .Where(role => Permissions.IsEmployeeRole(role.RoleName))
                        .ToList();
                }

                // 授权写入使用串行化事务，避免目标或操作者范围在复验与写入之间漂移。
                await db.Ado.BeginTranAsync(IsolationLevel.Serializable);

                try
                {
                    var transactionActor = await UserAccessMutationSecurity.ResolveActorAsync(
                        db,
                        _manageableStoreScopeService
                    );
                    var revalidatedTarget = await UserAccessMutationSecurity.ValidateGlobalTargetAsync(
                        db,
                        transactionActor,
                        userGuid
                    );
                    if (!revalidatedTarget.IsAllowed)
                    {
                        await db.Ado.RollbackTranAsync();
                        return ApiResponse<bool>.Error(
                            revalidatedTarget.Message,
                            revalidatedTarget.ErrorCode
                        );
                    }

                    var transactionRequestedRoles = requestedRoleGuids.Count == 0
                        ? new List<Role>()
                        : await db.Queryable<Role>()
                            .Where(role =>
                                requestedRoleGuids.Contains(role.RoleGUID)
                                && role.IsActive
                                && !role.IsDeleted
                            )
                            .ToListAsync();
                    if (transactionRequestedRoles.Count != requestedRoleGuids.Count)
                    {
                        await db.Ado.RollbackTranAsync();
                        return ApiResponse<bool>.Error("包含不存在的角色", "ROLE_NOT_FOUND");
                    }

                    var transactionRestrictedActor = !transactionActor.IsSuperAdmin;
                    assignableRoles = transactionRequestedRoles
                        .Where(role => !Permissions.IsStoreManagerRole(role.RoleName))
                        .ToList();
                    if (transactionRestrictedActor)
                    {
                        var existingAssignedRoleGuids = await db.Queryable<UserRole>()
                            .Where(item => item.UserGUID == userGuid && !item.IsDeleted)
                            .Select(item => item.RoleGUID)
                            .ToListAsync();
                        var newNonEmployeeRole = transactionRequestedRoles.FirstOrDefault(role =>
                            !Permissions.IsEmployeeRole(role.RoleName)
                            && !existingAssignedRoleGuids.Contains(
                                role.RoleGUID,
                                StringComparer.OrdinalIgnoreCase
                            )
                        );
                        if (newNonEmployeeRole != null)
                        {
                            await db.Ado.RollbackTranAsync();
                            return ApiResponse<bool>.Error(
                                "店长只能授予员工角色",
                                "ROLE_DELEGATION_DENIED"
                            );
                        }

                        assignableRoles = assignableRoles
                            .Where(role => Permissions.IsEmployeeRole(role.RoleName))
                            .ToList();

                        if (assignableRoles.Count == 0)
                        {
                            var posBusinessCodes = PermissionSeedData
                                .PosTerminalBusinessPermissionCodes
                                .ToArray();
                            var hasDirectPosPermissions = await db.Queryable<SysUserPermission>()
                                .AnyAsync(item =>
                                    item.UserGuid == userGuid
                                    && !item.IsDeleted
                                    && posBusinessCodes.Contains(item.PermissionCode)
                                );
                            var hasStorePosOverrides = await db
                                .Queryable<SysUserStorePosPermission>()
                                .AnyAsync(item =>
                                    item.UserGuid == userGuid && !item.IsDeleted
                                );
                            if (hasDirectPosPermissions || hasStorePosOverrides)
                            {
                                await db.Ado.RollbackTranAsync();
                                return ApiResponse<bool>.Error(
                                    "请先清除员工的账号及按店 POS 收银权限，再移除最后一个员工角色",
                                    "EMPLOYEE_ROLE_HAS_POS_PERMISSIONS"
                                );
                            }
                        }

                        var employeeRoleNames = Permissions.EmployeeRoleNames.ToArray();
                        var employeeRoleGuids = await db.Queryable<Role>()
                            .Where(role => employeeRoleNames.Contains(role.RoleName))
                            .Select(role => role.RoleGUID)
                            .ToListAsync();
                        if (employeeRoleGuids.Count > 0)
                        {
                            // 受限集合替换只删除员工角色关联，其他角色行及审计信息保持不变。
                            await db.Deleteable<UserRole>()
                                .Where(userRole =>
                                    userRole.UserGUID == userGuid
                                    && employeeRoleGuids.Contains(userRole.RoleGUID)
                                )
                                .ExecuteCommandAsync();
                        }
                    }
                    else
                    {
                        var managerRoleNames = Permissions.StoreManagerRoleNames.ToArray();
                        var managerRoleGuids = await db.Queryable<Role>()
                            .Where(role => managerRoleNames.Contains(role.RoleName))
                            .Select(role => role.RoleGUID)
                            .ToListAsync();
                        if (managerRoleGuids.Count == 0)
                        {
                            await db.Deleteable<UserRole>()
                                .Where(userRole => userRole.UserGUID == userGuid)
                                .ExecuteCommandAsync();
                        }
                        else
                        {
                            await db.Deleteable<UserRole>()
                                .Where(userRole =>
                                    userRole.UserGUID == userGuid
                                    && !managerRoleGuids.Contains(userRole.RoleGUID)
                                )
                                .ExecuteCommandAsync();
                        }
                    }

                    // 添加新的角色分配
                    var rolesToInsert = assignableRoles;
                    if (rolesToInsert.Any())
                    {
                        var userRoles = rolesToInsert.Select(role => new UserRole
                        {
                            UserRoleGUID = Guid.NewGuid().ToString(), // ✅ 必须设置主键
                            UserGUID = userGuid,
                            RoleGUID = role.RoleGUID,
                            AssignedAt = DateTime.UtcNow,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow,
                        })
                            .ToList();

                        await db.Insertable(userRoles).ExecuteCommandAsync();
                    }

                    if (!transactionRestrictedActor)
                    {
                        await UserStoreManagerRoleSynchronizer.SynchronizeAsync(db, userGuid);
                    }

                    await db.Ado.CommitTranAsync();

                    _logger.LogInformation(
                        "用户角色分配成功，UserGUID: {UserGUID}, RoleCount: {RoleCount}",
                        userGuid,
                        assignableRoles.Count
                    );
                    return ApiResponse<bool>.OK(true, "角色分配成功");
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "用户角色分配失败，UserGUID: {UserGUID}", userGuid);
                return ApiResponse<bool>.Error("角色分配失败", "ASSIGN_ROLES_FAILED");
            }
        }

        /// <summary>
        /// 为用户分配分店
        /// </summary>
        public async Task<ApiResponse<bool>> AssignStoresToUserAsync(
            string userGuid,
            List<UserStoreAssignmentDto> storeAssignments
        )
        {
            try
            {
                var db = _context.Db;

                // 检查用户是否存在
                var userExists = await db.Queryable<User>()
                    .Where(u => u.UserGUID == userGuid && !u.IsDeleted)
                    .AnyAsync();

                if (!userExists)
                {
                    return ApiResponse<bool>.Error("用户不存在", "USER_NOT_FOUND");
                }

                var actor = await UserAccessMutationSecurity.ResolveActorAsync(
                    db,
                    _manageableStoreScopeService
                );
                var targetDecision = await UserAccessMutationSecurity.ValidateTargetAsync(
                    db,
                    actor,
                    userGuid
                );
                if (!targetDecision.IsAllowed)
                {
                    return ApiResponse<bool>.Error(
                        targetDecision.Message,
                        targetDecision.ErrorCode
                    );
                }

                var normalizedStoreAssignments = storeAssignments
                    .Where(item => !string.IsNullOrWhiteSpace(item.StoreGUID))
                    .GroupBy(item => item.StoreGUID, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new UserStoreAssignmentDto
                    {
                        StoreGUID = group.Key,
                        IsPrimary = group.Any(item => item.IsPrimary),
                    })
                    .ToList();
                var requestedStoreGuids = normalizedStoreAssignments
                    .Select(item => item.StoreGUID)
                    .ToList();
                var validStoreCount = requestedStoreGuids.Count == 0
                    ? 0
                    : await db.Queryable<Store>()
                        .Where(store =>
                            requestedStoreGuids.Contains(store.StoreGUID)
                            && !store.IsDeleted
                        )
                        .CountAsync();
                if (validStoreCount != requestedStoreGuids.Count)
                {
                    return ApiResponse<bool>.Error("包含不存在或已删除的分店", "STORE_NOT_FOUND");
                }

                var existingUserStores = await db.Queryable<UserStore>()
                    .Where(us => us.UserGUID == userGuid && !us.IsDeleted)
                    .ToListAsync();

                var storeMutationScope = await ResolveUserStoreMutationScopeAsync(db);
                if (storeMutationScope.IsForbidden)
                {
                    return ApiResponse<bool>.Error(
                        "当前店长未分配任何可管理分店",
                        "STORE_SCOPE_DENIED"
                    );
                }

                var effectiveStoreAssignments = normalizedStoreAssignments;
                if (storeMutationScope.IsRestricted)
                {
                    var scopedStoreGuids = new HashSet<string>(
                        storeMutationScope.StoreGuids,
                        StringComparer.OrdinalIgnoreCase
                    );
                    var existingInScope = existingUserStores.Any(us =>
                        scopedStoreGuids.Contains(us.StoreGUID)
                    );

                    if (!existingInScope)
                    {
                        return ApiResponse<bool>.Error("无权管理该用户的分店", "USER_SCOPE_DENIED");
                    }

                    var hiddenExistingStoreGuids = existingUserStores
                        .Where(us => !scopedStoreGuids.Contains(us.StoreGUID))
                        .Select(us => us.StoreGUID)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var outOfScopeAssignments = normalizedStoreAssignments
                        .Where(assignment => !scopedStoreGuids.Contains(assignment.StoreGUID))
                        .ToList();

                    if (
                        outOfScopeAssignments.Any(assignment =>
                            !hiddenExistingStoreGuids.Contains(assignment.StoreGUID)
                        )
                    )
                    {
                        return ApiResponse<bool>.Error("不能分配非管辖分店", "STORE_SCOPE_DENIED");
                    }

                    effectiveStoreAssignments = normalizedStoreAssignments
                        .Where(assignment => scopedStoreGuids.Contains(assignment.StoreGUID))
                        .ToList();
                }

                var isRestrictedActor = actor.IsEnforced && !actor.IsSuperAdmin;
                if (isRestrictedActor)
                {
                    var existingManageableStoreGuids = existingUserStores
                        .Where(item => item.IsPrimary)
                        .Select(item => item.StoreGUID)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (
                        effectiveStoreAssignments.Any(assignment =>
                            assignment.IsPrimary
                            && !existingManageableStoreGuids.Contains(assignment.StoreGUID)
                        )
                    )
                    {
                        return ApiResponse<bool>.Error(
                            "只有管理员可以授予分店管理关系",
                            "MANAGEABLE_STORE_GRANT_DENIED"
                        );
                    }

                    // 店长只提交查看关系；已有管理关系由数据库原行保留，不参与集合重建。
                    effectiveStoreAssignments = effectiveStoreAssignments
                        .Where(item =>
                            !item.IsPrimary
                            && !existingManageableStoreGuids.Contains(item.StoreGUID)
                        )
                        .ToList();
                }

                // 授权写入使用串行化事务，并在事务内重新解析操作者范围。
                await db.Ado.BeginTranAsync(IsolationLevel.Serializable);

                try
                {
                    var transactionActor = await UserAccessMutationSecurity.ResolveActorAsync(
                        db,
                        _manageableStoreScopeService
                    );
                    var revalidatedTarget = await UserAccessMutationSecurity.ValidateTargetAsync(
                        db,
                        transactionActor,
                        userGuid
                    );
                    if (!revalidatedTarget.IsAllowed)
                    {
                        await db.Ado.RollbackTranAsync();
                        return ApiResponse<bool>.Error(
                            revalidatedTarget.Message,
                            revalidatedTarget.ErrorCode
                        );
                    }

                    var transactionValidStoreCount = requestedStoreGuids.Count == 0
                        ? 0
                        : await db.Queryable<Store>()
                            .Where(store =>
                                requestedStoreGuids.Contains(store.StoreGUID)
                                && !store.IsDeleted
                            )
                            .CountAsync();
                    if (transactionValidStoreCount != requestedStoreGuids.Count)
                    {
                        await db.Ado.RollbackTranAsync();
                        return ApiResponse<bool>.Error(
                            "包含不存在或已删除的分店",
                            "STORE_NOT_FOUND"
                        );
                    }

                    var transactionExistingUserStores = await db.Queryable<UserStore>()
                        .Where(us => us.UserGUID == userGuid && !us.IsDeleted)
                        .ToListAsync();
                    var transactionRestrictedActor = !transactionActor.IsSuperAdmin;
                    effectiveStoreAssignments = normalizedStoreAssignments;

                    if (transactionRestrictedActor)
                    {
                        var scopedStoreGuids = transactionActor.StoreGuids
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var hiddenExistingStoreGuids = transactionExistingUserStores
                            .Where(us => !scopedStoreGuids.Contains(us.StoreGUID))
                            .Select(us => us.StoreGUID)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var outOfScopeAssignments = normalizedStoreAssignments
                            .Where(assignment =>
                                !scopedStoreGuids.Contains(assignment.StoreGUID)
                            )
                            .ToList();
                        if (
                            outOfScopeAssignments.Any(assignment =>
                                !hiddenExistingStoreGuids.Contains(assignment.StoreGUID)
                            )
                        )
                        {
                            await db.Ado.RollbackTranAsync();
                            return ApiResponse<bool>.Error(
                                "不能分配非管辖分店",
                                "STORE_SCOPE_DENIED"
                            );
                        }

                        var existingManageableStoreGuids = transactionExistingUserStores
                            .Where(item => item.IsPrimary)
                            .Select(item => item.StoreGUID)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        if (
                            normalizedStoreAssignments.Any(assignment =>
                                scopedStoreGuids.Contains(assignment.StoreGUID)
                                && assignment.IsPrimary
                                && !existingManageableStoreGuids.Contains(
                                    assignment.StoreGUID
                                )
                            )
                        )
                        {
                            await db.Ado.RollbackTranAsync();
                            return ApiResponse<bool>.Error(
                                "只有管理员可以授予分店管理关系",
                                "MANAGEABLE_STORE_GRANT_DENIED"
                            );
                        }

                        effectiveStoreAssignments = normalizedStoreAssignments
                            .Where(assignment =>
                                scopedStoreGuids.Contains(assignment.StoreGUID)
                                && !assignment.IsPrimary
                                && !existingManageableStoreGuids.Contains(
                                    assignment.StoreGUID
                                )
                            )
                            .ToList();

                        var scopedStoreGuidList = scopedStoreGuids.ToArray();
                        // 受限集合替换只删除范围内查看关系，primary 与范围外原行完全不触碰。
                        await db.Deleteable<UserStore>()
                            .Where(us =>
                                us.UserGUID == userGuid
                                && !us.IsPrimary
                                && scopedStoreGuidList.Contains(us.StoreGUID)
                            )
                            .ExecuteCommandAsync();
                    }
                    else
                    {
                        await db.Deleteable<UserStore>()
                            .Where(us => us.UserGUID == userGuid)
                            .ExecuteCommandAsync();
                    }

                    // 添加新的分店分配
                    if (effectiveStoreAssignments.Any())
                    {
                        var userStores = effectiveStoreAssignments
                            .Select(
                                (assignment, index) =>
                                    new UserStore
                                    {
                                        UserStoreGUID = Guid.NewGuid().ToString(), // ✅ 必须设置主键
                                        UserGUID = userGuid,
                                        StoreGUID = assignment.StoreGUID,
                                        IsPrimary = assignment.IsPrimary,
                                        CreatedAt = DateTime.UtcNow,
                                        UpdatedAt = DateTime.UtcNow,
                                    }
                            )
                            .ToList();

                        await db.Insertable(userStores).ExecuteCommandAsync();
                    }

                    if (!transactionRestrictedActor)
                    {
                        await UserStoreManagerRoleSynchronizer.SynchronizeAsync(db, userGuid);
                    }

                    await db.Ado.CommitTranAsync();

                    _logger.LogInformation(
                        "用户分店分配成功，UserGUID: {UserGUID}, StoreCount: {StoreCount}",
                        userGuid,
                        effectiveStoreAssignments.Count
                    );
                    return ApiResponse<bool>.OK(true, "分店分配成功");
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "用户分店分配失败，UserGUID: {UserGUID}", userGuid);
                return ApiResponse<bool>.Error("分店分配失败", "ASSIGN_STORES_FAILED");
            }
        }

        /// <summary>
        /// 获取用户的角色列表
        /// </summary>
        public async Task<ApiResponse<List<RoleDto>>> GetUserRolesAsync(string userGuid)
        {
            try
            {
                var db = _context.Db;
                var actor = await UserAccessMutationSecurity.ResolveActorAsync(
                    db,
                    _manageableStoreScopeService
                );
                var isSelfRead = !string.IsNullOrWhiteSpace(actor.UserGuid)
                    && string.Equals(
                        actor.UserGuid,
                        userGuid,
                        StringComparison.OrdinalIgnoreCase
                    );
                if (!isSelfRead)
                {
                    var targetDecision = await UserAccessMutationSecurity.ValidateGlobalTargetAsync(
                        db,
                        actor,
                        userGuid
                    );
                    if (!targetDecision.IsAllowed)
                    {
                        return ApiResponse<List<RoleDto>>.Error(
                            targetDecision.Message,
                            targetDecision.ErrorCode
                        );
                    }
                }

                var restrictedEmployeeRead = actor.IsStoreManager && !isSelfRead;
                var roleQuery = db.Queryable<UserRole>()
                    .InnerJoin<Role>((ur, r) => ur.RoleGUID == r.RoleGUID)
                    .Where((ur, r) =>
                        ur.UserGUID == userGuid
                        && !ur.IsDeleted
                        && !r.IsDeleted
                        && r.IsActive
                    );

                if (restrictedEmployeeRead)
                {
                    var employeeRoleNames = Permissions.EmployeeRoleNames.ToArray();
                    // 本地布尔值不能与数据库条件合并，否则 SQL Server 会生成无效的 OR 表达式。
                    roleQuery = roleQuery.Where((ur, r) =>
                        employeeRoleNames.Contains(r.RoleName)
                    );
                }

                var roles = await roleQuery
                    .Select(
                        (ur, r) =>
                            new RoleDto
                            {
                                RoleGUID = r.RoleGUID,
                                RoleName = r.RoleName,
                                Description = r.Description,
                                IsActive = r.IsActive,
                                CreatedAt = r.CreatedAt,
                                UpdatedAt = r.UpdatedAt ?? r.CreatedAt,
                            }
                    )
                    .ToListAsync();

                return ApiResponse<List<RoleDto>>.OK(roles, "获取用户角色成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户角色失败，UserGUID: {UserGUID}", userGuid);
                return ApiResponse<List<RoleDto>>.Error(
                    "获取用户角色失败",
                    "GET_USER_ROLES_FAILED"
                );
            }
        }

        /// <summary>
        /// 获取用户的分店列表
        /// </summary>
        public async Task<ApiResponse<List<UserStoreDto>>> GetUserStoresAsync(string userGuid)
        {
            try
            {
                var db = _context.Db;

                var userRoles = await db.Queryable<UserRole>()
                    .InnerJoin<Role>((ur, r) => ur.RoleGUID == r.RoleGUID)
                    .Where((ur, r) =>
                        ur.UserGUID == userGuid
                        && !ur.IsDeleted
                        && !r.IsDeleted
                        && r.IsActive
                    )
                    .Select((ur, r) => r.RoleName)
                    .ToListAsync();

                var isAdminOrWarehouse = userRoles.Any(roleName =>
                    Permissions.IsSuperAdminRole(roleName)
                    || Permissions.WarehouseManagerRoleNames.Contains(
                        roleName,
                        StringComparer.OrdinalIgnoreCase
                    )
                );

                List<UserStoreDto> stores;

                if (isAdminOrWarehouse)
                {
                    stores = await db.Queryable<Store>()
                        // 当前用户身份范围需要展示已关联/可见分店，停用分店不能影响登录后的用户信息。
                        .Where(s => !s.IsDeleted)
                        .Select(s => new UserStoreDto
                        {
                            StoreGUID = s.StoreGUID,
                            StoreName = s.StoreName,
                            StoreCode = s.StoreCode,
                            IsActive = s.IsActive,
                            IsPrimary = false,
                            AssignedAt = DateTime.UtcNow,
                        })
                        .ToListAsync();
                }
                else
                {
                    stores = await db.Queryable<UserStore>()
                        .InnerJoin<Store>((us, s) => us.StoreGUID == s.StoreGUID)
                        // 用户登录和用户信息展示只要求用户分店关系存在，停用分店也要返回。
                        .Where((us, s) => us.UserGUID == userGuid && !us.IsDeleted && !s.IsDeleted)
                        .Select(
                            (us, s) =>
                                new UserStoreDto
                                {
                                    StoreGUID = s.StoreGUID,
                                    StoreName = s.StoreName,
                                    StoreCode = s.StoreCode,
                                    IsActive = s.IsActive,
                                    IsPrimary = us.IsPrimary,
                                    AssignedAt = us.CreatedAt,
                                }
                        )
                        .ToListAsync();
                }

                return ApiResponse<List<UserStoreDto>>.OK(stores, "获取用户分店成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户分店失败，UserGUID: {UserGUID}", userGuid);
                return ApiResponse<List<UserStoreDto>>.Error(
                    "获取用户分店失败",
                    "GET_USER_STORES_FAILED"
                );
            }
        }

        /// <summary>
        /// 从用户移除角色
        /// </summary>
        public async Task<ApiResponse<bool>> RemoveRoleFromUserAsync(
            string userGuid,
            string roleGuid
        )
        {
            try
            {
                var db = _context.Db;

                var actor = await UserAccessMutationSecurity.ResolveActorAsync(
                    db,
                    _manageableStoreScopeService
                );
                var targetDecision = await UserAccessMutationSecurity.ValidateAdminTargetAsync(
                    db,
                    actor,
                    userGuid
                );
                if (!targetDecision.IsAllowed)
                {
                    return ApiResponse<bool>.Error(
                        targetDecision.Message,
                        targetDecision.ErrorCode
                    );
                }

                var role = await db.Queryable<Role>()
                    .Where(item => item.RoleGUID == roleGuid && !item.IsDeleted)
                    .FirstAsync();
                if (role == null)
                {
                    return ApiResponse<bool>.Error("用户角色关联不存在", "USER_ROLE_NOT_FOUND");
                }
                if (Permissions.IsStoreManagerRole(role.RoleName))
                {
                    return ApiResponse<bool>.Error(
                        "店长角色由可管理分店关系维护",
                        "DERIVED_STORE_MANAGER_ROLE"
                    );
                }

                var result = await db.Deleteable<UserRole>()
                    .Where(ur => ur.UserGUID == userGuid && ur.RoleGUID == roleGuid)
                    .ExecuteCommandAsync();

                if (result == 0)
                {
                    return ApiResponse<bool>.Error("用户角色关联不存在", "USER_ROLE_NOT_FOUND");
                }

                _logger.LogInformation(
                    "移除用户角色成功，UserGUID: {UserGUID}, RoleGUID: {RoleGUID}",
                    userGuid,
                    roleGuid
                );
                return ApiResponse<bool>.OK(true, "移除角色成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "移除用户角色失败，UserGUID: {UserGUID}, RoleGUID: {RoleGUID}",
                    userGuid,
                    roleGuid
                );
                return ApiResponse<bool>.Error("移除角色失败", "REMOVE_ROLE_FAILED");
            }
        }

        /// <summary>
        /// 从用户移除分店
        /// </summary>
        public async Task<ApiResponse<bool>> RemoveStoreFromUserAsync(
            string userGuid,
            string storeGuid
        )
        {
            try
            {
                var db = _context.Db;

                var actor = await UserAccessMutationSecurity.ResolveActorAsync(
                    db,
                    _manageableStoreScopeService
                );
                var targetDecision = await UserAccessMutationSecurity.ValidateAdminTargetAsync(
                    db,
                    actor,
                    userGuid
                );
                if (!targetDecision.IsAllowed)
                {
                    return ApiResponse<bool>.Error(
                        targetDecision.Message,
                        targetDecision.ErrorCode
                    );
                }
                await db.Ado.BeginTranAsync();
                int result;
                try
                {
                    result = await db.Deleteable<UserStore>()
                        .Where(us => us.UserGUID == userGuid && us.StoreGUID == storeGuid)
                        .ExecuteCommandAsync();

                    if (result > 0)
                    {
                        await UserStoreManagerRoleSynchronizer.SynchronizeAsync(db, userGuid);
                    }

                    await db.Ado.CommitTranAsync();
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }

                if (result == 0)
                {
                    return ApiResponse<bool>.Error("用户分店关联不存在", "USER_STORE_NOT_FOUND");
                }

                _logger.LogInformation(
                    "移除用户分店成功，UserGUID: {UserGUID}, StoreGUID: {StoreGUID}",
                    userGuid,
                    storeGuid
                );
                return ApiResponse<bool>.OK(true, "移除分店成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "移除用户分店失败，UserGUID: {UserGUID}, StoreGUID: {StoreGUID}",
                    userGuid,
                    storeGuid
                );
                return ApiResponse<bool>.Error("移除分店失败", "REMOVE_STORE_FAILED");
            }
        }

        /// <summary>
        /// 批量管理用户
        /// </summary>
        public async Task<ApiResponse<bool>> BatchManageUsersAsync(BatchUserOperationDto dto)
        {
            try
            {
                var db = _context.Db;
                var userGuids = dto.UserGuids
                    .Where(userGuid => !string.IsNullOrWhiteSpace(userGuid))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.Ordinal)
                    .ToArray();

                switch (dto.Operation.ToLower())
                {
                    case "activate":
                    case "deactivate":
                        await db.Ado.BeginTranAsync(IsolationLevel.Serializable);
                        try
                        {
                            foreach (var targetUserGuid in userGuids)
                            {
                                var target = await db.Queryable<User>()
                                    .Where(user => user.UserGUID == targetUserGuid && !user.IsDeleted)
                                    .FirstAsync();
                                if (target == null)
                                {
                                    return await RollbackUserUpdateAsync<bool>(
                                        db,
                                        "用户不存在",
                                        "USER_NOT_FOUND"
                                    );
                                }

                                var decision = await ValidateUserProfileMutationTargetAsync(
                                    db,
                                    targetUserGuid,
                                    allowSelf: false
                                );
                                if (!decision.IsAllowed)
                                {
                                    return await RollbackUserUpdateAsync<bool>(
                                        db,
                                        decision.Message,
                                        decision.ErrorCode
                                    );
                                }
                            }

                            var isActive = string.Equals(
                                dto.Operation,
                                "activate",
                                StringComparison.OrdinalIgnoreCase
                            );
                            await db.Updateable<User>()
                                .SetColumns(u => new User
                                {
                                    IsActive = isActive,
                                    UpdatedAt = DateTime.UtcNow,
                                })
                                .Where(u => userGuids.Contains(u.UserGUID) && !u.IsDeleted)
                                .ExecuteCommandAsync();
                            await db.Ado.CommitTranAsync();
                        }
                        catch
                        {
                            await db.Ado.RollbackTranAsync();
                            throw;
                        }
                        break;

                    case "delete":
                        {
                            var removalPlans = new List<
                                (string UserGuid, EmployeeProfileDataRemovalPlan Plan)
                            >();
                            {
                                // 关键逻辑：批量删除按 UserGUID 全序取得两层锁，避免两个批次反序等待。
                                await using var sensitiveChangeLocks = await EmployeeProfileMediaLock
                                    .AcquireManyAsync(
                                        db,
                                        userGuids,
                                        "sensitive-change",
                                        _logger
                                    );
                                await using var lifecycleLocks = await EmployeeProfileMediaLock
                                    .AcquireProfileLifecycleManyAsync(db, userGuids, _logger);
                                await db.Ado.BeginTranAsync();
                                try
                                {
                                    // 锁内重新确认当前仍存在的目标，避免等待锁期间误删已变化的用户状态。
                                    var existingUserGuids = await db.Queryable<User>()
                                        .Where(item =>
                                            userGuids.Contains(item.UserGUID) && !item.IsDeleted
                                        )
                                        .Select(item => item.UserGUID)
                                        .ToListAsync();
                                    if (existingUserGuids.Count != userGuids.Length)
                                    {
                                        return await RollbackUserUpdateAsync<bool>(
                                            db,
                                            "用户不存在",
                                            "USER_NOT_FOUND"
                                        );
                                    }
                                    foreach (var targetUserGuid in existingUserGuids)
                                    {
                                        var decision = await ValidateUserProfileMutationTargetAsync(
                                            db,
                                            targetUserGuid,
                                            allowSelf: false
                                        );
                                        if (!decision.IsAllowed)
                                        {
                                            return await RollbackUserUpdateAsync<bool>(
                                                db,
                                                decision.Message,
                                                decision.ErrorCode
                                            );
                                        }

                                        var profile = await db.Queryable<EmployeeProfile>()
                                            .FirstAsync(item => item.UserGUID == targetUserGuid);
                                        if (_sensitiveChangeService is not null)
                                        {
                                            var plan = await _sensitiveChangeService
                                                .PrepareProfileDataRemovalWithinTransactionAsync(
                                                    targetUserGuid,
                                                    profile,
                                                    "System"
                                                );
                                            removalPlans.Add((targetUserGuid, plan));
                                        }
                                    }

                                    await db.Deleteable<UserRole>()
                                        .Where(ur => existingUserGuids.Contains(ur.UserGUID))
                                        .ExecuteCommandAsync();
                                    await db.Deleteable<UserStore>()
                                        .Where(us => existingUserGuids.Contains(us.UserGUID))
                                        .ExecuteCommandAsync();
                                    await db.Deleteable<RefreshToken>()
                                        .Where(rt => existingUserGuids.Contains(rt.UserGUID))
                                        .ExecuteCommandAsync();
                                    await db.Deleteable<EmployeeProfile>()
                                        .Where(item => existingUserGuids.Contains(item.UserGUID))
                                        .ExecuteCommandAsync();
                                    await db.Deleteable<User>()
                                        .Where(u => existingUserGuids.Contains(u.UserGUID))
                                        .ExecuteCommandAsync();
                                    await db.Ado.CommitTranAsync();
                                }
                                catch
                                {
                                    await db.Ado.RollbackTranAsync();
                                    throw;
                                }
                            }

                            if (_sensitiveChangeService is not null)
                            {
                                foreach (var (targetUserGuid, plan) in removalPlans)
                                {
                                    try
                                    {
                                        await _sensitiveChangeService
                                            .CleanupProfileDataRemovalAsync(targetUserGuid, plan);
                                    }
                                    catch (Exception cleanupException)
                                    {
                                        _logger.LogWarning(
                                            cleanupException,
                                            "清理已批量删除用户的员工资料对象失败，UserGUID: {UserGUID}",
                                            targetUserGuid
                                        );
                                    }
                                }
                            }
                        }
                        break;

                    default:
                        return ApiResponse<bool>.Error("不支持的操作类型", "UNSUPPORTED_OPERATION");
                }

                _logger.LogInformation(
                    "批量管理用户成功，Operation: {Operation}, UserCount: {UserCount}",
                    dto.Operation,
                    dto.UserGuids.Count
                );
                return ApiResponse<bool>.OK(true, $"批量{dto.Operation}操作成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量管理用户失败，Operation: {Operation}", dto.Operation);
                return ApiResponse<bool>.Error("批量操作失败", "BATCH_OPERATION_FAILED");
            }
        }

        /// <summary>
        /// 批量为多个用户添加 / 移除分店关联。
        /// 权限规则与单人分店分配一致：管理员不受限；店长只能操作自己管辖范围内的分店、
        /// 只能增删普通关联，可管理关联既不能授予也不能移除（原样保留并计入 ProtectedManageableCount）。
        /// 正式执行在一个可串行化事务里完成，任一目标不合法整批回滚；DryRun 只计算影响、不写入。
        /// </summary>
        public async Task<ApiResponse<BatchUserStoreOperationResultDto>> BatchManageUserStoresAsync(
            BatchUserStoreOperationDto dto
        )
        {
            var operation = dto.Operation?.Trim().ToLowerInvariant();
            if (operation is not ("add" or "remove"))
            {
                return ApiResponse<BatchUserStoreOperationResultDto>.Error(
                    "不支持的操作类型",
                    "UNSUPPORTED_OPERATION"
                );
            }

            var userGuids = NormalizeBatchGuids(dto.UserGuids);
            var storeGuids = NormalizeBatchGuids(dto.StoreGuids);
            if (userGuids.Length == 0 || storeGuids.Length == 0)
            {
                return ApiResponse<BatchUserStoreOperationResultDto>.Error(
                    "请选择用户和分店",
                    "VALIDATION_ERROR"
                );
            }
            if (
                userGuids.Length > BatchUserStoreOperationDto.MaxUserCount
                || storeGuids.Length > BatchUserStoreOperationDto.MaxStoreCount
            )
            {
                return ApiResponse<BatchUserStoreOperationResultDto>.Error(
                    $"一次最多处理 {BatchUserStoreOperationDto.MaxUserCount} 位用户、{BatchUserStoreOperationDto.MaxStoreCount} 家分店",
                    "BATCH_LIMIT_EXCEEDED"
                );
            }

            // 「设为可管理」只对添加有意义。
            var asManageable = operation == "add" && dto.AsManageable;

            try
            {
                var db = _context.Db;
                if (dto.DryRun)
                {
                    // 预演只读：不开串行化事务，避免在确认弹窗反复预览时持有范围锁。
                    return await ApplyBatchUserStoreOperationAsync(
                        db,
                        operation,
                        userGuids,
                        storeGuids,
                        asManageable,
                        dryRun: true
                    );
                }

                await db.Ado.BeginTranAsync(IsolationLevel.Serializable);
                try
                {
                    var result = await ApplyBatchUserStoreOperationAsync(
                        db,
                        operation,
                        userGuids,
                        storeGuids,
                        asManageable,
                        dryRun: false
                    );
                    if (result.Success)
                    {
                        await db.Ado.CommitTranAsync();
                        _logger.LogInformation(
                            "批量分店操作成功，Operation: {Operation}, UserCount: {UserCount}, StoreCount: {StoreCount}, Added: {Added}, Upgraded: {Upgraded}, Removed: {Removed}",
                            operation,
                            userGuids.Length,
                            storeGuids.Length,
                            result.Data!.AddedCount,
                            result.Data.UpgradedCount,
                            result.Data.RemovedCount
                        );
                    }
                    else
                    {
                        await db.Ado.RollbackTranAsync();
                    }
                    return result;
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量分店操作失败，Operation: {Operation}", operation);
                return ApiResponse<BatchUserStoreOperationResultDto>.Error(
                    "批量分店操作失败",
                    "BATCH_STORE_OPERATION_FAILED"
                );
            }
        }

        private static string[] NormalizeBatchGuids(IEnumerable<string>? values)
        {
            return (values ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal)
                .ToArray();
        }

        private async Task<ApiResponse<BatchUserStoreOperationResultDto>> ApplyBatchUserStoreOperationAsync(
            ISqlSugarClient db,
            string operation,
            string[] userGuids,
            string[] storeGuids,
            bool asManageable,
            bool dryRun
        )
        {
            ApiResponse<BatchUserStoreOperationResultDto> Fail(string message, string code) =>
                ApiResponse<BatchUserStoreOperationResultDto>.Error(message, code);

            var actor = await UserAccessMutationSecurity.ResolveActorAsync(
                db,
                _manageableStoreScopeService
            );
            if (asManageable && !actor.IsSuperAdmin)
            {
                return Fail("只有管理员可以授予分店管理关系", "MANAGEABLE_STORE_GRANT_DENIED");
            }
            if (actor.IsStoreManager)
            {
                var scopedStoreGuids = actor.StoreGuids.ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (storeGuids.Any(storeGuid => !scopedStoreGuids.Contains(storeGuid)))
                {
                    return Fail("不能分配非管辖分店", "STORE_SCOPE_DENIED");
                }
            }

            var validStoreCount = await db.Queryable<Store>()
                .Where(store => storeGuids.Contains(store.StoreGUID) && !store.IsDeleted)
                .CountAsync();
            if (validStoreCount != storeGuids.Length)
            {
                return Fail("包含不存在或已删除的分店", "STORE_NOT_FOUND");
            }

            var users = await db.Queryable<User>()
                .Where(user => userGuids.Contains(user.UserGUID) && !user.IsDeleted)
                .ToListAsync();
            if (users.Count != userGuids.Length)
            {
                return Fail("用户不存在", "USER_NOT_FOUND");
            }
            var usersByGuid = users.ToDictionary(user => user.UserGUID, StringComparer.OrdinalIgnoreCase);

            // 逐个复用单人接口的目标校验（本人、高权限账号、管辖范围），任一不通过整批拒绝并指明是谁。
            foreach (var userGuid in userGuids)
            {
                var decision = await UserAccessMutationSecurity.ValidateTargetAsync(db, actor, userGuid);
                if (!decision.IsAllowed)
                {
                    return Fail($"{usersByGuid[userGuid].Username}：{decision.Message}", decision.ErrorCode);
                }
            }

            var existingRows = await db.Queryable<UserStore>()
                .Where(item =>
                    userGuids.Contains(item.UserGUID)
                    && storeGuids.Contains(item.StoreGUID)
                    && !item.IsDeleted
                )
                .ToListAsync();
            var existingByPair = existingRows
                .GroupBy(item => (User: item.UserGUID.ToLowerInvariant(), Store: item.StoreGUID.ToLowerInvariant()))
                .ToDictionary(group => group.Key, group => group.ToList());

            // 每位用户操作前持有的全部可管理分店，用来判断店长角色会不会因此增减。
            var primaryRows = await db.Queryable<UserStore>()
                .Where(item => userGuids.Contains(item.UserGUID) && item.IsPrimary && !item.IsDeleted)
                .ToListAsync();
            var primaryStoresBefore = userGuids.ToDictionary(
                userGuid => userGuid,
                userGuid => primaryRows
                    .Where(item => string.Equals(item.UserGUID, userGuid, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.StoreGUID)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase
            );
            var primaryStoresAfter = primaryStoresBefore.ToDictionary(
                pair => pair.Key,
                pair => new HashSet<string>(pair.Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase
            );

            var result = new BatchUserStoreOperationResultDto { DryRun = dryRun };
            var rowsToInsert = new List<UserStore>();
            var rowGuidsToUpgrade = new List<string>();
            var rowGuidsToDelete = new List<string>();
            var affectedUserGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var now = DateTime.UtcNow;

            foreach (var userGuid in userGuids)
            {
                foreach (var storeGuid in storeGuids)
                {
                    existingByPair.TryGetValue(
                        (userGuid.ToLowerInvariant(), storeGuid.ToLowerInvariant()),
                        out var rows
                    );
                    var hasRelation = rows is { Count: > 0 };
                    var hasManageable = hasRelation && rows!.Any(item => item.IsPrimary);

                    if (operation == "add")
                    {
                        if (!hasRelation)
                        {
                            rowsToInsert.Add(new UserStore
                            {
                                UserStoreGUID = Guid.NewGuid().ToString(),
                                UserGUID = usersByGuid[userGuid].UserGUID,
                                StoreGUID = storeGuid,
                                IsPrimary = asManageable,
                                CreatedAt = now,
                                UpdatedAt = now,
                            });
                            result.AddedCount++;
                        }
                        else if (asManageable && !hasManageable)
                        {
                            // 已有普通关联：升级为可管理，不新建重复行。
                            rowGuidsToUpgrade.Add(rows![0].UserStoreGUID);
                            result.UpgradedCount++;
                        }
                        else
                        {
                            // 添加从不降级：已是可管理的关联保持不变。
                            result.UnchangedCount++;
                            continue;
                        }

                        if (asManageable)
                        {
                            primaryStoresAfter[userGuid].Add(storeGuid);
                        }
                        affectedUserGuids.Add(userGuid);
                        continue;
                    }

                    if (!hasRelation)
                    {
                        result.UnchangedCount++;
                        continue;
                    }
                    if (hasManageable && !actor.IsSuperAdmin)
                    {
                        // 店长不能撤销分店管理关系，与单人分配接口一致：原行保留。
                        result.ProtectedManageableCount++;
                        continue;
                    }

                    rowGuidsToDelete.AddRange(rows!.Select(item => item.UserStoreGUID));
                    result.RemovedCount++;
                    if (hasManageable)
                    {
                        result.RemovedManageableCount++;
                        primaryStoresAfter[userGuid].Remove(storeGuid);
                    }
                    affectedUserGuids.Add(userGuid);
                }
            }

            var managerRoleChangedUserGuids = userGuids
                .Where(userGuid =>
                    (primaryStoresBefore[userGuid].Count > 0) != (primaryStoresAfter[userGuid].Count > 0)
                )
                .ToList();
            result.UsersLosingStoreManagerRole = managerRoleChangedUserGuids
                .Where(userGuid => primaryStoresBefore[userGuid].Count > 0)
                .Select(userGuid => usersByGuid[userGuid].Username)
                .ToList();
            result.UsersGainingStoreManagerRole = managerRoleChangedUserGuids
                .Where(userGuid => primaryStoresAfter[userGuid].Count > 0)
                .Select(userGuid => usersByGuid[userGuid].Username)
                .ToList();
            result.AffectedUserCount = affectedUserGuids.Count;

            if (dryRun)
            {
                return ApiResponse<BatchUserStoreOperationResultDto>.OK(result, "预演完成");
            }

            if (rowsToInsert.Count > 0)
            {
                await db.Insertable(rowsToInsert).ExecuteCommandAsync();
            }
            if (rowGuidsToUpgrade.Count > 0)
            {
                var upgradeGuids = rowGuidsToUpgrade.ToArray();
                await db.Updateable<UserStore>()
                    .SetColumns(item => new UserStore { IsPrimary = true, UpdatedAt = now })
                    .Where(item => upgradeGuids.Contains(item.UserStoreGUID))
                    .ExecuteCommandAsync();
            }
            if (rowGuidsToDelete.Count > 0)
            {
                var deleteGuids = rowGuidsToDelete.ToArray();
                await db.Deleteable<UserStore>()
                    .Where(item => deleteGuids.Contains(item.UserStoreGUID))
                    .ExecuteCommandAsync();
            }

            // 可管理关系有增减的用户同步派生的店长角色（只有管理员能改可管理关系）。
            foreach (var userGuid in affectedUserGuids)
            {
                if (!primaryStoresBefore[userGuid].SetEquals(primaryStoresAfter[userGuid]))
                {
                    await UserStoreManagerRoleSynchronizer.SynchronizeAsync(db, usersByGuid[userGuid].UserGUID);
                }
            }

            return ApiResponse<BatchUserStoreOperationResultDto>.OK(result, "批量分店操作成功");
        }

        /// <summary>
        /// 导入用户（简化实现）
        /// </summary>
        public async Task<ApiResponse<ImportUserResultDto>> ImportUsersAsync(
            List<ImportUserDto> users
        )
        {
            var result = new ImportUserResultDto { TotalCount = users.Count };

            try
            {
                foreach (var importUser in users)
                {
                    try
                    {
                        var createDto = new CreateUserDto
                        {
                            Username = importUser.Username,
                            Email = importUser.Email,
                            FullName = importUser.FullName,
                            // 导入密码按原始密码交给后端统一慢哈希，保持与新登录协议一致。
                            Password = importUser.Password,
                            PasswordFormat = PasswordHasher.PasswordFormatRaw,
                            IsActive = true,
                        };

                        var createResult = await CreateUserAsync(createDto);
                        if (createResult.IsSuccess)
                        {
                            result.SuccessCount++;
                            result.ImportedUsers.Add(createResult.Data!);
                        }
                        else
                        {
                            result.FailureCount++;
                            result.Errors.Add(
                                $"用户 {importUser.Username}: {createResult.Message}"
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        result.FailureCount++;
                        result.Errors.Add($"用户 {importUser.Username}: {ex.Message}");
                    }
                }

                return ApiResponse<ImportUserResultDto>.OK(
                    result,
                    $"导入完成，成功: {result.SuccessCount}, 失败: {result.FailureCount}"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导入用户失败");
                return ApiResponse<ImportUserResultDto>.Error(
                    "导入用户失败",
                    "IMPORT_USERS_FAILED"
                );
            }
        }

        /// <summary>
        /// 导出用户
        /// </summary>
        public async Task<ApiResponse<List<UserDto>>> ExportUsersAsync(UserQueryDto query)
        {
            try
            {
                query.PageSize = int.MaxValue; // 获取所有数据
                query.PageNumber = 1;

                var result = await GetUsersAsync(query);
                if (result.IsSuccess)
                {
                    return ApiResponse<List<UserDto>>.OK(
                        result.Data?.Items ?? new List<UserDto>(),
                        "导出用户数据成功"
                    );
                }

                return ApiResponse<List<UserDto>>.Error(result.Message, result.ErrorCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出用户失败");
                return ApiResponse<List<UserDto>>.Error("导出用户失败", "EXPORT_USERS_FAILED");
            }
        }

        /// <summary>
        /// 获取用户统计信息
        /// </summary>
        public async Task<ApiResponse<UserStatisticsDto>> GetUserStatisticsAsync()
        {
            try
            {
                var db = _context.Db;
                var storeScope = await ResolveUserListStoreScopeAsync(db);
                if (storeScope.ReturnEmptyPage)
                {
                    return ApiResponse<UserStatisticsDto>.OK(new UserStatisticsDto(), "获取用户统计成功");
                }

                var visibleUserQuery = db.Queryable<User>().Where(user => !user.IsDeleted);
                if (storeScope.StoreGuids.Count > 0)
                {
                    var scopedStoreGuids = storeScope.StoreGuids.ToArray();
                    var scopedUserGuids = await db.Queryable<UserStore>()
                        .Where(userStore =>
                            !userStore.IsDeleted && scopedStoreGuids.Contains(userStore.StoreGUID)
                        )
                        .Select(userStore => userStore.UserGUID)
                        .Distinct()
                        .ToListAsync();
                    if (scopedUserGuids.Count == 0)
                    {
                        return ApiResponse<UserStatisticsDto>.OK(new UserStatisticsDto(), "获取用户统计成功");
                    }

                    var excluded = await ResolveScopedHighPrivilegeUserGuidsAsync(db);
                    visibleUserQuery = visibleUserQuery.Where(user =>
                        scopedUserGuids.Contains(user.UserGUID)
                        && !excluded.Contains(user.UserGUID)
                    );
                }

                // 统计只读取用户标识和状态，关系表也只读取去重后的用户标识，避免构造完整列表 DTO。
                var visibleUsers = await visibleUserQuery
                    .Select(user => new User
                    {
                        UserGUID = user.UserGUID,
                        IsActive = user.IsActive,
                    })
                    .ToListAsync();
                var visibleUserGuids = visibleUsers.Select(user => user.UserGUID).ToArray();
                var usersWithRoles = visibleUserGuids.Length == 0
                    ? new List<string>()
                    : await db.Queryable<UserRole>()
                        .InnerJoin<Role>((userRole, role) => userRole.RoleGUID == role.RoleGUID)
                        .Where((userRole, role) =>
                            visibleUserGuids.Contains(userRole.UserGUID)
                            && !userRole.IsDeleted
                            && !role.IsDeleted
                            && role.IsActive
                        )
                        .Select((userRole, role) => userRole.UserGUID)
                        .Distinct()
                        .ToListAsync();
                var usersWithStores = visibleUserGuids.Length == 0
                    ? new List<string>()
                    : await db.Queryable<UserStore>()
                        .InnerJoin<Store>((userStore, store) => userStore.StoreGUID == store.StoreGUID)
                        .Where((userStore, store) =>
                            visibleUserGuids.Contains(userStore.UserGUID)
                            && !userStore.IsDeleted
                            && !store.IsDeleted
                        )
                        .Select((userStore, store) => userStore.UserGUID)
                        .Distinct()
                        .ToListAsync();
                var totalUsers = visibleUsers.Count;
                var activeUsers = visibleUsers.Count(user => user.IsActive);
                var inactiveUsers = totalUsers - activeUsers;
                var usersWithoutRoles = totalUsers - usersWithRoles.Count;
                var usersWithoutStores = totalUsers - usersWithStores.Count;

                var statistics = new UserStatisticsDto
                {
                    TotalUsers = totalUsers,
                    ActiveUsers = activeUsers,
                    InactiveUsers = inactiveUsers,
                    UsersWithoutRoles = usersWithoutRoles,
                    UsersWithoutStores = usersWithoutStores,
                };

                return ApiResponse<UserStatisticsDto>.OK(statistics, "获取用户统计成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户统计失败");
                return ApiResponse<UserStatisticsDto>.Error(
                    "获取用户统计失败",
                    "GET_USER_STATISTICS_FAILED"
                );
            }
        }

        /// <summary>
        /// 检查用户名是否可用
        /// </summary>
        public async Task<ApiResponse<bool>> IsUsernameAvailableAsync(
            string username,
            string? excludeUserGuid = null
        )
        {
            try
            {
                var db = _context.Db;
                var usernameLower = (username ?? string.Empty).Trim().ToLowerInvariant();
                var query = db.Queryable<User>().Where(u => u.Username.ToLower() == usernameLower);

                if (!string.IsNullOrEmpty(excludeUserGuid))
                {
                    query = query.Where(u => u.UserGUID != excludeUserGuid);
                }

                var exists = await query.AnyAsync();
                return ApiResponse<bool>.OK(!exists, exists ? "用户名已存在" : "用户名可用");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查用户名可用性失败，Username: {Username}", username);
                return ApiResponse<bool>.Error("检查用户名失败", "CHECK_USERNAME_FAILED");
            }
        }

        /// <summary>
        /// 检查邮箱是否可用
        /// </summary>
        public async Task<ApiResponse<bool>> IsEmailAvailableAsync(
            string email,
            string? excludeUserGuid = null
        )
        {
            try
            {
                var db = _context.Db;
                var query = db.Queryable<User>().Where(u => u.Email == email);

                if (!string.IsNullOrEmpty(excludeUserGuid))
                {
                    query = query.Where(u => u.UserGUID != excludeUserGuid);
                }

                var exists = await query.AnyAsync();
                return ApiResponse<bool>.OK(!exists, exists ? "邮箱已存在" : "邮箱可用");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查邮箱可用性失败，Email: {Email}", email);
                return ApiResponse<bool>.Error("检查邮箱失败", "CHECK_EMAIL_FAILED");
            }
        }

        /// <summary>
        /// 重置用户密码
        /// </summary>
        public async Task<ApiResponse<string>> ResetUserPasswordAsync(string userGuid)
        {
            var db = _context.Db;
            try
            {
                await db.Ado.BeginTranAsync(IsolationLevel.Serializable);
                var target = await db.Queryable<User>()
                    .Where(user => user.UserGUID == userGuid && !user.IsDeleted)
                    .FirstAsync();
                if (target == null)
                {
                    await db.Ado.RollbackTranAsync();
                    return ApiResponse<string>.Error("用户不存在", "USER_NOT_FOUND");
                }

                var targetDecision = await ValidateUserProfileMutationTargetAsync(db, userGuid);
                if (!targetDecision.IsAllowed)
                {
                    await db.Ado.RollbackTranAsync();
                    return ApiResponse<string>.Error(targetDecision.Message, targetDecision.ErrorCode);
                }

                // 生成新密码
                var newPassword = GenerateRandomPassword();
                var hashedPassword = PasswordHasher.HashPassword(newPassword);

                var result = await db.Updateable<User>()
                    .SetColumns(u => new User
                    {
                        PasswordHash = hashedPassword,
                        UpdatedAt = DateTime.UtcNow,
                    })
                    .Where(u => u.UserGUID == userGuid && !u.IsDeleted)
                    .ExecuteCommandAsync();

                if (result == 0)
                {
                    await db.Ado.RollbackTranAsync();
                    return ApiResponse<string>.Error("用户不存在", "USER_NOT_FOUND");
                }

                await db.Ado.CommitTranAsync();

                _logger.LogInformation("重置用户密码成功，UserGUID: {UserGUID}", userGuid);
                return ApiResponse<string>.OK(newPassword, "密码重置成功");
            }
            catch (Exception ex)
            {
                try
                {
                    await db.Ado.RollbackTranAsync();
                }
                catch
                {
                    // 回滚失败不覆盖原始重置错误。
                }
                _logger.LogError(ex, "重置用户密码失败，UserGUID: {UserGUID}", userGuid);
                return ApiResponse<string>.Error("重置密码失败", "RESET_PASSWORD_FAILED");
            }
        }

        /// <summary>
        /// 锁定/解锁用户
        /// </summary>
        public async Task<ApiResponse<bool>> LockUserAsync(string userGuid, bool isLocked)
        {
            try
            {
                // 这里可以实现用户锁定逻辑，暂时使用IsActive字段代替
                return await UpdateUserStatusByGuidAsync(userGuid, !isLocked);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "锁定/解锁用户失败，UserGUID: {UserGUID}, IsLocked: {IsLocked}",
                    userGuid,
                    isLocked
                );
                return ApiResponse<bool>.Error("锁定/解锁用户失败", "LOCK_USER_FAILED");
            }
        }

        #region 私有方法


        /// <summary>
        /// 生成随机密码
        /// </summary>
        private string GenerateRandomPassword()
        {
            const string chars =
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!@#$%^&*";
            var random = new Random();
            return new string(
                Enumerable.Repeat(chars, 12).Select(s => s[random.Next(s.Length)]).ToArray()
            );
        }

        #endregion
    }
}
