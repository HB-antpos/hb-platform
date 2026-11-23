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

        // ---------------- 补录带薪假（忘记申请的带薪病假 / 年假 / 年假发放） ----------------

        private static CreateAttendanceManualLeaveDto ManualLeaveRequest(
            string leaveType,
            DateTime? start = null,
            DateTime? end = null,
            DateTime[]? sickDates = null,
            string reason = "员工忘记申请，已电话确认",
            string userGuid = "staff-user",
            decimal? totalHours = null,
            bool? rests = null)
        {
            var from = (start ?? ManualEntryWorkDate).Date;
            return new CreateAttendanceManualLeaveDto
            {
                StoreCode = "BRI",
                UserGuid = userGuid,
                LeaveType = leaveType,
                StartDate = from,
                EndDate = (end ?? from).Date,
                Dates = sickDates?.ToList(),
                Reason = reason,
                TotalHours = totalHours,
                RestsOnLeaveDays = rests,
            };
        }

        /// <summary>预览通过后带上版本号再保存，与页面的两步流程一致。</summary>
        private static async Task<BlazorApp.Shared.DTOs.ApiResponse<AttendanceManualLeaveResultDto>> SaveManualLeaveAsync(
            AttendanceReactService service,
            CreateAttendanceManualLeaveDto request)
        {
            var preview = await service.PreviewManagedManualLeaveAsync(request);
            Assert.True(preview.Success, $"{preview.ErrorCode}: {preview.Message}");
            request.PreviewRevision = preview.Data!.PreviewRevision;
            return await service.CreateManagedManualLeaveAsync(request);
        }

        [Fact]
        public async Task 补录年假_预览_周末不计工时_只算区间内工作日乘8()
        {
            await SeedPayrollStoreAsync();

            // 10-15 周四 → 10-18 周日：只有周四、周五是工作日。
            var preview = await Manager().PreviewManagedManualLeaveAsync(
                ManualLeaveRequest("PaidAnnual", new DateTime(2026, 10, 15), new DateTime(2026, 10, 18)));

            Assert.True(preview.Success, $"{preview.ErrorCode}: {preview.Message}");
            Assert.Equal(16m, preview.Data!.PaidHours);
            Assert.Equal(new[] { 8m, 8m, 0m, 0m }, preview.Data.Days.Select(item => item.Hours).ToArray());
            Assert.False(preview.Data.NoCertificate);
            // 预览不写库。
            Assert.Empty(await _db.Queryable<AttendanceLeaveRequest>().ToListAsync());
        }

        [Fact]
        public async Task 补录年假_保存_直接已批准_自动标不能上班_工时表带补录留痕()
        {
            await SeedPayrollStoreAsync();

            var result = await SaveManualLeaveAsync(
                Manager(), ManualLeaveRequest("PaidAnnual", new DateTime(2026, 10, 15), new DateTime(2026, 10, 16)));

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            var leave = Assert.Single(await _db.Queryable<AttendanceLeaveRequest>().ToListAsync());
            Assert.Equal("Approved", leave.Status);
            Assert.Equal(16m, leave.PaidHours);
            Assert.Equal("manager-user", leave.ReviewedBy);
            Assert.Equal("补录：员工忘记申请，已电话确认", leave.ReviewRemark);
            // 不走审批：没有待审批记录。
            Assert.Empty(await _db.Queryable<AttendanceApproval>().Where(item => item.SourceType == "Leave").ToListAsync());
            // 与批准带薪年假一致：区间内每天一条「不能上班」。
            var unavailable = await _db.Queryable<AttendanceAvailability>().Where(item => item.SourceLeaveGuid == leave.LeaveGuid).ToListAsync();
            Assert.Equal(2, unavailable.Count);
            var hours = await Manager().GetPayrollHoursAsync(PayrollQuery());
            var day = hours.Data!.Employees.Single(item => item.UserGuid == "staff-user").Days
                .Single(item => item.Date == new DateTime(2026, 10, 15));
            Assert.Equal("PaidAnnual", day.Leave!.Type);
            Assert.Equal(8m, day.Leave.Hours);
            Assert.NotNull(day.Leave.ManualEntry);
            Assert.Equal("员工忘记申请，已电话确认", day.Leave.ManualEntry!.Reason);
            Assert.Equal("manager", day.Leave.ManualEntry.CreatedBy);
        }

        [Fact]
        public async Task 补录病假_在有排班没打卡的班上补录_工时按排班扣餐_不再算未打卡_标无医嘱()
        {
            await SeedPayrollStoreAsync();
            await SeedScheduleAsync("sched-sick", "BRI", "staff-user", ManualEntryWorkDate, "Active", new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0));
            var request = ManualLeaveRequest("PaidSick", sickDates: new[] { ManualEntryWorkDate });

            var preview = await Manager().PreviewManagedManualLeaveAsync(request);
            Assert.True(preview.Success, $"{preview.ErrorCode}: {preview.Message}");
            // 8 小时扣 1 次餐 → 7.5 h；没有医嘱要提示。
            Assert.Equal(7.5m, preview.Data!.PaidHours);
            Assert.True(preview.Data.NoCertificate);

            request.PreviewRevision = preview.Data.PreviewRevision;
            var result = await Manager().CreateManagedManualLeaveAsync(request);

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            var hours = await Manager().GetPayrollHoursAsync(PayrollQuery());
            var day = hours.Data!.Employees.Single(item => item.UserGuid == "staff-user").Days
                .Single(item => item.Date == ManualEntryWorkDate);
            Assert.Equal("PaidSick", day.Leave!.Type);
            Assert.Equal(7.5m, day.Leave.Hours);
            Assert.False(day.Leave.HasCertificate);
            Assert.NotNull(day.Leave.ManualEntry);
            // 病假已经处理了这个班：不再挂在「未打卡」里。
            Assert.False(day.MissingPunch);
        }

        [Fact]
        public async Task 补录病假_不受生病后N天内提交的限制_只要还在可改窗口内()
        {
            await SeedPayrollStoreAsync();
            // 10-12 周一：距今天（10-20）已 8 天，员工自己申请会被「生病后 N 天内提交」拦住；店长补录不受限。
            var monday = new DateTime(2026, 10, 12);
            await SeedScheduleAsync("sched-old", "BRI", "staff-user", monday, "Active", new TimeSpan(9, 0, 0), new TimeSpan(13, 0, 0));

            var result = await SaveManualLeaveAsync(Manager(), ManualLeaveRequest("PaidSick", monday, sickDates: new[] { monday }));

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
        }

        [Fact]
        public async Task 补录病假_已有打卡的排班_当天没排班_没选日期_都拒绝()
        {
            await SeedPayrollStoreAsync();
            await SeedScheduleAsync("sched-punched", "BRI", "staff-user", ManualEntryWorkDate, "Active", new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0));
            await _db.Insertable(
                CreateStoredPunchForSchedule("p-in", "sched-punched", "BRI", "staff-user", "ClockIn", ManualEntryWorkDate.AddHours(9))
            ).ExecuteCommandAsync();
            // 别人当天的排班不能算在 staff-user 头上：按「员工 + 日期」找排班。
            await SeedBoundStaffAsync("other-staff", EmployeeType.FullTime);
            await SeedScheduleAsync("sched-other", "BRI", "other-staff", new DateTime(2026, 10, 15), "Active", new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0));

            var punched = await Manager().PreviewManagedManualLeaveAsync(ManualLeaveRequest("PaidSick", sickDates: new[] { ManualEntryWorkDate }));
            var noSchedule = await Manager().PreviewManagedManualLeaveAsync(ManualLeaveRequest("PaidSick", sickDates: new[] { new DateTime(2026, 10, 15) }));
            var none = await Manager().PreviewManagedManualLeaveAsync(ManualLeaveRequest("PaidSick"));

            Assert.Equal("SICK_LEAVE_SCHEDULE_PUNCHED", punched.ErrorCode);
            Assert.Equal("SICK_LEAVE_SCHEDULE_INVALID", noSchedule.ErrorCode);
            Assert.Equal("SICK_LEAVE_SCHEDULE_REQUIRED", none.ErrorCode);
        }

        [Fact]
        public async Task 补录年假_休息的工作日已有打卡_拒绝_选了不休息则不受此限()
        {
            await SeedPayrollStoreAsync();
            await SeedScheduleAsync("sched-worked", "BRI", "staff-user", ManualEntryWorkDate, "Active", new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0));
            await _db.Insertable(
                CreateStoredPunchForSchedule("w-in", "sched-worked", "BRI", "staff-user", "ClockIn", ManualEntryWorkDate.AddHours(9))
            ).ExecuteCommandAsync();

            var rest = await Manager().PreviewManagedManualLeaveAsync(ManualLeaveRequest("PaidAnnual"));
            var noRest = await Manager().PreviewManagedManualLeaveAsync(ManualLeaveRequest("PaidAnnual", rests: false));

            Assert.Equal("ANNUAL_LEAVE_HAS_PUNCHES", rest.ErrorCode);
            Assert.True(noRest.Success, $"{noRest.ErrorCode}: {noRest.Message}");
            Assert.False(noRest.Data!.RestsOnLeaveDays);
        }

        [Fact]
        public async Task 补录请假_与已有病假或年假重叠_拒绝_不休息的年假同样占天()
        {
            await SeedPayrollStoreAsync();
            Assert.True((await SaveManualLeaveAsync(
                Manager(), ManualLeaveRequest("PaidAnnual", new DateTime(2026, 10, 13), new DateTime(2026, 10, 14)))).Success);

            var overlap = await Manager().PreviewManagedManualLeaveAsync(ManualLeaveRequest("PaidAnnual", new DateTime(2026, 10, 14), new DateTime(2026, 10, 15)));
            var overlapNoRest = await Manager().PreviewManagedManualLeaveAsync(
                ManualLeaveRequest("PaidAnnual", new DateTime(2026, 10, 13), new DateTime(2026, 10, 14), rests: false));

            Assert.Equal("LEAVE_OVERLAP", overlap.ErrorCode);
            Assert.Equal("LEAVE_OVERLAP", overlapNoRest.ErrorCode);
        }

        [Fact]
        public async Task 补录年假_指定合计小时数_平均摊到每个工作日_工资表与保存金额一致()
        {
            await SeedPayrollStoreAsync();
            // 10-13 周二 ~ 10-16 周五共 4 个工作日；默认 32 h，这里指定合计 10.01 h（不是每天）。
            var request = ManualLeaveRequest("PaidAnnual", new DateTime(2026, 10, 13), new DateTime(2026, 10, 16), totalHours: 10.01m);
            var preview = await Manager().PreviewManagedManualLeaveAsync(request);

            Assert.True(preview.Success, $"{preview.ErrorCode}: {preview.Message}");
            Assert.Equal(10.01m, preview.Data!.PaidHours);
            Assert.Equal(32m, preview.Data.DefaultPaidHours);
            Assert.Equal(10.01m, preview.Data.Days.Sum(item => item.Hours));
            Assert.Equal(2.51m, preview.Data.Days.Last().Hours);

            var saved = await SaveManualLeaveAsync(Manager(), request);
            Assert.True(saved.Success, $"{saved.ErrorCode}: {saved.Message}");
            Assert.Equal(10.01m, saved.Data!.PaidHours);
            var hours = await Manager().GetPayrollHoursAsync(PayrollQuery());
            var staff = hours.Data!.Employees.Single(item => item.UserGuid == "staff-user");
            Assert.Equal(2.5m, staff.Days.Single(item => item.Date == new DateTime(2026, 10, 13)).Leave!.Hours);
            Assert.Equal(2.51m, staff.Days.Single(item => item.Date == new DateTime(2026, 10, 16)).Leave!.Hours);
            Assert.Equal(10.01m, staff.Days.Where(item => item.Leave?.Type == "PaidAnnual").Sum(item => item.Leave!.Hours));
        }

        [Fact]
        public async Task 补录年假_合计小时数必须大于0且不超过区间天数乘24()
        {
            await SeedPayrollStoreAsync();
            var from = new DateTime(2026, 10, 13);
            var to = new DateTime(2026, 10, 14);

            Assert.Equal("ANNUAL_LEAVE_HOURS_INVALID",
                (await Manager().PreviewManagedManualLeaveAsync(ManualLeaveRequest("PaidAnnual", from, to, totalHours: 0m))).ErrorCode);
            Assert.Equal("ANNUAL_LEAVE_HOURS_INVALID",
                (await Manager().PreviewManagedManualLeaveAsync(ManualLeaveRequest("PaidAnnual", from, to, totalHours: -1m))).ErrorCode);
            // 2 天 × 24 = 48：48 可以，48.01 不行。
            Assert.True((await Manager().PreviewManagedManualLeaveAsync(ManualLeaveRequest("PaidAnnual", from, to, totalHours: 48m))).Success);
            Assert.Equal("ANNUAL_LEAVE_HOURS_TOO_MANY",
                (await Manager().PreviewManagedManualLeaveAsync(ManualLeaveRequest("PaidAnnual", from, to, totalHours: 48.01m))).ErrorCode);
        }

        [Fact]
        public async Task 补录年假_选不休息_不标不能上班_不清未打卡_仍按小时数计入_撤销可用()
        {
            await SeedPayrollStoreAsync();
            await SeedScheduleAsync("sched-work", "BRI", "staff-user", ManualEntryWorkDate, "Active", new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0));

            var saved = await SaveManualLeaveAsync(
                Manager(), ManualLeaveRequest("PaidAnnual", totalHours: 4m, rests: false));

            Assert.True(saved.Success, $"{saved.ErrorCode}: {saved.Message}");
            var leave = Assert.Single(await _db.Queryable<AttendanceLeaveRequest>().ToListAsync());
            Assert.Equal("补录（不休息）：员工忘记申请，已电话确认", leave.ReviewRemark);
            Assert.Equal(4m, leave.PaidHours);
            // 不休息：不写「不能上班」。
            Assert.Empty(await _db.Queryable<AttendanceAvailability>().Where(item => item.SourceLeaveGuid == leave.LeaveGuid).ToListAsync());
            var hours = await Manager().GetPayrollHoursAsync(PayrollQuery());
            var day = hours.Data!.Employees.Single(item => item.UserGuid == "staff-user").Days
                .Single(item => item.Date == ManualEntryWorkDate);
            Assert.Equal(4m, day.Leave!.Hours);
            Assert.NotNull(day.Leave.ManualEntry);
            // 员工照常上班：排班没打卡仍然提示未打卡（休息的年假才会清掉）。
            Assert.True(day.MissingPunch);

            // 同样能用补录的撤销入口撤销。
            var revoke = await Manager().RevokeManagedManualLeaveAsync(new RevokeAttendanceManualLeaveDto { StoreCode = "BRI", LeaveGuid = leave.LeaveGuid });
            Assert.True(revoke.Success, $"{revoke.ErrorCode}: {revoke.Message}");
            Assert.Equal("Revoked", await _db.Queryable<AttendanceLeaveRequest>()
                .Where(item => item.LeaveGuid == leave.LeaveGuid).Select(item => item.Status).FirstAsync());
        }

        [Fact]
        public async Task 年假发放已取消_补录与员工申请都拒绝ANNUAL_CASH_OUT_RETIRED()
        {
            await SeedPayrollStoreAsync();

            var manual = await Manager().PreviewManagedManualLeaveAsync(ManualLeaveRequest("AnnualCashOut"));
            Assert.Equal("ANNUAL_CASH_OUT_RETIRED", manual.ErrorCode);

            var employee = await Staff().CreateMyLeaveRequestAsync(new CreateAttendanceLeaveRequestDto
            {
                StoreCode = "BRI",
                LeaveType = "AnnualCashOut",
                StartDate = new DateTime(2026, 11, 17),
                EndDate = new DateTime(2026, 11, 18),
            });
            Assert.False(employee.Success);
            Assert.Equal("ANNUAL_CASH_OUT_RETIRED", employee.ErrorCode);
            Assert.Empty(await _db.Queryable<AttendanceLeaveRequest>().ToListAsync());
        }

        [Fact]
        public async Task 补录请假_临时工_没有带薪假_拒绝()
        {
            await SeedPayrollStoreAsync();
            await SeedBoundStaffAsync("casual-staff", EmployeeType.Temporary);

            var result = await Manager().PreviewManagedManualLeaveAsync(
                ManualLeaveRequest("PaidAnnual", userGuid: "casual-staff"));

            Assert.Equal("PAID_LEAVE_NOT_ELIGIBLE", result.ErrorCode);
        }

        [Fact]
        public async Task 补录请假_未来日期_超出窗口_原因为空_类型无效_都拒绝_Admin不受窗口限制()
        {
            await SeedPayrollStoreAsync();

            // 今天是 10-20 周二：明天不能补。
            var future = await Manager().PreviewManagedManualLeaveAsync(ManualLeaveRequest("PaidAnnual", new DateTime(2026, 10, 21)));
            Assert.Equal("LEAVE_DATE_IN_FUTURE", future.ErrorCode);

            // 10-08 周四：不在「本周 + 周二仍可改的上周」窗口内。
            var oldDay = new DateTime(2026, 10, 8);
            var expired = await Manager().PreviewManagedManualLeaveAsync(ManualLeaveRequest("PaidAnnual", oldDay));
            Assert.Equal("ADJUSTMENT_WINDOW_EXPIRED", expired.ErrorCode);
            var admin = CreateService("admin-user", "admin", "Admin");
            var allowed = await admin.PreviewManagedManualLeaveAsync(ManualLeaveRequest("PaidAnnual", oldDay));
            Assert.True(allowed.Success, $"{allowed.ErrorCode}: {allowed.Message}");

            Assert.Equal("REASON_REQUIRED", (await Manager().PreviewManagedManualLeaveAsync(ManualLeaveRequest("PaidAnnual", reason: "  "))).ErrorCode);
            Assert.Equal("INVALID_LEAVE_TYPE", (await Manager().PreviewManagedManualLeaveAsync(ManualLeaveRequest("SickLeave"))).ErrorCode);
        }

        [Fact]
        public async Task 补录请假_已锁定的周期_员工本人_缺预览版本号_预览之后改了日期_都不能保存()
        {
            await SeedPayrollStoreAsync();

            // 员工本人没有管理分店权限。
            Assert.False((await Staff().PreviewManagedManualLeaveAsync(ManualLeaveRequest("PaidAnnual"))).Success);

            // 缺版本号。
            Assert.Equal("MANUAL_LEAVE_PREVIEW_STALE", (await Manager().CreateManagedManualLeaveAsync(ManualLeaveRequest("PaidAnnual"))).ErrorCode);

            // 预览之后改了日期：版本变化，必须重新预览。
            var request = ManualLeaveRequest("PaidAnnual", new DateTime(2026, 10, 13), new DateTime(2026, 10, 14));
            var preview = await Manager().PreviewManagedManualLeaveAsync(request);
            request.PreviewRevision = preview.Data!.PreviewRevision;
            request.EndDate = new DateTime(2026, 10, 15);
            Assert.Equal("MANUAL_LEAVE_PREVIEW_STALE", (await Manager().CreateManagedManualLeaveAsync(request)).ErrorCode);
            Assert.Empty(await _db.Queryable<AttendanceLeaveRequest>().ToListAsync());

            await _db.Insertable(new AttendancePayPeriod
            {
                StoreCode = "BRI", PeriodStart = PayrollPeriodStart, Weeks = 2, Status = "Submitted",
            }).ExecuteCommandAsync();
            Assert.Equal("PERIOD_LOCKED", (await Manager().PreviewManagedManualLeaveAsync(ManualLeaveRequest("PaidAnnual"))).ErrorCode);
        }

        [Fact]
        public async Task 补录请假_撤销_改成已撤销_清除不能上班_工时表回到没有请假()
        {
            await SeedPayrollStoreAsync();
            var saved = await SaveManualLeaveAsync(
                Manager(), ManualLeaveRequest("PaidAnnual", new DateTime(2026, 10, 15), new DateTime(2026, 10, 16)));
            var leaveGuid = saved.Data!.LeaveGuid;

            var revoke = await Manager().RevokeManagedManualLeaveAsync(new RevokeAttendanceManualLeaveDto { StoreCode = "BRI", LeaveGuid = leaveGuid });

            Assert.True(revoke.Success, $"{revoke.ErrorCode}: {revoke.Message}");
            Assert.Equal("Revoked", (await _db.Queryable<AttendanceLeaveRequest>().FirstAsync(item => item.LeaveGuid == leaveGuid)).Status);
            Assert.Empty(await _db.Queryable<AttendanceAvailability>().Where(item => item.SourceLeaveGuid == leaveGuid && !item.IsDeleted).ToListAsync());
            var hours = await Manager().GetPayrollHoursAsync(PayrollQuery());
            Assert.DoesNotContain(hours.Data!.Employees, item => item.UserGuid == "staff-user"
                && item.Days.Any(d => d.Leave != null));
        }

        [Fact]
        public async Task 补录请假_撤销_员工自己申请的假不是补录_不能在这里撤销_已锁定也拒绝()
        {
            await SeedPayrollStoreAsync();
            // 员工自己申请、已批准的年假：没有「补录：」前缀。
            await _db.Insertable(new AttendanceLeaveRequest
            {
                LeaveGuid = "own-leave",
                StoreCode = "BRI",
                UserGuid = "staff-user",
                LeaveType = "PaidAnnual",
                StartDate = new DateTime(2026, 10, 15),
                EndDate = new DateTime(2026, 10, 15),
                PaidHours = 8m,
                Status = "Approved",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();

            var own = await Manager().RevokeManagedManualLeaveAsync(new RevokeAttendanceManualLeaveDto { StoreCode = "BRI", LeaveGuid = "own-leave" });
            Assert.Equal("MANUAL_LEAVE_NOT_FOUND", own.ErrorCode);

            var saved = await SaveManualLeaveAsync(
                Manager(), ManualLeaveRequest("PaidAnnual", new DateTime(2026, 10, 13)));
            await _db.Insertable(new AttendancePayPeriod
            {
                StoreCode = "BRI", PeriodStart = PayrollPeriodStart, Weeks = 2, Status = "Submitted",
            }).ExecuteCommandAsync();
            var locked = await Manager().RevokeManagedManualLeaveAsync(new RevokeAttendanceManualLeaveDto { StoreCode = "BRI", LeaveGuid = saved.Data!.LeaveGuid });
            Assert.Equal("PERIOD_LOCKED", locked.ErrorCode);
        }
    }
}
