using System.Data;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Services
{
    /// <summary>
    /// 角色管理服务实现
    /// 🔐 提供角色数据的CRUD操作和权限管理功能
    /// </summary>
    public class RoleService : IRoleService
    {
        private readonly SqlSugarContext _context;
        private readonly ILogger<RoleService> _logger;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ICurrentUserManageableStoreScopeService? _manageableStoreScopeService;

        // 系统预定义权限
        private readonly List<PermissionCategoryDto> _systemPermissions = new()
        {
            new PermissionCategoryDto
            {
                Category = "user_management",
                DisplayName = "用户管理",
                Description = "用户相关的权限",
                Permissions = new List<PermissionDto>
                {
                    new()
                    {
                        Name = "user.view",
                        DisplayName = "查看用户",
                        Description = "查看用户列表和详情",
                        Category = "user_management",
                        IsSystemPermission = true,
                        CreatedAt = DateTime.Now,
                        CreatedBy = "System",
                    },
                    new()
                    {
                        Name = "user.create",
                        DisplayName = "创建用户",
                        Description = "创建新用户",
                        Category = "user_management",
                        IsSystemPermission = true,
                        CreatedAt = DateTime.Now,
                        CreatedBy = "System",
                    },
                    new()
                    {
                        Name = "user.edit",
                        DisplayName = "编辑用户",
                        Description = "修改用户信息",
                        Category = "user_management",
                        IsSystemPermission = true,
                        CreatedAt = DateTime.Now,
                        CreatedBy = "System",
                    },
                    new()
                    {
                        Name = "user.delete",
                        DisplayName = "删除用户",
                        Description = "删除用户",
                        Category = "user_management",
                        IsSystemPermission = true,
                        CreatedAt = DateTime.Now,
                        CreatedBy = "System",
                    },
                    new()
                    {
                        Name = "user.manage_roles",
                        DisplayName = "管理用户角色",
                        Description = "分配和移除用户角色",
                        Category = "user_management",
                        IsSystemPermission = true,
                        CreatedAt = DateTime.Now,
                        CreatedBy = "System",
                    },
                    new()
                    {
                        Name = "user.manage_stores",
                        DisplayName = "管理用户分店",
                        Description = "分配和移除用户分店",
                        Category = "user_management",
                        IsSystemPermission = true,
                        CreatedAt = DateTime.Now,
                        CreatedBy = "System",
                    },
                    new()
                    {
                        Name = "user.reset_password",
                        DisplayName = "重置密码",
                        Description = "重置用户密码",
                        Category = "user_management",
                        IsSystemPermission = true,
                        CreatedAt = DateTime.Now,
                        CreatedBy = "System",
                    },
                },
            },
            new PermissionCategoryDto
            {
                Category = "role_management",
                DisplayName = "角色管理",
                Description = "角色相关的权限",
                Permissions = new List<PermissionDto>
                {
                    new()
                    {
                        Name = "role.view",
                        DisplayName = "查看角色",
                        Description = "查看角色列表和详情",
                        Category = "role_management",
                        IsSystemPermission = true,
                        CreatedAt = DateTime.Now,
                        CreatedBy = "System",
                    },
                    new()
                    {
                        Name = "role.create",
                        DisplayName = "创建角色",
                        Description = "创建新角色",
                        Category = "role_management",
                        IsSystemPermission = true,
                        CreatedAt = DateTime.Now,
                        CreatedBy = "System",
                    },
                    new()
                    {
                        Name = "role.edit",
                        DisplayName = "编辑角色",
                        Description = "修改角色信息",
                        Category = "role_management",
                        IsSystemPermission = true,
                        CreatedAt = DateTime.Now,
                        CreatedBy = "System",
                    },
                    new()
                    {
                        Name = "role.delete",
                        DisplayName = "删除角色",
                        Description = "删除角色",
                        Category = "role_management",
                        IsSystemPermission = true,
                        CreatedAt = DateTime.Now,
                        CreatedBy = "System",
                    },
                    new()
                    {
                        Name = "role.manage_permissions",
                        DisplayName = "管理角色权限",
                        Description = "分配和移除角色权限",
                        Category = "role_management",
                        IsSystemPermission = true,
                        CreatedAt = DateTime.Now,
                        CreatedBy = "System",
                    },
                    new()
                    {
                        Name = "role.manage_users",
                        DisplayName = "管理角色用户",
                        Description = "为角色添加和移除用户",
                        Category = "role_management",
                        IsSystemPermission = true,
                        CreatedAt = DateTime.Now,
                        CreatedBy = "System",
                    },
                },
            },
            new PermissionCategoryDto
            {
                Category = "store_management",
                DisplayName = "分店管理",
                Description = "分店相关的权限",
                Permissions = new List<PermissionDto>
                {
                    new()
                    {
                        Name = "store.view",
                        DisplayName = "查看分店",
                        Description = "查看分店列表和详情",
                        Category = "store_management",
                        IsSystemPermission = true,
                        CreatedAt = DateTime.Now,
                        CreatedBy = "System",
                    },
                    new()
                    {
                        Name = "store.create",
                        DisplayName = "创建分店",
                        Description = "创建新分店",
                        Category = "store_management",
                        IsSystemPermission = true,
                        CreatedAt = DateTime.Now,
                        CreatedBy = "System",
                    },
                    new()
                    {
                        Name = "store.edit",
                        DisplayName = "编辑分店",
                        Description = "修改分店信息",
                        Category = "store_management",
                        IsSystemPermission = true,
                        CreatedAt = DateTime.Now,
                        CreatedBy = "System",
                    },
                    new()
                    {
                        Name = "store.delete",
                        DisplayName = "删除分店",
                        Description = "删除分店",
                        Category = "store_management",
                        IsSystemPermission = true,
                        CreatedAt = DateTime.Now,
                        CreatedBy = "System",
                    },
                    new()
                    {
                        Name = "store.manage_users",
                        DisplayName = "管理分店用户",
                        Description = "为分店添加和移除用户",
                        Category = "store_management",
                        IsSystemPermission = true,
                        CreatedAt = DateTime.Now,
                        CreatedBy = "System",
                    },
                    new()
                    {
                        Name = "store.sync",
                        DisplayName = "同步分店数据",
                        Description = "从HQ同步分店数据",
                        Category = "store_management",
                        IsSystemPermission = true,
                        CreatedAt = DateTime.Now,
                        CreatedBy = "System",
                    },
                },
            },
            new PermissionCategoryDto
            {
                Category = "system_management",
                DisplayName = "系统管理",
                Description = "系统相关的权限",
                Permissions = new List<PermissionDto>
                {
                    new()
                    {
                        Name = "system.view_logs",
                        DisplayName = "查看日志",
                        Description = "查看系统日志",
                        Category = "system_management",
                        IsSystemPermission = true,
                        CreatedAt = DateTime.Now,
                        CreatedBy = "System",
                    },
                    new()
                    {
                        Name = "system.manage_settings",
                        DisplayName = "管理设置",
                        Description = "管理系统设置",
                        Category = "system_management",
                        IsSystemPermission = true,
                        CreatedAt = DateTime.Now,
                        CreatedBy = "System",
                    },
                    new()
                    {
                        Name = "system.backup",
                        DisplayName = "备份数据",
                        Description = "备份系统数据",
                        Category = "system_management",
                        IsSystemPermission = true,
                        CreatedAt = DateTime.Now,
                        CreatedBy = "System",
                    },
                    new()
                    {
                        Name = "system.restore",
                        DisplayName = "恢复数据",
                        Description = "恢复系统数据",
                        Category = "system_management",
                        IsSystemPermission = true,
                        CreatedAt = DateTime.Now,
                        CreatedBy = "System",
                    },
                },
            },
        };

        public RoleService(
            SqlSugarContext context,
            ILogger<RoleService> logger,
            IHttpContextAccessor httpContextAccessor,
            ICurrentUserManageableStoreScopeService? manageableStoreScopeService = null
        )
        {
            _context = context;
            _logger = logger;
            _httpContextAccessor = httpContextAccessor;
            _manageableStoreScopeService = manageableStoreScopeService;
        }

        private string GetCurrentUsername()
        {
            return _httpContextAccessor.HttpContext?.User?.Identity?.Name ?? "System";
        }

        private static bool IsSuperAdminRoleName(string? roleName)
        {
            return Permissions.IsSuperAdminRole(roleName);
        }

        private async Task<string[]?> ResolveRoleMemberReadScopeAsync(ISqlSugarClient db)
        {
            if (_manageableStoreScopeService == null)
            {
                return null;
            }

            var scope = await _manageableStoreScopeService.GetScopeAsync();
            if (!scope.IsAuthenticated || string.IsNullOrWhiteSpace(scope.UserGuid))
            {
                return Array.Empty<string>();
            }

            if (!scope.IsStoreManager)
            {
                return null;
            }

            var managedStoreGuids = scope.StoreGuids
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (!scope.IsAllowed || managedStoreGuids.Length == 0)
            {
                return Array.Empty<string>();
            }

            var scopedUserGuids = await db.Queryable<UserStore>()
                    .InnerJoin<User>((userStore, user) => userStore.UserGUID == user.UserGUID)
                    .Where((userStore, user) =>
                        !userStore.IsDeleted
                        && !user.IsDeleted
                        && managedStoreGuids.Contains(userStore.StoreGUID)
                    )
                    .Select((userStore, user) => userStore.UserGUID)
                    .Distinct()
                    .ToListAsync();
            var highPrivilegeUserGuids = await UserAccessMutationSecurity.GetHighPrivilegeUserGuidsAsync(
                db,
                scopedUserGuids
            );
            return scopedUserGuids
                .Where(userGuid => !highPrivilegeUserGuids.Contains(userGuid))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static List<string> IncludeBuiltInSalesDashboardPermissionCodes(
            IEnumerable<string> permissionCodes
        )
        {
            // 常规启动不会全量同步权限表；只把本次七个内建页面权限合并到运行时目录。
            return permissionCodes
                .Concat(
                    PermissionSeedData.SalesDashboardPermissions.Select(permission =>
                        permission.Code
                    )
                )
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsBuiltInSalesDashboardPermission(string permissionCode)
        {
            return PermissionSeedData.SalesDashboardPermissions.Any(permission =>
                permission.Code.Equals(permissionCode, StringComparison.OrdinalIgnoreCase)
            );
        }

        /// <summary>
        /// 获取角色列表（分页）
        /// </summary>
        public async Task<ApiResponse<PagedResult<RoleDto>>> GetRolesAsync(RoleQueryDto query)
        {
            try
            {
                var db = _context.Db;
                var roleQuery = db.Queryable<Role>();

                // 搜索条件
                if (!string.IsNullOrEmpty(query.SearchKeyword))
                {
                    roleQuery = roleQuery.Where(r =>
                        r.RoleName.Contains(query.SearchKeyword)
                        || (r.Description != null && r.Description.Contains(query.SearchKeyword))
                    );
                }

                // 状态筛选
                if (query.IsActive.HasValue)
                {
                    roleQuery = roleQuery.Where(r => r.IsActive == query.IsActive.Value);
                }

                // 排序
                if (!string.IsNullOrEmpty(query.SortBy))
                {
                    if (query.SortDirection?.ToLower() == "asc")
                    {
                        roleQuery = query.SortBy.ToLower() switch
                        {
                            "rolename" => roleQuery.OrderBy(r => r.RoleName),
                            "description" => roleQuery.OrderBy(r => r.Description),
                            "createdat" => roleQuery.OrderBy(r => r.CreatedAt),
                            _ => roleQuery.OrderBy(r => r.CreatedAt),
                        };
                    }
                    else
                    {
                        roleQuery = query.SortBy.ToLower() switch
                        {
                            "rolename" => roleQuery.OrderByDescending(r => r.RoleName),
                            "description" => roleQuery.OrderByDescending(r => r.Description),
                            "createdat" => roleQuery.OrderByDescending(r => r.CreatedAt),
                            _ => roleQuery.OrderByDescending(r => r.CreatedAt),
                        };
                    }
                }
                else
                {
                    roleQuery = roleQuery.OrderByDescending(r => r.CreatedAt);
                }

                // 获取总数
                var total = await roleQuery.CountAsync();

                // 分页查询
                var roles = await roleQuery
                    .Skip((query.Page - 1) * query.PageSize)
                    .Take(query.PageSize)
                    .Select(r => new RoleDto
                    {
                        UserCount = SqlFunc
                            .Subqueryable<UserRole>()
                            .Where(ur => ur.RoleGUID == r.RoleGUID)
                            .Count(),
                        RoleGUID = r.RoleGUID,
                        RoleName = r.RoleName,
                        Description = r.Description,
                        IsActive = r.IsActive,
                        CreatedAt = r.CreatedAt,
                        UpdatedAt = r.UpdatedAt ?? r.CreatedAt, // 处理可空DateTime
                    })
                    .ToListAsync();

                var result = new PagedResult<RoleDto>
                {
                    Items = roles,
                    Total = total,
                    Page = query.Page,
                    PageSize = query.PageSize,
                };

                return ApiResponse<PagedResult<RoleDto>>.OK(result, "获取角色列表成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取角色列表失败");
                return ApiResponse<PagedResult<RoleDto>>.Error(
                    "获取角色列表失败",
                    "GET_ROLES_FAILED"
                );
            }
        }

        /// <summary>
        /// 获取角色列表（高性能版本 - 使用复合查询）
        /// ⚡ 优化查询性能，解决N+1问题，使用单一复合查询
        /// </summary>
        public async Task<ApiResponse<PagedResult<RoleDto>>> GetRolesOptimizedAsync(
            RoleQueryDto query
        )
        {
            try
            {
                var db = _context.Db;

                // 构建复合查询，一次性获取角色和用户数量信息
                var baseQuery = db.Queryable<Role>()
                    .LeftJoin<UserRole>((r, ur) => r.RoleGUID == ur.RoleGUID);

                // 搜索条件
                if (!string.IsNullOrEmpty(query.SearchKeyword))
                {
                    baseQuery = baseQuery.Where(
                        (r, ur) =>
                            r.RoleName.Contains(query.SearchKeyword)
                            || (
                                r.Description != null && r.Description.Contains(query.SearchKeyword)
                            )
                    );
                }

                // 状态筛选
                if (query.IsActive.HasValue)
                {
                    baseQuery = baseQuery.Where((r, ur) => r.IsActive == query.IsActive.Value);
                }

                // 获取所有角色数据和用户统计
                var roleDataList = await baseQuery
                    .Select(
                        (r, ur) =>
                            new
                            {
                                Role = new RoleDto
                                {
                                    RoleGUID = r.RoleGUID,
                                    RoleName = r.RoleName,
                                    Description = r.Description,
                                    IsActive = r.IsActive,
                                    CreatedAt = r.CreatedAt,
                                    UpdatedAt = r.UpdatedAt ?? r.CreatedAt,
                                },
                                HasUser = ur.UserGUID != null,
                            }
                    )
                    .ToListAsync();

                // 合并相同角色的数据并统计用户数量
                var roleDict = new Dictionary<string, RoleDto>();
                var userCounts = new Dictionary<string, int>();

                foreach (var item in roleDataList)
                {
                    var roleGuid = item.Role.RoleGUID;

                    // 添加角色基本信息（只添加一次）
                    if (!roleDict.ContainsKey(roleGuid))
                    {
                        roleDict[roleGuid] = item.Role;
                        userCounts[roleGuid] = 0;
                    }

                    // 统计用户数量
                    if (item.HasUser)
                    {
                        userCounts[roleGuid]++;
                    }
                }

                // 将去重后的角色转换为列表并设置用户数量
                var distinctRoles = roleDict.Values.ToList();
                foreach (var role in distinctRoles)
                {
                    role.UserCount = userCounts.GetValueOrDefault(role.RoleGUID, 0);
                }

                // 排序
                if (!string.IsNullOrEmpty(query.SortBy))
                {
                    if (query.SortDirection?.ToLower() == "asc")
                    {
                        distinctRoles = query.SortBy.ToLower() switch
                        {
                            "rolename" => distinctRoles.OrderBy(r => r.RoleName).ToList(),
                            "createdat" => distinctRoles.OrderBy(r => r.CreatedAt).ToList(),
                            "usercount" => distinctRoles.OrderBy(r => r.UserCount).ToList(),
                            _ => distinctRoles.OrderBy(r => r.CreatedAt).ToList(),
                        };
                    }
                    else
                    {
                        distinctRoles = query.SortBy.ToLower() switch
                        {
                            "rolename" => distinctRoles.OrderByDescending(r => r.RoleName).ToList(),
                            "createdat" => distinctRoles
                                .OrderByDescending(r => r.CreatedAt)
                                .ToList(),
                            "usercount" => distinctRoles
                                .OrderByDescending(r => r.UserCount)
                                .ToList(),
                            _ => distinctRoles.OrderByDescending(r => r.CreatedAt).ToList(),
                        };
                    }
                }
                else
                {
                    distinctRoles = distinctRoles.OrderByDescending(r => r.CreatedAt).ToList();
                }

                // 分页
                var total = distinctRoles.Count;
                var pagedRoles = distinctRoles
                    .Skip((query.Page - 1) * query.PageSize)
                    .Take(query.PageSize)
                    .ToList();

                var result = new PagedResult<RoleDto>
                {
                    Items = pagedRoles,
                    Total = total,
                    Page = query.Page,
                    PageSize = query.PageSize,
                };

                return ApiResponse<PagedResult<RoleDto>>.OK(result, "获取角色列表成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取角色列表失败");
                return ApiResponse<PagedResult<RoleDto>>.Error(
                    "获取角色列表失败",
                    "GET_ROLES_FAILED"
                );
            }
        }

        /// <summary>
        /// 角色服务性能测试方法 - 比较不同查询方式的性能
        /// </summary>
        public async Task<ApiResponse<object>> PerformanceTestAsync(RoleQueryDto query)
        {
            try
            {
                // 测试原始方法
                var sw1 = System.Diagnostics.Stopwatch.StartNew();
                var result1 = await GetRolesAsync(query);
                sw1.Stop();

                // 测试优化方法
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                var result2 = await GetRolesOptimizedAsync(query);
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
                        HasStatusFilter = query.IsActive.HasValue,
                        TestTimestamp = DateTime.Now,
                    },
                };

                return ApiResponse<object>.OK(performanceReport, "角色服务性能测试完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "角色服务性能测试失败");
                return ApiResponse<object>.Error(
                    "角色服务性能测试失败",
                    "ROLE_PERFORMANCE_TEST_FAILED"
                );
            }
        }

        /// <summary>
        /// 获取所有活跃角色（不分页）
        /// </summary>
        public async Task<ApiResponse<List<RoleDto>>> GetActiveRolesAsync()
        {
            try
            {
                var db = _context.Db;

                var roles = await db.Queryable<Role>()
                    .Where(r => r.IsActive && !r.IsDeleted)
                    .OrderBy(r => r.RoleName)
                    .Select(r => new RoleDto
                    {
                        RoleGUID = r.RoleGUID,
                        RoleName = r.RoleName,
                        Description = r.Description,
                        IsActive = r.IsActive,
                        CreatedAt = r.CreatedAt,
                        UpdatedAt = r.UpdatedAt ?? r.CreatedAt,
                    })
                    .ToListAsync();

                return ApiResponse<List<RoleDto>>.OK(roles, "获取活跃角色成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取活跃角色失败");
                return ApiResponse<List<RoleDto>>.Error(
                    "获取活跃角色失败",
                    "GET_ACTIVE_ROLES_FAILED"
                );
            }
        }

        /// <summary>
        /// 根据GUID获取角色详情
        /// </summary>
        public async Task<ApiResponse<RoleDetailDto>> GetRoleByGuidAsync(string roleGuid)
        {
            try
            {
                var db = _context.Db;
                var role = await db.Queryable<Role>()
                    .Where(r => r.RoleGUID == roleGuid && !r.IsDeleted)
                    .FirstAsync();

                if (role == null)
                {
                    return ApiResponse<RoleDetailDto>.Error("角色不存在", "ROLE_NOT_FOUND");
                }

                var roleDetail = new RoleDetailDto
                {
                    RoleGUID = role.RoleGUID,
                    RoleName = role.RoleName,
                    Description = role.Description,
                    IsActive = role.IsActive,
                    CreatedAt = role.CreatedAt,
                    UpdatedAt = role.UpdatedAt ?? role.CreatedAt,
                };

                // 获取角色用户详情
                var visibleUserGuids = await ResolveRoleMemberReadScopeAsync(db);
                var usersQuery = db.Queryable<UserRole>()
                    .InnerJoin<User>((ur, u) => ur.UserGUID == u.UserGUID)
                    .Where((ur, u) =>
                        ur.RoleGUID == roleGuid && !ur.IsDeleted && !u.IsDeleted
                    );
                if (visibleUserGuids != null)
                {
                    usersQuery = usersQuery.Where((ur, u) =>
                        visibleUserGuids.Contains(u.UserGUID)
                    );
                }
                var users = await usersQuery
                    .Select(
                        (ur, u) =>
                            new RoleUserDto
                            {
                                UserGUID = u.UserGUID,
                                Username = u.Username,
                                Email = u.Email,
                                FullName = u.FullName,
                                GivenName = u.GivenName,
                                MiddleName = u.MiddleName,
                                FamilyName = u.FamilyName,
                                Nickname = u.Nickname,
                                IsActive = u.IsActive,
                                AssignedAt = ur.CreatedAt,
                            }
                    )
                    .ToListAsync();
                roleDetail.Users = users;

                // 获取用户数量
                roleDetail.UserCount = users.Count;

                // 从 SysRolePermission 表读取角色的权限代码列表
                var rolePermissions = await db.Queryable<SysRolePermission>()
                    .Where(rp => rp.RoleGuid == roleGuid && rp.IsDeleted == false)
                    .Select(rp => rp.PermissionCode)
                    .ToListAsync();
                roleDetail.Permissions = rolePermissions;

                return ApiResponse<RoleDetailDto>.OK(roleDetail, "获取角色详情成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取角色详情失败，RoleGUID: {RoleGUID}", roleGuid);
                return ApiResponse<RoleDetailDto>.Error(
                    "获取角色详情失败",
                    "GET_ROLE_DETAIL_FAILED"
                );
            }
        }

        /// <summary>
        /// 根据角色名获取角色
        /// </summary>
        public async Task<ApiResponse<RoleDto>> GetRoleByNameAsync(string roleName)
        {
            try
            {
                var db = _context.Db;
                var role = await db.Queryable<Role>()
                    .Where(r => r.RoleName == roleName)
                    .Select(r => new RoleDto
                    {
                        RoleGUID = r.RoleGUID,
                        RoleName = r.RoleName,
                        Description = r.Description,
                        IsActive = r.IsActive,
                        CreatedAt = r.CreatedAt,
                        UpdatedAt = r.UpdatedAt ?? r.CreatedAt,
                    })
                    .FirstAsync();

                if (role == null)
                {
                    return ApiResponse<RoleDto>.Error("角色不存在", "ROLE_NOT_FOUND");
                }

                return ApiResponse<RoleDto>.OK(role, "获取角色成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "根据角色名获取角色失败，RoleName: {RoleName}", roleName);
                return ApiResponse<RoleDto>.Error("获取角色失败", "GET_ROLE_BY_NAME_FAILED");
            }
        }

        /// <summary>
        /// 创建新角色
        /// </summary>
        public async Task<ApiResponse<RoleDto>> CreateRoleAsync(CreateRoleDto dto)
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
                    return ApiResponse<RoleDto>.Error(
                        adminDecision.Message,
                        adminDecision.ErrorCode
                    );
                }

                // 检查角色名是否已存在
                var existingRole = await db.Queryable<Role>()
                    .Where(r => r.RoleName == dto.RoleName)
                    .FirstAsync();

                if (existingRole != null)
                {
                    return ApiResponse<RoleDto>.Error("角色名已存在", "ROLE_NAME_EXISTS");
                }

                // 创建角色
                var role = new Role
                {
                    RoleGUID = Guid.NewGuid().ToString(),
                    RoleName = dto.RoleName,
                    Description = dto.Description,
                    IsActive = dto.IsActive,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now,
                };

                await db.Insertable(role).ExecuteCommandAsync();

                var result = new RoleDto
                {
                    RoleGUID = role.RoleGUID,
                    RoleName = role.RoleName,
                    Description = role.Description,
                    IsActive = role.IsActive,
                    CreatedAt = role.CreatedAt,
                    UpdatedAt = role.UpdatedAt ?? role.CreatedAt,
                    UserCount = 0,
                };

                _logger.LogInformation(
                    "创建角色成功，RoleGUID: {RoleGUID}, RoleName: {RoleName}",
                    role.RoleGUID,
                    role.RoleName
                );
                return ApiResponse<RoleDto>.OK(result, "创建角色成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建角色失败，RoleName: {RoleName}", dto.RoleName);
                return ApiResponse<RoleDto>.Error("创建角色失败", "CREATE_ROLE_FAILED");
            }
        }

        /// <summary>
        /// 根据GUID更新角色
        /// </summary>
        public async Task<ApiResponse<RoleDto>> UpdateRoleByGuidAsync(
            string roleGuid,
            UpdateRoleDto dto
        )
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
                    return ApiResponse<RoleDto>.Error(
                        adminDecision.Message,
                        adminDecision.ErrorCode
                    );
                }

                // 检查角色是否存在
                var role = await db.Queryable<Role>()
                    .Where(r => r.RoleGUID == roleGuid)
                    .FirstAsync();

                if (role == null)
                {
                    return ApiResponse<RoleDto>.Error("角色不存在", "ROLE_NOT_FOUND");
                }

                // 检查角色名是否被其他角色使用
                var existingRole = await db.Queryable<Role>()
                    .Where(r => r.RoleGUID != roleGuid && r.RoleName == dto.RoleName)
                    .FirstAsync();

                if (existingRole != null)
                {
                    return ApiResponse<RoleDto>.Error("角色名已被其他角色使用", "ROLE_NAME_EXISTS");
                }

                // 更新角色信息
                role.RoleName = dto.RoleName;
                role.Description = dto.Description;
                role.IsActive = dto.IsActive;
                role.UpdatedAt = DateTime.UtcNow;

                await db.Updateable(role).ExecuteCommandAsync();

                var result = new RoleDto
                {
                    RoleGUID = role.RoleGUID,
                    RoleName = role.RoleName,
                    Description = role.Description,
                    IsActive = role.IsActive,
                    CreatedAt = role.CreatedAt,
                    UpdatedAt = role.UpdatedAt ?? role.CreatedAt,
                };

                _logger.LogInformation("更新角色成功，RoleGUID: {RoleGUID}", roleGuid);
                return ApiResponse<RoleDto>.OK(result, "更新角色成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新角色失败，RoleGUID: {RoleGUID}", roleGuid);
                return ApiResponse<RoleDto>.Error("更新角色失败", "UPDATE_ROLE_FAILED");
            }
        }

        /// <summary>
        /// 根据GUID删除角色
        /// </summary>
        public async Task<ApiResponse<bool>> DeleteRoleByGuidAsync(string roleGuid)
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
                    return ApiResponse<bool>.Error(
                        adminDecision.Message,
                        adminDecision.ErrorCode
                    );
                }

                // 检查角色是否存在
                var role = await db.Queryable<Role>()
                    .Where(r => r.RoleGUID == roleGuid)
                    .FirstAsync();

                if (role == null)
                {
                    return ApiResponse<bool>.Error("角色不存在", "ROLE_NOT_FOUND");
                }

                // 检查是否有用户正在使用该角色
                var hasUsers = await db.Queryable<UserRole>()
                    .Where(ur => ur.RoleGUID == roleGuid)
                    .AnyAsync();

                if (hasUsers)
                {
                    return ApiResponse<bool>.Error("角色正在被使用，无法删除", "ROLE_IN_USE");
                }

                // 开启事务
                await db.Ado.BeginTranAsync();

                try
                {
                    // 删除角色用户关联（如果有的话）
                    await db.Deleteable<UserRole>()
                        .Where(ur => ur.RoleGUID == roleGuid)
                        .ExecuteCommandAsync();

                    // 删除角色
                    await db.Deleteable<Role>()
                        .Where(r => r.RoleGUID == roleGuid)
                        .ExecuteCommandAsync();

                    await db.Ado.CommitTranAsync();

                    _logger.LogInformation("删除角色成功，RoleGUID: {RoleGUID}", roleGuid);
                    return ApiResponse<bool>.OK(true, "删除角色成功");
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除角色失败，RoleGUID: {RoleGUID}", roleGuid);
                return ApiResponse<bool>.Error("删除角色失败", "DELETE_ROLE_FAILED");
            }
        }

        /// <summary>
        /// 根据GUID更新角色状态
        /// </summary>
        public async Task<ApiResponse<bool>> UpdateRoleStatusByGuidAsync(
            string roleGuid,
            bool isActive
        )
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
                    return ApiResponse<bool>.Error(
                        adminDecision.Message,
                        adminDecision.ErrorCode
                    );
                }

                var result = await db.Updateable<Role>()
                    .SetColumns(r => new Role { IsActive = isActive, UpdatedAt = DateTime.UtcNow })
                    .Where(r => r.RoleGUID == roleGuid)
                    .ExecuteCommandAsync();

                if (result == 0)
                {
                    return ApiResponse<bool>.Error("角色不存在", "ROLE_NOT_FOUND");
                }

                _logger.LogInformation(
                    "更新角色状态成功，RoleGUID: {RoleGUID}, IsActive: {IsActive}",
                    roleGuid,
                    isActive
                );
                return ApiResponse<bool>.OK(
                    true,
                    $"角色状态已更新为{(isActive ? "激活" : "禁用")}"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新角色状态失败，RoleGUID: {RoleGUID}", roleGuid);
                return ApiResponse<bool>.Error("更新角色状态失败", "UPDATE_ROLE_STATUS_FAILED");
            }
        }

        /// <summary>
        /// 获取角色的用户列表
        /// </summary>
        public async Task<ApiResponse<PagedResult<RoleUserDto>>> GetRoleUsersAsync(
            string roleGuid,
            RoleQueryDto query
        )
        {
            try
            {
                var db = _context.Db;
                var userQuery = db.Queryable<UserRole>()
                    .InnerJoin<User>((ur, u) => ur.UserGUID == u.UserGUID)
                    .Where((ur, u) =>
                        ur.RoleGUID == roleGuid && !ur.IsDeleted && !u.IsDeleted
                    );
                var visibleUserGuids = await ResolveRoleMemberReadScopeAsync(db);
                if (visibleUserGuids != null)
                {
                    userQuery = userQuery.Where((ur, u) =>
                        visibleUserGuids.Contains(u.UserGUID)
                    );
                }

                // 搜索条件
                if (!string.IsNullOrEmpty(query.SearchKeyword))
                {
                    userQuery = userQuery.Where(
                        (ur, u) =>
                            u.Username.Contains(query.SearchKeyword)
                            || u.Email.Contains(query.SearchKeyword)
                            || (u.FullName != null && u.FullName.Contains(query.SearchKeyword))
                            || (u.GivenName != null && u.GivenName.Contains(query.SearchKeyword))
                            || (u.MiddleName != null && u.MiddleName.Contains(query.SearchKeyword))
                            || (u.FamilyName != null && u.FamilyName.Contains(query.SearchKeyword))
                            || (u.Nickname != null && u.Nickname.Contains(query.SearchKeyword))
                    );
                }

                // 状态筛选
                if (query.IsActive.HasValue)
                {
                    userQuery = userQuery.Where((ur, u) => u.IsActive == query.IsActive.Value);
                }

                // 获取总数
                var total = await userQuery.CountAsync();

                // 分页查询
                var users = await userQuery
                    .OrderByDescending((ur, u) => ur.CreatedAt)
                    .Select(
                        (ur, u) =>
                            new RoleUserDto
                            {
                                UserGUID = u.UserGUID,
                                Username = u.Username,
                                Email = u.Email,
                                FullName = u.FullName,
                                GivenName = u.GivenName,
                                MiddleName = u.MiddleName,
                                FamilyName = u.FamilyName,
                                Nickname = u.Nickname,
                                IsActive = u.IsActive,
                                AssignedAt = ur.CreatedAt,
                            }
                    )
                    .Skip((query.Page - 1) * query.PageSize)
                    .Take(query.PageSize)
                    .ToListAsync();

                var result = new PagedResult<RoleUserDto>
                {
                    Items = users,
                    Total = total,
                    Page = query.Page,
                    PageSize = query.PageSize,
                };

                return ApiResponse<PagedResult<RoleUserDto>>.OK(result, "获取角色用户列表成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取角色用户列表失败，RoleGUID: {RoleGUID}", roleGuid);
                return ApiResponse<PagedResult<RoleUserDto>>.Error(
                    "获取角色用户列表失败",
                    "GET_ROLE_USERS_FAILED"
                );
            }
        }

        /// <summary>
        /// 为角色添加用户
        /// </summary>
        public async Task<ApiResponse<bool>> AddUsersToRoleAsync(
            string roleGuid,
            List<string> userGuids
        )
        {
            try
            {
                var db = _context.Db;
                var actor = await UserAccessMutationSecurity.ResolveActorAsync(
                    db,
                    _manageableStoreScopeService
                );
                if (!actor.IsSuperAdmin)
                {
                    return ApiResponse<bool>.Error(
                        "只有管理员可以维护用户角色",
                        "ADMIN_REQUIRED"
                    );
                }

                // 检查角色是否存在
                var role = await db.Queryable<Role>()
                    .Where(r => r.RoleGUID == roleGuid)
                    .FirstAsync();

                if (role == null)
                {
                    return ApiResponse<bool>.Error("角色不存在", "ROLE_NOT_FOUND");
                }
                if (Permissions.IsStoreManagerRole(role.RoleName))
                {
                    return ApiResponse<bool>.Error(
                        "店长角色由可管理分店关系维护",
                        "DERIVED_STORE_MANAGER_ROLE"
                    );
                }

                // 获取已存在的用户角色关联
                var existingUserRoles = await db.Queryable<UserRole>()
                    .Where(ur => ur.RoleGUID == roleGuid && userGuids.Contains(ur.UserGUID))
                    .Select(ur => ur.UserGUID)
                    .ToListAsync();

                // 筛选出需要添加的用户
                var newUserGuids = userGuids.Except(existingUserRoles).ToList();

                if (newUserGuids.Any())
                {
                    var userRoles = newUserGuids
                        .Select(userGuid => new UserRole
                        {
                            UserGUID = userGuid,
                            RoleGUID = roleGuid,
                            CreatedAt = DateTime.Now,
                            UpdatedAt = DateTime.Now,
                        })
                        .ToList();

                    await db.Insertable(userRoles).ExecuteCommandAsync();
                }

                _logger.LogInformation(
                    "为角色添加用户成功，RoleGUID: {RoleGUID}, AddedCount: {AddedCount}",
                    roleGuid,
                    newUserGuids.Count
                );
                return ApiResponse<bool>.OK(true, $"成功添加 {newUserGuids.Count} 个用户到角色");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "为角色添加用户失败，RoleGUID: {RoleGUID}", roleGuid);
                return ApiResponse<bool>.Error("添加用户失败", "ADD_USERS_TO_ROLE_FAILED");
            }
        }

        /// <summary>
        /// 从角色移除用户
        /// </summary>
        public async Task<ApiResponse<bool>> RemoveUserFromRoleAsync(
            string roleGuid,
            string userGuid
        )
        {
            try
            {
                var db = _context.Db;
                var actor = await UserAccessMutationSecurity.ResolveActorAsync(
                    db,
                    _manageableStoreScopeService
                );
                if (!actor.IsSuperAdmin)
                {
                    return ApiResponse<bool>.Error(
                        "只有管理员可以维护用户角色",
                        "ADMIN_REQUIRED"
                    );
                }

                var role = await db.Queryable<Role>()
                    .Where(item => item.RoleGUID == roleGuid && !item.IsDeleted)
                    .FirstAsync();
                if (role == null)
                {
                    return ApiResponse<bool>.Error("角色不存在", "ROLE_NOT_FOUND");
                }
                if (Permissions.IsStoreManagerRole(role.RoleName))
                {
                    return ApiResponse<bool>.Error(
                        "店长角色由可管理分店关系维护",
                        "DERIVED_STORE_MANAGER_ROLE"
                    );
                }

                var result = await db.Deleteable<UserRole>()
                    .Where(ur => ur.RoleGUID == roleGuid && ur.UserGUID == userGuid)
                    .ExecuteCommandAsync();

                if (result == 0)
                {
                    return ApiResponse<bool>.Error("用户角色关联不存在", "USER_ROLE_NOT_FOUND");
                }

                _logger.LogInformation(
                    "从角色移除用户成功，RoleGUID: {RoleGUID}, UserGUID: {UserGUID}",
                    roleGuid,
                    userGuid
                );
                return ApiResponse<bool>.OK(true, "移除用户成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "从角色移除用户失败，RoleGUID: {RoleGUID}, UserGUID: {UserGUID}",
                    roleGuid,
                    userGuid
                );
                return ApiResponse<bool>.Error("移除用户失败", "REMOVE_USER_FROM_ROLE_FAILED");
            }
        }

        /// <summary>
        /// 批量管理角色
        /// </summary>
        public async Task<ApiResponse<bool>> BatchManageRolesAsync(BatchRoleOperationDto dto)
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
                    return ApiResponse<bool>.Error(
                        adminDecision.Message,
                        adminDecision.ErrorCode
                    );
                }

                switch (dto.Operation.ToLower())
                {
                    case "activate":
                        await db.Updateable<Role>()
                            .SetColumns(r => new Role
                            {
                                IsActive = true,
                                UpdatedAt = DateTime.Now,
                            })
                            .Where(r => dto.RoleGuids.Contains(r.RoleGUID))
                            .ExecuteCommandAsync();
                        break;

                    case "deactivate":
                        await db.Updateable<Role>()
                            .SetColumns(r => new Role
                            {
                                IsActive = false,
                                UpdatedAt = DateTime.Now,
                            })
                            .Where(r => dto.RoleGuids.Contains(r.RoleGUID))
                            .ExecuteCommandAsync();
                        break;

                    case "delete":
                        // 检查是否有角色正在被使用
                        var rolesInUse = await db.Queryable<UserRole>()
                            .Where(ur => dto.RoleGuids.Contains(ur.RoleGUID))
                            .Select(ur => ur.RoleGUID)
                            .ToListAsync();

                        if (rolesInUse.Any())
                        {
                            return ApiResponse<bool>.Error(
                                "部分角色正在被使用，无法删除",
                                "ROLES_IN_USE"
                            );
                        }

                        // 开启事务
                        await db.Ado.BeginTranAsync();
                        try
                        {
                            // 删除角色用户关联
                            await db.Deleteable<UserRole>()
                                .Where(ur => dto.RoleGuids.Contains(ur.RoleGUID))
                                .ExecuteCommandAsync();

                            // 删除角色
                            await db.Deleteable<Role>()
                                .Where(r => dto.RoleGuids.Contains(r.RoleGUID))
                                .ExecuteCommandAsync();

                            await db.Ado.CommitTranAsync();
                        }
                        catch
                        {
                            await db.Ado.RollbackTranAsync();
                            throw;
                        }
                        break;

                    default:
                        return ApiResponse<bool>.Error("不支持的操作类型", "UNSUPPORTED_OPERATION");
                }

                _logger.LogInformation(
                    "批量管理角色成功，Operation: {Operation}, RoleCount: {RoleCount}",
                    dto.Operation,
                    dto.RoleGuids.Count
                );
                return ApiResponse<bool>.OK(true, $"批量{dto.Operation}操作成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量管理角色失败，Operation: {Operation}", dto.Operation);
                return ApiResponse<bool>.Error("批量操作失败", "BATCH_OPERATION_FAILED");
            }
        }

        /// <summary>
        /// 获取角色统计信息
        /// </summary>
        public async Task<ApiResponse<RoleStatisticsDto>> GetRoleStatisticsAsync()
        {
            try
            {
                var db = _context.Db;

                var statistics = new RoleStatisticsDto
                {
                    TotalRoles = await db.Queryable<Role>().CountAsync(),
                    ActiveRoles = await db.Queryable<Role>().Where(r => r.IsActive).CountAsync(),
                    InactiveRoles = await db.Queryable<Role>().Where(r => !r.IsActive).CountAsync(),
                };

                // 获取角色使用情况
                var roleUsage = await db.Queryable<Role>()
                    .LeftJoin<UserRole>((r, ur) => r.RoleGUID == ur.RoleGUID)
                    .GroupBy((r, ur) => new { r.RoleGUID, r.RoleName })
                    .Select(
                        (r, ur) =>
                            new RoleUsageDto
                            {
                                RoleGUID = r.RoleGUID,
                                RoleName = r.RoleName,
                                UserCount = SqlFunc.AggregateCount(ur.UserGUID),
                                IsSystemRole = false, // 暂时设为false，可以根据实际需求调整
                            }
                    )
                    .ToListAsync();

                statistics.RoleUsage = roleUsage;

                return ApiResponse<RoleStatisticsDto>.OK(statistics, "获取角色统计成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取角色统计失败");
                return ApiResponse<RoleStatisticsDto>.Error(
                    "获取角色统计失败",
                    "GET_ROLE_STATISTICS_FAILED"
                );
            }
        }

        /// <summary>
        /// 检查角色名是否可用
        /// </summary>
        public async Task<ApiResponse<bool>> IsRoleNameAvailableAsync(
            string roleName,
            string? excludeRoleGuid = null
        )
        {
            try
            {
                var db = _context.Db;
                var query = db.Queryable<Role>().Where(r => r.RoleName == roleName);

                if (!string.IsNullOrEmpty(excludeRoleGuid))
                {
                    query = query.Where(r => r.RoleGUID != excludeRoleGuid);
                }

                var exists = await query.AnyAsync();
                return ApiResponse<bool>.OK(!exists, exists ? "角色名已存在" : "角色名可用");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查角色名可用性失败，RoleName: {RoleName}", roleName);
                return ApiResponse<bool>.Error("检查角色名失败", "CHECK_ROLE_NAME_FAILED");
            }
        }

        /// <summary>
        /// 获取所有权限列表
        /// </summary>
        public async Task<ApiResponse<List<PermissionCategoryDto>>> GetPermissionsAsync()
        {
            var db = _context.Db;
            var adminDecision = await UserAccessMutationSecurity.ValidateAdminOperationAsync(
                db,
                _manageableStoreScopeService
            );
            return adminDecision.IsAllowed
                ? await GetPermissionsCoreAsync()
                : ApiResponse<List<PermissionCategoryDto>>.Error(
                    adminDecision.Message,
                    adminDecision.ErrorCode
                );
        }

        private async Task<ApiResponse<List<PermissionCategoryDto>>> GetPermissionsCoreAsync()
        {
            try
            {
                var db = _context.Db;
                var permissions = await db.Queryable<SysPermission>().Where(p => !p.IsDeleted).ToListAsync();

                var databasePermissionCodes = permissions
                    .Select(permission => permission.Code)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var permissionDtos = permissions
                    .Select(p => new PermissionDto
                    {
                        Name = p.Code,
                        DisplayName = p.Name,
                        Description = p.Description,
                        Category = p.Category,
                        IsSystemPermission = false,
                        Surfaces = PermissionSurfaceCatalog.ResolveNames(p.Code),
                        CreatedAt = p.CreatedAt,
                        CreatedBy = p.CreatedBy,
                        UpdatedAt = p.UpdatedAt,
                        UpdatedBy = p.UpdatedBy,
                    })
                    .Concat(
                        PermissionSeedData.SalesDashboardPermissions
                            .Where(seed => !databasePermissionCodes.Contains(seed.Code))
                            .Select(seed => new PermissionDto
                            {
                                Name = seed.Code,
                                DisplayName = seed.Name,
                                Description = seed.Description,
                                Category = seed.Category,
                                IsSystemPermission = true,
                                Surfaces = PermissionSurfaceCatalog.ResolveNames(seed.Code),
                                CreatedAt = DateTime.MinValue,
                                CreatedBy = "System",
                            })
                    );

                var result = permissionDtos
                    .GroupBy(p => p.Category)
                    .Select(g => new PermissionCategoryDto
                    {
                        Category = g.Key,
                        DisplayName = g.Key, // 暂时使用Category作为显示名，实际可以加字典表
                        Description = $"{g.Key}相关权限",
                        Permissions = g.ToList(),
                    })
                    .ToList();

                return ApiResponse<List<PermissionCategoryDto>>.OK(result, "获取权限列表成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取权限列表失败");
                return ApiResponse<List<PermissionCategoryDto>>.Error(
                    "获取权限列表失败",
                    "GET_PERMISSIONS_FAILED"
                );
            }
        }

        public async Task<ApiResponse<PermissionCatalogDto>> GetPermissionCatalogAsync()
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
                    return ApiResponse<PermissionCatalogDto>.Error(
                        adminDecision.Message,
                        adminDecision.ErrorCode
                    );
                }

                var permissions = await GetPermissionsCoreAsync();
                var catalog = new PermissionCatalogDto
                {
                    Categories = permissions.Data ?? new List<PermissionCategoryDto>(),
                    PermissionAliases = Permissions.GetPermissionAliases()
                        .Select(item => new PermissionAliasDto
                        {
                            CanonicalCode = item.Key,
                            AliasCodes = item.Value.ToList(),
                        })
                        .ToList(),
                    RoleTemplates = PermissionSeedData.RolePermissionTemplates
                        .Select(item => new RolePermissionTemplateDto
                        {
                            RoleName = item.RoleName,
                            PermissionCodes = item.PermissionCodes.ToList(),
                        })
                        .ToList(),
                    SuperAdminRoleNames = Permissions.SuperAdminRoleNames.ToList(),
                };

                return ApiResponse<PermissionCatalogDto>.OK(catalog, "获取权限目录成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取权限目录失败");
                return ApiResponse<PermissionCatalogDto>.Error(
                    "获取权限目录失败",
                    "GET_PERMISSION_CATALOG_FAILED"
                );
            }
        }

        /// <summary>
        /// 获取角色的权限列表
        /// </summary>
        public async Task<ApiResponse<List<string>>> GetRolePermissionsAsync(string roleGuid)
        {
            var db = _context.Db;
            var adminDecision = await UserAccessMutationSecurity.ValidateAdminOperationAsync(
                db,
                _manageableStoreScopeService
            );
            return adminDecision.IsAllowed
                ? await GetRolePermissionsCoreAsync(roleGuid)
                : ApiResponse<List<string>>.Error(
                    adminDecision.Message,
                    adminDecision.ErrorCode
                );
        }

        public Task<ApiResponse<List<string>>> GetRolePermissionsForAuthorizationAsync(
            string roleGuid
        ) => GetRolePermissionsCoreAsync(roleGuid);

        private async Task<ApiResponse<List<string>>> GetRolePermissionsCoreAsync(string roleGuid)
        {
            try
            {
                var db = _context.Db;
                var role = await db.Queryable<Role>()
                    .Where(r => r.RoleGUID == roleGuid && !r.IsDeleted)
                    .FirstAsync();

                if (role == null)
                {
                    return ApiResponse<List<string>>.OK(new List<string>(), "获取角色权限成功");
                }

                if (IsSuperAdminRoleName(role.RoleName))
                {
                    var persistedPermissions = await db.Queryable<SysPermission>()
                        .Where(p => !p.IsDeleted)
                        .Select(p => p.Code)
                        .ToListAsync();
                    var allPermissions = IncludeBuiltInSalesDashboardPermissionCodes(
                        persistedPermissions
                    );

                    return ApiResponse<List<string>>.OK(
                        allPermissions.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                        "Admin 默认拥有所有权限"
                    );
                }

                var permissions = await db.Queryable<SysRolePermission>()
                    .Where(rp => rp.RoleGuid == roleGuid && rp.IsDeleted == false)
                    .Select(rp => rp.PermissionCode)
                    .ToListAsync();

                return ApiResponse<List<string>>.OK(permissions, "获取角色权限成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取角色权限失败，RoleGUID: {RoleGUID}", roleGuid);
                return ApiResponse<List<string>>.Error(
                    "获取角色权限失败",
                    "GET_ROLE_PERMISSIONS_FAILED"
                );
            }
        }

        public async Task<ApiResponse<RolePermissionStateDto>> GetRolePermissionStateAsync(
            string roleGuid
        )
        {
            var db = _context.Db;
            var adminDecision = await UserAccessMutationSecurity.ValidateAdminOperationAsync(
                db,
                _manageableStoreScopeService
            );
            return adminDecision.IsAllowed
                ? await GetRolePermissionStateCoreAsync(roleGuid)
                : ApiResponse<RolePermissionStateDto>.Error(
                    adminDecision.Message,
                    adminDecision.ErrorCode
                );
        }

        private async Task<ApiResponse<RolePermissionStateDto>> GetRolePermissionStateCoreAsync(
            string roleGuid
        )
        {
            try
            {
                var db = _context.Db;
                var role = await db.Queryable<Role>()
                    .Where(item => item.RoleGUID == roleGuid && !item.IsDeleted)
                    .FirstAsync();

                if (role == null)
                {
                    return ApiResponse<RolePermissionStateDto>.Error(
                        "角色不存在",
                        "ROLE_NOT_FOUND"
                    );
                }

                var explicitCodes = await db.Queryable<SysRolePermission>()
                    .Where(item => item.RoleGuid == roleGuid && !item.IsDeleted)
                    .Select(item => item.PermissionCode)
                    .ToListAsync();

                var effectivePermissions = await GetRolePermissionsCoreAsync(roleGuid);
                var isSuperAdmin = IsSuperAdminRoleName(role.RoleName);

                return ApiResponse<RolePermissionStateDto>.OK(
                    new RolePermissionStateDto
                    {
                        RoleGuid = role.RoleGUID,
                        RoleName = role.RoleName,
                        IsSuperAdmin = isSuperAdmin,
                        ImplicitAllPermissions = isSuperAdmin,
                        ExplicitPermissionCodes = explicitCodes
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList(),
                        EffectivePermissionCodes =
                            effectivePermissions.Data ?? new List<string>(),
                    },
                    "获取角色权限状态成功"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取角色权限状态失败，RoleGUID: {RoleGUID}", roleGuid);
                return ApiResponse<RolePermissionStateDto>.Error(
                    "获取角色权限状态失败",
                    "GET_ROLE_PERMISSION_STATE_FAILED"
                );
            }
        }

        /// <summary>
        /// 为角色分配权限
        /// </summary>
        public async Task<ApiResponse<bool>> AssignPermissionsToRoleAsync(
            string roleGuid,
            RolePermissionAssignmentDto dto
        )
        {
            try
            {
                var db = _context.Db;
                var actor = await UserAccessMutationSecurity.ResolveActorAsync(
                    db,
                    _manageableStoreScopeService
                );
                if (!actor.IsSuperAdmin)
                {
                    return ApiResponse<bool>.Error(
                        "只有管理员可以维护角色权限",
                        "ADMIN_REQUIRED"
                    );
                }

                var role = await db.Queryable<Role>()
                    .Where(r => r.RoleGUID == roleGuid && !r.IsDeleted)
                    .FirstAsync();

                await db.Ado.BeginTranAsync();
                try
                {
                    if (role != null && IsSuperAdminRoleName(role.RoleName))
                    {
                        await db.Deleteable<SysRolePermission>()
                            .Where(rp => rp.RoleGuid == roleGuid)
                            .ExecuteCommandAsync();

                        await db.Ado.CommitTranAsync();

                        _logger.LogInformation(
                            "跳过 Admin 显式权限写入，RoleGUID: {RoleGUID}",
                            roleGuid
                        );
                        return ApiResponse<bool>.OK(true, "Admin 默认拥有所有权限，无需分配");
                    }

                    // 1. 删除旧权限
                    await db.Deleteable<SysRolePermission>()
                        .Where(rp => rp.RoleGuid == roleGuid)
                        .ExecuteCommandAsync();

                    // 2. 添加新权限
                    if (dto.Permissions != null && dto.Permissions.Any())
                    {
                        var newPermissions = dto
                            .Permissions.Select(code => new SysRolePermission
                            {
                                Id = UuidHelper.GenerateUuid7(),
                                RoleGuid = roleGuid,
                                PermissionCode = code,
                                CreatedAt = DateTime.Now,
                                CreatedBy = GetCurrentUsername(),
                                UpdatedAt = DateTime.Now,
                                UpdatedBy = GetCurrentUsername(),
                                IsDeleted = false
                            })
                            .ToList();

                        await db.Insertable(newPermissions).ExecuteCommandAsync();
                    }

                    await db.Ado.CommitTranAsync();

                    _logger.LogInformation(
                        "为角色分配权限成功，RoleGUID: {RoleGUID}, PermissionCount: {PermissionCount}",
                        roleGuid,
                        dto.Permissions?.Count ?? 0
                    );
                    return ApiResponse<bool>.OK(true, "权限分配成功");
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "为角色分配权限失败，RoleGUID: {RoleGUID}", roleGuid);
                return ApiResponse<bool>.Error("权限分配失败", "ASSIGN_PERMISSIONS_FAILED");
            }
        }

        /// <summary>
        /// 检查用户是否有指定角色
        /// </summary>
        public async Task<ApiResponse<bool>> UserHasRoleAsync(string userGuid, string roleName)
        {
            try
            {
                var db = _context.Db;

                var hasRole = await db.Queryable<UserRole>()
                    .InnerJoin<User>((ur, u) => ur.UserGUID == u.UserGUID)
                    .InnerJoin<Role>((ur, u, r) => ur.RoleGUID == r.RoleGUID)
                    .Where(
                        (ur, u, r) =>
                            ur.UserGUID == userGuid
                            && !ur.IsDeleted
                            && u.IsActive
                            && !u.IsDeleted
                            && r.RoleName == roleName
                            && r.IsActive
                            && !r.IsDeleted
                    )
                    .AnyAsync();

                return ApiResponse<bool>.OK(hasRole, hasRole ? "用户拥有该角色" : "用户没有该角色");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "检查用户角色失败，UserGUID: {UserGUID}, RoleName: {RoleName}",
                    userGuid,
                    roleName
                );
                return ApiResponse<bool>.Error("检查用户角色失败", "CHECK_USER_ROLE_FAILED");
            }
        }

        /// <summary>
        /// 检查用户是否有指定权限
        /// </summary>
        public async Task<ApiResponse<bool>> UserHasPermissionAsync(
            string userGuid,
            string permission
        )
        {
            try
            {
                var db = _context.Db;
                var superAdminRoleNames = Permissions.SuperAdminRoleNames.ToArray();
                // 直接权限沿用原来的 OrdinalIgnoreCase 语义；ToLower 让 SQLite 也不依赖默认排序规则。
                var equivalentPermissionCodes = Permissions
                    .GetEquivalentPermissionCodes(permission)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var equivalentPermissionCodesLower = equivalentPermissionCodes
                    .Select(code => code.ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                // 所有分支都挂在同一个活动用户锚点下，并由一个 EXISTS 查询完成，避免管理员、直接权限、角色权限分别往返数据库。
                var hasPermission = await db.Queryable<User>()
                    .Where(user =>
                        user.UserGUID == userGuid
                        && user.IsActive
                        && !user.IsDeleted
                    )
                    .Where(user =>
                        SqlFunc.Subqueryable<UserRole>()
                            .InnerJoin<Role>((ur, role) => ur.RoleGUID == role.RoleGUID)
                            .Where(
                                (ur, role) =>
                                    ur.UserGUID == user.UserGUID
                                    && !ur.IsDeleted
                                    && role.IsActive
                                    && !role.IsDeleted
                                    && superAdminRoleNames.Contains(role.RoleName)
                            )
                            .Any()
                        || SqlFunc.Subqueryable<SysUserPermission>()
                            .Where(up =>
                                up.UserGuid == user.UserGUID
                                && !up.IsDeleted
                                && equivalentPermissionCodesLower.Contains(up.PermissionCode.ToLower())
                            )
                            .Any()
                        || SqlFunc.Subqueryable<UserRole>()
                            .InnerJoin<Role>((ur, role) => ur.RoleGUID == role.RoleGUID)
                            .InnerJoin<SysRolePermission>(
                                (ur, role, rolePermission) => ur.RoleGUID == rolePermission.RoleGuid
                            )
                            .Where(
                                (ur, role, rolePermission) =>
                                    ur.UserGUID == user.UserGUID
                                    && !ur.IsDeleted
                                    && role.IsActive
                                    && !role.IsDeleted
                                    && !rolePermission.IsDeleted
                                    && equivalentPermissionCodes.Contains(rolePermission.PermissionCode)
                            )
                            .Any()
                    )
                    .AnyAsync();

                return ApiResponse<bool>.OK(
                    hasPermission,
                    hasPermission ? "用户拥有该权限" : "用户没有该权限"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "检查用户权限失败，UserGUID: {UserGUID}, Permission: {Permission}",
                    userGuid,
                    permission
                );
                return ApiResponse<bool>.Error("检查用户权限失败", "CHECK_USER_PERMISSION_FAILED");
            }
        }

        /// <summary>
        /// 检查用户是否显式拥有指定权限。此入口不展开权限别名，供必须保持精确权限边界的接口使用。
        /// </summary>
        public async Task<ApiResponse<bool>> UserHasExactPermissionAsync(
            string userGuid,
            string permission
        )
        {
            try
            {
                if (string.IsNullOrWhiteSpace(userGuid) || string.IsNullOrWhiteSpace(permission))
                {
                    return ApiResponse<bool>.OK(false, "用户没有该权限");
                }

                var db = _context.Db;
                var superAdminRoleNames = Permissions.SuperAdminRoleNames.ToList();
                var hasSuperAdminRole = await db.Queryable<UserRole>()
                    .InnerJoin<User>((ur, u) => ur.UserGUID == u.UserGUID)
                    .InnerJoin<Role>((ur, u, r) => ur.RoleGUID == r.RoleGUID)
                    .Where(
                        (ur, u, r) =>
                            ur.UserGUID == userGuid
                            && !ur.IsDeleted
                            && u.IsActive
                            && !u.IsDeleted
                            && r.IsActive
                            && !r.IsDeleted
                            && superAdminRoleNames.Contains(r.RoleName)
                    )
                    .AnyAsync();
                if (hasSuperAdminRole)
                {
                    return ApiResponse<bool>.OK(true, "Admin 默认拥有所有权限");
                }

                var hasDirectPermission = await db.Queryable<SysUserPermission>()
                    .InnerJoin<User>((up, u) => up.UserGuid == u.UserGUID)
                    .Where(
                        (up, u) =>
                            up.UserGuid == userGuid
                            && !up.IsDeleted
                            && u.IsActive
                            && !u.IsDeleted
                            && up.PermissionCode == permission
                    )
                    .AnyAsync();
                if (hasDirectPermission)
                {
                    return ApiResponse<bool>.OK(true, "用户拥有该权限");
                }

                var hasRolePermission = await db.Queryable<UserRole>()
                    .InnerJoin<User>((ur, u) => ur.UserGUID == u.UserGUID)
                    .InnerJoin<Role>((ur, u, r) => ur.RoleGUID == r.RoleGUID)
                    .InnerJoin<SysRolePermission>((ur, u, r, rp) => ur.RoleGUID == rp.RoleGuid)
                    .Where(
                        (ur, u, r, rp) =>
                            ur.UserGUID == userGuid
                            && !ur.IsDeleted
                            && u.IsActive
                            && !u.IsDeleted
                            && r.IsActive
                            && !r.IsDeleted
                            && !rp.IsDeleted
                            && rp.PermissionCode == permission
                    )
                    .AnyAsync();

                return ApiResponse<bool>.OK(
                    hasRolePermission,
                    hasRolePermission ? "用户拥有该权限" : "用户没有该权限"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "检查用户精确权限失败，UserGUID: {UserGUID}, Permission: {Permission}",
                    userGuid,
                    permission
                );
                return ApiResponse<bool>.Error("检查用户权限失败", "CHECK_USER_PERMISSION_FAILED");
            }
        }

        public async Task<ApiResponse<UserPermissionSnapshotDto>> GetUserPermissionSnapshotAsync(
            string userGuid
        )
        {
            try
            {
                if (string.IsNullOrWhiteSpace(userGuid))
                {
                    return ApiResponse<UserPermissionSnapshotDto>.OK(
                        new UserPermissionSnapshotDto(),
                        "用户权限快照为空"
                    );
                }

                var db = _context.Db;
                var roleEntries = await db.Queryable<UserRole>()
                    .InnerJoin<User>((ur, u) => ur.UserGUID == u.UserGUID)
                    .InnerJoin<Role>((ur, u, r) => ur.RoleGUID == r.RoleGUID)
                    .Where(
                        (ur, u, r) =>
                            ur.UserGUID == userGuid
                            && !ur.IsDeleted
                            && u.IsActive
                            && !u.IsDeleted
                            && r.IsActive
                            && !r.IsDeleted
                    )
                    .Select((ur, u, r) => new { r.RoleGUID, r.RoleName })
                    .ToListAsync();

                var roleNames = roleEntries
                    .Select(item => item.RoleName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var isSuperAdmin = roleNames.Any(IsSuperAdminRoleName);

                List<string> permissionCodes;
                List<string> exactCodes;
                if (isSuperAdmin)
                {
                    var persistedPermissionCodes = await db.Queryable<SysPermission>()
                        .Where(item => !item.IsDeleted)
                        .Select(item => item.Code)
                        .ToListAsync();
                    permissionCodes = IncludeBuiltInSalesDashboardPermissionCodes(
                        persistedPermissionCodes
                    );
                    exactCodes = permissionCodes.ToList();
                }
                else
                {
                    var roleGuids = roleEntries
                        .Select(item => item.RoleGUID)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var explicitCodes = roleGuids.Count == 0
                        ? new List<string>()
                        : await db.Queryable<SysRolePermission>()
                            .Where(item => roleGuids.Contains(item.RoleGuid) && !item.IsDeleted)
                            .Select(item => item.PermissionCode)
                            .ToListAsync();

                    var directCodes = await db.Queryable<SysUserPermission>()
                        .Where(item => item.UserGuid == userGuid && !item.IsDeleted)
                        .Select(item => item.PermissionCode)
                        .ToListAsync();

                    exactCodes = explicitCodes
                        .Concat(directCodes)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    permissionCodes = Permissions.ExpandPermissionCodes(
                        explicitCodes.Concat(directCodes)
                    ).ToList();
                }

                return ApiResponse<UserPermissionSnapshotDto>.OK(
                    new UserPermissionSnapshotDto
                    {
                        UserGuid = userGuid,
                        IsSuperAdmin = isSuperAdmin,
                        RoleNames = roleNames,
                        PermissionCodes = permissionCodes
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList(),
                        ExactPermissionCodes = exactCodes
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList(),
                    },
                    isSuperAdmin ? "Admin 默认拥有所有权限" : "获取用户权限快照成功"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户权限快照失败，UserGUID: {UserGUID}", userGuid);
                return ApiResponse<UserPermissionSnapshotDto>.Error(
                    "获取用户权限快照失败",
                    "GET_USER_PERMISSION_SNAPSHOT_FAILED"
                );
            }
        }

        public async Task<ApiResponse<UserPermissionStateDto>> GetUserPermissionStateAsync(
            string userGuid
        )
        {
            var db = _context.Db;
            var adminDecision = await UserAccessMutationSecurity.ValidateAdminOperationAsync(
                db,
                _manageableStoreScopeService
            );
            return adminDecision.IsAllowed
                ? await GetUserPermissionStateCoreAsync(userGuid)
                : ApiResponse<UserPermissionStateDto>.Error(
                    adminDecision.Message,
                    adminDecision.ErrorCode
                );
        }

        private async Task<ApiResponse<UserPermissionStateDto>> GetUserPermissionStateCoreAsync(
            string userGuid
        )
        {
            try
            {
                var db = _context.Db;
                var userExists = await db.Queryable<User>()
                    .Where(item => item.UserGUID == userGuid && !item.IsDeleted)
                    .AnyAsync();

                if (!userExists)
                {
                    return ApiResponse<UserPermissionStateDto>.Error(
                        "用户不存在",
                        "USER_NOT_FOUND"
                    );
                }

                var roleEntries = await db.Queryable<UserRole>()
                    .InnerJoin<Role>((ur, r) => ur.RoleGUID == r.RoleGUID)
                    .Where(
                        (ur, r) =>
                            ur.UserGUID == userGuid
                            && !ur.IsDeleted
                            && r.IsActive
                            && !r.IsDeleted
                    )
                    .Select((ur, r) => new { r.RoleGUID, r.RoleName })
                    .ToListAsync();

                var directCodes = await db.Queryable<SysUserPermission>()
                    .Where(item => item.UserGuid == userGuid && !item.IsDeleted)
                    .Select(item => item.PermissionCode)
                    .ToListAsync();

                var isSuperAdmin = roleEntries.Any(item => IsSuperAdminRoleName(item.RoleName));
                List<string> inheritedCodes;
                List<UserPermissionInheritedSourceDto> inheritedSources;

                if (isSuperAdmin)
                {
                    var persistedPermissionCodes = await db.Queryable<SysPermission>()
                        .Where(item => !item.IsDeleted)
                        .Select(item => item.Code)
                        .ToListAsync();
                    inheritedCodes = IncludeBuiltInSalesDashboardPermissionCodes(
                        persistedPermissionCodes
                    );

                    inheritedSources = roleEntries
                        .Where(item => IsSuperAdminRoleName(item.RoleName))
                        .Select(item => new UserPermissionInheritedSourceDto
                        {
                            RoleName = item.RoleName,
                            PermissionCodes = inheritedCodes
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList(),
                        })
                        .ToList();
                }
                else
                {
                    var roleGuids = roleEntries
                        .Select(item => item.RoleGUID)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var rolePermissionRows = roleGuids.Count == 0
                        ? new List<SysRolePermission>()
                        : await db.Queryable<SysRolePermission>()
                            .Where(item => roleGuids.Contains(item.RoleGuid) && !item.IsDeleted)
                            .ToListAsync();

                    inheritedSources = roleEntries
                        .Select(role =>
                        {
                            var roleCodes = rolePermissionRows
                                .Where(item => item.RoleGuid == role.RoleGUID)
                                .Select(item => item.PermissionCode)
                                .ToList();

                            return new UserPermissionInheritedSourceDto
                            {
                                RoleName = role.RoleName,
                                PermissionCodes = Permissions.ExpandPermissionCodes(roleCodes)
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList(),
                            };
                        })
                        .Where(item => item.PermissionCodes.Count > 0)
                        .ToList();

                    inheritedCodes = inheritedSources
                        .SelectMany(item => item.PermissionCodes)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                var normalizedDirectCodes = Permissions.ExpandPermissionCodes(directCodes)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var effectiveCodes = inheritedCodes
                    .Concat(normalizedDirectCodes)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return ApiResponse<UserPermissionStateDto>.OK(
                    new UserPermissionStateDto
                    {
                        UserGuid = userGuid,
                        IsSuperAdmin = isSuperAdmin,
                        ImplicitAllPermissions = isSuperAdmin,
                        InheritedPermissionCodes = inheritedCodes,
                        DirectPermissionCodes = normalizedDirectCodes,
                        EffectivePermissionCodes = effectiveCodes,
                        InheritedSources = inheritedSources,
                    },
                    "获取用户权限状态成功"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户权限状态失败，UserGUID: {UserGUID}", userGuid);
                return ApiResponse<UserPermissionStateDto>.Error(
                    "获取用户权限状态失败",
                    "GET_USER_PERMISSION_STATE_FAILED"
                );
            }
        }

        public async Task<ApiResponse<UserAccessPermissionDto>> GetUserAccessPermissionsAsync(
            string actorUserGuid,
            string targetUserGuid
        )
        {
            try
            {
                var db = _context.Db;
                var actor = await UserAccessMutationSecurity.ResolveActorByUserGuidAsync(
                    db,
                    actorUserGuid
                );
                if (actor.Kind == UserAccessMutationActorKind.Denied)
                {
                    return ApiResponse<UserAccessPermissionDto>.Error(
                        "当前账号不能分配员工访问权限",
                        "ACCESS_DELEGATOR_DENIED"
                    );
                }

                var targetDecision = await UserAccessMutationSecurity.ValidateGlobalTargetAsync(
                    db,
                    actor,
                    targetUserGuid
                );
                if (!targetDecision.IsAllowed)
                {
                    return ApiResponse<UserAccessPermissionDto>.Error(
                        targetDecision.Message,
                        targetDecision.ErrorCode
                    );
                }

                if (
                    !actor.IsSuperAdmin
                    && !await UserAccessMutationSecurity.IsEmployeeTargetAsync(
                        db,
                        targetUserGuid
                    )
                )
                {
                    return ApiResponse<UserAccessPermissionDto>.Error(
                        "店长只能维护员工的 POS 收银权限",
                        "EMPLOYEE_TARGET_REQUIRED"
                    );
                }

                var stateResult = await GetUserPermissionStateCoreAsync(targetUserGuid);
                if (!stateResult.Success || stateResult.Data == null)
                {
                    return ApiResponse<UserAccessPermissionDto>.Error(
                        stateResult.Message,
                        stateResult.ErrorCode
                    );
                }

                var categoriesResult = await GetPermissionsCoreAsync();
                if (!categoriesResult.Success || categoriesResult.Data == null)
                {
                    return ApiResponse<UserAccessPermissionDto>.Error(
                        categoriesResult.Message,
                        categoriesResult.ErrorCode
                    );
                }

                if (actor.IsSuperAdmin)
                {
                    return ApiResponse<UserAccessPermissionDto>.OK(
                        new UserAccessPermissionDto
                        {
                            State = stateResult.Data,
                            Categories = categoriesResult.Data,
                        },
                        "获取用户可分配权限成功"
                    );
                }

                var actorPermissionCodes = await UserAccessMutationSecurity.GetEffectivePermissionCodesAsync(
                    db,
                    actor.UserGuid
                );
                var delegatedPermissionCodes = PermissionSeedData.PosTerminalBusinessPermissionCodes
                    .Where(actorPermissionCodes.Contains)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // 店长只看到本人拥有的 POS 收银业务权限；目标越界权限不泄露且保存时保留。
                var scopedState = new UserPermissionStateDto
                {
                    UserGuid = stateResult.Data.UserGuid,
                    IsSuperAdmin = stateResult.Data.IsSuperAdmin,
                    ImplicitAllPermissions = stateResult.Data.ImplicitAllPermissions,
                    InheritedPermissionCodes = stateResult.Data.InheritedPermissionCodes
                        .Where(delegatedPermissionCodes.Contains)
                        .ToList(),
                    DirectPermissionCodes = stateResult.Data.DirectPermissionCodes
                        .Where(delegatedPermissionCodes.Contains)
                        .ToList(),
                    EffectivePermissionCodes = stateResult.Data.EffectivePermissionCodes
                        .Where(delegatedPermissionCodes.Contains)
                        .ToList(),
                    InheritedSources = stateResult.Data.InheritedSources
                        .Select(source => new UserPermissionInheritedSourceDto
                        {
                            RoleName = source.RoleName,
                            PermissionCodes = source.PermissionCodes
                                .Where(delegatedPermissionCodes.Contains)
                                .ToList(),
                        })
                        .Where(source => source.PermissionCodes.Count > 0)
                        .ToList(),
                };
                var scopedCategories = categoriesResult.Data
                    .Select(category => new PermissionCategoryDto
                    {
                        Category = category.Category,
                        DisplayName = category.DisplayName,
                        Description = category.Description,
                        Permissions = category.Permissions
                            .Where(permission => delegatedPermissionCodes.Contains(permission.Name))
                            .ToList(),
                    })
                    .Where(category => category.Permissions.Count > 0)
                    .ToList();

                return ApiResponse<UserAccessPermissionDto>.OK(
                    new UserAccessPermissionDto
                    {
                        State = scopedState,
                        Categories = scopedCategories,
                    },
                    "获取用户可分配权限成功"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "获取用户可分配权限失败，ActorUserGUID: {ActorUserGUID}, TargetUserGUID: {TargetUserGUID}",
                    actorUserGuid,
                    targetUserGuid
                );
                return ApiResponse<UserAccessPermissionDto>.Error(
                    "获取用户可分配权限失败",
                    "GET_USER_ACCESS_PERMISSIONS_FAILED"
                );
            }
        }

        public async Task<ApiResponse<bool>> AssignPermissionsToUserAsync(
            string userGuid,
            UserPermissionAssignmentDto dto
        )
        {
            try
            {
                var db = _context.Db;
                var userExists = await db.Queryable<User>()
                    .Where(item => item.UserGUID == userGuid && !item.IsDeleted)
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

                var requestedCodes = Permissions.ExpandPermissionCodes(dto.Permissions)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var isRestrictedActor = actor.IsEnforced && !actor.IsSuperAdmin;
                var delegatedPermissionCodes = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase
                );

                if (isRestrictedActor)
                {
                    if (
                        !await UserAccessMutationSecurity.IsEmployeeTargetAsync(db, userGuid)
                    )
                    {
                        return ApiResponse<bool>.Error(
                            "店长只能维护员工的 POS 收银权限",
                            "EMPLOYEE_TARGET_REQUIRED"
                        );
                    }

                    var actorPermissionCodes = await UserAccessMutationSecurity.GetEffectivePermissionCodesAsync(
                        db,
                        actor.UserGuid
                    );
                    var posBusinessCodes = PermissionSeedData.PosTerminalBusinessPermissionCodes
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    delegatedPermissionCodes = posBusinessCodes
                        .Where(actorPermissionCodes.Contains)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (requestedCodes.Any(code => !posBusinessCodes.Contains(code)))
                    {
                        return ApiResponse<bool>.Error(
                            "店长只能分配 POS 收银业务权限",
                            "POS_PERMISSION_DELEGATION_DENIED"
                        );
                    }

                    if (requestedCodes.Any(code => !delegatedPermissionCodes.Contains(code)))
                    {
                        return ApiResponse<bool>.Error(
                            "不能授予当前账号自身不具备的权限",
                            "PERMISSION_ESCALATION_DENIED"
                        );
                    }
                }

                // 授权写入使用串行化事务，避免目标或操作者权限在复验与写入之间漂移。
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

                    var transactionExistingCodes = requestedCodes.Count == 0
                        ? new List<string>()
                        : await db.Queryable<SysPermission>()
                            .Where(item =>
                                requestedCodes.Contains(item.Code) && !item.IsDeleted
                            )
                            .Select(item => item.Code)
                            .ToListAsync();
                    var transactionPermissionCodesToWrite = transactionExistingCodes.ToList();
                    transactionPermissionCodesToWrite.AddRange(
                        requestedCodes.Where(IsBuiltInSalesDashboardPermission)
                    );
                    var transactionRestrictedActor = !transactionActor.IsSuperAdmin;
                    var transactionDelegatedPermissionCodes = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase
                    );

                    if (transactionRestrictedActor)
                    {
                        if (
                            !await UserAccessMutationSecurity.IsEmployeeTargetAsync(
                                db,
                                userGuid
                            )
                        )
                        {
                            await db.Ado.RollbackTranAsync();
                            return ApiResponse<bool>.Error(
                                "店长只能维护员工的 POS 收银权限",
                                "EMPLOYEE_TARGET_REQUIRED"
                            );
                        }

                        var transactionPosBusinessCodes = PermissionSeedData
                            .PosTerminalBusinessPermissionCodes
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        if (requestedCodes.Any(code => !transactionPosBusinessCodes.Contains(code)))
                        {
                            await db.Ado.RollbackTranAsync();
                            return ApiResponse<bool>.Error(
                                "店长只能分配 POS 收银业务权限",
                                "POS_PERMISSION_DELEGATION_DENIED"
                            );
                        }

                        var transactionActorPermissionCodes =
                            await UserAccessMutationSecurity.GetEffectivePermissionCodesAsync(
                            db,
                            transactionActor.UserGuid
                        );
                        transactionDelegatedPermissionCodes = transactionPosBusinessCodes
                            .Where(transactionActorPermissionCodes.Contains)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        if (
                            requestedCodes.Any(code =>
                                !transactionDelegatedPermissionCodes.Contains(code)
                            )
                        )
                        {
                            await db.Ado.RollbackTranAsync();
                            return ApiResponse<bool>.Error(
                                "不能授予当前账号自身不具备的权限",
                                "PERMISSION_ESCALATION_DENIED"
                            );
                        }

                        var delegatedCodeList = transactionDelegatedPermissionCodes.ToList();
                        if (delegatedCodeList.Count > 0)
                        {
                            // 受限集合替换只删除可委派子集，保留其他权限行的主键和审计字段。
                            await db.Deleteable<SysUserPermission>()
                                .Where(item =>
                                    item.UserGuid == userGuid
                                    && delegatedCodeList.Contains(item.PermissionCode)
                                )
                                .ExecuteCommandAsync();
                        }
                    }
                    else
                    {
                        await db.Deleteable<SysUserPermission>()
                            .Where(item => item.UserGuid == userGuid)
                            .ExecuteCommandAsync();
                    }

                    if (transactionPermissionCodesToWrite.Any())
                    {
                        var newPermissions = transactionPermissionCodesToWrite
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Select(code => new SysUserPermission
                            {
                                Id = UuidHelper.GenerateUuid7(),
                                UserGuid = userGuid,
                                PermissionCode = code,
                                CreatedAt = DateTime.Now,
                                CreatedBy = GetCurrentUsername(),
                                UpdatedAt = DateTime.Now,
                                UpdatedBy = GetCurrentUsername(),
                                IsDeleted = false,
                            })
                            .ToList();

                        await db.Insertable(newPermissions).ExecuteCommandAsync();
                    }

                    await db.Ado.CommitTranAsync();
                    return ApiResponse<bool>.OK(true, "用户直接权限分配成功");
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "为用户分配直接权限失败，UserGUID: {UserGUID}", userGuid);
                return ApiResponse<bool>.Error(
                    "用户直接权限分配失败",
                    "ASSIGN_USER_PERMISSIONS_FAILED"
                );
            }
        }

        /// <summary>
        /// 复制用户直接权限：校验与保存单个用户的权限沿用同一套操作者/目标校验，
        /// 多个目标在同一个 Serializable 事务里写入，任何一人校验失败整体不写。
        /// </summary>
        public async Task<ApiResponse<CopyUserPermissionsResultDto>> CopyUserDirectPermissionsAsync(
            string sourceUserGuid,
            CopyUserPermissionsRequestDto dto
        )
        {
            try
            {
                var mode = (dto.Mode ?? string.Empty).Trim().ToLowerInvariant();
                if (mode != CopyUserPermissionsRequestDto.ModeMerge && mode != CopyUserPermissionsRequestDto.ModeReplace)
                {
                    return ApiResponse<CopyUserPermissionsResultDto>.Error("复制方式只能是合并或覆盖", "VALIDATION_ERROR");
                }

                var targetGuids = (dto.TargetUserGuids ?? new List<string>())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (targetGuids.Count == 0)
                {
                    return ApiResponse<CopyUserPermissionsResultDto>.Error("请至少选择一位目标员工", "VALIDATION_ERROR");
                }
                if (targetGuids.Count > 50)
                {
                    return ApiResponse<CopyUserPermissionsResultDto>.Error("一次最多复制给 50 位员工", "VALIDATION_ERROR");
                }
                if (targetGuids.Any(item => string.Equals(item, sourceUserGuid, StringComparison.OrdinalIgnoreCase)))
                {
                    return ApiResponse<CopyUserPermissionsResultDto>.Error("目标员工不能包含来源员工", "VALIDATION_ERROR");
                }

                var db = _context.Db;
                if (dto.Preview)
                {
                    var preview = await BuildCopyPlanAsync(db, sourceUserGuid, targetGuids, mode, true);
                    return preview.Error ?? ApiResponse<CopyUserPermissionsResultDto>.OK(preview.Result!, "复制权限预览成功");
                }

                // 写入使用串行化事务，避免操作者/目标权限在复验与写入之间漂移；事务内重新计算，不信任预览结果。
                await db.Ado.BeginTranAsync(IsolationLevel.Serializable);
                try
                {
                    var plan = await BuildCopyPlanAsync(db, sourceUserGuid, targetGuids, mode, false);
                    if (plan.Error != null)
                    {
                        await db.Ado.RollbackTranAsync();
                        return plan.Error;
                    }

                    var now = DateTime.Now;
                    var username = GetCurrentUsername();
                    foreach (var target in plan.Result!.Targets)
                    {
                        var touched = target.Added.Concat(target.Removed).ToList();
                        if (touched.Count == 0)
                        {
                            continue;
                        }

                        // 与保存单个用户权限一致：先物理清掉将变动的行（含历史软删行，避免重复键），再插入新增。
                        await db.Deleteable<SysUserPermission>()
                            .Where(item => item.UserGuid == target.UserGuid && touched.Contains(item.PermissionCode))
                            .ExecuteCommandAsync();
                        if (target.Added.Count > 0)
                        {
                            var rows = target.Added.Select(code => new SysUserPermission
                            {
                                Id = UuidHelper.GenerateUuid7(),
                                UserGuid = target.UserGuid,
                                PermissionCode = code,
                                CreatedAt = now,
                                CreatedBy = username,
                                UpdatedAt = now,
                                UpdatedBy = username,
                                IsDeleted = false,
                            }).ToList();
                            await db.Insertable(rows).ExecuteCommandAsync();
                        }
                    }

                    await db.Ado.CommitTranAsync();
                    _logger.LogInformation(
                        "复制用户直接权限成功，Source: {Source}, Targets: {TargetCount}, Mode: {Mode}, Operator: {Operator}",
                        sourceUserGuid,
                        targetGuids.Count,
                        mode,
                        username
                    );
                    return ApiResponse<CopyUserPermissionsResultDto>.OK(plan.Result, "复制权限成功");
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "复制用户直接权限失败，SourceUserGUID: {SourceUserGUID}", sourceUserGuid);
                return ApiResponse<CopyUserPermissionsResultDto>.Error("复制权限失败", "COPY_USER_PERMISSIONS_FAILED");
            }
        }

        /// <summary>
        /// 计算复制计划：操作者与源/目标校验、可复制集合、每人新增与移除。预览与写入共用，保证两者口径一致。
        /// </summary>
        private async Task<(ApiResponse<CopyUserPermissionsResultDto>? Error, CopyUserPermissionsResultDto? Result)> BuildCopyPlanAsync(
            ISqlSugarClient db,
            string sourceUserGuid,
            IReadOnlyList<string> targetGuids,
            string mode,
            bool preview
        )
        {
            static (ApiResponse<CopyUserPermissionsResultDto>?, CopyUserPermissionsResultDto?) Fail(string message, string code) =>
                (ApiResponse<CopyUserPermissionsResultDto>.Error(message, code), null);

            var actor = await UserAccessMutationSecurity.ResolveActorAsync(db, _manageableStoreScopeService);
            if (actor.Kind == UserAccessMutationActorKind.Denied)
            {
                return Fail("当前账号不能分配员工访问权限", "ACCESS_DELEGATOR_DENIED");
            }

            var isRestricted = !actor.IsSuperAdmin;
            // 来源与目标都走同一套校验：不能是本人、高权限账号或范围外账号；店长只能涉及员工。
            foreach (var guid in new[] { sourceUserGuid }.Concat(targetGuids))
            {
                var exists = await db.Queryable<User>().AnyAsync(item => item.UserGUID == guid && !item.IsDeleted);
                if (!exists)
                {
                    return Fail("用户不存在", "USER_NOT_FOUND");
                }

                var decision = await UserAccessMutationSecurity.ValidateGlobalTargetAsync(db, actor, guid);
                if (!decision.IsAllowed)
                {
                    return Fail(decision.Message, decision.ErrorCode);
                }

                if (isRestricted && !await UserAccessMutationSecurity.IsEmployeeTargetAsync(db, guid))
                {
                    return Fail("店长只能维护员工的 POS 收银权限", "EMPLOYEE_TARGET_REQUIRED");
                }
            }

            var sourceCodes = await db.Queryable<SysUserPermission>()
                .Where(item => item.UserGuid == sourceUserGuid && !item.IsDeleted)
                .Select(item => item.PermissionCode)
                .ToListAsync();
            sourceCodes = sourceCodes.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 可复制范围：管理员不限；店长只能是「POS 收银业务权限 ∩ 自己持有」，与保存单个员工权限的约束一致。
            HashSet<string>? delegated = null;
            if (isRestricted)
            {
                var actorCodes = await UserAccessMutationSecurity.GetEffectivePermissionCodesAsync(db, actor.UserGuid);
                delegated = PermissionSeedData.PosTerminalBusinessPermissionCodes
                    .Where(actorCodes.Contains)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            // 只复制仍存在的权限定义（内置销售看板码例外），已被删除的旧码不再扩散。
            var knownCodes = sourceCodes.Count == 0
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : (await db.Queryable<SysPermission>()
                        .Where(item => sourceCodes.Contains(item.Code) && !item.IsDeleted)
                        .Select(item => item.Code)
                        .ToListAsync())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var transferable = sourceCodes
                .Where(code => (delegated == null || delegated.Contains(code))
                    && (knownCodes.Contains(code) || IsBuiltInSalesDashboardPermission(code)))
                .ToList();
            var skipped = sourceCodes.Except(transferable, StringComparer.OrdinalIgnoreCase).ToList();

            var result = new CopyUserPermissionsResultDto
            {
                SourceUserGuid = sourceUserGuid,
                Mode = mode,
                Preview = preview,
                SourceDirectPermissionCodes = sourceCodes,
                TransferablePermissionCodes = transferable,
                SkippedPermissionCodes = skipped,
            };

            var replace = mode == CopyUserPermissionsRequestDto.ModeReplace;
            foreach (var targetGuid in targetGuids)
            {
                var existing = await db.Queryable<SysUserPermission>()
                    .Where(item => item.UserGuid == targetGuid && !item.IsDeleted)
                    .Select(item => item.PermissionCode)
                    .ToListAsync();
                var plan = UserPermissionCopyPlanner.Plan(targetGuid, existing, transferable, replace, delegated);
                result.Targets.Add(new CopyUserPermissionsTargetResultDto
                {
                    UserGuid = plan.UserGuid,
                    Added = plan.Added.ToList(),
                    Removed = plan.Removed.ToList(),
                    UnchangedCount = plan.UnchangedCount,
                });
            }

            return (null, result);
        }

        /// <summary>
        /// 复制角色
        /// </summary>
        public async Task<ApiResponse<RoleDto>> DuplicateRoleAsync(
            string sourceRoleGuid,
            string newRoleName,
            string? newDescription = null
        )
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
                    return ApiResponse<RoleDto>.Error(
                        adminDecision.Message,
                        adminDecision.ErrorCode
                    );
                }

                // 获取源角色
                var sourceRole = await db.Queryable<Role>()
                    .Where(r => r.RoleGUID == sourceRoleGuid)
                    .FirstAsync();

                if (sourceRole == null)
                {
                    return ApiResponse<RoleDto>.Error("源角色不存在", "SOURCE_ROLE_NOT_FOUND");
                }

                // 检查新角色名是否已存在
                var existingRole = await db.Queryable<Role>()
                    .Where(r => r.RoleName == newRoleName)
                    .FirstAsync();

                if (existingRole != null)
                {
                    return ApiResponse<RoleDto>.Error("角色名已存在", "ROLE_NAME_EXISTS");
                }

                // 创建新角色
                var newRole = new Role
                {
                    RoleGUID = Guid.NewGuid().ToString(),
                    RoleName = newRoleName,
                    Description = newDescription ?? sourceRole.Description,
                    IsActive = sourceRole.IsActive,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now,
                };

                await db.Insertable(newRole).ExecuteCommandAsync();

                var result = new RoleDto
                {
                    RoleGUID = newRole.RoleGUID,
                    RoleName = newRole.RoleName,
                    Description = newRole.Description,
                    IsActive = newRole.IsActive,
                    CreatedAt = newRole.CreatedAt,
                    UpdatedAt = newRole.UpdatedAt ?? newRole.CreatedAt,
                    UserCount = 0,
                };

                _logger.LogInformation(
                    "复制角色成功，SourceRoleGUID: {SourceRoleGUID}, NewRoleGUID: {NewRoleGUID}",
                    sourceRoleGuid,
                    newRole.RoleGUID
                );
                return ApiResponse<RoleDto>.OK(result, "复制角色成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "复制角色失败，SourceRoleGUID: {SourceRoleGUID}, NewRoleName: {NewRoleName}",
                    sourceRoleGuid,
                    newRoleName
                );
                return ApiResponse<RoleDto>.Error("复制角色失败", "DUPLICATE_ROLE_FAILED");
            }
        }

        /// <summary>
        /// 获取所有系统权限（扁平列表）
        /// </summary>
        public async Task<ApiResponse<List<SysPermission>>> GetSysPermissionsAsync()
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
                    return ApiResponse<List<SysPermission>>.Error(
                        adminDecision.Message,
                        adminDecision.ErrorCode
                    );
                }

                var list = await db.Queryable<SysPermission>()
                    .Where(p => p.IsDeleted == false)
                    .OrderBy(p => p.Category)
                    .OrderBy(p => p.Code)
                    .ToListAsync();
                return ApiResponse<List<SysPermission>>.OK(list, "获取系统权限列表成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取系统权限列表失败");
                return ApiResponse<List<SysPermission>>.Error(
                    "获取系统权限列表失败",
                    "GET_SYS_PERMISSIONS_FAILED"
                );
            }
        }

        /// <summary>
        /// 获取拥有指定权限的角色列表
        /// </summary>
        public async Task<ApiResponse<List<RoleDto>>> GetPermissionRolesAsync(string permissionCode)
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
                    return ApiResponse<List<RoleDto>>.Error(
                        adminDecision.Message,
                        adminDecision.ErrorCode
                    );
                }

                var roles = await db.Queryable<Role>()
                    .InnerJoin<SysRolePermission>((r, rp) => r.RoleGUID == rp.RoleGuid)
                    .Where((r, rp) => rp.PermissionCode == permissionCode && rp.IsDeleted == false)
                    .Select(r => new RoleDto
                    {
                        RoleGUID = r.RoleGUID,
                        RoleName = r.RoleName,
                        Description = r.Description,
                        IsActive = r.IsActive,
                        CreatedAt = r.CreatedAt,
                        UpdatedAt = r.UpdatedAt ?? r.CreatedAt,
                    })
                    .ToListAsync();

                return ApiResponse<List<RoleDto>>.OK(roles, "获取权限角色列表成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "获取权限角色列表失败，PermissionCode: {PermissionCode}",
                    permissionCode
                );
                return ApiResponse<List<RoleDto>>.Error(
                    "获取权限角色列表失败",
                    "GET_PERMISSION_ROLES_FAILED"
                );
            }
        }

        /// <summary>
        /// 获取每个权限被显式分配的角色数量
        /// 📱 供移动端权限列表一次性展示「N 个角色」，避免逐个权限调用 GetPermissionRolesAsync
        /// </summary>
        public async Task<ApiResponse<Dictionary<string, int>>> GetPermissionRoleCountsAsync()
        {
            try
            {
                var db = _context.Db;
                // 与 GetPermissionRolesAsync 保持相同的管理员校验，避免通过聚合接口绕过服务层限制。
                var adminDecision = await UserAccessMutationSecurity.ValidateAdminOperationAsync(
                    db,
                    _manageableStoreScopeService
                );
                if (!adminDecision.IsAllowed)
                {
                    return ApiResponse<Dictionary<string, int>>.Error(
                        adminDecision.Message,
                        adminDecision.ErrorCode
                    );
                }

                // 仅统计仍然存在的角色；被删除的角色不应继续占用权限的引用计数。
                var rows = await db.Queryable<SysRolePermission>()
                    .InnerJoin<Role>((rp, r) => rp.RoleGuid == r.RoleGUID)
                    .Where((rp, r) => rp.IsDeleted == false && r.IsDeleted == false)
                    .GroupBy((rp, r) => rp.PermissionCode)
                    .Select((rp, r) => new PermissionRoleCountRow
                    {
                        PermissionCode = rp.PermissionCode,
                        RoleCount = SqlFunc.AggregateDistinctCount(rp.RoleGuid),
                    })
                    .ToListAsync();

                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in rows)
                {
                    if (string.IsNullOrWhiteSpace(row.PermissionCode))
                    {
                        continue;
                    }
                    counts[row.PermissionCode.Trim()] = row.RoleCount;
                }

                return ApiResponse<Dictionary<string, int>>.OK(counts, "获取权限角色数量成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取权限角色数量失败");
                return ApiResponse<Dictionary<string, int>>.Error(
                    "获取权限角色数量失败",
                    "GET_PERMISSION_ROLE_COUNTS_FAILED"
                );
            }
        }

        private sealed class PermissionRoleCountRow
        {
            public string PermissionCode { get; set; } = string.Empty;
            public int RoleCount { get; set; }
        }

        /// <summary>
        /// 为权限分配角色
        /// </summary>
        public async Task<ApiResponse<bool>> AssignRolesToPermissionAsync(
            string permissionCode,
            List<string> roleGuids
        )
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
                    return ApiResponse<bool>.Error(
                        adminDecision.Message,
                        adminDecision.ErrorCode
                    );
                }

                await db.Ado.BeginTranAsync();
                try
                {
                    // 1. 删除该权限的所有角色关联
                    await db.Deleteable<SysRolePermission>()
                        .Where(rp => rp.PermissionCode == permissionCode)
                        .ExecuteCommandAsync();

                    // 2. 添加新的角色关联
                    if (roleGuids != null && roleGuids.Any())
                    {
                        var superAdminRoleNames = Permissions.SuperAdminRoleNames.ToList();
                        var assignableRoleGuids = await db.Queryable<Role>()
                            .Where(r =>
                                roleGuids.Contains(r.RoleGUID)
                                && !r.IsDeleted
                                && !superAdminRoleNames.Contains(r.RoleName)
                            )
                            .Select(r => r.RoleGUID)
                            .ToListAsync();

                        var newPermissions = assignableRoleGuids
                            .Select(roleGuid => new SysRolePermission
                            {
                                Id = UuidHelper.GenerateUuid7(),
                                RoleGuid = roleGuid,
                                PermissionCode = permissionCode,
                                CreatedAt = DateTime.Now,
                                CreatedBy = GetCurrentUsername(),
                                UpdatedAt = DateTime.Now,
                                UpdatedBy = GetCurrentUsername(),
                                IsDeleted = false
                            })
                            .ToList();

                        await db.Insertable(newPermissions).ExecuteCommandAsync();
                    }

                    await db.Ado.CommitTranAsync();

                    _logger.LogInformation(
                        "为权限分配角色成功，PermissionCode: {PermissionCode}, RoleCount: {RoleCount}",
                        permissionCode,
                        roleGuids?.Count ?? 0
                    );
                    return ApiResponse<bool>.OK(true, "权限角色分配成功");
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "为权限分配角色失败，PermissionCode: {PermissionCode}",
                    permissionCode
                );
                return ApiResponse<bool>.Error(
                    "权限角色分配失败",
                    "ASSIGN_ROLES_TO_PERMISSION_FAILED"
                );
            }
        }

        /// <summary>
        /// 获取被直接授予指定权限的用户列表（不含通过角色继承的用户）
        /// </summary>
        public async Task<ApiResponse<List<RoleUserDto>>> GetPermissionUsersAsync(
            string permissionCode
        )
        {
            try
            {
                var db = _context.Db;
                // 与 GetPermissionRolesAsync 相同：权限反查属于全局授权视图，仅管理员可读。
                var adminDecision = await UserAccessMutationSecurity.ValidateAdminOperationAsync(
                    db,
                    _manageableStoreScopeService
                );
                if (!adminDecision.IsAllowed)
                {
                    return ApiResponse<List<RoleUserDto>>.Error(
                        adminDecision.Message,
                        adminDecision.ErrorCode
                    );
                }

                var normalizedCode = permissionCode?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(normalizedCode))
                {
                    return ApiResponse<List<RoleUserDto>>.Error(
                        "权限代码不能为空",
                        "PERMISSION_CODE_REQUIRED"
                    );
                }

                var rows = await db.Queryable<SysUserPermission>()
                    .InnerJoin<User>((up, u) => up.UserGuid == u.UserGUID)
                    .Where((up, u) =>
                        up.PermissionCode == normalizedCode
                        && up.IsDeleted == false
                        && u.IsDeleted == false
                    )
                    .Select((up, u) => new RoleUserDto
                    {
                        UserGUID = u.UserGUID,
                        Username = u.Username,
                        Email = u.Email,
                        FullName = u.FullName,
                        GivenName = u.GivenName,
                        MiddleName = u.MiddleName,
                        FamilyName = u.FamilyName,
                        Nickname = u.Nickname,
                        IsActive = u.IsActive,
                        AssignedAt = up.CreatedAt,
                    })
                    .ToListAsync();

                // 历史数据可能存在同一用户的重复授权行，按用户去重并保留最早授权时间。
                var users = rows
                    .GroupBy(item => item.UserGUID, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderBy(item => item.AssignedAt).First())
                    .OrderBy(item => item.Username, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return ApiResponse<List<RoleUserDto>>.OK(users, "获取权限用户列表成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "获取权限用户列表失败，PermissionCode: {PermissionCode}",
                    permissionCode
                );
                return ApiResponse<List<RoleUserDto>>.Error(
                    "获取权限用户列表失败",
                    "GET_PERMISSION_USERS_FAILED"
                );
            }
        }

        /// <summary>
        /// 按增量调整指定权限的直接授权用户
        /// </summary>
        public async Task<ApiResponse<bool>> AssignUsersToPermissionAsync(
            string permissionCode,
            PermissionUserAssignmentDto dto
        )
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
                    return ApiResponse<bool>.Error(
                        adminDecision.Message,
                        adminDecision.ErrorCode
                    );
                }

                var normalizedCode = permissionCode?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(normalizedCode))
                {
                    return ApiResponse<bool>.Error("权限代码不能为空", "PERMISSION_CODE_REQUIRED");
                }

                var addGuids = NormalizeUserGuidList(dto?.AddUserGuids);
                var removeGuids = NormalizeUserGuidList(dto?.RemoveUserGuids);
                if (addGuids.Any(guid => removeGuids.Contains(guid, StringComparer.OrdinalIgnoreCase)))
                {
                    return ApiResponse<bool>.Error(
                        "同一用户不能同时新增和移除授权",
                        "PERMISSION_USER_ASSIGNMENT_CONFLICT"
                    );
                }

                if (addGuids.Count == 0 && removeGuids.Count == 0)
                {
                    return ApiResponse<bool>.OK(true, "权限用户授权无变化");
                }

                // 优先使用库内权限的原始代码写入，保证与角色授权、鉴权查询使用同一拼写。
                var persistedCode = await db.Queryable<SysPermission>()
                    .Where(item => item.Code == normalizedCode && !item.IsDeleted)
                    .Select(item => item.Code)
                    .FirstAsync();
                var builtInCode = PermissionSeedData.SalesDashboardPermissions
                    .Select(item => item.Code)
                    .FirstOrDefault(code =>
                        code.Equals(normalizedCode, StringComparison.OrdinalIgnoreCase)
                    );
                var assignableCode = !string.IsNullOrWhiteSpace(persistedCode)
                    ? persistedCode
                    : builtInCode;

                // 新增授权要求权限已入库（或为内置销售看板权限），否则写入后既不生效也无法在用户侧看到；
                // 移除授权不做该限制，允许清理已下线权限的残留授权。
                if (addGuids.Count > 0 && string.IsNullOrWhiteSpace(assignableCode))
                {
                    return ApiResponse<bool>.Error(
                        "权限不存在或尚未入库，无法直接授权给用户",
                        "PERMISSION_NOT_FOUND"
                    );
                }

                var storedCode = assignableCode ?? normalizedCode;
                var targetUserGuids = new List<string>();
                if (addGuids.Count > 0)
                {
                    targetUserGuids = await db.Queryable<User>()
                        .Where(item => addGuids.Contains(item.UserGUID) && !item.IsDeleted)
                        .Select(item => item.UserGUID)
                        .ToListAsync();
                    if (
                        addGuids.Any(guid =>
                            !targetUserGuids.Contains(guid, StringComparer.OrdinalIgnoreCase)
                        )
                    )
                    {
                        // 任一目标无效即整体拒绝，避免部分成功导致前端读回核验不一致。
                        return ApiResponse<bool>.Error(
                            "部分用户不存在或已删除",
                            "USER_NOT_FOUND"
                        );
                    }
                }

                await db.Ado.BeginTranAsync();
                try
                {
                    if (removeGuids.Count > 0)
                    {
                        await db.Deleteable<SysUserPermission>()
                            .Where(item =>
                                item.PermissionCode == storedCode
                                && removeGuids.Contains(item.UserGuid)
                            )
                            .ExecuteCommandAsync();
                    }

                    if (targetUserGuids.Count > 0)
                    {
                        // 已持有该直接权限的用户保持原记录，保留其授权时间与审计字段。
                        var alreadyGranted = await db.Queryable<SysUserPermission>()
                            .Where(item =>
                                item.PermissionCode == storedCode
                                && !item.IsDeleted
                                && targetUserGuids.Contains(item.UserGuid)
                            )
                            .Select(item => item.UserGuid)
                            .ToListAsync();
                        var alreadyGrantedSet = alreadyGranted.ToHashSet(
                            StringComparer.OrdinalIgnoreCase
                        );
                        var now = DateTime.Now;
                        var operatorName = GetCurrentUsername();
                        var newLinks = targetUserGuids
                            .Where(guid => !alreadyGrantedSet.Contains(guid))
                            .Select(guid => new SysUserPermission
                            {
                                Id = UuidHelper.GenerateUuid7(),
                                UserGuid = guid,
                                PermissionCode = storedCode,
                                CreatedAt = now,
                                CreatedBy = operatorName,
                                UpdatedAt = now,
                                UpdatedBy = operatorName,
                                IsDeleted = false,
                            })
                            .ToList();

                        if (newLinks.Count > 0)
                        {
                            await db.Insertable(newLinks).ExecuteCommandAsync();
                        }
                    }

                    await db.Ado.CommitTranAsync();

                    _logger.LogInformation(
                        "调整权限直接授权用户成功，PermissionCode: {PermissionCode}, Added: {AddedCount}, Removed: {RemovedCount}",
                        storedCode,
                        addGuids.Count,
                        removeGuids.Count
                    );
                    return ApiResponse<bool>.OK(true, "权限用户授权已更新");
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "调整权限直接授权用户失败，PermissionCode: {PermissionCode}",
                    permissionCode
                );
                return ApiResponse<bool>.Error(
                    "权限用户授权失败",
                    "ASSIGN_USERS_TO_PERMISSION_FAILED"
                );
            }
        }

        private static List<string> NormalizeUserGuidList(IEnumerable<string>? values)
        {
            return (values ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// 创建新权限
        /// </summary>
        public async Task<ApiResponse<List<SysPermission>>> CreatePermissionAsync(CreateSysPermissionDto dto)
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
                    return ApiResponse<List<SysPermission>>.Error(
                        adminDecision.Message,
                        adminDecision.ErrorCode
                    );
                }

                var createdPermissions = new List<SysPermission>();
                var permissionDtos = new List<SysPermission>();

                if (dto.Actions != null && dto.Actions.Any())
                {
                    // 批量创建模式
                    foreach (var action in dto.Actions)
                    {
                        var suffix = action switch
                        {
                            "Create" => "创建",
                            "Delete" => "删除",
                            "Edit" => "编辑",
                            "View" => "查看",
                            _ => action
                        };

                        var newCode = $"{dto.Code}.{action}";
                        var newName = $"{dto.Name} - {suffix}";
                        var newDescription = !string.IsNullOrWhiteSpace(dto.Description)
                            ? $"{dto.Description} - {suffix}"
                            : $"{newName}";

                        permissionDtos.Add(new SysPermission
                        {
                            Id = UuidHelper.GenerateUuid7(),
                            Code = newCode,
                            Name = newName,
                            Category = dto.Category,
                            Description = newDescription,
                            CreatedAt = DateTime.Now,
                            CreatedBy = GetCurrentUsername(),
                            UpdatedAt = DateTime.Now,
                            UpdatedBy = GetCurrentUsername(),
                            IsDeleted = false
                        });
                    }
                }
                else
                {
                    // 单个创建模式
                    permissionDtos.Add(new SysPermission
                    {
                        Id = UuidHelper.GenerateUuid7(),
                        Code = dto.Code,
                        Name = dto.Name,
                        Category = dto.Category,
                        Description = dto.Description,
                        CreatedAt = DateTime.Now,
                        CreatedBy = GetCurrentUsername(),
                        UpdatedAt = DateTime.Now,
                        UpdatedBy = GetCurrentUsername(),
                        IsDeleted = false
                    });
                }

                // 检查重复并插入
                foreach (var permission in permissionDtos)
                {
                    var exists = await db.Queryable<SysPermission>()
                        .Where(p => p.Code == permission.Code && !p.IsDeleted)
                        .AnyAsync();

                    if (!exists)
                    {
                        await db.Insertable(permission).ExecuteCommandAsync();
                        createdPermissions.Add(permission);
                        _logger.LogInformation("创建权限成功，Code: {Code}", permission.Code);
                    }
                    else
                    {
                        _logger.LogWarning("权限代码已存在，跳过创建: {Code}", permission.Code);
                    }
                }

                if (!createdPermissions.Any())
                {
                    return ApiResponse<List<SysPermission>>.Error("未创建任何权限（可能代码已存在）", "NO_PERMISSION_CREATED");
                }

                return ApiResponse<List<SysPermission>>.OK(createdPermissions, $"成功创建 {createdPermissions.Count} 个权限");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建权限失败，BaseCode: {Code}", dto.Code);
                return ApiResponse<List<SysPermission>>.Error("创建权限失败", "CREATE_PERMISSION_FAILED");
            }
        }

        /// <summary>
        /// 软删除权限（按 code 唯一标识），同时清理 SysRolePermission 关联
        /// </summary>
        public async Task<ApiResponse<bool>> DeletePermissionAsync(string code)
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
                    return ApiResponse<bool>.Error(
                        adminDecision.Message,
                        adminDecision.ErrorCode
                    );
                }

                var permission = await db.Queryable<SysPermission>()
                    .Where(p => p.Code == code && !p.IsDeleted)
                    .FirstAsync();

                if (permission == null)
                {
                    return ApiResponse<bool>.Error("权限不存在或已删除", "PERMISSION_NOT_FOUND");
                }

                // 删除角色-权限关联
                await db.Deleteable<SysRolePermission>()
                    .Where(rp => rp.PermissionCode == code)
                    .ExecuteCommandAsync();

                // 软删除权限
                permission.IsDeleted = true;
                permission.UpdatedAt = DateTime.Now;
                permission.UpdatedBy = GetCurrentUsername();
                await db.Updateable(permission).ExecuteCommandAsync();

                _logger.LogInformation("删除权限成功，Code: {Code}", code);
                return ApiResponse<bool>.OK(true, "权限已删除");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除权限失败，Code: {Code}", code);
                return ApiResponse<bool>.Error("删除权限失败", "DELETE_PERMISSION_FAILED");
            }
        }
    }
}
