using System.Security.Claims;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlazorApp.Api.Utils;

namespace BlazorApp.Api.Controllers.React;

/// <summary>
/// 销售明细四栏的一次一致性读取入口。
/// </summary>
[ApiController]
[Route("api/react/v1/dashboard")]
[Authorize(Policy = Permissions.SalesDashboard.SalesDetailView)]
public sealed class SalesDetailReportController : ControllerBase
{
    private const int MaxSelectedSupplierCodes = 200;
    private const int MaxCategoryGuids = 500;
    private const int MaxCategoryReportSuppliers = 100;
    private readonly ISalesDashboardReactService _service;
    private readonly IUserService _userService;
    private readonly ILogger<SalesDetailReportController> _logger;

    public SalesDetailReportController(
        ISalesDashboardReactService service,
        IUserService userService,
        ILogger<SalesDetailReportController> logger)
    {
        _service = service;
        _userService = userService;
        _logger = logger;
    }

    [HttpGet("sales-detail-report")]
    public async Task<IActionResult> GetSalesDetailReport(
        [FromQuery] SalesDetailKind kind,
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        [FromQuery] DateTime? compareStartDate = null,
        [FromQuery] DateTime? compareEndDate = null,
        [FromQuery] CompareMode compareMode = CompareMode.ByDate,
        [FromQuery] List<string>? branchCodes = null,
        [FromQuery] string? selectedBranchCode = null,
        [FromQuery] string? selectedSupplierCode = null,
        [FromQuery] List<string>? selectedSupplierCodes = null,
        [FromQuery] List<string>? supplierCategoryGuids = null,
        [FromQuery] List<string>? warehouseCategoryGuids = null,
        [FromQuery] string? selectedProductCode = null,
        [FromQuery] string? search = null,
        [FromQuery] int pageIndex = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] List<SalesDetailSection>? sections = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Enum.IsDefined(kind) || !Enum.IsDefined(compareMode))
                return BadRequest(new { success = false, message = "kind 或 compareMode 无效" });
            if (pageIndex < 1 || pageSize < 1 || pageSize > 500)
                return BadRequest(new { success = false, message = "分页参数无效" });
            ValidateMultiSelectLimit(selectedSupplierCodes, MaxSelectedSupplierCodes, "供应商");
            ValidateMultiSelectLimit(supplierCategoryGuids, MaxCategoryGuids, "供应商分类");
            ValidateMultiSelectLimit(warehouseCategoryGuids, MaxCategoryGuids, "仓库分类");
            ValidateDateRange(startDate, endDate, compareStartDate, compareEndDate);

            var scope = await ResolveBranchScopeAsync(branchCodes);
            if (!scope.HasAccess || (selectedBranchCode != null && scope.BranchCodes != null
                && !scope.BranchCodes.Contains(selectedBranchCode.Trim(), StringComparer.OrdinalIgnoreCase)))
            {
                return Ok(new ProductReportResponseDto<SalesDetailReportDto>
                {
                    StatisticStatus = SalesStatisticRefreshStatus.Fresh,
                    StatisticMessage = "当前账号没有可访问的分店范围",
                    CacheVersion = "no-access",
                    Data = new SalesDetailReportDto(),
                });
            }

            var result = await _service.GetSalesDetailReportFilteredAsync(
                new DateRangeDto
                {
                    StartDate = startDate,
                    EndDate = endDate,
                    CompareStartDate = compareStartDate,
                    CompareEndDate = compareEndDate,
                    CompareMode = compareMode,
                },
                kind,
                scope.BranchCodes,
                selectedBranchCode,
                selectedSupplierCode,
                selectedProductCode,
                search,
                pageIndex,
                pageSize,
                sections,
                cancellationToken,
                selectedSupplierCodes,
                supplierCategoryGuids,
                warehouseCategoryGuids);
            cancellationToken.ThrowIfCancellationRequested();
            return Ok(result);
        }
        catch (Exception ex) when (ClientAbortDetector.IsClientAbort(ex, cancellationToken))
        {
            // 客户端中止（含令牌在 SqlClient 读取中途触发抛出的用户取消 SqlException）交给全局过滤器按 499 处理，
            // 不落入下方 500 分支；长区间查询被切走时这里最常见（生产 09-21～28 共 9 条）。
            throw;
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetSalesDetailReport failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// 「澳洲供应商分类」页签：所选供应商按分类树汇总；带 nodeSupplierCode 时附带该节点商品分页
    /// （nodeCategoryGuid 为空 = 该供应商全部商品，"__unassigned__" = 未归类）。分店范围与销售明细相同。
    /// </summary>
    [HttpGet("sales-detail-category-report")]
    public async Task<IActionResult> GetSalesDetailCategoryReport(
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        [FromQuery] List<string>? supplierCodes,
        [FromQuery] DateTime? compareStartDate = null,
        [FromQuery] DateTime? compareEndDate = null,
        [FromQuery] CompareMode compareMode = CompareMode.ByDate,
        [FromQuery] List<string>? branchCodes = null,
        [FromQuery] string? selectedBranchCode = null,
        [FromQuery] string? nodeSupplierCode = null,
        [FromQuery] string? nodeCategoryGuid = null,
        [FromQuery] string? search = null,
        [FromQuery] int pageIndex = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool includeTree = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Enum.IsDefined(compareMode))
                return BadRequest(new { success = false, message = "compareMode 无效" });
            if (pageIndex < 1 || pageSize < 1 || pageSize > 500)
                return BadRequest(new { success = false, message = "分页参数无效" });
            if (Normalize(supplierCodes).Count == 0)
                return BadRequest(new { success = false, message = "请至少选择一个供应商" });
            ValidateMultiSelectLimit(supplierCodes, MaxCategoryReportSuppliers, "供应商");
            ValidateDateRange(startDate, endDate, compareStartDate, compareEndDate);

            var scope = await ResolveBranchScopeAsync(branchCodes);
            if (!scope.HasAccess || (selectedBranchCode != null && scope.BranchCodes != null
                && !scope.BranchCodes.Contains(selectedBranchCode.Trim(), StringComparer.OrdinalIgnoreCase)))
            {
                return Ok(new ProductReportResponseDto<SalesDetailCategoryReportDto>
                {
                    StatisticStatus = SalesStatisticRefreshStatus.Fresh,
                    StatisticMessage = "当前账号没有可访问的分店范围",
                    CacheVersion = "no-access",
                    Data = new SalesDetailCategoryReportDto(),
                });
            }

            var result = await _service.GetSalesDetailCategoryReportAsync(
                new DateRangeDto
                {
                    StartDate = startDate,
                    EndDate = endDate,
                    CompareStartDate = compareStartDate,
                    CompareEndDate = compareEndDate,
                    CompareMode = compareMode,
                },
                Normalize(supplierCodes),
                scope.BranchCodes,
                selectedBranchCode,
                nodeSupplierCode,
                nodeCategoryGuid,
                search,
                pageIndex,
                pageSize,
                includeTree,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Ok(result);
        }
        catch (Exception ex) when (ClientAbortDetector.IsClientAbort(ex, cancellationToken))
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetSalesDetailCategoryReport failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>「澳洲供应商分类」页签的筛选选项：供应商（含分类数）与账号可见分店。</summary>
    [HttpGet("sales-detail-category-options")]
    public async Task<IActionResult> GetSalesDetailCategoryOptions(CancellationToken cancellationToken = default)
    {
        try
        {
            var scope = await ResolveBranchScopeAsync(null);
            var options = await _service.GetSalesDetailCategoryOptionsAsync(scope.HasAccess ? scope.BranchCodes : new List<string>(), cancellationToken);
            return Ok(new { success = true, data = options });
        }
        catch (Exception ex) when (ClientAbortDetector.IsClientAbort(ex, cancellationToken))
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetSalesDetailCategoryOptions failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    private async Task<(bool HasAccess, List<string>? BranchCodes)> ResolveBranchScopeAsync(List<string>? requested)
    {
        var normalizedRequested = Normalize(requested);
        if (requested != null && normalizedRequested.Count == 0)
            return (false, new List<string>());
        if (User.IsInRole("Admin") || User.IsInRole("WarehouseManager"))
            return (true, requested == null ? null : normalizedRequested);
        var userGuid = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(userGuid))
            return (false, new List<string>());
        // 只读销售范围取全部关联分店，不依赖用户管理详情的管理分店校验。
        var userStores = await _userService.GetUserStoresAsync(userGuid);
        if (userStores?.Success != true || userStores.Data == null)
            return (false, new List<string>());
        var allowed = Normalize(userStores.Data.Select(store => store.StoreCode));
        if (allowed.Count == 0)
            return (false, new List<string>());
        if (normalizedRequested.Count == 0)
            return (true, allowed);
        var intersection = normalizedRequested.Intersect(allowed, StringComparer.OrdinalIgnoreCase).ToList();
        return (intersection.Count > 0, intersection);
    }

    private static List<string> Normalize(IEnumerable<string>? values) => values?
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList() ?? new List<string>();

    private static void ValidateMultiSelectLimit(IEnumerable<string>? values, int limit, string name)
    {
        var count = Normalize(values).Count;
        if (count > limit)
            throw new ArgumentException($"{name}筛选最多选择 {limit} 项，当前为 {count} 项");
    }

    /// <summary>与前端日期控件一致：最长两年（含闰日）。</summary>
    internal const int MaxReportDays = 731;

    private static void ValidateDateRange(DateTime startDate, DateTime endDate, DateTime? compareStartDate, DateTime? compareEndDate)
    {
        static void Period(DateTime start, DateTime end, string label)
        {
            if (start == default || end == default || start.Date > end.Date)
                throw new ArgumentException($"{label}日期范围无效");
            if ((end.Date - start.Date).TotalDays + 1 > MaxReportDays)
                throw new ArgumentException($"{label}日期范围不能超过 {MaxReportDays} 天");
        }
        Period(startDate, endDate, "当前");
        if (compareStartDate.HasValue != compareEndDate.HasValue)
            throw new ArgumentException("比较日期必须同时提供开始和结束日期");
        if (compareStartDate.HasValue)
        {
            Period(compareStartDate.Value, compareEndDate!.Value, "比较");
            if ((endDate.Date - startDate.Date).TotalDays != (compareEndDate.Value.Date - compareStartDate.Value.Date).TotalDays)
                throw new ArgumentException("当前与比较日期范围长度必须一致");
        }
    }
}
