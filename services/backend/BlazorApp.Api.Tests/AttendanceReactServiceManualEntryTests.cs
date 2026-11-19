using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Xunit;

namespace BlazorApp.Api.Tests
{
    /// <summary>
    /// 工时统计 / 工资表「手动补录工时」：预览、保存、撤销与未打卡标记。
    /// 夹具共用 AttendanceReactServiceTests / Payroll 用例：BRI 门店按 Brisbane（UTC+10）算本地时间，
    /// 「今天」是 2026-10-20 周二本地中午，所以店长可改本周和上周（周二仍在上周结算核对期内）。
    /// </summary>
    public sealed partial class AttendanceReactServiceTests
    {
        private static readonly DateTime ManualEntryWorkDate = new(2026, 10, 14);

        private static CreateAttendanceManualEntryDto ManualEntryRequest(
            string start = "09:00",
            string end = "15:00",
            string reason = "忘记打卡，已核对监控",
            DateTime? workDate = null,
            string userGuid = "staff-user",
            int? mealBreakCount = null)
        {
            var day = (workDate ?? ManualEntryWorkDate).Date;
            return new CreateAttendanceManualEntryDto
            {
                StoreCode = "BRI",
                UserGuid = userGuid,
                WorkDate = day,
                Segments = new List<AttendanceManualEntrySegmentDto>
                {
                    new() { StartLocal = day.Add(TimeSpan.Parse(start)), EndLocal = day.Add(TimeSpan.Parse(end)) },
                },
                MealBreakCount = mealBreakCount,
                Reason = reason,
            };
        }

        /// <summary>预览通过后带上版本号再保存，与页面的两步流程一致。</summary>
        private static async Task<BlazorApp.Shared.DTOs.ApiResponse<AttendanceManualEntryResultDto>> SaveManualEntryAsync(
            AttendanceReactService service,
            CreateAttendanceManualEntryDto request)
        {
            var preview = await service.PreviewManagedManualEntryAsync(request);
            Assert.True(preview.Success, $"{preview.ErrorCode}: {preview.Message}");
            request.PreviewRevision = preview.Data!.PreviewRevision;
            return await service.CreateManagedManualEntryAsync(request);
        }

        [Fact]
        public async Task 手动补录_预览_当天没排班_会新建排班并按引擎算出工时()
        {
            await SeedPayrollStoreAsync();

            var preview = await Manager().PreviewManagedManualEntryAsync(ManualEntryRequest("09:00", "15:00"));

            Assert.True(preview.Success, $"{preview.ErrorCode}: {preview.Message}");
            Assert.True(preview.Data!.WillCreateSchedule);
            Assert.Equal("09:00", preview.Data.ScheduleStart);
            Assert.Equal("15:00", preview.Data.ScheduleEnd);
            // 6 小时默认扣 1 次餐（30 分钟）→ 5.5 h；预览不写库。
            Assert.Equal(1, preview.Data.EffectiveMealBreakCount);
            Assert.Equal(330, preview.Data.PaidMinutes);
            Assert.Equal(5.5m, preview.Data.PaidHours);
            Assert.Equal(0, preview.Data.CandidateOvertimeMinutes);
            Assert.Empty(await _db.Queryable<AttendanceSchedule>().ToListAsync());
            Assert.Empty(await _db.Queryable<AttendancePunch>().ToListAsync());
        }

        [Fact]
        public async Task 手动补录_保存_无排班时同时建排班_打卡留痕为补录_工时进入工时统计()
        {
            await SeedPayrollStoreAsync();

            var result = await SaveManualEntryAsync(Manager(), ManualEntryRequest("09:00", "15:00"));

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            Assert.True(result.Data!.CreatedSchedule);
            Assert.Equal(2, result.Data.PunchCount);
            var schedule = Assert.Single(await _db.Queryable<AttendanceSchedule>().ToListAsync());
            Assert.Equal("Active", schedule.Status);
            Assert.StartsWith("补录：", schedule.Remark);
            Assert.Equal(new TimeSpan(9, 0, 0), schedule.StartTime);
            Assert.Equal(new TimeSpan(15, 0, 0), schedule.EndTime);
            var punches = await _db.Queryable<AttendancePunch>().OrderBy(item => item.PunchTimeUtc).ToListAsync();
            Assert.Equal(new[] { "ClockIn", "ClockOut" }, punches.Select(item => item.PunchType).ToArray());
            Assert.All(punches, punch =>
            {
                Assert.Equal("ManualEntry", punch.Source);
                Assert.Equal(schedule.ScheduleGuid, punch.ScheduleGuid);
                Assert.Equal("忘记打卡，已核对监控", punch.Remark);
                Assert.False(string.IsNullOrWhiteSpace(punch.AdjustmentGuid));
            });
            // 留痕：每个补录打卡对应一条已生效的补卡记录，申请人 = 审核人 = 操作的店长。
            var adjustments = await _db.Queryable<AttendancePunchAdjustment>().ToListAsync();
            Assert.Equal(2, adjustments.Count);
            Assert.All(adjustments, item =>
            {
                Assert.Equal("Applied", item.Status);
                Assert.Equal("staff-user", item.UserGuid);
                Assert.Equal("manager-user", item.RequestedByUserGuid);
                Assert.Equal("manager-user", item.ReviewedByUserGuid);
                Assert.False(item.IsManagerSelfDirect);
            });
            // 补录后：工时统计里那天有 5.5 h、带「补」留痕、不再是未打卡。
            var hours = await Manager().GetPayrollHoursAsync(PayrollQuery());
            var day = hours.Data!.Employees.Single(item => item.UserGuid == "staff-user").Days
                .Single(item => item.Date == ManualEntryWorkDate);
            Assert.Equal(5.5m, day.PaidHours);
            Assert.False(day.MissingPunch);
            Assert.NotNull(day.ManualEntry);
            Assert.Equal("忘记打卡，已核对监控", day.ManualEntry!.Reason);
            Assert.True(day.ManualEntry.CreatedSchedule);
            Assert.Equal("manager", day.ManualEntry.CreatedBy);
        }

        [Fact]
        public async Task 手动补录_保存_已有排班时只补打卡_不新建排班()
        {
            await SeedPayrollStoreAsync();
            await SeedScheduleAsync("sched-existing", "BRI", "staff-user", ManualEntryWorkDate, "Active", new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0));

            var result = await SaveManualEntryAsync(Manager(), ManualEntryRequest("09:00", "17:00"));

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            Assert.False(result.Data!.CreatedSchedule);
            Assert.Equal("sched-existing", result.Data.ScheduleGuid);
            Assert.Single(await _db.Queryable<AttendanceSchedule>().ToListAsync());
            // 8 小时扣 1 次餐 → 7.5 h。
            Assert.Equal(7.5m, result.Data.PaidHours);
        }

        [Fact]
        public async Task 手动补录_超出排班窗口的部分作为加班候选_保存后进待审_批准前不计薪()
        {
            await SeedPayrollStoreAsync();
            await SeedScheduleAsync("sched-existing", "BRI", "staff-user", ManualEntryWorkDate, "Active", new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0));
            var request = ManualEntryRequest("09:00", "18:00");

            var preview = await Manager().PreviewManagedManualEntryAsync(request);
            Assert.True(preview.Success, $"{preview.ErrorCode}: {preview.Message}");
            Assert.Equal(60, preview.Data!.CandidateOvertimeMinutes);
            // 计薪只认排班窗口内：09:00–17:00 扣 1 次餐 = 7.5 h，多出的 1 h 不在其中。
            Assert.Equal(7.5m, preview.Data.PaidHours);

            request.PreviewRevision = preview.Data.PreviewRevision;
            var result = await Manager().CreateManagedManualEntryAsync(request);
            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            var overtime = await _db.Queryable<AttendanceApproval>()
                .Where(item => item.SourceType == "Overtime" && item.SourceGuid == "sched-existing")
                .ToListAsync();
            var pending = Assert.Single(overtime);
            Assert.Equal("Pending", pending.ReviewStatus);
        }

        [Fact]
        public async Task 手动补录_多个班段_要求先后有序且互不重叠()
        {
            await SeedPayrollStoreAsync();
            var request = ManualEntryRequest("09:00", "12:00");
            request.Segments.Add(new AttendanceManualEntrySegmentDto
            {
                StartLocal = ManualEntryWorkDate.AddHours(11),
                EndLocal = ManualEntryWorkDate.AddHours(15),
            });

            var overlap = await Manager().PreviewManagedManualEntryAsync(request);
            Assert.Equal("SEGMENT_OVERLAP", overlap.ErrorCode);

            request.Segments[1].StartLocal = ManualEntryWorkDate.AddHours(13);
            var ok = await Manager().PreviewManagedManualEntryAsync(request);
            Assert.True(ok.Success, $"{ok.ErrorCode}: {ok.Message}");
            // 3 h + 2 h = 5 h；中间空档 1 h 抵掉那次用餐，所以不再扣餐。
            Assert.Equal(300, ok.Data!.WorkedMinutes);
        }

        [Theory]
        [InlineData("10:00", "09:30", "INVALID_TIME_RANGE")]
        [InlineData("09:00", "15:00", "REASON_REQUIRED")]
        public async Task 手动补录_时间或原因不合法_给明确错误码(string start, string end, string expectedCode)
        {
            await SeedPayrollStoreAsync();
            var request = ManualEntryRequest(start, end, reason: expectedCode == "REASON_REQUIRED" ? "  " : "忘记打卡");

            var result = await Manager().PreviewManagedManualEntryAsync(request);

            Assert.False(result.Success);
            Assert.Equal(expectedCode, result.ErrorCode);
        }

        [Fact]
        public async Task 手动补录_不能补未来时间_也不能跨午夜()
        {
            await SeedPayrollStoreAsync();
            // 「现在」是 10-20 12:00：今天 13:00 还没发生。
            var future = await Manager().PreviewManagedManualEntryAsync(
                ManualEntryRequest("09:00", "13:00", workDate: new DateTime(2026, 10, 20)));
            Assert.Equal("PUNCH_TIME_IN_FUTURE", future.ErrorCode);

            var request = ManualEntryRequest("20:00", "23:00");
            request.Segments[0].EndLocal = ManualEntryWorkDate.AddDays(1).AddHours(2);
            var overnight = await Manager().PreviewManagedManualEntryAsync(request);
            Assert.Equal("PUNCH_TIME_INVALID", overnight.ErrorCode);
        }

        [Fact]
        public async Task 手动补录_当天已有打卡_要求改走修改打卡()
        {
            await SeedPayrollStoreAsync();
            await SeedWorkedScheduleAsync("sched-worked", ManualEntryWorkDate, "09:00-17:00", "09:00-12:00");

            var result = await Manager().PreviewManagedManualEntryAsync(ManualEntryRequest("13:00", "17:00"));

            Assert.Equal("ALREADY_HAS_PUNCHES", result.ErrorCode);
        }

        [Fact]
        public async Task 手动补录_员工不属于本店_被拒绝()
        {
            await SeedPayrollStoreAsync();

            var result = await Manager().PreviewManagedManualEntryAsync(ManualEntryRequest(userGuid: "ghost-user"));

            Assert.Equal("EMPLOYEE_NOT_IN_STORE", result.ErrorCode);
        }

        [Fact]
        public async Task 手动补录_本期已提交锁定_预览与保存都拒绝()
        {
            await SeedPayrollStoreAsync();
            await _db.Insertable(new AttendancePayPeriod
            {
                StoreCode = "BRI", PeriodStart = PayrollPeriodStart, Weeks = 2, Status = "Submitted",
            }).ExecuteCommandAsync();

            var result = await Manager().PreviewManagedManualEntryAsync(ManualEntryRequest());

            Assert.Equal("PERIOD_LOCKED", result.ErrorCode);
        }

        [Fact]
        public async Task 手动补录_非Admin只能补本周和结算核对期内的上周_Admin不受窗口限制()
        {
            await SeedPayrollStoreAsync();
            // 10-07 是两周前，超出店长窗口；Admin 不受限（同代补卡）。
            var oldDay = new DateTime(2026, 10, 7);

            var denied = await Manager().PreviewManagedManualEntryAsync(ManualEntryRequest(workDate: oldDay));
            Assert.Equal("ADJUSTMENT_WINDOW_EXPIRED", denied.ErrorCode);

            var admin = CreateService("admin-user", "admin", "Admin");
            var allowed = await admin.PreviewManagedManualEntryAsync(ManualEntryRequest(workDate: oldDay));
            Assert.True(allowed.Success, $"{allowed.ErrorCode}: {allowed.Message}");
        }

        [Fact]
        public async Task 手动补录_保存时预览版本不匹配_要求重新预览()
        {
            await SeedPayrollStoreAsync();
            var request = ManualEntryRequest();
            var preview = await Manager().PreviewManagedManualEntryAsync(request);
            request.PreviewRevision = preview.Data!.PreviewRevision;
            // 预览之后改了下班时间：版本变化，必须重新预览。
            request.Segments[0].EndLocal = ManualEntryWorkDate.AddHours(16);

            var result = await Manager().CreateManagedManualEntryAsync(request);

            Assert.Equal("MANUAL_ENTRY_PREVIEW_STALE", result.ErrorCode);
            Assert.Empty(await _db.Queryable<AttendancePunch>().ToListAsync());
        }

        [Fact]
        public async Task 手动补录_缺预览版本号_拒绝保存()
        {
            await SeedPayrollStoreAsync();

            var result = await Manager().CreateManagedManualEntryAsync(ManualEntryRequest());

            Assert.Equal("MANUAL_ENTRY_PREVIEW_STALE", result.ErrorCode);
        }

        [Fact]
        public async Task 手动补录_员工本人没有管理分店权限_被拒绝()
        {
            await SeedPayrollStoreAsync();

            var result = await Staff().PreviewManagedManualEntryAsync(ManualEntryRequest());

            Assert.False(result.Success);
        }

        [Fact]
        public async Task 手动补录_撤销_补录新建的排班一并取消_打卡软删除_补卡记录标成已撤销()
        {
            await SeedPayrollStoreAsync();
            Assert.True((await SaveManualEntryAsync(Manager(), ManualEntryRequest())).Success);

            var revoke = await Manager().RevokeManagedManualEntryAsync(new RevokeAttendanceManualEntryDto
            {
                StoreCode = "BRI", UserGuid = "staff-user", WorkDate = ManualEntryWorkDate,
            });

            Assert.True(revoke.Success, $"{revoke.ErrorCode}: {revoke.Message}");
            Assert.Empty(await _db.Queryable<AttendancePunch>().Where(item => !item.IsDeleted).ToListAsync());
            Assert.Equal(2, (await _db.Queryable<AttendancePunch>().ToListAsync()).Count);
            var schedule = Assert.Single(await _db.Queryable<AttendanceSchedule>().ToListAsync());
            Assert.True(schedule.IsDeleted);
            Assert.Equal("Cancelled", schedule.Status);
            Assert.All(await _db.Queryable<AttendancePunchAdjustment>().ToListAsync(), item => Assert.Equal("Revoked", item.Status));
            // 撤销后工时统计里那天回到没有工时，也不再有补录标记。
            var hours = await Manager().GetPayrollHoursAsync(PayrollQuery());
            Assert.DoesNotContain(hours.Data!.Employees, item => item.UserGuid == "staff-user");
        }

        [Fact]
        public async Task 手动补录_撤销_排班本来就有时保留排班_回到未打卡状态()
        {
            await SeedPayrollStoreAsync();
            await SeedScheduleAsync("sched-existing", "BRI", "staff-user", ManualEntryWorkDate, "Active", new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0));
            Assert.True((await SaveManualEntryAsync(Manager(), ManualEntryRequest("09:00", "17:00"))).Success);

            var revoke = await Manager().RevokeManagedManualEntryAsync(new RevokeAttendanceManualEntryDto
            {
                StoreCode = "BRI", UserGuid = "staff-user", WorkDate = ManualEntryWorkDate,
            });

            Assert.True(revoke.Success, $"{revoke.ErrorCode}: {revoke.Message}");
            var schedule = Assert.Single(await _db.Queryable<AttendanceSchedule>().ToListAsync());
            Assert.False(schedule.IsDeleted);
            Assert.Equal("Active", schedule.Status);
            var hours = await Manager().GetPayrollHoursAsync(PayrollQuery());
            var day = hours.Data!.Employees.Single(item => item.UserGuid == "staff-user").Days
                .Single(item => item.Date == ManualEntryWorkDate);
            Assert.True(day.MissingPunch);
            Assert.Null(day.ManualEntry);
        }

        [Fact]
        public async Task 手动补录_撤销_没有补录_或当天还有其他打卡_或已锁定_都拒绝()
        {
            await SeedPayrollStoreAsync();
            var key = new RevokeAttendanceManualEntryDto { StoreCode = "BRI", UserGuid = "staff-user", WorkDate = ManualEntryWorkDate };

            Assert.Equal("MANUAL_ENTRY_NOT_FOUND", (await Manager().RevokeManagedManualEntryAsync(key)).ErrorCode);

            Assert.True((await SaveManualEntryAsync(Manager(), ManualEntryRequest("09:00", "12:00"))).Success);
            // 补录之后员工又自己打了一张卡：不再允许一键撤销补录（会让卡序错乱）。
            var schedule = await _db.Queryable<AttendanceSchedule>().FirstAsync(item => item.UserGuid == "staff-user");
            await _db.Insertable(
                CreateStoredPunchForSchedule("late-in", schedule.ScheduleGuid, "BRI", "staff-user", "ClockIn", ManualEntryWorkDate.AddHours(13))
            ).ExecuteCommandAsync();
            Assert.Equal("MANUAL_ENTRY_HAS_OTHER_PUNCHES", (await Manager().RevokeManagedManualEntryAsync(key)).ErrorCode);

            await _db.Insertable(new AttendancePayPeriod
            {
                StoreCode = "BRI", PeriodStart = PayrollPeriodStart, Weeks = 2, Status = "Submitted",
            }).ExecuteCommandAsync();
            Assert.Equal("PERIOD_LOCKED", (await Manager().RevokeManagedManualEntryAsync(key)).ErrorCode);
        }

        [Fact]
        public async Task 工时统计_有排班但窗口已过且没有任何打卡_标记为未打卡并带排班时间()
        {
            await SeedPayrollStoreAsync();
            await SeedScheduleAsync("sched-miss", "BRI", "staff-user", ManualEntryWorkDate, "Active", new TimeSpan(9, 0, 0), new TimeSpan(15, 30, 0));
            // 今天的班：窗口还没结束，不算未打卡。
            await SeedScheduleAsync("sched-today", "BRI", "staff-user", new DateTime(2026, 10, 20), "Active", new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0));
            var query = new AttendancePayrollQueryDto { StoreCode = "BRI", PeriodStart = new DateTime(2026, 10, 12), Weeks = 1 };

            var hours = await Manager().GetPayrollHoursAsync(query);

            // 只有未打卡班、0 工时的人也要列出来，店长才找得到该补录的人。
            var employee = hours.Data!.Employees.Single(item => item.UserGuid == "staff-user");
            var day = employee.Days.Single(item => item.Date == ManualEntryWorkDate);
            Assert.True(day.MissingPunch);
            Assert.Equal("09:00", day.ScheduleStart);
            Assert.Equal("15:30", day.ScheduleEnd);
            // 有排班但没打卡：计薪工时是 0（不是 null；null 专指当天没有排班也没有打卡）。
            Assert.Equal(0m, day.PaidHours);

            var current = await Manager().GetPayrollHoursAsync(new AttendancePayrollQueryDto
            {
                StoreCode = "BRI", PeriodStart = new DateTime(2026, 10, 19), Weeks = 1,
            });
            Assert.DoesNotContain(current.Data!.Employees, item => item.UserGuid == "staff-user"
                && item.Days.Any(d => d.MissingPunch));
        }
    }
}
