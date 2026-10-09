namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// CloudBackendAsync 结算会话所属营业日的判断。营业日取会话创建时间在本机时区下的日期，
/// 与日结页 BusinessDate（DateTime.Today）同一口径；营业日切点与跨时区不在这里处理。
/// </summary>
internal static class LinklySettlementBusinessDay
{
    /// <summary>会话创建时间对应的本地日期；服务端尚未提供 CreatedAt（旧版本）时返回 null，表示无法确认营业日。</summary>
    public static DateTime? Of(DateTimeOffset? createdAt, TimeProvider timeProvider)
    {
        return createdAt is null
            ? null
            : TimeZoneInfo.ConvertTime(createdAt.Value, timeProvider.LocalTimeZone).Date;
    }

    /// <summary>确认属于今天之前的营业日。无法确认（无 CreatedAt）时返回 false，沿用旧版本行为。</summary>
    public static bool IsEarlierThanToday(DateTimeOffset? createdAt, TimeProvider timeProvider)
    {
        return Of(createdAt, timeProvider) is { } day && day < timeProvider.GetLocalNow().Date;
    }
}
