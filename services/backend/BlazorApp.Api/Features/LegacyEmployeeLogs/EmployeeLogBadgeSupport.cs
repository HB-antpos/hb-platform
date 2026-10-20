using System.Security.Cryptography;
using System.Text;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.Extensions.Caching.Memory;

namespace BlazorApp.Api.Features.LegacyEmployeeLogs;

/// <summary>
/// 员工操作日志「待核查异常」角标（老收银与新收银共用）的口径与缓存辅助。
/// 角标由移动端工作台定时轮询，必须轻量：只做计数 SQL，并按「可见分店范围 + 天数」缓存一小段时间。
/// </summary>
public static class EmployeeLogBadgeSupport
{
    public const int DefaultDays = 7;
    public const int MaxDays = 31;

    /// <summary>角标允许最多滞后这么久；核查后短时间内角标可能还是旧值，下一次轮询就会更新。</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(45);

    /// <summary>天数缺省取 7，并限制在 1~31（越界值收敛而不是报错，角标失败对用户不可见）。</summary>
    public static int NormalizeDays(int? days) => Math.Clamp(days ?? DefaultDays, 1, MaxDays);

    /// <summary>
    /// 缓存键：结果只取决于可见分店范围与天数，与具体用户无关，所以同范围的账号共用一份缓存。
    /// 管理员用固定标记；分店编码排序后拼接，过长时取哈希，避免键过大。
    /// </summary>
    public static string BuildCacheKey(string prefix, bool isAdmin, IEnumerable<string> storeCodes, int days)
    {
        string scope;
        if (isAdmin)
        {
            scope = "*";
        }
        else
        {
            var joined = string.Join(",", storeCodes.OrderBy(code => code, StringComparer.OrdinalIgnoreCase));
            scope = joined.Length <= 120
                ? joined
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
        }
        return $"{prefix}:{days}:{scope}";
    }

    /// <summary>
    /// 老收银 OperationTime 是门店本地墙钟时间，没有统一时区；这里按默认时区（悉尼，比布里斯班只多至多 1 小时）
    /// 取「现在」的墙钟时间再回退 days 天，作为窗口起点。读不到时区时退回 UTC+10。
    /// </summary>
    public static DateTime WallClockWindowStart(DateTime utcNow, int days)
    {
        var utc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        DateTime wall;
        try
        {
            wall = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.FindSystemTimeZoneById(StoreTimeZonePolicy.Sydney));
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            wall = utc.AddHours(10);
        }
        return DateTime.SpecifyKind(wall.AddDays(-days), DateTimeKind.Unspecified);
    }

    /// <summary>
    /// 先查缓存，未命中才执行 loader；缓存为空（测试替身等）时直接执行。loader 抛异常时不写缓存，异常照常向上抛。
    /// </summary>
    public static async Task<EmployeeLogBadgeSummaryDto> GetOrLoadAsync(
        IMemoryCache? cache,
        string key,
        Func<Task<EmployeeLogBadgeSummaryDto>> loader
    )
    {
        if (cache != null && cache.TryGetValue(key, out EmployeeLogBadgeSummaryDto? cached) && cached != null)
        {
            return Copy(cached);
        }
        var loaded = await loader();
        cache?.Set(key, Copy(loaded), CacheDuration);
        return loaded;
    }

    /// <summary>防御性拷贝：缓存里的对象不会被调用方改动。</summary>
    private static EmployeeLogBadgeSummaryDto Copy(EmployeeLogBadgeSummaryDto source) => new()
    {
        AbnormalPending = source.AbnormalPending,
        DangerPending = source.DangerPending,
    };
}
