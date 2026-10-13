using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.Attendance;

/// <summary>
/// 班中定位样本的「班次有效性」判定（纯函数，不碰数据库）。
/// 单条上传（HasActiveLocationSampleShiftAsync）与批量上传（CreateLocationSamplesBatchAsync）共用同一份判定，
/// 保证两条路径对「这个采集时刻有没有有效班次」的结论完全一致。
/// 判定始终是「相对采集时刻」：客户端在下班打卡成功之后才上传尾批点，所以晚于采集时刻的下班卡不能影响结论。
/// </summary>
public static class AttendanceLocationSampleShiftRules
{
    /// <summary>一个班次最长有效窗口：采集时刻前 10 小时内的上班卡才算。</summary>
    public static readonly TimeSpan MaxShiftWindow = TimeSpan.FromHours(10);

    /// <summary>
    /// 有效窗口下限（不含）：门店本地当日 0 点与「采集时刻前 10 小时」取较晚者。
    /// 跨过门店本地午夜的上班卡不能给次日的定位点背书。
    /// </summary>
    public static DateTime ResolveWindowStartUtc(DateTime capturedAtUtc, DateTime storeLocalDayStartUtc)
    {
        var maximumShiftStartUtc = capturedAtUtc.Subtract(MaxShiftWindow);
        return storeLocalDayStartUtc > maximumShiftStartUtc ? storeLocalDayStartUtc : maximumShiftStartUtc;
    }

    /// <summary>
    /// 采集时刻 <paramref name="capturedAtUtc"/> 是否处在有效班次内。
    /// <paramref name="loadedPunches"/> 是调用方已按「用户 + 门店 + 未删除」加载的候选打卡（可以比窗口宽，这里再按窗口筛）；
    /// <paramref name="supersededPunchGuids"/> 是其中被其他有效打卡顶替（SupersedesPunchGuid）的打卡 GUID，
    /// 顶替关系与顶替卡自身的时间无关（与单条判定的历史口径一致）。
    /// 规则：窗口 (windowStartUtc, capturedAtUtc] 内、排除被顶替的打卡中，PunchTimeUtc 最晚（并列取 Id 大者）的一条必须是 ClockIn。
    /// </summary>
    public static bool IsShiftActiveAt(
        IEnumerable<AttendancePunch> loadedPunches,
        IReadOnlySet<string> supersededPunchGuids,
        DateTime capturedAtUtc,
        DateTime windowStartUtc
    )
    {
        var latestEffectivePunch = loadedPunches
            .Where(item => item.PunchTimeUtc > windowStartUtc && item.PunchTimeUtc <= capturedAtUtc)
            .OrderByDescending(item => item.PunchTimeUtc)
            .ThenByDescending(item => item.Id)
            .FirstOrDefault(item => !supersededPunchGuids.Contains(item.PunchGuid));

        // 采集时刻之前最后一条有效打卡必须是上班卡，且该卡不晚于采集时刻（窗口已保证，这里保留与原判定一致的显式比较）。
        return string.Equals(latestEffectivePunch?.PunchType, "ClockIn", StringComparison.OrdinalIgnoreCase)
            && latestEffectivePunch!.PunchTimeUtc <= capturedAtUtc;
    }
}
