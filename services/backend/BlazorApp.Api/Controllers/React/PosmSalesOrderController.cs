using BlazorApp.Api.Data;
using BlazorApp.Api.Features.PosmSalesOrders;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace BlazorApp.Api.Controllers.React
{
    [ApiController]
    [Route("api/react/v1/posm-sales-orders")]
    [Authorize]
    public class PosmSalesOrderController : ControllerBase
    {
        private readonly IPosmSalesOrderReactService _service;
        private readonly ITaxInvoiceService _taxInvoiceService;
        private readonly SqlSugarContext _dbContext;
        private readonly ILogger<PosmSalesOrderController> _logger;

        public PosmSalesOrderController(
            IPosmSalesOrderReactService service,
            ITaxInvoiceService taxInvoiceService,
            SqlSugarContext dbContext,
            ILogger<PosmSalesOrderController> logger
        )
        {
            _service = service;
            _taxInvoiceService = taxInvoiceService;
            _dbContext = dbContext;
            _logger = logger;
        }

        private bool IsAdmin()
        {
            var user = User;
            if (user == null) return false;
            return user.Claims.Any(c =>
                c.Type == ClaimTypes.Role
                && (c.Value.Equals("Admin", StringComparison.OrdinalIgnoreCase)
                    || c.Value.Equals("WarehouseManager", StringComparison.OrdinalIgnoreCase))
            );
        }

        private string GetCurrentUserGuid()
        {
            return User?.FindFirst("userId")?.Value
                ?? User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? string.Empty;
        }

        private async Task<List<string>> GetCurrentUserStoreCodesAsync()
        {
            var result = new List<string>();
            var userGuid = GetCurrentUserGuid();
            if (string.IsNullOrEmpty(userGuid))
                return result;

            var storeGuids = await _dbContext.Db.Queryable<UserStore>()
                .Where(us => us.UserGUID == userGuid)
                .Select(us => us.StoreGUID)
                .ToListAsync();

            if (!storeGuids.Any())
                return result;

            var codes = await _dbContext.Db.Queryable<Store>()
                .Where(s => storeGuids.Contains(s.StoreGUID))
                .Select(s => s.StoreCode)
                .ToListAsync();

            result.AddRange(codes.Where(c => !string.IsNullOrEmpty(c)));
            return result;
        }

        // Web 收银记录页沿用 Orders.View（历史决策：移动端销售订单查询独立使用 SalesOrders.View）。
        [HttpPost("list")]
        [Authorize(Policy = Permissions.Orders.View)]
        public async Task<IActionResult> GetSalesOrderList([FromBody] PosmSalesOrderQueryParams queryParams)
        {
            try
            {
                if (!IsAdmin())
                {
                    var userStoreCodes = await GetCurrentUserStoreCodesAsync();
                    if (userStoreCodes.Any())
                    {
                        if (!string.IsNullOrWhiteSpace(queryParams.BranchCode))
                        {
                            if (!userStoreCodes.Contains(queryParams.BranchCode))
                            {
                                return Ok(new { success = true, data = new PosmSalesOrderListResultDto { Items = new List<PosmSalesOrderDto>(), Total = 0, PageNumber = queryParams.PageNumber, PageSize = queryParams.PageSize } });
                            }
                        }
                        else
                        {
                            queryParams.BranchCodes = userStoreCodes;
                        }
                    }
                }

                // 分店范围确定后再校验：授权范围只有一家分店时，件数/种数条件同样允许 92 天。
                if (!PosmSalesOrderListRules.TryValidateWebQuery(queryParams, out var error, out var errorCode))
                {
                    return BadRequest(new { success = false, message = error, errorCode });
                }

                var result = await _service.GetSalesOrderListAsync(queryParams);
                return Ok(new { success = true, data = result });
            }
            catch (PosmSalesOrderQueryRejectedException ex)
            {
                return BadRequest(new { success = false, message = ex.Message, errorCode = ex.ErrorCode });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetSalesOrderList failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        [HttpGet("detail/{orderGuid}")]
        [Authorize(Policy = Permissions.Orders.View)]
        public async Task<IActionResult> GetSalesOrderDetail(string orderGuid)
        {
            try
            {
                var result = await _service.GetSalesOrderDetailAsync(orderGuid);

                if (result.Success && result.Data?.Order != null && !IsAdmin())
                {
                    var userStoreCodes = await GetCurrentUserStoreCodesAsync();
                    if (userStoreCodes.Any() && !string.IsNullOrEmpty(result.Data.Order.BranchCode))
                    {
                        if (!userStoreCodes.Contains(result.Data.Order.BranchCode))
                        {
                            return Forbid();
                        }
                    }
                }

                return Ok(new
                {
                    success = result.Success,
                    data = result.Data,
                    message = result.Message,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetSalesOrderDetail failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 与既有列表 / 详情 / 发票接口相同的分店范围口径：
        /// 管理员或未分配分店的账号视为全分店，其余账号严格限授权分店。
        /// 移动端单独收口在这里，保证列表、分店清单与详情三者可见范围一致。
        /// </summary>
        private async Task<(string Scope, List<string>? BranchCodes)> ResolveMobileBranchScopeAsync()
        {
            if (IsAdmin())
            {
                return (PosmSalesOrderMobileRules.ScopeAllStores, null);
            }

            var storeCodes = await GetCurrentUserStoreCodesAsync();
            return storeCodes.Count == 0
                ? (PosmSalesOrderMobileRules.ScopeAllStores, null)
                : (PosmSalesOrderMobileRules.ScopeAuthorizedStores, storeCodes);
        }

        /// <summary>移动端销售订单列表：区间必填且不超过 30 天，按下单时间排序，关键词命中的商品随单返回。</summary>
        [HttpPost("mobile-list")]
        [Authorize(Policy = Permissions.SalesOrders.View)]
        public async Task<IActionResult> GetMobileSalesOrderList(
            [FromBody] PosmSalesOrderMobileQueryDto query
        )
        {
            try
            {
                if (
                    !PosmSalesOrderMobileRules.TryResolveRange(
                        query.StartDate,
                        query.EndDate,
                        out var range,
                        out var error,
                        out var errorCode
                    )
                )
                {
                    return BadRequest(ApiResponse<PosmSalesOrderMobileListDto>.Error(error!, errorCode));
                }

                var scope = await ResolveMobileBranchScopeAsync();
                var pageNumber = PosmSalesOrderMobileRules.NormalizePageNumber(query.PageNumber);
                var pageSize = PosmSalesOrderMobileRules.NormalizePageSize(query.PageSize);
                var sortDirection = PosmSalesOrderMobileRules.NormalizeSortDirection(query.SortDirection);
                var response = new PosmSalesOrderMobileListDto
                {
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                    Scope = scope.Scope,
                    SortDirection = sortDirection,
                    Range = new PosmSalesOrderMobileRangeDto
                    {
                        StartDate = PosmSalesOrderMobileRules.FormatDate(range.StartDate),
                        EndDate = PosmSalesOrderMobileRules.FormatDate(range.EndDate),
                        DayCount = PosmSalesOrderMobileRules.CountDays(range.StartDate, range.EndDate),
                    },
                };

                var branchCodes = PosmSalesOrderMobileRules.ResolveEffectiveBranchCodes(
                    query.BranchCodes,
                    scope.BranchCodes
                );
                if (branchCodes is { Count: 0 })
                {
                    // 请求的分店全部不在授权范围内：返回空结果，而不是退回到授权范围放大查询。
                    return Ok(ApiResponse<PosmSalesOrderMobileListDto>.OK(response));
                }

                var keyword = query.Keyword?.Trim();
                var result = await _service.GetSalesOrderListAsync(
                    new PosmSalesOrderQueryParams
                    {
                        StartDate = range.StartDate,
                        EndDate = range.EndDate,
                        BranchCodes = branchCodes,
                        OrderType = PosmSalesOrderMobileRules.NormalizeOrderType(query.OrderType),
                        RefundFilter = PosmSalesOrderListRules.NormalizeRefundFilter(query.RefundFilter),
                        Keyword = string.IsNullOrEmpty(keyword) ? null : keyword,
                        SortField = "orderTime",
                        SortDirection = sortDirection,
                        PageNumber = pageNumber,
                        PageSize = pageSize,
                    }
                );

                // 列表查询已按同一套关键词口径为当前页算出命中商品，不再二次解析关键词。
                response.Total = result.Total;
                response.Items = result
                    .Items.Select(item => PosmSalesOrderMobileItemDto.From(item, item.MatchedProducts))
                    .ToList();
                return Ok(ApiResponse<PosmSalesOrderMobileListDto>.OK(response));
            }
            catch (PosmSalesOrderQueryRejectedException ex)
            {
                return BadRequest(ApiResponse<PosmSalesOrderMobileListDto>.Error(ex.Message, ex.ErrorCode));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetMobileSalesOrderList failed");
                return StatusCode(
                    500,
                    ApiResponse<PosmSalesOrderMobileListDto>.Error("服务器内部错误", "QUERY_ERROR")
                );
            }
        }

        /// <summary>移动端筛选用的分店清单：全分店账号返回全部未删除门店，其余只返回授权分店。</summary>
        [HttpGet("mobile-branches")]
        [Authorize(Policy = Permissions.SalesOrders.View)]
        public async Task<IActionResult> GetMobileBranches()
        {
            try
            {
                var scope = await ResolveMobileBranchScopeAsync();
                var storeQuery = _dbContext.Db.Queryable<Store>().Where(s => !s.IsDeleted);
                if (scope.BranchCodes != null)
                {
                    var authorized = scope.BranchCodes;
                    storeQuery = storeQuery.Where(s => authorized.Contains(s.StoreCode));
                }

                var stores = await storeQuery
                    .OrderBy(s => s.StoreCode)
                    .Select(s => new PosmSalesOrderBranchDto
                    {
                        StoreCode = s.StoreCode,
                        StoreName = s.StoreName,
                    })
                    .ToListAsync();

                return Ok(
                    ApiResponse<PosmSalesOrderMobileBranchesDto>.OK(
                        new PosmSalesOrderMobileBranchesDto { Scope = scope.Scope, Branches = stores }
                    )
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetMobileBranches failed");
                return StatusCode(
                    500,
                    ApiResponse<PosmSalesOrderMobileBranchesDto>.Error("服务器内部错误", "QUERY_ERROR")
                );
            }
        }

        [HttpGet("tax-invoice/{orderGuid}")]
        [Authorize(Policy = Permissions.Orders.View)]
        public async Task<IActionResult> GetTaxInvoicePdf(string orderGuid)
        {
            try
            {
                if (!IsAdmin())
                {
                    var userStoreCodes = await GetCurrentUserStoreCodesAsync();
                    if (userStoreCodes.Any())
                    {
                        var result = await _service.GetSalesOrderDetailAsync(orderGuid);
                        if (result.Success && result.Data?.Order != null
                            && !string.IsNullOrEmpty(result.Data.Order.BranchCode)
                            && !userStoreCodes.Contains(result.Data.Order.BranchCode))
                        {
                            return Forbid();
                        }
                    }
                }

                var pdfBytes = await _taxInvoiceService.GenerateTaxInvoicePdfAsync(orderGuid);
                return File(pdfBytes, "application/pdf", $"TaxInvoice_{orderGuid}.pdf");
            }
            catch (ArgumentException ex)
            {
                _logger.LogError(ex, "GetTaxInvoicePdf failed: {Message}", ex.Message);
                return NotFound(new { success = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetTaxInvoicePdf failed");
                return StatusCode(500, new { success = false, message = "生成PDF失败" });
            }
        }
    }
}
