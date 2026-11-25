using System;
using System.Linq;
using System.Threading.Tasks;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Xunit;

namespace BlazorApp.Api.Tests
{
    /// <summary>
    /// 整班未打卡（缺勤）待审：生成时机、豁免、自动取消、确认缺勤与提交门槛。
    /// 夹具共用 AttendanceReactServiceTests：BRI 门店按 Brisbane（UTC+10）算本地时间。
    /// 「今天」是 2026-11-25 周三本地中午；本周 11-23（周一）~ 11-29，启用日 11-16 之后。
    /// </summary>
    public sealed partial class AttendanceReactServiceTests
    {
        private static readonly DateTime NoShowWeekStart = new(2026, 11, 23);
        private static readonly DateTime NoShowWorkDate = new(2026, 11, 24);

        private async Task SeedNoShowStoreAsync()
        {
            await SeedPayrollStoreAsync();
            SetStoreLocalNow(new DateTime(2026, 11, 25, 12, 0, 0));
        }

        private static AttendancePayrollQueryDto NoShowWeekQuery(DateTime? periodStart = null) => new()
        {
            StoreCode = "BRI",
            PeriodStart = periodStart ?? NoShowWeekStart,
            Weeks = 1,
        };

        private Task<System.Collections.Generic.List<AttendanceApproval>> NoShowApprovalsAsync() =>
            _db.Queryable<AttendanceApproval>()
                .Where(item => item.SourceType == AttendanceReactService.NoShowSourceType)
                .ToListAsync();

        private static AttendanceSchedule NoShowSchedule(DateTime workDate, string start, string end) => new()
        {
            ScheduleGuid = "s",
            UserGuid = "staff-user",
            StoreCode = "BRI",
            WorkDate = workDate,
            StartTime = TimeSpan.Parse(start),
            EndTime = TimeSpan.Parse(end),
            Status = "Active",
        };

        // ---------------- 纯规则 ----------------

        [Fact]
        public void 缺勤到点判断_排班结束加宽限之后才算_启用日之前和关闭开关都不算_跨午夜落到次日()
        {
            var settings = new AttendanceSettings { NoShowApprovalEnabled = true, NoShowGraceMinutes = 60 };
            var schedule = NoShowSchedule(NoShowWorkDate, "09:00", "17:00");

            Assert.False(AttendanceReactService.IsNoShowDue(schedule, settings, NoShowWorkDate.AddHours(17).AddMinutes(59)));
            Assert.True(AttendanceReactService.IsNoShowDue(schedule, settings, NoShowWorkDate.AddHours(18)));

            settings.NoShowApprovalEnabled = false;
            Assert.False(AttendanceReactService.IsNoShowDue(schedule, settings, NoShowWorkDate.AddDays(1)));

            settings.NoShowApprovalEnabled = true;
            var beforeLaunch = NoShowSchedule(new DateTime(2026, 11, 15), "09:00", "17:00");
            Assert.False(AttendanceReactService.IsNoShowDue(beforeLaunch, settings, NoShowWorkDate));

            // 22:00–02:00 跨午夜：结束在次日 02:00，宽限 60 → 次日 03:00 才到点。
            var overnight = NoShowSchedule(NoShowWorkDate, "22:00", "02:00");
            Assert.False(AttendanceReactService.IsNoShowDue(overnight, settings, NoShowWorkDate.AddDays(1).AddHours(2).AddMinutes(59)));
            Assert.True(AttendanceReactService.IsNoShowDue(overnight, settings, NoShowWorkDate.AddDays(1).AddHours(3)));
        }

        [Fact]
        public void 缺勤请假覆盖判断_病假看所选排班_年假休息看日期_年假发放和已拒绝的假不算()
        {
            var schedule = NoShowSchedule(NoShowWorkDate, "09:00", "17:00");
            schedule.ScheduleGuid = "sched-a";
            AttendanceLeaveRequest Leave(string type, string status = "Approved", string? scheduleGuids = null) => new()
            {
                LeaveGuid = Guid.NewGuid().ToString(),
                UserGuid = "staff-user",
                StoreCode = "BRI",
                LeaveType = type,
                StartDate = NoShowWorkDate,
                EndDate = NoShowWorkDate,
                Status = status,
                ScheduleGuids = scheduleGuids,
            };

            Assert.True(AttendanceReactService.IsScheduleCoveredByLeave(schedule, new[] { Leave("PaidSick", scheduleGuids: "[\"sched-a\"]") }));
            Assert.False(AttendanceReactService.IsScheduleCoveredByLeave(schedule, new[] { Leave("PaidSick", scheduleGuids: "[\"sched-b\"]") }));
            Assert.True(AttendanceReactService.IsScheduleCoveredByLeave(schedule, new[] { Leave("PaidSick", "Pending", "[\"sched-a\"]") }));
            Assert.True(AttendanceReactService.IsScheduleCoveredByLeave(schedule, new[] { Leave("PaidAnnual") }));
            Assert.False(AttendanceReactService.IsScheduleCoveredByLeave(schedule, new[] { Leave("AnnualCashOut") }));
            Assert.False(AttendanceReactService.IsScheduleCoveredByLeave(schedule, new[] { Leave("PaidAnnual", "Rejected") }));
        }

        // ---------------- 生成 ----------------

        [Fact]
        public async Task 缺勤_打开工时统计时生成待审_计入待处理并挡住提交()
        {
            await SeedNoShowStoreAsync();
            await SeedScheduleAsync("sched-noshow", "BRI", "staff-user", NoShowWorkDate, "Active");

            var hours = await Manager().GetPayrollHoursAsync(NoShowWeekQuery());

            Assert.True(hours.Success, hours.Message);
            var approval = Assert.Single(await NoShowApprovalsAsync());
            Assert.Equal("Pending", approval.ReviewStatus);
            Assert.Equal("sched-noshow", approval.SourceGuid);
            Assert.Equal("staff-user", approval.ApplicantUserGuid);
            Assert.Equal(1, hours.Data!.PendingCount);
            var day = hours.Data.Employees.Single(item => item.UserGuid == "staff-user").Days
                .Single(item => item.Date == NoShowWorkDate);
            Assert.True(day.MissingPunch);
            Assert.Contains(AttendanceReactService.NoShowSourceType, day.Pending);

            var submit = await Manager().SubmitPayPeriodAsync(new AttendancePayPeriodKeyDto
            {
                StoreCode = "BRI",
                PeriodStart = NoShowWeekStart,
                Weeks = 1,
            });
            Assert.False(submit.Success);
            Assert.Equal("PERIOD_HAS_PENDING", submit.ErrorCode);

            // 审核列表能看到排班时段与标题。
            var pending = await Manager().GetPendingApprovalsAsync(new AttendanceApprovalQueryDto { StoreCode = "BRI" });
            var dto = Assert.Single(pending.Data!, item => item.SourceType == AttendanceReactService.NoShowSourceType);
            Assert.Equal(NoShowWorkDate, dto.WorkDate);
            Assert.Equal("09:00", dto.ScheduleStartTime);
            Assert.Equal("17:00", dto.ScheduleEndTime);
            Assert.Equal("缺勤待确认", dto.Title);
        }

        [Fact]
        public async Task 缺勤_后台检测_宽限内不生成_过了宽限才生成_重复跑幂等()
        {
            await SeedNoShowStoreAsync();
            // 今天 09:00–11:30，宽限 60 → 12:30 才到点；现在 12:00。
            await SeedScheduleAsync("sched-today", "BRI", "staff-user", new DateTime(2026, 11, 25), "Active",
                new TimeSpan(9, 0, 0), new TimeSpan(11, 30, 0));

            await Manager().RunNoShowDetectionAsync();
            Assert.Empty(await NoShowApprovalsAsync());

            SetStoreLocalNow(new DateTime(2026, 11, 25, 12, 30, 0));
            Assert.Equal(1, await Manager().RunNoShowDetectionAsync());
            Assert.Equal(0, await Manager().RunNoShowDetectionAsync());
            Assert.Single(await NoShowApprovalsAsync());
        }

        [Fact]
        public async Task 缺勤_启用日之前的排班_有打卡的排班_当天有未挂排班打卡的_关闭开关的_都不生成()
        {
            await SeedNoShowStoreAsync();
            await SeedScheduleAsync("sched-old", "BRI", "staff-user", new DateTime(2026, 11, 13), "Active");
            await SeedWorkedScheduleAsync("sched-worked", new DateTime(2026, 11, 23), "09:00-17:00", "09:00-17:00");
            await SeedScheduleAsync("sched-loose", "BRI", "staff-user", NoShowWorkDate, "Active");
            var loose = CreateStoredPunchForSchedule("loose-in", "sched-loose", "BRI", "staff-user", "ClockIn", NoShowWorkDate.AddHours(18));
            loose.ScheduleGuid = null;
            await _db.Insertable(loose).ExecuteCommandAsync();

            await Manager().GetPayrollHoursAsync(NoShowWeekQuery(new DateTime(2026, 11, 9)));
            await Manager().GetPayrollHoursAsync(NoShowWeekQuery());
            Assert.Empty(await NoShowApprovalsAsync());

            var admin = CreateService("admin-user", "admin", "Admin");
            Assert.True((await admin.UpdateSettingsAsync(new UpdateAttendanceSettingsDto { NoShowApprovalEnabled = false })).Success);
            await SeedScheduleAsync("sched-off", "BRI", "staff-user", new DateTime(2026, 11, 25), "Active",
                new TimeSpan(6, 0, 0), new TimeSpan(9, 0, 0));
            Assert.Equal(0, await Manager().RunNoShowDetectionAsync());
            await Manager().GetPayrollHoursAsync(NoShowWeekQuery());
            Assert.Empty(await NoShowApprovalsAsync());
        }

        [Fact]
        public async Task 缺勤_已提交锁定的工资期不再生成()
        {
            await SeedNoShowStoreAsync();
            await SeedScheduleAsync("sched-locked", "BRI", "staff-user", new DateTime(2026, 11, 18), "Active");
            await _db.Insertable(new AttendancePayPeriod
            {
                PeriodGuid = "period-locked",
                StoreCode = "BRI",
                PeriodStart = new DateTime(2026, 11, 16),
                Weeks = 1,
                Status = "Submitted",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();

            await Manager().GetPayrollHoursAsync(NoShowWeekQuery(new DateTime(2026, 11, 16)));

            Assert.Empty(await NoShowApprovalsAsync());
        }

        // ---------------- 自动取消 ----------------

        [Fact]
        public async Task 缺勤_补录打卡后待审自动取消()
        {
            await SeedNoShowStoreAsync();
            await SeedScheduleAsync("sched-noshow", "BRI", "staff-user", NoShowWorkDate, "Active");
            await Manager().GetPayrollHoursAsync(NoShowWeekQuery());
            Assert.Equal("Pending", Assert.Single(await NoShowApprovalsAsync()).ReviewStatus);

            var saved = await SaveManualEntryAsync(Manager(), ManualEntryRequest("09:00", "17:00", workDate: NoShowWorkDate));

            Assert.True(saved.Success, $"{saved.ErrorCode}: {saved.Message}");
            Assert.Equal("Cancelled", Assert.Single(await NoShowApprovalsAsync()).ReviewStatus);
            var hours = await Manager().GetPayrollHoursAsync(NoShowWeekQuery());
            Assert.Equal(0, hours.Data!.PendingCount);
            // 系统取消不算结案，但有打卡了也不会再生成。
            Assert.Single(await NoShowApprovalsAsync());
        }

        [Fact]
        public async Task 缺勤_补录带薪病假后待审自动取消()
        {
            await SeedNoShowStoreAsync();
            await SeedScheduleAsync("sched-noshow", "BRI", "staff-user", NoShowWorkDate, "Active");
            await Manager().GetPayrollHoursAsync(NoShowWeekQuery());

            var saved = await SaveManualLeaveAsync(
                Manager(),
                ManualLeaveRequest("PaidSick", NoShowWorkDate, sickDates: new[] { NoShowWorkDate }));

            Assert.True(saved.Success, $"{saved.ErrorCode}: {saved.Message}");
            Assert.Equal("Cancelled", Assert.Single(await NoShowApprovalsAsync()).ReviewStatus);
            await Manager().GetPayrollHoursAsync(NoShowWeekQuery());
            Assert.Single(await NoShowApprovalsAsync());
        }

        [Fact]
        public async Task 缺勤_删除排班后待审自动取消()
        {
            await SeedNoShowStoreAsync();
            await SeedScheduleAsync("sched-noshow", "BRI", "staff-user", NoShowWorkDate, "Active");
            await Manager().GetPayrollHoursAsync(NoShowWeekQuery());

            // 已发布且过了周一的排班只有管理员能删。
            var deleted = await CreateService("admin-user", "admin", "Admin").DeleteScheduleAsync("sched-noshow");

            Assert.True(deleted.Success, $"{deleted.ErrorCode}: {deleted.Message}");

            Assert.Equal("Cancelled", Assert.Single(await NoShowApprovalsAsync()).ReviewStatus);
        }

        // ---------------- 店长处理 ----------------

        [Fact]
        public async Task 缺勤_确认缺勤后格子显示缺勤_不再算未打卡_可以提交_不会重新生成()
        {
            await SeedNoShowStoreAsync();
            await SeedScheduleAsync("sched-noshow", "BRI", "staff-user", NoShowWorkDate, "Active");
            await Manager().GetPayrollHoursAsync(NoShowWeekQuery());
            var approval = Assert.Single(await NoShowApprovalsAsync());

            var approved = await Manager().ApproveAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto());

            Assert.True(approved.Success, $"{approved.ErrorCode}: {approved.Message}");
            var hours = await Manager().GetPayrollHoursAsync(NoShowWeekQuery());
            Assert.Equal(0, hours.Data!.PendingCount);
            var day = hours.Data.Employees.Single(item => item.UserGuid == "staff-user").Days
                .Single(item => item.Date == NoShowWorkDate);
            Assert.True(day.NoShowConfirmed);
            Assert.False(day.MissingPunch);
            Assert.Equal(0m, day.PaidHours ?? 0m);
            Assert.Single(await NoShowApprovalsAsync());

            var submit = await Manager().SubmitPayPeriodAsync(new AttendancePayPeriodKeyDto
            {
                StoreCode = "BRI",
                PeriodStart = NoShowWeekStart,
                Weeks = 1,
            });
            Assert.True(submit.Success, $"{submit.ErrorCode}: {submit.Message}");
        }

        [Fact]
        public async Task 缺勤_不能驳回()
        {
            await SeedNoShowStoreAsync();
            await SeedScheduleAsync("sched-noshow", "BRI", "staff-user", NoShowWorkDate, "Active");
            await Manager().GetPayrollHoursAsync(NoShowWeekQuery());
            var approval = Assert.Single(await NoShowApprovalsAsync());

            var rejected = await Manager().RejectAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto { ReviewRemark = "x" });

            Assert.False(rejected.Success);
            Assert.Equal("NO_SHOW_REJECT_NOT_ALLOWED", rejected.ErrorCode);
            Assert.Equal("Pending", Assert.Single(await NoShowApprovalsAsync()).ReviewStatus);
        }

        [Fact]
        public async Task 缺勤_确认时该班已有打卡_自动取消并提示()
        {
            await SeedNoShowStoreAsync();
            await SeedScheduleAsync("sched-noshow", "BRI", "staff-user", NoShowWorkDate, "Active");
            await Manager().GetPayrollHoursAsync(NoShowWeekQuery());
            var approval = Assert.Single(await NoShowApprovalsAsync());
            // 绕过对账直接写一张打卡，模拟审批时状态已变化。
            await _db.Insertable(CreateStoredPunchForSchedule(
                "late-in", "sched-noshow", "BRI", "staff-user", "ClockIn", NoShowWorkDate.AddHours(9))).ExecuteCommandAsync();

            var approved = await Manager().ApproveAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto());

            Assert.False(approved.Success);
            Assert.Equal("NO_SHOW_RESOLVED", approved.ErrorCode);
            Assert.Equal("Cancelled", Assert.Single(await NoShowApprovalsAsync()).ReviewStatus);
        }

        // ---------------- 设置项 ----------------

        [Fact]
        public async Task 缺勤设置_默认开启且宽限60分钟_宽限只能15到720()
        {
            await SeedNoShowStoreAsync();
            var admin = CreateService("admin-user", "admin", "Admin");

            var current = await admin.GetSettingsAsync();
            Assert.True(current.Data!.NoShowApprovalEnabled);
            Assert.Equal(60, current.Data.NoShowGraceMinutes);

            var tooSmall = await admin.UpdateSettingsAsync(new UpdateAttendanceSettingsDto { NoShowGraceMinutes = 10 });
            Assert.False(tooSmall.Success);
            Assert.Equal("INVALID_NO_SHOW_GRACE", tooSmall.ErrorCode);

            var updated = await admin.UpdateSettingsAsync(new UpdateAttendanceSettingsDto { NoShowGraceMinutes = 120 });
            Assert.True(updated.Success, updated.Message);
            Assert.Equal(120, updated.Data!.NoShowGraceMinutes);
            Assert.True(updated.Data.NoShowApprovalEnabled);
        }
    }
}
