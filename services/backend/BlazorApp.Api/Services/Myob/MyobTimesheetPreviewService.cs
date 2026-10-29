using System.Globalization;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Api.Services.Attendance;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace BlazorApp.Api.Services.Myob;

public interface IMyobTimesheetPreviewService
{
    Task<ApiResponse<MyobTimesheetPreviewDto>> PreviewAsync(
        string connectionGuid, string? periodStart, bool checkMyob, CancellationToken cancellationToken);
}

/// <summary>
/// 预览与推送共用的「规划」结果（方案第 4.2 节「预览看到什么就推什么」）：预览 DTO 之外，还带每个员工在本公司名下的分钟级计划、
/// 员工卡绑定与类别映射。推送服务只信任这里算出来的数字，不信任客户端传来的任何工时。
/// </summary>
internal sealed record MyobTimesheetComputation(
    MyobTimesheetPreviewDto Dto,
    MyobConnection Connection,
    DateTime Start,
    DateTime End,
    int Weeks,
    DateTime Today,
    IReadOnlyDictionary<string, MyobCompanyPlan> Plans,
    IReadOnlyDictionary<string, EmployeeMyobLink> Links,
    IReadOnlyDictionary<string, string?> MappingByKey);

internal interface IMyobTimesheetPlanSource
{
    Task<ApiResponse<MyobTimesheetComputation>> ComputeAsync(
        string connectionGuid, string? periodStart, bool checkMyob, CancellationToken cancellationToken);
}

/// <summary>
/// Timesheet 推送预览（方案第 8、11.1 节）：只读，不往 MYOB 写任何东西。
/// 以「公司文件 × 工资周期」为单位，算出每个员工在该公司名下要推送的转账工时（按日、按 MYOB 类别），并列出拦截项与提示：
/// - 发薪公司按「员工 × 上班分店」决定（分店映射 + 发薪公司覆盖），一个分店的工时整期归同一家公司；
/// - 员工在多家公司有工时时，固定转账工时按各公司实际工时比例分摊（<see cref="MyobTimesheetPlanner"/>）；
/// - 拦截项：未绑定员工卡 / 涉及分店未锁定 / 分店未映射 / 类别未映射 / 无时薪 / 无分店权限 / MYOB 已发薪；
/// - 提示项（不阻止）：生日换档、低于最低时薪、公假价与平日价不一致、年假折现不推送。
/// 数据全部来自工资表同一个快照构建器（<see cref="IAttendancePayrollPushSource"/>），口径与工资表一致。
/// </summary>
internal sealed class MyobTimesheetPreviewService : IMyobTimesheetPreviewService, IMyobTimesheetPlanSource
{
    private const int PeriodOptionCount = 6;

    private readonly ISqlSugarClient _db;
    private readonly IAttendancePayrollPushSource _source;
    private readonly ICurrentUserManageableStoreScopeService _scope;
    private readonly IMyobPayCategoryService _categories;
    private readonly IMyobApiClient _client;
    private readonly IMyobTokenProvider _tokens;
    private readonly MyobOptions _options;
    private readonly TimeProvider _time;

    public MyobTimesheetPreviewService(
        SqlSugarContext context,
        IAttendancePayrollPushSource source,
        ICurrentUserManageableStoreScopeService scope,
        IMyobPayCategoryService categories,
        IMyobApiClient client,
        IMyobTokenProvider tokens,
        IOptions<MyobOptions> options,
        TimeProvider? timeProvider = null)
        : this(context.Db, source, scope, categories, client, tokens, options, timeProvider)
    {
    }

    internal MyobTimesheetPreviewService(
        ISqlSugarClient db,
        IAttendancePayrollPushSource source,
        ICurrentUserManageableStoreScopeService scope,
        IMyobPayCategoryService categories,
        IMyobApiClient client,
        IMyobTokenProvider tokens,
        IOptions<MyobOptions> options,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _source = source;
        _scope = scope;
        _categories = categories;
        _client = client;
        _tokens = tokens;
        _options = options.Value;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<ApiResponse<MyobTimesheetPreviewDto>> PreviewAsync(
        string connectionGuid,
        string? periodStart,
        bool checkMyob,
        CancellationToken cancellationToken)
    {
        var computed = await ComputeAsync(connectionGuid, periodStart, checkMyob, cancellationToken);
        return computed.Success && computed.Data is not null
            ? ApiResponse<MyobTimesheetPreviewDto>.OK(computed.Data.Dto)
            : ApiResponse<MyobTimesheetPreviewDto>.Error(computed.Message, computed.ErrorCode);
    }

    public async Task<ApiResponse<MyobTimesheetComputation>> ComputeAsync(
        string connectionGuid,
        string? periodStart,
        bool checkMyob,
        CancellationToken cancellationToken)
    {
        var scope = await _scope.GetScopeAsync();
        if (!scope.IsAllowed)
        {
            return ApiResponse<MyobTimesheetComputation>.Error(scope.Message, MyobTimesheetErrorCodes.Forbidden);
        }

        var connection = await _db.Queryable<MyobConnection>()
            .FirstAsync(item => item.ConnectionGuid == connectionGuid && item.Status == MyobConnectionStatuses.Active);
        if (connection is null)
        {
            return ApiResponse<MyobTimesheetComputation>.Error("MYOB 连接不存在或未启用", MyobErrorCodes.ConnectionNotActive);
        }

        var settings = await _source.GetSettingsAsync();
        var weeks = settings.Weeks;
        // 门店本地「今天」：澳洲东部标准时间（UTC+10）足够用来挑周期，周期边界在凌晨之外几乎没有差别。
        var today = _time.GetUtcNow().UtcDateTime.AddHours(10).Date;
        var currentStart = AttendanceReactService.ResolvePayPeriodStart(today, weeks, settings.FortnightAnchorDate);
        var start = currentStart.AddDays(-7 * weeks);
        if (!string.IsNullOrWhiteSpace(periodStart))
        {
            if (!DateTime.TryParseExact(periodStart.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                || AttendanceReactService.ResolvePayPeriodStart(parsed, weeks, settings.FortnightAnchorDate) != parsed.Date)
            {
                return ApiResponse<MyobTimesheetComputation>.Error(
                    "工资周期起始日不正确，必须是某个周期的起始日", MyobTimesheetErrorCodes.InvalidPeriod);
            }

            start = parsed.Date;
        }

        var end = start.AddDays(weeks * 7 - 1);
        var dto = new MyobTimesheetPreviewDto
        {
            ConnectionGuid = connection.ConnectionGuid,
            ConnectionName = connection.BusinessName,
            PeriodStart = Format(start),
            PeriodEnd = Format(end),
            Weeks = weeks,
            Periods = Enumerable.Range(0, PeriodOptionCount)
                .Select(index => currentStart.AddDays(-7 * weeks * index))
                .Select(date => new MyobPreviewPeriodOptionDto { Start = Format(date), End = Format(date.AddDays(weeks * 7 - 1)) })
                .ToList(),
            PushEnabled = MyobTimesheetPushGate.IsAllowed(_options, connection.ConnectionGuid),
        };

        var mapping = await _categories.GetSavedMappingAsync(connectionGuid);
        var mappingByKey = MyobPayCategoryService.ToDictionary(mapping);
        var categoryNames = _categories.GetCachedCategoryNames(connectionGuid);
        // 完整度只看必填 5 项；临时工周末两项可选（真有临时工周末工时又没设，会在员工行上拦截）。
        dto.Readiness.CategoryMappedCount = mappingByKey.Count(
            pair => pair.Value is not null && MyobPayCategoryKeys.Required.Contains(pair.Key));
        dto.Readiness.CategoryComplete = dto.Readiness.CategoryMappedCount == MyobPayCategoryKeys.Required.Length;

        // ① 发薪公司：本公司名下的员工 = 至少有一个上班分店在该周期由本公司发薪
        var workforce = await new MyobWorkforceLoader(_db).LoadAsync(start);
        var companies = (await _db.Queryable<MyobConnection>().Where(item => item.Status == MyobConnectionStatuses.Active).ToListAsync())
            .ToDictionary(item => item.ConnectionGuid, item => item.BusinessName, StringComparer.OrdinalIgnoreCase);
        var companyUsers = workforce.Pairs
            .Where(pair => string.Equals(pair.EffectiveConnectionGuid, connectionGuid, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.UserGuid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var plans = new Dictionary<string, MyobCompanyPlan>(StringComparer.OrdinalIgnoreCase);
        if (companyUsers.Count == 0)
        {
            return ApiResponse<MyobTimesheetComputation>.OK(new MyobTimesheetComputation(
                dto, connection, start, end, weeks, today, plans, new Dictionary<string, EmployeeMyobLink>(), mappingByKey));
        }

        // ② 只加载这些员工本期有数据的分店（总部账号挂 40～50 个分店，但多数没有排班）
        var dataPairs = await _source.FindDataPairsAsync(companyUsers, start, weeks);
        var storeCodes = dataPairs.Select(pair => pair.StoreCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var forbiddenStores = storeCodes.Where(code => !scope.CanAccessStoreCode(code)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var storeData = new Dictionary<string, PayrollPushStoreData>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in storeCodes.Where(code => !forbiddenStores.Contains(code)))
        {
            var usersHere = dataPairs
                .Where(pair => pair.StoreCode.Equals(code, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.UserGuid)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            storeData[code] = await _source.GetStoreDataAsync(code, start, weeks, usersHere);
        }

        var links = (await _db.Queryable<EmployeeMyobLink>()
                .Where(item => item.ConnectionGuid == connectionGuid && item.UnboundAt == null)
                .ToListAsync())
            .ToDictionary(item => item.UserGuid, StringComparer.OrdinalIgnoreCase);

        // ③ 逐员工规划
        var rows = new List<MyobPreviewRowDto>();
        var hints = new List<MyobPreviewHintDto>();
        var storeEmployees = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var userGuid in companyUsers)
        {
            var built = BuildRow(userGuid, connectionGuid, start, end, weeks, workforce, storeData, forbiddenStores, dataPairs,
                mappingByKey, links, companies);
            if (built is null)
            {
                continue;
            }

            // 「将推送」网格的行名：缓存里有 MYOB 类别名就带上（设计稿里显示 Base Hourly 这类名字），没有则前端回退我方叫法。
            foreach (var gridRow in built.Row.Detail.Pushed)
            {
                if (mappingByKey.GetValueOrDefault(gridRow.Key) is { } uid && categoryNames.TryGetValue(uid, out var categoryName))
                {
                    gridRow.Label = categoryName;
                }
            }

            rows.Add(built.Row);
            if (built.Plan is not null)
            {
                plans[userGuid] = built.Plan;
            }

            hints.AddRange(built.Hints);
            foreach (var code in built.InvolvedStores)
            {
                if (!storeEmployees.TryGetValue(code, out var set))
                {
                    storeEmployees[code] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                set.Add(userGuid);
            }
        }

        // ④ MYOB 现状（可选，逐员工只读一次 Timesheet）
        dto.Readiness.MyobChecked = checkMyob;
        if (checkMyob)
        {
            await CheckMyobAsync(connection, rows, links, start, end, dto.Readiness, cancellationToken);
        }

        foreach (var row in rows)
        {
            Finalize(row);
        }

        await FillPushStatesAsync(connection, start, end, rows, plans, links, mappingByKey);

        // ⑤ 就绪卡：涉及分店的锁定情况
        foreach (var (code, data) in storeData.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!storeEmployees.ContainsKey(code))
            {
                continue;
            }

            if (data.IsLocked)
            {
                dto.Readiness.LockedStores.Add(code);
            }
            else
            {
                dto.Readiness.UnlockedStores.Add(new MyobUnlockedStoreDto
                {
                    StoreCode = code,
                    StoreName = data.StoreName,
                    EmployeeCount = storeEmployees[code].Count,
                });
            }
        }

        dto.Rows = rows
            .OrderBy(row => StatusRank(row.Status))
            .ThenBy(row => row.UserName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        dto.Hints = hints;
        dto.Totals = new MyobPreviewTotalsDto
        {
            ReadyCount = rows.Count(row => row.Status == MyobPreviewStatuses.Ready),
            ReadyHours = PayrollCalculator.Round2(rows.Where(row => row.Status == MyobPreviewStatuses.Ready).Sum(row => row.PushedTotal)),
            BlockedCount = rows.Count(row => row.Status == MyobPreviewStatuses.Blocked),
            NoTransferCount = rows.Count(row => row.Status == MyobPreviewStatuses.NoTransfer),
        };
        return ApiResponse<MyobTimesheetComputation>.OK(new MyobTimesheetComputation(
            dto, connection, start, end, weeks, today, plans, links, mappingByKey));
    }

    // ───────────────────────── 推送状态 ─────────────────────────

    /// <summary>
    /// 每行的「MYOB 状态」（方案 4.3）：取该员工在 (公司文件, 周期) 的最近一条推送记录，
    /// 成功记录的请求哈希 = 现在规划出的哈希、且涉及分店仍是锁定状态 → 已推送；否则 → 需重新推送。
    /// 读推送表失败（例如迁移还没执行）不影响预览本身，统一当作「未推送」。
    /// </summary>
    private async Task FillPushStatesAsync(
        MyobConnection connection,
        DateTime start,
        DateTime end,
        IReadOnlyList<MyobPreviewRowDto> rows,
        IReadOnlyDictionary<string, MyobCompanyPlan> plans,
        IReadOnlyDictionary<string, EmployeeMyobLink> links,
        IReadOnlyDictionary<string, string?> mappingByKey)
    {
        if (rows.Count == 0)
        {
            return;
        }

        List<MyobTimesheetPush> records;
        try
        {
            records = await _db.Queryable<MyobTimesheetPush>()
                .Where(item => item.ConnectionGuid == connection.ConnectionGuid && item.PeriodStart == start)
                .ToListAsync();
        }
        catch (Exception)
        {
            return;
        }

        // 取最近一条「反映 MYOB 现状」的记录：Running 还没有结果；SkippedHasEntries 只是「没写」，不能盖住之前的成功推送
        // （否则工时变了、重推被跳过后，界面会把 MYOB 里还留着的旧工时显示成「未推送」）。
        var latest = records
            .Where(item => item.Status != MyobPushStatuses.Running && item.Status != MyobPushStatuses.SkippedHasEntries)
            .GroupBy(item => item.UserGuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.StartedAt).First(), StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!latest.TryGetValue(row.UserGuid, out var record))
            {
                continue;
            }

            var push = row.Push;
            push.LastStatus = record.Status;
            push.LastBatchGuid = record.BatchGuid;
            push.LastPushedAt = record.FinishedAt ?? record.StartedAt;
            switch (record.Status)
            {
                case MyobPushStatuses.Pushed or MyobPushStatuses.UpToDate:
                    {
                        // 周期被解锁（有「未锁定」拦截）也算需重新推送：MYOB 里还留着旧工时。
                        var unlocked = row.Blockers.Any(blocker => blocker.Code == MyobBlockerCodes.StoreNotLocked);
                        var hash = CurrentHash(connection, start, end, row.UserGuid, plans, links, mappingByKey);
                        push.NeedsRepush = unlocked || hash is null || !string.Equals(hash, record.RequestHash, StringComparison.Ordinal);
                        push.State = push.NeedsRepush ? MyobPushStates.NeedsRepush : MyobPushStates.Pushed;
                        break;
                    }
                case MyobPushStatuses.SkippedProcessed:
                    push.State = MyobPushStates.ProcessedSkipped;
                    break;
                case MyobPushStatuses.Failed:
                    push.State = MyobPushStates.Failed;
                    break;
                case MyobPushStatuses.Mismatch:
                    push.State = MyobPushStates.Mismatch;
                    break;
                default:
                    // Undone 等：MYOB 里没有我们的工时，按未推送显示，LastStatus 保留供界面提示。
                    push.State = MyobPushStates.NotPushed;
                    break;
            }
        }
    }

    private static string? CurrentHash(
        MyobConnection connection,
        DateTime start,
        DateTime end,
        string userGuid,
        IReadOnlyDictionary<string, MyobCompanyPlan> plans,
        IReadOnlyDictionary<string, EmployeeMyobLink> links,
        IReadOnlyDictionary<string, string?> mappingByKey)
    {
        if (!plans.TryGetValue(userGuid, out var plan) || !links.TryGetValue(userGuid, out var link))
        {
            return null;
        }

        var (cells, unmapped) = MyobTimesheetPushRules.BuildPlannedCells(plan, mappingByKey);
        return unmapped.Count > 0
            ? null
            : MyobTimesheetPushRules.ComputeRequestHash(connection.ConnectionGuid, link.MyobEmployeeUid, start, end, cells);
    }

    // ───────────────────────── 单个员工 ─────────────────────────

    private sealed record BuiltRow(
        MyobPreviewRowDto Row, List<MyobPreviewHintDto> Hints, HashSet<string> InvolvedStores, MyobCompanyPlan? Plan);

    private BuiltRow? BuildRow(
        string userGuid,
        string connectionGuid,
        DateTime start,
        DateTime end,
        int weeks,
        Workforce workforce,
        IReadOnlyDictionary<string, PayrollPushStoreData> storeData,
        ISet<string> forbiddenStores,
        IReadOnlyList<(string UserGuid, string StoreCode)> dataPairs,
        IReadOnlyDictionary<string, string?> mappingByKey,
        IReadOnlyDictionary<string, EmployeeMyobLink> links,
        IReadOnlyDictionary<string, string> companyNames)
    {
        var items = new List<MyobWorkItem>();
        var unmapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var involved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allWork = new List<PayrollWorkItem>();
        var allLeave = new List<PayrollLeaveItem>();
        PayrollPushEmployee? profile = null;

        foreach (var (code, data) in storeData)
        {
            var employee = data.Employees.FirstOrDefault(item => item.UserGuid.Equals(userGuid, StringComparison.OrdinalIgnoreCase));
            if (employee is null)
            {
                continue;
            }

            profile ??= employee;
            var employer = EmployerOf(workforce, userGuid, code, start);
            var storeItems = ToItems(employee, employer ?? string.Empty, code);
            if (storeItems.Count == 0)
            {
                continue;
            }

            involved.Add(code);
            allWork.AddRange(employee.WorkItems);
            allLeave.AddRange(employee.Sick);
            allLeave.AddRange(employee.PublicHolidayPay);
            allLeave.AddRange(employee.Annual);
            allLeave.AddRange(employee.AnnualCashOut);
            if (employer is null)
            {
                unmapped.Add(code);
                continue;
            }

            items.AddRange(storeItems);
        }

        var forbidden = dataPairs
            .Where(pair => pair.UserGuid.Equals(userGuid, StringComparison.OrdinalIgnoreCase) && forbiddenStores.Contains(pair.StoreCode))
            .Select(pair => pair.StoreCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (items.Count == 0 && unmapped.Count == 0 && forbidden.Count == 0)
        {
            return null; // 本期没有任何数据，不出现在预览里
        }

        var rates = profile?.Rates ?? [];
        var rate = PayrollCalculator.ResolveRate(rates, end) ?? rates.OrderBy(item => item.EffectiveFrom).FirstOrDefault();
        var name = profile?.UserName ?? workforce.Users.GetValueOrDefault(userGuid)?.Name ?? userGuid;
        var row = new MyobPreviewRowDto
        {
            UserGuid = userGuid,
            UserName = name,
            PayMode = rate?.PayMode,
            FixedTransferHoursPerWeek = rate?.FixedTransferHoursPerWeek ?? 0m,
            MyobName = links.TryGetValue(userGuid, out var link) ? link.MyobName : null,
        };
        var hints = new List<MyobPreviewHintDto>();
        var blockers = row.Blockers;

        // 没有时薪记录：不知道转账 / 现金怎么分，不能规划
        var payMode = rate is null ? null : (PayrollCalculator.IsValidPayMode(rate.PayMode) ? rate.PayMode : PayrollCalculator.PayModeTransfer);
        // 临时工（Casual）周末推到单独的 MYOB 类别；员工类型没填按全职 / 兼职处理。
        var isCasual = string.Equals(profile?.EmploymentType, "Casual", StringComparison.OrdinalIgnoreCase);
        var plan = payMode is null
            ? null
            : MyobTimesheetPlanner.Plan(payMode, rate!.FixedTransferHoursPerWeek, weeks, items, isCasual);
        if (payMode is null && items.Count > 0)
        {
            blockers.Add(new MyobPreviewBlockerDto { Code = MyobBlockerCodes.NoRate, Message = "本期没有生效的时薪记录，无法确定转账 / 现金分配" });
        }

        MyobCompanyPlan? mine = null;
        plan?.Companies.TryGetValue(connectionGuid, out mine);
        var days = Enumerable.Range(0, weeks * 7).Select(offset => start.AddDays(offset)).ToList();
        row.Detail.Days = days.Select(Format).ToList();

        // 本公司名下的实际工时（含该员工在其他公司的份额说明）
        var mineItems = items.Where(item => item.ConnectionGuid.Equals(connectionGuid, StringComparison.OrdinalIgnoreCase)).ToList();
        row.ActualHours = MyobTimesheetPlanner.Hours(mineItems.Sum(item => item.Minutes));
        row.CashOutHours = MyobTimesheetPlanner.Hours(
            mineItems.Where(item => item.Kind == MyobWorkKind.AnnualCashOut).Sum(item => item.Minutes));
        row.StoreLabels = mineItems
            .Select(item => item.StoreCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .Select(workforce.StoreLabel)
            .ToList();
        FillActualGrid(row.Detail, mineItems, days);

        if (plan is not null)
        {
            row.IsCrossCompany = plan.Companies.Count > 1;
            foreach (var (guid, companyPlan) in plan.Companies.OrderByDescending(pair => pair.Value.ActualMinutes))
            {
                row.Detail.Shares.Add(new MyobCompanyShareDto
                {
                    ConnectionGuid = guid,
                    ConnectionName = companyNames.GetValueOrDefault(guid) ?? guid,
                    ActualHours = MyobTimesheetPlanner.Hours(companyPlan.ActualMinutes),
                    AllocatedTransferHours = MyobTimesheetPlanner.Hours(companyPlan.CapacityMinutes),
                });
            }
        }

        if (mine is not null)
        {
            row.AllocatedTransferHours = MyobTimesheetPlanner.Hours(mine.CapacityMinutes);
            FillPlanGrids(row, mine, days);
            row.Detail.AnnualCashOutTransferHours = MyobTimesheetPlanner.Hours(mine.CashOutTransferMinutes);
            var pushedMinutes = mine.PushedMinutes;
            row.Detail.CheckOk = pushedMinutes + mine.CashOutTransferMinutes == mine.CapacityMinutes;
            row.Detail.CheckMessage = row.Detail.CheckOk
                ? $"推送合计 {MyobTimesheetPlanner.Hours(pushedMinutes):0.00} h"
                    + (mine.CashOutTransferMinutes > 0 ? $" + 年假折现占用 {MyobTimesheetPlanner.Hours(mine.CashOutTransferMinutes):0.00} h" : string.Empty)
                    + $" = 本公司分到的转账额度 {MyobTimesheetPlanner.Hours(mine.CapacityMinutes):0.00} h；"
                    + $"转账 + 现金 = {MyobTimesheetPlanner.Hours(mine.ActualMinutes):0.00} h = 实际工时。"
                : $"推送合计 {MyobTimesheetPlanner.Hours(pushedMinutes):0.00} h 与转账额度 {MyobTimesheetPlanner.Hours(mine.CapacityMinutes):0.00} h 对不上，请联系开发核对。";

            row.Pushed.Weekday = MyobTimesheetPlanner.Hours(SumMinutes(mine, MyobPayCategoryKeys.Weekday));
            // 汇总列按「周六 / 周日」口径，全职 / 兼职与临时工两个类别合并；分开的数在日网格里。
            row.Pushed.Saturday = MyobTimesheetPlanner.Hours(
                SumMinutes(mine, MyobPayCategoryKeys.Saturday) + SumMinutes(mine, MyobPayCategoryKeys.SaturdayCasual));
            row.Pushed.Sunday = MyobTimesheetPlanner.Hours(
                SumMinutes(mine, MyobPayCategoryKeys.Sunday) + SumMinutes(mine, MyobPayCategoryKeys.SundayCasual));
            row.Pushed.PersonalLeave = MyobTimesheetPlanner.Hours(SumMinutes(mine, MyobPayCategoryKeys.PersonalLeave));
            row.Pushed.HolidayLeave = MyobTimesheetPlanner.Hours(SumMinutes(mine, MyobPayCategoryKeys.HolidayLeave));
            row.PushedTotal = MyobTimesheetPlanner.Hours(pushedMinutes);
        }
        else
        {
            row.Detail.CheckOk = true;
            row.Detail.CheckMessage = "本期该员工在本公司名下没有工时。";
        }

        // 拦截项
        foreach (var code in involved.OrderBy(code => code, StringComparer.OrdinalIgnoreCase))
        {
            if (storeData.TryGetValue(code, out var data) && !data.IsLocked)
            {
                blockers.Add(new MyobPreviewBlockerDto
                {
                    Code = MyobBlockerCodes.StoreNotLocked,
                    Message = $"{workforce.StoreLabel(code)} 本期还没有提交锁定",
                    Ref = code,
                });
            }
        }

        foreach (var code in unmapped.OrderBy(code => code, StringComparer.OrdinalIgnoreCase))
        {
            blockers.Add(new MyobPreviewBlockerDto
            {
                Code = MyobBlockerCodes.StoreNotMapped,
                Message = $"{workforce.StoreLabel(code)} 还没有映射到公司文件，算不出发薪公司",
                Ref = code,
            });
        }

        foreach (var code in forbidden)
        {
            blockers.Add(new MyobPreviewBlockerDto
            {
                Code = MyobBlockerCodes.NoStoreAccess,
                Message = $"没有权限查看 {workforce.StoreLabel(code)}，该员工的数据不完整",
                Ref = code,
            });
        }

        if (!links.ContainsKey(userGuid))
        {
            blockers.Add(new MyobPreviewBlockerDto { Code = MyobBlockerCodes.NotBound, Message = "员工还没有和这个公司文件里的 MYOB 员工卡绑定" });
        }

        if (mine is not null)
        {
            foreach (var key in mine.Pushed.Select(line => line.CategoryKey).Distinct().OrderBy(key => Array.IndexOf(MyobPayCategoryKeys.All, key)))
            {
                if (mappingByKey.GetValueOrDefault(key) is null)
                {
                    blockers.Add(new MyobPreviewBlockerDto
                    {
                        Code = MyobBlockerCodes.CategoryNotMapped,
                        Message = $"要推送的「{CategoryLabel(key)}」工时还没有映射 MYOB 工资类别",
                        Ref = key,
                    });
                }
            }
        }

        // 提示项
        AddHints(hints, row, userGuid, name, rate, profile, end, mineItems, allWork, allLeave);
        row.HintCodes = hints.Select(hint => hint.Code).Distinct().ToList();
        return new BuiltRow(row, hints, involved, mine);
    }

    private static void AddHints(
        List<MyobPreviewHintDto> hints,
        MyobPreviewRowDto row,
        string userGuid,
        string name,
        PayrollRate? rate,
        PayrollPushEmployee? profile,
        DateTime periodEnd,
        IReadOnlyList<MyobWorkItem> mineItems,
        IReadOnlyList<PayrollWorkItem> allWork,
        IReadOnlyList<PayrollLeaveItem> allLeave)
    {
        void Add(string code, string message) =>
            hints.Add(new MyobPreviewHintDto { Code = code, UserGuid = userGuid, UserName = name, Message = message });

        // 生日换档：沿用时薪表的判断（最近一次生日已过 60 天内，而当前时薪记录生效在那次生日之前）
        if (profile?.Birthday is { } birthday && profile.AgeYears.HasValue && rate is not null)
        {
            var lastBirthday = new DateTime(
                periodEnd.Year, birthday.Month, Math.Min(birthday.Day, DateTime.DaysInMonth(periodEnd.Year, birthday.Month)));
            if (lastBirthday > periodEnd)
            {
                lastBirthday = lastBirthday.AddYears(-1);
            }

            if (rate.EffectiveFrom.Date < lastBirthday && lastBirthday > periodEnd.AddDays(-60))
            {
                Add(MyobHintCodes.BirthdayReview,
                    $"{name} 已于 {lastBirthday:yyyy-MM-dd} 满 {lastBirthday.Year - birthday.Year} 岁：请先在我方时薪表新增一条生效记录，并同时在 MYOB 里改时薪。");
            }
        }

        // 低于最低时薪：直接用工资表的计算器判断（含所有分店的工时）
        if (rate is not null && (allWork.Count > 0 || allLeave.Count > 0))
        {
            var pay = PayrollCalculator.CalculatePay(new PayrollPayInput
            {
                PeriodStart = periodEnd.AddDays(-(row.Detail.Days.Count - 1)),
                Weeks = Math.Max(1, row.Detail.Days.Count / 7),
                Rates = profile!.Rates,
                WorkItems = allWork,
                LeaveItems = allLeave,
            });
            if (pay.Warnings.Contains(PayrollCalculator.WarningBelowMinimumRate))
            {
                Add(MyobHintCodes.BelowMinimumRate, $"{name} 本期用到的时薪低于最低时薪：请核对时薪记录，并在 MYOB 里确认时薪。");
            }
        }

        var hasHolidayHours = mineItems.Any(item => item.Kind is MyobWorkKind.PublicHolidayWork or MyobWorkKind.PublicHolidayPay);
        if (hasHolidayHours && rate is not null && rate.PayMode != PayrollCalculator.PayModeCash
            && rate.TransferPublicHolidayRate != rate.TransferWeekdayRate)
        {
            Add(MyobHintCodes.PublicHolidayRateMismatch,
                $"{name} 本期有公假工时，但我方公假价与平日价不一致：公假按平日类别推送，请新建时薪记录对齐。");
        }

        if (row.CashOutHours > 0)
        {
            Add(MyobHintCodes.AnnualCashOutNotPushed,
                $"{name} 本期有年假折现 {row.CashOutHours:0.00} h：不会推送，请财务在 MYOB 手工处理。");
        }
    }

    // ───────────────────────── 明细网格 ─────────────────────────

    private static List<MyobWorkItem> ToItems(PayrollPushEmployee employee, string connectionGuid, string storeCode)
    {
        var items = new List<MyobWorkItem>();
        foreach (var work in employee.WorkItems.Where(item => item.Minutes > 0))
        {
            var kind = work.Bucket switch
            {
                PayrollBucket.Saturday => MyobWorkKind.Saturday,
                PayrollBucket.Sunday => MyobWorkKind.Sunday,
                PayrollBucket.PublicHoliday => MyobWorkKind.PublicHolidayWork,
                _ => MyobWorkKind.Weekday,
            };
            items.Add(new MyobWorkItem(connectionGuid, storeCode, work.Date.Date, kind, work.Minutes));
        }

        AddLeave(items, employee.Sick, MyobWorkKind.Sick, connectionGuid, storeCode);
        AddLeave(items, employee.PublicHolidayPay, MyobWorkKind.PublicHolidayPay, connectionGuid, storeCode);
        AddLeave(items, employee.Annual, MyobWorkKind.Annual, connectionGuid, storeCode);
        AddLeave(items, employee.AnnualCashOut, MyobWorkKind.AnnualCashOut, connectionGuid, storeCode);
        return items;
    }

    private static void AddLeave(
        List<MyobWorkItem> items, IReadOnlyList<PayrollLeaveItem> leave, MyobWorkKind kind, string connectionGuid, string storeCode)
    {
        foreach (var item in leave.Where(item => item.Hours > 0))
        {
            items.Add(new MyobWorkItem(
                connectionGuid, storeCode, item.Date.Date, kind,
                (int)Math.Round(item.Hours * 60m, MidpointRounding.AwayFromZero)));
        }
    }

    private static void FillActualGrid(MyobPreviewDetailDto detail, IReadOnlyList<MyobWorkItem> items, IReadOnlyList<DateTime> days)
    {
        foreach (var kind in Enum.GetValues<MyobWorkKind>())
        {
            var perDay = days
                .Select(day => items.Where(item => item.Kind == kind && item.Date.Date == day.Date).Sum(item => item.Minutes))
                .ToList();
            if (perDay.Sum() == 0)
            {
                continue;
            }

            detail.Actual.Add(ToRow(MyobTimesheetPlanner.KindKey(kind), perDay));
        }
    }

    private static void FillPlanGrids(MyobPreviewRowDto row, MyobCompanyPlan plan, IReadOnlyList<DateTime> days)
    {
        foreach (var key in MyobPayCategoryKeys.All)
        {
            var perDay = days
                .Select(day => plan.Pushed.Where(line => line.CategoryKey == key && line.Date.Date == day.Date).Sum(line => line.Minutes))
                .ToList();
            if (perDay.Sum() > 0)
            {
                row.Detail.Pushed.Add(ToRow(key, perDay));
            }
        }

        foreach (var kind in Enum.GetValues<MyobWorkKind>())
        {
            var perDay = days
                .Select(day => plan.CashByDayKind.GetValueOrDefault((day.Date, kind)))
                .ToList();
            if (perDay.Sum() > 0)
            {
                row.Detail.Cash.Add(ToRow(MyobTimesheetPlanner.KindKey(kind), perDay));
            }
        }
    }

    private static MyobDayGridRowDto ToRow(string key, IReadOnlyList<int> minutesPerDay) => new()
    {
        Key = key,
        Hours = minutesPerDay.Select(MyobTimesheetPlanner.Hours).ToList(),
        Total = MyobTimesheetPlanner.Hours(minutesPerDay.Sum()),
    };

    private static int SumMinutes(MyobCompanyPlan plan, string key) =>
        plan.Pushed.Where(line => line.CategoryKey == key).Sum(line => line.Minutes);

    // ───────────────────────── MYOB 现状 ─────────────────────────

    private async Task CheckMyobAsync(
        MyobConnection connection,
        IReadOnlyList<MyobPreviewRowDto> rows,
        IReadOnlyDictionary<string, EmployeeMyobLink> links,
        DateTime start,
        DateTime end,
        MyobPreviewReadinessDto readiness,
        CancellationToken cancellationToken)
    {
        string token;
        try
        {
            token = await _tokens.GetAccessTokenAsync(connection.ConnectionGuid, cancellationToken);
        }
        catch (MyobApiException)
        {
            foreach (var row in rows.Where(row => links.ContainsKey(row.UserGuid)))
            {
                row.MyobState = MyobTimesheetStates.CheckFailed;
                readiness.MyobCheckFailedCount++;
            }

            return;
        }

        foreach (var row in rows.Where(row => row.PushedTotal > 0 && links.ContainsKey(row.UserGuid)))
        {
            try
            {
                var status = await _client.GetTimesheetStatusAsync(
                    connection.BusinessId, token, links[row.UserGuid].MyobEmployeeUid, start, end, cancellationToken);
                row.MyobState = status.HasProcessedEntries
                    ? MyobTimesheetStates.Processed
                    : status.HasEntries ? MyobTimesheetStates.HasEntries : MyobTimesheetStates.Clear;
                if (status.HasProcessedEntries)
                {
                    readiness.MyobProcessedCount++;
                    row.Blockers.Add(new MyobPreviewBlockerDto
                    {
                        Code = MyobBlockerCodes.MyobProcessed,
                        Message = "MYOB 里该周期已有已发薪（Processed）的记录，不会覆盖",
                    });
                }
            }
            catch (MyobApiException)
            {
                row.MyobState = MyobTimesheetStates.CheckFailed;
                readiness.MyobCheckFailedCount++;
            }
        }
    }

    // ───────────────────────── 状态 / 辅助 ─────────────────────────

    /// <summary>
    /// 状态：有拦截项 = Blocked；没有要推送的转账工时 = NoTransfer（此时「未绑定」「类别未映射」等与推送相关的拦截不再有意义，去掉；
    /// 但没有时薪 / 分店未映射 / 无分店权限说明数据本身不完整，仍保留为 Blocked）。
    /// </summary>
    private static void Finalize(MyobPreviewRowDto row)
    {
        var dataBlockers = new[] { MyobBlockerCodes.NoRate, MyobBlockerCodes.StoreNotMapped, MyobBlockerCodes.NoStoreAccess };
        if (row.PushedTotal <= 0 && !row.Blockers.Any(blocker => dataBlockers.Contains(blocker.Code)))
        {
            row.Blockers.Clear();
            row.Status = MyobPreviewStatuses.NoTransfer;
            return;
        }

        row.Status = row.Blockers.Count > 0 ? MyobPreviewStatuses.Blocked : MyobPreviewStatuses.Ready;
    }

    private static string? EmployerOf(Workforce workforce, string userGuid, string storeCode, DateTime date)
    {
        var defaultConnection = workforce.Stores.TryGetValue(storeCode, out var store) ? store.DefaultConnectionGuid : null;
        var rows = workforce.OverrideRows.Where(row =>
            row.UserGuid.Equals(userGuid, StringComparison.OrdinalIgnoreCase)
            && row.StoreCode.Equals(storeCode, StringComparison.OrdinalIgnoreCase));
        return MyobPayrollEmployerRules.Resolve(defaultConnection, rows, date).ConnectionGuid;
    }

    private static int StatusRank(string status) => status switch
    {
        MyobPreviewStatuses.Ready => 0,
        MyobPreviewStatuses.Blocked => 1,
        _ => 2,
    };

    private static string CategoryLabel(string key) => key switch
    {
        MyobPayCategoryKeys.Weekday => "平日",
        MyobPayCategoryKeys.Saturday => "周六",
        MyobPayCategoryKeys.Sunday => "周日",
        MyobPayCategoryKeys.SaturdayCasual => "周六（临时工）",
        MyobPayCategoryKeys.SundayCasual => "周日（临时工）",
        MyobPayCategoryKeys.PersonalLeave => "病假",
        _ => "年假",
    };

    private static string Format(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
