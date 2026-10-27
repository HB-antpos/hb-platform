using System.Collections.Concurrent;
using BlazorApp.Api.Data;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Shared.DTOs;
using Microsoft.Extensions.Caching.Memory;
using SqlSugar;

namespace BlazorApp.Api.Services.Myob;

/// <summary>某个公司文件的 MYOB 员工名册（Contact/Employee 与 EmployeePayrollDetails 合并后的快照）。</summary>
public sealed record MyobEmployeeDirectoryData(
    string ConnectionGuid,
    string ConnectionName,
    DateTime FetchedAt,
    IReadOnlyList<MyobDirectoryCard> Cards);

/// <summary>名册里的一张员工卡：匹配用字段 + 给前端看的 DTO + 用工 / 假期累计信息。</summary>
public sealed record MyobDirectoryCard(MyobMatchCard Match, MyobEmployeeCardDto Dto);

/// <summary>
/// 拉取并缓存 MYOB 员工名册。缓存 10 分钟（MYOB 员工资料变动很慢，页面反复打开不必每次打 MYOB 的 8 次/秒配额）；
/// 「刷新」按钮传 refresh=true 绕过缓存；同一连接的并发请求串行，只打一轮 MYOB。
/// </summary>
public interface IMyobEmployeeDirectory
{
    /// <exception cref="MyobApiException">连接不存在 / 需要重新授权 / MYOB 暂时不可用。</exception>
    Task<MyobEmployeeDirectoryData> GetAsync(string connectionGuid, bool refresh, CancellationToken cancellationToken);
}

internal sealed class MyobEmployeeDirectory : IMyobEmployeeDirectory
{
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    private readonly ISqlSugarClient _db;
    private readonly IMyobApiClient _client;
    private readonly IMyobTokenProvider _tokens;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _time;

    public MyobEmployeeDirectory(
        SqlSugarContext context,
        IMyobApiClient client,
        IMyobTokenProvider tokens,
        IMemoryCache cache,
        TimeProvider? timeProvider = null)
        : this(context.Db, client, tokens, cache, timeProvider)
    {
    }

    internal MyobEmployeeDirectory(
        ISqlSugarClient db,
        IMyobApiClient client,
        IMyobTokenProvider tokens,
        IMemoryCache cache,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _client = client;
        _tokens = tokens;
        _cache = cache;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<MyobEmployeeDirectoryData> GetAsync(
        string connectionGuid,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var key = CacheKey(connectionGuid);
        MyobEmployeeDirectoryData? cached;
        if (!refresh && _cache.TryGetValue(key, out cached) && cached is not null)
        {
            return cached;
        }

        var gate = Locks.GetOrAdd(connectionGuid, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // 排队期间别人可能已经拉过一轮：非强制刷新就直接用，避免重复打 MYOB。
            if (!refresh && _cache.TryGetValue(key, out cached) && cached is not null)
            {
                return cached;
            }

            var connection = await _db.Queryable<MyobConnection>().FirstAsync(item => item.ConnectionGuid == connectionGuid)
                ?? throw new MyobApiException(MyobErrorCodes.ConnectionNotFound, "MYOB 连接不存在");
            var accessToken = await _tokens.GetAccessTokenAsync(connectionGuid, cancellationToken);
            var employees = await _client.GetEmployeesAsync(connection.BusinessId, accessToken, cancellationToken);
            var payroll = await _client.GetEmployeePayrollDetailsAsync(connection.BusinessId, accessToken, cancellationToken);

            var data = Build(connection, employees, payroll, _time.GetUtcNow().UtcDateTime);
            _cache.Set(key, data, CacheLifetime);
            return data;
        }
        finally
        {
            gate.Release();
        }
    }

    internal static string CacheKey(string connectionGuid) => $"myob:employees:{connectionGuid}";

    internal static MyobEmployeeDirectoryData Build(
        MyobConnection connection,
        IReadOnlyList<MyobEmployeeRecord> employees,
        IReadOnlyList<MyobEmployeePayrollRecord> payroll,
        DateTime fetchedAt)
    {
        var payrollByUid = payroll
            .GroupBy(item => item.EmployeeUid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var cards = employees
            .Select(employee =>
            {
                payrollByUid.TryGetValue(employee.Uid, out var detail);
                var hasAnnual = detail is not null && HasAssigned(detail, IsAnnualCategory);
                var hasSick = detail is not null && HasAssigned(detail, IsSickCategory);
                var name = DisplayName(employee);
                var match = new MyobMatchCard(
                    employee.Uid, employee.FirstName, employee.LastName, employee.CompanyName,
                    employee.IsIndividual, employee.Emails, employee.Phones, detail?.DateOfBirth, employee.IsActive);
                var dto = new MyobEmployeeCardDto
                {
                    Uid = employee.Uid,
                    DisplayId = employee.DisplayId,
                    Name = name,
                    Email = employee.Emails.FirstOrDefault(),
                    PhoneMasked = MyobEmployeeMatcher.MaskPhone(employee.Phones.FirstOrDefault()),
                    DateOfBirth = detail?.DateOfBirth?.ToString("yyyy-MM-dd"),
                    IsActive = employee.IsActive,
                    EmploymentStatus = detail?.EmploymentStatus,
                    HasAnnualEntitlement = hasAnnual,
                    HasSickEntitlement = hasSick,
                };
                return new MyobDirectoryCard(match, dto);
            })
            .ToList();

        return new MyobEmployeeDirectoryData(connection.ConnectionGuid, connection.BusinessName, fetchedAt, cards);
    }

    internal static string DisplayName(MyobEmployeeRecord employee)
    {
        var person = $"{employee.FirstName} {employee.LastName}".Trim();
        if (!employee.IsIndividual && !string.IsNullOrWhiteSpace(employee.CompanyName))
        {
            return employee.CompanyName!;
        }

        return string.IsNullOrWhiteSpace(person) ? (employee.CompanyName ?? employee.Uid) : person;
    }

    // 假期累计类别没有固定类型码，按 MYOB 默认名称判断：Holiday / Annual Leave Accrual = 年假，Sick / Personal Leave Accrual = 病假。
    // 公司改名后的类别会识别不到（页面上表现为「没挂累计」），属于已知限制，由财务按默认名称命名。
    internal static bool IsAnnualCategory(string? name) =>
        name is not null && (name.Contains("holiday", StringComparison.OrdinalIgnoreCase)
            || name.Contains("annual", StringComparison.OrdinalIgnoreCase));

    internal static bool IsSickCategory(string? name) =>
        name is not null && (name.Contains("sick", StringComparison.OrdinalIgnoreCase)
            || name.Contains("personal", StringComparison.OrdinalIgnoreCase));

    private static bool HasAssigned(MyobEmployeePayrollRecord detail, Func<string?, bool> classify) =>
        detail.Entitlements.Any(item => item.IsAssigned && classify(item.CategoryName));
}
