using System.Globalization;

namespace Hbpos.Client.Wpf.Views.Controls;

/// <summary>
/// 触屏日期选择器的纯逻辑：月历格子、按界面语言格式化、未来日期限制。
/// 不依赖 WPF，也不读线程区域性，所有文字都按调用方传入的界面语言生成。
/// </summary>
public static class PosDatePickerCalendar
{
    /// <summary>月历固定 6 行 × 7 列，翻月时弹层高度不跳动。</summary>
    public const int GridDayCount = 42;

    /// <summary>门店在澳洲，一周从周一开始。</summary>
    public const DayOfWeek FirstDayOfWeek = DayOfWeek.Monday;

    /// <summary>返回包含该月的 42 个日期，首格是该月 1 日所在周的周一。</summary>
    public static IReadOnlyList<DateTime> BuildMonthGrid(DateTime month)
    {
        var firstOfMonth = new DateTime(month.Year, month.Month, 1);
        var leadingDays = ((int)firstOfMonth.DayOfWeek - (int)FirstDayOfWeek + 7) % 7;
        var start = firstOfMonth.AddDays(-leadingDays);
        var days = new DateTime[GridDayCount];
        for (var index = 0; index < GridDayCount; index++)
        {
            days[index] = start.AddDays(index);
        }

        return days;
    }

    /// <summary>生成弹层里 42 个日期格子的显示状态。</summary>
    public static IReadOnlyList<PosDatePickerDayCell> BuildDayCells(
        DateTime displayMonth,
        DateTime? selectedDate,
        DateTime today,
        bool allowFutureDates,
        CultureInfo culture)
    {
        var grid = BuildMonthGrid(displayMonth);
        var cells = new PosDatePickerDayCell[grid.Count];
        for (var index = 0; index < grid.Count; index++)
        {
            var date = grid[index];
            cells[index] = new PosDatePickerDayCell(
                date,
                date.Day.ToString(culture),
                date.Month == displayMonth.Month && date.Year == displayMonth.Year,
                selectedDate?.Date == date,
                date == today.Date,
                IsSelectable(date, today, allowFutureDates),
                FormatDate(date, culture, includeDayOfWeek: true));
        }

        return cells;
    }

    /// <summary>星期行，从周一开始，用界面语言的缩写（Mon / 周一）。</summary>
    public static IReadOnlyList<string> GetDayNames(CultureInfo culture)
    {
        var names = culture.DateTimeFormat.AbbreviatedDayNames;
        var result = new string[7];
        for (var index = 0; index < 7; index++)
        {
            result[index] = names[((int)FirstDayOfWeek + index) % 7];
        }

        return result;
    }

    /// <summary>月份标题：October 2026 / 2026年10月。</summary>
    public static string FormatMonthTitle(DateTime month, CultureInfo culture) =>
        month.ToString(culture.DateTimeFormat.YearMonthPattern, culture);

    /// <summary>
    /// 日期按钮上的文字。英文用「日 月份缩写 年」（Thu, 8 Oct 2026）：界面英文是 en-US，
    /// 其短日期 10/8/2026 在澳洲门店会被读成 8 月 10 日。中文用 2026年10月8日 周四。
    /// </summary>
    public static string FormatDate(DateTime date, CultureInfo culture, bool includeDayOfWeek)
    {
        var pattern = IsChinese(culture)
            ? includeDayOfWeek ? "yyyy年M月d日 ddd" : "yyyy年M月d日"
            : includeDayOfWeek ? "ddd, d MMM yyyy" : "d MMM yyyy";
        return date.ToString(pattern, culture);
    }

    /// <summary>不允许未来日期时，只能选到今天为止。</summary>
    public static bool IsSelectable(DateTime date, DateTime today, bool allowFutureDates) =>
        allowFutureDates || date.Date <= today.Date;

    /// <summary>不允许未来日期时，当月已是本月就不能再往后翻。</summary>
    public static bool CanShowNextMonth(DateTime displayMonth, DateTime today, bool allowFutureDates) =>
        allowFutureDates ||
        new DateTime(displayMonth.Year, displayMonth.Month, 1) < new DateTime(today.Year, today.Month, 1);

    private static bool IsChinese(CultureInfo culture) =>
        string.Equals(culture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase);
}

/// <summary>弹层月历里的一个日期格子；AccessibleName 供读屏与自动化测试识别完整日期。</summary>
public sealed record PosDatePickerDayCell(
    DateTime Date,
    string DayText,
    bool IsInDisplayMonth,
    bool IsSelected,
    bool IsToday,
    bool IsSelectable,
    string AccessibleName);
