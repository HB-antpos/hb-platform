using System.Security.Claims;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    [ApiController]
    [Route("api/react/v1/local-supplier-invoices")]
    // 类级只要求登录：按单据分析的 action 仍声明 LocalPurchase.View 策略；
    // 后台进货销量分析三个 action 在方法内校验「销售看板新权限码 或 LocalPurchase.View」；
    // 订货前台 shop/* action 在方法内校验订货前台权限，前后台权限互不放开。
    [Authorize]
    public class ReactLocalSupplierInvoiceSalesAnalysisController : ControllerBase
    {
        private readonly ILocalSupplierInvoiceSalesAnalysisService _service;
        private readonly IUserService _userService;
        private readonly SqlSugarContext _dbContext;
        private readonly ILogger<ReactLocalSupplierInvoiceSalesAnalysisController> _logger;
        private readonly IAuthorizationService? _authorizationService;

        // 后台分析页已挪到「销售看板」：新逐页权限码或原 LocalPurchase.View 任一即可访问。
        private static readonly string[] PurchaseSalesAnalysisReadPermissions =
        {
            Permissions.SalesDashboard.LocalSupplierPurchaseSalesView,
            Permissions.LocalPurchase.View,
        };

        // 前台只读接口只认订货前台权限，不放开后台 LocalPurchase.View。
        private static readonly string[] ShopPurchaseSalesAnalysisReadPermissions =
        {
            Permissions.OrderFront.View,
        };

        public ReactLocalSupplierInvoiceSalesAnalysisController(
            ILocalSupplierInvoiceSalesAnalysisService service,
            IUserService userService,
            SqlSugarContext dbContext,
            ILogger<ReactLocalSupplierInvoiceSalesAnalysisController> logger,
            // 可选参数：保持既有构造调用（含测试）不被破坏；缺失时前台接口一律拒绝。
            IAuthorizationService? authorizationService = null
        )
        {
            _authorizationService = authorizationService;
            _service = service;
            _userService = userService;
            _dbContext = dbContext;
            _logger = logger;
        }

        [Authorize(Policy = Permissions.LocalPurchase.View)]
        [HttpGet("{invoiceGuid}/sales-analysis")]
        public async Task<IActionResult> GetSalesAnalysis(string invoiceGuid)
        {
            var storeScope = await ResolveStoreScopeAsync(null, requireStoreSelectionWhenMissing: false);
            if (storeScope.Forbidden)
            {
                return Forbid();
            }

            if (!await CanAccessInvoiceAsync(invoiceGuid, storeScope))
            {
                return Forbid();
            }

            var result = await _service.GetAnalysisAsync(invoiceGuid);
            if (result.Success)
            {
                return Ok(
                    new
                    {
                        success = true,
                        data = result.Data,
                        message = result.Message,
                    }
                );
            }

            return NotFound(new { success = false, message = result.Message });
        }

        // 授权在方法内校验：销售看板新权限码或 LocalPurchase.View 任一即可。
        [HttpGet("purchase-sales-analysis")]
        public async Task<IActionResult> GetPurchaseSalesAnalysis(
            [FromQuery] LocalSupplierPurchaseSalesAnalysisQueryDto query
        )
        {
            try
            {
                if (!await HasPurchaseSalesAnalysisReadPermissionAsync())
                {
                    return Forbid();
                }

                if (string.IsNullOrWhiteSpace(query.StoreCode))
                {
                    return BadRequest(
                        ApiResponse<LocalSupplierPurchaseSalesAnalysisResponseDto>.Error(
                            "请先选择分店。",
                            "VALIDATION_ERROR"
                        )
                    );
                }

                if (string.IsNullOrWhiteSpace(query.SupplierCode))
                {
                    return BadRequest(
                        ApiResponse<LocalSupplierPurchaseSalesAnalysisResponseDto>.Error(
                            "请先选择供应商。",
                            "VALIDATION_ERROR"
                        )
                    );
                }

                var storeScope = await ResolveStoreScopeAsync(query.StoreCode);
                if (storeScope.Forbidden)
                {
                    return Forbid();
                }

                if (storeScope.RequiresStoreSelection)
                {
                    return BadRequest(
                        ApiResponse<LocalSupplierPurchaseSalesAnalysisResponseDto>.Error(
                            "当前账号关联多个门店，请先选择一个门店。",
                            "STORE_REQUIRED"
                        )
                    );
                }

                query.StoreCode = storeScope.SelectedStoreCode ?? query.StoreCode;
                var result = await _service.GetPurchaseSalesAnalysisAsync(
                    query,
                    storeScope.ScopedStoreCodes
                );

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
            catch (Exception ex)
            {
                _logger.LogError(ex, "分店供应商进货销量分析查询失败");
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponse<LocalSupplierPurchaseSalesAnalysisResponseDto>.Error(
                        "分店供应商进货销量分析查询失败"
                    )
                );
            }
        }

        // 授权在方法内校验：销售看板新权限码或 LocalPurchase.View 任一即可。
        [HttpGet("purchase-sales-analysis/store-options")]
        public async Task<IActionResult> GetPurchaseSalesAnalysisStoreOptions()
        {
            try
            {
                if (!await HasPurchaseSalesAnalysisReadPermissionAsync())
                {
                    return Forbid();
                }

                var storeScope = await ResolveStoreScopeAsync(null, requireStoreSelectionWhenMissing: false);
                if (storeScope.Forbidden)
                {
                    return Forbid();
                }

                var result = await _service.GetStoreOptionsAsync(storeScope.ScopedStoreCodes);
                return Ok(ApiResponse<List<LocalSupplierPurchaseSalesAnalysisStoreOptionDto>>.OK(result));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "分店供应商进货销量分析分店选项加载失败");
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponse<List<LocalSupplierPurchaseSalesAnalysisStoreOptionDto>>.Error(
                        "分店供应商进货销量分析分店选项加载失败"
                    )
                );
            }
        }

        // 授权在方法内校验：销售看板新权限码或 LocalPurchase.View 任一即可。
        [HttpGet("purchase-sales-analysis/supplier-options")]
        public async Task<IActionResult> GetPurchaseSalesAnalysisSupplierOptions(
            [FromQuery] string? storeCode
        )
        {
            try
            {
                if (!await HasPurchaseSalesAnalysisReadPermissionAsync())
                {
                    return Forbid();
                }

                // 供应商候选跟随门店权限收口，避免普通用户看到无权门店的进货供应商。
                var storeScope = await ResolveStoreScopeAsync(
                    storeCode,
                    requireStoreSelectionWhenMissing: false
                );
                if (storeScope.Forbidden)
                {
                    return Forbid();
                }

                var result = await _service.GetSupplierOptionsAsync(
                    storeScope.ScopedStoreCodes,
                    storeScope.SelectedStoreCode ?? storeCode
                );
                return Ok(
                    ApiResponse<List<LocalSupplierPurchaseSalesAnalysisSupplierOptionDto>>.OK(result)
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "分店供应商进货销量分析供应商选项加载失败");
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponse<List<LocalSupplierPurchaseSalesAnalysisSupplierOptionDto>>.Error(
                        "分店供应商进货销量分析供应商选项加载失败"
                    )
                );
            }
        }

        [HttpGet("purchase-sales-analysis/category-tree")]
        public async Task<IActionResult> GetPurchaseSalesAnalysisCategoryTree(
            [FromQuery] string? supplierCode,
            [FromQuery] string? storeCode)
        {
            if (!await HasPurchaseSalesAnalysisReadPermissionAsync()) return Forbid();
            if (string.IsNullOrWhiteSpace(supplierCode) || supplierCode.Trim().Length > 64)
                return BadRequest(ApiResponse<List<LocalSupplierCategoryNodeDto>>.Error("供应商代码无效。", "VALIDATION_ERROR"));
            try
            {
                // 分类选项与页面供应商下拉共用分店范围，不能仅凭供应商代码枚举其他门店的分类。
                var storeScope = await ResolveStoreScopeAsync(storeCode, requireStoreSelectionWhenMissing: false);
                if (storeScope.Forbidden) return Forbid();
                var visibleSuppliers = await _service.GetSupplierOptionsAsync(
                    storeScope.ScopedStoreCodes,
                    storeScope.SelectedStoreCode ?? storeCode);
                if (!visibleSuppliers.Any(option => string.Equals(option.Value, supplierCode.Trim(), StringComparison.OrdinalIgnoreCase)))
                    return Forbid();
                var data = await _service.GetPurchaseSalesAnalysisCategoryTreeAsync(supplierCode);
                return Ok(ApiResponse<List<LocalSupplierCategoryNodeDto>>.OK(data));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "分店供应商进货销量分析分类树加载失败 SupplierCode={SupplierCode}", supplierCode);
                return StatusCode(500, ApiResponse<List<LocalSupplierCategoryNodeDto>>.Error("分类树加载失败。", "QUERY_ERROR"));
            }
        }

        /// <summary>
        /// 订货前台：分店供应商进货销量分析（只读）。
        /// 只认订货前台权限（OrderFront.View，或纯仓库员工 + Orders.Create），不放开后台 LocalPurchase.View；
        /// 门店范围沿用 ResolveStoreScopeAsync，非管理员只能查本人名下门店。
        /// </summary>
        [HttpGet("shop/purchase-sales-analysis")]
        public async Task<IActionResult> GetShopPurchaseSalesAnalysis(
            [FromQuery] LocalSupplierPurchaseSalesAnalysisQueryDto query
        )
        {
            try
            {
                if (!await HasShopPurchaseSalesAnalysisReadPermissionAsync())
                {
                    return Forbid();
                }

                if (string.IsNullOrWhiteSpace(query.StoreCode))
                {
                    return BadRequest(
                        ApiResponse<LocalSupplierPurchaseSalesAnalysisResponseDto>.Error(
                            "请先选择分店。",
                            "VALIDATION_ERROR"
                        )
                    );
                }

                if (string.IsNullOrWhiteSpace(query.SupplierCode))
                {
                    return BadRequest(
                        ApiResponse<LocalSupplierPurchaseSalesAnalysisResponseDto>.Error(
                            "请先选择供应商。",
                            "VALIDATION_ERROR"
                        )
                    );
                }

                var storeScope = await ResolveStoreScopeAsync(query.StoreCode);
                if (storeScope.Forbidden)
                {
                    return Forbid();
                }

                query.StoreCode = storeScope.SelectedStoreCode ?? query.StoreCode;
                var result = await _service.GetPurchaseSalesAnalysisAsync(
                    query,
                    storeScope.ScopedStoreCodes
                );

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
            catch (Exception ex)
            {
                _logger.LogError(ex, "订货前台分店供应商进货销量分析查询失败");
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponse<LocalSupplierPurchaseSalesAnalysisResponseDto>.Error(
                        "分店供应商进货销量分析查询失败"
                    )
                );
            }
        }

        /// <summary>
        /// 订货前台：进货销量分析的供应商候选（只读）。权限口径与前台分析接口一致，同样不放开后台 LocalPurchase.View。
        /// </summary>
        [HttpGet("shop/purchase-sales-analysis/supplier-options")]
        public async Task<IActionResult> GetShopPurchaseSalesAnalysisSupplierOptions(
            [FromQuery] string? storeCode
        )
        {
            try
            {
                if (!await HasShopPurchaseSalesAnalysisReadPermissionAsync())
                {
                    return Forbid();
                }

                // 前台必须带门店：管理员缺省门店会变成全部门店，候选范围过大且与页面语义不符。
                if (string.IsNullOrWhiteSpace(storeCode))
                {
                    return BadRequest(
                        ApiResponse<List<LocalSupplierPurchaseSalesAnalysisSupplierOptionDto>>.Error(
                            "请先选择分店。",
                            "VALIDATION_ERROR"
                        )
                    );
                }

                var storeScope = await ResolveStoreScopeAsync(
                    storeCode,
                    requireStoreSelectionWhenMissing: false
                );
                if (storeScope.Forbidden)
                {
                    return Forbid();
                }

                var result = await _service.GetSupplierOptionsAsync(
                    storeScope.ScopedStoreCodes,
                    storeScope.SelectedStoreCode ?? storeCode
                );
                return Ok(
                    ApiResponse<List<LocalSupplierPurchaseSalesAnalysisSupplierOptionDto>>.OK(result)
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "订货前台分店供应商进货销量分析供应商选项加载失败");
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponse<List<LocalSupplierPurchaseSalesAnalysisSupplierOptionDto>>.Error(
                        "分店供应商进货销量分析供应商选项加载失败"
                    )
                );
            }
        }

        /// <summary>
        /// 后台分析接口读取权限：销售看板新权限码或 LocalPurchase.View 任一通过即放行；未注入授权服务时一律拒绝。
        /// </summary>
        private async Task<bool> HasPurchaseSalesAnalysisReadPermissionAsync()
        {
            if (_authorizationService == null)
            {
                return false;
            }

            foreach (var permission in PurchaseSalesAnalysisReadPermissions)
            {
                if ((await _authorizationService.AuthorizeAsync(User, null, permission)).Succeeded)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 订货前台读取权限：与 ReactLocalSupplierInvoicesController 的前台规则对齐，
        /// 但这里刻意不接受后台 LocalPurchase.View / MobileView，前台接口只认订货前台权限。
        /// </summary>
        private async Task<bool> HasShopPurchaseSalesAnalysisReadPermissionAsync()
        {
            if (_authorizationService == null)
            {
                return false;
            }

            foreach (var permission in ShopPurchaseSalesAnalysisReadPermissions)
            {
                if ((await _authorizationService.AuthorizeAsync(User, null, permission)).Succeeded)
                {
                    return true;
                }
            }

            // 与 Web canAccessOrderFront 对齐：Orders.Create 只兼容纯仓库员工，不能扩成通用读取权限。
            return IsWarehouseStaffOnly()
                && (
                    await _authorizationService.AuthorizeAsync(
                        User,
                        null,
                        Permissions.Orders.Create
                    )
                ).Succeeded;
        }

        private bool IsWarehouseStaffOnly()
        {
            return (HasRole("WarehouseStaff") || HasRole("仓库员工")) && !IsAdminOrWarehouseManager();
        }

        private async Task<StoreScopeResult> ResolveStoreScopeAsync(
            string? requestedStoreCode,
            bool requireStoreSelectionWhenMissing = true
        )
        {
            if (IsAdminOrWarehouseManager())
            {
                return new StoreScopeResult
                {
                    SelectedStoreCode = NormalizeStoreCode(requestedStoreCode),
                    ScopedStoreCodes = string.IsNullOrWhiteSpace(requestedStoreCode)
                        ? null
                        : new[] { NormalizeStoreCode(requestedStoreCode)! },
                };
            }

            var userGuid = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrWhiteSpace(userGuid))
            {
                return new StoreScopeResult { Forbidden = true };
            }

            var userResult = await _userService.GetUserByGuidAsync(userGuid);
            var accessibleStoreCodes = userResult.Data?.Stores?
                .Select(store => NormalizeStoreCode(store.StoreCode))
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            if (accessibleStoreCodes.Count == 0)
            {
                return new StoreScopeResult { Forbidden = true };
            }

            var normalizedRequestedStore = NormalizeStoreCode(requestedStoreCode);
            if (!string.IsNullOrWhiteSpace(normalizedRequestedStore))
            {
                return accessibleStoreCodes.Contains(
                    normalizedRequestedStore,
                    StringComparer.OrdinalIgnoreCase
                )
                    ? new StoreScopeResult
                    {
                        SelectedStoreCode = normalizedRequestedStore,
                        ScopedStoreCodes = new[] { normalizedRequestedStore },
                    }
                    : new StoreScopeResult { Forbidden = true };
            }

            if (accessibleStoreCodes.Count == 1)
            {
                return new StoreScopeResult
                {
                    SelectedStoreCode = accessibleStoreCodes[0],
                    ScopedStoreCodes = accessibleStoreCodes,
                };
            }

            if (!requireStoreSelectionWhenMissing)
            {
                return new StoreScopeResult
                {
                    ScopedStoreCodes = accessibleStoreCodes,
                };
            }

            // 多门店普通用户必须显式选店，避免把多个分店的进货节奏混成一张表。
            return new StoreScopeResult
            {
                RequiresStoreSelection = true,
                ScopedStoreCodes = accessibleStoreCodes,
            };
        }

        private async Task<bool> CanAccessInvoiceAsync(string invoiceGuid, StoreScopeResult storeScope)
        {
            if (storeScope.ScopedStoreCodes == null || storeScope.ScopedStoreCodes.Count == 0)
            {
                return true;
            }

            var invoiceStoreCode = await _dbContext.Db.Queryable<StoreLocalSupplierInvoice>()
                .Where(invoice => invoice.InvoiceGUID == invoiceGuid && invoice.IsDeleted == false)
                .Select(invoice => invoice.StoreCode)
                .FirstAsync();

            return !string.IsNullOrWhiteSpace(invoiceStoreCode)
                && storeScope.ScopedStoreCodes.Contains(
                    invoiceStoreCode,
                    StringComparer.OrdinalIgnoreCase
                );
        }

        private bool IsAdminOrWarehouseManager()
        {
            return HasRole("Admin")
                || HasRole("管理员")
                || HasRole("WarehouseManager")
                || HasRole("仓库经理");
        }

        private bool HasRole(string role)
        {
            return User.Claims.Any(claim =>
                claim.Type == ClaimTypes.Role
                && claim.Value.Equals(role, StringComparison.OrdinalIgnoreCase)
            );
        }

        private static string? NormalizeStoreCode(string? storeCode)
        {
            return string.IsNullOrWhiteSpace(storeCode) ? null : storeCode.Trim();
        }

        private sealed class StoreScopeResult
        {
            public bool Forbidden { get; set; }
            public bool RequiresStoreSelection { get; set; }
            public string? SelectedStoreCode { get; set; }
            public IReadOnlyList<string>? ScopedStoreCodes { get; set; }
        }
    }
}
