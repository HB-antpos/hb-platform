using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Features.StoreOrders.OrderManagement.Domain;
using BlazorApp.Api.Features.StoreOrders.OrderManagement.Infrastructure;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Features.StoreOrders.OrderManagement.Application;

internal sealed record BatchAddOrderLineCommand(BatchAddOrderLineDto? Request);

internal sealed class BatchAddOrderLineValidator
{
    internal StoreOrderManagementValidationResult<BatchAddOrderLineInput> Validate(
        BatchAddOrderLineCommand command
    )
    {
        if (command.Request == null)
        {
            return StoreOrderManagementValidationResult<BatchAddOrderLineInput>.Invalid(
                "Request is required"
            );
        }

        var items = (command.Request.Items ?? new List<ProductQuantityDto>())
            .Select(item =>
                new AddOrderLineItemInput(
                    item.ProductCode,
                    item.Quantity,
                    item.ImportPrice,
                    new StoreOrderReturnLineRequest(
                        item.ReturnReason,
                        item.ReturnDisposition,
                        item.ReturnNote
                    )
                )
            )
            .ToList();
        return StoreOrderManagementValidationResult<BatchAddOrderLineInput>.Valid(
            new BatchAddOrderLineInput(command.Request.OrderGUID, items)
        );
    }
}

internal sealed class BatchAddOrderLineHandler(
    BatchAddOrderLineValidator validator,
    IStoreOrderLineCommandStore commandStore,
    ILogger<BatchAddOrderLineHandler> logger
)
{
    internal async Task<ApiResponse<bool>> HandleAsync(BatchAddOrderLineCommand command)
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
                await commandStore.BatchAddOrderLineAsync(validation.Value!)
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "BatchAddOrderLineAsync failed");
            return new ApiResponse<bool> { Success = false, Message = ex.Message };
        }
    }
}
