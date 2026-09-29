using System.Security.Claims;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SqlSugar;

namespace BlazorApp.Api.Controllers.React;

/// <summary>销售明细筛选器的只读分类选项；沿用销售明细权限，避免要求商品管理权限。</summary>
[ApiController]
[Route("api/react/v1/dashboard/sales-detail-view")]
[Authorize(Policy = Permissions.SalesDashboard.SalesDetailView)]
public sealed class SalesDetailCategoryOptionsController : ControllerBase
{
    private const int MaxSupplierCodes = 100;
    private sealed class VisibleSupplierRow
    {
        public string? Code { get; set; }
        public string? ChinaCode { get; set; }
    }
    private readonly ILocalSupplierCategoryReactService _supplierCategories;
    private readonly IWarehouseCategoryReactService _warehouseCategories;
    private readonly SqlSugarContext _context;
    private readonly IUserService _userService;
    private readonly IRoleService _roleService;

    public SalesDetailCategoryOptionsController(
        ILocalSupplierCategoryReactService supplierCategories,
        IWarehouseCategoryReactService warehouseCategories,
        SqlSugarContext context,
        IUserService userService,
        IRoleService roleService)
    {
        _supplierCategories = supplierCategories;
        _warehouseCategories = warehouseCategories;
        _context = context;
        _userService = userService;
        _roleService = roleService;
    }

    [HttpGet("category-options")]
    public async Task<IActionResult> Get(
        [FromQuery] SalesDetailKind kind,
        [FromQuery] List<string>? supplierCodes = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind)) return BadRequest(new { success = false, message = "kind 无效" });
        var normalizedSupplierCodes = (supplierCodes ?? new List<string>())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedSupplierCodes.Count > MaxSupplierCodes)
            return BadRequest(new { success = false, message = $"供应商最多选择 {MaxSupplierCodes} 项，当前为 {normalizedSupplierCodes.Count} 项" });
        // 国内供应商分类界面展示的是 200 映射后的 ChinaSupplier 编码，
        // 而销售事实中的 SupplierCode 仍可能是原始 200；仓库分类树也不按国内供应商编码分流。
        // 因此国内 tab 只校验页面权限和请求上限，不用澳洲供应商的逐码销售范围规则拦截。
        if (kind == SalesDetailKind.Australia && normalizedSupplierCodes.Count > 0)
        {
            var visible = await ResolveVisibleSupplierCodesAsync(normalizedSupplierCodes, cancellationToken);
            if (!visible.IsAuthorized)
                return StatusCode(StatusCodes.Status403Forbidden, new { success = false, message = visible.Message });
            if (visible.SupplierCodes.Count != normalizedSupplierCodes.Count)
            {
                var hidden = normalizedSupplierCodes
                    .Except(visible.SupplierCodes, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    success = false,
                    message = $"无权读取供应商分类：{string.Join("、", hidden)}",
                });
            }
        }
        if (kind == SalesDetailKind.China)
        {
            var warehouse = await _warehouseCategories.GetTreeAsync();
            return Ok(new { success = true, data = new { supplierCategories = Array.Empty<object>(), warehouseCategories = warehouse } });
        }

        var groups = new List<object>();
        foreach (var code in normalizedSupplierCodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tree = await _supplierCategories.GetTreeAsync(code, cancellationToken);
            groups.Add(new { supplierCode = code, categories = tree });
        }
        return Ok(new { success = true, data = new { supplierCategories = groups, warehouseCategories = Array.Empty<object>() } });
    }

    private async Task<(bool IsAuthorized, List<string> SupplierCodes, string Message)> ResolveVisibleSupplierCodesAsync(
        IReadOnlyCollection<string> requestedSupplierCodes,
        CancellationToken cancellationToken)
    {
        var userGuid = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(userGuid))
            return (false, new List<string>(), "当前登录身份无效");

        var permission = await _roleService.GetUserPermissionSnapshotAsync(userGuid);
        if (permission?.Success != true || permission.Data == null)
            return (false, new List<string>(), "无法确认当前账号的分店权限");

        var roles = permission.Data.RoleNames ?? new List<string>();
        var allStores = roles.Any(role =>
            Permissions.SuperAdminRoleNames.Contains(role, StringComparer.OrdinalIgnoreCase)
            || Permissions.WarehouseManagerRoleNames.Contains(role, StringComparer.OrdinalIgnoreCase));
        List<string>? storeCodes = null;
        if (!allStores)
        {
            var stores = await _userService.GetUserStoresAsync(userGuid);
            if (stores?.Success != true || stores.Data == null)
                return (false, new List<string>(), "无法确认当前账号的分店范围");
            storeCodes = stores.Data
                .Select(store => store.StoreCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (storeCodes.Count == 0)
                return (false, new List<string>(), "当前账号没有可访问的分店范围");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var visibleRaw = await BuildVisibleSupplierQuery(_context.Db, requestedSupplierCodes, storeCodes)
            .Select((sales, china) => new VisibleSupplierRow { Code = sales.SupplierCode, ChinaCode = china.SupplierCode })
            .Distinct()
            .ToListAsync();
        cancellationToken.ThrowIfCancellationRequested();
        var visible = MapVisibleSupplierCodes(visibleRaw.Select(row => (row.Code, row.ChinaCode)));
        return (true, visible, string.Empty);
    }

    internal static ISugarQueryable<ProductStoreDailySalesStatistic, ChinaSupplier> BuildVisibleSupplierQuery(
        ISqlSugarClient db,
        IReadOnlyCollection<string> requestedSupplierCodes,
        IReadOnlyCollection<string>? storeCodes)
    {
        // 销售明细 AU 口径会把 ChinaSupplier 中的原始销售供应商归并为 200。
        // 用数据库 JOIN 判断映射，避免把全量 ChinaSupplier 编码展开成 SQL IN 参数。
        var includeChinaSupplier = requestedSupplierCodes.Contains("200", StringComparer.OrdinalIgnoreCase);
        var rawSupplierCodes = requestedSupplierCodes
            .Where(code => !string.Equals(code, "200", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var query = db.Queryable<ProductStoreDailySalesStatistic>()
            .LeftJoin<ChinaSupplier>((sales, china) => sales.SupplierCode == china.SupplierCode);
        // SqlSugar 会把闭包中的 bool 翻译成 SQL bit 参数，不能直接放在 AND 条件位置。
        // 按所选供应商在 C# 侧分支，保证生成的 WHERE 只包含 SQL 谓词。
        if (includeChinaSupplier)
        {
            query = rawSupplierCodes.Count > 0
                ? query.Where((sales, china) => rawSupplierCodes.Contains(sales.SupplierCode)
                    || sales.SupplierCode == "200"
                    || (china.SupplierCode != null && china.SupplierCode != ""))
                : query.Where((sales, china) => sales.SupplierCode == "200"
                    || (china.SupplierCode != null && china.SupplierCode != ""));
        }
        else
        {
            query = query.Where((sales, china) => rawSupplierCodes.Contains(sales.SupplierCode));
        }
        if (storeCodes != null)
        {
            var allowedStoreCodes = storeCodes.ToList();
            query = query.Where((sales, china) => allowedStoreCodes.Contains(sales.BranchCode));
        }
        return query;
    }

    internal static List<string> MapVisibleSupplierCodes(
        IEnumerable<(string? Code, string? ChinaCode)> visibleRaw)
        => visibleRaw
            .Where(row => !string.IsNullOrWhiteSpace(row.Code))
            .Select(row => !string.IsNullOrWhiteSpace(row.ChinaCode) ? "200" : row.Code!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
