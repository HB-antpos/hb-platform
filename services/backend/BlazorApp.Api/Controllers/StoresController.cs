using BlazorApp.Api.Filters;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers
{
    /// <summary>
    /// 分店管理控制器
    /// 🏢 提供分店数据的CRUD操作和同步功能
    /// 🔐 包含完整的授权控制，确保只有有权限的用户才能访问相应功能
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize] // 🔐 启用全局授权，所有端点都需要认证
    // 全局授权：控制器级别的[Authorize]会应用到所有Action方法
    // 如果启用，所有端点都需要认证，除非单独标记[AllowAnonymous]
    public class StoresController : ControllerBase
    {
        private readonly IStoreService _storeService;
        private readonly StoreSyncService _syncService;
        private readonly IAuthorizationService _authorizationService;
        private readonly ILogger<StoresController> _logger;

        public StoresController(
            IStoreService storeService,
            StoreSyncService syncService,
            IAuthorizationService authorizationService,
            ILogger<StoresController> logger
        )
        {
            _storeService = storeService;
            _syncService = syncService;
            _authorizationService = authorizationService;
            _logger = logger;
        }

        /// <summary>
        /// 获取所有未删除的分店列表（按名称排序）
        /// </summary>
        /// <returns>分店列表</returns>
        [HttpGet("all-by-name")]
        public async Task<IActionResult> GetAllStoresByName()
        {
            try
            {
                if (!await CanReadAllStoresByNameAsync())
                {
                    return Forbid();
                }

                var result = await _storeService.GetAllStoresByNameAsync();
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取所有分店列表失败");
                return StatusCode(
                    500,
                    ApiResponse<List<StoreDto>>.Error("获取所有分店列表失败", "INTERNAL_ERROR")
                );
            }
        }

        private async Task<bool> CanReadAllStoresByNameAsync()
        {
            // 关键逻辑：Orders.Create 不是通用全分店读取权限，只给纯仓库员工代建购物车选店放行。
            if (await HasPermissionAsync(Permissions.Stores.View))
            {
                return true;
            }

            if (
                await HasPermissionAsync(Permissions.DeviceRegistration.View)
                || await HasPermissionAsync(Permissions.DeviceRegistration.Manage)
            )
            {
                return true;
            }

            return IsWarehouseStaffOnly() && await HasPermissionAsync(Permissions.Orders.Create);
        }

        private async Task<bool> HasPermissionAsync(string permission)
        {
            return (await _authorizationService.AuthorizeAsync(User, null, permission)).Succeeded;
        }

        private bool IsWarehouseStaffOnly()
        {
            return HasAnyRole("WarehouseStaff", "仓库员工")
                && !HasAnyRole("Admin", "管理员")
                && !HasAnyRole("WarehouseManager", "仓库经理");
        }

        private bool HasAnyRole(params string[] roles)
        {
            return roles.Any(User.IsInRole);
        }

        /// <summary>
        /// 获取所有激活的分店列表（用于数据同步分店选择）
        /// 🏢 返回所有状态为激活的分店信息，主要用于数据同步时的分店选择
        /// </summary>
        /// <returns>激活的分店列表</returns>
        [HttpGet("active")]
        [Authorize(Policy = Permissions.Stores.View)]
        public async Task<IActionResult> GetActiveStores()
        {
            try
            {
                var result = await _storeService.GetActiveStoresAsync();
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取激活分店列表失败");
                return StatusCode(
                    500,
                    ApiResponse<List<StoreDto>>.Error("获取激活分店列表失败", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 获取分店列表
        /// 📋 支持分页、搜索、筛选的分店数据查询
        /// </summary>
        /// <param name="query">查询参数（分页、搜索条件等）</param>
        /// <returns>分页的分店数据</returns>
        [HttpGet]
        [Authorize(Policy = Permissions.Stores.View)]
        public async Task<IActionResult> GetStores([FromQuery] StoreQueryDto query)
        {
            try
            {
                var result = await _storeService.GetStoresAsync(query);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取分店列表失败");
                return StatusCode(
                    500,
                    ApiResponse<PagedResult<StoreDto>>.Error(
                        "服务器内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 根据GUID获取分店详情
        /// </summary>
        [HttpGet("guid/{guid}")]
        [Authorize(Policy = Permissions.Stores.View)]
        public async Task<IActionResult> GetStoreByGuid(string guid)
        {
            try
            {
                var result = await _storeService.GetStoreByGuidAsync(guid);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取分店详情失败，GUID: {StoreGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<StoreDetailDto>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 获取新建分店时建议使用的下一个编码
        /// </summary>
        [HttpGet("next-code")]
        [Authorize(Policy = Permissions.Stores.Create)]
        public async Task<IActionResult> GetNextStoreCode()
        {
            try
            {
                var result = await _storeService.GetNextStoreCodeAsync();
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取下一个分店编码失败");
                return StatusCode(
                    500,
                    ApiResponse<string>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 创建新分店
        /// ➕ 只有Admin角色才能创建分店，确保数据安全
        /// </summary>
        /// <param name="dto">创建分店的数据传输对象</param>
        /// <returns>创建结果</returns>
        [HttpPost]
        [Authorize(Policy = Permissions.Stores.Create)]
        public async Task<IActionResult> CreateStore([FromBody] CreateStoreDto dto)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(
                        ApiResponse<StoreDto>.Error(
                            "请求参数验证失败",
                            "VALIDATION_ERROR",
                            ModelState
                        )
                    );
                }

                var result = await _storeService.CreateStoreAsync(dto);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建分店失败");
                return StatusCode(
                    500,
                    ApiResponse<StoreDto>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 根据GUID更新分店
        /// </summary>
        [HttpPut("guid/{guid}")]
        [Authorize(Policy = Permissions.Stores.Edit)]
        public async Task<IActionResult> UpdateStoreByGuid(
            string guid,
            [FromBody] UpdateStoreDto dto
        )
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(
                        ApiResponse<StoreDto>.Error(
                            "请求参数验证失败",
                            "VALIDATION_ERROR",
                            ModelState
                        )
                    );
                }

                var result = await _storeService.UpdateStoreByGuidAsync(guid, dto);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新分店失败，GUID: {StoreGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<StoreDto>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 原子批量修改分店的指定字段，不触发HQ同步。
        /// </summary>
        [HttpPatch("batch")]
        [Authorize(Policy = Permissions.Stores.Edit)]
        public async Task<IActionResult> BatchUpdateStores([FromBody] BatchUpdateStoresDto dto)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    var response = ApiResponse<BatchUpdateStoresResultDto>.Error(
                        "请求参数验证失败",
                        "VALIDATION_ERROR",
                        ModelState
                    );
                    response.Data = new BatchUpdateStoresResultDto
                    {
                        RequestedCount = dto?.StoreGuids?.Count ?? 0,
                        UpdatedCount = 0,
                    };
                    return BadRequest(response);
                }

                var result = await _storeService.BatchUpdateStoresAsync(dto);
                if (result.Success)
                {
                    return Ok(result);
                }

                return result.ErrorCode switch
                {
                    "STORE_BATCH_TARGET_INVALID" or "STORE_BATCH_CONFLICT" => Conflict(result),
                    "BATCH_UPDATE_STORES_ERROR" => StatusCode(
                        StatusCodes.Status500InternalServerError,
                        result
                    ),
                    _ => BadRequest(result),
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量修改分店失败");
                var response = ApiResponse<BatchUpdateStoresResultDto>.Error(
                    "服务器内部错误",
                    "INTERNAL_SERVER_ERROR"
                );
                response.Data = new BatchUpdateStoresResultDto
                {
                    RequestedCount = dto?.StoreGuids?.Count ?? 0,
                    UpdatedCount = 0,
                };
                return StatusCode(StatusCodes.Status500InternalServerError, response);
            }
        }

        /// <summary>
        /// 根据GUID删除分店
        /// </summary>
        [HttpDelete("guid/{guid}")]
        [Authorize(Policy = Permissions.Stores.Delete)]
        public async Task<IActionResult> DeleteStoreByGuid(string guid)
        {
            try
            {
                var result = await _storeService.DeleteStoreByGuidAsync(guid);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除分店失败，GUID: {StoreGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 根据GUID更新分店状态
        /// </summary>
        [HttpPut("guid/{guid}/status")]
        [Authorize(Policy = Permissions.Stores.Edit)]
        public async Task<IActionResult> UpdateStoreStatusByGuid(
            string guid,
            [FromBody] UpdateStoreStatusDto dto
        )
        {
            try
            {
                var result = await _storeService.UpdateStoreStatusByGuidAsync(guid, dto.IsActive);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新分店状态失败，GUID: {StoreGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 将当前分店资料同步到HQ分店信息表
        /// </summary>
        [HttpPost("guid/{guid}/sync-hq")]
        [Authorize(Policy = Permissions.Stores.Edit)]
        public async Task<IActionResult> SyncStoreToHq(string guid)
        {
            try
            {
                var result = await _storeService.SyncStoreToHqAsync(guid);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "同步分店到HQ失败，GUID: {StoreGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 获取分店用户列表
        /// </summary>
        [HttpGet("guid/{guid}/users")]
        [Authorize(Policy = Permissions.Stores.View)]
        public async Task<IActionResult> GetStoreUsers(string guid, [FromQuery] UserQueryDto query)
        {
            try
            {
                var result = await _storeService.GetStoreUsersAsync(guid, query);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取分店用户列表失败，StoreGUID: {StoreGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<PagedResult<StoreUserDto>>.Error(
                        "服务器内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 为分店添加用户
        /// </summary>
        [HttpPost("guid/{guid}/users")]
        [Authorize(Policy = Permissions.Stores.Edit)]
        public async Task<IActionResult> AddUserToStore(
            string guid,
            [FromBody] AddUserToStoreDto dto
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

                var result = await _storeService.AddUserToStoreAsync(guid, dto);
                return ToStoreUserMutationActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "为分店添加用户失败，StoreGUID: {StoreGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 从分店移除用户
        /// </summary>
        [HttpDelete("guid/{guid}/users/{userGuid}")]
        [Authorize(Policy = Permissions.Stores.Edit)]
        public async Task<IActionResult> RemoveUserFromStore(string guid, string userGuid)
        {
            try
            {
                var result = await _storeService.RemoveUserFromStoreAsync(guid, userGuid);
                return ToStoreUserMutationActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "从分店移除用户失败，StoreGUID: {StoreGUID}, UserGUID: {UserGUID}",
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
        /// 设置用户是否可管理该分店
        /// </summary>
        [HttpPut("guid/{guid}/users/{userGuid}/primary")]
        [Authorize(Policy = Permissions.Stores.Edit)]
        public async Task<IActionResult> SetPrimaryUser(
            string guid,
            string userGuid,
            [FromBody] bool isPrimary
        )
        {
            try
            {
                var result = await _storeService.SetPrimaryUserAsync(guid, userGuid, isPrimary);
                return ToStoreUserMutationActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "设置分店管理关系失败，StoreGUID: {StoreGUID}, UserGUID: {UserGUID}",
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
        /// 批量管理用户
        /// </summary>
        [HttpPost("guid/{guid}/users/batch")]
        [Authorize(Policy = Permissions.Stores.Edit)]
        public async Task<IActionResult> BatchManageUsers(
            string guid,
            [FromBody] BatchUserOperationDto dto
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

                var result = await _storeService.BatchManageUsersAsync(guid, dto);
                return ToStoreUserMutationActionResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量管理用户失败，StoreGUID: {StoreGUID}", guid);
                return StatusCode(
                    500,
                    ApiResponse<bool>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        private IActionResult ToStoreUserMutationActionResult(ApiResponse<bool> result)
        {
            if (result.Success)
            {
                return Ok(result);
            }

            return result.ErrorCode switch
            {
                "STORE_NOT_FOUND"
                or "USER_NOT_FOUND"
                or "USER_NOT_ASSIGNED"
                or "USER_STORE_NOT_FOUND" => NotFound(result),
                "SELF_ACCESS_MANAGEMENT_DENIED"
                or "HIGH_PRIVILEGE_TARGET_DENIED"
                or "USER_SCOPE_DENIED"
                or "STORE_SCOPE_DENIED"
                or "MANAGEABLE_STORE_GRANT_DENIED"
                or "ACCESS_DELEGATOR_DENIED"
                or "ADMIN_REQUIRED"
                or "USER_ACCESS_ACTOR_DENIED"
                or "USER_ACCESS_ADMIN_REQUIRED"
                or "FORBIDDEN" => StatusCode(StatusCodes.Status403Forbidden, result),
                _ => BadRequest(result),
            };
        }

        /// <summary>
        /// 从HQ总部同步分店数据
        /// 🔄 从总部数据库获取最新分店信息并更新本地数据库
        /// 这是一个敏感操作，只有Admin角色才能执行
        /// 2026-09-29 起停用（HQ → HBweb），统一返回 410；guid/{guid}/sync-hq（本地 → HQ）保持可用。
        /// </summary>
        /// <returns>同步结果</returns>
        [HttpPost("sync")]
        [Authorize(Policy = Permissions.Stores.Sync)]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncStoresFromHq()
        {
            try
            {
                _logger.LogInformation("开始从HQ数据库同步分店数据");

                var result = await _syncService.SyncStoresFromHqAsync();

                if (result.IsSuccess)
                {
                    _logger.LogInformation("分店数据同步成功: {Message}", result.Message);
                    return Ok(ApiResponse<SyncResult>.OK(result, result.Message));
                }
                else
                {
                    _logger.LogWarning("分店数据同步失败: {Message}", result.Message);
                    return BadRequest(
                        ApiResponse<SyncResult>.Error(result.Message, "SYNC_FAILED", result)
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "同步分店数据时发生异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("同步过程中发生内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 获取同步历史记录
        /// </summary>
        [HttpGet("sync/history")]
        [Authorize(Policy = Permissions.Stores.View)]
        public async Task<IActionResult> GetSyncHistory([FromQuery] int pageSize = 10)
        {
            try
            {
                var history = await _syncService.GetSyncHistoryAsync(pageSize);
                return Ok(ApiResponse<List<SyncHistory>>.OK(history));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取同步历史失败");
                return StatusCode(
                    500,
                    ApiResponse<List<SyncHistory>>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }
    }
}
