using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 节日贺卡的填报开放窗口：节日当天起 4 周内（当天 + 之后 28 天，共 29 天）开放对应节日，其余时间只读。
    /// 日期一律按门店本地日期（见 StoreCashClock），节日日期按澳洲习惯：
    /// 圣诞节 12/25、情人节 2/14、母亲节 5 月第二个周日、父亲节 9 月第一个周日、复活节为复活节周日。
    /// 圣诞节窗口会跨年（12/25–次年 1/22），此时填报归属的仍是上一年，所以开放窗口同时决定填报年份。
    /// </summary>
    internal static class SeasonalCardHolidayCalendar
    {
        /// <summary>节日当天之后还开放的天数（4 周）。</summary>
        public const int OpenDaysAfterHoliday = 28;

        public static DateOnly GetHolidayDate(SeasonalCardType cardType, int year) =>
            cardType switch
            {
                SeasonalCardType.Christmas => new DateOnly(year, 12, 25),
                SeasonalCardType.ValentinesDay => new DateOnly(year, 2, 14),
                SeasonalCardType.MothersDay => NthSunday(year, 5, 2),
                SeasonalCardType.FathersDay => NthSunday(year, 9, 1),
                SeasonalCardType.Easter => EasterSunday(year),
                _ => throw new ArgumentOutOfRangeException(nameof(cardType), cardType, null),
            };

        /// <summary>
        /// 今天（门店本地日期）该节日是否开放；开放时给出归属年份，未开放时给出下一次开放的节日日期。
        /// </summary>
        public static SeasonalCardHolidayWindow Resolve(SeasonalCardType cardType, DateOnly today)
        {
            // 只有圣诞节会跨年，所以只需看今年和去年两次节日。
            foreach (var year in new[] { today.Year, today.Year - 1 })
            {
                var holiday = GetHolidayDate(cardType, year);
                if (today >= holiday && today <= holiday.AddDays(OpenDaysAfterHoliday))
                {
                    return new SeasonalCardHolidayWindow(cardType, year, holiday, true);
                }
            }

            var next = GetHolidayDate(cardType, today.Year);
            if (next < today)
            {
                next = GetHolidayDate(cardType, today.Year + 1);
            }

            return new SeasonalCardHolidayWindow(cardType, next.Year, next, false);
        }

        private static DateOnly NthSunday(int year, int month, int nth)
        {
            var first = new DateOnly(year, month, 1);
            var offset = ((int)DayOfWeek.Sunday - (int)first.DayOfWeek + 7) % 7;
            return first.AddDays(offset + (nth - 1) * 7);
        }

        /// <summary>公历复活节周日（Anonymous Gregorian 算法）。</summary>
        private static DateOnly EasterSunday(int year)
        {
            var a = year % 19;
            var b = year / 100;
            var c = year % 100;
            var d = b / 4;
            var e = b % 4;
            var f = (b + 8) / 25;
            var g = (b - f + 1) / 3;
            var h = (19 * a + b - d - g + 15) % 30;
            var i = c / 4;
            var k = c % 4;
            var l = (32 + 2 * e + 2 * i - h - k) % 7;
            var m = (a + 11 * h + 22 * l) / 451;
            var month = (h + l - 7 * m + 114) / 31;
            var day = ((h + l - 7 * m + 114) % 31) + 1;
            return new DateOnly(year, month, day);
        }
    }

    /// <summary>某节日相对「今天」的开放情况；未开放时 SeasonYear / HolidayDate 指下一次节日。</summary>
    internal sealed record SeasonalCardHolidayWindow(
        SeasonalCardType CardType,
        int SeasonYear,
        DateOnly HolidayDate,
        bool IsOpen
    )
    {
        public DateOnly OpensOn => HolidayDate;
        public DateOnly ClosesOn => HolidayDate.AddDays(SeasonalCardHolidayCalendar.OpenDaysAfterHoliday);
    }
}
