using System.Security.Claims;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// 仓库商品流转分析控制器。
    /// 路由: api/react/v1/dashboard/warehouse-product-flow-analysis
    /// </summary>
    [ApiController]
    [Route("api/react/v1/dashboard/warehouse-product-flow-analysis")]
    [Authorize]
    public class WarehouseProductFlowAnalysisController : ControllerBase
    {
        private readonly IWarehouseProductFlowAnalysisService _service;
        private readonly ILogger<WarehouseProductFlowAnalysisController> _logger;
        private readonly IUserService _userService;
        private readonly IRoleService _roleService;

        public WarehouseProductFlowAnalysisController(
            IWarehouseProductFlowAnalysisService service,
            ILogger<WarehouseProductFlowAnalysisController> logger,
            IUserService userService,
            IRoleService roleService
        )
        {
            _service = service;
            _logger = logger;
            _userService = userService;
            _roleService = roleService;
        }

        private async Task<bool> HasExactWarehouseProductFlowAnalysisPermissionAsync()
        {
            var userGuid = HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrWhiteSpace(userGuid))
                return false;

            var result = await _roleService.UserHasExactPermissionAsync(
                userGuid,
                Permissions.SalesDashboard.WarehouseFlowView
            );
            return result.Success && result.Data;
        }

        /// <summary>
        /// 与商品销量分析相同的分店范围解析：全分店身份只能来自实时权限快照中的
        /// 超级管理员/仓库管理员别名，普通用户严格限 IUserService 返回的可访问分店。
        /// </summary>
        private async Task<(bool HasAccess, List<string>? BranchCodes)> ResolveWarehouseProductFlowAnalysisBranchCodesAsync()
        {
            var userGuid = HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrWhiteSpace(userGuid))
                return (false, new List<string>());

            var snapshotTask = _roleService.GetUserPermissionSnapshotAsync(userGuid);
            if (snapshotTask == null)
                return (false, new List<string>());

            var snapshotResult = await snapshotTask;
            if (snapshotResult?.Success != true || snapshotResult.Data == null)
                return (false, new List<string>());

            var roleNames = snapshotResult.Data.RoleNames ?? new List<string>();
            var hasFullStoreRole = roleNames.Any(role =>
                Permissions.SuperAdminRoleNames.Contains(role, StringComparer.OrdinalIgnoreCase)
                || Permissions.WarehouseManagerRoleNames.Contains(role, StringComparer.OrdinalIgnoreCase)
            );
            if (hasFullStoreRole)
                return (true, null);

            var userResult = await _userService.GetUserByGuidAsync(userGuid);
            if (userResult?.Success != true || userResult.Data == null)
                return (false, new List<string>());

            var storeCodes = NormalizeBranchCodes(
                userResult.Data.Stores?.Select(store => store.StoreCode)
            );
            return (true, storeCodes);
        }

        private static List<string> NormalizeBranchCodes(IEnumerable<string>? branchCodes)
        {
            return branchCodes?
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                ?? new List<string>();
        }

        [HttpGet("options")]
        [Authorize(Policy = Permissions.SalesDashboard.WarehouseFlowView)]
        public async Task<IActionResult> GetOptions(
            [FromQuery] WarehouseProductFlowAnalysisFilterDto filter,
            [FromQuery] bool forceRefresh = false
        )
        {
            try
            {
                if (!await HasExactWarehouseProductFlowAnalysisPermissionAsync())
                    return Forbid();

                var branchScope = await ResolveWarehouseProductFlowAnalysisBranchCodesAsync();
                if (!branchScope.HasAccess)
                    return Ok(
                        ApiResponse<WarehouseProductFlowAnalysisOptionsDto>.OK(
                            new WarehouseProductFlowAnalysisOptionsDto()
                        )
                    );

                var result = await _service.GetOptionsAsync(
                    filter,
                    branchScope.BranchCodes,
                    forceRefresh
                );
                return Ok(result);
            }
            catch (WarehouseProductFlowAnalysisValidationException ex)
            {
                return BadRequest(
                    ApiResponse<WarehouseProductFlowAnalysisOptionsDto>.Error(ex.Message)
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetOptions failed");
                return StatusCode(
                    500,
                    ApiResponse<WarehouseProductFlowAnalysisOptionsDto>.Error("服务器内部错误")
                );
            }
        }

        [HttpPost("candidates")]
        [Authorize(Policy = Permissions.SalesDashboard.WarehouseFlowView)]
        public async Task<IActionResult> GetCandidates(
            [FromBody] WarehouseProductFlowCandidateRequest request
        )
        {
            try
            {
                if (!await HasExactWarehouseProductFlowAnalysisPermissionAsync())
                    return Forbid();

                // 候选是仓库商品主档，不依赖用户分店或任何业务日期。
                var result = await _service.GetCandidatesAsync(request);
                return Ok(result);
            }
            catch (WarehouseProductFlowAnalysisValidationException ex)
            {
                return BadRequest(
                    ApiResponse<
                        WarehouseProductFlowAnalysisPagedDto<WarehouseProductFlowCandidateDto>
                    >.Error(ex.Message)
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetCandidates failed");
                return StatusCode(
                    500,
                    ApiResponse<
                        WarehouseProductFlowAnalysisPagedDto<WarehouseProductFlowCandidateDto>
                    >.Error("服务器内部错误")
                );
            }
        }

        [HttpPost("summary")]
        [Authorize(Policy = Permissions.SalesDashboard.WarehouseFlowView)]
        public async Task<IActionResult> GetSummary(
            [FromBody] WarehouseProductFlowAnalysisRequest request
        )
        {
            try
            {
                if (!await HasExactWarehouseProductFlowAnalysisPermissionAsync())
                    return Forbid();

                var branchScope = await ResolveWarehouseProductFlowAnalysisBranchCodesAsync();
                if (!branchScope.HasAccess)
                    return Ok(
                        ApiResponse<WarehouseProductFlowAnalysisSummaryDto>.OK(
                            new WarehouseProductFlowAnalysisSummaryDto()
                        )
                    );

                var result = await _service.GetSummaryAsync(request, branchScope.BranchCodes);
                return Ok(result);
            }
            catch (WarehouseProductFlowAnalysisValidationException ex)
            {
                return BadRequest(
                    ApiResponse<WarehouseProductFlowAnalysisSummaryDto>.Error(ex.Message)
                );
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                // 客户端已断开：不再按 500 记错误；服务端自身超时不满足该条件，仍走下方错误日志。
                return StatusCode(499);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetSummary failed");
                return StatusCode(
                    500,
                    ApiResponse<WarehouseProductFlowAnalysisSummaryDto>.Error("服务器内部错误")
                );
            }
        }

        [HttpPost("product-daily")]
        [Authorize(Policy = Permissions.SalesDashboard.WarehouseFlowView)]
        public async Task<IActionResult> GetProductDaily(
            [FromBody] WarehouseProductFlowAnalysisRequest request
        )
        {
            return await GetListAsync(
                request,
                _service.GetProductDailyAsync,
                new List<WarehouseProductFlowDailyDto>(),
                "GetProductDaily"
            );
        }

        [HttpPost("order-shipment-daily")]
        [Authorize(Policy = Permissions.SalesDashboard.WarehouseFlowView)]
        public async Task<IActionResult> GetOrderShipmentDaily(
            [FromBody] WarehouseProductFlowAnalysisRequest request
        )
        {
            return await GetListAsync(
                request,
                _service.GetOrderShipmentDailyAsync,
                new List<WarehouseProductFlowDailyDto>(),
                "GetOrderShipmentDaily"
            );
        }

        [HttpPost("sales-daily")]
        [Authorize(Policy = Permissions.SalesDashboard.WarehouseFlowView)]
        public async Task<IActionResult> GetSalesDaily(
            [FromBody] WarehouseProductFlowAnalysisRequest request
        )
        {
            return await GetListAsync(
                request,
                _service.GetSalesDailyAsync,
                new List<WarehouseProductFlowDailyDto>(),
                "GetSalesDaily"
            );
        }

        [HttpPost("containers")]
        [Authorize(Policy = Permissions.SalesDashboard.WarehouseFlowView)]
        public async Task<IActionResult> GetContainers(
            [FromBody] WarehouseProductFlowAnalysisRequest request
        )
        {
            return await GetListAsync(
                request,
                _service.GetContainersAsync,
                new List<WarehouseProductFlowContainerDto>(),
                "GetContainers"
            );
        }

        [HttpPost("orders")]
        [Authorize(Policy = Permissions.SalesDashboard.WarehouseFlowView)]
        public async Task<IActionResult> GetOrders(
            [FromBody] WarehouseProductFlowAnalysisRequest request
        )
        {
            return await GetListAsync(
                request,
                _service.GetOrdersAsync,
                new List<WarehouseProductFlowOrderDto>(),
                "GetOrders"
            );
        }

        [HttpPost("shipments")]
        [Authorize(Policy = Permissions.SalesDashboard.WarehouseFlowView)]
        public async Task<IActionResult> GetShipments(
            [FromBody] WarehouseProductFlowAnalysisRequest request
        )
        {
            return await GetListAsync(
                request,
                _service.GetShipmentsAsync,
                new List<WarehouseProductFlowShipmentDto>(),
                "GetShipments"
            );
        }

        [HttpPost("branches")]
        [Authorize(Policy = Permissions.SalesDashboard.WarehouseFlowView)]
        public async Task<IActionResult> GetBranches(
            [FromBody] WarehouseProductFlowAnalysisRequest request
        )
        {
            return await GetListAsync(
                request,
                _service.GetBranchesAsync,
                new List<WarehouseProductFlowBranchDto>(),
                "GetBranches"
            );
        }

        [HttpPost("branch-daily")]
        [Authorize(Policy = Permissions.SalesDashboard.WarehouseFlowView)]
        public async Task<IActionResult> GetBranchDaily(
            [FromBody] WarehouseProductFlowAnalysisRequest request
        )
        {
            return await GetListAsync(
                request,
                _service.GetBranchDailyAsync,
                new List<WarehouseProductFlowDailyDto>(),
                "GetBranchDaily"
            );
        }

        private async Task<IActionResult> GetListAsync<T>(
            WarehouseProductFlowAnalysisRequest request,
            Func<WarehouseProductFlowAnalysisRequest, List<string>?, Task<ApiResponse<T>>> handler,
            T emptyData,
            string actionName
        )
        {
            try
            {
                if (!await HasExactWarehouseProductFlowAnalysisPermissionAsync())
                    return Forbid();

                var branchScope = await ResolveWarehouseProductFlowAnalysisBranchCodesAsync();
                if (!branchScope.HasAccess)
                    return Ok(ApiResponse<T>.OK(emptyData));

                var result = await handler(request, branchScope.BranchCodes);
                return Ok(result);
            }
            catch (WarehouseProductFlowAnalysisValidationException ex)
            {
                return BadRequest(ApiResponse<T>.Error(ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{ActionName} failed", actionName);
                return StatusCode(500, ApiResponse<T>.Error("服务器内部错误"));
            }
        }
    }
}
