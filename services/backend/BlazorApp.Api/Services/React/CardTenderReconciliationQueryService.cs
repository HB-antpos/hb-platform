using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Models.Linkly;
using BlazorApp.Shared.DTOs;
using SqlSugar;

namespace BlazorApp.Api.Services.React;

/// <summary>卡付款对账异常的只读查询（异常由 Hbpos.Api 写入 POSM 库）。</summary>
internal sealed class CardTenderReconciliationQueryService : ICardTenderReconciliationQueryService
{
    private const int MaxPageSize = 100;
    private static readonly IReadOnlySet<string> Statuses =
        new HashSet<string>(["Open", "Resolved", "Dismissed"], StringComparer.Ordinal);
    private readonly ISqlSugarClient _db;

    public CardTenderReconciliationQueryService(POSMSqlSugarContext context)
    {
        _db = context.Db;
    }

    public async Task<PagedListReactDto<CardTenderReconciliationIssueDto>> GetListAsync(
        CardTenderReconciliationQueryDto request,
        CancellationToken cancellationToken = default)
    {
        var status = NormalizeStatus(request.Status);
        var issueType = TrimToNull(request.IssueType);
        var storeCode = TrimToNull(request.StoreCode);
        var sessionId = TrimToNull(request.SessionId);
        var orderGuid = TrimToNull(request.OrderGuid);
        var pageNumber = Math.Max(1, request.PageNumber);
        var pageSize = Math.Clamp(request.PageSize, 1, MaxPageSize);

        var query = _db.Queryable<PosmCardTenderReconciliationIssue>();
        if (status is not null)
            query = query.Where(item => item.Status == status);
        if (issueType is not null)
            query = query.Where(item => item.IssueType == issueType);
        if (storeCode is not null)
            query = query.Where(item => item.StoreCode == storeCode);
        if (sessionId is not null)
            query = query.Where(item => item.SessionId == sessionId);
        if (orderGuid is not null)
            query = query.Where(item => item.OrderGuid == orderGuid);

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderBy(item => item.LastDetectedAt, OrderByType.Desc)
            .OrderBy(item => item.Id, OrderByType.Desc)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedListReactDto<CardTenderReconciliationIssueDto>
        {
            Items = rows.Select(Map).ToList(),
            Total = total,
            PageNumber = pageNumber,
            PageSize = pageSize,
        };
    }

    private static string? NormalizeStatus(string? value)
    {
        var trimmed = TrimToNull(value);
        if (trimmed is null)
            return "Open";
        if (string.Equals(trimmed, "All", StringComparison.OrdinalIgnoreCase))
            return null;
        var match = Statuses.FirstOrDefault(status => string.Equals(status, trimmed, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new CardTenderReconciliationRequestException(
            "INVALID_STATUS",
            "状态只能是 Open、Resolved、Dismissed 或 All。");
    }

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static CardTenderReconciliationIssueDto Map(PosmCardTenderReconciliationIssue row) => new()
    {
        Id = row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        IssueType = row.IssueType,
        Severity = row.Severity,
        Source = row.Source,
        Status = row.Status,
        StoreCode = row.StoreCode,
        DeviceCode = row.DeviceCode,
        Environment = row.Environment,
        SessionId = row.SessionId,
        TxnRef = row.TxnRef,
        OrderGuid = row.OrderGuid,
        PaymentGuid = row.PaymentGuid,
        Amount = row.Amount,
        Detail = row.Detail,
        OccurrenceCount = row.OccurrenceCount,
        // 库里存的是 UTC 墙钟（DATETIME2 不带时区），显式标成 UTC，避免随机器时区漂移。
        FirstDetectedAtUtc = DateTime.SpecifyKind(row.FirstDetectedAt, DateTimeKind.Utc),
        LastDetectedAtUtc = DateTime.SpecifyKind(row.LastDetectedAt, DateTimeKind.Utc),
        ResolvedAtUtc = row.ResolvedAt is { } resolved ? DateTime.SpecifyKind(resolved, DateTimeKind.Utc) : null,
    };
}
