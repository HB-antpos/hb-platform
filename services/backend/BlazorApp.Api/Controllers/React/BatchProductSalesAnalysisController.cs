using System.Security.Claims;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

[ApiController]
[Route("api/react/v1/dashboard/batch-product-sales-analysis")]
// 后台销售看板与订货前台共用本控制器；两种权限码任一即可，具体授权与门店范围由 ResolveStoreScopeAsync 按实时快照逐请求核验。
[Authorize]
public sealed class BatchProductSalesAnalysisController : ControllerBase
{
    private readonly IBatchProductSalesAnalysisService _service;
    private readonly IUserService _userService;
    private readonly IRoleService _roleService;
    private readonly ILogger<BatchProductSalesAnalysisController> _logger;

    public BatchProductSalesAnalysisController(IBatchProductSalesAnalysisService service, IUserService userService,
        IRoleService roleService, ILogger<BatchProductSalesAnalysisController> logger)
    {
        _service = service; _userService = userService; _roleService = roleService; _logger = logger;
    }

    [HttpGet("options")]
    public async Task<IActionResult> GetOptions()
    {
        try { return Ok(await _service.GetOptionsAsync(await ResolveStoreScopeAsync(), HttpContext.RequestAborted)); }
        catch (BatchProductSalesAnalysisForbiddenException) { return Forbid(); }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { return new EmptyResult(); }
        catch (Exception ex) { return InternalError<BatchProductSalesOptionsDto>(ex, "批量货号销量选项加载失败"); }
    }

    [HttpPost("query")]
    public async Task<IActionResult> Query([FromBody] BatchProductSalesQueryRequestDto request)
    {
        try { return Ok(await _service.QueryAsync(request, await ResolveStoreScopeAsync(), HttpContext.RequestAborted)); }
        catch (BatchProductSalesAnalysisForbiddenException) { return Forbid(); }
        catch (BatchProductSalesCoverageVersionConflictException) { return Conflict(ApiResponse<BatchProductSalesQueryResultDto>.Error("统计读取期间日期版本已变化，请重新查询。", "BATCH_PRODUCT_SALES_COVERAGE_VERSION_CONFLICT")); }
        catch (BatchProductSalesAnalysisValidationException ex) { return BadRequest(ApiResponse<BatchProductSalesQueryResultDto>.Error(ex.Message)); }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { return new EmptyResult(); }
        catch (Exception ex) { return InternalError<BatchProductSalesQueryResultDto>(ex, "批量货号销量查询失败"); }
    }

    [HttpPost("detail")]
    public async Task<IActionResult> Detail([FromBody] BatchProductSalesDetailRequestDto request)
    {
        try { return Ok(await _service.GetDetailAsync(request, await ResolveStoreScopeAsync(), HttpContext.RequestAborted)); }
        catch (BatchProductSalesAnalysisForbiddenException) { return Forbid(); }
        catch (BatchProductSalesCoverageVersionConflictException)
        {
            return Conflict(ApiResponse<BatchProductSalesDetailDto>.Error(
                "摘要可用日期版本已变化，请重新查询。", "BATCH_PRODUCT_SALES_COVERAGE_VERSION_CONFLICT"));
        }
        catch (BatchProductSalesAnalysisValidationException ex) { return BadRequest(ApiResponse<BatchProductSalesDetailDto>.Error(ex.Message)); }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { return new EmptyResult(); }
        catch (Exception ex) { return InternalError<BatchProductSalesDetailDto>(ex, "批量货号销量明细查询失败"); }
    }

    [HttpPost("overview/branch")]
    public async Task<IActionResult> BranchOverview([FromBody] BatchProductSalesBranchOverviewRequestDto request)
    {
        try { return Ok(await _service.GetBranchOverviewAsync(request, await ResolveStoreScopeAsync(), HttpContext.RequestAborted)); }
        catch (BatchProductSalesAnalysisForbiddenException) { return Forbid(); }
        catch (BatchProductSalesCoverageVersionConflictException) { return Conflict(ApiResponse<BatchProductSalesBranchOverviewDto>.Error("摘要可用日期版本已变化，请重新查询。", "BATCH_PRODUCT_SALES_COVERAGE_VERSION_CONFLICT")); }
        catch (BatchProductSalesAnalysisValidationException ex) { return BadRequest(ApiResponse<BatchProductSalesBranchOverviewDto>.Error(ex.Message)); }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { return new EmptyResult(); }
        catch (Exception ex) { return InternalError<BatchProductSalesBranchOverviewDto>(ex, "分店总览加载失败"); }
    }

    [HttpPost("overview/discounts")]
    public async Task<IActionResult> DiscountOverview([FromBody] BatchProductSalesBranchOverviewRequestDto request)
    {
        try { return Ok(await _service.GetDiscountOverviewAsync(request, await ResolveStoreScopeAsync(), HttpContext.RequestAborted)); }
        catch (BatchProductSalesAnalysisForbiddenException) { return Forbid(); }
        catch (BatchProductSalesCoverageVersionConflictException) { return Conflict(ApiResponse<BatchProductSalesDiscountOverviewDto>.Error("统计或折扣快照在读取期间更新，请重新查询。", "BATCH_PRODUCT_SALES_COVERAGE_VERSION_CONFLICT")); }
        catch (BatchProductSalesAnalysisValidationException ex) { return BadRequest(ApiResponse<BatchProductSalesDiscountOverviewDto>.Error(ex.Message)); }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { return new EmptyResult(); }
        catch (Exception ex) { return InternalError<BatchProductSalesDiscountOverviewDto>(ex, "折扣总览加载失败"); }
    }

    [HttpPost("export/detail")]
    public async Task<IActionResult> ExportDetail([FromBody] BatchProductSalesFollowupRequestDto request)
    {
        try
        {
            var initialScope = await ResolveStoreScopeAsync();
            var csv = await _service.ExportDetailCsvAsync(request, initialScope, HttpContext.RequestAborted);
            // 下载前重读实时权限，防止长批量导出跨越撤权窗口后仍然下发文件。
            var finalScope = await ResolveStoreScopeAsync();
            if (!ScopeEquals(initialScope, finalScope)) throw new BatchProductSalesAnalysisForbiddenException();
            return File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv; charset=utf-8", "batch-product-sales-detail.csv");
        }
        catch (BatchProductSalesAnalysisForbiddenException) { return Forbid(); }
        catch (BatchProductSalesCoverageVersionConflictException) { return Conflict(ApiResponse<object>.Error("摘要可用日期版本已变化，请重新查询。", "BATCH_PRODUCT_SALES_COVERAGE_VERSION_CONFLICT")); }
        catch (BatchProductSalesAnalysisValidationException ex) { return BadRequest(ApiResponse<object>.Error(ex.Message)); }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { return new EmptyResult(); }
        catch (Exception ex) { return InternalError<object>(ex, "批量销量导出失败"); }
    }

    /// <summary>权限快照失败时拒绝访问，不能信任可能过期的 JWT 角色或请求门店。</summary>
    private async Task<List<string>?> ResolveStoreScopeAsync()
    {
        var userGuid = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(userGuid)) throw new BatchProductSalesAnalysisForbiddenException();
        var snapshot = await _roleService.GetUserPermissionSnapshotAsync(userGuid);
        if (snapshot?.Success != true || snapshot.Data == null) throw new BatchProductSalesAnalysisForbiddenException();
        // JWT 可能仍包含已撤销权限；每次读取都以实时精确授权为准。
        var exactPermissions = snapshot.Data.ExactPermissionCodes ?? [];
        var hasDashboardPermission = exactPermissions.Contains(Permissions.SalesDashboard.BatchProductSalesView, StringComparer.OrdinalIgnoreCase);
        var hasOrderFrontPermission = exactPermissions.Contains(Permissions.OrderFront.BatchProductSalesView, StringComparer.OrdinalIgnoreCase);
        if (!snapshot.Data.IsSuperAdmin && !hasDashboardPermission && !hasOrderFrontPermission)
            throw new BatchProductSalesAnalysisForbiddenException();
        if (snapshot.Data.IsSuperAdmin) return null;
        // 订货前台权限与热销榜口径一致：面向前台用户展示全部分店，不受用户名下门店限制。
        if (hasOrderFrontPermission) return null;
        var roles = snapshot.Data.RoleNames ?? [];
        if (roles.Any(role => Permissions.SuperAdminRoleNames.Contains(role, StringComparer.OrdinalIgnoreCase)
            || Permissions.WarehouseManagerRoleNames.Contains(role, StringComparer.OrdinalIgnoreCase))) return null;
        var user = await _userService.GetUserByGuidAsync(userGuid);
        if (user?.Success != true || user.Data == null) return [];
        return user.Data.Stores?.Where(store => !string.IsNullOrWhiteSpace(store.StoreCode))
            .Select(store => store.StoreCode.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
    }

    private static bool ScopeEquals(IReadOnlyList<string>? left, IReadOnlyList<string>? right) => left == null ? right == null : right != null && left.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).SequenceEqual(right.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    private ObjectResult InternalError<T>(Exception ex, string message)
    {
        _logger.LogError(ex, "{Message}", message);
        return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<T>.Error(message));
    }
}
