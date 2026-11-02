using System.Globalization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Cashiers;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

/// <summary>
/// 日结后的提前维护窗口：当天日结保存后打开到次日 08:00，收银员登录时暂停；
/// 补做往日日结、夜间安装被关闭时都不打开。
/// </summary>
public sealed class PostDailyCloseMaintenanceWindowTests
{
    // 2026-09-28 是周一；FakeTimeProvider 的本地时区固定为 UTC。
    private static readonly DateTime Monday = new(2026, 9, 28);

    [Fact]
    public void Same_day_close_opens_until_8am_next_morning()
    {
        var harness = new Harness(new TimeOnly(16, 30));
        Assert.False(harness.Window.IsOpen);

        harness.Window.Open(Monday);
        Assert.True(harness.Window.IsOpen);

        harness.Time.SetUtcNow(new DateTimeOffset(2026, 9, 29, 7, 59, 0, TimeSpan.Zero));
        Assert.True(harness.Window.IsOpen);

        harness.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(harness.Window.IsOpen);
    }

    [Fact]
    public void Backfilled_close_for_an_earlier_business_date_does_not_open()
    {
        var harness = new Harness(new TimeOnly(10, 0));

        harness.Window.Open(Monday.AddDays(-1));

        Assert.False(harness.Window.IsOpen);
    }

    [Fact]
    public void Signed_in_cashier_pauses_the_window_until_signed_out()
    {
        var harness = new Harness(new TimeOnly(16, 30));
        harness.Cashier.SetCurrent(new CashierSessionDto(
            "C001", "U-1", "Alice", "S001", "POS-01", [], [], ["S001"], false, false, false));

        harness.Window.Open(Monday);
        Assert.False(harness.Window.IsOpen);

        harness.Cashier.Clear();
        Assert.True(harness.Window.IsOpen);
    }

    [Fact]
    public void Disabled_unattended_install_never_opens()
    {
        var harness = new Harness(new TimeOnly(16, 30), AppUpdateUnattendedInstallOptions.Disabled);

        harness.Window.Open(Monday);

        Assert.False(harness.Window.IsOpen);
    }

    [Theory]
    [InlineData("2026-09-28 06:30", "2026-09-28 08:00")]
    [InlineData("2026-09-28 08:00", "2026-09-29 08:00")]
    [InlineData("2026-09-28 17:00", "2026-09-29 08:00")]
    [InlineData("2026-09-28 23:30", "2026-09-29 08:00")]
    public void Window_closes_at_the_next_8am(string localNow, string expectedLocalEnd)
    {
        var now = new DateTimeOffset(ParseLocal(localNow), TimeSpan.Zero);

        var end = PostDailyCloseMaintenanceWindow.NextWindowEnd(now, TimeZoneInfo.Utc, new TimeOnly(8, 0));

        Assert.Equal(ParseLocal(expectedLocalEnd), end.DateTime);
    }

    [Fact]
    public void Window_end_uses_the_offset_after_a_daylight_saving_change()
    {
        // 悉尼 2026-10-04 凌晨 2 点进入夏令时：周六 17:00（+10:00）日结，关闭时刻是周日 08:00（+11:00）。
        var sydney = GetSydneyTimeZone();
        var closedAt = new DateTimeOffset(2026, 10, 3, 17, 0, 0, TimeSpan.FromHours(10));

        var end = PostDailyCloseMaintenanceWindow.NextWindowEnd(closedAt, sydney, new TimeOnly(8, 0));

        Assert.Equal(new DateTimeOffset(2026, 10, 4, 8, 0, 0, TimeSpan.FromHours(11)), end);
        Assert.Equal(TimeSpan.FromHours(11), end.Offset);
    }

    private static DateTime ParseLocal(string value) =>
        DateTime.ParseExact(value, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static TimeZoneInfo GetSydneyTimeZone()
    {
        foreach (var timeZoneId in new[] { "AUS Eastern Standard Time", "Australia/Sydney" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        throw new InvalidOperationException("找不到用于夏令时边界测试的悉尼时区。");
    }

    private sealed class Harness
    {
        public Harness(TimeOnly localTime, AppUpdateUnattendedInstallOptions? options = null)
        {
            Time = new FakeTimeProvider(new DateTimeOffset(Monday + localTime.ToTimeSpan(), TimeSpan.Zero));
            Time.SetLocalTimeZone(TimeZoneInfo.Utc);
            Window = new PostDailyCloseMaintenanceWindow(
                options ?? AppUpdateUnattendedInstallOptions.Default,
                Cashier,
                Time);
        }

        public FakeTimeProvider Time { get; }

        public CashierSessionContext Cashier { get; } = new();

        public PostDailyCloseMaintenanceWindow Window { get; }
    }
}
