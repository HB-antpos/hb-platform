using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React;

/// <summary>
/// POS 商品管理页的供应商分类管理：概览、分类树、促销标记、重新解析。
/// </summary>
public interface ILocalSupplierCategoryReactService
{
    Task<List<LocalSupplierCategorySupplierSummaryDto>> GetSummaryAsync(CancellationToken cancellationToken = default);

    Task<List<LocalSupplierCategoryNodeDto>> GetTreeAsync(string supplierCode, CancellationToken cancellationToken = default);

    Task<LocalSupplierCategoryPromotionalResultDto> SetPromotionalAsync(
        string categoryGuid,
        bool isPromotional,
        string? actor,
        CancellationToken cancellationToken = default
    );

    Task<LocalSupplierCategoryResolveResultDto> ResolveSupplierAsync(
        string supplierCode,
        string? actor,
        CancellationToken cancellationToken = default
    );
}
