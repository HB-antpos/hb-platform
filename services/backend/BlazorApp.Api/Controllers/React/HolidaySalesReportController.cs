using BlazorApp.Api.Services;
using System.Security.Claims;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Utils;
using BlazorApp.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

/// <summary>
/// 节日销售报告（万圣节）的数据入口：全部分店合计 / 单店两种视图，另有大类归并对照。
/// 只读已发布的统计与进货单，不触发任何后台重算。
/// </summary>
[ApiController]
[Route("api/react/v1/dashboard/holiday-sales-report")]
[Authorize(Policy = Permissions.SalesDashboard.HolidaySalesReportView)]
public sealed class HolidaySalesReportController : ControllerBase
{
    private readonly IHolidaySalesReportReactService _service;
    private readonly IUserService _userService;
    private readonly ILogger<HolidaySalesReportController> _logger;

    public HolidaySalesReportController(
        IHolidaySalesReportReactService service,
        IUserService userService,
        ILogger<HolidaySalesReportController> logger
    )
    {
        _service = service;
        _userService = userService;
        _logger = logger;
    }

    /// <summary>
    /// GET api/react/v1/dashboard/holiday-sales-report?year=2026&amp;branchCode=1013&amp;supplierCode=257
    /// 不传 branchCode 是授权范围内全部分店合计；传了必须在授权范围内，越权直接 403。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetReport(
        [FromQuery] int? year = null,
        [FromQuery] string? branchCode = null,
        [FromQuery] string? supplierCode = null,
        [FromQuery] bool forceRefresh = false,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var scope = await ResolveBranchScopeAsync();
            if (!scope.HasAccess)
                return Forbid();

            var branch = string.IsNullOrWhiteSpace(branchCode) ? null : branchCode.Trim();
            if (branch != null && scope.BranchCodes != null
                && !scope.BranchCodes.Contains(branch, StringComparer.OrdinalIgnoreCase))
                return Forbid();

            var result = await _service.GetReportAsync(
                year ?? DefaultYear(),
                scope.BranchCodes,
                branch,
                supplierCode,
                forceRefresh,
                cancellationToken
            );
            return Ok(new { success = true, data = result });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (Exception ex) when (ClientAbortDetector.IsClientAbort(ex, HttpContext.RequestAborted))
        {
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetHolidaySalesReport failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// GET api/react/v1/dashboard/holiday-sales-report/category?categoryKey=Costume&amp;year=2026&amp;branchCode=1013&amp;supplierCode=257
    /// 大类表里点大类钻取：该大类的合计、各分店表现与完整商品清单；传 branchCode 只看这一家店（越权 403）。
    /// </summary>
    [HttpGet("category")]
    public async Task<IActionResult> GetCategoryDetail(
        [FromQuery] string categoryKey,
        [FromQuery] int? year = null,
        [FromQuery] string? branchCode = null,
        [FromQuery] string? supplierCode = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var scope = await ResolveBranchScopeAsync();
            if (!scope.HasAccess)
                return Forbid();

            var branch = string.IsNullOrWhiteSpace(branchCode) ? null : branchCode.Trim();
            if (branch != null && scope.BranchCodes != null
                && !scope.BranchCodes.Contains(branch, StringComparer.OrdinalIgnoreCase))
                return Forbid();

            var result = await _service.GetCategoryDetailAsync(
                year ?? DefaultYear(), scope.BranchCodes, branch, supplierCode, categoryKey ?? string.Empty, cancellationToken);
            return result == null
                ? NotFound(new { success = false, message = "大类不存在" })
                : Ok(new { success = true, data = result });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (Exception ex) when (ClientAbortDetector.IsClientAbort(ex, HttpContext.RequestAborted))
        {
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetHolidaySalesCategoryDetail failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// GET api/react/v1/dashboard/holiday-sales-report/product?year=2026&amp;productCode=...
    /// 榜单里点商品钻取：这款商品在授权范围内各分店的进货、售出、卖断与剩余。
    /// </summary>
    [HttpGet("product")]
    public async Task<IActionResult> GetProductStores(
        [FromQuery] string productCode,
        [FromQuery] int? year = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var scope = await ResolveBranchScopeAsync();
            if (!scope.HasAccess)
                return Forbid();

            var result = await _service.GetProductStoresAsync(year ?? DefaultYear(), scope.BranchCodes, productCode, cancellationToken);
            return result == null
                ? NotFound(new { success = false, message = "商品不在节日商品范围内" })
                : Ok(new { success = true, data = result });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (Exception ex) when (ClientAbortDetector.IsClientAbort(ex, HttpContext.RequestAborted))
        {
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetHolidaySalesProductStores failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>GET api/react/v1/dashboard/holiday-sales-report/category-map?year=2026</summary>
    [HttpGet("category-map")]
    public async Task<IActionResult> GetCategoryMap(
        [FromQuery] int? year = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var scope = await ResolveBranchScopeAsync();
            if (!scope.HasAccess)
                return Forbid();

            var result = await _service.GetCategoryMapAsync(year ?? DefaultYear(), scope.BranchCodes, cancellationToken);
            return Ok(new { success = true, data = result });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (Exception ex) when (ClientAbortDetector.IsClientAbort(ex, HttpContext.RequestAborted))
        {
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetHolidaySalesCategoryMap failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>不传年份时：当年万圣节（10/31）还没到之前的看今年，这里只取日历年，具体合法范围由服务校验。</summary>
    private static int DefaultYear() => DateTime.UtcNow.AddHours(11).Year;

    /// <summary>
    /// 分店范围从实时用户关系解析，任何解析失败都拒绝请求，不能退化成全分店查询。
    /// 与营业额快照入口同一规则：Admin / WarehouseManager 不限分店，其他用户取全部关联分店。
    /// </summary>
    private async Task<(bool HasAccess, List<string>? BranchCodes)> ResolveBranchScopeAsync()
    {
        if (IsFullStoreRole())
            return (true, null);

        var userGuid = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(userGuid))
            return (false, new List<string>());

        var userStores = await _userService.GetUserStoresAsync(userGuid);
        if (userStores?.Success != true || userStores.Data == null)
            return (false, new List<string>());

        var allowed = userStores.Data
            .Select(store => store.StoreCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (allowed.Count > 0, allowed);
    }

    private bool IsFullStoreRole()
    {
        return User.Claims.Any(claim =>
            claim.Type == ClaimTypes.Role
            && (claim.Value.Equals("Admin", StringComparison.OrdinalIgnoreCase)
                || (!StoreScopePolicy.WarehouseManagerStoreScoped && claim.Value.Equals("WarehouseManager", StringComparison.OrdinalIgnoreCase))));
    }
}
