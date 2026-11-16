using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Xunit;

namespace BlazorApp.Api.Tests
{
    /// <summary>
    /// 未挂排班的打卡挂回排班：排班晚建 / 晚发布时，此前的打卡要按「排班当时已发布」的口径挂回去，
    /// 否则不计入排班工时。夹具共用 AttendanceReactServiceTests：BRI 门店按 Brisbane（UTC+10）算本地时间。
    /// </summary>
    public sealed partial class AttendanceReactServiceTests
    {
        private static readonly DateTime AdoptionWorkDate = new(2026, 5, 18);

        /// <summary>当天 18:00（门店本地），班次都已结束。</summary>
        private static readonly DateTime AdoptionEveningUtc = new(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task 排班晚发布_单个排班改为已发布时_此前的上下班卡挂回排班并取消未排班待审()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("late-schedule", "BRI", "staff-user", AdoptionWorkDate, "Draft", new TimeSpan(9, 0, 0), new TimeSpan(17, 30, 0));
            await SeedUnscheduledPunchAsync("late-in", "ClockIn", AdoptionWorkDate.AddHours(9).AddMinutes(2), withPendingApproval: true);
            await SeedUnscheduledPunchAsync("late-out", "ClockOut", AdoptionWorkDate.AddHours(17).AddMinutes(39), withPendingApproval: true);
            _timeProvider.SetUtcNow(AdoptionEveningUtc);

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .UpdateScheduleAsync("late-schedule", new UpdateAttendanceScheduleDto
                {
                    WorkDate = AdoptionWorkDate,
                    StartTime = new TimeSpan(9, 0, 0),
                    EndTime = new TimeSpan(17, 30, 0),
                    Status = "Active",
                });

            Assert.True(result.Success, result.Message);
            var punches = await LoadPunchesAsync("late-in", "late-out");
            Assert.All(punches, punch => Assert.Equal("late-schedule", punch.ScheduleGuid));
            // 09:02 在迟到宽限内、17:39 晚走不足 15 分钟：挂回后都是正常卡，不再是「未排班」。
            Assert.All(punches, punch => Assert.Equal("Normal", punch.Status));
            Assert.Equal(0, await CountPunchApprovalsAsync("Pending"));
            Assert.Equal(2, await CountPunchApprovalsAsync("Cancelled"));
        }

        [Fact]
        public async Task 排班晚发布_整周发布时_此前的上下班卡挂回排班()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("week-schedule", "BRI", "staff-user", AdoptionWorkDate, "Draft", new TimeSpan(9, 0, 0), new TimeSpan(17, 30, 0));
            await SeedUnscheduledPunchAsync("week-in", "ClockIn", AdoptionWorkDate.AddHours(8).AddMinutes(56), withPendingApproval: true);
            await SeedUnscheduledPunchAsync("week-out", "ClockOut", AdoptionWorkDate.AddHours(17).AddMinutes(30), withPendingApproval: true);
            _timeProvider.SetUtcNow(AdoptionEveningUtc);

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .PublishWeekAsync(new PublishAttendanceWeekDto { StoreCode = "BRI", WeekStartDate = AdoptionWorkDate });

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, result.Data);
            var punches = await LoadPunchesAsync("week-in", "week-out");
            Assert.All(punches, punch => Assert.Equal("week-schedule", punch.ScheduleGuid));
            Assert.All(punches, punch => Assert.Equal("Normal", punch.Status));
            Assert.Equal(0, await CountPunchApprovalsAsync("Pending"));
        }

        [Fact]
        public async Task 历史数据_员工打开今日打卡时_已发布排班之前的未排班卡自动挂回()
        {
            await SeedStoreScopeAsync();
            // 模拟修复上线前留下的数据：排班已是已发布，但更早的打卡仍未挂排班。
            await SeedScheduleAsync("heal-schedule", "BRI", "staff-user", AdoptionWorkDate, "Active", new TimeSpan(9, 0, 0), new TimeSpan(17, 30, 0));
            await SeedUnscheduledPunchAsync("heal-in", "ClockIn", AdoptionWorkDate.AddHours(9).AddMinutes(2), withPendingApproval: false);
            await SeedUnscheduledPunchAsync("heal-out", "ClockOut", AdoptionWorkDate.AddHours(17).AddMinutes(39), withPendingApproval: true);
            _timeProvider.SetUtcNow(AdoptionEveningUtc);

            var today = await CreateService("staff-user", "staff", "StoreStaff")
                .GetMyTodayAsync(AdoptionWorkDate, "BRI");

            Assert.True(today.Success, today.Message);
            var schedule = Assert.Single(today.Data!.Schedules);
            // 当次返回就要反映挂回后的结果：一段 09:02–17:39，共 517 分钟。
            Assert.Equal(517, schedule.WorkedMinutes);
            var punches = await LoadPunchesAsync("heal-in", "heal-out");
            Assert.All(punches, punch => Assert.Equal("heal-schedule", punch.ScheduleGuid));
            Assert.Equal(0, await CountPunchApprovalsAsync("Pending"));
        }

        [Fact]
        public async Task 已审过的未排班卡_挂回后保留审核结论_不重开待审()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("reviewed-schedule", "BRI", "staff-user", AdoptionWorkDate, "Draft", new TimeSpan(9, 0, 0), new TimeSpan(17, 30, 0));
            // 09:20 上班：按排班算是迟到，但店长此前已把这张未排班卡审过。
            await SeedUnscheduledPunchAsync("reviewed-in", "ClockIn", AdoptionWorkDate.AddHours(9).AddMinutes(20), withPendingApproval: false, status: "Approved");
            await _db.Insertable(new AttendanceApproval
            {
                ApprovalGuid = "reviewed-in-approval",
                SourceType = "Punch",
                SourceGuid = "reviewed-in",
                StoreCode = "BRI",
                ApplicantUserGuid = "staff-user",
                ReviewStatus = "Approved",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 2, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .UpdateScheduleAsync("reviewed-schedule", new UpdateAttendanceScheduleDto
                {
                    WorkDate = AdoptionWorkDate,
                    StartTime = new TimeSpan(9, 0, 0),
                    EndTime = new TimeSpan(17, 30, 0),
                    Status = "Active",
                });

            Assert.True(result.Success, result.Message);
            var punch = Assert.Single(await LoadPunchesAsync("reviewed-in"));
            Assert.Equal("reviewed-schedule", punch.ScheduleGuid);
            Assert.Equal("Approved", punch.Status);
            Assert.Equal(0, await CountPunchApprovalsAsync("Pending"));
        }

        [Fact]
        public async Task 漏打上班卡_下班时间过后才首次扫码_不挂回已结束的排班()
        {
            await SeedStoreScopeAsync();
            // 排班 15:00–17:30 早就发布了，员工 15:00 没打卡，17:30 过后才连扫两次：这不是排班晚建，仍属未排班打卡。
            await SeedScheduleAsync("missed-schedule", "BRI", "staff-user", AdoptionWorkDate, "Active", new TimeSpan(15, 0, 0), new TimeSpan(17, 30, 0));
            await SeedUnscheduledPunchAsync("missed-in", "ClockIn", AdoptionWorkDate.AddHours(17).AddMinutes(30).AddSeconds(19), withPendingApproval: true);
            await SeedUnscheduledPunchAsync("missed-out", "ClockOut", AdoptionWorkDate.AddHours(17).AddMinutes(30).AddSeconds(32), withPendingApproval: true);
            _timeProvider.SetUtcNow(AdoptionEveningUtc);

            var today = await CreateService("staff-user", "staff", "StoreStaff")
                .GetMyTodayAsync(AdoptionWorkDate, "BRI");

            Assert.True(today.Success, today.Message);
            var punches = await LoadPunchesAsync("missed-in", "missed-out");
            Assert.All(punches, punch => Assert.Null(punch.ScheduleGuid));
            Assert.All(punches, punch => Assert.Equal("NoSchedule", punch.Status));
            Assert.Equal(2, await CountPunchApprovalsAsync("Pending"));
        }

        [Fact]
        public async Task 排班名下已有更晚的打卡_不自动挂回_避免改写已有班段()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("mixed-schedule", "BRI", "staff-user", AdoptionWorkDate, "Active", new TimeSpan(9, 0, 0), new TimeSpan(17, 30, 0));
            await SeedUnscheduledPunchAsync("mixed-orphan-in", "ClockIn", AdoptionWorkDate.AddHours(9).AddMinutes(2), withPendingApproval: false);
            await _db.Insertable(CreateStoredPunchForSchedule(
                "mixed-linked-in", "mixed-schedule", "BRI", "staff-user", "ClockIn", AdoptionWorkDate.AddHours(13)))
                .ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 4, 0, 0, DateTimeKind.Utc));

            var today = await CreateService("staff-user", "staff", "StoreStaff")
                .GetMyTodayAsync(AdoptionWorkDate, "BRI");

            Assert.True(today.Success, today.Message);
            var orphan = Assert.Single(await LoadPunchesAsync("mixed-orphan-in"));
            Assert.Null(orphan.ScheduleGuid);
            Assert.Equal("NoSchedule", orphan.Status);
        }

        [Fact]
        public async Task 上班时还没排班_排班发布后在下班时间前扫码_记为该排班的下班而不是第二张上班卡()
        {
            await SeedStoreScopeAsync();
            await SeedUnscheduledPunchAsync("early-in", "ClockIn", AdoptionWorkDate.AddHours(9).AddMinutes(2), withPendingApproval: true);
            await SeedScheduleAsync("early-schedule", "BRI", "staff-user", AdoptionWorkDate, "Active", new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0));
            var request = CreatePunchRequest("ClockOut", "2026-05-18T06:00:00Z");
            request.ExpectedPunchType = "ClockOut";

            var result = await CreateService("staff-user", "staff", "StoreStaff").PunchAsync(request);

            Assert.True(result.Success, result.Message);
            Assert.Equal("ClockOut", result.Data!.PunchType);
            Assert.Equal("early-schedule", result.Data.ScheduleGuid);
            var clockIn = Assert.Single(await LoadPunchesAsync("early-in"));
            Assert.Equal("early-schedule", clockIn.ScheduleGuid);
            Assert.Equal("Normal", clockIn.Status);
            // 上班卡原来的「未排班」待审随挂回一并取消。
            Assert.Equal("Cancelled", await _db.Queryable<AttendanceApproval>()
                .Where(item => item.SourceType == "Punch" && item.SourceGuid == "early-in")
                .Select(item => item.ReviewStatus)
                .FirstAsync());
        }

        [Fact]
        public async Task 审核未排班卡_能挂回已发布排班的_先挂回再按排班口径判定()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("review-schedule", "BRI", "staff-user", AdoptionWorkDate, "Active", new TimeSpan(9, 0, 0), new TimeSpan(17, 30, 0));
            await SeedUnscheduledPunchAsync("review-in", "ClockIn", AdoptionWorkDate.AddHours(9).AddMinutes(2), withPendingApproval: true);
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 2, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .ApproveAsync("review-in-approval", new ReviewAttendanceApprovalDto { ReviewRemark = "批准" });

            // 挂回后是按时上班的正常卡，已经没有需要审的异常。
            Assert.False(result.Success);
            Assert.Equal("PUNCH_APPROVAL_STALE", result.ErrorCode);
            var punch = Assert.Single(await LoadPunchesAsync("review-in"));
            Assert.Equal("review-schedule", punch.ScheduleGuid);
            Assert.Equal("Normal", punch.Status);
            Assert.Equal(1, await CountPunchApprovalsAsync("Cancelled"));
        }

        [Fact]
        public async Task 审核未排班卡_挂不回任何排班的_即使当天有已发布排班也能正常批准()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("ended-schedule", "BRI", "staff-user", AdoptionWorkDate, "Active", new TimeSpan(15, 0, 0), new TimeSpan(17, 30, 0));
            await SeedUnscheduledPunchAsync("ended-in", "ClockIn", AdoptionWorkDate.AddHours(17).AddMinutes(30).AddSeconds(19), withPendingApproval: true);
            _timeProvider.SetUtcNow(AdoptionEveningUtc);

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .ApproveAsync("ended-in-approval", new ReviewAttendanceApprovalDto { ReviewRemark = "确认到岗" });

            Assert.True(result.Success, result.Message);
            var punch = Assert.Single(await LoadPunchesAsync("ended-in"));
            Assert.Null(punch.ScheduleGuid);
            Assert.Equal("Approved", punch.Status);
        }

        private async Task SeedUnscheduledPunchAsync(
            string punchGuid,
            string punchType,
            DateTime punchTimeLocal,
            bool withPendingApproval,
            string status = "NoSchedule")
        {
            var punch = CreateStoredPunchForSchedule(punchGuid, "unused", "BRI", "staff-user", punchType, punchTimeLocal);
            punch.ScheduleGuid = null;
            punch.Status = status;
            await _db.Insertable(punch).ExecuteCommandAsync();
            if (withPendingApproval)
            {
                await _db.Insertable(new AttendanceApproval
                {
                    ApprovalGuid = $"{punchGuid}-approval",
                    SourceType = "Punch",
                    SourceGuid = punchGuid,
                    StoreCode = "BRI",
                    ApplicantUserGuid = "staff-user",
                    ReviewStatus = "Pending",
                    CreatedAt = DateTime.UtcNow,
                }).ExecuteCommandAsync();
            }
        }

        private async Task<List<AttendancePunch>> LoadPunchesAsync(params string[] punchGuids)
        {
            var rows = await _db.Queryable<AttendancePunch>()
                .Where(item => punchGuids.Contains(item.PunchGuid))
                .ToListAsync();
            Assert.Equal(punchGuids.Length, rows.Count);
            return rows;
        }

        private Task<int> CountPunchApprovalsAsync(string reviewStatus) =>
            _db.Queryable<AttendanceApproval>()
                .CountAsync(item => item.SourceType == "Punch" && item.ReviewStatus == reviewStatus);
    }
}
