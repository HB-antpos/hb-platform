using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Features.StoreOrders.OrderManagement.Domain;
using BlazorApp.Api.Features.StoreOrders.OrderManagement.Infrastructure;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Features.StoreOrders.OrderManagement.Application;

internal sealed record AddOrderLineCommand(AddOrderLineDto? Request);

internal sealed class AddOrderLineValidator
{
    internal StoreOrderManagementValidationResult<AddOrderLineInput> Validate(
        AddOrderLineCommand command
    )
    {
        if (command.Request == null)
        {
            return StoreOrderManagementValidationResult<AddOrderLineInput>.Invalid(
                "Request is required"
            );
        }

        return StoreOrderManagementValidationResult<AddOrderLineInput>.Valid(
            new AddOrderLineInput(
                command.Request.OrderGUID,
                command.Request.ProductCode,
                command.Request.Quantity,
                new StoreOrderReturnLineRequest(
                    command.Request.ReturnReason,
                    command.Request.ReturnDisposition,
                    command.Request.ReturnNote
                )
            )
        );
    }
}

internal sealed class AddOrderLineHandler(
    AddOrderLineValidator validator,
    IStoreOrderLineCommandStore commandStore,
    ILogger<AddOrderLineHandler> logger
)
{
    internal async Task<ApiResponse<bool>> HandleAsync(AddOrderLineCommand command)
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
                await commandStore.AddOrderLineAsync(validation.Value!)
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AddOrderLineAsync failed");
            return new ApiResponse<bool> { Success = false, Message = ex.Message };
        }
    }
}
