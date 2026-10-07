using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Models.DailyClose;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Services.DailyCloses;

/// <summary>
/// 日结记录后台查询：POSM 库 POSM_DailyClose 只读，门店名称/时区取自 HBweb 门店表。
/// 数据库层只做筛选、计数、求和与分页；「第 N 次」序号在内存里算，保持数据库无关（SQLite 与 SQL Server 行为一致）。
/// </summary>
internal sealed class DailyCloseQueryService : IDailyCloseQueryService
{
    /// <summary>营业日区间最长含首尾 93 个自然日。</summary>
    internal const int MaxRangeDays = 93;

    /// <summary>未给日期时默认最近 7 个自然日（含当天）。</summary>
    internal const int DefaultRangeDays = 7;

    internal const int MaxPageSize = 100;
    internal const int DefaultPageSize = 20;

    /// <summary>面额不小于 5 元（500 分）按纸币汇总，其余为硬币，与上传契约 NoteSubtotal / CoinSubtotal 口径一致。</summary>
    private const int NoteMinDenominationCents = 500;

    private static readonly string[] AdminRoleAliases = [.. Permissions.SuperAdminRoleNames];
    private static readonly string[] StoreManagerRoleAliases = ["StoreManager", "店长", "经理"];
    private static readonly string[] ClientKinds = ["Wpf", "Handheld", "Ipad"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ISqlSugarClient _posmDb;
    private readonly ISqlSugarClient? _hbwebDb;
    private readonly ICurrentUserManageableStoreScopeService _storeScopeService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<DailyCloseQueryService>? _logger;
    private readonly TimeProvider _timeProvider;

    public DailyCloseQueryService(
        ISqlSugarClient posmDb,
        ISqlSugarClient? hbwebDb,
        ICurrentUserManageableStoreScopeService storeScopeService,
        IHttpContextAccessor httpContextAccessor,
        ILogger<DailyCloseQueryService>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _posmDb = posmDb;
        _hbwebDb = hbwebDb;
        _storeScopeService = storeScopeService;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DailyCloseListResultDto> GetListAsync(
        DailyCloseQueryDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // 参数校验与权限无关，先校验，保证越权账号和有权账号对非法参数的反应一致。
        var query = Normalize(request, _timeProvider.GetUtcNow().UtcDateTime);
        var empty = new DailyCloseListResultDto { Page = query.Page, PageSize = query.PageSize };

        var access = await ResolveAccessAsync();
        if (!access.IsAllowed)
        {
            return empty;
        }

        // 请求分店与可见范围取交集；交集为空直接返回空结果，不报错也不查库。
        var stores = ResolveEffectiveStores(query.StoreCodes, access);
        if (stores is { Count: 0 })
        {
            return empty;
        }

        // SqlSugar 的 Where 会就地修改查询对象，每个计数/汇总/分页都重新构建，避免条件互相叠加。
        ISugarQueryable<PosmDailyClose> Build() => BuildFilteredQuery(query, stores);

        // 计数不受 status 影响：四类互斥且覆盖全部行（差异为 NULL / <0 / >0 / =0），all 即四者之和。
        var counts = new DailyCloseCountsDto
        {
            Short = await Build().Where(item => item.CashDifference < 0).CountAsync(cancellationToken),
            Over = await Build().Where(item => item.CashDifference > 0).CountAsync(cancellationToken),
            Even = await Build().Where(item => item.CashDifference == 0).CountAsync(cancellationToken),
            None = await Build().Where(item => item.CashDifference == null).CountAsync(cancellationToken),
        };
        counts.All = counts.Short + counts.Over + counts.Even + counts.None;

        var total = query.Status switch
        {
            DailyCloseStatusFilter.Short => counts.Short,
            DailyCloseStatusFilter.Over => counts.Over,
            DailyCloseStatusFilter.Even => counts.Even,
            DailyCloseStatusFilter.None => counts.None,
            _ => counts.All,
        };
        var result = new DailyCloseListResultDto
        {
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize,
            Counts = counts,
        };
        if (total == 0)
        {
            return result;
        }

        result.Totals = await LoadTotalsAsync(ApplyStatus(Build(), query.Status), cancellationToken);

        var skipLong = (long)(query.Page - 1) * query.PageSize;
        if (skipLong >= total)
        {
            // 页码超出范围：保留总数与汇总，条目为空。
            return result;
        }

        var rows = await ApplyStatus(Build(), query.Status)
            .OrderBy(item => item.BusinessDate, OrderByType.Desc)
            .OrderBy(item => item.SavedAtUtc, OrderByType.Desc)
            .OrderBy(item => item.Id, OrderByType.Desc)
            .Skip((int)skipLong)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        var sequences = await LoadSaveSequencesAsync(rows, cancellationToken);
        var storeInfo = await LoadStoreInfoAsync(rows.Select(row => row.StoreCode), cancellationToken);
        result.Items = rows
            .Select(row => MapListItem(row, sequences, storeInfo))
            .ToList();
        return result;
    }

    public async Task<DailyCloseDetailDto?> GetDetailAsync(
        Guid dailyCloseGuid,
        CancellationToken cancellationToken = default)
    {
        if (dailyCloseGuid == Guid.Empty)
        {
            return null;
        }

        var access = await ResolveAccessAsync();
        if (!access.IsAllowed)
        {
            return null;
        }

        var rows = await _posmDb.Queryable<PosmDailyClose>()
            .Where(item => item.DailyCloseGuid == dailyCloseGuid)
            .Take(1)
            .ToListAsync(cancellationToken);
        var row = rows.SingleOrDefault();
        // 记录不存在与无分店权限一律返回 null（控制器映射 404），不泄露记录是否存在。
        if (row is null || !access.CanAccess(row.StoreCode))
        {
            return null;
        }

        var sequences = await LoadSaveSequencesAsync([row], cancellationToken);
        var storeInfo = await LoadStoreInfoAsync([row.StoreCode], cancellationToken);
        var listItem = MapListItem(row, sequences, storeInfo);
        var isFull = string.Equals(row.DetailLevel, "Full", StringComparison.Ordinal);
        return new DailyCloseDetailDto
        {
            DailyCloseGuid = listItem.DailyCloseGuid,
            StoreCode = listItem.StoreCode,
            StoreName = listItem.StoreName,
            StoreTimeZoneId = listItem.StoreTimeZoneId,
            DeviceCode = listItem.DeviceCode,
            ClientKind = listItem.ClientKind,
            DetailLevel = listItem.DetailLevel,
            DataSource = listItem.DataSource,
            BusinessDate = listItem.BusinessDate,
            BusinessDateInferred = listItem.BusinessDateInferred,
            CashierId = listItem.CashierId,
            CashierName = listItem.CashierName,
            SavedAtUtc = listItem.SavedAtUtc,
            OrderCount = listItem.OrderCount,
            ExpectedCashAmount = listItem.ExpectedCashAmount,
            CountedCashAmount = listItem.CountedCashAmount,
            CashDifference = listItem.CashDifference,
            CardNetAmount = listItem.CardNetAmount,
            DifferenceKind = listItem.DifferenceKind,
            SaveSequence = listItem.SaveSequence,
            SaveCountInDay = listItem.SaveCountInDay,
            PeriodFromUtc = AsNullableUtc(row.PeriodFromUtc),
            PeriodToUtc = AsNullableUtc(row.PeriodToUtc),
            AppVersion = row.AppVersion,
            ReturnQuantity = row.ReturnQuantity,
            RefundAmount = row.RefundAmount,
            // 回填行（CashOnly / TraceOnly）没有付款方式与面额明细，保持空数组而不是编造 0 值。
            Tenders = isFull ? BuildTenders(row) : [],
            CashCounts = isFull ? ParseCashCounts(row.CashCountsJson, row.DailyCloseGuid) : [],
            NoteSubtotal = row.NoteSubtotal,
            CoinSubtotal = row.CoinSubtotal,
            ReceivedAtUtc = AsUtc(row.ReceivedAtUtc),
        };
    }

    /// <summary>
    /// 构建列表、计数与汇总共用的基础过滤（营业日、分店、设备、端类型、收银员关键字），不含 status、分页与排序。
    /// stores 为 null 表示不限分店（管理员且未指定分店）。
    /// </summary>
    private ISugarQueryable<PosmDailyClose> BuildFilteredQuery(
        NormalizedQuery request,
        IReadOnlyList<string>? stores)
    {
        var from = request.From;
        var to = request.To;
        var query = _posmDb.Queryable<PosmDailyClose>()
            .Where(item => item.BusinessDate >= from && item.BusinessDate <= to);

        if (stores is not null)
        {
            var storeList = stores.ToList();
            query = query.Where(item => storeList.Contains(item.StoreCode));
        }

        if (request.DeviceCode is { } deviceCode)
        {
            query = query.Where(item => item.DeviceCode == deviceCode);
        }

        if (request.ClientKind is { } clientKind)
        {
            query = query.Where(item => item.ClientKind == clientKind);
        }

        if (request.Keyword is { } keyword)
        {
            // CashierId / CashierName 均为 NOT NULL（空值以空串兜底），不需要判空。
            query = query.Where(item => item.CashierName.Contains(keyword) || item.CashierId.Contains(keyword));
        }

        return query;
    }

    private static ISugarQueryable<PosmDailyClose> ApplyStatus(
        ISugarQueryable<PosmDailyClose> query,
        DailyCloseStatusFilter status) => status switch
    {
        DailyCloseStatusFilter.Short => query.Where(item => item.CashDifference < 0),
        DailyCloseStatusFilter.Over => query.Where(item => item.CashDifference > 0),
        DailyCloseStatusFilter.Even => query.Where(item => item.CashDifference == 0),
        DailyCloseStatusFilter.None => query.Where(item => item.CashDifference == null),
        _ => query,
    };

    /// <summary>
    /// 汇总当前筛选（含 status）下现金差异非空的记录：应有现金、实点现金与差异。
    /// 简单聚合，不内嵌子查询，SQLite 与 SQL Server 都能执行。
    /// </summary>
    private async Task<DailyCloseTotalsDto> LoadTotalsAsync(
        ISugarQueryable<PosmDailyClose> filtered,
        CancellationToken cancellationToken)
    {
        var row = await filtered
            .Where(item => item.CashDifference != null)
            .Select(item => new DailyCloseTotalsRow
            {
                ExpectedCash = SqlFunc.AggregateSum(SqlFunc.IsNull(item.ExpectedCashAmount, 0m)),
                CountedCash = SqlFunc.AggregateSum(SqlFunc.IsNull(item.CountedCashAmount, 0m)),
                Difference = SqlFunc.AggregateSum(SqlFunc.IsNull(item.CashDifference, 0m)),
            })
            .FirstAsync(cancellationToken);
        return new DailyCloseTotalsDto
        {
            ExpectedCash = row?.ExpectedCash ?? 0m,
            CountedCash = row?.CountedCash ?? 0m,
            Difference = row?.Difference ?? 0m,
        };
    }

    /// <summary>
    /// 计算页内记录的「第 N 次」：同一（分店、设备、营业日）内按 SavedAtUtc 升序、Id 升序的 1 起序号。
    /// 序号只和分组内的全部记录有关，与列表上的 status / 关键字等筛选无关；先取页内涉及的分组，再另查这些分组的全部行（仅 5 个字段）在内存里排序。
    /// </summary>
    private async Task<IReadOnlyDictionary<long, SaveSequence>> LoadSaveSequencesAsync(
        IReadOnlyList<PosmDailyClose> pageRows,
        CancellationToken cancellationToken)
    {
        if (pageRows.Count == 0)
        {
            return new Dictionary<long, SaveSequence>();
        }

        var pageKeys = pageRows
            .Select(row => GroupKey(row.StoreCode, row.DeviceCode, row.BusinessDate))
            .ToHashSet(StringComparer.Ordinal);
        var minDate = pageRows.Min(row => row.BusinessDate.Date);
        var maxDate = pageRows.Max(row => row.BusinessDate.Date);
        var storeCodes = pageRows.Select(row => row.StoreCode).Distinct(StringComparer.Ordinal).ToList();
        var deviceCodes = pageRows.Select(row => row.DeviceCode).Distinct(StringComparer.Ordinal).ToList();

        // 用「日期区间 + 分店 + 设备」粗筛（避免对日期做 IN），再按精确分组键在内存里过滤。
        var candidates = await _posmDb.Queryable<PosmDailyClose>()
            .Where(item =>
                item.BusinessDate >= minDate
                && item.BusinessDate <= maxDate
                && storeCodes.Contains(item.StoreCode)
                && deviceCodes.Contains(item.DeviceCode))
            .Select(item => new SaveOrderRow
            {
                Id = item.Id,
                StoreCode = item.StoreCode,
                DeviceCode = item.DeviceCode,
                BusinessDate = item.BusinessDate,
                SavedAtUtc = item.SavedAtUtc,
            })
            .ToListAsync(cancellationToken);

        var result = new Dictionary<long, SaveSequence>();
        foreach (var group in candidates
                     .GroupBy(row => GroupKey(row.StoreCode, row.DeviceCode, row.BusinessDate), StringComparer.Ordinal)
                     .Where(group => pageKeys.Contains(group.Key)))
        {
            var ordered = group.OrderBy(row => row.SavedAtUtc).ThenBy(row => row.Id).ToList();
            for (var index = 0; index < ordered.Count; index++)
            {
                result[ordered[index].Id] = new SaveSequence(index + 1, ordered.Count);
            }
        }

        return result;
    }

    /// <summary>
    /// 门店名称与时区来自 HBweb 门店表；读取失败只记录警告，不影响列表主体（名称、时区给 null）。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, StoreInfo>> LoadStoreInfoAsync(
        IEnumerable<string> storeCodes,
        CancellationToken cancellationToken)
    {
        var codes = storeCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var result = new Dictionary<string, StoreInfo>(StringComparer.OrdinalIgnoreCase);
        if (_hbwebDb is null || codes.Count == 0)
        {
            return result;
        }

        try
        {
            var stores = await _hbwebDb.Queryable<Store>()
                .Where(store => codes.Contains(store.StoreCode) && !store.IsDeleted)
                .Select(store => new StoreInfoRow
                {
                    StoreCode = store.StoreCode,
                    StoreName = store.StoreName,
                    TimeZoneId = store.TimeZoneId,
                })
                .ToListAsync(cancellationToken);
            foreach (var store in stores)
            {
                var code = store.StoreCode.Trim();
                result.TryAdd(code, new StoreInfo(TrimToNull(store.StoreName), TrimToNull(store.TimeZoneId)));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogWarning(exception, "读取日结记录的门店名称与时区失败，列表按空名称返回");
        }

        return result;
    }

    private static DailyCloseListItemDto MapListItem(
        PosmDailyClose row,
        IReadOnlyDictionary<long, SaveSequence> sequences,
        IReadOnlyDictionary<string, StoreInfo> storeInfo)
    {
        sequences.TryGetValue(row.Id, out var sequence);
        storeInfo.TryGetValue(row.StoreCode.Trim(), out var store);
        return new DailyCloseListItemDto
        {
            DailyCloseGuid = row.DailyCloseGuid,
            StoreCode = row.StoreCode,
            StoreName = store?.Name,
            StoreTimeZoneId = store?.TimeZoneId,
            DeviceCode = row.DeviceCode,
            ClientKind = row.ClientKind,
            DetailLevel = row.DetailLevel,
            DataSource = row.DataSource,
            BusinessDate = DateOnly.FromDateTime(row.BusinessDate),
            BusinessDateInferred = row.BusinessDateInferred,
            CashierId = row.CashierId,
            CashierName = row.CashierName,
            SavedAtUtc = AsUtc(row.SavedAtUtc),
            OrderCount = row.OrderCount,
            ExpectedCashAmount = row.ExpectedCashAmount,
            CountedCashAmount = row.CountedCashAmount,
            CashDifference = row.CashDifference,
            CardNetAmount = row.CardNetAmount,
            DifferenceKind = ClassifyDifference(row.CashDifference),
            // 理论上每行都在自己的分组里；查不到（并发删除等）时按「第 1 次 / 共 1 次」兜底。
            SaveSequence = sequence?.Sequence ?? 1,
            SaveCountInDay = sequence?.Count ?? 1,
        };
    }

    /// <summary>按现金差异符号分类：负为短款、正为长款、0 持平、NULL（无金额）为 none。</summary>
    internal static string ClassifyDifference(decimal? difference) => difference switch
    {
        null => "none",
        < 0 => "short",
        > 0 => "over",
        _ => "even",
    };

    private static List<DailyCloseTenderDto> BuildTenders(PosmDailyClose row) =>
    [
        new()
        {
            Method = "Cash",
            SalesAmount = row.CashSalesAmount ?? 0m,
            RefundAmount = row.CashRefundAmount ?? 0m,
            NetAmount = row.CashNetAmount ?? 0m,
        },
        new()
        {
            Method = "Card",
            SalesAmount = row.CardSalesAmount ?? 0m,
            RefundAmount = row.CardRefundAmount ?? 0m,
            NetAmount = row.CardNetAmount ?? 0m,
        },
        new()
        {
            Method = "Voucher",
            SalesAmount = row.VoucherSalesAmount ?? 0m,
            RefundAmount = row.VoucherRefundAmount ?? 0m,
            NetAmount = row.VoucherNetAmount ?? 0m,
        },
    ];

    /// <summary>
    /// 解析面额明细 JSON（[{"denominationCents":10000,"quantity":8},...]），按面额降序输出并算小计。
    /// 单条记录的 JSON 损坏不应让整个详情失败：记录警告并返回空数组。
    /// </summary>
    private List<DailyCloseCashCountDto> ParseCashCounts(string? json, Guid dailyCloseGuid)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var items = JsonSerializer.Deserialize<List<CashCountJson>>(json, JsonOptions) ?? [];
            return items
                .Where(item => item.DenominationCents > 0 && item.Quantity >= 0)
                .OrderByDescending(item => item.DenominationCents)
                .Select(item => new DailyCloseCashCountDto
                {
                    DenominationCents = item.DenominationCents,
                    Quantity = item.Quantity,
                    SubtotalAmount = decimal.Round(
                        (decimal)item.DenominationCents * item.Quantity / 100m,
                        2,
                        MidpointRounding.AwayFromZero),
                    Kind = item.DenominationCents >= NoteMinDenominationCents ? "Note" : "Coin",
                })
                .ToList();
        }
        catch (JsonException exception)
        {
            _logger?.LogWarning(exception, "日结记录 {DailyCloseGuid} 的面额明细 JSON 无法解析，详情按空明细返回", dailyCloseGuid);
            return [];
        }
    }

    /// <summary>
    /// 解析可见分店：管理员未指定分店时返回 null（不限）；其余按可见范围与请求分店取交集（忽略大小写，保留可见范围里的写法）。
    /// 返回空列表表示交集为空。
    /// </summary>
    private static IReadOnlyList<string>? ResolveEffectiveStores(
        IReadOnlyList<string>? requested,
        StoreAccess access)
    {
        if (access.IsAdmin)
        {
            return requested is { Count: > 0 } ? requested : null;
        }

        if (requested is not { Count: > 0 })
        {
            return access.StoreCodes;
        }

        return access.StoreCodes
            .Where(code => requested.Contains(code, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// 与员工操作日志（OperationAuditQueryService）同口径：管理员看全部；店长按 UserStore 全部关联分店查看；其余账号无可见记录。
    /// </summary>
    private async Task<StoreAccess> ResolveAccessAsync()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return StoreAccess.Denied;
        }

        if (HasAnyRole(user, AdminRoleAliases))
        {
            return StoreAccess.Admin;
        }

        if (!HasAnyRole(user, StoreManagerRoleAliases))
        {
            return StoreAccess.Denied;
        }

        var scope = await _storeScopeService.GetAssignedStoreScopeAsync();
        var storeCodes = scope.StoreCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return scope.IsAllowed && storeCodes.Length > 0
            ? new StoreAccess(true, false, storeCodes)
            : StoreAccess.Denied;
    }

    private static bool HasAnyRole(ClaimsPrincipal user, IEnumerable<string> aliases) =>
        user.Claims.Any(claim =>
            claim.Type == ClaimTypes.Role
            && aliases.Any(alias => alias.Equals(claim.Value, StringComparison.OrdinalIgnoreCase)));

    /// <summary>校验并规整查询参数；缺省日期按澳洲东部「今天」推算默认区间。</summary>
    private static NormalizedQuery Normalize(DailyCloseQueryDto request, DateTime utcNow)
    {
        var fromText = TrimToNull(request.BusinessDateFrom);
        var toText = TrimToNull(request.BusinessDateTo);
        var from = fromText is null ? (DateOnly?)null : ParseDate(fromText, "businessDateFrom");
        var to = toText is null ? (DateOnly?)null : ParseDate(toText, "businessDateTo");

        // 默认窗口以悉尼「今天」为终点：门店在布里斯班/悉尼/墨尔本，悉尼日期不会落后于任何一家门店。
        var today = DateOnly.FromDateTime(ToSydneyTime(utcNow));
        if (from is null && to is null)
        {
            to = today;
            from = today.AddDays(-(DefaultRangeDays - 1));
        }
        else if (from is null)
        {
            from = to!.Value.AddDays(-(DefaultRangeDays - 1));
        }
        else if (to is null)
        {
            to = from.Value > today ? from.Value : today;
        }

        if (from!.Value > to!.Value)
        {
            throw Invalid("营业日期起始值不能晚于结束值。");
        }

        var inclusiveDays = to.Value.DayNumber - from.Value.DayNumber + 1;
        if (inclusiveDays > MaxRangeDays)
        {
            throw Invalid($"营业日期区间最长 {MaxRangeDays} 天，请缩小范围。");
        }

        if (request.Page < 1)
        {
            throw Invalid("page 必须大于或等于 1。");
        }

        if (request.PageSize is < 1 or > MaxPageSize)
        {
            throw Invalid($"pageSize 必须在 1 到 {MaxPageSize} 之间。");
        }

        var clientKindText = TrimToNull(request.ClientKind);
        string? clientKind = null;
        if (clientKindText is not null)
        {
            clientKind = ClientKinds.FirstOrDefault(kind => kind.Equals(clientKindText, StringComparison.OrdinalIgnoreCase))
                ?? throw Invalid("clientKind 只允许 Wpf、Handheld 或 Ipad。");
        }

        var statusText = TrimToNull(request.Status);
        var status = statusText?.ToLowerInvariant() switch
        {
            null or "all" => DailyCloseStatusFilter.All,
            "short" => DailyCloseStatusFilter.Short,
            "over" => DailyCloseStatusFilter.Over,
            "even" => DailyCloseStatusFilter.Even,
            "none" => DailyCloseStatusFilter.None,
            _ => throw Invalid("status 只允许 all、short、over、even 或 none。"),
        };

        var storeCodes = TrimToNull(request.StoreCodes) is { } storeText
            ? storeText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : null;

        return new NormalizedQuery
        {
            From = from.Value.ToDateTime(TimeOnly.MinValue),
            To = to.Value.ToDateTime(TimeOnly.MinValue),
            StoreCodes = storeCodes,
            DeviceCode = TrimToNull(request.DeviceCode),
            ClientKind = clientKind,
            Keyword = TrimToNull(request.Keyword),
            Status = status,
            Page = request.Page,
            PageSize = request.PageSize,
        };
    }

    private static DateOnly ParseDate(string value, string field)
    {
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw Invalid($"{field} 格式必须为 yyyy-MM-dd。");
        }

        return date;
    }

    /// <summary>UTC 转悉尼本地时间；系统缺少时区数据时退化为固定 UTC+10，只影响默认窗口终点。</summary>
    private static DateTime ToSydneyTime(DateTime utcNow)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(StoreTimeZonePolicy.Sydney);
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), zone);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return DateTime.SpecifyKind(utcNow, DateTimeKind.Utc).AddHours(10);
        }
    }

    private static DailyCloseRequestException Invalid(string message) => new("INVALID_QUERY", message);

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string GroupKey(string storeCode, string deviceCode, DateTime businessDate) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{storeCode.Trim().ToUpperInvariant()}|{deviceCode.Trim().ToUpperInvariant()}|{businessDate:yyyyMMdd}");

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static DateTime? AsNullableUtc(DateTime? value) =>
        value.HasValue ? AsUtc(value.Value) : null;

    private enum DailyCloseStatusFilter
    {
        All,
        Short,
        Over,
        Even,
        None,
    }

    private sealed class NormalizedQuery
    {
        public DateTime From { get; init; }
        public DateTime To { get; init; }
        public IReadOnlyList<string>? StoreCodes { get; init; }
        public string? DeviceCode { get; init; }
        public string? ClientKind { get; init; }
        public string? Keyword { get; init; }
        public DailyCloseStatusFilter Status { get; init; }
        public int Page { get; init; }
        public int PageSize { get; init; }
    }

    private sealed record SaveSequence(int Sequence, int Count);

    private sealed record StoreInfo(string? Name, string? TimeZoneId);

    private sealed record StoreAccess(bool IsAllowed, bool IsAdmin, IReadOnlyList<string> StoreCodes)
    {
        public static StoreAccess Denied { get; } = new(false, false, []);

        public static StoreAccess Admin { get; } = new(true, true, []);

        public bool CanAccess(string storeCode) =>
            IsAllowed
            && (IsAdmin || StoreCodes.Contains(storeCode.Trim(), StringComparer.OrdinalIgnoreCase));
    }

    private sealed class DailyCloseTotalsRow
    {
        public decimal? ExpectedCash { get; set; }
        public decimal? CountedCash { get; set; }
        public decimal? Difference { get; set; }
    }

    private sealed class SaveOrderRow
    {
        public long Id { get; set; }
        public string StoreCode { get; set; } = string.Empty;
        public string DeviceCode { get; set; } = string.Empty;
        public DateTime BusinessDate { get; set; }
        public DateTime SavedAtUtc { get; set; }
    }

    private sealed class StoreInfoRow
    {
        public string StoreCode { get; set; } = string.Empty;
        public string? StoreName { get; set; }
        public string? TimeZoneId { get; set; }
    }

    private sealed class CashCountJson
    {
        public int DenominationCents { get; set; }
        public int Quantity { get; set; }
    }
}
