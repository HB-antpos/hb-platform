using System;
using System.Collections.Generic;
using System.Linq;
using BlazorApp.Api.Services.Attendance;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Models;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 工时统计与工资纯计算（规格 docs/plans/2026-10-21-attendance-payroll-leave.md 第 3 节）。
/// 验收样例取自设计图两周数据 docs/design/attendance-payroll-20261021/src/period-data.js：
/// 2026-10-05（周一，QLD King's Birthday 公共假期）~ 10-18。每个人从逐段打卡算起，经当天计薪、档位、转账装填到应发。
/// </summary>
public sealed class PayrollCalculatorTests
{
    private static readonly DateTime PeriodStart = new(2026, 10, 5);
    private static readonly HashSet<DateTime> PublicHolidays = new() { PeriodStart };

    // ---------------- 验收样例 ----------------

    [Fact]
    public void 验收样例_JackNguyen_转账加现金_日六假平装满60h_现金588h_补贴扣回20()
    {
        var result = CalculateSample(
            Mixed(30, 28.5m, 35.63m, 42.75m, 64.13m, cash: 28.5m, allowance: -20m),
            new[]
            {
                Day(2, "09:00-17:30", "08:56-13:00", "13:31-17:30"),
                Day(4, "13:00-21:00", "12:59-17:00", "17:30-21:01"),
                Day(5, "09:00-17:30", "09:00-13:00", "13:30-17:32"),
                Day(8, "09:00-17:30", "08:55-13:00", "13:30-17:31"),
                Day(10, "09:00-17:30", "09:12-13:00", "13:30-17:30"),
                Day(11, "13:00-21:00", "12:58-21:02"),
                // 10-18：13:02 上班后漏打下班，系统午夜自动下班，该段不计薪。
                Day(13, "09:00-17:30", new[] { "08:57-12:30" }, autoSegment: "13:02"),
            },
            // 10-06 带薪病假 8 h；10-05 带薪公假（店长确认 7.6 h）。
            leave: new[] { Leave(1, 8m), Leave(0, 7.6m) });

        Assert.Equal(65.88m, result.Hours.Total);
        Assert.Equal(38.78m, result.Hours.Weekday);
        Assert.Equal(8m, result.Hours.Saturday);
        Assert.Equal(3.5m, result.Hours.Sunday);
        Assert.Equal(15.6m, result.Hours.Leave);
        // 转账 60 h = 日 3.5 + 六 8 + 假 15.6 + 平 32.9
        Assert.Equal(3.5m, result.TransferHours.Sunday);
        Assert.Equal(8m, result.TransferHours.Saturday);
        Assert.Equal(15.6m, result.TransferHours.Leave);
        Assert.Equal(32.9m, result.TransferHours.Weekday);
        Assert.Equal(60m, result.TransferHours.Total);
        Assert.Equal(5.88m, result.CashHours.Total);
        Assert.Equal(1816.92m, result.TransferAmount);
        Assert.Equal(167.58m, result.CashAmount);
        Assert.Equal(-20m, result.Allowance);
        Assert.Equal(1964.50m, result.GrossPay);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void 验收样例_EmmaWalsh_年假申请24h_财务核定20h_全转账()
    {
        // 年假休息 10-06 ~ 10-08：3 个工作日 × 8 h = 24 h 申请；工资按财务核定的 20 h。
        Assert.Equal(24m, PayrollCalculator.AnnualLeaveRequestedHours(
            new DateTime(2026, 10, 6), new DateTime(2026, 10, 8), PublicHolidays));

        var result = CalculateSample(
            Transfer(29.5m, 36.88m, 44.25m, 66.38m),
            new[]
            {
                Day(4, "09:00-13:00", "08:58-13:00"),
                Day(5, "09:00-17:00", "09:00-12:30", "13:00-17:01"),
                Day(7, "09:00-15:00", "09:00-15:02"),
                Day(9, "12:00-18:00", "11:58-15:00", "15:30-18:03"),
                Day(11, "09:00-13:00", "08:59-13:00"),
                // 10-17 加班待审：不计。
                Day(12, "09:00-17:00", "08:58-13:02", "13:33-17:47"),
            },
            leave: new[] { Leave(1, 20m) });

        Assert.Equal(53.98m, result.Hours.Total);
        Assert.Equal(20m, result.Hours.Leave);
        Assert.Equal(0m, result.CashHours.Total);
        Assert.Equal(1702.96m, result.GrossPay);
    }

    [Fact]
    public void 验收样例_LiamPatel_每周固定10h转账_其余现金3198h乘27()
    {
        var result = CalculateSample(
            Mixed(10, 27m, 33.75m, 40.5m, 60.75m, cash: 27m),
            new[]
            {
                Day(0, "12:00-18:00", "12:00-18:01"),
                Day(3, "16:00-21:30", "16:00-21:30"),
                Day(5, "12:00-20:00", "11:59-16:00", "16:30-20:00"),
                Day(6, "12:00-18:00", "12:00-18:00"),
                Day(7, "16:00-21:30", "16:00-21:30"),
                // 10-14 加班 30 分钟已批准。
                Day(9, "16:00-21:30", new[] { "15:58-22:02" }, overtime: 30),
                Day(10, "16:00-21:30", "16:01-21:30"),
                Day(12, "12:00-20:00", "11:58-16:00", "16:30-20:00"),
                Day(13, "12:00-18:00", "12:00-18:00"),
            });

        Assert.Equal(51.98m, result.Hours.Total);
        Assert.Equal(5.5m, result.Hours.PublicHoliday);
        // 转账容量 10 × 2 = 20 h：周日 11 h 全装、周六装 9 h。
        Assert.Equal(20m, result.TransferHours.Total);
        Assert.Equal(11m, result.TransferHours.Sunday);
        Assert.Equal(9m, result.TransferHours.Saturday);
        Assert.Equal(749.25m, result.TransferAmount);
        Assert.Equal(31.98m, result.CashHours.Total);
        Assert.Equal(863.46m, result.CashAmount);
        Assert.Equal(1612.71m, result.GrossPay);
    }

    [Fact]
    public void 验收样例_MiaRobinson_全现金_所有工时乘单一现金时薪()
    {
        var result = CalculateSample(
            Cash(23.1m),
            new[]
            {
                Day(1, "09:00-15:00", "09:00-15:01"),
                Day(3, "15:30-18:00", "15:30-18:00"),
                Day(5, "09:00-17:00", "08:59-12:30", "13:00-17:00"),
                // 10-13 没休息，加回 30 分钟已批准。
                Day(8, "09:00-18:00", new[] { "09:00-18:02" }, addBack: 30),
                Day(9, "09:00-15:00", "09:01-15:00"),
                Day(11, "12:00-18:00", "11:59-18:00"),
                Day(12, "09:00-17:00", "08:59-12:30", "13:00-17:02"),
                Day(13, "10:00-16:00", "10:00-16:00"),
            });

        Assert.Equal(48.48m, result.Hours.Total);
        Assert.Equal(48.48m, result.CashHours.Total);
        Assert.Equal(0m, result.TransferAmount);
        Assert.Equal(1119.89m, result.CashAmount);
        Assert.Equal(1119.89m, result.GrossPay);
    }

    [Fact]
    public void 验收样例_全店六人合计与设计稿一致()
    {
        var sarah = CalculateSample(
            Mixed(38, 32m, 40m, 48m, 72m, cash: 32m, allowance: 50m),
            new[]
            {
                Day(0, "08:30-17:00", "08:29-12:30", "13:00-17:01"),
                Day(1, "08:30-17:00", "08:30-12:30", "13:00-17:00"),
                Day(3, "08:30-12:00", "08:28-12:00"),
                Day(4, "08:30-17:00", "08:31-12:30", "13:02-17:00"),
                Day(6, "08:30-17:00", "08:30-12:30", "13:00-17:03"),
                Day(7, "08:30-17:00", "08:28-12:30", "13:01-17:05"),
                Day(8, "08:30-17:00", "08:31-12:00", "12:30-14:40", "14:55-17:02"),
                Day(10, "08:30-17:00", "08:25-13:00", "13:30-17:00"),
                Day(11, "08:30-17:00", "08:30-17:01"),
                Day(13, "08:30-17:00", "09:07-12:30", "13:00-17:00"),
            });
        var olivia = CalculateSample(
            Transfer(29m, 36.25m, 43.5m, 65.25m),
            new[]
            {
                Day(1, "10:00-16:00", "10:00-16:00"),
                Day(3, "09:00-15:00", "08:59-15:00"),
                Day(5, "10:00-16:00", "10:00-16:02"),
                Day(7, "10:00-16:00", "09:58-16:00"),
                Day(11, "09:00-18:30", "08:59-13:30", "14:00-18:31"),
                Day(12, "10:00-16:00", "10:00-16:01"),
            },
            // 10-14 病假 6 h；年假发放（照常上班）18 h。
            leave: new[] { Leave(9, 6m), Leave(7, 18m) });

        Assert.Equal(3001.68m, sarah.GrossPay);
        Assert.Equal(1819.75m, olivia.GrossPay);
        Assert.Equal(11221.49m, sarah.GrossPay + olivia.GrossPay + 1964.50m + 1702.96m + 1612.71m + 1119.89m);
    }

    // ---------------- 当天计薪口径 ----------------

    [Fact]
    public void 当天计薪_只算排班窗口内的重叠_早到迟走不算_已批准加班另加()
    {
        var day = PayrollCalculator.CalculateDay(new PayrollDayInput(
            At(0, "16:00"), At(0, "21:30"),
            new[] { Segment(0, "15:58", "22:02") },
            EffectiveMealBreakCount: 1,
            ApprovedOvertimeMinutes: 30));

        Assert.Equal(364, day.WorkedMinutes);
        Assert.Equal(330, day.InScheduleMinutes);
        Assert.Equal(30, day.MealDeductionMinutes);
        Assert.Equal(330, day.PaidMinutes); // 330 + 30 − 30
    }

    [Fact]
    public void 当天计薪_段间满30分钟空档抵掉一次扣餐()
    {
        var withGap = PayrollCalculator.CalculateDay(new PayrollDayInput(
            At(0, "08:30"), At(0, "17:00"),
            new[] { Segment(0, "08:30", "12:30"), Segment(0, "13:00", "17:00") },
            EffectiveMealBreakCount: 1));
        var shortGap = PayrollCalculator.CalculateDay(new PayrollDayInput(
            At(0, "08:30"), At(0, "17:00"),
            new[] { Segment(0, "08:30", "12:30"), Segment(0, "12:50", "17:00") },
            EffectiveMealBreakCount: 1));

        Assert.Equal(0, withGap.MealDeductionMinutes);
        Assert.Equal(480, withGap.PaidMinutes);
        Assert.Equal(30, shortGap.MealDeductionMinutes);
        Assert.Equal(460, shortGap.PaidMinutes);
    }

    [Fact]
    public void 当天计薪_系统午夜自动下班段不计_不足4小时不扣餐()
    {
        var day = PayrollCalculator.CalculateDay(new PayrollDayInput(
            At(0, "09:00"), At(0, "17:30"),
            new[] { Segment(0, "08:57", "12:30"), new PayrollSegment(At(0, "13:02"), At(1, "00:00"), true) },
            EffectiveMealBreakCount: 1));

        Assert.Equal(213, day.WorkedMinutes);
        Assert.Equal(0, day.MealDeductionMinutes);
        Assert.Equal(210, day.PaidMinutes);
    }

    [Fact]
    public void 当天计薪_加回不超过扣除()
    {
        var day = PayrollCalculator.CalculateDay(new PayrollDayInput(
            At(0, "09:00"), At(0, "18:00"),
            new[] { Segment(0, "09:00", "18:02") },
            EffectiveMealBreakCount: 1,
            ApprovedMealAddBackMinutes: 90));

        Assert.Equal(30, day.MealAddBackMinutes);
        Assert.Equal(540, day.PaidMinutes);
    }

    [Fact]
    public void 当天计薪_跨午夜排班窗口()
    {
        var day = PayrollCalculator.CalculateDay(new PayrollDayInput(
            At(0, "22:00"), At(1, "02:00"),
            new[] { Segment(0, "21:55", "23:59"), new PayrollSegment(At(1, "00:00"), At(1, "02:05")) },
            EffectiveMealBreakCount: 0));

        Assert.Equal(239, day.PaidMinutes);
    }

    // ---------------- 档位与舍入 ----------------

    [Theory]
    [InlineData(479, 7.98)]  // 7:59 → 7.98
    [InlineData(450, 7.5)]
    [InlineData(1, 0.02)]     // 0.01666… → 0.02
    [InlineData(329, 5.48)]
    public void 小时_分钟除以60舍到2位小数(int minutes, double expected)
    {
        Assert.Equal((decimal)expected, PayrollCalculator.RoundHours(minutes));
    }

    [Fact]
    public void 档位_公假优先_其次周六周日平日()
    {
        Assert.Equal(PayrollBucket.PublicHoliday, PayrollCalculator.ResolveTier(new DateTime(2026, 10, 10), true));
        Assert.Equal(PayrollBucket.Saturday, PayrollCalculator.ResolveTier(new DateTime(2026, 10, 10), false));
        Assert.Equal(PayrollBucket.Sunday, PayrollCalculator.ResolveTier(new DateTime(2026, 10, 11), false));
        Assert.Equal(PayrollBucket.Weekday, PayrollCalculator.ResolveTier(new DateTime(2026, 10, 12), false));
    }

    [Fact]
    public void 金额_四舍五入到分_不用银行家舍入()
    {
        // 3.5 h × $42.75 = 149.625 → 149.63（默认银行家舍入会得到 149.62）。
        var result = PayrollCalculator.CalculatePay(new PayrollPayInput
        {
            PeriodStart = PeriodStart,
            Weeks = 1,
            Rates = new[] { Transfer(28.5m, 35.63m, 42.75m, 64.13m) },
            WorkItems = new[] { new PayrollWorkItem(PeriodStart.AddDays(6), PayrollBucket.Sunday, 210) },
        });

        Assert.Equal(149.63m, result.TransferAmount);
    }

    [Fact]
    public void 工时先按档合计分钟再舍2位_再乘时薪()
    {
        // 两天平日各 479 分钟：合计 958 → 15.97 h（逐天舍再相加会是 15.96）。
        var result = PayrollCalculator.CalculatePay(new PayrollPayInput
        {
            PeriodStart = PeriodStart,
            Weeks = 1,
            Rates = new[] { Transfer(30m, 30m, 30m, 30m) },
            WorkItems = new[]
            {
                new PayrollWorkItem(PeriodStart.AddDays(1), PayrollBucket.Weekday, 479),
                new PayrollWorkItem(PeriodStart.AddDays(2), PayrollBucket.Weekday, 479),
            },
        });

        Assert.Equal(15.97m, result.Hours.Weekday);
        Assert.Equal(479.10m, result.TransferAmount);
    }

    // ---------------- 发放方式与装填顺序 ----------------

    [Fact]
    public void 转账加现金_按周日周六带薪假公假平日顺序装入转账()
    {
        var result = PayrollCalculator.CalculatePay(new PayrollPayInput
        {
            PeriodStart = PeriodStart,
            Weeks = 1,
            Rates = new[] { Mixed(14, 20m, 30m, 40m, 50m, cash: 18m) },
            WorkItems = new[]
            {
                new PayrollWorkItem(PeriodStart, PayrollBucket.PublicHoliday, 300),          // 5 h
                new PayrollWorkItem(PeriodStart.AddDays(1), PayrollBucket.Weekday, 480),     // 8 h
                new PayrollWorkItem(PeriodStart.AddDays(5), PayrollBucket.Saturday, 240),    // 4 h
                new PayrollWorkItem(PeriodStart.AddDays(6), PayrollBucket.Sunday, 180),      // 3 h
            },
            LeaveItems = new[] { new PayrollLeaveItem(PeriodStart.AddDays(2), 6m) },
        });

        // 容量 14 h：日 3 → 六 4 → 假 6 → 公 1；公假剩 4 h、平日 8 h 走现金。
        Assert.Equal(3m, result.TransferHours.Sunday);
        Assert.Equal(4m, result.TransferHours.Saturday);
        Assert.Equal(6m, result.TransferHours.Leave);
        Assert.Equal(1m, result.TransferHours.PublicHoliday);
        Assert.Equal(0m, result.TransferHours.Weekday);
        Assert.Equal(4m, result.CashHours.PublicHoliday);
        Assert.Equal(8m, result.CashHours.Weekday);
        // 转账：3×40 + 4×30 + 6×20（带薪假用平日转账时薪）+ 1×50 = 410；现金：12 h × 18（单一价，不分公假）= 216。
        Assert.Equal(410m, result.TransferAmount);
        Assert.Equal(216m, result.CashAmount);
        Assert.Equal(626m, result.GrossPay);
    }

    [Fact]
    public void 全转账_带薪假按平日转账时薪_周末公假按各档时薪()
    {
        var result = PayrollCalculator.CalculatePay(new PayrollPayInput
        {
            PeriodStart = PeriodStart,
            Weeks = 1,
            Rates = new[] { Transfer(20m, 30m, 40m, 50m) },
            WorkItems = new[]
            {
                new PayrollWorkItem(PeriodStart, PayrollBucket.PublicHoliday, 60),
                new PayrollWorkItem(PeriodStart.AddDays(5), PayrollBucket.Saturday, 60),
                new PayrollWorkItem(PeriodStart.AddDays(6), PayrollBucket.Sunday, 60),
            },
            LeaveItems = new[] { new PayrollLeaveItem(PeriodStart.AddDays(1), 2m) },
        });

        Assert.Equal(0m, result.CashHours.Total);
        Assert.Equal(50m + 30m + 40m + 2 * 20m, result.TransferAmount);
    }

    [Fact]
    public void 全现金_所有工时含带薪假都按现金时薪_加补贴()
    {
        var result = PayrollCalculator.CalculatePay(new PayrollPayInput
        {
            PeriodStart = PeriodStart,
            Weeks = 1,
            Rates = new[] { Cash(25m, allowance: 15m) },
            WorkItems = new[]
            {
                new PayrollWorkItem(PeriodStart, PayrollBucket.PublicHoliday, 60),
                new PayrollWorkItem(PeriodStart.AddDays(6), PayrollBucket.Sunday, 60),
            },
            LeaveItems = new[] { new PayrollLeaveItem(PeriodStart.AddDays(1), 1m) },
        });

        Assert.Equal(0m, result.TransferAmount);
        Assert.Equal(75m, result.CashAmount);
        Assert.Equal(90m, result.GrossPay);
    }

    [Fact]
    public void 时薪按工作日当天有效的记录_期中调薪分段定价()
    {
        var oldRate = Transfer(20m, 20m, 20m, 20m, key: "old", effectiveFrom: new DateTime(2026, 7, 1));
        var newRate = Transfer(30m, 30m, 30m, 30m, key: "new", effectiveFrom: new DateTime(2026, 10, 8));
        var result = PayrollCalculator.CalculatePay(new PayrollPayInput
        {
            PeriodStart = PeriodStart,
            Weeks = 1,
            Rates = new[] { oldRate, newRate },
            WorkItems = new[]
            {
                new PayrollWorkItem(new DateTime(2026, 10, 6), PayrollBucket.Weekday, 60),
                new PayrollWorkItem(new DateTime(2026, 10, 9), PayrollBucket.Weekday, 60),
            },
        });

        Assert.Equal(2m, result.Hours.Weekday);
        Assert.Equal(50m, result.TransferAmount);
    }

    [Fact]
    public void 没有时薪记录_工时照算_金额为0并提示()
    {
        var result = PayrollCalculator.CalculatePay(new PayrollPayInput
        {
            PeriodStart = PeriodStart,
            Weeks = 1,
            WorkItems = new[] { new PayrollWorkItem(PeriodStart.AddDays(1), PayrollBucket.Weekday, 120) },
        });

        Assert.Null(result.PayMode);
        Assert.Equal(2m, result.Hours.Total);
        Assert.Equal(0m, result.GrossPay);
        Assert.Contains(PayrollCalculator.WarningNoRate, result.Warnings);
    }

    [Fact]
    public void 缺档位时薪或低于最低时薪_给出提示()
    {
        var rate = new PayrollRate("r", PeriodStart, PayrollCalculator.PayModeTransfer, 0, 30m, null, 40m, 50m, null, 0m, 31m);
        var result = PayrollCalculator.CalculatePay(new PayrollPayInput
        {
            PeriodStart = PeriodStart,
            Weeks = 1,
            Rates = new[] { rate },
            WorkItems = new[] { new PayrollWorkItem(PeriodStart.AddDays(5), PayrollBucket.Saturday, 60) },
        });

        Assert.Contains(PayrollCalculator.WarningMissingTransferRate, result.Warnings);
        Assert.Contains(PayrollCalculator.WarningBelowMinimumRate, result.Warnings);
    }

    // ---------------- 年假、病假、周期 ----------------

    [Fact]
    public void 年假申请工时_区间内工作日排除公假乘8()
    {
        // 设计稿：11-17 周二 ~ 11-28 周六，工作日 9 天 × 8 h = 72 h。
        Assert.Equal(72m, PayrollCalculator.AnnualLeaveRequestedHours(
            new DateTime(2026, 11, 17), new DateTime(2026, 11, 28), new HashSet<DateTime>()));
        // 区间含一个公假的工作日：少 8 h。
        Assert.Equal(16m, PayrollCalculator.AnnualLeaveRequestedHours(
            new DateTime(2026, 10, 5), new DateTime(2026, 10, 7), PublicHolidays));
        Assert.Equal(0m, PayrollCalculator.AnnualLeaveRequestedHours(
            new DateTime(2026, 10, 10), new DateTime(2026, 10, 11), PublicHolidays));
    }

    [Fact]
    public void 病假工时_排班工时扣餐()
    {
        // 设计稿：10-19 09:00–15:00 → 5.5 h。
        var schedule = new AttendanceSchedule
        {
            WorkDate = new DateTime(2026, 10, 19),
            StartTime = new TimeSpan(9, 0, 0),
            EndTime = new TimeSpan(15, 0, 0),
        };
        Assert.Equal(5.5m, AttendanceReactService.SickLeaveHoursForSchedule(schedule));
        schedule.EndTime = new TimeSpan(13, 0, 0);
        Assert.Equal(4m, AttendanceReactService.SickLeaveHoursForSchedule(schedule));
    }

    [Theory]
    [InlineData("2026-10-05", 1, "2026-10-05")]
    [InlineData("2026-10-11", 1, "2026-10-05")]
    [InlineData("2026-10-14", 2, "2026-10-05")]
    [InlineData("2026-10-19", 2, "2026-10-19")]
    [InlineData("2026-10-04", 2, "2026-09-21")]
    public void 周期起点_一周取周一_两周与双周起始日对齐(string date, int weeks, string expected)
    {
        Assert.Equal(
            DateTime.Parse(expected),
            AttendanceReactService.ResolvePayPeriodStart(DateTime.Parse(date), weeks, new DateTime(2026, 10, 5)));
    }

    // ---------------- 样例构造 ----------------

    private sealed record SampleDay(int Index, string Schedule, string[] Segments, int Overtime, int AddBack, string? AutoSegment);

    private static SampleDay Day(int index, string schedule, params string[] segments) =>
        new(index, schedule, segments, 0, 0, null);

    private static SampleDay Day(int index, string schedule, string[] segments, int overtime = 0, int addBack = 0, string? autoSegment = null) =>
        new(index, schedule, segments, overtime, addBack, autoSegment);

    private static PayrollLeaveItem Leave(int index, decimal hours) => new(PeriodStart.AddDays(index), hours);

    private static PayrollPayResult CalculateSample(PayrollRate rate, IEnumerable<SampleDay> days, IEnumerable<PayrollLeaveItem>? leave = null)
    {
        var workItems = new List<PayrollWorkItem>();
        foreach (var day in days)
        {
            var (start, end) = SplitRange(day.Schedule);
            var segments = day.Segments.Select(item =>
            {
                var (from, to) = SplitRange(item);
                return Segment(day.Index, from, to);
            }).ToList();
            if (day.AutoSegment != null)
            {
                segments.Add(new PayrollSegment(At(day.Index, day.AutoSegment), At(day.Index + 1, "00:00"), true));
            }
            var result = PayrollCalculator.CalculateDay(new PayrollDayInput(
                At(day.Index, start),
                At(day.Index, end),
                segments,
                AttendanceMealBreakRules.EffectiveCount(null, TimeSpan.Parse(start), TimeSpan.Parse(end)),
                day.Overtime,
                day.AddBack));
            var date = PeriodStart.AddDays(day.Index);
            workItems.Add(new PayrollWorkItem(date, PayrollCalculator.ResolveTier(date, PublicHolidays.Contains(date)), result.PaidMinutes));
        }

        return PayrollCalculator.CalculatePay(new PayrollPayInput
        {
            PeriodStart = PeriodStart,
            Weeks = 2,
            Rates = new[] { rate },
            WorkItems = workItems,
            LeaveItems = leave?.ToList() ?? new List<PayrollLeaveItem>(),
        });
    }

    private static (string Start, string End) SplitRange(string range)
    {
        var parts = range.Split('-');
        return (parts[0], parts[1]);
    }

    private static DateTime At(int index, string time) => PeriodStart.AddDays(index).Add(TimeSpan.Parse(time));

    private static PayrollSegment Segment(int index, string from, string to) => new(At(index, from), At(index, to));

    private static PayrollRate Transfer(decimal weekday, decimal saturday, decimal sunday, decimal publicHoliday,
        string key = "rate", DateTime? effectiveFrom = null) =>
        new(key, effectiveFrom ?? new DateTime(2026, 7, 1), PayrollCalculator.PayModeTransfer, 0,
            weekday, saturday, sunday, publicHoliday, null, 0m, null);

    private static PayrollRate Mixed(decimal fixedPerWeek, decimal weekday, decimal saturday, decimal sunday,
        decimal publicHoliday, decimal cash, decimal allowance = 0m) =>
        new("rate", new DateTime(2026, 7, 1), PayrollCalculator.PayModeMixed, fixedPerWeek,
            weekday, saturday, sunday, publicHoliday, cash, allowance, null);

    private static PayrollRate Cash(decimal cash, decimal allowance = 0m) =>
        new("rate", new DateTime(2026, 7, 1), PayrollCalculator.PayModeCash, 0, null, null, null, null, cash, allowance, null);
}
