using BlazorApp.Api.Data;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.Myob;

internal sealed record WorkforceUser(
    string UserGuid,
    string Name,
    string? Username,
    string? Email,
    string? GivenName,
    string? FamilyName,
    string? FullName,
    string? Phone,
    DateTime? Birthday,
    EmployeeType? EmployeeType,
    DateTime CreatedAt);

internal sealed record WorkforceStore(string StoreCode, string StoreName, string? DefaultConnectionGuid);

/// <summary>员工 × 上班分店 一对：默认公司（分店映射）与今天实际生效的发薪公司。</summary>
internal sealed record WorkforcePair(
    string UserGuid,
    string StoreCode,
    string? DefaultConnectionGuid,
    string? EffectiveConnectionGuid,
    bool IsOverride,
    DateTime? OverrideFrom);

internal sealed class Workforce
{
    public Dictionary<string, WorkforceUser> Users { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, WorkforceStore> Stores { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<WorkforcePair> Pairs { get; init; } = [];
    public List<EmployeePayrollEmployer> OverrideRows { get; init; } = [];

    public string StoreLabel(string storeCode) =>
        Stores.TryGetValue(storeCode, out var store) && !string.IsNullOrWhiteSpace(store.StoreName)
            ? $"{store.StoreCode} {store.StoreName}"
            : storeCode;

    /// <summary>某员工的上班分店标签，按「由 connectionGuid 发薪的分店优先、再按店号」排序。</summary>
    public List<string> StoreLabelsOf(string userGuid, string? preferConnectionGuid = null) =>
        Pairs.Where(pair => pair.UserGuid.Equals(userGuid, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => preferConnectionGuid is not null && pair.EffectiveConnectionGuid == preferConnectionGuid ? 0 : 1)
            .ThenBy(pair => pair.StoreCode, StringComparer.OrdinalIgnoreCase)
            .Select(pair => StoreLabel(pair.StoreCode))
            .ToList();
}

/// <summary>发薪公司解析规则（方案第 7 节）：纯函数，便于单测。</summary>
internal static class MyobPayrollEmployerRules
{
    /// <summary>
    /// 某员工在某分店、某日的发薪公司：取 EffectiveFrom ≤ 日期 的最新一行；该行指定了公司 → 覆盖；
    /// 该行 ConnectionGuid 为空（改回按分店）或没有任何行 → 分店所属公司（可能为 null＝分店还没映射）。
    /// </summary>
    internal static (string? ConnectionGuid, bool IsOverride, DateTime? From) Resolve(
        string? defaultConnectionGuid,
        IEnumerable<EmployeePayrollEmployer> rowsOfUserAndStore,
        DateTime date)
    {
        var current = rowsOfUserAndStore
            .Where(row => row.EffectiveFrom.Date <= date.Date)
            .OrderByDescending(row => row.EffectiveFrom)
            .FirstOrDefault();
        if (current is null)
        {
            return (defaultConnectionGuid, false, null);
        }

        return string.IsNullOrWhiteSpace(current.ConnectionGuid)
            ? (defaultConnectionGuid, false, current.EffectiveFrom.Date)
            : (current.ConnectionGuid, true, current.EffectiveFrom.Date);
    }
}

/// <summary>读出「谁在哪些分店上班、各自由哪家公司发薪」。员工绑定与发薪公司设置共用。</summary>
internal sealed class MyobWorkforceLoader
{
    private const int InChunk = 500;
    private readonly ISqlSugarClient _db;

    public MyobWorkforceLoader(ISqlSugarClient db) => _db = db;

    public async Task<Workforce> LoadAsync(DateTime today)
    {
        var assignments = await _db.Queryable<User>()
            .InnerJoin<UserStore>((u, us) => u.UserGUID == us.UserGUID)
            .InnerJoin<Store>((u, us, s) => us.StoreGUID == s.StoreGUID)
            .Where((u, us, s) => !u.IsDeleted && u.IsActive && !us.IsDeleted && !s.IsDeleted)
            .Select((u, us, s) => new
            {
                u.UserGUID, u.Username, u.FullName, u.GivenName, u.FamilyName, u.Email, u.CreatedAt,
                s.StoreCode, s.StoreName,
            })
            .ToListAsync();

        var storeRows = await _db.Queryable<Store>()
            .Where(store => !store.IsDeleted)
            .Select(store => new { store.StoreCode, store.StoreName })
            .ToListAsync();
        var mappings = (await _db.Queryable<MyobStoreMapping>().ToListAsync())
            .ToDictionary(item => item.StoreCode, item => item.ConnectionGuid, StringComparer.OrdinalIgnoreCase);

        var stores = storeRows
            .GroupBy(item => item.StoreCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new WorkforceStore(
                    group.Key,
                    group.First().StoreName,
                    mappings.GetValueOrDefault(group.Key)),
                StringComparer.OrdinalIgnoreCase);

        var userGuids = assignments.Select(item => item.UserGUID).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var profiles = new Dictionary<string, EmployeeProfile>(StringComparer.OrdinalIgnoreCase);
        var minorBirthdays = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in userGuids.Chunk(InChunk))
        {
            var guids = chunk.ToList();
            foreach (var profile in await _db.Queryable<EmployeeProfile>()
                         .Where(item => !item.IsDeleted && guids.Contains(item.UserGUID))
                         .ToListAsync())
            {
                profiles[profile.UserGUID] = profile;
            }

            foreach (var minor in await _db.Queryable<EmployeeMinorCompliance>()
                         .Where(item => !item.IsDeleted && guids.Contains(item.UserGUID))
                         .ToListAsync())
            {
                if (minor.DateOfBirth.HasValue)
                {
                    minorBirthdays[minor.UserGUID] = minor.DateOfBirth.Value;
                }
            }
        }

        var users = assignments
            .GroupBy(item => item.UserGUID, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var first = group.First();
                    profiles.TryGetValue(first.UserGUID, out var profile);
                    var birthday = profile?.Birthday ?? (minorBirthdays.TryGetValue(first.UserGUID, out var dob) ? dob : null);
                    var name = string.IsNullOrWhiteSpace(first.FullName) ? first.Username : first.FullName!;
                    return new WorkforceUser(
                        first.UserGUID, name, first.Username, first.Email, first.GivenName, first.FamilyName,
                        first.FullName, profile?.Phone, birthday, profile?.EmployeeType, first.CreatedAt);
                },
                StringComparer.OrdinalIgnoreCase);

        var overrideRows = await _db.Queryable<EmployeePayrollEmployer>().ToListAsync();
        var overrideLookup = overrideRows
            .GroupBy(row => $"{row.UserGuid}|{row.StoreCode}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var pairs = new List<WorkforcePair>();
        foreach (var group in assignments.GroupBy(item => $"{item.UserGUID}|{item.StoreCode}", StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var defaultConnection = mappings.GetValueOrDefault(first.StoreCode);
            overrideLookup.TryGetValue(group.Key, out var rows);
            var resolved = MyobPayrollEmployerRules.Resolve(defaultConnection, rows ?? [], today);
            pairs.Add(new WorkforcePair(first.UserGUID, first.StoreCode, defaultConnection, resolved.ConnectionGuid, resolved.IsOverride, resolved.From));
        }

        return new Workforce { Users = users, Stores = stores, Pairs = pairs, OverrideRows = overrideRows };
    }
}
