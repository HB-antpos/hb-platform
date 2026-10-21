using BlazorApp.Api.Models.Linkly;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React;

public interface ILinklySettlementQueryService
{
    Task<PagedListReactDto<LinklySettlementListItemDto>> GetListAsync(
        LinklySettlementQueryDto request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LinklySettlementDeviceOptionDto>> GetDeviceOptionsAsync(
        CancellationToken cancellationToken = default);

    Task<LinklySettlementDetailDto?> GetDetailAsync(
        long id,
        CancellationToken cancellationToken = default);

    Task<LinklySettlementExportResult> ExportAsync(
        LinklySettlementQueryDto request,
        CancellationToken cancellationToken = default);
}
