using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Features.StoreOrders.Sync;
using BlazorApp.Api.Filters;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React.StoreOrders;

/// <summary>
/// 分店订货单 HQ → 本地 WareHouseOrder 同步。2026-09-21 只读核查时 HQ 分店订货单主表最后变更停在 2026-06-21
/// （订货业务已迁到 HBweb），2026-09-29 起四个发起同步的入口统一返回 410；两个查询 job 状态的只读接口保留。
/// </summary>
[ApiController]
[Route(StoreOrderControllerBase.BaseRoute)]
[Authorize]
public sealed class StoreOrderSyncController(
    IStoreOrderMissingOrdersSyncExecutor missingOrdersSyncExecutor,
    IStoreOrderSyncJobService syncJobService,
    IStoreOrderAccessPolicy accessPolicy,
    ILogger<StoreOrderSyncController> logger
) : StoreOrderControllerBase(accessPolicy)
{
    /// <summary>
    /// 从 HQ 同步本地不存在的仓库订单（主表 + 明细表）
    /// </summary>
    [HttpPost("sync-missing-orders")]
    [HqToHbwebSyncDisabled]
    public async Task<IActionResult> SyncMissingOrders(
        [FromBody] SyncMissingOrdersRequestDto? request
    )
    {
        try
        {
            var forbidden = ForbidIf(await AccessPolicy.RequireWarehouseSyncAsync());
            if (forbidden != null)
            {
                return forbidden;
            }

            var scope = await AccessPolicy.ResolveMissingOrdersSyncScopeAsync(
                request?.StoreCodes,
                request?.StoreCode
            );
            if (scope.IsForbidden)
            {
                return Forbid();
            }

            var scopedRequest = ApplyMissingOrdersScope(request, scope);
            var result = await missingOrdersSyncExecutor.SyncMissingOrdersFromHqAsync(
                scopedRequest
            );
            return Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SyncMissingOrders failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// 创建分店订货缺失订单同步 job。
    /// </summary>
    [HttpPost("sync-missing-orders/jobs")]
    [HqToHbwebSyncDisabled]
    public async Task<IActionResult> CreateSyncMissingOrdersJob(
        [FromBody] SyncMissingOrdersRequestDto? request
    )
    {
        try
        {
            var forbidden = ForbidIf(await AccessPolicy.RequireWarehouseSyncAsync());
            if (forbidden != null)
            {
                return forbidden;
            }

            var scope = await AccessPolicy.ResolveMissingOrdersSyncScopeAsync(
                request?.StoreCodes,
                request?.StoreCode
            );
            if (scope.IsForbidden)
            {
                return Forbid();
            }

            var scopedRequest = ApplyMissingOrdersScope(request, scope);
            var userId = AccessPolicy.GetCurrentUserId();
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Unauthorized(new { success = false, message = "未获取到当前用户" });
            }

            var job = await syncJobService.StartJobAsync(
                userId,
                scopedRequest,
                HttpContext.RequestAborted
            );
            return Ok(new { success = true, data = job });
        }
        catch (Exception ex)
        {
            if (ex is InvalidOperationException invalidOperation
                && invalidOperation.Message.Contains(
                    "已有分店订货同步任务",
                    StringComparison.Ordinal
                ))
            {
                return Conflict(new { success = false, message = invalidOperation.Message });
            }

            logger.LogError(ex, "CreateSyncMissingOrdersJob failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// 获取分店订货缺失订单同步 job 状态。
    /// </summary>
    [HttpGet("sync-missing-orders/jobs/{jobId}")]
    public async Task<IActionResult> GetSyncMissingOrdersJob(string jobId)
    {
        try
        {
            var forbidden = ForbidIf(await AccessPolicy.RequireWarehouseSyncAsync());
            if (forbidden != null)
            {
                return forbidden;
            }

            var job = await syncJobService.GetJobAsync(
                jobId,
                HttpContext.RequestAborted
            );
            if (job == null)
            {
                return NotFound(new { success = false, message = "任务不存在" });
            }

            forbidden = ForbidIf(
                await AccessPolicy.RequireScopedJobStoresAsync(job.StoreCodes)
            );
            if (forbidden != null)
            {
                return forbidden;
            }

            return Ok(new { success = true, data = job });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetSyncMissingOrdersJob failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// 创建分店订货 HQ 全量同步 job。全量同步只允许真实 Admin，且忽略前端筛选条件。
    /// </summary>
    [HttpPost("hq-sync/full/jobs")]
    [HqToHbwebSyncDisabled]
    public async Task<IActionResult> CreateStoreOrderHqFullSyncJob(
        [FromBody] StoreOrderHqSyncRequestDto? request
    )
    {
        try
        {
            var forbidden = ForbidIf(AccessPolicy.RequireRealAdmin());
            if (forbidden != null)
            {
                return forbidden;
            }

            var userId = AccessPolicy.GetCurrentUserId();
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Unauthorized(new { success = false, message = "未获取到当前用户" });
            }

            // 全量同步是全库行为，服务端主动丢弃页面分店/客户筛选。
            var job = await syncJobService.StartHqSyncJobAsync(
                userId,
                StoreOrderHqSyncMode.Full,
                new StoreOrderHqSyncRequestDto(),
                HttpContext.RequestAborted
            );
            return Ok(new { success = true, data = job });
        }
        catch (Exception ex)
        {
            if (ex is InvalidOperationException invalidOperation
                && invalidOperation.Message.Contains(
                    "已有分店订货同步任务",
                    StringComparison.Ordinal
                ))
            {
                return Conflict(new { success = false, message = invalidOperation.Message });
            }

            logger.LogError(ex, "CreateStoreOrderHqFullSyncJob failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// 创建分店订货 HQ 增量同步 job。
    /// </summary>
    [HttpPost("hq-sync/incremental/jobs")]
    [HqToHbwebSyncDisabled]
    public async Task<IActionResult> CreateStoreOrderHqIncrementalSyncJob(
        [FromBody] StoreOrderHqSyncRequestDto? request
    )
    {
        try
        {
            var forbidden = ForbidIf(await AccessPolicy.RequireWarehouseSyncAsync());
            if (forbidden != null)
            {
                return forbidden;
            }

            var scope = await AccessPolicy.ResolveHqIncrementalSyncScopeAsync(
                request?.StoreCodes,
                request?.StoreCode
            );
            if (scope.IsForbidden)
            {
                return Forbid();
            }

            var scopedRequest = ApplyHqIncrementalScope(request, scope);
            var userId = AccessPolicy.GetCurrentUserId();
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Unauthorized(new { success = false, message = "未获取到当前用户" });
            }

            var job = await syncJobService.StartHqSyncJobAsync(
                userId,
                StoreOrderHqSyncMode.Incremental,
                scopedRequest,
                HttpContext.RequestAborted
            );
            return Ok(new { success = true, data = job });
        }
        catch (Exception ex)
        {
            if (ex is InvalidOperationException invalidOperation
                && invalidOperation.Message.Contains(
                    "已有分店订货同步任务",
                    StringComparison.Ordinal
                ))
            {
                return Conflict(new { success = false, message = invalidOperation.Message });
            }

            logger.LogError(ex, "CreateStoreOrderHqIncrementalSyncJob failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// 获取分店订货 HQ 同步 job 状态。
    /// </summary>
    [HttpGet("hq-sync/jobs/{jobId}")]
    public async Task<IActionResult> GetStoreOrderHqSyncJob(string jobId)
    {
        try
        {
            var forbidden = ForbidIf(await AccessPolicy.RequireWarehouseSyncAsync());
            if (forbidden != null)
            {
                return forbidden;
            }

            var job = await syncJobService.GetJobAsync(
                jobId,
                HttpContext.RequestAborted
            );
            if (job == null)
            {
                return NotFound(new { success = false, message = "任务不存在" });
            }

            if (
                string.Equals(
                    job.Mode,
                    StoreOrderHqSyncMode.Full.ToString(),
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return AccessPolicy.IsRealAdmin()
                    ? Ok(new { success = true, data = job })
                    : Forbid();
            }

            forbidden = ForbidIf(
                await AccessPolicy.RequireScopedJobStoresAsync(job.StoreCodes)
            );
            if (forbidden != null)
            {
                return forbidden;
            }

            return Ok(new { success = true, data = job });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetStoreOrderHqSyncJob failed");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    private static SyncMissingOrdersRequestDto? ApplyMissingOrdersScope(
        SyncMissingOrdersRequestDto? request,
        StoreOrderStoreSelectionDecision scope
    )
    {
        if (scope.PreserveRequestedSelection)
        {
            return request;
        }

        var scopedRequest = request ?? new SyncMissingOrdersRequestDto();
        scopedRequest.StoreCodes = scope.ScopedStoreCodes?.ToList() ?? new List<string>();
        return scopedRequest;
    }

    private static StoreOrderHqSyncRequestDto? ApplyHqIncrementalScope(
        StoreOrderHqSyncRequestDto? request,
        StoreOrderStoreSelectionDecision scope
    )
    {
        if (scope.PreserveRequestedSelection)
        {
            return request;
        }

        var scopedRequest = request ?? new StoreOrderHqSyncRequestDto();
        scopedRequest.StoreCodes = scope.ScopedStoreCodes?.ToList() ?? new List<string>();
        return scopedRequest;
    }
}
