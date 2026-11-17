using System.Collections.Concurrent;
using BlazorApp.Api.Data;
using BlazorApp.Api.Models.Myob;
using Microsoft.Extensions.Caching.Memory;
using SqlSugar;

namespace BlazorApp.Api.Services.Myob;

/// <summary>
/// 某公司文件里每位员工的时薪相关值（Contact/EmployeePayrollDetails 的 Wage 节 + 离职日期），员工 UID → 记录。
/// 只给「从 MYOB 同步时薪」用，所以和员工名册分开拉取、分开缓存：时薪属于敏感数据，不放进会发给前端的名册 DTO。
/// 年薪（AnnualSalary）在读取层就不反序列化，这里拿不到。
/// </summary>
public interface IMyobEmployeeWageSource
{
    /// <exception cref="MyobApiException">连接不存在 / 需要重新授权 / MYOB 暂时不可用。</exception>
    Task<IReadOnlyDictionary<string, MyobEmployeePayrollRecord>> GetAsync(
        string connectionGuid, bool refresh, CancellationToken cancellationToken);
}

internal sealed class MyobEmployeeWageSource : IMyobEmployeeWageSource
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    private readonly ISqlSugarClient _db;
    private readonly IMyobApiClient _client;
    private readonly IMyobTokenProvider _tokens;
    private readonly IMemoryCache _cache;

    public MyobEmployeeWageSource(
        SqlSugarContext context,
        IMyobApiClient client,
        IMyobTokenProvider tokens,
        IMemoryCache cache)
        : this(context.Db, client, tokens, cache)
    {
    }

    internal MyobEmployeeWageSource(
        ISqlSugarClient db,
        IMyobApiClient client,
        IMyobTokenProvider tokens,
        IMemoryCache cache)
    {
        _db = db;
        _client = client;
        _tokens = tokens;
        _cache = cache;
    }

    internal static string CacheKey(string connectionGuid) => $"myob:wage:{connectionGuid}";

    public async Task<IReadOnlyDictionary<string, MyobEmployeePayrollRecord>> GetAsync(
        string connectionGuid, bool refresh, CancellationToken cancellationToken)
    {
        var key = CacheKey(connectionGuid);
        IReadOnlyDictionary<string, MyobEmployeePayrollRecord>? cached;
        if (!refresh && _cache.TryGetValue(key, out cached) && cached is not null)
        {
            return cached;
        }

        var gate = Locks.GetOrAdd(connectionGuid, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && _cache.TryGetValue(key, out cached) && cached is not null)
            {
                return cached;
            }

            var connection = await _db.Queryable<MyobConnection>().FirstAsync(item => item.ConnectionGuid == connectionGuid)
                ?? throw new MyobApiException(MyobErrorCodes.ConnectionNotFound, "MYOB 连接不存在");
            var accessToken = await _tokens.GetAccessTokenAsync(connectionGuid, cancellationToken);
            var records = await _client.GetEmployeePayrollDetailsAsync(connection.BusinessId, accessToken, cancellationToken);

            var data = (IReadOnlyDictionary<string, MyobEmployeePayrollRecord>)records
                .GroupBy(record => record.EmployeeUid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            // 与员工名册同样缓存 10 分钟；含时薪，只放进程内存，不落盘。
            _cache.Set(key, data, MyobEmployeeDirectory.CacheLifetime);
            return data;
        }
        finally
        {
            gate.Release();
        }
    }
}
