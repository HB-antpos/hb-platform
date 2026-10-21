using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Features.SupplyNotices;

namespace BlazorApp.Api.Features.StoreOrders.OrderManagement.Domain;

internal sealed record AddOrderLineInput(
    string OrderGuid,
    string ProductCode,
    decimal Quantity,
    StoreOrderReturnLineRequest ReturnRequest = default
);

internal sealed record BatchAddOrderLineInput(
    string OrderGuid,
    IReadOnlyList<AddOrderLineItemInput> Items
);

internal sealed record AddOrderLineItemInput(
    string ProductCode,
    decimal Quantity,
    decimal? ImportPrice,
    StoreOrderReturnLineRequest ReturnRequest = default
);

internal sealed record UpdateOrderLineInput(
    string OrderGuid,
    string ProductCode,
    decimal Quantity,
    decimal? ImportPrice,
    bool SyncImportPrice,
    StoreOrderReturnLineRequest ReturnRequest = default
);

internal sealed record RemoveOrderLineInput(string OrderGuid, string DetailGuid);

internal sealed record BatchUpdateOrderLineInput(
    string OrderGuid,
    IReadOnlyList<BatchUpdateOrderLineItemInput> Items
);

internal sealed record BatchUpdateOrderLineItemInput(
    string? DetailGuid,
    string ProductCode,
    decimal? Quantity,
    decimal? ImportPrice,
    bool SyncImportPrice,
    StoreOrderReturnLineRequest ReturnRequest = default
);

internal sealed record RefreshOrderLineImportPricesInput(
    string OrderGuid,
    IReadOnlyList<string> DetailGuids
);

internal sealed record RefreshOrderLineImportPricesResult(
    int UpdatedCount,
    int UnchangedCount,
    int SkippedCount,
    int MissingWarehousePriceCount
);

internal sealed record UpdateOrderHeaderInput(
    string OrderGuid,
    string? Remarks,
    decimal? ShippingFee,
    DateTime? OrderDate,
    string? StoreCode
);

internal sealed record UpdateOrderOutboundDateInput(
    string OrderGuid,
    DateTime? OutboundDate,
    bool CompleteOrder
);

internal sealed record DeleteOrderInput(string OrderGuid);

internal sealed record UpdateProductStatusInput(
    string ProductCode,
    bool IsActive,
    NormalizedSupplyNotice? SupplyNotice = null
);

internal sealed record BatchUpdateProductStatusInput(
    IReadOnlyList<string> ProductCodes,
    bool IsActive,
    NormalizedSupplyNotice? SupplyNotice = null
);

internal sealed class StoreOrderManagementResult<T>
{
    private StoreOrderManagementResult(bool success, T? data, string? errorMessage)
    {
        Success = success;
        Data = data;
        ErrorMessage = errorMessage;
    }

    internal bool Success { get; }

    internal T? Data { get; }

    internal string? ErrorMessage { get; }

    internal static StoreOrderManagementResult<T> Ok(T data) =>
        new(true, data, null);

    internal static StoreOrderManagementResult<T> Fail(string errorMessage) =>
        new(false, default, errorMessage);
}

internal sealed class StoreOrderManagementValidationResult<T>
{
    private StoreOrderManagementValidationResult(
        bool isValid,
        T? value,
        string? errorMessage
    )
    {
        IsValid = isValid;
        Value = value;
        ErrorMessage = errorMessage;
    }

    internal bool IsValid { get; }

    internal T? Value { get; }

    internal string? ErrorMessage { get; }

    internal static StoreOrderManagementValidationResult<T> Valid(T value) =>
        new(true, value, null);

    internal static StoreOrderManagementValidationResult<T> Invalid(string errorMessage) =>
        new(false, default, errorMessage);
}
