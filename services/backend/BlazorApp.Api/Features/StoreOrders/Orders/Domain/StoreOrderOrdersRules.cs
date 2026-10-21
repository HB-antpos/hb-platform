using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Features.StoreOrders.Orders.Domain;

internal sealed record StoreOrderDetailInput(
    string OrderGuid,
    StoreOrderDetailQueryDto Query,
    bool LoadAllItems
);

internal sealed record UpdateStoreContactInput(
    string OrderGuid,
    string StoreCode,
    string? Address,
    string? ContactEmail
);

internal sealed record BatchMapStoreOrderStoreCodeInput(
    IReadOnlyList<StoreOrderStoreCodeMappingDto> Mappings
);

internal sealed record StoreOrderOrdersValidationResult<T>(
    bool IsValid,
    T? Value,
    string? ErrorMessage
)
    where T : class
{
    internal static StoreOrderOrdersValidationResult<T> Valid(T value) =>
        new(true, value, null);

    internal static StoreOrderOrdersValidationResult<T> Invalid(string errorMessage) =>
        new(false, null, errorMessage);
}

internal sealed record StoreOrderOrdersWriteResult<T>(
    bool Success,
    T? Data,
    string? ErrorMessage,
    string? ErrorCode
)
    where T : class
{
    internal static StoreOrderOrdersWriteResult<T> Ok(T data) =>
        new(true, data, null, null);

    internal static StoreOrderOrdersWriteResult<T> Fail(
        string errorMessage,
        string? errorCode = null
    ) => new(false, null, errorMessage, errorCode);
}

internal static class StoreOrderOrdersRules
{
    internal static StoreOrderDetailQueryDto NormalizeDetailQuery(
        StoreOrderDetailQueryDto? query
    )
    {
        var pageNumber = Math.Max(
            StoreOrderDetailQueryDto.DefaultPageNumber,
            query?.PageNumber ?? StoreOrderDetailQueryDto.DefaultPageNumber
        );
        var requestedPageSize = query?.PageSize ?? StoreOrderDetailQueryDto.DefaultPageSize;
        var pageSize = Math.Clamp(
            requestedPageSize <= 0
                ? StoreOrderDetailQueryDto.DefaultPageSize
                : requestedPageSize,
            1,
            StoreOrderDetailQueryDto.MaxPageSize
        );

        return new StoreOrderDetailQueryDto
        {
            PageNumber = pageNumber,
            PageSize = pageSize,
            Keyword = NormalizeTextFilter(query?.Keyword),
            StatFilter = NormalizeTextFilter(query?.StatFilter),
            ItemNumber = NormalizeTextFilter(query?.ItemNumber),
            ProductName = NormalizeTextFilter(query?.ProductName),
            Barcode = NormalizeTextFilter(query?.Barcode),
            LocationCode = NormalizeTextFilter(query?.LocationCode),
            QuantityMin = query?.QuantityMin,
            QuantityMax = query?.QuantityMax,
            AllocQuantityMin = query?.AllocQuantityMin,
            AllocQuantityMax = query?.AllocQuantityMax,
            ImportPriceMin = query?.ImportPriceMin,
            ImportPriceMax = query?.ImportPriceMax,
            IsActive = query?.IsActive,
            SortBy = NormalizeTextFilter(query?.SortBy),
            SortDescending = query?.SortDescending ?? false,
        };
    }

    internal static ApiResponse<StoreOrderCartDto?> ToFullDetailResponse(
        ApiResponse<StoreOrderDetailDto?> result
    )
    {
        return new ApiResponse<StoreOrderCartDto?>
        {
            Success = result.Success,
            Data = result.Data == null
                ? null
                : new StoreOrderCartDto
                {
                    OrderGUID = result.Data.OrderGUID,
                    OrderNo = result.Data.OrderNo,
                    StoreCode = result.Data.StoreCode,
                    StoreName = result.Data.StoreName,
                    TotalAmount = result.Data.TotalAmount,
                    TotalQuantity = result.Data.TotalQuantity,
                    TotalImportAmount = result.Data.TotalImportAmount,
                    TotalAllocatedImportAmount = result.Data.TotalAllocatedImportAmount,
                    TotalShipImportAmount = result.Data.TotalShipImportAmount,
                    TotalReturnImportAmount = result.Data.TotalReturnImportAmount,
                    ReturnLineCount = result.Data.ReturnLineCount,
                    TotalReturnQuantity = result.Data.TotalReturnQuantity,
                    DocumentType = result.Data.DocumentType,
                    TotalVolume = result.Data.TotalVolume,
                    TotalOrderVolume = result.Data.TotalOrderVolume,
                    TotalAllocVolume = result.Data.TotalAllocVolume,
                    ShippingFee = result.Data.ShippingFee,
                    Remarks = result.Data.Remarks,
                    StoreAddress = result.Data.StoreAddress,
                    StoreContactEmail = result.Data.StoreContactEmail,
                    OrderDate = result.Data.OrderDate,
                    OutboundDate = result.Data.OutboundDate,
                    TotalAllocQuantity = result.Data.TotalAllocQuantity,
                    TotalSKU = result.Data.TotalSKU,
                    FlowStatus = result.Data.FlowStatus,
                    InvoiceEmailSentInfo = result.Data.InvoiceEmailSentInfo,
                    Items = result.Data.Items,
                },
            Message = result.Message,
        };
    }

    internal static StoreOrderOrdersValidationResult<BatchMapStoreOrderStoreCodeInput> NormalizeMappings(
        BatchMapStoreOrderStoreCodeDto? request
    )
    {
        var mappings = (request?.Mappings ?? new List<StoreOrderStoreCodeMappingDto>())
            .Select(item => new StoreOrderStoreCodeMappingDto
            {
                SourceStoreCode = item.SourceStoreCode?.Trim() ?? string.Empty,
                TargetStoreCode = item.TargetStoreCode?.Trim() ?? string.Empty,
            })
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.SourceStoreCode)
                && !string.IsNullOrWhiteSpace(item.TargetStoreCode)
            )
            .GroupBy(item => item.SourceStoreCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();

        return mappings.Count == 0
            ? StoreOrderOrdersValidationResult<BatchMapStoreOrderStoreCodeInput>.Invalid(
                "请至少选择一个需要修复的分店标识"
            )
            : StoreOrderOrdersValidationResult<BatchMapStoreOrderStoreCodeInput>.Valid(
                new BatchMapStoreOrderStoreCodeInput(mappings)
            );
    }

    internal static bool SameText(string? left, string? right)
    {
        return string.Equals(
            left?.Trim(),
            right?.Trim(),
            StringComparison.OrdinalIgnoreCase
        );
    }

    internal static string? NormalizeOptionalEmail(string? email)
    {
        return string.IsNullOrWhiteSpace(email) ? null : email.Trim();
    }

    internal static string? TrimLen(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string? NormalizeTextFilter(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
