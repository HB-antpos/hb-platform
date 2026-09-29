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

    /// <summary>
    /// 对所有有采集记录或归属记录的供应商（不含 200）逐个重新归类；供每晚定时任务调用。
    /// </summary>
    Task<LocalSupplierCategoryNightlyResolveResultDto> ResolveAllSuppliersAsync(
        string? actor,
        CancellationToken cancellationToken = default
    );
}
