using BlazorApp.Api.Features.StoreOrders.OrderPlacement.Commands.CopyOrder;
using BlazorApp.Api.Features.StoreOrders.OrderPlacement.Commands.CreateOrder;
using BlazorApp.Api.Features.StoreOrders.OrderPlacement.Commands.SubmitOrder;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Features.StoreOrders.OrderPlacement;

public interface IStoreOrderPlacementSlice
{
    Task<ApiResponse<SubmitStoreOrderResultDto>> SubmitOrderAsync(
        SubmitStoreOrderRequestDto request
    );

    Task<ApiResponse<string>> CreateOrderAsync(CreateStoreOrderDto request);

    Task<ApiResponse<CopyOrderResultDto>> CopyOrderAsync(CopyOrderDto request);
}

internal sealed class StoreOrderPlacementSlice(
    SubmitOrderHandler submitOrderHandler,
    CreateOrderHandler createOrderHandler,
    CopyOrderHandler copyOrderHandler
) : IStoreOrderPlacementSlice
{
    public Task<ApiResponse<SubmitStoreOrderResultDto>> SubmitOrderAsync(
        SubmitStoreOrderRequestDto request
    )
    {
        return submitOrderHandler.HandleAsync(new SubmitOrderCommand(request));
    }

    public Task<ApiResponse<string>> CreateOrderAsync(CreateStoreOrderDto request)
    {
        return createOrderHandler.HandleAsync(new CreateOrderCommand(request));
    }

    public Task<ApiResponse<CopyOrderResultDto>> CopyOrderAsync(CopyOrderDto request)
    {
        return copyOrderHandler.HandleAsync(new CopyOrderCommand(request));
    }
}
