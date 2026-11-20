using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.Attendance;

/// <summary>
/// 打卡「共用设备」标记：防止有人登录同事账号替人打卡。
///
/// 判断思路：手机端登录时会把本机安装 ID（HardwareId）写进 UserLoginDeviceRecord，打卡时也会带同一个 ID。
/// 如果某台手机在打卡之前的 90 天里登录过 2 个及以上账号，就说明这台手机上出现过别人的账号，
/// 这类打卡打上标记交给店长复核。只标记、不拦截：换手机、家人共用手机、一个人两个账号都会命中，
/// 因此结果仅作为「需要看一眼」的线索，不能当作违规结论。
/// </summary>
public static class AttendancePunchDeviceFlagEvaluator
{
    /// <summary>回看窗口：员工登录一次后会话可长期保持，窗口太短会漏掉「A 很久前登录、B 昨天才登录」的情形。</summary>
    public static readonly TimeSpan LookBack = TimeSpan.FromDays(90);

    /// <summary>打卡请求与登录记录由不同时钟生成，留 5 分钟容差，避免临界登录被漏算。</summary>
    public static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMinutes(5);

    private const string AppLoginSource = "AppLogin";
    private const string DeviceLoginSource = "DeviceLogin";
    private const int QueryChunkSize = 500;

    /// <summary>规则只需要的登录证据字段，避免把整行实体带进纯规则里。</summary>
    public readonly record struct DeviceLoginEvidence(
        string HardwareId,
        string? UserGuid,
        string LoginSource,
        DateTime LoginAtUtc);

    /// <summary>
    /// 纯规则：返回该设备在打卡时刻之前出现过的不同账号数（含打卡人自己）；
    /// 不足 2 个、没有设备 ID、或这是门店登记设备（设备登录）时返回 null，表示不标记。
    /// </summary>
    public static int? CountSharedAccounts(
        string? punchUserGuid,
        string? deviceId,
        DateTime punchTimeUtc,
        IEnumerable<DeviceLoginEvidence> deviceLogins)
    {
        var hardwareId = deviceId?.Trim();
        if (string.IsNullOrWhiteSpace(hardwareId) || string.IsNullOrWhiteSpace(punchUserGuid))
        {
            return null;
        }

        var windowStart = punchTimeUtc - LookBack;
        var windowEnd = punchTimeUtc + ClockSkewTolerance;
        var accounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            punchUserGuid.Trim(),
        };

        foreach (var login in deviceLogins)
        {
            if (!string.Equals(login.HardwareId?.Trim(), hardwareId, StringComparison.OrdinalIgnoreCase)
                || login.LoginAtUtc > windowEnd)
            {
                continue;
            }

            // 门店登记设备（设备登录）本来就是多人共用，不是借账号，整台设备不标记。
            // 设备登录很少发生，所以不受 90 天窗口限制：只要登录过一次就算。
            if (string.Equals(login.LoginSource, DeviceLoginSource, StringComparison.Ordinal))
            {
                return null;
            }

            if (login.LoginAtUtc >= windowStart
                && string.Equals(login.LoginSource, AppLoginSource, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(login.UserGuid))
            {
                accounts.Add(login.UserGuid.Trim());
            }
        }

        return accounts.Count >= 2 ? accounts.Count : null;
    }

    /// <summary>
    /// 批量给打卡 DTO 填 <see cref="AttendancePunchDto.SharedDeviceAccountCount"/>。
    /// 只查一次登录记录（按设备 ID 分批 IN 查询，走 IX_UserLoginDeviceRecord_Hardware_LoginAt 索引）。
    /// </summary>
    public static async Task PopulateAsync(
        ISqlSugarClient db,
        IReadOnlyCollection<AttendancePunchDto> punches)
    {
        var candidates = punches
            .Where(item => !string.IsNullOrWhiteSpace(item.DeviceId))
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        var hardwareIds = candidates
            .Select(item => item.DeviceId!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var earliestStart = candidates.Min(item => item.PunchTimeUtc) - LookBack;
        var latestEnd = candidates.Max(item => item.PunchTimeUtc) + ClockSkewTolerance;

        var evidence = new List<DeviceLoginEvidence>();
        foreach (var chunk in hardwareIds.Chunk(QueryChunkSize))
        {
            var ids = chunk.ToList();
            // 设备登录不受回看窗口限制（见 CountSharedAccounts），所以窗口条件用 OR 放行它。
            var rows = await db.Queryable<UserLoginDeviceRecord>()
                .Where(item => !item.IsDeleted
                    && item.HardwareId != null
                    && ids.Contains(item.HardwareId)
                    && item.LoginAtUtc <= latestEnd
                    && (item.LoginAtUtc >= earliestStart || item.LoginSource == DeviceLoginSource))
                .Select(item => new { item.HardwareId, item.UserGuid, item.LoginSource, item.LoginAtUtc })
                .ToListAsync();
            evidence.AddRange(rows.Select(row => new DeviceLoginEvidence(
                row.HardwareId!,
                row.UserGuid,
                row.LoginSource,
                row.LoginAtUtc)));
        }

        var byDevice = evidence
            .GroupBy(item => item.HardwareId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var punch in candidates)
        {
            if (!byDevice.TryGetValue(punch.DeviceId!.Trim(), out var logins))
            {
                // 这台设备没有任何登录记录（例如旧版 App 登录没带设备 ID）：证据不足，不标记。
                continue;
            }

            punch.SharedDeviceAccountCount = CountSharedAccounts(
                punch.UserGuid,
                punch.DeviceId,
                punch.PunchTimeUtc,
                logins);
        }
    }
}
