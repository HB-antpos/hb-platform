using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Features.StoreOrders.OrderPlacement.Application.Ports;
using BlazorApp.Api.Features.StoreOrders.OrderPlacement.Domain;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Features.StoreOrders.OrderPlacement.Commands.SubmitOrder;

internal sealed record SubmitOrderCommand(SubmitStoreOrderRequestDto? Request);

internal sealed class SubmitOrderValidator
{
    internal StoreOrderPlacementValidationFailure? Validate(SubmitOrderCommand command)
    {
        return command.Request == null || string.IsNullOrWhiteSpace(command.Request.StoreCode)
            ? new StoreOrderPlacementValidationFailure("StoreCode is required")
            : null;
    }
}

internal sealed class SubmitOrderHandler(
    SubmitOrderValidator validator,
    IStoreOrderCartOwnerScope ownerScope,
    IStoreOrderCartCommandCoordinator cartCoordinator,
    IStoreOrderCartPlacementPort cartPort,
    IStoreOrderPlacementGateCoordinator gateCoordinator,
    IStoreOrderPlacementExecutionContext executionContext,
    IOrderNumberGenerator orderNumberGenerator,
    ILogger<SubmitOrderHandler> logger
)
{
    private const int MaxLabelsInMessage = 10;

    internal async Task<ApiResponse<SubmitStoreOrderResultDto>> HandleAsync(
        SubmitOrderCommand command
    )
    {
        var validationFailure = validator.Validate(command);
        if (validationFailure != null)
        {
            return StoreOrderPlacementResponses.ValidationFailure<SubmitStoreOrderResultDto>(
                validationFailure.Value
            );
        }

        var request = command.Request!;
        var storeCode = request.StoreCode.Trim();
        var bypassPreorderGate = request.BypassPreorderGate
            || ownerScope.IsWarehouseStaffOnly
            || await executionContext.CanBypassPreorderCompletionAsync();
        try
        {
            return await gateCoordinator.ExecuteWithProcessGateAsync(
                storeCode,
                bypassPreorderGate,
                "React.SubmitOrder",
                async gateContext =>
                {
                    // 锁序固定为 StoreGate(process) -> Cart(process) -> Cart DB -> StoreGate DB。
                    var cartScope = ownerScope.Resolve(storeCode);
                    return await cartCoordinator.ExecuteAsync(cartScope, async () =>
                    {
                        var gateDecision = await gateCoordinator.IsBlockedInsideTransactionAsync(
                            gateContext,
                            storeCode,
                            "React.SubmitOrder"
                        );
                        if (gateDecision.IsBlocked)
                        {
                            return StoreOrderPlacementResponses.PreorderRequired<SubmitStoreOrderResultDto>(
                                "请先完成当前有效的 Preorder，再提交普通订货",
                                gateDecision.Details
                            );
                        }

                        // StoreGate 等待及数据库锁取得后才读取购物车和状态。
                        var cart = await cartPort.GetActiveForSubmissionAsync(cartScope);
                        if (cart == null)
                        {
                            return new ApiResponse<SubmitStoreOrderResultDto>
                            {
                                Success = false,
                                Message = "No active cart found",
                            };
                        }

                        var activeLineCount = await cartPort.CountActiveItemsAsync(cart.OrderGuid);
                        if (activeLineCount == 0)
                        {
                            return new ApiResponse<SubmitStoreOrderResultDto>
                            {
                                Success = false,
                                Message = "Cart is empty",
                            };
                        }

                        // 加购后才被仓库下架的商品不跟着进单：在供货的行照常提交，下架行留在购物车里
                        // 等仓库恢复供货后再提交；系统不悄悄删除，也不替换成别的商品。
                        // 仓库侧代下单不受限，此时 pausedLines 恒为空，整车按原样提交。
                        var pausedLines = await cartPort.GetSupplyPausedLinesAsync(cart.OrderGuid);
                        var keptLines = pausedLines
                            .Select(line => new SubmitStoreOrderKeptLineDto
                            {
                                DetailGUID = line.DetailGuid,
                                ProductCode = line.ProductCode,
                                ItemNumber = line.ItemNumber,
                                ProductName = line.ProductName,
                                Quantity = line.Quantity,
                                SupplyPlan = line.SupplyPlan,
                            })
                            .ToList();
                        var keptLabels = keptLines
                            .Select(line =>
                                string.IsNullOrWhiteSpace(line.ItemNumber)
                                    ? line.ProductCode
                                    : line.ItemNumber
                            )
                            .Where(label => !string.IsNullOrWhiteSpace(label))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        var discontinuedCount = keptLines.Count(line =>
                            string.Equals(
                                line.SupplyPlan,
                                WarehouseProductSupplyPlans.Discontinued,
                                StringComparison.OrdinalIgnoreCase
                            )
                        );

                        if (pausedLines.Count >= activeLineCount)
                        {
                            // 购物车里没有一行能进单：不写任何东西，把原因和货号一起告诉分店。
                            return new ApiResponse<SubmitStoreOrderResultDto>
                            {
                                Success = false,
                                ErrorCode = StoreOrderSupplyGuard.PausedErrorCode,
                                Message =
                                    $"购物车里没有可提交的商品：{keptLines.Count} 个商品都已暂停供货"
                                    + (
                                        discontinuedCount > 0
                                            ? $"，其中 {discontinuedCount} 个已不再供应，请从购物车删除"
                                            : string.Empty
                                    )
                                    + "："
                                    + FormatLabels(keptLabels),
                                Details = keptLabels,
                                Data = new SubmitStoreOrderResultDto
                                {
                                    SubmittedLineCount = 0,
                                    KeptLineCount = keptLines.Count,
                                    KeptLines = keptLines,
                                },
                            };
                        }

                        var orderNo = await orderNumberGenerator.GetNextOrderNoAsync();
                        var submittedAt = executionContext.LocalNow;
                        var actor = executionContext.ActorName;
                        StoreOrderCartSplitResult? split = null;
                        if (pausedLines.Count > 0)
                        {
                            // 先把下架行挪出去再翻状态：CAS 失败时整个事务回滚，不会留下孤儿购物车。
                            split = await cartPort.MoveLinesToNewCartAsync(
                                cartScope,
                                cart,
                                pausedLines.Select(line => line.DetailGuid).ToList(),
                                submittedAt,
                                actor
                            );
                        }

                        var affected = await cartPort.CompareExchangeSubmitAsync(
                            cart,
                            orderNo,
                            request.Remarks,
                            submittedAt,
                            actor
                        );
                        if (affected != 1)
                        {
                            return StoreOrderPlacementResponses.OrderStatusConflict<SubmitStoreOrderResultDto>();
                        }

                        var submittedLineCount = activeLineCount - pausedLines.Count;
                        return new ApiResponse<SubmitStoreOrderResultDto>
                        {
                            Success = true,
                            Message = BuildSubmittedMessage(
                                submittedLineCount,
                                keptLines.Count,
                                discontinuedCount,
                                keptLabels
                            ),
                            Data = new SubmitStoreOrderResultDto
                            {
                                OrderGUID = cart.OrderGuid,
                                OrderNo = orderNo,
                                SubmittedLineCount = submittedLineCount,
                                KeptLineCount = keptLines.Count,
                                KeptCartOrderGUID = split?.NewCartOrderGuid,
                                KeptLines = keptLines,
                            },
                        };
                    });
                }
            );
        }
        catch (PreorderBusinessException exception)
        {
            logger.LogWarning(exception, "SubmitOrder Preorder gate unavailable");
            return new ApiResponse<SubmitStoreOrderResultDto>
            {
                Success = false,
                ErrorCode = exception.ErrorCode,
                Message = exception.Message,
                Details = exception.Details,
            };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "SubmitOrderAsync failed");
            return new ApiResponse<SubmitStoreOrderResultDto>
            {
                Success = false,
                Message = "订单提交失败，请稍后重试",
            };
        }
    }

    /// <summary>
    /// 成功提交后的说明文案：旧客户端不认识结果字段时也能直接展示这段话。
    /// </summary>
    private static string BuildSubmittedMessage(
        int submittedLineCount,
        int keptLineCount,
        int discontinuedCount,
        IReadOnlyList<string> keptLabels
    )
    {
        if (keptLineCount == 0)
        {
            return $"订单已提交，共 {submittedLineCount} 行";
        }

        var message =
            $"已提交 {submittedLineCount} 行；{keptLineCount} 行商品已暂停供货，仍保留在购物车，待仓库恢复供货后可再提交";
        if (discontinuedCount > 0)
        {
            message += $"，其中 {discontinuedCount} 行已不再供应，请从购物车删除";
        }

        return message + "：" + FormatLabels(keptLabels);
    }

    private static string FormatLabels(IReadOnlyList<string> labels) =>
        string.Join("、", labels.Take(MaxLabelsInMessage))
        + (labels.Count > MaxLabelsInMessage ? " 等" : string.Empty);
}
