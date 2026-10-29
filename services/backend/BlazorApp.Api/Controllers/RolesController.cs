using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers
{
    /// <summary>
    /// 角色管理控制器
    /// 🔐 提供角色数据的CRUD操作和权限管理功能
    /// 🔐 包含完整的授权控制，确保只有有权限的用户才能访问相应功能
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class RolesController : ControllerBase
    {
        /// <summary>角色目录范围：只列等级严格低于当前登录人的角色（人员列表角色筛选用）。</summary>
        public const string ActiveRolesScopeBelowActor = "below-actor";

        private readonly IRoleService _roleService;
        private readonly ILogger<RolesController> _logger;

        public RolesController(IRoleService roleService, ILogger<RolesController> logger)
        {
            _roleService = roleService;
            _logger = logger;
        }

        /// <summary>
        /// 获取角色列表
        /// </summary>
        [HttpGet]
        [Authorize(Policy = Permissions.Roles.View)]
        public async Task<IActionResult> GetRoles([FromQuery] RoleQueryDto query)
        {
            try
            {
                var result = await _roleService.GetRolesAsync(query);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取角色列表失败");
                return StatusCode(
                    500,
                    ApiResponse<PagedResult<RoleDto>>.Error(
                        "服务器内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 获取角色列表（高性能版本）
        /// ⚡ 优化查询性能，解决N+1问题，使用单一复合查询
        /// 📋 支持分页、搜索、筛选的角色数据查询
        /// </summary>
        /// <param name="query">查询参数（分页、搜索条件等）</param>
        /// <returns>分页的角色数据</returns>
        [HttpGet("optimized")]
        [Authorize(Policy = Permissions.Roles.View)]
        public async Task<IActionResult> GetRolesOptimized([FromQuery] RoleQueryDto query)
        {
            try
            {
                var result = await _roleService.GetRolesOptimizedAsync(query);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取角色列表失败");
                return StatusCode(
                    500,
                    ApiResponse<PagedResult<RoleDto>>.Error(
                        "服务器内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 角色服务性能测试端点 - 比较原始查询与优化查询的性能差异
        /// 🧪 用于评估查询优化效果
        /// </summary>
        /// <param name="query">查询参数</param>
        /// <returns>性能测试报告</returns>
        [HttpGet("performance-test")]
        [Authorize(Policy = Permissions.System.ManageSettings)]
        public async Task<IActionResult> RolePerformanceTest([FromQuery] RoleQueryDto query)
        {
            try
            {
                var result = await _roleService.PerformanceTestAsync(query);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "角色服务性能测试失败");
                return StatusCode(
                    500,
                    ApiResponse<object>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 获取所有活跃角色（不分页）
        /// </summary>
        [HttpGet("active")]
        [Authorize]
        public async Task<IActionResult> GetActiveRoles([FromQuery] string? scope = null)
        {
            try
            {
                var currentUserGuid = ResolveCurrentUserGuid(User);
                if (string.IsNullOrWhiteSpace(currentUserGuid))
                {
                    return Forbid();
                }

                var isAdmin = await CurrentActorIsAdminAsync();
                var isStoreManager = false;
                if (!isAdmin)
                {
                    foreach (var roleName in Permissions.StoreManagerRoleNames)
                    {
                        var roleResult = await _roleService.UserHasRoleAsync(
                            currentUserGuid,
                            roleName
                        );
                        if (roleResult?.Data == true)
                        {
                            isStoreManager = true;
                            break;
                        }
                    }
                }

                var canViewRoles = await _roleService.UserHasPermissionAsync(
                    currentUserGuid,
                    Permissions.Roles.View
                );
                var canManageUserRoles = canViewRoles?.Data == true
                    ? null
                    : await _roleService.UserHasPermissionAsync(
                        currentUserGuid,
                        Permissions.Users.ManageRoles
                    );
                var hasFullCatalogAccess = isAdmin
                    || (!isStoreManager
                        && (canViewRoles?.Data == true || canManageUserRoles?.Data == true));

                if (!hasFullCatalogAccess && !isStoreManager)
                {
                    return Forbid();
                }

                // scope=below-actor：人员列表的角色筛选专用，列出等级严格低于当前登录人的全部角色
                // （与改写权限同一规则）；管理员仍看全部。默认目录（分配角色等场景）行为不变。
                if (string.Equals(scope, ActiveRolesScopeBelowActor, StringComparison.OrdinalIgnoreCase)
                    && !isAdmin)
                {
                    return Ok(await _roleService.GetActiveRolesBelowUserLevelAsync(currentUserGuid));
                }

                var result = await _roleService.GetActiveRolesAsync();
                if (isStoreManager && result.Success && result.Data != null)
                {
                    // 店长目录只暴露员工角色；最终写入仍由 UserService 再次校验范围和角色。
                    result = ApiResponse<List<RoleDto>>.OK(
                        result.Data.Where(role => Permissions.IsEmployeeRole(role.RoleName)).ToList(),
                        result.Message
                    );
                }
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取活跃角色列表失败");
                return StatusCode(
                    500,
                    ApiResponse<List<RoleDto>>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        private static string ResolveCurrentUserGuid(ClaimsPrincipal user)
        {
            return user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? user.FindFirst("userGuid")?.Value
                ?? user.FindFirst("userId")?.Value
                ?? user.FindFirst("sub")?.Value
                ?? string.Empty;
        }

        private async Task<bool> CurrentActorIsAdminAsync()
        {
            var currentUserGuid = ResolveCurrentUserGuid(User);
            if (string.IsNullOrWhiteSpace(currentUserGuid))
            {
                return false;
            }

            foreach (var adminRoleName in Permissions.SuperAdminRoleNames)
            {
                var result = await _roleService.UserHasRoleAsync(
                    currentUserGuid,
                    adminRoleName
                );
                if (result?.Data == true)
                {
                    return true;
                }
            }

            return false;
        }

        private IActionResult AdminRequiredResult()
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                ApiResponse<bool>.Error("只有管理员可以检查其他用户的角色或权限", "ADMIN_REQUIRED")
            );
        }

        private IActionResult ToRoleAccessMutationActionResult<T>(ApiResponse<T> result)
        {
            if (result.Success)
            {
                return Ok(result);
            }

            return result.ErrorCode switch
            {
                "ADMIN_REQUIRED"
                or "ACCESS_DELEGATOR_DENIED"
                or "USER_ACCESS_ADMIN_REQUIRED"
                or "USER_ACCESS_ACTOR_DENIED"
                or "DERIVED_STORE_MANAGER_ROLE" => StatusCode(
                    StatusCodes.Status403Forbidden,
                    result
                ),
                "ROLE_NOT_FOUND" or "USER_NOT_FOUND" => NotFound(result),
                _ => BadRequest(result),
            };
        }

        /// <summary>
        /// 根据GUID获取角色详情
        /// </summary>
        [HttpGet("guid/{guid}")]
        [Authorize(Policy = Permissions.Roles.View)]
        public async Task<IActionResult> GetRoleByGuid(string guid)
        {
            try
            {
                var result = await _roleService.GetRoleByGuidAsync(guid);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取角色详情失败，RoleGUID: {RoleGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<RoleDetailDto>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 根据角色名获取角色
        /// </summary>
        [HttpGet("name/{name}")]
        [Authorize(Policy = Permissions.Roles.View)]
        public async Task<IActionResult> GetRoleByName(string name)
        {
            try
            {
                var result = await _roleService.GetRoleByNameAsync(name);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "根据角色名获取角色失败，RoleName: {RoleName}", name);
                return StatusCode(
                    500,
                    ApiResponse<RoleDto>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 创建新角色
        /// ➕ 只有Admin角色才能创建角色，确保数据安全
        /// </summary>
        /// <param name="dto">创建角色的数据传输对象</param>
        /// <returns>创建结果</returns>
        [HttpPost]
        [Authorize(Policy = Permissions.Roles.Create)]
        public async Task<IActionResult> CreateRole([FromBody] CreateRoleDto dto)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(
                        ApiResponse<RoleDto>.Error(
                            "请求参数验证失败",
                            "VALIDATION_ERROR",
                            ModelState
                        )
                    );
                }

                var result = await _roleService.CreateRoleAsync(dto);
                return ToRoleAccessMutationActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建角色失败");
                return StatusCode(
                    500,
                    ApiResponse<RoleDto>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 根据GUID更新角色
        /// </summary>
        [HttpPut("guid/{guid}")]
        [Authorize(Policy = Permissions.Roles.Edit)]
        public async Task<IActionResult> UpdateRoleByGuid(string guid, [FromBody] UpdateRoleDto dto)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(
                        ApiResponse<RoleDto>.Error(
                            "请求参数验证失败",
                            "VALIDATION_ERROR",
                            ModelState
                        )
                    );
                }

                var result = await _roleService.UpdateRoleByGuidAsync(guid, dto);
                return ToRoleAccessMutationActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新角色失败，RoleGUID: {RoleGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<RoleDto>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 根据GUID删除角色
        /// </summary>
        [HttpDelete("guid/{guid}")]
        [Authorize(Policy = Permissions.Roles.Delete)]
        public async Task<IActionResult> DeleteRoleByGuid(string guid)
        {
            try
            {
                var result = await _roleService.DeleteRoleByGuidAsync(guid);
                return ToRoleAccessMutationActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除角色失败，RoleGUID: {RoleGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 根据GUID更新角色状态
        /// </summary>
        [HttpPut("guid/{guid}/status")]
        [Authorize(Policy = Permissions.Roles.Edit)]
        public async Task<IActionResult> UpdateRoleStatusByGuid(
            string guid,
            [FromBody] UpdateRoleStatusDto dto
        )
        {
            try
            {
                var result = await _roleService.UpdateRoleStatusByGuidAsync(guid, dto.IsActive);
                return ToRoleAccessMutationActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新角色状态失败，RoleGUID: {RoleGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 获取角色的用户列表
        /// </summary>
        [HttpGet("guid/{guid}/users")]
        [Authorize(Policy = Permissions.Roles.View)]
        public async Task<IActionResult> GetRoleUsers(string guid, [FromQuery] RoleQueryDto query)
        {
            try
            {
                var result = await _roleService.GetRoleUsersAsync(guid, query);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取角色用户列表失败，RoleGUID: {RoleGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<PagedResult<RoleUserDto>>.Error(
                        "服务器内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 为角色添加用户
        /// </summary>
        [HttpPost("guid/{guid}/users")]
        [Authorize(Policy = Permissions.Roles.ManageUsers)]
        public async Task<IActionResult> AddUsersToRole(
            string guid,
            [FromBody] List<string> userGuids
        )
        {
            try
            {
                if (userGuids == null || !userGuids.Any())
                {
                    return BadRequest(
                        ApiResponse<bool>.Error("用户GUID列表不能为空", "VALIDATION_ERROR")
                    );
                }

                var result = await _roleService.AddUsersToRoleAsync(guid, userGuids);
                return ToRoleAccessMutationActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "为角色添加用户失败，RoleGUID: {RoleGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 从角色移除用户
        /// </summary>
        [HttpDelete("guid/{guid}/users/{userGuid}")]
        [Authorize(Policy = Permissions.Roles.ManageUsers)]
        public async Task<IActionResult> RemoveUserFromRole(string guid, string userGuid)
        {
            try
            {
                var result = await _roleService.RemoveUserFromRoleAsync(guid, userGuid);
                return ToRoleAccessMutationActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "从角色移除用户失败，RoleGUID: {RoleGUID}, UserGUID: {UserGUID}",
                    guid,
                    userGuid
                );
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 批量管理角色
        /// </summary>
        [HttpPost("batch")]
        [Authorize(Policy = Permissions.Roles.Edit)]
        public async Task<IActionResult> BatchManageRoles([FromBody] BatchRoleOperationDto dto)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(
                        ApiResponse<bool>.Error("请求参数验证失败", "VALIDATION_ERROR", ModelState)
                    );
                }

                var result = await _roleService.BatchManageRolesAsync(dto);
                return ToRoleAccessMutationActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量管理角色失败");
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 获取角色统计信息
        /// </summary>
        [HttpGet("statistics")]
        [Authorize(Policy = Permissions.Roles.View)]
        public async Task<IActionResult> GetRoleStatistics()
        {
            try
            {
                var result = await _roleService.GetRoleStatisticsAsync();
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取角色统计失败");
                return StatusCode(
                    500,
                    ApiResponse<RoleStatisticsDto>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 检查角色名是否可用
        /// </summary>
        [HttpGet("check-name/{name}")]
        [Authorize(Policy = Permissions.Roles.View)]
        public async Task<IActionResult> CheckRoleNameAvailability(
            string name,
            [FromQuery] string? excludeRoleGuid = null
        )
        {
            try
            {
                var result = await _roleService.IsRoleNameAvailableAsync(name, excludeRoleGuid);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查角色名可用性失败，RoleName: {RoleName}", name);
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 获取所有权限列表
        /// </summary>
        [HttpGet("permissions")]
        public async Task<IActionResult> GetPermissions()
        {
            try
            {
                var result = await _roleService.GetPermissionsAsync();
                return ToRoleAccessMutationActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取权限列表失败");
                return StatusCode(
                    500,
                    ApiResponse<List<PermissionCategoryDto>>.Error(
                        "服务器内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 获取权限目录元数据
        /// </summary>
        [HttpGet("permissions/catalog")]
        [Authorize(Policy = Permissions.Roles.View)]
        public async Task<IActionResult> GetPermissionCatalog()
        {
            try
            {
                var result = await _roleService.GetPermissionCatalogAsync();
                return ToRoleAccessMutationActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取权限目录失败");
                return StatusCode(
                    500,
                    ApiResponse<PermissionCatalogDto>.Error(
                        "服务器内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 获取所有系统权限（扁平列表）
        /// 📋 用于权限管理页面展示
        /// </summary>
        [HttpGet("sys-permissions")]
        [Authorize(Policy = Permissions.Roles.View)]
        public async Task<IActionResult> GetSysPermissions()
        {
            try
            {
                var result = await _roleService.GetSysPermissionsAsync();
                return ToRoleAccessMutationActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取系统权限列表失败");
                return StatusCode(
                    500,
                    ApiResponse<List<SysPermission>>.Error(
                        "服务器内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 获取拥有指定权限的角色列表
        /// </summary>
        [HttpGet("permissions/{code}/roles")]
        [Authorize(Policy = Permissions.Roles.View)]
        public async Task<IActionResult> GetPermissionRoles(string code)
        {
            try
            {
                var result = await _roleService.GetPermissionRolesAsync(code);
                return ToRoleAccessMutationActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "获取权限角色列表失败，PermissionCode: {PermissionCode}",
                    code
                );
                return StatusCode(
                    500,
                    ApiResponse<List<RoleDto>>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 获取每个权限被显式分配的角色数量（权限代码 → 数量）
        /// 📱 移动端权限列表用于展示「N 个角色」
        /// </summary>
        [HttpGet("permissions/role-counts")]
        [Authorize(Policy = Permissions.Roles.View)]
        public async Task<IActionResult> GetPermissionRoleCounts()
        {
            try
            {
                var result = await _roleService.GetPermissionRoleCountsAsync();
                return ToRoleAccessMutationActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取权限角色数量失败");
                return StatusCode(
                    500,
                    ApiResponse<Dictionary<string, int>>.Error(
                        "服务器内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 为权限分配角色
        /// </summary>
        [HttpPost("permissions/{code}/roles")]
        [Authorize(Policy = Permissions.Roles.ManagePermissions)]
        public async Task<IActionResult> AssignRolesToPermission(
            string code,
            [FromBody] List<string> roleGuids
        )
        {
            try
            {
                var result = await _roleService.AssignRolesToPermissionAsync(code, roleGuids);
                return ToRoleAccessMutationActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "为权限分配角色失败，PermissionCode: {PermissionCode}", code);
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 获取被直接授予指定权限的用户列表（不含通过角色继承的用户）
        /// </summary>
        [HttpGet("permissions/{code}/users")]
        [Authorize(Policy = Permissions.Roles.View)]
        public async Task<IActionResult> GetPermissionUsers(string code)
        {
            try
            {
                var result = await _roleService.GetPermissionUsersAsync(code);
                return ToRoleAccessMutationActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "获取权限用户列表失败，PermissionCode: {PermissionCode}",
                    code
                );
                return StatusCode(
                    500,
                    ApiResponse<List<RoleUserDto>>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 按增量调整指定权限的直接授权用户
        /// </summary>
        [HttpPost("permissions/{code}/users")]
        [Authorize(Policy = Permissions.Roles.ManagePermissions)]
        public async Task<IActionResult> AssignUsersToPermission(
            string code,
            [FromBody] PermissionUserAssignmentDto dto
        )
        {
            try
            {
                if (dto == null || !ModelState.IsValid)
                {
                    return BadRequest(
                        ApiResponse<bool>.Error("请求参数验证失败", "VALIDATION_ERROR", ModelState)
                    );
                }

                var result = await _roleService.AssignUsersToPermissionAsync(code, dto);
                return ToRoleAccessMutationActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "为权限分配用户失败，PermissionCode: {PermissionCode}", code);
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 创建新权限
        /// </summary>
        [HttpPost("permissions")]
        [Authorize(Policy = Permissions.Roles.ManagePermissions)]
        public async Task<IActionResult> CreatePermission([FromBody] CreateSysPermissionDto dto)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(
                        ApiResponse<SysPermission>.Error(
                            "请求参数验证失败",
                            "VALIDATION_ERROR",
                            ModelState
                        )
                    );
                }

                var result = await _roleService.CreatePermissionAsync(dto);
                return ToRoleAccessMutationActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建权限失败");
                return StatusCode(
                    500,
                    ApiResponse<List<SysPermission>>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 删除权限（软删除，同时清理角色-权限关联）
        /// </summary>
        [HttpDelete("permissions/{code}")]
        [Authorize(Policy = Permissions.Roles.ManagePermissions)]
        public async Task<IActionResult> DeletePermission(string code)
        {
            try
            {
                code = Uri.UnescapeDataString(code);
                var result = await _roleService.DeletePermissionAsync(code);
                return ToRoleAccessMutationActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除权限失败，Code: {Code}", code);
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 获取角色的权限列表
        /// </summary>
        [HttpGet("guid/{guid}/permissions")]
        [Authorize(Policy = Permissions.Roles.View)]
        public async Task<IActionResult> GetRolePermissions(string guid)
        {
            try
            {
                var result = await _roleService.GetRolePermissionsAsync(guid);
                return ToRoleAccessMutationActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取角色权限失败，RoleGUID: {RoleGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<List<string>>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 获取角色权限状态
        /// </summary>
        [HttpGet("guid/{guid}/permissions/state")]
        [Authorize(Policy = Permissions.Roles.View)]
        public async Task<IActionResult> GetRolePermissionState(string guid)
        {
            try
            {
                var result = await _roleService.GetRolePermissionStateAsync(guid);
                return ToRoleAccessMutationActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取角色权限状态失败，RoleGUID: {RoleGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<RolePermissionStateDto>.Error(
                        "服务器内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 为角色分配权限
        /// </summary>
        [HttpPost("guid/{guid}/permissions")]
        [Authorize(Policy = Permissions.Roles.ManagePermissions)]
        public async Task<IActionResult> AssignPermissionsToRole(
            string guid,
            [FromBody] RolePermissionAssignmentDto dto
        )
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(
                        ApiResponse<bool>.Error("请求参数验证失败", "VALIDATION_ERROR", ModelState)
                    );
                }

                var result = await _roleService.AssignPermissionsToRoleAsync(guid, dto);
                return ToRoleAccessMutationActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "为角色分配权限失败，RoleGUID: {RoleGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 检查用户是否有指定角色
        /// </summary>
        [HttpGet("check-user-role")]
        [Authorize(Policy = Permissions.Users.View)]
        public async Task<IActionResult> CheckUserHasRole(
            [FromQuery] string userGuid,
            [FromQuery] string roleName
        )
        {
            try
            {
                if (!await CurrentActorIsAdminAsync())
                {
                    return AdminRequiredResult();
                }

                if (string.IsNullOrEmpty(userGuid) || string.IsNullOrEmpty(roleName))
                {
                    return BadRequest(
                        ApiResponse<bool>.Error("用户GUID和角色名不能为空", "VALIDATION_ERROR")
                    );
                }

                var result = await _roleService.UserHasRoleAsync(userGuid, roleName);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "检查用户角色失败，UserGUID: {UserGUID}, RoleName: {RoleName}",
                    userGuid,
                    roleName
                );
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 检查用户是否有指定权限
        /// </summary>
        [HttpGet("check-user-permission")]
        [Authorize(Policy = Permissions.Users.View)]
        public async Task<IActionResult> CheckUserHasPermission(
            [FromQuery] string userGuid,
            [FromQuery] string permission
        )
        {
            try
            {
                if (!await CurrentActorIsAdminAsync())
                {
                    return AdminRequiredResult();
                }

                if (string.IsNullOrEmpty(userGuid) || string.IsNullOrEmpty(permission))
                {
                    return BadRequest(
                        ApiResponse<bool>.Error("用户GUID和权限名不能为空", "VALIDATION_ERROR")
                    );
                }

                var result = await _roleService.UserHasPermissionAsync(userGuid, permission);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "检查用户权限失败，UserGUID: {UserGUID}, Permission: {Permission}",
                    userGuid,
                    permission
                );
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 复制角色
        /// </summary>
        [HttpPost("guid/{guid}/duplicate")]
        [Authorize(Policy = Permissions.Roles.Create)]
        public async Task<IActionResult> DuplicateRole(string guid, [FromBody] DuplicateRoleDto dto)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(
                        ApiResponse<RoleDto>.Error(
                            "请求参数验证失败",
                            "VALIDATION_ERROR",
                            ModelState
                        )
                    );
                }

                var result = await _roleService.DuplicateRoleAsync(
                    guid,
                    dto.NewRoleName,
                    dto.NewDescription
                );
                return ToRoleAccessMutationActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "复制角色失败，RoleGUID: {RoleGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<RoleDto>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }
    }

    /// <summary>
    /// 复制角色请求DTO
    /// </summary>
    public class DuplicateRoleDto
    {
        [Required(ErrorMessage = "新角色名不能为空")]
        public string NewRoleName { get; set; } = string.Empty;

        public string? NewDescription { get; set; }
    }
}
