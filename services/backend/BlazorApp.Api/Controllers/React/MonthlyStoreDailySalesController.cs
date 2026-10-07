using System.Security.Claims;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Utils;
using BlazorApp.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

/// <summary>
/// 月度日销售下载页的数据入口：一次返回某个月内授权分店逐日的营业额、刷卡、现金。
/// 只读已发布的统计与 POSM 支付明细，不触发任何后台重算。
/// </summary>
[ApiController]
[Route("api/react/v1/dashboard")]
[Authorize(Policy = Permissions.SalesDashboard.MonthlyDailySalesDownloadView)]
public sealed class MonthlyStoreDailySalesController : ControllerBase
{
    private readonly IMonthlyStoreDailySalesReactService _service;
    private readonly IUserService _userService;
    private readonly ILogger<MonthlyStoreDailySalesController> _logger;

    public MonthlyStoreDailySalesController(
        IMonthlyStoreDailySalesReactService service,
        IUserService userService,
        ILogger<MonthlyStoreDailySalesController> logger
    )
    {
        _service = service;
        _userService = userService;
        _logger = logger;
    }

    /// <summary>
    /// GET api/react/v1/dashboard/monthly-store-daily-sales
    /// branchCodes 只表示授权范围的子集：普通用户与自己关联的分店取交集，越权的分店不会返回，也不会报错。
    /// </summary>
    [HttpGet("monthly-store-daily-sales")]
    public async Task<IActionResult> GetMonthlyStoreDailySales(
        [FromQuery] string month,
        [FromQuery] List<string>? branchCodes = null,
        [FromQuery] bool forceRefresh = false,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var scope = await ResolveBranchScopeAsync(branchCodes);
            if (!scope.HasAccess)
                return Forbid();

            var result = await _service.GetMonthlyStoreDailySalesAsync(
                month,
                scope.BranchCodes,
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
            // 客户端已断开：不按 500 记错误；服务端自身超时不满足该条件，仍走下方错误日志。
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetMonthlyStoreDailySales failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// 分店范围统一从实时用户关系解析，任何解析失败都拒绝请求，不能退化成全分店查询。
    /// 与营业额快照入口同一规则：Admin / WarehouseManager 不限分店，其他用户取全部关联分店（只读销售范围）。
    /// </summary>
    private async Task<(bool HasAccess, List<string>? BranchCodes)> ResolveBranchScopeAsync(
        List<string>? requestedCodes
    )
    {
        var requested = NormalizeCodes(requestedCodes);
        if (requestedCodes != null && requested.Count == 0)
            return (false, new List<string>());

        if (IsFullStoreRole())
            return (true, requestedCodes == null ? null : requested);

        var userGuid = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(userGuid))
            return (false, new List<string>());

        var userStores = await _userService.GetUserStoresAsync(userGuid);
        if (userStores?.Success != true || userStores.Data == null)
            return (false, new List<string>());

        var allowed = NormalizeCodes(userStores.Data.Select(store => store.StoreCode));
        if (allowed.Count == 0)
            return (false, new List<string>());
        if (requested.Count == 0)
            return (true, allowed);

        var selected = requested
            .Intersect(allowed, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (selected.Count > 0, selected);
    }

    private bool IsFullStoreRole()
    {
        return User.Claims.Any(claim =>
            claim.Type == ClaimTypes.Role
            && (claim.Value.Equals("Admin", StringComparison.OrdinalIgnoreCase)
                || claim.Value.Equals("WarehouseManager", StringComparison.OrdinalIgnoreCase)));
    }

    private static List<string> NormalizeCodes(IEnumerable<string>? codes)
    {
        return codes?
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            ?? new List<string>();
    }
}
