using System.Security.Claims;
using BlazorApp.Api.Authentication;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.MobileDeviceActivation;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers
{
    /// <summary>
    /// 用户管理控制器
    /// 🧑‍💼 提供用户数据的CRUD操作和权限管理功能
    /// 🔐 包含完整的授权控制，确保只有有权限的用户才能访问相应功能
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize] // 🔐 启用全局授权，所有端点都需要认证
    public class UsersController : ControllerBase
    {
        private readonly IUserService _userService;
        private readonly IRoleService _roleService;
        private readonly ILogger<UsersController> _logger;
        private readonly SqlSugarContext? _context;
        private readonly EmployeeCashierBarcodeService? _cashierBarcodeService;

        public UsersController(
            IUserService userService,
            IRoleService roleService,
            ILogger<UsersController> logger,
            SqlSugarContext? context = null,
            EmployeeCashierBarcodeService? cashierBarcodeService = null
        )
        {
            _userService = userService;
            _roleService = roleService;
            _logger = logger;
            _context = context;
            _cashierBarcodeService = cashierBarcodeService;
        }

        [HttpGet("guid/{guid}/cashier-barcode")]
        [Authorize(Policy = Permissions.Users.ManagePosTerminalPermissions)]
        public async Task<IActionResult> GetUserCashierBarcode(string guid)
        {
            Response.Headers.CacheControl = "no-store";
            var failure = await AuthorizeGlobalCashierAdminAsync(guid);
            if (failure != null) return failure;
            return Ok(await _cashierBarcodeService!.GetForUserAsync(guid));
        }

        [HttpPost("guid/{guid}/cashier-barcode/refresh")]
        [Authorize(Policy = Permissions.Users.ManagePosTerminalPermissions)]
        public async Task<IActionResult> RefreshUserCashierBarcode(
            string guid,
            [FromBody] AdminCashierBarcodeRefreshRequest? request
        )
        {
            Response.Headers.CacheControl = "no-store";
            var failure = await AuthorizeGlobalCashierAdminAsync(guid);
            if (failure != null) return failure;
            if (request == null || !request.HasExpectedBarcode)
            {
                return BadRequest(ApiResponse<object>.Error("必须提供 expectedBarcode（可为 null）", "EXPECTED_BARCODE_REQUIRED"));
            }
            var result = await _cashierBarcodeService!.RefreshForUserAsync(guid, request.ExpectedBarcode);
            if (result.Success) return Ok(result);
            // 冲突响应不回显当前条码，避免陈旧客户端通过失败响应获得敏感身份码。
            result.Data = default;
            return Conflict(result);
        }

        private async Task<IActionResult?> AuthorizeGlobalCashierAdminAsync(string targetUserGuid)
        {
            if (_context == null || _cashierBarcodeService == null) return Forbid();
            // 管理端二维码接口只接受浏览器用户会话，拒绝服务、设备和订货助手令牌。
            if (User.HasClaim(ServiceApiTokenAuthenticationDefaults.TokenTypeClaim, "true")
                || User.HasClaim("token_use", MobileDeviceAccountTokenIssuer.TokenUse)
                || User.HasClaim("token_use", "browser_extension"))
            {
                return Forbid();
            }
            var actorGuid = ResolveCurrentUserGuid(User);
            var actorUser = await _context.Db.Queryable<User>()
                .Where(item => item.UserGUID == actorGuid && !item.IsDeleted && item.IsActive)
                .FirstAsync();
            if (actorUser == null) return Forbid();
            var actor = await UserAccessMutationSecurity.ResolveActorByUserGuidAsync(_context.Db, actorGuid);
            if (!actor.IsSuperAdmin) return Forbid();
            var permission = await _roleService.UserHasPermissionAsync(actorGuid, Permissions.Users.ManagePosTerminalPermissions);
            if (permission?.Data != true) return Forbid();
            var target = await _context.Db.Queryable<User>()
                .Where(item => item.UserGUID == targetUserGuid && !item.IsDeleted && item.IsActive)
                .FirstAsync();
            if (target == null) return NotFound(ApiResponse<object>.Error("用户不存在或已停用", "USER_NOT_FOUND"));
            return null;
        }

        /// <summary>
        /// 获取用户列表
        /// 📋 支持分页、搜索、筛选的用户数据查询
        /// </summary>
        /// <param name="query">查询参数（分页、搜索条件等）</param>
        /// <returns>分页的用户数据</returns>
        [HttpGet]
        [Authorize(Policy = Permissions.Users.View)]
        public async Task<IActionResult> GetUsers([FromQuery] UserQueryDto query)
        {
            try
            {
                var result = await _userService.GetUsersAsync(query);
                return ToUserAccessActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户列表失败");
                return StatusCode(
                    500,
                    ApiResponse<PagedResult<UserDto>>.Error(
                        "服务器内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 获取用户列表（高性能版本）
        /// ⚡ 优化查询性能，解决N+1问题，使用单一复合查询
        /// 📋 支持分页、搜索、筛选的用户数据查询
        /// </summary>
        /// <param name="query">查询参数（分页、搜索条件等）</param>
        /// <returns>分页的用户数据</returns>
        [HttpGet("optimized")]
        [Authorize(Policy = Permissions.Users.View)]
        public async Task<IActionResult> GetUsersOptimized([FromQuery] UserQueryDto query)
        {
            try
            {
                var result = await _userService.GetUsersOptimizedAsync(query);
                return ToUserAccessActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户列表失败");
                return StatusCode(
                    500,
                    ApiResponse<PagedResult<UserDto>>.Error(
                        "服务器内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 性能测试端点 - 比较原始查询与优化查询的性能差异
        /// 🧪 用于评估查询优化效果
        /// </summary>
        /// <param name="query">查询参数</param>
        /// <returns>性能测试报告</returns>
        [HttpGet("performance-test")]
        [Authorize(Policy = Permissions.Users.View)]
        public async Task<IActionResult> PerformanceTest([FromQuery] UserQueryDto query)
        {
            try
            {
                var result = await _userService.PerformanceTestAsync(query);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "性能测试失败");
                return StatusCode(
                    500,
                    ApiResponse<object>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 根据GUID获取用户详情
        /// </summary>
        [HttpGet("guid/{guid}")]
        [Authorize(Policy = Permissions.Users.View)]
        public async Task<IActionResult> GetUserByGuid(string guid)
        {
            try
            {
                var result = await _userService.GetUserByGuidAsync(guid);
                return ToUserAccessActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户详情失败，UserGUID: {UserGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<UserDetailDto>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 获取用户登录记录
        /// </summary>
        [HttpGet("guid/{guid}/login-records")]
        [Authorize(Policy = Permissions.Users.View)]
        public async Task<IActionResult> GetUserLoginRecords(
            string guid,
            [FromQuery] UserLoginRecordQueryDto query
        )
        {
            try
            {
                var result = await _userService.GetUserLoginRecordsAsync(guid, query);
                return ToUserAccessActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户登录记录失败，UserGUID: {UserGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<PagedResult<UserLoginRecordDto>>.Error(
                        "服务器内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 根据用户名获取用户
        /// </summary>
        [HttpGet("username/{username}")]
        [Authorize(Policy = Permissions.Users.View)]
        public async Task<IActionResult> GetUserByUsername(string username)
        {
            try
            {
                var result = await _userService.GetUserByUsernameAsync(username);
                return ToUserAccessActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "根据用户名获取用户失败，Username: {Username}", username);
                return StatusCode(
                    500,
                    ApiResponse<UserDto>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 根据邮箱获取用户
        /// </summary>
        [HttpGet("email/{email}")]
        [Authorize(Policy = Permissions.Users.View)]
        public async Task<IActionResult> GetUserByEmail(string email)
        {
            try
            {
                var result = await _userService.GetUserByEmailAsync(email);
                return ToUserAccessActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "根据邮箱获取用户失败，Email: {Email}", email);
                return StatusCode(
                    500,
                    ApiResponse<UserDto>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 创建新用户
        /// ➕ 只有Admin角色才能创建用户，确保数据安全
        /// </summary>
        /// <param name="dto">创建用户的数据传输对象</param>
        /// <returns>创建结果</returns>
        [HttpPost]
        [Authorize(Policy = Permissions.Users.Create)]
        public async Task<IActionResult> CreateUser([FromBody] CreateUserDto dto)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(
                        ApiResponse<UserDto>.Error(
                            "请求参数验证失败",
                            "VALIDATION_ERROR",
                            ModelState
                        )
                    );
                }

                var result = await _userService.CreateUserAsync(dto);
                return ToUserAccessActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建用户失败");
                return StatusCode(
                    500,
                    ApiResponse<UserDto>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 批量创建用户（分店代码、用户名、密码；分配默认角色与对应分店）
        /// </summary>
        [HttpPost("batch-create")]
        [Authorize(Policy = Permissions.Users.Create)]
        public async Task<IActionResult> BatchCreateUsers([FromBody] BatchCreateUserRequestDto dto)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(
                        ApiResponse<BatchCreateUserResultDto>.Error(
                            "请求参数验证失败",
                            "VALIDATION_ERROR",
                            ModelState
                        )
                    );
                }

                var result = await _userService.BatchCreateUsersAsync(dto);
                return ToUserAccessActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量创建用户失败");
                return StatusCode(
                    500,
                    ApiResponse<BatchCreateUserResultDto>.Error(
                        "服务器内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 根据GUID更新用户
        /// </summary>
        [HttpPut("guid/{guid}")]
        [Authorize(Policy = Permissions.Users.Edit)]
        public async Task<IActionResult> UpdateUserByGuid(string guid, [FromBody] UpdateUserDto dto)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(
                        ApiResponse<UserDto>.Error(
                            "请求参数验证失败",
                            "VALIDATION_ERROR",
                            ModelState
                        )
                    );
                }

                var result = await _userService.UpdateUserByGuidAsync(guid, dto);
                return ToUserAccessActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新用户失败，UserGUID: {UserGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<UserDto>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 根据GUID删除用户
        /// </summary>
        [HttpDelete("guid/{guid}")]
        [Authorize(Policy = Permissions.Users.Delete)]
        public async Task<IActionResult> DeleteUserByGuid(string guid)
        {
            try
            {
                var result = await _userService.DeleteUserByGuidAsync(guid);
                return ToUserAccessActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除用户失败，UserGUID: {UserGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 根据GUID更新用户状态
        /// </summary>
        [HttpPut("guid/{guid}/status")]
        [Authorize(Policy = Permissions.Users.Edit)]
        public async Task<IActionResult> UpdateUserStatusByGuid(
            string guid,
            [FromBody] UpdateUserStatusDto dto
        )
        {
            try
            {
                var result = await _userService.UpdateUserStatusByGuidAsync(guid, dto.IsActive);
                return ToUserAccessActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新用户状态失败，UserGUID: {UserGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 更新用户密码
        /// </summary>
        [HttpPut("guid/{guid}/password")]
        [Authorize(Policy = Permissions.Users.ResetPassword)]
        public async Task<IActionResult> UpdateUserPassword(
            string guid,
            [FromBody] UpdateUserPasswordDto dto
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

                var result = await _userService.UpdateUserPasswordByGuidAsync(guid, dto);
                return ToUserAccessActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新用户密码失败，UserGUID: {UserGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 为用户分配角色
        /// </summary>
        [HttpPost("guid/{guid}/roles")]
        [Authorize]
        public async Task<IActionResult> AssignRolesToUser(
            string guid,
            [FromBody] UserRoleAssignmentDto dto
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

                var result = await _userService.AssignRolesToUserAsync(guid, dto);
                return ToUserAccessActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "为用户分配角色失败，UserGUID: {UserGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 获取用户权限状态
        /// </summary>
        [HttpGet("guid/{guid}/permissions/state")]
        [Authorize]
        public async Task<IActionResult> GetUserPermissionState(string guid)
        {
            try
            {
                var access = await AuthorizeTargetReadAsync(
                    ResolveCurrentUserGuid(User),
                    guid,
                    Permissions.Users.ManageRoles
                );
                if (access.Failure != null)
                {
                    return access.Failure;
                }

                var result = await _roleService.GetUserPermissionStateAsync(guid);
                return ToUserAccessActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户权限状态失败，UserGUID: {UserGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<UserPermissionStateDto>.Error(
                        "服务器内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 获取当前操作者可为目标员工分配的权限
        /// </summary>
        [HttpGet("guid/{guid}/access-permissions")]
        [Authorize]
        public async Task<IActionResult> GetUserAccessPermissions(string guid)
        {
            try
            {
                var result = await _roleService.GetUserAccessPermissionsAsync(
                    ResolveCurrentUserGuid(User),
                    guid
                );
                return ToUserAccessActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户可分配权限失败，UserGUID: {UserGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<UserAccessPermissionDto>.Error(
                        "服务器内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 为用户分配直接权限
        /// </summary>
        [HttpPost("guid/{guid}/permissions")]
        [Authorize]
        public async Task<IActionResult> AssignPermissionsToUser(
            string guid,
            [FromBody] UserPermissionAssignmentDto dto
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

                var result = await _roleService.AssignPermissionsToUserAsync(guid, dto);
                return ToUserAccessActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "为用户分配直接权限失败，UserGUID: {UserGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 为用户分配分店
        /// 关键逻辑：控制器要求 Users.ManageStores，与移除分店接口、前端「分店分配」入口口径一致；
        /// 服务层再按角色（管理员 / 有主分店的店长）与目标分店范围收口。
        /// 此前这里只有 [Authorize]，该权限码对写操作不起作用，单独去掉它仍能通过接口分配分店。
        /// </summary>
        [HttpPost("guid/{guid}/stores")]
        [Authorize(Policy = Permissions.Users.ManageStores)]
        public async Task<IActionResult> AssignStoresToUser(
            string guid,
            [FromBody] List<UserStoreAssignmentDto> storeAssignments
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

                var result = await _userService.AssignStoresToUserAsync(guid, storeAssignments);
                return ToUserAccessActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "为用户分配分店失败，UserGUID: {UserGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 获取用户的角色列表
        /// </summary>
        [HttpGet("guid/{guid}/roles")]
        [Authorize]
        public async Task<IActionResult> GetUserRoles(string guid)
        {
            try
            {
                var access = await AuthorizeTargetReadAsync(
                    ResolveCurrentUserGuid(User),
                    guid,
                    Permissions.Users.ManageRoles,
                    Permissions.Users.ManagePosTerminalPermissions
                );
                if (access.Failure != null)
                {
                    return access.Failure;
                }

                var result = await _userService.GetUserRolesAsync(guid);
                return ToUserAccessActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户角色失败，UserGUID: {UserGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<List<RoleDto>>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 获取用户的分店列表
        /// 用户可查看自己的分店，管理员可查看所有用户的分店
        /// </summary>
        [HttpGet("guid/{guid}/stores")]
        public async Task<IActionResult> GetUserStores(
            string guid,
            [FromServices] ICurrentUserService currentUser,
            [FromServices] ICurrentUserManageableStoreScopeService? storeScopeService = null
        )
        {
            var currentUserGuid = currentUser.GetCurrentUserGuid();
            var access = await AuthorizeTargetReadAsync(
                currentUserGuid,
                guid,
                Permissions.Users.ManageStores,
                Permissions.Users.ManagePosTerminalPermissions
            );
            if (access.Failure != null)
            {
                return access.Failure;
            }

            try
            {
                var result = await _userService.GetUserStoresAsync(guid);
                if (!result.Success || result.Data == null)
                {
                    return ToUserAccessActionResult(result);
                }

                if (access.Decision?.VisibleStoreGuids == null)
                {
                    return Ok(result);
                }

                var visibleStoreGuids = access.Decision.VisibleStoreGuids.ToHashSet(
                    StringComparer.OrdinalIgnoreCase
                );
                var visibleStores = result.Data
                    .Where(store => visibleStoreGuids.Contains(store.StoreGUID))
                    .ToList();
                if (access.Decision.RequiresDelegatedPermission && visibleStores.Count == 0)
                {
                    return Forbid();
                }

                // 本人读取返回自身真实关联；委派读取只返回双方管理范围的交集。
                return Ok(ApiResponse<List<UserStoreDto>>.OK(visibleStores, "获取用户分店成功"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户分店失败，UserGUID: {UserGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<List<UserStoreDto>>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        private async Task<(IActionResult? Failure, UserAccessReadDecision? Decision)>
            AuthorizeTargetReadAsync(
                string currentUserGuid,
                string targetUserGuid,
                params string[] delegatedPermissions
            )
        {
            if (_context == null)
            {
                return (Forbid(), null);
            }

            var decision = await UserAccessMutationSecurity.ValidateReadTargetAsync(
                _context.Db,
                currentUserGuid,
                targetUserGuid
            );
            if (decision.IsNotFound)
            {
                return (
                    NotFound(ApiResponse<object>.Error("用户不存在", "USER_NOT_FOUND")),
                    decision
                );
            }
            if (!decision.IsAllowed)
            {
                return (Forbid(), decision);
            }

            if (decision.RequiresDelegatedPermission)
            {
                var hasPermission = false;
                foreach (var permission in delegatedPermissions)
                {
                    var result = await _roleService.UserHasPermissionAsync(
                        currentUserGuid,
                        permission
                    );
                    if (result?.Data == true)
                    {
                        hasPermission = true;
                        break;
                    }
                }

                if (!hasPermission)
                {
                    return (Forbid(), decision);
                }
            }

            return (null, decision);
        }

        private IActionResult ToUserAccessActionResult<T>(ApiResponse<T> result)
        {
            if (result.Success)
            {
                return Ok(result);
            }

            if (string.Equals(result.ErrorCode, "USER_NOT_FOUND", StringComparison.Ordinal))
            {
                return NotFound(result);
            }

            return result.ErrorCode switch
            {
                "SELF_ACCESS_MANAGEMENT_DENIED"
                or "SELF_PROFILE_FIELDS_DENIED"
                or "HIGH_PRIVILEGE_TARGET_DENIED"
                or "USER_SCOPE_DENIED"
                or "STORE_SCOPE_DENIED"
                or "PERMISSION_ESCALATION_DENIED"
                or "ROLE_ESCALATION_DENIED"
                or "ROLE_DELEGATION_DENIED"
                or "POS_PERMISSION_DELEGATION_DENIED"
                or "EMPLOYEE_TARGET_REQUIRED"
                or "EMPLOYEE_ROLE_HAS_POS_PERMISSIONS"
                or "MANAGEABLE_STORE_GRANT_DENIED"
                or "ACCESS_DELEGATOR_DENIED"
                or "ADMIN_REQUIRED"
                or "USER_ACCESS_ACTOR_DENIED"
                or "DERIVED_STORE_MANAGER_ROLE"
                or "USER_ACCESS_ADMIN_REQUIRED"
                or "FORBIDDEN" => StatusCode(StatusCodes.Status403Forbidden, result),
                _ => Ok(result),
            };
        }

        private static string ResolveCurrentUserGuid(ClaimsPrincipal user)
        {
            return user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? user.FindFirst("userGuid")?.Value
                ?? user.FindFirst("userId")?.Value
                ?? user.FindFirst("sub")?.Value
                ?? string.Empty;
        }

        /// <summary>
        /// 从用户移除角色
        /// </summary>
        [HttpDelete("guid/{guid}/roles/{roleGuid}")]
        [Authorize(Policy = Permissions.Users.ManageRoles)]
        public async Task<IActionResult> RemoveRoleFromUser(string guid, string roleGuid)
        {
            try
            {
                var result = await _userService.RemoveRoleFromUserAsync(guid, roleGuid);
                return ToUserAccessActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "从用户移除角色失败，UserGUID: {UserGUID}, RoleGUID: {RoleGUID}",
                    guid,
                    roleGuid
                );
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 从用户移除分店
        /// </summary>
        [HttpDelete("guid/{guid}/stores/{storeGuid}")]
        [Authorize(Policy = Permissions.Users.ManageStores)]
        public async Task<IActionResult> RemoveStoreFromUser(string guid, string storeGuid)
        {
            try
            {
                var result = await _userService.RemoveStoreFromUserAsync(guid, storeGuid);
                return ToUserAccessActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "从用户移除分店失败，UserGUID: {UserGUID}, StoreGUID: {StoreGUID}",
                    guid,
                    storeGuid
                );
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 批量管理用户
        /// </summary>
        [HttpPost("batch")]
        [Authorize(Policy = Permissions.Users.Edit)]
        public async Task<IActionResult> BatchManageUsers([FromBody] BatchUserOperationDto dto)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(
                        ApiResponse<bool>.Error("请求参数验证失败", "VALIDATION_ERROR", ModelState)
                    );
                }

                if (string.Equals(dto.Operation, "delete", StringComparison.OrdinalIgnoreCase))
                {
                    var actorGuid = ResolveCurrentUserGuid(User);
                    var canDelete = await _roleService.UserHasPermissionAsync(
                        actorGuid,
                        Permissions.Users.Delete
                    );
                    if (canDelete.Data != true)
                    {
                        return StatusCode(
                            StatusCodes.Status403Forbidden,
                            ApiResponse<bool>.Error("无权批量删除用户", "FORBIDDEN")
                        );
                    }
                }

                var result = await _userService.BatchManageUsersAsync(dto);
                return ToUserAccessActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量管理用户失败");
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 批量为多个用户添加 / 移除分店关联；dryRun=true 时只返回影响、不写入。
        /// </summary>
        [HttpPost("batch-stores")]
        [Authorize(Policy = Permissions.Users.ManageStores)]
        public async Task<IActionResult> BatchManageUserStores(
            [FromBody] BatchUserStoreOperationDto dto
        )
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(
                        ApiResponse<BatchUserStoreOperationResultDto>.Error(
                            "请求参数验证失败",
                            "VALIDATION_ERROR",
                            ModelState
                        )
                    );
                }

                var result = await _userService.BatchManageUserStoresAsync(dto);
                return ToUserAccessActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量分店操作失败");
                return StatusCode(
                    500,
                    ApiResponse<BatchUserStoreOperationResultDto>.Error(
                        "服务器内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 导入用户
        /// </summary>
        [HttpPost("import")]
        [Authorize(Policy = Permissions.Users.Create)]
        public async Task<IActionResult> ImportUsers([FromBody] List<ImportUserDto> users)
        {
            try
            {
                var result = await _userService.ImportUsersAsync(users);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导入用户失败");
                return StatusCode(
                    500,
                    ApiResponse<ImportUserResultDto>.Error(
                        "服务器内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 导出用户
        /// </summary>
        [HttpGet("export")]
        [Authorize(Policy = Permissions.Users.View)]
        public async Task<IActionResult> ExportUsers([FromQuery] UserQueryDto query)
        {
            try
            {
                var result = await _userService.ExportUsersAsync(query);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出用户失败");
                return StatusCode(
                    500,
                    ApiResponse<List<UserDto>>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 获取用户统计信息
        /// </summary>
        [HttpGet("statistics")]
        [Authorize(Policy = Permissions.Users.View)]
        public async Task<IActionResult> GetUserStatistics()
        {
            try
            {
                var result = await _userService.GetUserStatisticsAsync();
                return ToUserAccessActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户统计失败");
                return StatusCode(
                    500,
                    ApiResponse<UserStatisticsDto>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 检查用户名是否可用
        /// </summary>
        [HttpGet("check-username/{username}")]
        // [Authorize(Roles = "Admin")] // 暂时注释掉授权
        public async Task<IActionResult> CheckUsernameAvailability(
            string username,
            [FromQuery] string? excludeUserGuid = null
        )
        {
            try
            {
                var result = await _userService.IsUsernameAvailableAsync(username, excludeUserGuid);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查用户名可用性失败，Username: {Username}", username);
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 检查邮箱是否可用
        /// </summary>
        [HttpGet("check-email/{email}")]
        // [Authorize(Roles = "Admin")] // 暂时注释掉授权
        public async Task<IActionResult> CheckEmailAvailability(
            string email,
            [FromQuery] string? excludeUserGuid = null
        )
        {
            try
            {
                var result = await _userService.IsEmailAvailableAsync(email, excludeUserGuid);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查邮箱可用性失败，Email: {Email}", email);
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 重置用户密码
        /// </summary>
        [HttpPost("guid/{guid}/reset-password")]
        [Authorize(Policy = Permissions.Users.ResetPassword)]
        public async Task<IActionResult> ResetUserPassword(string guid)
        {
            try
            {
                var result = await _userService.ResetUserPasswordAsync(guid);
                return ToUserAccessActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重置用户密码失败，UserGUID: {UserGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<string>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 锁定/解锁用户
        /// </summary>
        [HttpPut("guid/{guid}/lock")]
        [Authorize(Policy = Permissions.Users.Edit)]
        public async Task<IActionResult> LockUser(string guid, [FromBody] bool isLocked)
        {
            try
            {
                var result = await _userService.LockUserAsync(guid, isLocked);
                return ToUserAccessActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "锁定/解锁用户失败，UserGUID: {UserGUID}, IsLocked: {IsLocked}",
                    guid,
                    isLocked
                );
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }
    }
}
