using System.Collections.Concurrent;
using System.Globalization;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using MimeKit;
using SqlSugar;

namespace BlazorApp.Api.Services.Myob;

public interface IMyobPayslipService
{
    /// <summary>当前账号可见的公司文件（只列状态 Active 的连接；仅「管辖分店」口径时只列与自己管辖分店有关的公司）。</summary>
    Task<ApiResponse<List<MyobPayslipCompanyDto>>> GetCompaniesAsync(CancellationToken cancellationToken);

    Task<ApiResponse<MyobPayslipListDto>> ListAsync(MyobPayslipQuery query, CancellationToken cancellationToken);

    /// <summary>单张工资单明细；每次成功打开写一条查看记录（只记谁看了谁哪一期，不含金额）。</summary>
    Task<ApiResponse<MyobPayslipDetailDto>> GetDetailAsync(MyobPayslipDetailRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// 这张工资单对应周期里「我方考勤」按分店的计薪工时（仅供参考，按需加载）。权限与归类和明细完全一致（重新取这张工资单再判断），
    /// 但不写查看记录：它和打开明细属于同一次查看。
    /// </summary>
    Task<ApiResponse<MyobPayslipHoursDto>> GetHoursAsync(MyobPayslipDetailRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// 生成 MYOB 默认样式的工资单 PDF。需要 Myob.Payslip.Download，且看得到这张工资单（与明细同一套范围判断）；先写「Download」查看记录再返回文件。
    /// </summary>
    Task<ApiResponse<MyobPayslipPdfFile>> DownloadPdfAsync(MyobPayslipDetailRequest request, CancellationToken cancellationToken);

    /// <summary>邮件弹窗预填：员工档案邮箱（脱敏另给）、主题、文件名、当前登录人邮箱。需要 Myob.Payslip.Email。不写查看记录。</summary>
    Task<ApiResponse<MyobPayslipEmailTargetDto>> GetEmailTargetAsync(MyobPayslipDetailRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// 把工资单 PDF 发给员工。需要 Myob.Payslip.Email；每次一个收件人；档案邮箱存在且收件人不同需 RecipientChangeConfirmed。
    /// 先写「Email」查看记录（含收件邮箱）再发送，发送失败会把这条记录标成 EmailFailed。
    /// </summary>
    Task<ApiResponse<MyobPayslipEmailResultDto>> SendEmailAsync(MyobPayslipEmailRequest request, CancellationToken cancellationToken);
}

/// <summary>生成好的 PDF 文件。</summary>
public sealed record MyobPayslipPdfFile(byte[] Content, string Filename);

/// <summary>查「某用户是否有某权限码」的最小抽象：生产用 IRoleService（含管理员角色与权限别名），测试用假实现。</summary>
public interface IMyobPayslipPermissionChecker
{
    Task<bool> HasAsync(string userGuid, string permission);
}

internal sealed class RoleServicePayslipPermissionChecker : IMyobPayslipPermissionChecker
{
    private readonly IRoleService _roles;

    public RoleServicePayslipPermissionChecker(IRoleService roles) => _roles = roles;

    public async Task<bool> HasAsync(string userGuid, string permission) =>
        !string.IsNullOrWhiteSpace(userGuid) && (await _roles.UserHasPermissionAsync(userGuid, permission)).Data;
}

/// <summary>
/// MYOB 历史工资单（只读）：读 Report/Payroll/EmployeePayrollAdvice，按绑定 → 周期内考勤分店 → 支付日发薪公司归到分店，再按权限范围过滤。
/// 工资单本身不落库（进程内存缓存 10 分钟）；日志与异常消息不含姓名 / 金额 / MYOB 响应体。
/// </summary>
internal sealed class MyobPayslipService : IMyobPayslipService
{
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    /// <summary>单次查询的最长区间（含首尾）：24 个月。</summary>
    internal const int MaxRangeDays = 731;

    /// <summary>默认区间：最近 90 天（约 3 个月）。</summary>
    internal const int DefaultRangeDays = 90;

    private const int InChunk = 500;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    private readonly ISqlSugarClient _db;
    private readonly IMyobApiClient _client;
    private readonly IMyobTokenProvider _tokens;
    private readonly ICurrentUserManageableStoreScopeService _scope;
    private readonly IMyobPayslipPermissionChecker _permissions;
    private readonly IAttendancePayrollPushSource _source;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _time;
    private readonly IMyobPayslipEmailSender? _emailSender;

    public MyobPayslipService(
        SqlSugarContext context,
        IMyobApiClient client,
        IMyobTokenProvider tokens,
        ICurrentUserManageableStoreScopeService scope,
        IMyobPayslipPermissionChecker permissions,
        IAttendancePayrollPushSource source,
        IMemoryCache cache,
        TimeProvider? timeProvider = null,
        IMyobPayslipEmailSender? emailSender = null)
        : this(context.Db, client, tokens, scope, permissions, source, cache, timeProvider, emailSender)
    {
    }

    internal MyobPayslipService(
        ISqlSugarClient db,
        IMyobApiClient client,
        IMyobTokenProvider tokens,
        ICurrentUserManageableStoreScopeService scope,
        IMyobPayslipPermissionChecker permissions,
        IAttendancePayrollPushSource source,
        IMemoryCache cache,
        TimeProvider? timeProvider = null,
        IMyobPayslipEmailSender? emailSender = null)
    {
        _emailSender = emailSender;
        _db = db;
        _client = client;
        _tokens = tokens;
        _scope = scope;
        _permissions = permissions;
        _source = source;
        _cache = cache;
        _time = timeProvider ?? TimeProvider.System;
    }

    // ───────────────────────── 权限 ─────────────────────────

    private sealed record AccessResult(MyobPayslipAccess? Access, string UserGuid, string Message);

    /// <summary>
    /// 权限口径：有 ViewAllStores → 全部；否则有 ViewManagedStore → 按 ICurrentUserManageableStoreScopeService 的管辖分店
    /// （管理员 / 仓库经理在该服务里不限分店，沿用其口径）；都没有 → 拒绝。每个接口都重新判断，不靠前端隐藏。
    /// </summary>
    private async Task<AccessResult> ResolveAccessAsync()
    {
        var scope = await _scope.GetScopeAsync();
        if (!scope.IsAuthenticated || string.IsNullOrWhiteSpace(scope.UserGuid))
        {
            return new AccessResult(null, string.Empty, "当前账号未登录");
        }

        if (await _permissions.HasAsync(scope.UserGuid, Permissions.Myob.Payslip.ViewAllStores))
        {
            return new AccessResult(MyobPayslipAccess.Everything, scope.UserGuid, string.Empty);
        }

        if (!await _permissions.HasAsync(scope.UserGuid, Permissions.Myob.Payslip.ViewManagedStore))
        {
            return new AccessResult(null, scope.UserGuid, "你没有查看工资单的权限");
        }

        if (scope.IsAdmin)
        {
            return new AccessResult(MyobPayslipAccess.Everything, scope.UserGuid, string.Empty);
        }

        return scope.IsAllowed
            ? new AccessResult(MyobPayslipAccess.Stores(scope.StoreCodes), scope.UserGuid, string.Empty)
            : new AccessResult(null, scope.UserGuid, string.IsNullOrWhiteSpace(scope.Message) ? "你没有查看工资单的权限" : scope.Message);
    }

    // ───────────────────────── 公司文件 ─────────────────────────

    public async Task<ApiResponse<List<MyobPayslipCompanyDto>>> GetCompaniesAsync(CancellationToken cancellationToken)
    {
        var access = await ResolveAccessAsync();
        if (access.Access is null)
        {
            return ApiResponse<List<MyobPayslipCompanyDto>>.Error(access.Message, MyobPayslipErrorCodes.Forbidden);
        }

        var connections = await LoadVisibleConnectionsAsync(access.Access, null);
        return ApiResponse<List<MyobPayslipCompanyDto>>.OK(connections
            .Select(connection => new MyobPayslipCompanyDto { ConnectionGuid = connection.ConnectionGuid, ConnectionName = connection.BusinessName })
            .ToList());
    }

    /// <summary>
    /// 可见的公司文件：状态 Active 的连接。仅「管辖分店」口径时，只保留与管辖分店有关的公司（分店映射，或员工覆盖指定了该公司）。
    /// requestedGuid 非空时再取交集。
    /// </summary>
    private async Task<List<MyobConnection>> LoadVisibleConnectionsAsync(MyobPayslipAccess access, string? requestedGuid)
    {
        var connections = await _db.Queryable<MyobConnection>()
            .Where(item => item.Status == MyobConnectionStatuses.Active)
            .ToListAsync();

        if (!access.All)
        {
            var scoped = access.StoreCodes.ToList();
            var related = (await _db.Queryable<MyobStoreMapping>().ToListAsync())
                .Where(mapping => access.StoreCodes.Contains(mapping.StoreCode))
                .Select(mapping => mapping.ConnectionGuid)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (scoped.Count > 0)
            {
                foreach (var row in await _db.Queryable<EmployeePayrollEmployer>()
                             .Where(item => scoped.Contains(item.StoreCode) && item.ConnectionGuid != null)
                             .ToListAsync())
                {
                    related.Add(row.ConnectionGuid!);
                }
            }

            connections = connections.Where(connection => related.Contains(connection.ConnectionGuid)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(requestedGuid))
        {
            connections = connections
                .Where(connection => connection.ConnectionGuid.Equals(requestedGuid, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return connections.OrderBy(connection => connection.BusinessName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ───────────────────────── 列表 ─────────────────────────

    public async Task<ApiResponse<MyobPayslipListDto>> ListAsync(MyobPayslipQuery query, CancellationToken cancellationToken)
    {
        var access = await ResolveAccessAsync();
        if (access.Access is null)
        {
            return ApiResponse<MyobPayslipListDto>.Error(access.Message, MyobPayslipErrorCodes.Forbidden);
        }

        var today = Today();
        if (!TryResolveRange(query.From, query.To, today, out var from, out var to, out var rangeError))
        {
            return ApiResponse<MyobPayslipListDto>.Error(rangeError, MyobPayslipErrorCodes.InvalidRange);
        }

        var storeFilter = string.IsNullOrWhiteSpace(query.StoreCode) ? null : query.StoreCode.Trim();
        if (storeFilter is not null && !access.Access.All && !access.Access.StoreCodes.Contains(storeFilter))
        {
            return ApiResponse<MyobPayslipListDto>.Error("你没有查看该分店工资单的权限", MyobPayslipErrorCodes.Forbidden);
        }

        if (query.OnlyUnassigned && !access.Access.All)
        {
            return ApiResponse<MyobPayslipListDto>.Error("你没有查看未归属工资单的权限", MyobPayslipErrorCodes.Forbidden);
        }

        var connections = await LoadVisibleConnectionsAsync(access.Access, query.ConnectionGuid);
        var statuses = new List<MyobPayslipCompanyStatusDto>();
        var items = new List<AdviceItem>();
        DateTime? oldestFetch = null;
        foreach (var connection in connections)
        {
            var status = new MyobPayslipCompanyStatusDto { ConnectionGuid = connection.ConnectionGuid, ConnectionName = connection.BusinessName };
            try
            {
                var cached = await GetAdvicesCachedAsync(connection, from, to.AddDays(1), query.Refresh, cancellationToken);
                status.Ok = true;
                status.Count = cached.Records.Count;
                oldestFetch = oldestFetch is null || cached.FetchedAt < oldestFetch ? cached.FetchedAt : oldestFetch;
                items.AddRange(cached.Records.Select(record => new AdviceItem(connection.ConnectionGuid, connection.BusinessName, record)));
            }
            catch (MyobApiException ex)
            {
                // 一家失败不拖垮另一家；消息只含状态码，不含 MYOB 响应体。
                status.ErrorCode = ex.Code;
                status.ErrorMessage = ex.Message;
            }

            statuses.Add(status);
        }

        var placed = await PlaceAsync(items, cancellationToken);
        var visible = placed.Where(entry => MyobPayslipMatcher.IsVisible(access.Access, entry.Link is not null, entry.Placement)).ToList();

        var unassigned = access.Access.All
            ? visible.Count(entry => MyobPayslipMatcher.IsUnassigned(entry.PlacementKind))
            : 0;

        IEnumerable<PlacedAdvice> filtered = visible;
        if (query.OnlyUnassigned)
        {
            filtered = filtered.Where(entry => MyobPayslipMatcher.IsUnassigned(entry.PlacementKind));
        }
        else if (storeFilter is not null)
        {
            filtered = filtered.Where(entry => entry.Placement.StoreCodes.Contains(storeFilter, StringComparer.OrdinalIgnoreCase));
        }

        var keyword = query.Keyword?.Trim();
        if (!string.IsNullOrEmpty(keyword))
        {
            filtered = filtered.Where(entry =>
                (entry.Item.Record.EmployeeName?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
                || (entry.Item.Record.EmployeeDisplayId?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var storeLabels = await LoadStoreLabelsAsync();
        var rows = filtered
            .OrderByDescending(entry => entry.Item.Record.PaymentDate)
            .ThenBy(entry => entry.Item.Record.EmployeeName, StringComparer.OrdinalIgnoreCase)
            .Select(entry => ToRow(entry, storeLabels))
            .ToList();

        return ApiResponse<MyobPayslipListDto>.OK(new MyobPayslipListDto
        {
            From = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            To = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Companies = statuses,
            Rows = rows,
            TotalCount = rows.Count,
            EmployeeCount = rows.Select(row => (row.ConnectionGuid, row.EmployeeUid)).Distinct().Count(),
            GrossTotal = rows.Sum(row => row.GrossPay ?? 0m),
            NetTotal = rows.Sum(row => row.NetPay ?? 0m),
            UnassignedCount = unassigned,
            CanViewAllStores = access.Access.All,
            FetchedAt = (oldestFetch ?? _time.GetUtcNow().UtcDateTime).ToString("O", CultureInfo.InvariantCulture),
        });
    }

    /// <summary>区间：from / to 为空时默认最近 90 天；必须 from ≤ to，且不超过 24 个月。</summary>
    internal static bool TryResolveRange(string? fromText, string? toText, DateTime today, out DateTime from, out DateTime to, out string error)
    {
        from = default;
        to = default;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(toText))
        {
            to = today;
        }
        else if (!TryParseDate(toText, out to))
        {
            error = "支付日期区间格式不正确，应为 yyyy-MM-dd";
            return false;
        }

        if (string.IsNullOrWhiteSpace(fromText))
        {
            from = to.AddDays(-DefaultRangeDays);
        }
        else if (!TryParseDate(fromText, out from))
        {
            error = "支付日期区间格式不正确，应为 yyyy-MM-dd";
            return false;
        }

        if (from > to)
        {
            error = "支付日期区间的开始日期不能晚于结束日期";
            return false;
        }

        if ((to - from).TotalDays + 1 > MaxRangeDays)
        {
            error = "支付日期区间最长 24 个月";
            return false;
        }

        return true;
    }

    private static bool TryParseDate(string text, out DateTime date) =>
        DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    // 澳洲东部标准时间（UTC+10）足够用来取「今天」，与推送预览同口径。
    private DateTime Today() => _time.GetUtcNow().UtcDateTime.AddHours(10).Date;

    // ───────────────────────── 取数 + 缓存 ─────────────────────────

    private sealed record CachedAdvices(DateTime FetchedAt, IReadOnlyList<MyobPayrollAdviceRecord> Records);

    private sealed record AdviceItem(string ConnectionGuid, string ConnectionName, MyobPayrollAdviceRecord Record);

    internal static string CacheKey(string connectionGuid, DateTime from, DateTime toExclusive) =>
        $"myob:advice:{connectionGuid}:{from:yyyyMMdd}:{toExclusive:yyyyMMdd}";

    /// <summary>
    /// 某公司文件某区间的工资单，进程内存缓存 10 分钟（只存精简对象，不落盘不落库）；refresh 绕过缓存；
    /// 同一公司文件的并发请求串行，同键的后到请求直接用先到请求的结果，只打一轮 MYOB。
    /// </summary>
    private async Task<CachedAdvices> GetAdvicesCachedAsync(
        MyobConnection connection, DateTime from, DateTime toExclusive, bool refresh, CancellationToken cancellationToken)
    {
        var key = CacheKey(connection.ConnectionGuid, from, toExclusive);
        CachedAdvices? cached;
        if (!refresh && _cache.TryGetValue(key, out cached) && cached is not null)
        {
            return cached;
        }

        // 锁按公司文件而不是按查询键：键里带日期会让字典无限增长；同一公司的查询本来也共用同一个 MYOB 限流闸。
        var gate = Locks.GetOrAdd(connection.ConnectionGuid, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && _cache.TryGetValue(key, out cached) && cached is not null)
            {
                return cached;
            }

            var accessToken = await _tokens.GetAccessTokenAsync(connection.ConnectionGuid, cancellationToken);
            var records = await _client.GetPayrollAdvicesAsync(
                connection.BusinessId, accessToken, new MyobPayrollAdviceQuery(from, toExclusive), cancellationToken);
            cached = new CachedAdvices(_time.GetUtcNow().UtcDateTime, records);
            _cache.Set(key, cached, CacheLifetime);
            return cached;
        }
        finally
        {
            gate.Release();
        }
    }

    // ───────────────────────── 归类 ─────────────────────────

    private sealed record PlacedAdvice(AdviceItem Item, EmployeeMyobLink? Link, MyobPayslipPlacement Placement)
    {
        internal string PlacementKind => Placement.Kind;
    }

    /// <summary>给每张工资单找绑定员工并归到分店。批量取数：绑定、所属分店、发薪公司覆盖、分店映射各一次，考勤分店按「周期」各一次。</summary>
    private async Task<List<PlacedAdvice>> PlaceAsync(IReadOnlyList<AdviceItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var connectionGuids = items.Select(item => item.ConnectionGuid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var uids = items.Select(item => item.Record.EmployeeUid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // 绑定（含已解除的历史绑定）：只取本批公司文件、本批员工卡。
        var links = new List<EmployeeMyobLink>();
        foreach (var chunk in uids.Chunk(InChunk))
        {
            var part = chunk.ToList();
            links.AddRange(await _db.Queryable<EmployeeMyobLink>()
                .Where(link => connectionGuids.Contains(link.ConnectionGuid) && part.Contains(link.MyobEmployeeUid))
                .ToListAsync());
        }

        var userGuids = links.Select(link => link.UserGuid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // 员工当前所属分店：包含已停用账号（历史工资单不能因为员工离职而消失），所以不复用只加载在职账号的 MyobWorkforceLoader。
        var currentStores = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var overrides = new Dictionary<string, List<EmployeePayrollEmployer>>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in userGuids.Chunk(InChunk))
        {
            var part = chunk.ToList();
            foreach (var row in await _db.Queryable<UserStore>()
                         .InnerJoin<Store>((us, s) => us.StoreGUID == s.StoreGUID)
                         .Where((us, s) => !us.IsDeleted && !s.IsDeleted && part.Contains(us.UserGUID))
                         .Select((us, s) => new { us.UserGUID, s.StoreCode })
                         .ToListAsync())
            {
                if (!currentStores.TryGetValue(row.UserGUID, out var list))
                {
                    currentStores[row.UserGUID] = list = [];
                }

                list.Add(row.StoreCode);
            }

            foreach (var row in await _db.Queryable<EmployeePayrollEmployer>().Where(item => part.Contains(item.UserGuid)).ToListAsync())
            {
                if (!overrides.TryGetValue(row.UserGuid, out var list))
                {
                    overrides[row.UserGuid] = list = [];
                }

                list.Add(row);
            }
        }

        var defaultConnectionByStore = (await _db.Queryable<MyobStoreMapping>().ToListAsync())
            .ToDictionary(mapping => mapping.StoreCode, mapping => (string?)mapping.ConnectionGuid, StringComparer.OrdinalIgnoreCase);

        var ignored = (await _db.Queryable<MyobEmployeeIgnore>().Where(item => connectionGuids.Contains(item.ConnectionGuid)).ToListAsync())
            .Select(item => (item.ConnectionGuid.ToLowerInvariant(), item.MyobEmployeeUid.ToLowerInvariant()))
            .ToHashSet();

        // 周期内有考勤数据的分店：同一周期的员工合并成一次查询。
        var attendance = new Dictionary<(DateTime Start, DateTime End), Dictionary<string, List<string>>>();
        var periodGroups = items
            .Select(item => (Item: item, Link: MyobPayslipMatcher.ResolveLink(links, item.ConnectionGuid, item.Record.EmployeeUid)))
            .Where(entry => entry.Link is not null && entry.Item.Record.PeriodStart is not null)
            .GroupBy(entry => (Start: entry.Item.Record.PeriodStart!.Value.Date, End: (entry.Item.Record.PeriodEnd ?? entry.Item.Record.PeriodStart!.Value).Date));
        foreach (var group in periodGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var weeks = Math.Max(1, (int)Math.Ceiling(((group.Key.End - group.Key.Start).TotalDays + 1) / 7d));
            var users = group.Select(entry => entry.Link!.UserGuid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var pairs = await _source.FindDataPairsAsync(users, group.Key.Start, weeks);
            attendance[group.Key] = pairs
                .GroupBy(pair => pair.UserGuid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(pair => pair.Key, pair => pair.Select(item => item.StoreCode).ToList(), StringComparer.OrdinalIgnoreCase);
        }

        var result = new List<PlacedAdvice>();
        foreach (var item in items)
        {
            var record = item.Record;
            var link = MyobPayslipMatcher.ResolveLink(links, item.ConnectionGuid, record.EmployeeUid);
            if (link is null)
            {
                // 「忽略」名单里的员工卡不是我方员工，不展示。
                if (ignored.Contains((item.ConnectionGuid.ToLowerInvariant(), record.EmployeeUid.ToLowerInvariant())))
                {
                    continue;
                }

                result.Add(new PlacedAdvice(item, null, new MyobPayslipPlacement(MyobPayslipPlacements.Unbound, [])));
                continue;
            }

            IEnumerable<string> attended = [];
            if (record.PeriodStart is { } start)
            {
                var key = (start.Date, (record.PeriodEnd ?? start).Date);
                if (attendance.TryGetValue(key, out var byUser) && byUser.TryGetValue(link.UserGuid, out var stores))
                {
                    attended = stores;
                }
            }

            var placement = MyobPayslipMatcher.Place(
                item.ConnectionGuid,
                record.PaymentDate ?? DateTime.MinValue,
                attended,
                currentStores.GetValueOrDefault(link.UserGuid) ?? [],
                defaultConnectionByStore,
                overrides.GetValueOrDefault(link.UserGuid) ?? []);
            result.Add(new PlacedAdvice(item, link, placement));
        }

        return result;
    }

    private async Task<Dictionary<string, string>> LoadStoreLabelsAsync() =>
        (await _db.Queryable<Store>().Where(store => !store.IsDeleted).Select(store => new { store.StoreCode, store.StoreName }).ToListAsync())
            .GroupBy(store => store.StoreCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => string.IsNullOrWhiteSpace(group.First().StoreName) ? group.Key : $"{group.Key} {group.First().StoreName}",
                StringComparer.OrdinalIgnoreCase);

    private static List<MyobPayslipStoreDto> ToStoreDtos(MyobPayslipPlacement placement, IReadOnlyDictionary<string, string> labels) =>
        placement.StoreCodes
            .Select(code => new MyobPayslipStoreDto { StoreCode = code, StoreLabel = labels.GetValueOrDefault(code) ?? code })
            .ToList();

    private static MyobPayslipRowDto ToRow(PlacedAdvice entry, IReadOnlyDictionary<string, string> labels)
    {
        var record = entry.Item.Record;
        return new MyobPayslipRowDto
        {
            ConnectionGuid = entry.Item.ConnectionGuid,
            CompanyName = record.EmployerName ?? entry.Item.ConnectionName,
            EmployeeUid = record.EmployeeUid,
            EmployeeName = record.EmployeeName ?? string.Empty,
            DisplayId = record.EmployeeDisplayId,
            UserGuid = entry.Link?.UserGuid,
            PaymentDate = FormatDate(record.PaymentDate) ?? string.Empty,
            PeriodStart = FormatDate(record.PeriodStart),
            PeriodEnd = FormatDate(record.PeriodEnd),
            PayFrequency = record.PayFrequency,
            GrossPay = record.GrossPay,
            TaxTotal = record.Lines.Where(line => IsType(line, "Tax")).Sum(line => line.Amount ?? 0m),
            NetPay = record.NetPay,
            Placement = entry.Placement.Kind,
            Stores = ToStoreDtos(entry.Placement, labels),
        };
    }

    private static string? FormatDate(DateTime? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool IsType(MyobPayrollAdviceLine line, string type) =>
        string.Equals(line.CategoryType, type, StringComparison.OrdinalIgnoreCase);

    // ───────────────────────── 明细 ─────────────────────────

    /// <summary>已通过权限与归类校验的一张工资单（明细与工时共用）。</summary>
    private sealed record ResolvedSlip(
        AccessResult Access, MyobConnection Connection, MyobPayrollAdviceRecord Slip, int SameDayCount, PlacedAdvice Placed);

    private sealed record ResolveOutcome(ResolvedSlip? Slip, string? ErrorCode, string? Message);

    private static ResolveOutcome NotFoundOutcome() => new(null, MyobPayslipErrorCodes.NotFound, "找不到这张工资单");

    /// <summary>
    /// 取出并校验单张工资单：权限 → 参数格式 → 公司文件可见 → 从 MYOB 只取这名员工这一天 → 归类 → 可见性。
    /// 越权与不存在返回同样的 NotFound，不暴露这张工资单是否存在。工时接口也走这里重新取工资单，
    /// 不信任客户端传来的周期，避免用伪造的周期绕过「分店归属」判断。
    /// </summary>
    private async Task<ResolveOutcome> ResolveSlipAsync(
        MyobPayslipDetailRequest request, CancellationToken cancellationToken, string? extraPermission = null)
    {
        var access = await ResolveAccessAsync();
        if (access.Access is null)
        {
            return new ResolveOutcome(null, MyobPayslipErrorCodes.Forbidden, access.Message);
        }

        // 导出类操作（下载 / 邮件）在能看工资单之外还要有各自的权限；先判断，免得白白去请求一次 MYOB。
        if (extraPermission is not null && !await _permissions.HasAsync(access.UserGuid, extraPermission))
        {
            return new ResolveOutcome(null, MyobPayslipErrorCodes.Forbidden, "你没有此操作的权限");
        }

        // 员工卡 UID 必须是合法 Guid（也是后面拼进 OData 表达式的前提）；日期必须是 yyyy-MM-dd。
        if (!Guid.TryParse(request.EmployeeUid, out _)
            || !TryParseDate(request.PaymentDate ?? string.Empty, out var paymentDate))
        {
            return NotFoundOutcome();
        }

        DateTime? periodStart = null;
        if (!string.IsNullOrWhiteSpace(request.PeriodStart))
        {
            if (!TryParseDate(request.PeriodStart, out var parsed))
            {
                return NotFoundOutcome();
            }

            periodStart = parsed;
        }

        var connection = (await LoadVisibleConnectionsAsync(access.Access, request.ConnectionGuid)).FirstOrDefault();
        if (connection is null)
        {
            // 公司文件不存在、未启用或对该账号不可见：统一当作找不到，不暴露区别。
            return NotFoundOutcome();
        }

        IReadOnlyList<MyobPayrollAdviceRecord> records;
        try
        {
            var accessToken = await _tokens.GetAccessTokenAsync(connection.ConnectionGuid, cancellationToken);
            records = await _client.GetPayrollAdvicesAsync(
                connection.BusinessId,
                accessToken,
                new MyobPayrollAdviceQuery(paymentDate, paymentDate.AddDays(1), request.EmployeeUid.Trim()),
                cancellationToken);
        }
        catch (MyobApiException ex)
        {
            return new ResolveOutcome(null, ex.Code, ex.Message);
        }

        var sameDay = records
            .Where(record => record.EmployeeUid.Equals(request.EmployeeUid.Trim(), StringComparison.OrdinalIgnoreCase)
                && record.PaymentDate?.Date == paymentDate)
            .ToList();
        var chosen = periodStart is null
            ? sameDay.FirstOrDefault()
            : sameDay.FirstOrDefault(record => record.PeriodStart?.Date == periodStart);
        if (chosen is null)
        {
            return NotFoundOutcome();
        }

        var placed = (await PlaceAsync([new AdviceItem(connection.ConnectionGuid, connection.BusinessName, chosen)], cancellationToken)).FirstOrDefault();
        if (placed is null || !MyobPayslipMatcher.IsVisible(access.Access, placed.Link is not null, placed.Placement))
        {
            return NotFoundOutcome();
        }

        return new ResolveOutcome(new ResolvedSlip(access, connection, chosen, sameDay.Count, placed), null, null);
    }

    public async Task<ApiResponse<MyobPayslipDetailDto>> GetDetailAsync(MyobPayslipDetailRequest request, CancellationToken cancellationToken)
    {
        var outcome = await ResolveSlipAsync(request, cancellationToken);
        if (outcome.Slip is null)
        {
            return ApiResponse<MyobPayslipDetailDto>.Error(outcome.Message ?? "找不到这张工资单", outcome.ErrorCode);
        }

        var (access, connection, chosen, sameDayCount, placed) = outcome.Slip;

        // 先记查看，再返回内容：记不下来就拒绝（宁可不给看，也不能无痕地看）。只记谁、哪一期，不记金额。
        if (await TryWriteLogAsync(access, connection, placed, chosen, MyobPayslipLogActions.View, null) is null)
        {
            return ApiResponse<MyobPayslipDetailDto>.Error("暂时无法记录本次查看，已拒绝显示工资单，请稍后重试", MyobPayslipErrorCodes.ViewLogFailed);
        }

        var labels = await LoadStoreLabelsAsync();
        var detail = new MyobPayslipDetailDto
        {
            ConnectionGuid = connection.ConnectionGuid,
            CompanyName = chosen.EmployerName ?? connection.BusinessName,
            CompanyAbn = chosen.EmployerAbn,
            EmployeeName = chosen.EmployeeName ?? string.Empty,
            DisplayId = chosen.EmployeeDisplayId,
            UserGuid = placed.Link?.UserGuid,
            PaymentDate = FormatDate(chosen.PaymentDate) ?? string.Empty,
            PeriodStart = FormatDate(chosen.PeriodStart),
            PeriodEnd = FormatDate(chosen.PeriodEnd),
            PayFrequency = chosen.PayFrequency,
            GrossPay = chosen.GrossPay,
            NetPay = chosen.NetPay,
            SuperFundName = chosen.SuperFundName,
            HourlyRate = chosen.HourlyRate,
            ReferenceNumber = chosen.ChequeNumber,
            Placement = placed.Placement.Kind,
            Stores = ToStoreDtos(placed.Placement, labels),
            SamePaymentDateCount = sameDayCount,
            CanDownloadPdf = await _permissions.HasAsync(access.UserGuid, Permissions.Myob.Payslip.Download),
            CanSendEmail = await _permissions.HasAsync(access.UserGuid, Permissions.Myob.Payslip.Email),
        };
        foreach (var line in chosen.Lines)
        {
            var dto = new MyobPayslipLineDto
            {
                Description = line.CategoryName,
                Type = line.CategoryType,
                TypeLabel = MyobPayslipPresentation.TypeLabel(line.CategoryType),
                Hours = line.Hours,
                Rate = line.CalculationRate,
                Amount = line.Amount,
                YearToDate = line.YearToDate,
            };
            LinesFor(detail, line.CategoryType).Add(dto);

            // 扁平明细：与 MYOB 默认工资单通栏表一致（原始顺序 + Type 列）；假期累计的小时数放 Hours 列。
            var (hours, amount) = MyobPayslipPresentation.NormalizeQuantity(line.CategoryType, line.Hours, line.Amount);
            detail.Lines.Add(new MyobPayslipLineDto
            {
                Description = line.CategoryName,
                Type = line.CategoryType,
                TypeLabel = MyobPayslipPresentation.TypeLabel(line.CategoryType),
                Hours = hours,
                Rate = line.CalculationRate,
                Amount = amount,
                YearToDate = line.YearToDate,
            });
        }

        return ApiResponse<MyobPayslipDetailDto>.OK(detail);
    }

    public async Task<ApiResponse<MyobPayslipHoursDto>> GetHoursAsync(MyobPayslipDetailRequest request, CancellationToken cancellationToken)
    {
        var outcome = await ResolveSlipAsync(request, cancellationToken);
        if (outcome.Slip is null)
        {
            return ApiResponse<MyobPayslipHoursDto>.Error(outcome.Message ?? "找不到这张工资单", outcome.ErrorCode);
        }

        var (_, _, chosen, _, placed) = outcome.Slip;
        var dto = new MyobPayslipHoursDto
        {
            PeriodStart = FormatDate(chosen.PeriodStart),
            PeriodEnd = FormatDate(chosen.PeriodEnd),
        };

        // 没绑定我方员工、没有分店、或工资单没有周期：没有可算的考勤工时，返回空列表（不是错误）。
        if (placed.Link is null || placed.Placement.StoreCodes.Count == 0 || chosen.PeriodStart is not { } start)
        {
            return ApiResponse<MyobPayslipHoursDto>.OK(dto);
        }

        var end = (chosen.PeriodEnd ?? start).Date;
        var weeks = Math.Max(1, (int)Math.Ceiling(((end - start.Date).TotalDays + 1) / 7d));
        var labels = await LoadStoreLabelsAsync();
        foreach (var storeCode in placed.Placement.StoreCodes)
        {
            var row = new MyobPayslipStoreHoursDto
            {
                StoreCode = storeCode,
                StoreLabel = labels.GetValueOrDefault(storeCode) ?? storeCode,
            };
            try
            {
                // 与工资表 / Timesheet 推送同一个快照构建器，口径一致；只取这一名员工。
                var data = await _source.GetStoreDataAsync(storeCode, start.Date, weeks, [placed.Link.UserGuid]);
                var employee = data.Employees.FirstOrDefault(item => item.UserGuid.Equals(placed.Link.UserGuid, StringComparison.OrdinalIgnoreCase));
                if (employee is not null)
                {
                    row.WorkHours = Math.Round(employee.WorkItems.Sum(item => item.Minutes) / 60m, 2);
                    row.LeaveHours = Math.Round(
                        employee.Sick.Sum(item => item.Hours) + employee.Annual.Sum(item => item.Hours) + employee.PublicHolidayPay.Sum(item => item.Hours), 2);
                }
            }
            catch (Exception)
            {
                // 单个分店读取失败只标这一行，不拖垮其它分店；不回显异常内容。
                row.Failed = true;
            }

            dto.Stores.Add(row);
        }

        dto.TotalWorkHours = dto.Stores.Sum(item => item.WorkHours);
        dto.TotalLeaveHours = dto.Stores.Sum(item => item.LeaveHours);
        return ApiResponse<MyobPayslipHoursDto>.OK(dto);
    }

    // ───────────────────────── 下载 PDF / 邮件发送 ─────────────────────────

    /// <summary>写一条查看记录并返回它（失败返回 null）。只记谁、哪一期、做了什么、邮件收件邮箱，不记金额与姓名。</summary>
    private async Task<MyobPayslipViewLog?> TryWriteLogAsync(
        AccessResult access, MyobConnection connection, PlacedAdvice placed, MyobPayrollAdviceRecord slip, string action, string? recipient)
    {
        var log = new MyobPayslipViewLog
        {
            ActorUserGuid = access.UserGuid,
            ConnectionGuid = connection.ConnectionGuid,
            TargetUserGuid = placed.Link?.UserGuid,
            MyobEmployeeUid = slip.EmployeeUid,
            PaymentDate = slip.PaymentDate?.Date ?? DateTime.MinValue,
            ViewedAt = _time.GetUtcNow().UtcDateTime,
            Action = action,
            Recipient = recipient,
        };
        try
        {
            await _db.Insertable(log).ExecuteCommandAsync();
            return log;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<string?> LoadUserEmailAsync(string? userGuid)
    {
        if (string.IsNullOrWhiteSpace(userGuid))
        {
            return null;
        }

        var email = (await _db.Queryable<User>()
            .Where(user => user.UserGUID == userGuid && !user.IsDeleted)
            .Select(user => user.Email)
            .FirstAsync())?.Trim();
        return string.IsNullOrEmpty(email) ? null : email;
    }

    private static string PeriodText(MyobPayrollAdviceRecord slip) =>
        $"{MyobPayslipPresentation.Date(slip.PeriodStart)} to {MyobPayslipPresentation.Date(slip.PeriodEnd)}";

    private static string EmailSubject(MyobPayrollAdviceRecord slip) =>
        $"Your payslip – pay period {PeriodText(slip)} / 工资单";

    public async Task<ApiResponse<MyobPayslipPdfFile>> DownloadPdfAsync(MyobPayslipDetailRequest request, CancellationToken cancellationToken)
    {
        var outcome = await ResolveSlipAsync(request, cancellationToken, Permissions.Myob.Payslip.Download);
        if (outcome.Slip is null)
        {
            return ApiResponse<MyobPayslipPdfFile>.Error(outcome.Message ?? "找不到这张工资单", outcome.ErrorCode);
        }

        var (access, connection, chosen, _, placed) = outcome.Slip;
        byte[] bytes;
        try
        {
            bytes = MyobPayslipPdfRenderer.Render(MyobPayslipPdfModel.From(chosen, connection.BusinessName));
        }
        catch (Exception)
        {
            return ApiResponse<MyobPayslipPdfFile>.Error("工资单 PDF 生成失败，请稍后重试", MyobPayslipErrorCodes.PdfFailed);
        }

        // 先记下载，再给文件：记不下来就拒绝。
        if (await TryWriteLogAsync(access, connection, placed, chosen, MyobPayslipLogActions.Download, null) is null)
        {
            return ApiResponse<MyobPayslipPdfFile>.Error("暂时无法记录本次下载，已拒绝提供文件，请稍后重试", MyobPayslipErrorCodes.ViewLogFailed);
        }

        return ApiResponse<MyobPayslipPdfFile>.OK(new MyobPayslipPdfFile(bytes, MyobPayslipPresentation.Filename(chosen.EmployeeName, chosen.PaymentDate)));
    }

    public async Task<ApiResponse<MyobPayslipEmailTargetDto>> GetEmailTargetAsync(MyobPayslipDetailRequest request, CancellationToken cancellationToken)
    {
        var outcome = await ResolveSlipAsync(request, cancellationToken, Permissions.Myob.Payslip.Email);
        if (outcome.Slip is null)
        {
            return ApiResponse<MyobPayslipEmailTargetDto>.Error(outcome.Message ?? "找不到这张工资单", outcome.ErrorCode);
        }

        var (access, _, chosen, _, placed) = outcome.Slip;
        var profileEmail = await LoadUserEmailAsync(placed.Link?.UserGuid);
        return ApiResponse<MyobPayslipEmailTargetDto>.OK(new MyobPayslipEmailTargetDto
        {
            Email = profileEmail,
            MaskedEmail = profileEmail is null ? null : MyobPayslipPresentation.MaskEmail(profileEmail),
            Source = profileEmail is null ? "none" : "profile",
            Subject = EmailSubject(chosen),
            Filename = MyobPayslipPresentation.Filename(chosen.EmployeeName, chosen.PaymentDate),
            MyEmail = await LoadUserEmailAsync(access.UserGuid),
        });
    }

    /// <summary>单个、不带显示名、不含逗号分号的邮箱地址。</summary>
    internal static bool TryParseSingleEmail(string? text, out string email)
    {
        email = (text ?? string.Empty).Trim();
        if (email.Length is 0 or > 254 || email.IndexOfAny([',', ';', ' ', '<', '>']) >= 0)
        {
            return false;
        }

        // 必须是 本地部分@带点的域名（MimeKit 会放过没有域名的 "abc"）。
        var at = email.IndexOf('@');
        return at > 0
            && at == email.LastIndexOf('@')
            && email[(at + 1)..].Contains('.')
            && !email.EndsWith('.')
            && MailboxAddress.TryParse(email, out var address)
            && string.Equals(address.Address, email, StringComparison.Ordinal);
    }

    public async Task<ApiResponse<MyobPayslipEmailResultDto>> SendEmailAsync(MyobPayslipEmailRequest request, CancellationToken cancellationToken)
    {
        if (_emailSender is null)
        {
            return ApiResponse<MyobPayslipEmailResultDto>.Error("邮件发送暂不可用", MyobPayslipErrorCodes.EmailNotConfigured);
        }

        var detailRequest = new MyobPayslipDetailRequest
        {
            ConnectionGuid = request.ConnectionGuid,
            EmployeeUid = request.EmployeeUid,
            PaymentDate = request.PaymentDate,
            PeriodStart = request.PeriodStart,
        };
        var outcome = await ResolveSlipAsync(detailRequest, cancellationToken, Permissions.Myob.Payslip.Email);
        if (outcome.Slip is null)
        {
            return ApiResponse<MyobPayslipEmailResultDto>.Error(outcome.Message ?? "找不到这张工资单", outcome.ErrorCode);
        }

        // 权限与范围通过后才校验邮件参数，避免对无权限的人泄露任何信息。
        if (!TryParseSingleEmail(request.To, out var to))
        {
            return ApiResponse<MyobPayslipEmailResultDto>.Error("收件人邮箱格式不正确（每次只能填一个邮箱）", MyobPayslipErrorCodes.EmailInvalid);
        }

        var note = request.Note?.Trim();
        if (note is { Length: > 500 })
        {
            return ApiResponse<MyobPayslipEmailResultDto>.Error("附言最多 500 字", MyobPayslipErrorCodes.EmailInvalid);
        }

        var (access, connection, chosen, _, placed) = outcome.Slip;

        // 档案邮箱存在且与收件人不同：必须明确确认，防止发错人；档案没有邮箱时手填无需确认（只用于本次，不回写档案）。
        var profileEmail = await LoadUserEmailAsync(placed.Link?.UserGuid);
        if (profileEmail is not null
            && !profileEmail.Equals(to, StringComparison.OrdinalIgnoreCase)
            && !request.RecipientChangeConfirmed)
        {
            return ApiResponse<MyobPayslipEmailResultDto>.Error("收件人与员工档案邮箱不同，请核对并确认后再发送", MyobPayslipErrorCodes.RecipientUnconfirmed);
        }

        string? cc = null;
        if (request.CopyToMe)
        {
            cc = await LoadUserEmailAsync(access.UserGuid);
            if (cc is null)
            {
                return ApiResponse<MyobPayslipEmailResultDto>.Error("你的账号没有邮箱，无法抄送给自己", MyobPayslipErrorCodes.EmailInvalid);
            }
        }

        byte[] pdf;
        try
        {
            pdf = MyobPayslipPdfRenderer.Render(MyobPayslipPdfModel.From(chosen, connection.BusinessName));
        }
        catch (Exception)
        {
            return ApiResponse<MyobPayslipEmailResultDto>.Error("工资单 PDF 生成失败，请稍后重试", MyobPayslipErrorCodes.PdfFailed);
        }

        // 先记（含收件邮箱）再发送：记不下来就不发；发送失败把这条记录改成 EmailFailed，留下完整的尝试痕迹。
        var log = await TryWriteLogAsync(access, connection, placed, chosen, MyobPayslipLogActions.Email, to);
        if (log is null)
        {
            return ApiResponse<MyobPayslipEmailResultDto>.Error("暂时无法记录本次发送，已取消发送，请稍后重试", MyobPayslipErrorCodes.ViewLogFailed);
        }

        var sent = await _emailSender.SendAsync(
            new MyobPayslipEmail(
                to,
                cc,
                chosen.EmployeeName ?? string.Empty,
                chosen.EmployerName ?? connection.BusinessName,
                PeriodText(chosen),
                MyobPayslipPresentation.Date(chosen.PaymentDate),
                string.IsNullOrEmpty(note) ? null : note,
                EmailSubject(chosen),
                MyobPayslipPresentation.Filename(chosen.EmployeeName, chosen.PaymentDate),
                pdf),
            cancellationToken);
        if (!sent.Success)
        {
            try
            {
                await _db.Updateable<MyobPayslipViewLog>()
                    .SetColumns(item => item.Action == MyobPayslipLogActions.EmailFailed)
                    .Where(item => item.LogGuid == log.LogGuid)
                    .ExecuteCommandAsync();
            }
            catch (Exception)
            {
                // 标记失败不影响把发送失败的原因告诉用户。
            }

            return ApiResponse<MyobPayslipEmailResultDto>.Error(sent.Message ?? "邮件发送失败，请稍后重试", sent.ErrorCode ?? MyobPayslipErrorCodes.EmailSendFailed);
        }

        return ApiResponse<MyobPayslipEmailResultDto>.OK(new MyobPayslipEmailResultDto
        {
            SentTo = MyobPayslipPresentation.MaskEmail(to),
            CopiedToMe = cc is not null,
        });
    }

    private static List<MyobPayslipLineDto> LinesFor(MyobPayslipDetailDto detail, string? type) =>
        type?.Trim().ToLowerInvariant() switch
        {
            "wage" => detail.Wages,
            "tax" => detail.Taxes,
            "deduction" => detail.Deductions,
            "superannuation" => detail.Superannuation,
            "entitlement" => detail.Entitlements,
            _ => detail.Others,
        };
}
