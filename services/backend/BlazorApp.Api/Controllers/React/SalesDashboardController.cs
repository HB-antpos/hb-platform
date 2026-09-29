using System.Security.Claims;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// 销售仪表盘控制器 - 提供销售数据分析和统计功能
    /// 路由: api/react/v1/dashboard
    /// </summary>
    [ApiController]
    [Route("api/react/v1/dashboard")]
    [Authorize]
    public class SalesDashboardController : ControllerBase
    {
        private readonly ISalesDashboardReactService _service;
        private readonly ILogger<SalesDashboardController> _logger;
        private readonly IUserService _userService;
        private readonly ISalesDashboardCacheWarmer _cacheWarmer;
        private readonly IRoleService _roleService;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="service">销售仪表盘服务</param>
        /// <param name="logger">日志记录器</param>
        /// <param name="userService">用户服务</param>
        /// <param name="cacheWarmer">销售仪表盘缓存预热服务</param>
        /// <param name="roleService">角色与精确权限服务</param>
        public SalesDashboardController(
            ISalesDashboardReactService service,
            ILogger<SalesDashboardController> logger,
            IUserService userService,
            ISalesDashboardCacheWarmer cacheWarmer,
            IRoleService roleService
        )
        {
            _service = service;
            _logger = logger;
            _userService = userService;
            _cacheWarmer = cacheWarmer;
            _roleService = roleService;
        }

        /// <summary>
        /// 检查当前用户是否拥有指定角色（大小写不敏感）
        /// </summary>
        /// <param name="role">角色名称</param>
        /// <returns>是否拥有该角色</returns>
        private bool HasRole(string role)
        {
            var user = HttpContext.User;
            if (user == null)
                return false;
            return user.Claims.Any(c =>
                c.Type == ClaimTypes.Role
                && c.Value.Equals(role, StringComparison.OrdinalIgnoreCase)
            );
        }

        /// <summary>
        /// 检查当前用户是否为管理员（Admin或WarehouseManager可访问所有分店数据）
        /// </summary>
        /// <returns>是否为管理员</returns>
        private bool IsAdmin()
        {
            var user = HttpContext.User;
            if (user == null)
                return false;
            // Admin 和 WarehouseManager 都可以访问所有分店数据
            return HasRole("Admin") || HasRole("WarehouseManager");
        }

        /// <summary>
        /// 获取当前用户可访问的分店代码列表
        /// 管理员返回null（可访问所有分店），普通用户返回其关联的分店代码列表
        /// </summary>
        /// <returns>分店代码列表，管理员返回null</returns>
        private async Task<List<string>?> GetUserBranchCodesAsync()
        {
            // 管理员可访问所有分店
            if (IsAdmin())
                return null;

            // 获取当前用户GUID
            var userGuid = HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userGuid))
                return null;

            // 查询用户信息
            var user = await _userService.GetUserByGuidAsync(userGuid);
            if (user?.Success != true || user?.Data == null)
                return null;

            // 返回用户关联的分店代码
            return user.Data.Stores?.Select(s => s.StoreCode).ToList();
        }

        private async Task<(bool HasAccess, List<string>? BranchCodes)> ResolveTargetBranchCodesAsync(
            List<string>? branchCodes
        )
        {
            var requestedBranchCodes = NormalizeBranchCodes(branchCodes);
            if (branchCodes != null && requestedBranchCodes.Count == 0)
                return (false, new List<string>());

            if (IsAdmin())
                return (true, branchCodes != null ? requestedBranchCodes : null);

            var userBranchCodes = NormalizeBranchCodes(await GetUserBranchCodesAsync());
            if (userBranchCodes.Count == 0)
                return (false, new List<string>());

            if (requestedBranchCodes.Count == 0)
                return (true, userBranchCodes);

            // 普通用户只能查询自己关联分店与请求分店的交集。
            var allowedBranchCodes = requestedBranchCodes.Intersect(userBranchCodes).ToList();
            return (allowedBranchCodes.Count > 0, allowedBranchCodes);
        }

        private static List<string> NormalizeBranchCodes(IEnumerable<string>? branchCodes)
        {
            return branchCodes?
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code.Trim())
                    .Distinct()
                    .ToList()
                ?? new List<string>();
        }

        private async Task<bool> HasExactProductSalesAnalysisPermissionAsync()
        {
            var userGuid = HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrWhiteSpace(userGuid))
                return false;

            var result = await _roleService.UserHasExactPermissionAsync(
                userGuid,
                Permissions.Reports.ProductMovementView
            );
            return result.Success && result.Data;
        }

        /// <summary>
        /// 商品销量分析接口专用的分店范围解析。
        /// 全分店身份只能来自实时权限快照中的活动、未删除角色名，不能信任 JWT 旧角色 claim；
        /// 非全分店用户只取 IUserService 返回的授权分店；用户有效但分店为空时视为“授权成功、分店范围为空”，
        /// 把空 List 交给 service 走 Fresh 类型化空信封。快照失败、用户读取失败或缺用户 ID 时 fail closed。
        /// </summary>
        private async Task<(bool HasAccess, List<string>? BranchCodes)> ResolveProductSalesAnalysisBranchCodesAsync()
        {
            var userGuid = HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrWhiteSpace(userGuid))
                return (false, new List<string>());

            // 实时快照为全分店判定的唯一来源；防御性处理未返回快照任务的情况，保持 fail closed。
            var snapshotTask = _roleService.GetUserPermissionSnapshotAsync(userGuid);
            if (snapshotTask == null)
                return (false, new List<string>());

            var snapshotResult = await snapshotTask;
            if (snapshotResult?.Success != true || snapshotResult.Data == null)
                return (false, new List<string>());

            var roleNames = snapshotResult.Data.RoleNames ?? new List<string>();
            var hasFullStoreRole = roleNames.Any(role =>
                Permissions.SuperAdminRoleNames.Contains(role, StringComparer.OrdinalIgnoreCase)
                || Permissions.WarehouseManagerRoleNames.Contains(role, StringComparer.OrdinalIgnoreCase)
            );

            if (hasFullStoreRole)
                return (true, null);

            var userResult = await _userService.GetUserByGuidAsync(userGuid);
            if (userResult?.Success != true || userResult.Data == null)
                return (false, new List<string>());

            var storeCodes = NormalizeBranchCodes(
                userResult.Data.Stores?.Select(store => store.StoreCode)
            );
            return (true, storeCodes);
        }

        /// <summary>
        /// 获取仪表盘汇总数据
        /// GET api/react/v1/dashboard/summary
        /// </summary>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <param name="compareStartDate">对比开始日期（可选）</param>
        /// <param name="compareEndDate">对比结束日期（可选）</param>
        /// <param name="compareMode">对比模式（按日期/按期间）</param>
        /// <returns>仪表盘汇总数据</returns>
        [HttpGet("summary")]
        public async Task<IActionResult> GetDashboardSummary(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] DateTime? compareStartDate = null,
            [FromQuery] DateTime? compareEndDate = null,
            [FromQuery] CompareMode compareMode = CompareMode.ByDate
        )
        {
            try
            {
                // 构建日期范围DTO
                var dateRange = new DateRangeDto
                {
                    StartDate = startDate,
                    EndDate = endDate,
                    CompareStartDate = compareStartDate,
                    CompareEndDate = compareEndDate,
                    CompareMode = compareMode,
                };

                // 调用服务获取汇总数据
                var result = await _service.GetDashboardSummaryAsync(dateRange);
                return Ok(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetDashboardSummary failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取按小时统计的销售数据
        /// GET api/react/v1/dashboard/hourly-sales
        /// </summary>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <param name="compareStartDate">对比开始日期（可选）</param>
        /// <param name="compareEndDate">对比结束日期（可选）</param>
        /// <param name="compareMode">对比模式</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <returns>小时销售数据列表</returns>
        [HttpGet("hourly-sales")]
        public async Task<IActionResult> GetHourlySales(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] DateTime? compareStartDate = null,
            [FromQuery] DateTime? compareEndDate = null,
            [FromQuery] CompareMode compareMode = CompareMode.ByDate,
            [FromQuery] List<string>? branchCodes = null
        )
        {
            try
            {
                // 获取用户可访问的分店代码
                var userBranchCodes = await GetUserBranchCodesAsync();
                List<string>? targetBranchCodes = userBranchCodes;

                // 如果请求中指定了分店代码，需要进行权限校验
                if (branchCodes != null && branchCodes.Any())
                {
                    if (userBranchCodes != null)
                    {
                        // 普通用户：取请求分店与用户可访问分店的交集
                        targetBranchCodes = branchCodes.Intersect(userBranchCodes).ToList();

                        // 如果交集为空，说明用户无权访问任何请求的分店
                        if (!targetBranchCodes.Any())
                        {
                            return Ok(new { success = true, data = new List<object>() });
                        }
                    }
                    else
                    {
                        // 管理员：允许访问请求的所有分店
                        targetBranchCodes = branchCodes;
                    }
                }

                // 构建日期范围DTO
                var dateRange = new DateRangeDto
                {
                    StartDate = startDate,
                    EndDate = endDate,
                    CompareStartDate = compareStartDate,
                    CompareEndDate = compareEndDate,
                    CompareMode = compareMode,
                };

                // 调用服务获取小时销售数据
                var result = await _service.GetHourlySalesAsync(dateRange, targetBranchCodes);
                return Ok(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetHourlySales failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取分店销售排行
        /// GET api/react/v1/dashboard/store-sales-rank
        /// </summary>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <param name="compareStartDate">对比开始日期（可选）</param>
        /// <param name="compareEndDate">对比结束日期（可选）</param>
        /// <param name="compareMode">对比模式</param>
        /// <param name="topN">返回前N条记录</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <returns>分店销售排行列表</returns>
        [HttpGet("store-sales-rank")]
        public async Task<IActionResult> GetStoreSalesRank(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] DateTime? compareStartDate = null,
            [FromQuery] DateTime? compareEndDate = null,
            [FromQuery] CompareMode compareMode = CompareMode.ByDate,
            [FromQuery] int topN = 100,
            [FromQuery] List<string>? branchCodes = null
        )
        {
            try
            {
                // 统一用安全范围解析，避免普通用户解析失败时退化为全分店。
                var branchScope = await ResolveTargetBranchCodesAsync(branchCodes);
                if (!branchScope.HasAccess)
                    return Ok(new
                    {
                        success = true,
                        data = new List<ExecutiveBranchPerformanceDto>(),
                        statisticsPending = false,
                        statisticsExpectedBranchCount = 0,
                        statisticsSnapshotBranchCount = 0,
                    });

                // 构建日期范围DTO
                var dateRange = new DateRangeDto
                {
                    StartDate = startDate,
                    EndDate = endDate,
                    CompareStartDate = compareStartDate,
                    CompareEndDate = compareEndDate,
                    CompareMode = compareMode,
                };

                // 调用服务获取分店销售排行
                var result = await _service.GetStoreSalesRankAsync(
                    dateRange,
                    branchScope.BranchCodes,
                    topN
                );
                return Ok(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetStoreSalesRank failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取供应商销售排行
        /// GET api/react/v1/dashboard/supplier-sales-rank
        /// </summary>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <param name="compareStartDate">对比开始日期（可选）</param>
        /// <param name="compareEndDate">对比结束日期（可选）</param>
        /// <param name="compareMode">对比模式</param>
        /// <param name="topN">返回前N条记录</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <param name="supplierCode">供应商代码（可选，用于联动过滤）</param>
        /// <returns>供应商销售排行列表</returns>
        [HttpGet("supplier-sales-rank")]
        [Authorize(Policy = Permissions.Reports.ProductMovementView)]
        public async Task<IActionResult> GetSupplierSalesRank(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] DateTime? compareStartDate = null,
            [FromQuery] DateTime? compareEndDate = null,
            [FromQuery] CompareMode compareMode = CompareMode.ByDate,
            [FromQuery] int topN = 200,
            [FromQuery] List<string>? branchCodes = null,
            [FromQuery] string? supplierCode = null
        )
        {
            try
            {
                // 商品报告入口统一用安全范围解析，避免普通用户解析失败时退化为全分店。
                var branchScope = await ResolveTargetBranchCodesAsync(branchCodes);
                if (!branchScope.HasAccess)
                    return Ok(CreateProductReportResponse(
                        new List<SupplierSalesRankDto>(),
                        CreateNoAccessProductStatisticStatus()
                    ));

                // 构建日期范围DTO
                var dateRange = new DateRangeDto
                {
                    StartDate = startDate,
                    EndDate = endDate,
                    CompareStartDate = compareStartDate,
                    CompareEndDate = compareEndDate,
                    CompareMode = compareMode,
                };
                var statisticStatus = await _service.GetProductReportStatisticStatusAsync(dateRange);
                if (!IsProductStatisticFresh(statisticStatus))
                {
                    return Ok(CreateProductReportResponse(
                        new List<SupplierSalesRankDto>(),
                        statisticStatus
                    ));
                }

                // 调用服务获取供应商销售排行
                var result = await _service.GetSupplierSalesRankAsync(
                    dateRange,
                    branchScope.BranchCodes,
                    topN,
                    supplierCode,
                    statisticStatus
                );
                return Ok(CreateProductReportResponse(result, statisticStatus));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetSupplierSalesRank failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取中国供应商销售排行
        /// GET api/react/v1/dashboard/china-supplier-sales-rank
        /// </summary>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <param name="compareStartDate">对比开始日期（可选）</param>
        /// <param name="compareEndDate">对比结束日期（可选）</param>
        /// <param name="compareMode">对比模式</param>
        /// <param name="topN">返回前N条记录</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <param name="supplierCode">供应商代码（可选，用于联动过滤）</param>
        /// <returns>中国供应商销售排行列表</returns>
        [HttpGet("china-supplier-sales-rank")]
        [Authorize(Policy = Permissions.Reports.ProductMovementView)]
        public async Task<IActionResult> GetChinaSupplierSalesRankAsync(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] DateTime? compareStartDate = null,
            [FromQuery] DateTime? compareEndDate = null,
            [FromQuery] CompareMode compareMode = CompareMode.ByDate,
            [FromQuery] int topN = 200,
            [FromQuery] List<string>? branchCodes = null,
            [FromQuery] string? supplierCode = null
        )
        {
            try
            {
                // 商品报告入口统一用安全范围解析，避免普通用户解析失败时退化为全分店。
                var branchScope = await ResolveTargetBranchCodesAsync(branchCodes);
                if (!branchScope.HasAccess)
                    return Ok(CreateProductReportResponse(
                        new List<ChinaSupplierSalesRankDto>(),
                        CreateNoAccessProductStatisticStatus()
                    ));

                // 构建日期范围DTO
                var dateRange = new DateRangeDto
                {
                    StartDate = startDate,
                    EndDate = endDate,
                    CompareStartDate = compareStartDate,
                    CompareEndDate = compareEndDate,
                    CompareMode = compareMode,
                };
                var statisticStatus = await _service.GetProductReportStatisticStatusAsync(dateRange);
                if (!IsProductStatisticFresh(statisticStatus))
                {
                    return Ok(CreateProductReportResponse(
                        new List<ChinaSupplierSalesRankDto>(),
                        statisticStatus
                    ));
                }

                // 调用服务获取中国供应商销售排行
                var result = await _service.GetChinaSupplierSalesRankAsync(
                    dateRange,
                    branchScope.BranchCodes,
                    topN,
                    supplierCode,
                    statisticStatus
                );
                return Ok(CreateProductReportResponse(result, statisticStatus));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetChinaSupplierSalesRank failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 按分店汇总全部中国供应商的销售额（移动端商品报告「分店中国货占比」的分子）
        /// GET api/react/v1/dashboard/china-supplier-branch-totals
        /// </summary>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <param name="compareStartDate">对比开始日期（可选）</param>
        /// <param name="compareEndDate">对比结束日期（可选）</param>
        /// <param name="compareMode">对比模式</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <returns>每个分店一行的中国货销售合计</returns>
        [HttpGet("china-supplier-branch-totals")]
        [Authorize(Policy = Permissions.Reports.ProductMovementView)]
        public async Task<IActionResult> GetChinaSupplierBranchTotalsAsync(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] DateTime? compareStartDate = null,
            [FromQuery] DateTime? compareEndDate = null,
            [FromQuery] CompareMode compareMode = CompareMode.ByDate,
            [FromQuery] List<string>? branchCodes = null
        )
        {
            try
            {
                // 与排行同一套安全范围解析，普通用户解析失败时不能退化为全分店。
                var branchScope = await ResolveTargetBranchCodesAsync(branchCodes);
                if (!branchScope.HasAccess)
                    return Ok(CreateProductReportResponse(
                        new List<ChinaSupplierBranchTotalDto>(),
                        CreateNoAccessProductStatisticStatus()
                    ));

                var dateRange = new DateRangeDto
                {
                    StartDate = startDate,
                    EndDate = endDate,
                    CompareStartDate = compareStartDate,
                    CompareEndDate = compareEndDate,
                    CompareMode = compareMode,
                };
                // 与供应商排行、商品明细共用同一个统计版本，移动端据此把四块数据对齐到同一批次。
                var statisticStatus = await _service.GetProductReportStatisticStatusAsync(dateRange);
                if (!IsProductStatisticFresh(statisticStatus))
                {
                    return Ok(CreateProductReportResponse(
                        new List<ChinaSupplierBranchTotalDto>(),
                        statisticStatus
                    ));
                }

                var result = await _service.GetChinaSupplierBranchTotalsAsync(
                    dateRange,
                    branchScope.BranchCodes,
                    statisticStatus
                );
                return Ok(CreateProductReportResponse(result, statisticStatus));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetChinaSupplierBranchTotals failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取供应商在各分店的销售数据
        /// GET api/react/v1/dashboard/supplier-store-sales
        /// </summary>
        /// <param name="supplierCodes">供应商代码列表</param>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <param name="compareStartDate">对比开始日期（可选）</param>
        /// <param name="compareEndDate">对比结束日期（可选）</param>
        /// <param name="compareMode">对比模式</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <returns>供应商分店销售数据列表</returns>
        [HttpGet("supplier-store-sales")]
        [Authorize(Policy = Permissions.Reports.ProductMovementView)]
        public async Task<IActionResult> GetSupplierStoreSales(
            [FromQuery] List<string> supplierCodes,
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] DateTime? compareStartDate = null,
            [FromQuery] DateTime? compareEndDate = null,
            [FromQuery] CompareMode compareMode = CompareMode.ByDate,
            [FromQuery] List<string>? branchCodes = null
        )
        {
            try
            {
                // 校验供应商代码不能为空
                if (supplierCodes == null || !supplierCodes.Any())
                {
                    return BadRequest(new { success = false, message = "供应商代码不能为空" });
                }

                // 商品报告入口统一用安全范围解析，避免普通用户解析失败时退化为全分店。
                var branchScope = await ResolveTargetBranchCodesAsync(branchCodes);
                if (!branchScope.HasAccess)
                    return Ok(CreateProductReportResponse(
                        new List<SupplierStoreSalesDto>(),
                        CreateNoAccessProductStatisticStatus()
                    ));

                // 构建日期范围DTO
                var dateRange = new DateRangeDto
                {
                    StartDate = startDate,
                    EndDate = endDate,
                    CompareStartDate = compareStartDate,
                    CompareEndDate = compareEndDate,
                    CompareMode = compareMode,
                };
                var statisticStatus = await _service.GetProductReportStatisticStatusAsync(dateRange);
                if (!IsProductStatisticFresh(statisticStatus))
                {
                    return Ok(CreateProductReportResponse(
                        new List<SupplierStoreSalesDto>(),
                        statisticStatus
                    ));
                }

                // 调用服务获取供应商分店销售数据
                var result = await _service.GetSupplierStoreSalesAsync(
                    dateRange,
                    supplierCodes,
                    branchScope.BranchCodes,
                    statisticStatus
                );
                return Ok(CreateProductReportResponse(result, statisticStatus));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetSupplierStoreSales failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取分店供应商销售排行
        /// GET api/react/v1/dashboard/store-supplier-sales
        /// </summary>
        /// <param name="branchCodes">分店代码列表</param>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <param name="compareStartDate">对比开始日期（可选）</param>
        /// <param name="compareEndDate">对比结束日期（可选）</param>
        /// <param name="compareMode">对比模式</param>
        /// <param name="topN">返回前N条记录</param>
        /// <returns>分店供应商销售排行列表</returns>
        [HttpGet("store-supplier-sales")]
        public async Task<IActionResult> GetStoreSupplierSales(
            [FromQuery] List<string> branchCodes,
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] DateTime? compareStartDate = null,
            [FromQuery] DateTime? compareEndDate = null,
            [FromQuery] CompareMode compareMode = CompareMode.ByDate,
            [FromQuery] int topN = 100
        )
        {
            try
            {
                // 校验分店代码不能为空
                if (branchCodes == null || !branchCodes.Any())
                {
                    return BadRequest(new { success = false, message = "分店代码不能为空" });
                }

                // 获取用户可访问的分店代码
                var userBranchCodes = await GetUserBranchCodesAsync();
                List<string>? targetBranchCodes = userBranchCodes;

                // 权限校验：取请求分店与用户可访问分店的交集
                if (userBranchCodes != null)
                {
                    targetBranchCodes = branchCodes.Intersect(userBranchCodes).ToList();

                    if (!targetBranchCodes.Any())
                    {
                        return Ok(new { success = true, data = new List<object>() });
                    }
                }
                else
                {
                    targetBranchCodes = branchCodes;
                }

                // 构建日期范围DTO
                var dateRange = new DateRangeDto
                {
                    StartDate = startDate,
                    EndDate = endDate,
                    CompareStartDate = compareStartDate,
                    CompareEndDate = compareEndDate,
                    CompareMode = compareMode,
                };

                // 调用服务获取分店供应商销售排行
                var result = await _service.GetStoreSupplierSalesAsync(
                    dateRange,
                    targetBranchCodes,
                    topN
                );
                return Ok(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetStoreSupplierSales failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取中国供应商在各分店的销售数据
        /// GET api/react/v1/dashboard/china-supplier-store-sales
        /// </summary>
        /// <param name="supplierCodes">中国供应商代码列表</param>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <param name="compareStartDate">对比开始日期（可选）</param>
        /// <param name="compareEndDate">对比结束日期（可选）</param>
        /// <param name="compareMode">对比模式</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <returns>中国供应商分店销售数据列表</returns>
        [HttpGet("china-supplier-store-sales")]
        [Authorize(Policy = Permissions.Reports.ProductMovementView)]
        public async Task<IActionResult> GetChinaSupplierStoreSales(
            [FromQuery] List<string> supplierCodes,
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] DateTime? compareStartDate = null,
            [FromQuery] DateTime? compareEndDate = null,
            [FromQuery] CompareMode compareMode = CompareMode.ByDate,
            [FromQuery] List<string>? branchCodes = null
        )
        {
            try
            {
                if (supplierCodes == null || !supplierCodes.Any())
                {
                    return BadRequest(new { success = false, message = "供应商代码不能为空" });
                }

                // 商品报告入口统一用安全范围解析，避免普通用户解析失败时退化为全分店。
                var branchScope = await ResolveTargetBranchCodesAsync(branchCodes);
                if (!branchScope.HasAccess)
                    return Ok(CreateProductReportResponse(
                        new List<ChinaSupplierStoreSalesDto>(),
                        CreateNoAccessProductStatisticStatus()
                    ));

                var dateRange = new DateRangeDto
                {
                    StartDate = startDate,
                    EndDate = endDate,
                    CompareStartDate = compareStartDate,
                    CompareEndDate = compareEndDate,
                    CompareMode = compareMode,
                };
                var statisticStatus = await _service.GetProductReportStatisticStatusAsync(dateRange);
                if (!IsProductStatisticFresh(statisticStatus))
                {
                    return Ok(CreateProductReportResponse(
                        new List<ChinaSupplierStoreSalesDto>(),
                        statisticStatus
                    ));
                }

                var result = await _service.GetChinaSupplierStoreSalesAsync(
                    dateRange,
                    supplierCodes,
                    branchScope.BranchCodes,
                    statisticStatus
                );
                return Ok(CreateProductReportResponse(result, statisticStatus));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetChinaSupplierStoreSales failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取澳洲供应商分店销售明细
        /// GET api/react/v1/dashboard/australian-supplier-store-sales-details
        /// </summary>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <param name="supplierCodes">供应商代码列表（可选）</param>
        /// <returns>澳洲供应商分店销售明细列表</returns>
        [HttpGet("australian-supplier-store-sales-details")]
        [Authorize(Policy = Permissions.Reports.ProductMovementView)]
        public async Task<IActionResult> GetAustralianSupplierStoreSalesDetails(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] List<string>? branchCodes = null,
            [FromQuery] List<string>? supplierCodes = null
        )
        {
            try
            {
                // 商品报告入口统一用安全范围解析，避免普通用户解析失败时退化为全分店。
                var branchScope = await ResolveTargetBranchCodesAsync(branchCodes);
                if (!branchScope.HasAccess)
                    return Ok(CreateProductReportResponse(
                        new List<AustralianSupplierStoreSalesDetailDto>(),
                        CreateNoAccessProductStatisticStatus()
                    ));

                var dateRange = new DateRangeDto { StartDate = startDate, EndDate = endDate };
                var statisticStatus = await _service.GetProductReportStatisticStatusAsync(dateRange);
                if (!IsProductStatisticFresh(statisticStatus))
                    return Ok(CreateProductReportResponse(
                        new List<AustralianSupplierStoreSalesDetailDto>(),
                        statisticStatus
                    ));

                var result = await _service.GetAustralianSupplierStoreSalesDetailsAsync(
                    dateRange,
                    branchScope.BranchCodes,
                    supplierCodes
                );
                return Ok(CreateProductReportResponse(result, statisticStatus));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetAustralianSupplierStoreSalesDetails failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取中国供应商分店销售明细
        /// GET api/react/v1/dashboard/china-supplier-store-sales-details
        /// </summary>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <param name="supplierCodes">供应商代码列表（可选）</param>
        /// <returns>中国供应商分店销售明细列表</returns>
        [HttpGet("china-supplier-store-sales-details")]
        [Authorize(Policy = Permissions.Reports.ProductMovementView)]
        public async Task<IActionResult> GetChinaSupplierStoreSalesDetails(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] List<string>? branchCodes = null,
            [FromQuery] List<string>? supplierCodes = null
        )
        {
            try
            {
                // 商品报告入口统一用安全范围解析，避免普通用户解析失败时退化为全分店。
                var branchScope = await ResolveTargetBranchCodesAsync(branchCodes);
                if (!branchScope.HasAccess)
                    return Ok(CreateProductReportResponse(
                        new List<ChinaSupplierStoreSalesDetailDto>(),
                        CreateNoAccessProductStatisticStatus()
                    ));

                var dateRange = new DateRangeDto { StartDate = startDate, EndDate = endDate };
                var statisticStatus = await _service.GetProductReportStatisticStatusAsync(dateRange);
                if (!IsProductStatisticFresh(statisticStatus))
                    return Ok(CreateProductReportResponse(
                        new List<ChinaSupplierStoreSalesDetailDto>(),
                        statisticStatus
                    ));

                var result = await _service.GetChinaSupplierStoreSalesDetailsAsync(
                    dateRange,
                    branchScope.BranchCodes,
                    supplierCodes
                );
                return Ok(CreateProductReportResponse(result, statisticStatus));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetChinaSupplierStoreSalesDetails failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取销售商品明细（分页）
        /// GET api/react/v1/dashboard/sales-product-details
        /// </summary>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <param name="compareStartDate">对比开始日期（可选）</param>
        /// <param name="compareEndDate">对比结束日期（可选）</param>
        /// <param name="compareMode">对比模式</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <param name="supplierCodes">供应商代码列表（可选）</param>
        /// <param name="pageIndex">页码（从1开始）</param>
        /// <param name="pageSize">每页记录数</param>
        /// <returns>分页销售商品明细</returns>
        [HttpGet("sales-product-details")]
        public async Task<IActionResult> GetSalesProductDetails(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] DateTime? compareStartDate = null,
            [FromQuery] DateTime? compareEndDate = null,
            [FromQuery] CompareMode compareMode = CompareMode.ByDate,
            [FromQuery] List<string>? branchCodes = null,
            [FromQuery] List<string>? supplierCodes = null,
            [FromQuery] int pageIndex = 1,
            [FromQuery] int pageSize = 100
        )
        {
            try
            {
                // 获取用户可访问的分店代码
                var userBranchCodes = await GetUserBranchCodesAsync();
                List<string>? targetBranchCodes = userBranchCodes;

                // 权限校验：取请求分店与用户可访问分店的交集
                if (userBranchCodes != null && branchCodes != null && branchCodes.Any())
                {
                    targetBranchCodes = branchCodes.Intersect(userBranchCodes).ToList();

                    if (!targetBranchCodes.Any())
                    {
                        return Ok(
                            new
                            {
                                success = true,
                                data = new PagedSalesProductDetailDto
                                {
                                    Data = new List<SalesProductDetailDto>(),
                                    Total = 0,
                                    PageIndex = pageIndex,
                                    PageSize = pageSize,
                                },
                            }
                        );
                    }
                }
                else if (userBranchCodes == null)
                {
                    targetBranchCodes = branchCodes;
                }

                // 构建日期范围DTO
                var dateRange = new DateRangeDto
                {
                    StartDate = startDate,
                    EndDate = endDate,
                    CompareStartDate = compareStartDate,
                    CompareEndDate = compareEndDate,
                    CompareMode = compareMode,
                };

                // 调用服务获取销售商品明细
                var result = await _service.GetSalesProductDetailsAsync(
                    dateRange,
                    targetBranchCodes,
                    supplierCodes,
                    pageIndex,
                    pageSize
                );
                return Ok(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetSalesProductDetails failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取增强版销售商品明细（包含折扣信息，分页）
        /// GET api/react/v1/dashboard/enhanced-sales-product-details
        /// </summary>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <param name="compareStartDate">对比开始日期（可选）</param>
        /// <param name="compareEndDate">对比结束日期（可选）</param>
        /// <param name="compareMode">对比模式</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <param name="localSupplierCodes">本地供应商代码列表（可选）</param>
        /// <param name="chinaSupplierCodes">中国供应商代码列表（可选）</param>
        /// <param name="pageIndex">页码（从1开始）</param>
        /// <param name="pageSize">每页记录数</param>
        /// <param name="productSearch">货号或条码查询（可选）</param>
        /// <param name="supplierScope">供应商范围；china 表示全部中国供应商（可选）</param>
        /// <param name="sortField">排序字段 amount/quantity/unitPrice（可选，默认金额）</param>
        /// <param name="sortOrder">排序方向 asc/desc（可选，默认降序）</param>
        /// <returns>分页增强版销售商品明细</returns>
        [HttpGet("enhanced-sales-product-details")]
        [Authorize(Policy = Permissions.Reports.ProductMovementView)]
        public async Task<IActionResult> GetEnhancedSalesProductDetails(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] DateTime? compareStartDate = null,
            [FromQuery] DateTime? compareEndDate = null,
            [FromQuery] CompareMode compareMode = CompareMode.ByDate,
            [FromQuery] List<string>? branchCodes = null,
            [FromQuery] List<string>? localSupplierCodes = null,
            [FromQuery] List<string>? chinaSupplierCodes = null,
            [FromQuery] int pageIndex = 1,
            [FromQuery] int pageSize = 100,
            [FromQuery] string? productSearch = null,
            [FromQuery] string? supplierScope = null,
            [FromQuery] string? sortField = null,
            [FromQuery] string? sortOrder = null
        )
        {
            try
            {
                // 排序参数是用户输入，日志只记录白名单归一化后的标记，避免日志伪造。
                _logger.LogInformation("[GetEnhancedSalesProductDetails] Received request: StartDate={StartDate}, EndDate={EndDate}, CompareStartDate={CompareStartDate}, CompareEndDate={CompareEndDate}, CompareMode={CompareMode}, PageIndex={PageIndex}, PageSize={PageSize}, HasProductSearch={HasProductSearch}, Sort={Sort}",
                    startDate, endDate, compareStartDate, compareEndDate, compareMode, pageIndex, pageSize, !string.IsNullOrWhiteSpace(productSearch),
                    ProductReportSort.Parse(sortField, sortOrder).Token);

                // 商品报告入口统一用安全范围解析，避免普通用户解析失败时退化为全分店。
                var branchScope = await ResolveTargetBranchCodesAsync(branchCodes);
                if (!branchScope.HasAccess)
                {
                    return Ok(CreateProductReportResponse(
                        CreateEmptyEnhancedSalesProductDetails(pageIndex, pageSize),
                        CreateNoAccessProductStatisticStatus()
                    ));
                }

                // 构建日期范围DTO
                var dateRange = new DateRangeDto
                {
                    StartDate = startDate,
                    EndDate = endDate,
                    CompareStartDate = compareStartDate,
                    CompareEndDate = compareEndDate,
                    CompareMode = compareMode,
                };
                var statisticStatus = await _service.GetProductReportStatisticStatusAsync(dateRange);
                if (!IsProductStatisticFresh(statisticStatus))
                {
                    return Ok(CreateProductReportResponse(
                        CreateEmptyEnhancedSalesProductDetails(pageIndex, pageSize),
                        statisticStatus
                    ));
                }

                // 调用服务获取增强版销售商品明细
                var chinaSupplierScope = string.Equals(
                    supplierScope?.Trim(),
                    "china",
                    StringComparison.OrdinalIgnoreCase
                );
                var result = await _service.GetEnhancedSalesProductDetailsAsync(
                    dateRange,
                    branchScope.BranchCodes,
                    localSupplierCodes,
                    chinaSupplierCodes,
                    pageIndex,
                    pageSize,
                    productSearch,
                    statisticStatus,
                    chinaSupplierScope,
                    sortField,
                    sortOrder
                );
                return Ok(CreateProductReportResponse(result, statisticStatus));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetEnhancedSalesProductDetails failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取紧凑销售看板。
        /// 分店范围统一从实时用户关系解析，任何解析失败都拒绝请求，不能退化成全分店查询。
        /// branchCodes 只表示授权范围；页面上选中的分店走 selectedBranchCode，
        /// 服务端只在授权范围内匹配选中分店，越权选中只会得到空结果。
        /// </summary>
        [HttpGet("compact-sales-board")]
        [Authorize(Policy = Permissions.SalesDashboard.CompactBoardView)]
        public async Task<IActionResult> GetCompactSalesBoard(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] List<string>? branchCodes = null,
            [FromQuery] string? selectedBranchCode = null,
            [FromQuery] string? selectedChinaSupplierCode = null,
            [FromQuery] string? selectedProductCode = null,
            [FromQuery] string? keyword = null,
            [FromQuery] string? sortField = null,
            [FromQuery] string? sortOrder = null,
            [FromQuery] int pageIndex = 1,
            [FromQuery] int pageSize = 50,
            [FromQuery] bool forceRefresh = false
        )
        {
            try
            {
                var branchScope = await ResolveTargetBranchCodesAsync(branchCodes);
                if (!branchScope.HasAccess)
                    return Forbid();

                var result = await _service.GetCompactSalesBoardAsync(new CompactSalesBoardQuery
                {
                    DateRange = new DateRangeDto
                    {
                        StartDate = startDate,
                        EndDate = endDate,
                        CompareMode = CompareMode.ByDate,
                    },
                    BranchCodes = branchScope.BranchCodes,
                    SelectedBranchCode = selectedBranchCode,
                    SelectedChinaSupplierCode = selectedChinaSupplierCode,
                    SelectedProductCode = selectedProductCode,
                    Keyword = keyword,
                    SortField = sortField,
                    SortOrder = sortOrder,
                    PageIndex = pageIndex,
                    PageSize = pageSize,
                    ForceRefresh = forceRefresh,
                });
                return Ok(new { success = true, data = result });
            }
            catch (ArgumentException exception)
            {
                return BadRequest(new { success = false, message = exception.Message });
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "GetCompactSalesBoard failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取指定商品在所有分店的销售数据
        /// GET api/react/v1/dashboard/product-sales-by-branches
        /// </summary>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <param name="compareStartDate">对比开始日期（可选）</param>
        /// <param name="compareEndDate">对比结束日期（可选）</param>
        /// <param name="compareMode">对比模式</param>
        /// <param name="productCode">商品代码</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <returns>商品在各分店的销售数据</returns>
        [HttpGet("product-sales-by-branches")]
        [Authorize(Policy = Permissions.Reports.ProductMovementView)]
        public async Task<IActionResult> GetProductSalesByAllBranches(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] DateTime? compareStartDate = null,
            [FromQuery] DateTime? compareEndDate = null,
            [FromQuery] CompareMode compareMode = CompareMode.ByDate,
            [FromQuery] string productCode = "",
            [FromQuery] List<string>? branchCodes = null
        )
        {
            try
            {
                // 校验商品代码不能为空
                if (string.IsNullOrEmpty(productCode))
                    return BadRequest(new { success = false, message = "商品代码不能为空" });

                // 商品报告入口统一用安全范围解析，避免普通用户解析失败时退化为全分店。
                var branchScope = await ResolveTargetBranchCodesAsync(branchCodes);
                if (!branchScope.HasAccess)
                    return Ok(CreateProductReportResponse(
                        new List<ProductBranchSalesDto>(),
                        CreateNoAccessProductStatisticStatus()
                    ));

                // 构建日期范围DTO
                var dateRange = new DateRangeDto
                {
                    StartDate = startDate,
                    EndDate = endDate,
                    CompareStartDate = compareStartDate,
                    CompareEndDate = compareEndDate,
                    CompareMode = compareMode,
                };
                var statisticStatus = await _service.GetProductReportStatisticStatusAsync(dateRange);
                if (!IsProductStatisticFresh(statisticStatus))
                {
                    return Ok(CreateProductReportResponse(
                        new List<ProductBranchSalesDto>(),
                        statisticStatus
                    ));
                }

                // 调用服务获取商品在各分店的销售数据
                var result = await _service.GetProductSalesByAllBranchesAsync(
                    dateRange,
                    productCode,
                    branchScope.BranchCodes,
                    statisticStatus
                );
                return Ok(CreateProductReportResponse(result, statisticStatus));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetProductSalesByAllBranches failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 清除销售仪表板缓存
        /// POST api/react/v1/dashboard/cache/clear
        /// </summary>
        /// <returns>操作结果</returns>
        [HttpPost("cache/clear")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ClearCache()
        {
            try
            {
                // 管理员手动清理是全量的，连按统计版本缓存的完整报表条目一起清。
                await _cacheWarmer.ClearAllCacheAsync();
                return Ok(new { success = true, message = "缓存已清空" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ClearCache failed");
                return StatusCode(500, new { success = false, message = "清除缓存失败" });
            }
        }

        /// <summary>
        /// 预热销售仪表板缓存
        /// POST api/react/v1/dashboard/cache/warmup
        /// </summary>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <returns>操作结果</returns>
        [HttpPost("cache/warmup")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> WarmUpCache(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate
        )
        {
            try
            {
                var dateRange = new DateRangeDto { StartDate = startDate, EndDate = endDate };

                await _cacheWarmer.WarmUpAsync(dateRange);
                return Ok(new { success = true, message = "缓存预热完成" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WarmUpCache failed");
                return StatusCode(500, new { success = false, message = "缓存预热失败" });
            }
        }

        /// <summary>
        /// 获取 Executive Dashboard 分店业绩排名
        /// GET api/react/v1/dashboard/executive-branch-performance
        /// </summary>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <param name="compareStartDate">对比开始日期（可选）</param>
        /// <param name="compareEndDate">对比结束日期（可选）</param>
        /// <param name="compareMode">对比模式</param>
        /// <param name="topN">返回前N条记录</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <param name="includeProductStatisticMetadata">是否附带商品报表统计完整性元数据</param>
        /// <returns>分店业绩排名列表</returns>
        [HttpGet("executive-branch-performance")]
        [Authorize(Policy = Permissions.Reports.ProductMovementView)]
        public async Task<IActionResult> GetExecutiveBranchPerformance(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] DateTime? compareStartDate = null,
            [FromQuery] DateTime? compareEndDate = null,
            [FromQuery] CompareMode compareMode = CompareMode.ByDate,
            [FromQuery] int? topN = null,
            [FromQuery] List<string>? branchCodes = null,
            [FromQuery] bool includeProductStatisticMetadata = false
        )
        {
            try
            {
                var branchScope = await ResolveTargetBranchCodesAsync(branchCodes);
                if (!branchScope.HasAccess)
                {
                    if (includeProductStatisticMetadata)
                    {
                        var statisticStatus = CreateNoAccessProductStatisticStatus();
                        return Ok(new
                        {
                            success = true,
                            data = new List<ExecutiveBranchPerformanceDto>(),
                            statisticsPending = false,
                            statisticsExpectedBranchCount = 0,
                            statisticsSnapshotBranchCount = 0,
                            statisticStatus.StatisticStatus,
                            statisticStatus.StatisticMessage,
                            statisticStatus.StatisticUpdatedAt,
                            statisticStatus.CacheVersion,
                        });
                    }

                    return Ok(new
                    {
                        success = true,
                        data = new List<ExecutiveBranchPerformanceDto>(),
                        statisticsPending = false,
                        statisticsExpectedBranchCount = 0,
                        statisticsSnapshotBranchCount = 0,
                    });
                }

                // 构建日期范围DTO
                var dateRange = new DateRangeDto
                {
                    StartDate = startDate,
                    EndDate = endDate,
                    CompareStartDate = compareStartDate,
                    CompareEndDate = compareEndDate,
                    CompareMode = compareMode,
                };

                // 商品报告分三次读取，必须确认摘要读取期间统计版本没有变化。
                var productStatusBefore = includeProductStatisticMetadata
                    ? await _service.GetProductReportStatisticStatusAsync(dateRange)
                    : null;

                // 调用服务获取 Executive 分店业绩
                var result = await _service.GetExecutiveBranchPerformanceAsync(
                    dateRange,
                    topN,
                    branchScope.BranchCodes
                );
                if (includeProductStatisticMetadata)
                {
                    // 仅商品移动端显式请求时读取商品统计状态，避免普通营业额排行多一次统计表查询。
                    var statisticStatus = await _service.GetProductReportStatisticStatusAsync(dateRange);
                    if (productStatusBefore != null && IsProductStatisticFresh(statisticStatus)
                        && (!IsProductStatisticFresh(productStatusBefore)
                            || statisticStatus.CacheVersion != productStatusBefore.CacheVersion))
                    {
                        statisticStatus.StatisticStatus = SalesStatisticRefreshStatus.Pending;
                        statisticStatus.StatisticMessage = "统计版本正在更新。";
                    }
                    return Ok(new
                    {
                        success = true,
                        data = result.Items,
                        result.StatisticsPending,
                        result.StatisticsExpectedBranchCount,
                        result.StatisticsSnapshotBranchCount,
                        statisticStatus.StatisticStatus,
                        statisticStatus.StatisticMessage,
                        statisticStatus.StatisticUpdatedAt,
                        statisticStatus.CacheVersion,
                    });
                }

                // 保持 data 数组兼容既有 Web 调用方；完整性元数据放在同一 API 包络根级。
                return Ok(new
                {
                    success = true,
                    data = result.Items,
                    result.StatisticsPending,
                    result.StatisticsExpectedBranchCount,
                    result.StatisticsSnapshotBranchCount,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetExecutiveBranchPerformance failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取 Executive Dashboard 每小时流量密度
        /// GET api/react/v1/dashboard/executive-hourly-traffic
        /// </summary>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <param name="compareStartDate">对比开始日期（可选）</param>
        /// <param name="compareEndDate">对比结束日期（可选）</param>
        /// <param name="compareMode">对比模式</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <returns>每小时流量密度列表</returns>
        [HttpGet("executive-hourly-traffic")]
        [Authorize(Policy = Permissions.Reports.View)]
        public async Task<IActionResult> GetExecutiveHourlyTraffic(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] DateTime? compareStartDate = null,
            [FromQuery] DateTime? compareEndDate = null,
            [FromQuery] CompareMode compareMode = CompareMode.ByDate,
            [FromQuery] List<string>? branchCodes = null
        )
        {
            try
            {
                var branchScope = await ResolveTargetBranchCodesAsync(branchCodes);
                if (!branchScope.HasAccess)
                    return Ok(new
                    {
                        success = true,
                        data = new List<ExecutiveHourlyTrafficDto>(),
                        statisticsPending = false,
                        statisticsExpectedItemCount = 0,
                        statisticsSnapshotItemCount = 0,
                    });

                // 构建日期范围DTO
                var dateRange = new DateRangeDto
                {
                    StartDate = startDate,
                    EndDate = endDate,
                    CompareStartDate = compareStartDate,
                    CompareEndDate = compareEndDate,
                    CompareMode = compareMode,
                };

                // 调用服务获取 Executive 每小时流量
                var result = await _service.GetExecutiveHourlyTrafficAsync(
                    dateRange,
                    branchScope.BranchCodes
                );
                // 维持 data 数组兼容，完整性元数据只在外层新增。
                return Ok(new
                {
                    success = true,
                    data = result.Items,
                    result.StatisticsPending,
                    result.StatisticsExpectedItemCount,
                    result.StatisticsSnapshotItemCount,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetExecutiveHourlyTraffic failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取分店每日营业额
        /// GET api/react/v1/dashboard/branch-daily-performance
        /// </summary>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <param name="compareStartDate">对比开始日期（可选）</param>
        /// <param name="compareEndDate">对比结束日期（可选）</param>
        /// <param name="compareMode">对比模式</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <returns>分店每日营业额列表</returns>
        [HttpGet("branch-daily-performance")]
        [Authorize(Policy = Permissions.Reports.View)]
        public async Task<IActionResult> GetBranchDailyPerformance(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] DateTime? compareStartDate = null,
            [FromQuery] DateTime? compareEndDate = null,
            [FromQuery] CompareMode compareMode = CompareMode.ByDate,
            [FromQuery] List<string>? branchCodes = null
        )
        {
            try
            {
                var branchScope = await ResolveTargetBranchCodesAsync(branchCodes);
                if (!branchScope.HasAccess)
                    return Ok(new
                    {
                        success = true,
                        data = new List<BranchDailyPerformanceDto>(),
                        statisticsPending = false,
                        statisticsExpectedItemCount = 0,
                        statisticsSnapshotItemCount = 0,
                    });

                var dateRange = new DateRangeDto
                {
                    StartDate = startDate,
                    EndDate = endDate,
                    CompareStartDate = compareStartDate,
                    CompareEndDate = compareEndDate,
                    CompareMode = compareMode,
                };

                var result = await _service.GetBranchDailyPerformanceAsync(
                    dateRange,
                    branchScope.BranchCodes
                );
                return Ok(new
                {
                    success = true,
                    data = result.Items,
                    result.StatisticsPending,
                    result.StatisticsExpectedItemCount,
                    result.StatisticsSnapshotItemCount,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetBranchDailyPerformance failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取销售统计最近成功时间和最新运行状态
        /// GET api/react/v1/dashboard/statistics-freshness
        /// </summary>
        [HttpGet("statistics-freshness")]
        [Authorize(Policy = Permissions.Reports.ProductMovementView)]
        public async Task<IActionResult> GetStatisticsFreshness()
        {
            try
            {
                var result = await _service.GetStatisticsFreshnessAsync();
                return Ok(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetStatisticsFreshness failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取周业绩层级数据
        /// GET api/react/v1/dashboard/weekly-performance-hierarchy
        /// </summary>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <param name="compareStartDate">对比开始日期（可选）</param>
        /// <param name="compareEndDate">对比结束日期（可选）</param>
        /// <param name="compareMode">对比模式</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <returns>周业绩层级数据列表</returns>
        [HttpGet("weekly-performance-hierarchy")]
        [Authorize(Policy = Permissions.Reports.View)]
        public async Task<IActionResult> GetWeeklyPerformanceHierarchy(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] DateTime? compareStartDate = null,
            [FromQuery] DateTime? compareEndDate = null,
            [FromQuery] CompareMode compareMode = CompareMode.ByDate,
            [FromQuery] List<string>? branchCodes = null
        )
        {
            try
            {
                ValidateWeeklyPerformanceDateRange(
                    startDate,
                    endDate,
                    compareStartDate,
                    compareEndDate,
                    compareMode
                );
                var branchScope = await ResolveTargetBranchCodesAsync(branchCodes);
                if (!branchScope.HasAccess)
                    return Ok(new
                    {
                        success = true,
                        data = new List<WeeklyPerformanceHierarchyDto>(),
                        statisticsPending = false,
                        statisticsExpectedItemCount = 0,
                        statisticsSnapshotItemCount = 0,
                        statisticStatus = "Fresh",
                        cacheVersion = "scope-empty",
                    });

                var dateRange = new DateRangeDto
                {
                    StartDate = startDate,
                    EndDate = endDate,
                    CompareStartDate = compareStartDate,
                    CompareEndDate = compareEndDate,
                    CompareMode = compareMode,
                };

                var result = await _service.GetWeeklyPerformanceHierarchyAsync(
                    dateRange,
                    branchScope.BranchCodes
                );
                return Ok(new
                {
                    success = true,
                    data = result.Report.Items,
                    result.Report.StatisticsPending,
                    result.Report.StatisticsExpectedItemCount,
                    result.Report.StatisticsSnapshotItemCount,
                    statisticStatus = result.Report.StatisticsPending ? "Pending" : "Fresh",
                    cacheVersion = result.CacheVersion,
                });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetWeeklyPerformanceHierarchy failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        private static void ValidateWeeklyPerformanceDateRange(
            DateTime startDate,
            DateTime endDate,
            DateTime? compareStartDate,
            DateTime? compareEndDate,
            CompareMode compareMode
        )
        {
            var currentStart = startDate.Date;
            var currentEnd = endDate.Date;
            if (currentStart > currentEnd)
                throw new ArgumentException("开始日期不能晚于结束日期");

            var currentDays = (currentEnd - currentStart).Days + 1;
            if (currentDays > 366)
                throw new ArgumentException("日期范围不能超过366天");

            if (!Enum.IsDefined(typeof(CompareMode), compareMode))
                throw new ArgumentException("对比模式无效");

            if (compareStartDate.HasValue != compareEndDate.HasValue)
                throw new ArgumentException("比较开始日期和结束日期必须同时提供");

            if (!compareStartDate.HasValue)
                return;

            var compareStart = compareStartDate.Value.Date;
            var compareEnd = compareEndDate!.Value.Date;
            if (compareStart > compareEnd)
                throw new ArgumentException("比较开始日期不能晚于比较结束日期");

            var compareDays = (compareEnd - compareStart).Days + 1;
            if (compareDays != currentDays)
                throw new ArgumentException("当前日期范围与比较日期范围必须等长");
        }

        /// <summary>
        /// 获取分店销售聚合数据
        /// GET api/react/v1/dashboard/branch-sales-aggregate
        /// 用于 SalesDetailAnalysisV2 门店分布卡，直接返回分店级别的聚合数据
        /// </summary>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <param name="compareStartDate">对比开始日期（可选）</param>
        /// <param name="compareEndDate">对比结束日期（可选）</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <param name="supplierCodes">供应商代码列表（可选，用于过滤）</param>
        /// <returns>分店销售聚合数据列表</returns>
        [HttpGet("branch-sales-aggregate")]
        public async Task<IActionResult> GetBranchSalesAggregate(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] DateTime? compareStartDate = null,
            [FromQuery] DateTime? compareEndDate = null,
            [FromQuery] List<string>? branchCodes = null,
            [FromQuery] List<string>? supplierCodes = null
        )
        {
            try
            {
                var userBranchCodes = await GetUserBranchCodesAsync();
                List<string>? targetBranchCodes = userBranchCodes;

                if (branchCodes != null && branchCodes.Any())
                {
                    if (userBranchCodes != null)
                    {
                        targetBranchCodes = branchCodes.Intersect(userBranchCodes).ToList();

                        if (!targetBranchCodes.Any())
                        {
                            return Ok(new { success = true, data = new List<object>() });
                        }
                    }
                    else
                    {
                        targetBranchCodes = branchCodes;
                    }
                }

                var dateRange = new DateRangeDto { StartDate = startDate, EndDate = endDate };

                DateRangeDto? compareDateRange = null;
                if (compareStartDate.HasValue && compareEndDate.HasValue)
                {
                    compareDateRange = new DateRangeDto
                    {
                        StartDate = compareStartDate.Value,
                        EndDate = compareEndDate.Value,
                        CompareStartDate = compareStartDate,
                        CompareEndDate = compareEndDate,
                    };
                }

                var result = await _service.GetBranchSalesAggregateAsync(
                    dateRange,
                    compareDateRange,
                    targetBranchCodes,
                    supplierCodes
                );
                return Ok(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetBranchSalesAggregate failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取 Best Sellers 商品列表（销量排名）
        /// GET api/react/v1/dashboard/best-sellers
        /// </summary>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <param name="branchCodes">分店代码列表（接口兼容参数，热销榜固定查询全平台数据，不按分店过滤）</param>
        /// <param name="pageIndex">页码（默认1）</param>
        /// <param name="pageSize">每页记录数（默认50）</param>
        /// <returns>Best Sellers 分页响应</returns>
        [HttpGet("best-sellers")]
        // [Authorize(Roles = "Admin,WarehouseManager,User")]
        public async Task<IActionResult> GetBestSellers(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] List<string>? branchCodes = null,
            [FromQuery] int pageIndex = 1,
            [FromQuery] int pageSize = 50
        )
        {
            try
            {
                // 限制页大小
                if (pageSize <= 0) pageSize = 50;
                if (pageSize > 500) pageSize = 500;
                if (pageIndex <= 0) pageIndex = 1;

                var dateRange = new DateRangeDto
                {
                    StartDate = startDate,
                    EndDate = endDate,
                };

                // 热销榜面向所有已登录用户展示全平台排名，忽略请求分店和用户门店权限范围。
                var result = await _service.GetBestSellersAsync(
                    dateRange,
                    null,
                    pageIndex,
                    pageSize
                );

                return Ok(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetBestSellers failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取商品销量分析可选供应商
        /// GET api/react/v1/dashboard/product-sales-analysis/options
        /// </summary>
        [HttpGet("product-sales-analysis/options")]
        [Authorize(Policy = Permissions.Reports.ProductMovementView)]
        public async Task<IActionResult> GetProductSalesAnalysisOptions(
            [FromQuery] ProductSalesAnalysisFilterDto filter
        )
        {
            try
            {
                if (!await HasExactProductSalesAnalysisPermissionAsync())
                    return Forbid();

                var branchScope = await ResolveProductSalesAnalysisBranchCodesAsync();
                if (!branchScope.HasAccess)
                    return Ok(CreateNoAccessProductSalesAnalysisResponse(new ProductSalesAnalysisOptionsDto()));

                var result = await _service.GetProductSalesAnalysisOptionsAsync(filter, branchScope.BranchCodes);
                return Ok(result);
            }
            catch (ProductSalesAnalysisValidationException ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetProductSalesAnalysisOptions failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        [HttpPost("product-sales-analysis/candidates")]
        [Authorize(Policy = Permissions.Reports.ProductMovementView)]
        public async Task<IActionResult> GetProductSalesAnalysisCandidates(
            [FromBody] ProductSalesAnalysisRequest request
        )
        {
            try
            {
                if (!await HasExactProductSalesAnalysisPermissionAsync())
                    return Forbid();

                var branchScope = await ResolveProductSalesAnalysisBranchCodesAsync();
                if (!branchScope.HasAccess)
                    return Ok(CreateNoAccessProductSalesAnalysisResponse(
                        new ProductSalesAnalysisPagedDto<ProductSalesProductRowDto>()
                    ));

                var result = await _service.GetProductSalesAnalysisCandidatesAsync(request, branchScope.BranchCodes);
                return Ok(result);
            }
            catch (ProductSalesAnalysisValidationException ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetProductSalesAnalysisCandidates failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        [HttpPost("product-sales-analysis/summary")]
        [Authorize(Policy = Permissions.Reports.ProductMovementView)]
        public async Task<IActionResult> GetProductSalesAnalysisSummary(
            [FromBody] ProductSalesAnalysisRequest request,
            [FromQuery] bool allowNonFreshData = false
        )
        {
            try
            {
                if (!await HasExactProductSalesAnalysisPermissionAsync())
                    return Forbid();

                var branchScope = await ResolveProductSalesAnalysisBranchCodesAsync();
                if (!branchScope.HasAccess)
                    return Ok(CreateNoAccessProductSalesAnalysisResponse(
                        new ProductSalesAnalysisPagedDto<ProductSalesProductRowDto>()
                    ));

                var result = await _service.GetProductSalesAnalysisSummaryAsync(
                    request,
                    branchScope.BranchCodes,
                    allowNonFreshData
                );
                return Ok(result);
            }
            catch (ProductSalesAnalysisValidationException ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetProductSalesAnalysisSummary failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        [HttpPost("product-sales-analysis/product-daily")]
        [Authorize(Policy = Permissions.Reports.ProductMovementView)]
        public async Task<IActionResult> GetProductSalesAnalysisProductDaily(
            [FromBody] ProductSalesAnalysisRequest request,
            [FromQuery] bool allowNonFreshData = false
        )
        {
            try
            {
                if (!await HasExactProductSalesAnalysisPermissionAsync())
                    return Forbid();

                var branchScope = await ResolveProductSalesAnalysisBranchCodesAsync();
                if (!branchScope.HasAccess)
                    return Ok(CreateNoAccessProductSalesAnalysisResponse(new List<ProductSalesDailyDto>()));

                var result = await _service.GetProductSalesAnalysisProductDailyAsync(
                    request,
                    branchScope.BranchCodes,
                    allowNonFreshData
                );
                return Ok(result);
            }
            catch (ProductSalesAnalysisValidationException ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetProductSalesAnalysisProductDaily failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        [HttpPost("product-sales-analysis/branches")]
        [Authorize(Policy = Permissions.Reports.ProductMovementView)]
        public async Task<IActionResult> GetProductSalesAnalysisBranches(
            [FromBody] ProductSalesAnalysisRequest request,
            [FromQuery] bool allowNonFreshData = false
        )
        {
            try
            {
                if (!await HasExactProductSalesAnalysisPermissionAsync())
                    return Forbid();

                var branchScope = await ResolveProductSalesAnalysisBranchCodesAsync();
                if (!branchScope.HasAccess)
                    return Ok(CreateNoAccessProductSalesAnalysisResponse(new List<ProductSalesBranchDto>()));

                var result = await _service.GetProductSalesAnalysisBranchesAsync(
                    request,
                    branchScope.BranchCodes,
                    allowNonFreshData
                );
                return Ok(result);
            }
            catch (ProductSalesAnalysisValidationException ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetProductSalesAnalysisBranches failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        [HttpPost("product-sales-analysis/branch-daily")]
        [Authorize(Policy = Permissions.Reports.ProductMovementView)]
        public async Task<IActionResult> GetProductSalesAnalysisBranchDaily(
            [FromBody] ProductSalesAnalysisRequest request,
            [FromQuery] bool allowNonFreshData = false
        )
        {
            try
            {
                if (!await HasExactProductSalesAnalysisPermissionAsync())
                    return Forbid();

                var branchScope = await ResolveProductSalesAnalysisBranchCodesAsync();
                if (!branchScope.HasAccess)
                    return Ok(CreateNoAccessProductSalesAnalysisResponse(new List<ProductSalesBranchDailyDto>()));

                var result = await _service.GetProductSalesAnalysisBranchDailyAsync(
                    request,
                    branchScope.BranchCodes,
                    allowNonFreshData
                );
                return Ok(result);
            }
            catch (ProductSalesAnalysisValidationException ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetProductSalesAnalysisBranchDaily failed");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        private static ProductReportResponseDto<T> CreateProductReportResponse<T>(
            T data,
            ProductReportStatisticStatusDto status
        )
        {
            return new ProductReportResponseDto<T>
            {
                Data = data,
                StatisticStatus = status.StatisticStatus,
                StatisticMessage = status.StatisticMessage,
                StatisticUpdatedAt = status.StatisticUpdatedAt,
                CacheVersion = status.CacheVersion,
            };
        }

        private static ProductSalesAnalysisResponse<T> CreateNoAccessProductSalesAnalysisResponse<T>(T data)
        {
            var status = CreateNoAccessProductStatisticStatus();
            return new ProductSalesAnalysisResponse<T>
            {
                Data = data,
                StatisticStatus = status.StatisticStatus,
                StatisticMessage = status.StatisticMessage,
                StatisticUpdatedAt = status.StatisticUpdatedAt,
                CacheVersion = status.CacheVersion,
            };
        }

        private static PagedSalesProductDetailWithDiscountDto CreateEmptyEnhancedSalesProductDetails(
            int pageIndex,
            int pageSize
        )
        {
            return new PagedSalesProductDetailWithDiscountDto
            {
                Data = new List<SalesProductDetailWithDiscountDto>(),
                Total = 0,
                PageIndex = pageIndex,
                PageSize = pageSize,
            };
        }

        private static ProductReportStatisticStatusDto CreateNoAccessProductStatisticStatus()
        {
            // 不调用统计读侧，既不泄露数据也不把无授权范围误报为待补算；
            // 对当前权限范围而言，空集合是完整快照，使用 Fresh 让客户端可以终止加载。
            return new ProductReportStatisticStatusDto
            {
                StatisticStatus = SalesStatisticRefreshStatus.Fresh,
                StatisticMessage = "当前账号没有可访问的分店范围",
                StatisticUpdatedAt = DateTime.UtcNow,
                CacheVersion = "no-access",
            };
        }

        private static bool IsProductStatisticFresh(ProductReportStatisticStatusDto status)
        {
            return string.Equals(
                status.StatisticStatus,
                SalesStatisticRefreshStatus.Fresh,
                StringComparison.OrdinalIgnoreCase
            );
        }
    }
}
