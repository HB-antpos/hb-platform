using BlazorApp.Api.Filters;
using System.Globalization;
using System.Security.Claims;
using BlazorApp.Api.Features.ProductInsights;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

/// <summary>移动端仓库商品进销查询；与 Web 仓库商品流转分析共用权限与分店范围模型。</summary>
[ApiController]
[Route("api/react/v1/warehouse-product-insights")]
[Authorize(Policy = Permissions.SalesDashboard.WarehouseFlowView)]
[MaskContainerNumbers]
public sealed class WarehouseProductInsightsController(
    IWarehouseProductFlowAnalysisService service,
    IUserService userService,
    IRoleService roleService,
    ILogger<WarehouseProductInsightsController> logger,
    TimeProvider timeProvider
) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? productCode,
        [FromQuery] string? startDate,
        [FromQuery] string? endDate,
        [FromQuery] bool forceRefresh,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var normalizedProductCode = productCode?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedProductCode))
            {
                return BadRequest(
                    ApiResponse<WarehouseProductInsightDto>.Error("商品编码不能为空。", "INVALID_PRODUCT_CODE")
                );
            }

            if (!TryResolveRange(startDate, endDate, ResolveBusinessToday(), out var range, out var error))
            {
                return BadRequest(ApiResponse<WarehouseProductInsightDto>.Error(error!, "INVALID_DATE_RANGE"));
            }

            var scope = await ResolveBranchScopeAsync(cancellationToken);
            if (!scope.HasAccess)
            {
                return Forbid();
            }

            cancellationToken.ThrowIfCancellationRequested();
            var result = await service.GetProductInsightAsync(
                new WarehouseProductInsightQuery
                {
                    ProductCode = normalizedProductCode,
                    StartDate = range.StartDate,
                    EndDate = range.EndDate,
                    ForceRefresh = forceRefresh,
                },
                scope.BranchCodes
            );
            return Ok(result);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(ApiResponse<WarehouseProductInsightDto>.Error(exception.Message, "INVALID_REQUEST"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499);
        }
        catch (Exception exception)
        {
            // 查询异常必须返回失败，不能把数据源故障伪装成零进货零销售。
            logger.LogError(exception, "仓库商品进销查询失败 ProductCode={ProductCode}", productCode);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResponse<WarehouseProductInsightDto>.Error("仓库商品进销数据暂时无法读取。", "QUERY_ERROR")
            );
        }
    }

    /// <summary>
    /// 与商品分店销量查询相同的分店范围解析：全分店身份只能来自实时权限快照中的
    /// 超级管理员/仓库管理员别名，其余用户严格限授权分店。
    /// </summary>
    private async Task<(bool HasAccess, List<string>? BranchCodes)> ResolveBranchScopeAsync(
        CancellationToken cancellationToken
    )
    {
        var userGuid = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(userGuid))
        {
            return (false, []);
        }

        var snapshot = await roleService.GetUserPermissionSnapshotAsync(userGuid);
        if (snapshot?.Success != true || snapshot.Data == null)
        {
            return (false, []);
        }

        var roles = snapshot.Data.RoleNames ?? [];
        var hasAllStoreAccess = roles.Any(role =>
            Permissions.SuperAdminRoleNames.Contains(role, StringComparer.OrdinalIgnoreCase)
            || Permissions.WarehouseManagerRoleNames.Contains(role, StringComparer.OrdinalIgnoreCase)
        );
        if (hasAllStoreAccess)
        {
            return (true, null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var stores = await userService.GetUserStoresAsync(userGuid);
        if (stores?.Success != true || stores.Data == null)
        {
            return (false, []);
        }

        return (
            true,
            stores
                .Data.Select(store => store.StoreCode?.Trim())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        );
    }

    private static bool TryResolveRange(
        string? startDate,
        string? endDate,
        DateTime businessToday,
        out (DateTime StartDate, DateTime EndDate) range,
        out string? error
    )
    {
        error = null;
        range = default;
        var hasStartDate = !string.IsNullOrWhiteSpace(startDate);
        var hasEndDate = !string.IsNullOrWhiteSpace(endDate);
        if (hasStartDate != hasEndDate)
        {
            error = "开始日期和结束日期必须同时提供，或同时省略。";
            return false;
        }

        if (!hasStartDate)
        {
            range = (WarehouseProductInsightRules.DefaultStartDate(businessToday), businessToday);
            return true;
        }

        if (!TryParseBusinessDate(startDate!, out var parsedStart))
        {
            error = "开始日期必须使用 YYYY-MM-DD 格式。";
            return false;
        }
        if (!TryParseBusinessDate(endDate!, out var parsedEnd))
        {
            error = "结束日期必须使用 YYYY-MM-DD 格式。";
            return false;
        }
        if (!WarehouseProductInsightRules.IsChronological(parsedStart, parsedEnd))
        {
            error = "开始日期不能晚于结束日期。";
            return false;
        }
        // 区间上限同时由前端和后端把关；拼接 URL 绕过前端不能放大统计表扫描范围。
        if (!WarehouseProductInsightRules.IsWithinMaxRange(parsedStart, parsedEnd))
        {
            error = $"查询区间不能超过 {WarehouseProductInsightRules.MaxRangeDays} 天。";
            return false;
        }

        range = (parsedStart, parsedEnd);
        return true;
    }

    private static bool TryParseBusinessDate(string raw, out DateTime date) =>
        DateTime.TryParseExact(
            raw.Trim(),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date
        );

    /// <summary>仓库没有门店时区，业务日统一按澳洲总部时区推导，不受设备时区影响。</summary>
    private DateTime ResolveBusinessToday()
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane");
        return TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), timeZone).Date;
    }
}
