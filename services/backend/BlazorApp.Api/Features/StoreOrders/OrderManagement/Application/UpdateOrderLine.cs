using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Features.StoreOrders.OrderManagement.Domain;
using BlazorApp.Api.Features.StoreOrders.OrderManagement.Infrastructure;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Features.StoreOrders.OrderManagement.Application;

internal sealed record UpdateOrderLineCommand(UpdateOrderLineDto? Request);

internal sealed class UpdateOrderLineValidator
{
    internal StoreOrderManagementValidationResult<UpdateOrderLineInput> Validate(
        UpdateOrderLineCommand command
    )
    {
        if (command.Request == null)
        {
            return StoreOrderManagementValidationResult<UpdateOrderLineInput>.Invalid(
                "Request is required"
            );
        }

        return StoreOrderManagementValidationResult<UpdateOrderLineInput>.Valid(
            new UpdateOrderLineInput(
                command.Request.OrderGUID,
                command.Request.ProductCode,
                command.Request.Quantity,
                command.Request.ImportPrice,
                command.Request.SyncImportPrice == true,
                new StoreOrderReturnLineRequest(
                    command.Request.ReturnReason,
                    command.Request.ReturnDisposition,
                    command.Request.ReturnNote
                )
            )
        );
    }
}

internal sealed class UpdateOrderLineHandler(
    UpdateOrderLineValidator validator,
    IStoreOrderLineCommandStore commandStore,
    IStoreOrderProductCostCoordinator productCostCoordinator,
    ILogger<UpdateOrderLineHandler> logger
)
{
    internal async Task<ApiResponse<bool>> HandleAsync(UpdateOrderLineCommand command)
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
                await commandStore.UpdateOrderLineAsync(validation.Value!)
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "UpdateOrderLineAsync failed");
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
