using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Features.StoreOrders.OrderManagement.Domain;
using BlazorApp.Api.Features.StoreOrders.OrderManagement.Infrastructure;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Features.StoreOrders.OrderManagement.Application;

internal sealed record BatchUpdateOrderLineCommand(BatchUpdateOrderLineDto? Request);

internal sealed class BatchUpdateOrderLineValidator
{
    internal StoreOrderManagementValidationResult<BatchUpdateOrderLineInput> Validate(
        BatchUpdateOrderLineCommand command
    )
    {
        if (command.Request == null)
        {
            return StoreOrderManagementValidationResult<BatchUpdateOrderLineInput>.Invalid(
                "Request is required"
            );
        }

        var items = (command.Request.Items ?? new List<BatchUpdateItemDto>())
            .Select(item =>
                new BatchUpdateOrderLineItemInput(
                    item.DetailGUID,
                    item.ProductCode,
                    item.Quantity,
                    item.ImportPrice,
                    item.SyncImportPrice == true,
                    new StoreOrderReturnLineRequest(
                        item.ReturnReason,
                        item.ReturnDisposition,
                        item.ReturnNote
                    )
                )
            )
            .ToList();
        return StoreOrderManagementValidationResult<BatchUpdateOrderLineInput>.Valid(
            new BatchUpdateOrderLineInput(command.Request.OrderGUID, items)
        );
    }
}

internal sealed class BatchUpdateOrderLineHandler(
    BatchUpdateOrderLineValidator validator,
    IStoreOrderLineCommandStore commandStore,
    IStoreOrderProductCostCoordinator productCostCoordinator,
    ILogger<BatchUpdateOrderLineHandler> logger
)
{
    internal async Task<ApiResponse<bool>> HandleAsync(BatchUpdateOrderLineCommand command)
    {
        var validation = validator.Validate(command);
        if (!validation.IsValid)
        {
            return StoreOrderManagementResponseMapper.ValidationError<bool>(
                validation.ErrorMessage!
            );
        }

        try
        {
            return StoreOrderManagementResponseMapper.Map(
                await commandStore.BatchUpdateOrderLineAsync(validation.Value!)
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "BatchUpdateOrderLineAsync failed");
            if (productCostCoordinator.IsBusyConflict(ex))
            {
                return ApiResponse<bool>.Error(
                    "套装商品正在被其他操作修改，请稍后重试",
                    productCostCoordinator.BusyErrorCode
                );
            }

            return new ApiResponse<bool> { Success = false, Message = ex.Message };
        }
    }
}
