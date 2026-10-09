using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React;

public interface ICardTenderReconciliationQueryService
{
    Task<PagedListReactDto<CardTenderReconciliationIssueDto>> GetListAsync(
        CardTenderReconciliationQueryDto request,
        CancellationToken cancellationToken = default);
}

/// <summary>请求参数不合法（状态值未知等）。</summary>
public sealed class CardTenderReconciliationRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
