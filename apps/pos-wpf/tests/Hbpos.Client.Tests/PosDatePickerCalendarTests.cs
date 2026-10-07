using System.Globalization;
using Hbpos.Client.Wpf.Views.Controls;

namespace Hbpos.Client.Tests;

[Collection(CultureSensitiveTestCollection.Name)]
public sealed class PosDatePickerCalendarTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");

    [Fact]
    public void Month_grid_has_42_days_starting_on_monday_before_the_first()
    {
        // 2026-10-01 是周四，首格应是 9 月 28 日（周一）。
        var grid = PosDatePickerCalendar.BuildMonthGrid(new DateTime(2026, 10, 15));

        Assert.Equal(PosDatePickerCalendar.GridDayCount, grid.Count);
        Assert.Equal(new DateTime(2026, 9, 28), grid[0]);
        Assert.Equal(DayOfWeek.Monday, grid[0].DayOfWeek);
        Assert.Equal(new DateTime(2026, 10, 1), grid[3]);
        Assert.Equal(new DateTime(2026, 11, 8), grid[^1]);
    }

    [Fact]
    public void Month_starting_on_monday_has_no_leading_days()
    {
        // 2026-06-01 是周一。
        var grid = PosDatePickerCalendar.BuildMonthGrid(new DateTime(2026, 6, 20));

        Assert.Equal(new DateTime(2026, 6, 1), grid[0]);
    }

    [Fact]
    public void English_ui_formats_dates_in_english_regardless_of_thread_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // 复现现场：线程区域性卡在中文，界面语言是英文时，日期仍要按英文显示。
            CultureInfo.CurrentCulture = Chinese;
            var date = new DateTime(2026, 10, 8);

            Assert.Equal("Thu, 8 Oct 2026", PosDatePickerCalendar.FormatDate(date, English, includeDayOfWeek: true));
            Assert.Equal("8 Oct 2026", PosDatePickerCalendar.FormatDate(date, English, includeDayOfWeek: false));
            Assert.Equal("October 2026", PosDatePickerCalendar.FormatMonthTitle(date, English));
            Assert.Equal(
                ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"],
                PosDatePickerCalendar.GetDayNames(English));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Chinese_ui_formats_dates_in_chinese()
    {
        var date = new DateTime(2026, 10, 8);

        Assert.Equal("2026年10月8日 周四", PosDatePickerCalendar.FormatDate(date, Chinese, includeDayOfWeek: true));
        Assert.Equal("2026年10月8日", PosDatePickerCalendar.FormatDate(date, Chinese, includeDayOfWeek: false));
        Assert.Equal("2026年10月", PosDatePickerCalendar.FormatMonthTitle(date, Chinese));
        Assert.Equal("周一", PosDatePickerCalendar.GetDayNames(Chinese)[0]);
        Assert.Equal("周日", PosDatePickerCalendar.GetDayNames(Chinese)[6]);
    }

    [Fact]
    public void Future_dates_are_not_selectable_unless_allowed()
    {
        var today = new DateTime(2026, 10, 8);

        Assert.True(PosDatePickerCalendar.IsSelectable(today, today, allowFutureDates: false));
        Assert.True(PosDatePickerCalendar.IsSelectable(today.AddDays(-30), today, allowFutureDates: false));
        Assert.False(PosDatePickerCalendar.IsSelectable(today.AddDays(1), today, allowFutureDates: false));
        Assert.True(PosDatePickerCalendar.IsSelectable(today.AddDays(1), today, allowFutureDates: true));
    }

    [Fact]
    public void Next_month_is_blocked_once_the_current_month_is_shown()
    {
        var today = new DateTime(2026, 10, 8);

        Assert.True(PosDatePickerCalendar.CanShowNextMonth(new DateTime(2026, 9, 1), today, allowFutureDates: false));
        Assert.False(PosDatePickerCalendar.CanShowNextMonth(new DateTime(2026, 10, 1), today, allowFutureDates: false));
        Assert.True(PosDatePickerCalendar.CanShowNextMonth(new DateTime(2026, 10, 1), today, allowFutureDates: true));
    }

    [Fact]
    public void Day_cells_mark_selected_today_outside_month_and_future_days()
    {
        var today = new DateTime(2026, 10, 8);
        var cells = PosDatePickerCalendar.BuildDayCells(
            new DateTime(2026, 10, 1),
            selectedDate: new DateTime(2026, 10, 3),
            today,
            allowFutureDates: false,
            English);

        Assert.Equal(PosDatePickerCalendar.GridDayCount, cells.Count);
        var leading = cells[0];
        Assert.Equal(new DateTime(2026, 9, 28), leading.Date);
        Assert.False(leading.IsInDisplayMonth);
        Assert.True(leading.IsSelectable);

        var selected = Assert.Single(cells, cell => cell.IsSelected);
        Assert.Equal(new DateTime(2026, 10, 3), selected.Date);
        Assert.Equal("3", selected.DayText);
        Assert.Equal("Sat, 3 Oct 2026", selected.AccessibleName);

        var todayCell = Assert.Single(cells, cell => cell.IsToday);
        Assert.Equal(today, todayCell.Date);
        Assert.True(todayCell.IsSelectable);

        Assert.All(cells.Where(cell => cell.Date > today), cell => Assert.False(cell.IsSelectable));
        Assert.All(cells.Where(cell => cell.Date <= today), cell => Assert.True(cell.IsSelectable));
    }
}
