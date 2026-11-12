using BlazorApp.Api.Services;
using System.Security.Claims;
using BlazorApp.Api.Filters;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

/// <summary>
/// 三栏销售明细独立查询。不会改变旧报表接口的默认口径。
/// </summary>
[ApiController]
[Route("api/react/v1/dashboard")]
[Authorize(Policy = Permissions.Reports.ProductMovementView)]
public sealed class SalesDetailColumnsController : ControllerBase
{
    private readonly ISalesDashboardReactService _service;
    private readonly IUserService _userService;
    private readonly ILogger<SalesDetailColumnsController> _logger;

    public SalesDetailColumnsController(
        ISalesDashboardReactService service,
        IUserService userService,
        ILogger<SalesDetailColumnsController> logger
    )
    {
        _service = service;
        _userService = userService;
        _logger = logger;
    }

    [HttpGet("sales-detail-columns")]
    public async Task<IActionResult> GetSalesDetailColumns(
        [FromQuery] SalesDetailKind kind,
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        [FromQuery] DateTime? compareStartDate = null,
        [FromQuery] DateTime? compareEndDate = null,
        [FromQuery] CompareMode compareMode = CompareMode.ByDate,
        [FromQuery] List<string>? branchCodes = null,
        [FromQuery] string? selectedBranchCode = null,
        [FromQuery] string? selectedSupplierCode = null,
        [FromQuery] string? selectedProductCode = null,
        [FromQuery] string? search = null,
        [FromQuery] int pageIndex = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string section = "summary",
        CancellationToken cancellationToken = default
    )
        {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pageIndex < 1 || pageSize < 1 || pageSize > 100)
                return BadRequest(new { success = false, message = "分页参数无效" });
            if (!Enum.IsDefined(kind) || !Enum.IsDefined(compareMode))
                return BadRequest(new { success = false, message = "kind 或 compareMode 无效" });
            var parsedSection = ParseSection(section);
            ValidateDateRange(startDate, endDate, compareStartDate, compareEndDate);

            var scope = await ResolveBranchScopeAsync(branchCodes);
            if (!scope.HasAccess || (selectedBranchCode != null
                && scope.BranchCodes != null
                && !scope.BranchCodes.Contains(selectedBranchCode.Trim(), StringComparer.OrdinalIgnoreCase)))
            {
                return Ok(CreateResponse(
                    new SalesDetailSectionResultDto(),
                    CreateNoAccessStatus()
                ));
            }

            var dateRange = new DateRangeDto
            {
                StartDate = startDate,
                EndDate = endDate,
                CompareStartDate = compareStartDate,
                CompareEndDate = compareEndDate,
                CompareMode = compareMode,
            };
            var status = await _service.GetProductReportStatisticStatusAsync(dateRange);
            if (!string.Equals(status.StatisticStatus, SalesStatisticRefreshStatus.Fresh, StringComparison.OrdinalIgnoreCase))
            {
                return Ok(CreateResponse(new SalesDetailSectionResultDto(), status));
            }

            var result = await _service.GetSalesDetailColumnsAsync(
                dateRange,
                kind,
                section: parsedSection,
                branchCodes: scope.BranchCodes,
                selectedBranchCode,
                selectedSupplierCode,
                selectedProductCode,
                search,
                pageIndex,
                pageSize,
                status,
                cancellationToken
            );
            cancellationToken.ThrowIfCancellationRequested();
            // 国内供应商栏：没有「查看国内供应商名称」权限时只留代码；在副本上处理，不改动服务层可能缓存的对象。
            if (kind == SalesDetailKind.China
                && parsedSection == SalesDetailSection.Suppliers
                && !await HttpContext.CanViewDomesticSupplierNameAsync())
            {
                result = result.WithoutRowNames();
            }
            return Ok(CreateResponse(result, status));
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
            _logger.LogError(ex, "GetSalesDetailColumns failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    private static SalesDetailSection ParseSection(string? value)
    {
        return Enum.TryParse<SalesDetailSection>(value, true, out var section) && Enum.IsDefined(section)
            ? section
            : throw new ArgumentException("section 必须是 suppliers、branches、products 或 summary");
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
            if ((end.Date - start.Date).TotalDays + 1 > 366)
                throw new ArgumentException($"{label}日期范围不能超过366天");
        }

        ValidatePeriod(startDate, endDate, "当前");
        if (compareStartDate.HasValue != compareEndDate.HasValue)
            throw new ArgumentException("比较日期必须同时提供开始和结束日期");
        if (compareStartDate.HasValue)
        {
            ValidatePeriod(compareStartDate.Value, compareEndDate!.Value, "比较");
            var currentDays = (endDate.Date - startDate.Date).TotalDays;
            var compareDays = (compareEndDate.Value.Date - compareStartDate.Value.Date).TotalDays;
            if (currentDays != compareDays)
                throw new ArgumentException("当前与比较日期范围长度必须一致");
        }
    }

    private async Task<(bool HasAccess, List<string>? BranchCodes)> ResolveBranchScopeAsync(
        List<string>? requested
    )
    {
        var normalizedRequested = Normalize(requested);
        if (requested != null && normalizedRequested.Count == 0)
            return (false, new List<string>());

        if (User.IsInRole("Admin") || (!StoreScopePolicy.WarehouseManagerStoreScoped && User.IsInRole("WarehouseManager")))
            return (true, requested == null ? null : normalizedRequested);

        var userGuid = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(userGuid))
            return (false, new List<string>());

        var user = await _userService.GetUserByGuidAsync(userGuid);
        var allowed = Normalize(user.Data?.Stores?.Select(store => store.StoreCode));
        if (user.Success != true || allowed.Count == 0)
            return (false, new List<string>());
        if (normalizedRequested.Count == 0)
            return (true, allowed);

        var intersection = normalizedRequested
            .Intersect(allowed, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (intersection.Count > 0, intersection);
    }

    private static List<string> Normalize(IEnumerable<string>? values) => values?
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList() ?? new List<string>();

    private static ProductReportStatisticStatusDto CreateNoAccessStatus() => new()
    {
        StatisticStatus = SalesStatisticRefreshStatus.Fresh,
        StatisticMessage = "当前账号没有可访问的分店范围",
        StatisticUpdatedAt = DateTime.UtcNow,
        CacheVersion = "no-access",
    };

    private static ProductReportResponseDto<SalesDetailSectionResultDto> CreateResponse(
        SalesDetailSectionResultDto data,
        ProductReportStatisticStatusDto status
    ) => new()
    {
        Data = data,
        StatisticStatus = status.StatisticStatus,
        StatisticMessage = status.StatisticMessage,
        StatisticUpdatedAt = status.StatisticUpdatedAt,
        CacheVersion = status.CacheVersion,
    };
}
