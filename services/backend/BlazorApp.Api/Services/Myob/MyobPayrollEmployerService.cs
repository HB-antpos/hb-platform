using System.Globalization;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.Myob;

public interface IMyobPayrollEmployerService
{
    Task<ApiResponse<MyobPayrollEmployerListDto>> ListAsync(
        string? keyword, string? storeCode, bool crossCompanyOnly, CancellationToken cancellationToken);

    Task<ApiResponse<MyobPayrollEmployerDetailDto>> GetDetailAsync(string userGuid, CancellationToken cancellationToken);

    /// <summary>已启用（Active）的公司文件，给员工绑定 / 发薪公司页签的公司选择条用。</summary>
    Task<List<MyobPayrollCompanyOptionDto>> ListCompaniesAsync();

    Task<ApiResponse<MyobPayrollEmployerDetailDto>> SaveAsync(
        string userGuid, MyobSavePayrollEmployersRequest request, string actorUserGuid, CancellationToken cancellationToken);
}

/// <summary>
/// 发薪公司设置（方案第 7 节）：规则「按员工 × 上班分店」决定，默认＝分店所属公司；
/// 只有「在 A 店上班、由 B 公司发薪」这类例外才写一行覆盖。覆盖只新增、按生效日期留历史，
/// 生效日期必须是工资周期起始日且不早于当前期——同一分店的工时整期归同一家公司，不会被周期中途切换拆开。
/// </summary>
public sealed class MyobPayrollEmployerService : IMyobPayrollEmployerService
{
    /// <summary>生效日期下拉里提供的周期数（当前期 + 之后 3 期）。</summary>
    private const int AllowedPeriodCount = 4;

    private readonly ISqlSugarClient _db;
    private readonly ICurrentUserManageableStoreScopeService _scope;
    private readonly TimeProvider _time;

    public MyobPayrollEmployerService(
        SqlSugarContext context,
        ICurrentUserManageableStoreScopeService scope,
        TimeProvider? timeProvider = null)
        : this(context.Db, scope, timeProvider)
    {
    }

    internal MyobPayrollEmployerService(
        ISqlSugarClient db,
        ICurrentUserManageableStoreScopeService scope,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _scope = scope;
        _time = timeProvider ?? TimeProvider.System;
    }

    // ───────────────────────── 列表 ─────────────────────────

    public async Task<ApiResponse<MyobPayrollEmployerListDto>> ListAsync(
        string? keyword,
        string? storeCode,
        bool crossCompanyOnly,
        CancellationToken cancellationToken)
    {
        var scope = await _scope.GetScopeAsync();
        if (!scope.IsAllowed)
        {
            return ApiResponse<MyobPayrollEmployerListDto>.Error(scope.Message, MyobPayrollErrorCodes.Forbidden);
        }

        var today = _time.GetUtcNow().UtcDateTime.Date;
        var workforce = await new MyobWorkforceLoader(_db).LoadAsync(today);
        var companies = await LoadCompaniesAsync();
        var companyNames = companies.ToDictionary(item => item.ConnectionGuid, item => item.Name, StringComparer.OrdinalIgnoreCase);

        var term = keyword?.Trim();
        var store = storeCode?.Trim();
        var rows = workforce.Pairs
            .Where(pair => scope.CanAccessStoreCode(pair.StoreCode))
            .Where(pair => string.IsNullOrEmpty(store) || pair.StoreCode.Equals(store, StringComparison.OrdinalIgnoreCase))
            .Where(pair => string.IsNullOrEmpty(term)
                || workforce.Users[pair.UserGuid].Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Select(pair => ToRow(pair, workforce, companyNames))
            .Where(row => !crossCompanyOnly || row.IsCrossCompany)
            .OrderBy(row => row.UserName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.StoreCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return ApiResponse<MyobPayrollEmployerListDto>.OK(new MyobPayrollEmployerListDto { Rows = rows, Companies = companies });
    }

    // ───────────────────────── 详情 ─────────────────────────

    public async Task<ApiResponse<MyobPayrollEmployerDetailDto>> GetDetailAsync(
        string userGuid,
        CancellationToken cancellationToken)
    {
        var scope = await _scope.GetScopeAsync();
        if (!scope.IsAllowed)
        {
            return ApiResponse<MyobPayrollEmployerDetailDto>.Error(scope.Message, MyobPayrollErrorCodes.Forbidden);
        }

        var today = _time.GetUtcNow().UtcDateTime.Date;
        var workforce = await new MyobWorkforceLoader(_db).LoadAsync(today);
        if (!workforce.Users.ContainsKey(userGuid))
        {
            return ApiResponse<MyobPayrollEmployerDetailDto>.Error("员工不存在或已停用", MyobBindErrorCodes.UserNotFound);
        }

        var pairs = workforce.Pairs.Where(pair => pair.UserGuid.Equals(userGuid, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!pairs.Any(pair => scope.CanAccessStoreCode(pair.StoreCode)))
        {
            return ApiResponse<MyobPayrollEmployerDetailDto>.Error("没有权限查看该员工", MyobPayrollErrorCodes.Forbidden);
        }

        return ApiResponse<MyobPayrollEmployerDetailDto>.OK(await BuildDetailAsync(userGuid, workforce, scope, today));
    }

    private async Task<MyobPayrollEmployerDetailDto> BuildDetailAsync(
        string userGuid,
        Workforce workforce,
        CurrentUserManageableStoreScope scope,
        DateTime today)
    {
        var companies = await LoadCompaniesAsync();
        var companyNames = companies.ToDictionary(item => item.ConnectionGuid, item => item.Name, StringComparer.OrdinalIgnoreCase);
        var pairs = workforce.Pairs
            .Where(pair => pair.UserGuid.Equals(userGuid, StringComparison.OrdinalIgnoreCase))
            .Where(pair => scope.CanAccessStoreCode(pair.StoreCode))
            .OrderBy(pair => pair.StoreCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var links = await _db.Queryable<EmployeeMyobLink>()
            .Where(link => link.UserGuid == userGuid && link.UnboundAt == null)
            .ToListAsync();
        var history = workforce.OverrideRows
            .Where(row => row.UserGuid.Equals(userGuid, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(row => row.EffectiveFrom)
            .ThenBy(row => row.StoreCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var actorNames = await LoadActorNamesAsync(history.Select(row => row.CreatedByUserGuid));

        return new MyobPayrollEmployerDetailDto
        {
            UserGuid = userGuid,
            UserName = workforce.Users[userGuid].Name,
            Rows = pairs.Select(pair => ToRow(pair, workforce, companyNames)).ToList(),
            Companies = companies,
            BindingStates = companies
                .Select(company => new MyobPayrollBindingStateDto
                {
                    ConnectionGuid = company.ConnectionGuid,
                    Bound = links.Any(link => link.ConnectionGuid.Equals(company.ConnectionGuid, StringComparison.OrdinalIgnoreCase)),
                })
                .ToList(),
            AllowedEffectiveFrom = (await ResolveAllowedPeriodStartsAsync(today))
                .Select(date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .ToList(),
            History = history.Select(row => new MyobPayrollEmployerHistoryDto
            {
                StoreCode = row.StoreCode,
                StoreName = workforce.Stores.TryGetValue(row.StoreCode, out var store) ? store.StoreName : row.StoreCode,
                ConnectionGuid = string.IsNullOrWhiteSpace(row.ConnectionGuid) ? null : row.ConnectionGuid,
                ConnectionName = !string.IsNullOrWhiteSpace(row.ConnectionGuid) && companyNames.TryGetValue(row.ConnectionGuid, out var name) ? name : null,
                EffectiveFrom = row.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                CreatedByName = row.CreatedByUserGuid is not null && actorNames.TryGetValue(row.CreatedByUserGuid, out var actor) ? actor : null,
                CreatedAt = row.CreatedAt,
            }).ToList(),
        };
    }

    // ───────────────────────── 保存 ─────────────────────────

    public async Task<ApiResponse<MyobPayrollEmployerDetailDto>> SaveAsync(
        string userGuid,
        MyobSavePayrollEmployersRequest request,
        string actorUserGuid,
        CancellationToken cancellationToken)
    {
        var scope = await _scope.GetScopeAsync();
        if (!scope.IsAllowed)
        {
            return ApiResponse<MyobPayrollEmployerDetailDto>.Error(scope.Message, MyobPayrollErrorCodes.Forbidden);
        }

        var today = _time.GetUtcNow().UtcDateTime.Date;
        var workforce = await new MyobWorkforceLoader(_db).LoadAsync(today);
        if (!workforce.Users.ContainsKey(userGuid))
        {
            return ApiResponse<MyobPayrollEmployerDetailDto>.Error("员工不存在或已停用", MyobBindErrorCodes.UserNotFound);
        }

        if (!DateTime.TryParseExact(request.EffectiveFrom?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var effectiveFrom))
        {
            return ApiResponse<MyobPayrollEmployerDetailDto>.Error("生效日期格式不对，应为 yyyy-MM-dd", MyobPayrollErrorCodes.InvalidEffectiveFrom);
        }

        var allowed = await ResolveAllowedPeriodStartsAsync(today);
        if (!allowed.Contains(effectiveFrom.Date))
        {
            return ApiResponse<MyobPayrollEmployerDetailDto>.Error(
                "生效日期必须是工资周期的起始日，且不早于当前期", MyobPayrollErrorCodes.InvalidEffectiveFrom);
        }

        var userPairs = workforce.Pairs
            .Where(pair => pair.UserGuid.Equals(userGuid, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.StoreCode, StringComparer.OrdinalIgnoreCase);
        var activeCompanies = (await LoadCompaniesAsync())
            .Select(item => item.ConnectionGuid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 先全部校验，再统一写入：任何一项不合法整次保存都不生效。
        var plan = new List<(string StoreCode, string? ConnectionGuid)>();
        foreach (var item in request.Items.GroupBy(item => item.StoreCode?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()))
        {
            var code = item.StoreCode?.Trim() ?? string.Empty;
            if (!userPairs.TryGetValue(code, out var pair))
            {
                return ApiResponse<MyobPayrollEmployerDetailDto>.Error($"该员工不在分店 {code} 上班", MyobPayrollErrorCodes.StoreNotWorked);
            }

            if (!scope.CanAccessStoreCode(code))
            {
                return ApiResponse<MyobPayrollEmployerDetailDto>.Error($"没有权限修改分店 {code}", MyobPayrollErrorCodes.Forbidden);
            }

            var target = string.IsNullOrWhiteSpace(item.ConnectionGuid) ? null : item.ConnectionGuid.Trim();
            if (target is not null && !activeCompanies.Contains(target))
            {
                return ApiResponse<MyobPayrollEmployerDetailDto>.Error("所选公司文件不存在或未启用", MyobPayrollErrorCodes.ConnectionNotActive);
            }

            // 选的就是分店默认公司 ＝ 改回「按分店」，库里记 null，语义更干净。
            if (target is not null && target.Equals(pair.DefaultConnectionGuid, StringComparison.OrdinalIgnoreCase))
            {
                target = null;
            }

            plan.Add((pair.StoreCode, target));
        }

        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var (storeCode, target) in plan)
        {
            var rows = workforce.OverrideRows
                .Where(row => row.UserGuid.Equals(userGuid, StringComparison.OrdinalIgnoreCase)
                    && row.StoreCode.Equals(storeCode, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var sameDay = rows.FirstOrDefault(row => row.EffectiveFrom.Date == effectiveFrom.Date);
            if (sameDay is not null)
            {
                // 同一天已有一行（还没生效的预设）：改它，不新增（(用户, 分店, 日期) 唯一）。
                if (!string.Equals(sameDay.ConnectionGuid, target, StringComparison.OrdinalIgnoreCase))
                {
                    sameDay.ConnectionGuid = target;
                    await _db.Updateable(sameDay).UpdateColumns(row => new { row.ConnectionGuid }).ExecuteCommandAsync();
                }

                continue;
            }

            // 没有变化就不写行，避免历史里堆积无意义的记录。
            var defaultConnection = userPairs[storeCode].DefaultConnectionGuid;
            var current = MyobPayrollEmployerRules.Resolve(defaultConnection, rows, effectiveFrom).ConnectionGuid;
            var desired = target ?? defaultConnection;
            if (string.Equals(current, desired, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await _db.Insertable(new EmployeePayrollEmployer
            {
                EmployerGuid = Guid.NewGuid().ToString(),
                UserGuid = userGuid,
                StoreCode = storeCode,
                ConnectionGuid = target,
                EffectiveFrom = effectiveFrom.Date,
                CreatedByUserGuid = string.IsNullOrWhiteSpace(actorUserGuid) ? null : actorUserGuid,
                CreatedAt = now,
            }).ExecuteCommandAsync();
        }

        // 重新读一遍再返回，前端直接用返回值刷新抽屉。
        workforce = await new MyobWorkforceLoader(_db).LoadAsync(today);
        return ApiResponse<MyobPayrollEmployerDetailDto>.OK(
            await BuildDetailAsync(userGuid, workforce, scope, today), "发薪公司已保存");
    }

    // ───────────────────────── 辅助 ─────────────────────────

    private static MyobPayrollEmployerRowDto ToRow(
        WorkforcePair pair,
        Workforce workforce,
        IReadOnlyDictionary<string, string> companyNames)
    {
        var userPairs = workforce.Pairs
            .Where(item => item.UserGuid.Equals(pair.UserGuid, StringComparison.OrdinalIgnoreCase))
            .ToList();
        // 跨公司：同一员工的上班分店分属 ≥2 家发薪公司，或存在「改由其他公司发」的例外。
        var cross = userPairs.Any(item => item.IsOverride)
            || userPairs.Select(item => item.EffectiveConnectionGuid ?? string.Empty)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
        string? NameOf(string? guid) => guid is not null && companyNames.TryGetValue(guid, out var name) ? name : null;

        return new MyobPayrollEmployerRowDto
        {
            UserGuid = pair.UserGuid,
            UserName = workforce.Users[pair.UserGuid].Name,
            StoreCode = pair.StoreCode,
            StoreName = workforce.Stores.TryGetValue(pair.StoreCode, out var store) ? store.StoreName : pair.StoreCode,
            DefaultConnectionGuid = pair.DefaultConnectionGuid,
            DefaultConnectionName = NameOf(pair.DefaultConnectionGuid),
            EffectiveConnectionGuid = pair.EffectiveConnectionGuid,
            EffectiveConnectionName = NameOf(pair.EffectiveConnectionGuid),
            IsOverride = pair.IsOverride,
            EffectiveFrom = pair.IsOverride && pair.OverrideFrom.HasValue
                ? pair.OverrideFrom.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : null,
            IsCrossCompany = cross,
        };
    }

    public async Task<List<MyobPayrollCompanyOptionDto>> ListCompaniesAsync() => await LoadCompaniesAsync();

    private async Task<List<MyobPayrollCompanyOptionDto>> LoadCompaniesAsync() =>
        (await _db.Queryable<MyobConnection>()
            .Where(item => item.Status == MyobConnectionStatuses.Active)
            .OrderBy(item => item.BusinessName)
            .ToListAsync())
        .Select(item => new MyobPayrollCompanyOptionDto { ConnectionGuid = item.ConnectionGuid, Name = item.BusinessName, Abn = item.Abn })
        .ToList();

    /// <summary>当前周期起始日及之后几期的起始日（升序）。周期长度与双周锚点取考勤设置。</summary>
    private async Task<List<DateTime>> ResolveAllowedPeriodStartsAsync(DateTime today)
    {
        var settings = await _db.Queryable<AttendanceSettings>().FirstAsync();
        var weeks = settings?.DefaultPeriodWeeks == 2 ? 2 : 1;
        var first = AttendanceReactService.ResolvePayPeriodStart(today, weeks, settings?.FortnightAnchorDate);
        return Enumerable.Range(0, AllowedPeriodCount).Select(index => first.AddDays(index * 7 * weeks).Date).ToList();
    }

    private async Task<Dictionary<string, string>> LoadActorNamesAsync(IEnumerable<string?> userGuids)
    {
        var guids = userGuids.Where(guid => !string.IsNullOrWhiteSpace(guid)).Select(guid => guid!).Distinct().ToList();
        if (guids.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        var users = await _db.Queryable<User>()
            .Where(user => guids.Contains(user.UserGUID))
            .Select(user => new { user.UserGUID, user.FullName, user.Username })
            .ToListAsync();
        return users.ToDictionary(
            user => user.UserGUID,
            user => string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName!,
            StringComparer.OrdinalIgnoreCase);
    }
}
