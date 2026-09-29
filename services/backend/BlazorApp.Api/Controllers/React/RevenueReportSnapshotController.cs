using System.Security.Claims;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

/// <summary>
/// Executive Sales Intelligence 的一次性营业额快照读取入口。
/// 该入口只读取已发布统计，不触发后台重算。
/// </summary>
[ApiController]
[Route("api/react/v1/dashboard")]
[Authorize(Policy = Permissions.SalesDashboard.SalesDataView)]
public sealed class RevenueReportSnapshotController : ControllerBase
{
    private readonly ISalesDashboardReactService _service;
    private readonly IUserService _userService;
    private readonly ILogger<RevenueReportSnapshotController> _logger;

    public RevenueReportSnapshotController(
        ISalesDashboardReactService service,
        IUserService userService,
        ILogger<RevenueReportSnapshotController> logger
    )
    {
        _service = service;
        _userService = userService;
        _logger = logger;
    }

    /// <summary>
    /// 一次返回分店排行、时段和周层级快照。
    /// GET api/react/v1/dashboard/revenue-report-snapshot
    /// </summary>
    [HttpGet("revenue-report-snapshot")]
    public async Task<IActionResult> GetRevenueReportSnapshot(
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        [FromQuery] DateTime? compareStartDate = null,
        [FromQuery] DateTime? compareEndDate = null,
        [FromQuery] CompareMode compareMode = CompareMode.ByDate,
        [FromQuery] List<string>? branchCodes = null,
        [FromQuery] List<string>? focusBranchCodes = null,
        [FromQuery] int? topN = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Enum.IsDefined(compareMode))
                return BadRequest(new { success = false, message = "compareMode 无效" });
            if (topN is <= 0)
                return BadRequest(new { success = false, message = "topN 必须大于0" });
            ValidateDateRange(startDate, endDate, compareStartDate, compareEndDate);

            var scope = await ResolveBranchScopeAsync(branchCodes);
            if (!scope.HasAccess)
                return Forbid();

            var targetFocusCodes = ResolveFocusScope(focusBranchCodes, scope.BranchCodes);
            var result = await _service.GetRevenueReportSnapshotAsync(
                new DateRangeDto
                {
                    StartDate = startDate,
                    EndDate = endDate,
                    CompareStartDate = compareStartDate,
                    CompareEndDate = compareEndDate,
                    CompareMode = compareMode,
                },
                scope.BranchCodes,
                targetFocusCodes,
                topN,
                cancellationToken
            );
            var statisticsLastSuccessfulAtUtc = await ReadLastSuccessfulAtForTodayAsync(endDate);

            return Ok(new
            {
                success = true,
                data = result,
                statisticStatus = result.StatisticStatus,
                statisticMessage = result.StatisticMessage,
                statisticUpdatedAt = result.StatisticUpdatedAt,
                statisticsLastSuccessfulAtUtc,
                cacheVersion = result.CacheVersion,
                statisticsPending = result.StatisticsPending,
                statisticsExpectedBranchCount = result.StatisticsExpectedBranchCount,
                statisticsSnapshotBranchCount = result.StatisticsSnapshotBranchCount,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetRevenueReportSnapshot failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// 查询范围含今天时返回今天最近一次营业额发布时间，前端据此确定可与去年同一时刻比较的完整整点。
    /// 与移动端 statistics-freshness 同源，两端截止整点一致；只有销售数据权限的账号也能拿到。
    /// 读取失败只影响累计对齐（前端回退为不对齐），不能让整页快照失败。
    /// </summary>
    private async Task<DateTime?> ReadLastSuccessfulAtForTodayAsync(DateTime endDate)
    {
        if (endDate.Date < SalesStatisticsBusinessDate.Today())
            return null;
        try
        {
            var freshness = await _service.GetStatisticsFreshnessAsync();
            return freshness.LastSuccessfulAtUtc;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "营业额快照读取统计发布时间失败，累计对齐将回退为不对齐");
            return null;
        }
    }

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

        // 只读销售范围取全部关联分店，不依赖用户管理详情的管理分店校验。
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

    private List<string>? ResolveFocusScope(
        List<string>? requestedFocusCodes,
        List<string>? targetBranchCodes
    )
    {
        if (requestedFocusCodes == null)
            return targetBranchCodes;

        var focus = NormalizeCodes(requestedFocusCodes);
        if (targetBranchCodes == null)
            return focus;

        return focus
            .Intersect(targetBranchCodes, StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    private static void ValidateDateRange(
        DateTime startDate,
        DateTime endDate,
        DateTime? compareStartDate,
        DateTime? compareEndDate
    )
    {
        static void ValidatePeriod(DateTime start, DateTime end, string label)
        {
            if (start == default || end == default || start.Date > end.Date)
                throw new ArgumentException($"{label}日期范围无效");
            if ((end.Date - start.Date).TotalDays + 1 > SalesDetailReportController.MaxReportDays)
                throw new ArgumentException($"{label}日期范围不能超过 {SalesDetailReportController.MaxReportDays} 天");
        }

        ValidatePeriod(startDate, endDate, "当前");
        if (compareStartDate.HasValue != compareEndDate.HasValue)
            throw new ArgumentException("比较日期必须同时提供开始和结束日期");
        if (!compareStartDate.HasValue)
            return;

        ValidatePeriod(compareStartDate.Value, compareEndDate!.Value, "比较");
        if ((endDate.Date - startDate.Date).TotalDays
            != (compareEndDate.Value.Date - compareStartDate.Value.Date).TotalDays)
            throw new ArgumentException("当前与比较日期范围长度必须一致");
    }
}
