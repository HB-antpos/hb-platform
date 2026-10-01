using BlazorApp.Api.Data;
using BlazorApp.Api.Features.StoreOrders.OrderPlacement.Application.Ports;
using BlazorApp.Api.Features.StoreOrders.OrderPlacement.Domain;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using SqlSugar;

namespace BlazorApp.Api.Features.StoreOrders.OrderPlacement.Infrastructure;

internal sealed class StoreOrderPlacementGateCoordinator(
    SqlSugarContext context,
    ILogger<StoreOrderPlacementGateCoordinator> logger,
    IPreorderGateService? preorderGateService = null
) : IStoreOrderPlacementGateCoordinator
{
    private readonly ISqlSugarClient _db = context.Db;

    public async Task<ApiResponse<T>> ExecuteWithProcessGateAsync<T>(
        string storeCode,
        bool bypassPreorderGate,
        string entryPoint,
        Func<StoreOrderPlacementGateContext, Task<ApiResponse<T>>> command,
        CancellationToken requestAborted = default
    )
    {
        if (bypassPreorderGate)
        {
            return await command(new StoreOrderPlacementGateContext(null));
        }

        var resource = await PreorderGateEvaluator
            .ResolveStoreLockResourceForOrdinaryOrderWriteAsync(
                _db,
                storeCode,
                entryPoint,
                logger,
                requestAborted
            );
        if (resource == null)
        {
            // 仅 PREORDER_GATE_UNAVAILABLE 沿用普通订单既有 fail-open 契约。
            return await command(new StoreOrderPlacementGateContext(null));
        }

        // 直接复用 PreorderMutationLock；不得复制其 lowercase/distinct/sorted 规范。
        await using var storeLock = await PreorderMutationLock.AcquireProcessAsync(resource);
        return await command(new StoreOrderPlacementGateContext(resource));
    }

    public async Task<StoreOrderPlacementGateDecision> IsBlockedInsideTransactionAsync(
        StoreOrderPlacementGateContext context,
        string storeCode,
        string entryPoint,
        CancellationToken requestAborted = default
    )
    {
        if (!context.RequiresEvaluation)
        {
            return new StoreOrderPlacementGateDecision(false);
        }

        var evaluation = await PreorderGateEvaluator
            .EvaluateLockedForOrdinaryOrderWriteAsync(
                _db,
                context.StoreLockResource!,
                storeCode,
                TimeProvider.System,
                entryPoint,
                logger,
                requestAborted
            );
        if (evaluation?.IsBlocked != true)
        {
            return new StoreOrderPlacementGateDecision(false);
        }

        // 锁内判断是最终裁决；详情读取只负责恢复原 HTTP 错误 DTO，不能反向放行。
        var details = new PreorderGateResult
        {
            IsBlocked = true,
            PendingCount = evaluation.PendingActivations.Count,
        };
        if (preorderGateService != null)
        {
            try
            {
                var enriched = await preorderGateService.CheckAsync(storeCode);
                if (enriched.IsBlocked)
                {
                    details = enriched;
                }
            }
            catch (PreorderBusinessException exception)
            {
                logger.LogWarning(
                    exception,
                    "Preorder gate details unavailable after locked decision for {StoreCode}",
                    storeCode
                );
            }
        }

        return new StoreOrderPlacementGateDecision(true, details);
    }
}
