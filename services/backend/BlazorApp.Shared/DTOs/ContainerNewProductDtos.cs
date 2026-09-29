namespace BlazorApp.Shared.DTOs;

public sealed class ContainerNewProductsResponseDto
{
    public string StoreCode { get; init; } = string.Empty;
    public string StateCode { get; init; } = string.Empty;
    public IReadOnlyList<ContainerNewProductItemDto> Items { get; init; } = [];
}

public sealed class ContainerNewProductItemDto
{
    public string ProductCode { get; init; } = string.Empty;
    public string? ImageUrl { get; init; }
    public string ContainerCode { get; init; } = string.Empty;
    public string? ContainerNumber { get; init; }
    public DateOnly EstimatedStoreArrivalDate { get; init; }
    public string Basis { get; init; } = string.Empty;
}
