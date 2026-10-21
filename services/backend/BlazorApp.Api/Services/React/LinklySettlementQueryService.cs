using System.Globalization;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Models.Linkly;
using BlazorApp.Shared.DTOs;
using SqlSugar;

namespace BlazorApp.Api.Services.React;

internal sealed class LinklySettlementQueryService : ILinklySettlementQueryService
{
    private const int MaxExportRows = 5_000;
    private const int ExportBatchSize = 200;
    private const int MaxDeviceOptions = 2_000;
    private static readonly IReadOnlySet<string> ConnectionModes =
        new HashSet<string>(["LocalIp", "CloudDirectSync", "CloudBackendAsync"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> Environments =
        new HashSet<string>(["Production", "Sandbox"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> Statuses =
        new HashSet<string>(["Pending", "Unknown", "Succeeded", "Failed"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> ProviderSubmissionStates =
        new HashSet<string>(["NotSubmitted", "Submitted", "Unknown"], StringComparer.Ordinal);
    private readonly ISqlSugarClient _db;
    private readonly ILinklySettlementAmountParser _parser;
    private readonly LinklySettlementExcelExporter _excelExporter;

    public LinklySettlementQueryService(
        POSMSqlSugarContext context,
        ILinklySettlementAmountParser parser,
        LinklySettlementExcelExporter excelExporter)
    {
        _db = context.Db;
        _parser = parser;
        _excelExporter = excelExporter;
    }

    public async Task<PagedListReactDto<LinklySettlementListItemDto>> GetListAsync(
        LinklySettlementQueryDto request,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(request, export: false);
        var query = BuildFilteredQuery(normalized);
        var total = await query.CountAsync(cancellationToken);
        var skipLong = (long)(normalized.PageNumber - 1) * normalized.PageSize;
        var skip = skipLong > int.MaxValue ? int.MaxValue : (int)skipLong;

        // 关键逻辑：计数、筛选、排序和分页全部在 POSM DB 完成，之后才解析本页大字段。
        var rows = await ApplySort(query, normalized)
            .Skip(skip)
            .Take(normalized.PageSize)
            .ToListAsync(cancellationToken);
        return new PagedListReactDto<LinklySettlementListItemDto>
        {
            Items = rows.Select(MapListItem).ToList(),
            Total = total,
            PageNumber = normalized.PageNumber,
            PageSize = normalized.PageSize,
        };
    }

    public async Task<IReadOnlyList<LinklySettlementDeviceOptionDto>> GetDeviceOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        // 关键逻辑：只在库内按 门店+设备 去重取两个短字段，避免读取 SettlementData 等大字段；
        // 设备数量有限（每店几台），上限仅用于防止异常数据撑爆下拉。
        var rows = await _db.Queryable<PosmLinklySettlement>()
            .GroupBy(item => new { item.StoreCode, item.DeviceCode })
            .Select(item => new LinklySettlementDeviceOptionDto
            {
                StoreCode = item.StoreCode,
                DeviceCode = item.DeviceCode,
            })
            .ToListAsync(cancellationToken);
        return rows
            .Where(item => !string.IsNullOrWhiteSpace(item.DeviceCode))
            .OrderBy(item => item.StoreCode, StringComparer.Ordinal)
            .ThenBy(item => item.DeviceCode, StringComparer.Ordinal)
            .Take(MaxDeviceOptions)
            .ToList();
    }

    public async Task<LinklySettlementDetailDto?> GetDetailAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        if (id <= 0)
            return null;

        var rows = await _db.Queryable<PosmLinklySettlement>()
            .Where(item => item.Id == id)
            .Take(1)
            .ToListAsync(cancellationToken);
        var row = rows.SingleOrDefault();
        if (row is null)
            return null;

        var parsed = _parser.Parse(row.SettlementData);
        var listItem = MapListItem(row, parsed);
        return new LinklySettlementDetailDto
        {
            Id = listItem.Id,
            SettlementGuid = listItem.SettlementGuid,
            StoreCode = listItem.StoreCode,
            DeviceCode = listItem.DeviceCode,
            BusinessDate = listItem.BusinessDate,
            ConnectionMode = listItem.ConnectionMode,
            Environment = listItem.Environment,
            Status = listItem.Status,
            ProviderSubmissionState = listItem.ProviderSubmissionState,
            RequestedAtUtc = listItem.RequestedAtUtc,
            CompletedAtUtc = listItem.CompletedAtUtc,
            ResponseCode = listItem.ResponseCode,
            ResponseText = listItem.ResponseText,
            ReceiptCount = listItem.ReceiptCount,
            PrintCount = listItem.PrintCount,
            LastPrintError = listItem.LastPrintError,
            ReceivedAtUtc = listItem.ReceivedAtUtc,
            UpdatedAtUtc = listItem.UpdatedAtUtc,
            AmountParseStatus = listItem.AmountParseStatus,
            AmountSummary = listItem.AmountSummary,
            ProviderSessionId = row.ProviderSessionId,
            CloudBackendSessionId = row.CloudBackendSessionId?.ToString(CultureInfo.InvariantCulture),
            FirstPrintedAtUtc = AsNullableUtc(row.FirstPrintedAtUtc),
            LastPrintedAtUtc = AsNullableUtc(row.LastPrintedAtUtc),
            ClientRevision = row.ClientRevision.ToString(CultureInfo.InvariantCulture),
            CardTotals = parsed.CardTotals.ToList(),
            Receipts = LinklySettlementReceiptSanitizer.ParseAndSanitize(row.ReceiptTextsJson),
        };
    }

    public async Task<LinklySettlementExportResult> ExportAsync(
        LinklySettlementQueryDto request,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(request, export: true);
        var query = BuildFilteredQuery(normalized);
        // 先固定成员和顺序，避免分批期间新同步记录插入导致 offset 页漂移、重复或漏行。
        var snapshot = await ApplySort(query, normalized)
            .Select(item => new LinklySettlementExportSnapshot
            {
                Id = item.Id,
                ClientRevision = item.ClientRevision,
                UpdatedAtUtc = item.UpdatedAtUtc,
            })
            .Take(MaxExportRows + 1)
            .ToListAsync(cancellationToken);
        if (snapshot.Count > MaxExportRows)
            throw new LinklySettlementRequestException(
                "EXPORT_ROW_LIMIT_EXCEEDED",
                $"导出结果超过 {MaxExportRows} 行上限，请缩小筛选范围。");

        var exportRows = new List<LinklySettlementExportRow>(snapshot.Count);
        for (var offset = 0; offset < snapshot.Count; offset += ExportBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchSnapshot = snapshot
                .Skip(offset)
                .Take(Math.Min(ExportBatchSize, snapshot.Count - offset))
                .ToArray();
            var batchIds = batchSnapshot.Select(item => item.Id).ToArray();
            var batch = await _db.Queryable<PosmLinklySettlement>()
                .Where(item => batchIds.Contains(item.Id))
                .ToListAsync(cancellationToken);
            var rowsById = batch.ToDictionary(row => row.Id);

            foreach (var expected in batchSnapshot)
            {
                if (!rowsById.TryGetValue(expected.Id, out var row)
                    || row.ClientRevision != expected.ClientRevision
                    || row.UpdatedAtUtc != expected.UpdatedAtUtc)
                {
                    // 快照后删除或修改都必须整体拒绝，禁止生成跨版本拼接的工作簿。
                    throw new LinklySettlementExportChangedException();
                }

                exportRows.Add(new LinklySettlementExportRow
                {
                    Item = MapListItem(row),
                    ProviderSessionId = row.ProviderSessionId,
                    CloudBackendSessionId = row.CloudBackendSessionId?.ToString(CultureInfo.InvariantCulture),
                    ClientRevision = row.ClientRevision.ToString(CultureInfo.InvariantCulture),
                });
            }

            // 关键逻辑：映射后立即断开本批实体对两个大字段的引用，下一批前最多保留 200 份原始 payload。
            rowsById.Clear();
            batch.Clear();
        }

        return _excelExporter.Export(
            exportRows,
            DateOnly.FromDateTime(normalized.From),
            DateOnly.FromDateTime(normalized.To));
    }

    private ISugarQueryable<PosmLinklySettlement> BuildFilteredQuery(NormalizedQuery request)
    {
        var query = _db.Queryable<PosmLinklySettlement>()
            .Where(item => item.BusinessDate >= request.From && item.BusinessDate <= request.To);

        if (request.StoreCode is not null)
            query = query.Where(item => item.StoreCode == request.StoreCode);
        if (request.DeviceCode is not null)
            query = query.Where(item => item.DeviceCode == request.DeviceCode);
        if (request.ConnectionMode is not null)
            query = query.Where(item => item.ConnectionMode == request.ConnectionMode);
        if (request.Environment is not null)
            query = query.Where(item => item.Environment == request.Environment);
        if (request.Status is not null)
            query = query.Where(item => item.Status == request.Status);
        if (request.ProviderSubmissionState is not null)
            query = query.Where(item => item.ProviderSubmissionState == request.ProviderSubmissionState);

        if (request.Keyword is not null)
        {
            var keyword = request.Keyword;
            var predicate = Expressionable.Create<PosmLinklySettlement>()
                .Or(item => item.StoreCode.Contains(keyword))
                .Or(item => item.DeviceCode.Contains(keyword))
                .Or(item => item.ConnectionMode.Contains(keyword))
                .Or(item => item.Environment.Contains(keyword))
                .Or(item => item.Status.Contains(keyword))
                .Or(item => item.ProviderSubmissionState != null && item.ProviderSubmissionState.Contains(keyword))
                .Or(item => item.ProviderSessionId != null && item.ProviderSessionId.Contains(keyword))
                .Or(item => item.ResponseCode != null && item.ResponseCode.Contains(keyword))
                .Or(item => item.ResponseText != null && item.ResponseText.Contains(keyword))
                .Or(item => item.LastPrintError != null && item.LastPrintError.Contains(keyword));
            if (Guid.TryParse(keyword, out var settlementGuid))
                predicate = predicate.Or(item => item.SettlementGuid == settlementGuid);
            if (long.TryParse(keyword, NumberStyles.None, CultureInfo.InvariantCulture, out var sessionId))
                predicate = predicate.Or(item => item.CloudBackendSessionId == sessionId);
            query = query.Where(predicate.ToExpression());
        }

        return query;
    }

    private static ISugarQueryable<PosmLinklySettlement> ApplySort(
        ISugarQueryable<PosmLinklySettlement> query,
        NormalizedQuery request)
    {
        var order = request.Ascending ? OrderByType.Asc : OrderByType.Desc;
        return request.SortBy switch
        {
            SortField.Id => query.OrderBy(item => item.Id, order),
            SortField.SettlementGuid => query.OrderBy(item => item.SettlementGuid, order).OrderBy(item => item.Id, OrderByType.Desc),
            SortField.StoreCode => query.OrderBy(item => item.StoreCode, order).OrderBy(item => item.Id, OrderByType.Desc),
            SortField.DeviceCode => query.OrderBy(item => item.DeviceCode, order).OrderBy(item => item.Id, OrderByType.Desc),
            SortField.BusinessDate => query.OrderBy(item => item.BusinessDate, order).OrderBy(item => item.Id, OrderByType.Desc),
            SortField.ConnectionMode => query.OrderBy(item => item.ConnectionMode, order).OrderBy(item => item.Id, OrderByType.Desc),
            SortField.Environment => query.OrderBy(item => item.Environment, order).OrderBy(item => item.Id, OrderByType.Desc),
            SortField.Status => query.OrderBy(item => item.Status, order).OrderBy(item => item.Id, OrderByType.Desc),
            SortField.ProviderSubmissionState => query.OrderBy(item => item.ProviderSubmissionState, order).OrderBy(item => item.Id, OrderByType.Desc),
            SortField.CompletedAtUtc => query.OrderBy(item => item.CompletedAtUtc, order).OrderBy(item => item.Id, OrderByType.Desc),
            SortField.ResponseCode => query.OrderBy(item => item.ResponseCode, order).OrderBy(item => item.Id, OrderByType.Desc),
            SortField.ResponseText => query.OrderBy(item => item.ResponseText, order).OrderBy(item => item.Id, OrderByType.Desc),
            SortField.PrintCount => query.OrderBy(item => item.PrintCount, order).OrderBy(item => item.Id, OrderByType.Desc),
            SortField.LastPrintError => query.OrderBy(item => item.LastPrintError, order).OrderBy(item => item.Id, OrderByType.Desc),
            SortField.ReceivedAtUtc => query.OrderBy(item => item.ReceivedAtUtc, order).OrderBy(item => item.Id, OrderByType.Desc),
            SortField.UpdatedAtUtc => query.OrderBy(item => item.UpdatedAtUtc, order).OrderBy(item => item.Id, OrderByType.Desc),
            _ => query.OrderBy(item => item.RequestedAtUtc, order).OrderBy(item => item.Id, OrderByType.Desc),
        };
    }

    private LinklySettlementListItemDto MapListItem(PosmLinklySettlement row) =>
        MapListItem(row, _parser.Parse(row.SettlementData));

    private static LinklySettlementListItemDto MapListItem(
        PosmLinklySettlement row,
        LinklySettlementAmountParseResult parsed) => new()
    {
        Id = row.Id.ToString(CultureInfo.InvariantCulture),
        SettlementGuid = row.SettlementGuid,
        StoreCode = row.StoreCode,
        DeviceCode = row.DeviceCode,
        BusinessDate = DateOnly.FromDateTime(row.BusinessDate),
        ConnectionMode = row.ConnectionMode,
        Environment = row.Environment,
        Status = row.Status,
        ProviderSubmissionState = row.ProviderSubmissionState,
        RequestedAtUtc = AsUtc(row.RequestedAtUtc),
        CompletedAtUtc = AsNullableUtc(row.CompletedAtUtc),
        ResponseCode = row.ResponseCode,
        ResponseText = row.ResponseText,
        ReceiptCount = LinklySettlementReceiptSanitizer.Count(row.ReceiptTextsJson),
        PrintCount = row.PrintCount,
        LastPrintError = row.LastPrintError,
        ReceivedAtUtc = AsUtc(row.ReceivedAtUtc),
        UpdatedAtUtc = AsUtc(row.UpdatedAtUtc),
        AmountParseStatus = parsed.Status.ToString(),
        AmountSummary = parsed.Summary,
    };

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static DateTime? AsNullableUtc(DateTime? value) =>
        value.HasValue ? AsUtc(value.Value) : null;

    private static NormalizedQuery Normalize(LinklySettlementQueryDto request, bool export)
    {
        ArgumentNullException.ThrowIfNull(request);
        var from = ParseDate(request.BusinessDateFrom, nameof(request.BusinessDateFrom));
        var to = ParseDate(request.BusinessDateTo, nameof(request.BusinessDateTo));
        if (from > to)
            throw Invalid("营业日期起始值不能晚于结束值。");

        var inclusiveDays = to.DayNumber - from.DayNumber + 1;
        var maximumDays = export ? 31 : 366;
        if (inclusiveDays > maximumDays)
            throw Invalid($"营业日期范围最多允许 {maximumDays} 个含首尾自然日。");
        if (request.PageNumber < 1)
            throw Invalid("pageNumber 必须大于或等于 1。");
        if (request.PageSize is < 1 or > 200)
            throw Invalid("pageSize 必须在 1 到 200 之间。");

        var sortByText = TrimToNull(request.SortBy);
        var sortOrderText = TrimToNull(request.SortOrder);
        if (sortByText is null && sortOrderText is not null)
            throw Invalid("提供 sortOrder 时必须同时提供 sortBy。");

        var sortBy = sortByText is null ? SortField.RequestedAtUtc : ParseSortField(sortByText);
        var ascending = sortOrderText?.ToLowerInvariant() switch
        {
            null => false,
            "asc" => true,
            "desc" => false,
            _ => throw Invalid("sortOrder 只允许 asc 或 desc。"),
        };

        var connectionMode = ValidateFilter(
            request.ConnectionMode,
            "connectionMode",
            ConnectionModes);
        var environment = ValidateFilter(request.Environment, "environment", Environments);
        var status = ValidateFilter(request.Status, "status", Statuses);
        var providerSubmissionState = ValidateFilter(
            request.ProviderSubmissionState,
            "providerSubmissionState",
            ProviderSubmissionStates);

        return new NormalizedQuery
        {
            From = from.ToDateTime(TimeOnly.MinValue),
            To = to.ToDateTime(TimeOnly.MaxValue),
            StoreCode = TrimToNull(request.StoreCode),
            DeviceCode = TrimToNull(request.DeviceCode),
            ConnectionMode = connectionMode,
            Environment = environment,
            Status = status,
            ProviderSubmissionState = providerSubmissionState,
            Keyword = TrimToNull(request.Keyword),
            SortBy = sortBy,
            Ascending = ascending,
            PageNumber = request.PageNumber,
            PageSize = request.PageSize,
        };
    }

    private static DateOnly ParseDate(string? value, string field)
    {
        if (value is null
            || !DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
            throw Invalid($"{field} 为必填项，格式必须为 yyyy-MM-dd。");
        return date;
    }

    private static SortField ParseSortField(string value) => value.ToLowerInvariant() switch
    {
        "id" => SortField.Id,
        "settlementguid" => SortField.SettlementGuid,
        "storecode" => SortField.StoreCode,
        "devicecode" => SortField.DeviceCode,
        "businessdate" => SortField.BusinessDate,
        "connectionmode" => SortField.ConnectionMode,
        "environment" => SortField.Environment,
        "status" => SortField.Status,
        "providersubmissionstate" => SortField.ProviderSubmissionState,
        "requestedatutc" => SortField.RequestedAtUtc,
        "completedatutc" => SortField.CompletedAtUtc,
        "responsecode" => SortField.ResponseCode,
        "responsetext" => SortField.ResponseText,
        "printcount" => SortField.PrintCount,
        "lastprinterror" => SortField.LastPrintError,
        "receivedatutc" => SortField.ReceivedAtUtc,
        "updatedatutc" => SortField.UpdatedAtUtc,
        _ => throw Invalid("sortBy 不在允许的排序字段白名单中。"),
    };

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ValidateFilter(
        string? value,
        string field,
        IReadOnlySet<string> allowed)
    {
        var normalized = TrimToNull(value);
        if (normalized is null)
            return null;
        if (!allowed.Contains(normalized))
            throw Invalid($"{field} 不在允许值白名单中。");
        return normalized;
    }

    private static LinklySettlementRequestException Invalid(string message) =>
        new("INVALID_QUERY", message);

    private enum SortField
    {
        Id,
        SettlementGuid,
        StoreCode,
        DeviceCode,
        BusinessDate,
        ConnectionMode,
        Environment,
        Status,
        ProviderSubmissionState,
        RequestedAtUtc,
        CompletedAtUtc,
        ResponseCode,
        ResponseText,
        PrintCount,
        LastPrintError,
        ReceivedAtUtc,
        UpdatedAtUtc,
    }

    private sealed class NormalizedQuery
    {
        public DateTime From { get; init; }
        public DateTime To { get; init; }
        public string? StoreCode { get; init; }
        public string? DeviceCode { get; init; }
        public string? ConnectionMode { get; init; }
        public string? Environment { get; init; }
        public string? Status { get; init; }
        public string? ProviderSubmissionState { get; init; }
        public string? Keyword { get; init; }
        public SortField SortBy { get; init; }
        public bool Ascending { get; init; }
        public int PageNumber { get; init; }
        public int PageSize { get; init; }
    }
}
