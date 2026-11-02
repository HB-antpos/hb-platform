using System.Security.Claims;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.PosReceipts;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

/// <summary>
/// POS 订单在线小票（员工端）：看小票纸面、生成 / 重新生成客人扫码链接。
/// 权限与既有订单详情 / 发票接口一致（Orders.View），分店范围口径也一致：管理员 / 仓库经理或未分配分店的账号不受限，其余只能看授权分店。
/// </summary>
[ApiController]
[Route("api/react/v1/posm-sales-orders")]
[Authorize]
public sealed class PosmSalesOrderReceiptController : ControllerBase
{
    private readonly IPosReceiptService _service;
    private readonly SqlSugarContext _dbContext;
    private readonly ILogger<PosmSalesOrderReceiptController> _logger;

    public PosmSalesOrderReceiptController(
        IPosReceiptService service,
        SqlSugarContext dbContext,
        ILogger<PosmSalesOrderReceiptController> logger)
    {
        _service = service;
        _dbContext = dbContext;
        _logger = logger;
    }

    [HttpGet("{orderGuid}/receipt")]
    [Authorize(Policy = Permissions.Orders.View)]
    public async Task<IActionResult> GetReceipt(string orderGuid, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.GetStaffViewAsync(orderGuid, await ResolveBranchScopeAsync(), cancellationToken);
            return ToResponse(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "GetReceipt failed for {OrderGuid}", orderGuid);
            return StatusCode(500, ApiResponse<PosReceiptStaffViewDto>.Error("服务器内部错误", "QUERY_ERROR"));
        }
    }

    [HttpPost("{orderGuid}/receipt-link")]
    [Authorize(Policy = Permissions.Orders.View)]
    public async Task<IActionResult> GetOrCreateLink(string orderGuid, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.GetOrCreateLinkAsync(
                orderGuid, await ResolveBranchScopeAsync(), CurrentUserName(), cancellationToken);
            return ToResponse(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "GetOrCreateLink failed for {OrderGuid}", orderGuid);
            return StatusCode(500, ApiResponse<PosReceiptShareLinkDto>.Error("服务器内部错误", "QUERY_ERROR"));
        }
    }

    [HttpPost("{orderGuid}/receipt-link/regenerate")]
    [Authorize(Policy = Permissions.Orders.View)]
    public async Task<IActionResult> RegenerateLink(string orderGuid, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.RegenerateLinkAsync(
                orderGuid, await ResolveBranchScopeAsync(), CurrentUserName(), cancellationToken);
            return ToResponse(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "RegenerateLink failed for {OrderGuid}", orderGuid);
            return StatusCode(500, ApiResponse<PosReceiptShareLinkDto>.Error("服务器内部错误", "QUERY_ERROR"));
        }
    }

    private IActionResult ToResponse<T>(PosReceiptResult<T> result)
    {
        switch (result.Error)
        {
            case PosReceiptError.None:
                return Ok(new ApiResponse<T> { Success = true, Data = result.Data });
            case PosReceiptError.Forbidden:
                return Forbid();
            case PosReceiptError.Unsupported:
                return Ok(ApiResponse<T>.Error("该订单状态暂不支持在线小票（仅已支付 / 已退款订单）", "RECEIPT_UNSUPPORTED"));
            default:
                return Ok(ApiResponse<T>.Error("Order not found", "ORDER_NOT_FOUND"));
        }
    }

    /// <summary>null = 不限分店（管理员 / 仓库经理，或没有分配任何分店的账号）；否则是授权分店编号列表。</summary>
    private async Task<IReadOnlyCollection<string>?> ResolveBranchScopeAsync()
    {
        var isAdmin = User.Claims.Any(c =>
            c.Type == ClaimTypes.Role
            && (c.Value.Equals("Admin", StringComparison.OrdinalIgnoreCase)
                || c.Value.Equals("WarehouseManager", StringComparison.OrdinalIgnoreCase)));
        if (isAdmin)
        {
            return null;
        }

        var userGuid = User.FindFirst("userId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
        if (string.IsNullOrEmpty(userGuid))
        {
            return null;
        }

        var storeGuids = await _dbContext.Db.Queryable<UserStore>()
            .Where(us => us.UserGUID == userGuid)
            .Select(us => us.StoreGUID)
            .ToListAsync();
        if (storeGuids.Count == 0)
        {
            return null;
        }

        var codes = await _dbContext.Db.Queryable<Store>()
            .Where(s => storeGuids.Contains(s.StoreGUID))
            .Select(s => s.StoreCode)
            .ToListAsync();
        var scope = codes.Where(code => !string.IsNullOrEmpty(code)).ToList();
        return scope.Count == 0 ? null : scope;
    }

    private string? CurrentUserName() =>
        User.FindFirst(ClaimTypes.Name)?.Value ?? User.Identity?.Name;
}
