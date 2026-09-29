using System.Security.Claims;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    [ApiController]
    [Route("api/react/v1/local-supplier-product-sales-analysis")]
    [Authorize(Policy = Permissions.SalesDashboard.LocalProductAnalysisView)]
    public class LocalSupplierProductSalesAnalysisController : ControllerBase
    {
        private readonly ILocalSupplierProductSalesAnalysisService _service;
        private readonly IUserService _userService;
        private readonly IRoleService _roleService;
        private readonly ILogger<LocalSupplierProductSalesAnalysisController> _logger;

        public LocalSupplierProductSalesAnalysisController(
            ILocalSupplierProductSalesAnalysisService service,
            IUserService userService,
            IRoleService roleService,
            ILogger<LocalSupplierProductSalesAnalysisController> logger
        )
        {
            _service = service;
            _userService = userService;
            _roleService = roleService;
            _logger = logger;
        }

        [HttpGet("options")]
        public async Task<IActionResult> GetOptions()
        {
            try
            {
                var scope = await ResolveStoreScopeAsync();
                if (scope.Forbidden)
                {
                    return Forbid();
                }

                return ToResult(await _service.GetOptionsAsync(scope.ScopedStoreCodes));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "本地商品销量分析选项加载失败");
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponse<LocalSupplierProductSalesOptionsDto>.Error(
                        "本地商品销量分析选项加载失败"
                    )
                );
            }
        }

        [HttpGet("supplier-category-options")]
        public async Task<IActionResult> GetSupplierCategoryOptions([FromQuery] List<string>? supplierCodes, [FromQuery] bool tree = false)
        {
            try
            {
                var scope = await ResolveStoreScopeAsync();
                if (scope.Forbidden)
                {
                    return Forbid();
                }

                return ToResult(await _service.GetSupplierCategoryOptionsAsync(supplierCodes, scope.ScopedStoreCodes, tree));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "本地商品销量分析供应商分类选项加载失败");
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponse<List<LocalSupplierProductSalesSupplierCategoryOptionDto>>.Error(
                        "供应商分类选项加载失败"
                    )
                );
            }
        }

        [HttpPost("bootstrap")]
        public async Task<IActionResult> Bootstrap(
            [FromBody] LocalSupplierProductSalesAnalysisRequest request
        )
        {
            try
            {
                var scope = await ResolveStoreScopeAsync();
                if (scope.Forbidden)
                {
                    return Forbid();
                }

                var result = await _service.BootstrapAsync(request, scope.ScopedStoreCodes);
                AppendServerTiming(result.Data?.ServerTimings);
                return ToResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "本地商品销量分析 bootstrap 失败");
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponse<LocalSupplierProductSalesBootstrapResponseDto>.Error(
                        "本地商品销量分析 bootstrap 失败"
                    )
                );
            }
        }

        [HttpPost("candidates")]
        public async Task<IActionResult> Candidates(
            [FromBody] LocalSupplierProductSalesAnalysisRequest request
        )
        {
            try
            {
                var scope = await ResolveStoreScopeAsync();
                if (scope.Forbidden)
                {
                    return Forbid();
                }

                return ToResult(await _service.GetCandidatesAsync(request, scope.ScopedStoreCodes));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "本地商品候选查询失败");
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponse<
                        LocalSupplierProductSalesPagedDto<LocalSupplierProductSalesCandidateDto>
                    >.Error("本地商品候选查询失败")
                );
            }
        }

        [HttpPost("summary")]
        public async Task<IActionResult> Summary(
            [FromBody] LocalSupplierProductSalesAnalysisRequest request
        )
        {
            try
            {
                var scope = await ResolveStoreScopeAsync();
                if (scope.Forbidden)
                {
                    return Forbid();
                }

                return ToResult(await _service.GetSummaryAsync(request, scope.ScopedStoreCodes));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "本地商品销量汇总查询失败");
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponse<LocalSupplierProductSalesSummaryResponseDto>.Error(
                        "本地商品销量汇总查询失败"
                    )
                );
            }
        }

        [HttpPost("product-daily")]
        public async Task<IActionResult> ProductDaily(
            [FromBody] LocalSupplierProductSalesAnalysisRequest request
        )
        {
            try
            {
                var scope = await ResolveStoreScopeAsync();
                if (scope.Forbidden)
                {
                    return Forbid();
                }

                return ToResult(await _service.GetProductDailyAsync(request, scope.ScopedStoreCodes));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "本地商品每日趋势查询失败");
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponse<List<LocalSupplierProductSalesDailyDto>>.Error(
                        "本地商品每日趋势查询失败"
                    )
                );
            }
        }

        [HttpPost("invoice-details")]
        public async Task<IActionResult> InvoiceDetails(
            [FromBody] LocalSupplierProductSalesAnalysisRequest request
        )
        {
            try
            {
                var scope = await ResolveStoreScopeAsync();
                if (scope.Forbidden)
                {
                    return Forbid();
                }

                return ToResult(
                    await _service.GetInvoiceDetailsAsync(request, scope.ScopedStoreCodes)
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "本地商品进货明细查询失败");
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponse<LocalSupplierProductSalesInvoiceDetailPageDto>.Error(
                        "本地商品进货明细查询失败"
                    )
                );
            }
        }

        [HttpPost("branches")]
        public async Task<IActionResult> Branches(
            [FromBody] LocalSupplierProductSalesAnalysisRequest request
        )
        {
            try
            {
                var scope = await ResolveStoreScopeAsync();
                if (scope.Forbidden)
                {
                    return Forbid();
                }

                return ToResult(await _service.GetBranchesAsync(request, scope.ScopedStoreCodes));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "本地商品分店销售排行查询失败");
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponse<List<LocalSupplierProductSalesBranchDto>>.Error(
                        "本地商品分店销售排行查询失败"
                    )
                );
            }
        }

        [HttpPost("branch-daily")]
        public async Task<IActionResult> BranchDaily(
            [FromBody] LocalSupplierProductSalesAnalysisRequest request
        )
        {
            try
            {
                var scope = await ResolveStoreScopeAsync();
                if (scope.Forbidden)
                {
                    return Forbid();
                }

                return ToResult(await _service.GetBranchDailyAsync(request, scope.ScopedStoreCodes));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "本地商品分店每日趋势查询失败");
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponse<List<LocalSupplierProductSalesBranchDailyDto>>.Error(
                        "本地商品分店每日趋势查询失败"
                    )
                );
            }
        }

        private IActionResult ToResult<T>(ApiResponse<T> result)
        {
            if (result.Success)
            {
                return Ok(result);
            }

            return string.Equals(
                result.ErrorCode,
                "VALIDATION_ERROR",
                StringComparison.OrdinalIgnoreCase
            )
                ? BadRequest(result)
                : StatusCode(StatusCodes.Status500InternalServerError, result);
        }

        private void AppendServerTiming(
            IReadOnlyDictionary<string, double>? timings
        )
        {
            if (timings is null || timings.Count == 0)
            {
                return;
            }

            Response.Headers.Append(
                "Server-Timing",
                string.Join(
                    ", ",
                    timings.Select(kv => $"{kv.Key};dur={kv.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}")
                )
            );
        }

        private async Task<StoreScopeResult> ResolveStoreScopeAsync()
        {
            var userGuid = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrWhiteSpace(userGuid))
            {
                return new StoreScopeResult { Forbidden = true };
            }

            var snapshotResult = await _roleService.GetUserPermissionSnapshotAsync(userGuid);
            if (snapshotResult?.Success != true || snapshotResult.Data == null)
            {
                return new StoreScopeResult { Forbidden = true };
            }

            var roleNames = snapshotResult.Data.RoleNames ?? new List<string>();
            var hasFullStoreRole = roleNames.Any(role =>
                Permissions.SuperAdminRoleNames.Contains(role, StringComparer.OrdinalIgnoreCase)
                || Permissions.WarehouseManagerRoleNames.Contains(role, StringComparer.OrdinalIgnoreCase)
            );
            if (hasFullStoreRole)
            {
                return new StoreScopeResult { ScopedStoreCodes = null };
            }

            var userResult = await _userService.GetUserByGuidAsync(userGuid);
            var accessibleStoreCodes =
                userResult?.Data?.Stores?
                    .Select(store => NormalizeStoreCode(store.StoreCode))
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? new List<string>();

            if (accessibleStoreCodes.Count == 0)
            {
                // 零授权分店返回 200 空结果，而非 403。
                return new StoreScopeResult { ScopedStoreCodes = new List<string>() };
            }

            return new StoreScopeResult { ScopedStoreCodes = accessibleStoreCodes };
        }

        private static string? NormalizeStoreCode(string? storeCode)
        {
            return string.IsNullOrWhiteSpace(storeCode) ? null : storeCode.Trim();
        }

        private sealed class StoreScopeResult
        {
            public bool Forbidden { get; set; }
            public IReadOnlyList<string>? ScopedStoreCodes { get; set; }
        }
    }
}
