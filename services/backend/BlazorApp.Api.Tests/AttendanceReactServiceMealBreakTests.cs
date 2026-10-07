using System;
using System.Linq;
using System.Threading.Tasks;
using BlazorApp.Api.Models;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Xunit;

namespace BlazorApp.Api.Tests
{
    /// <summary>
    /// 用餐休息：当班一键开始/结束休息、下班时的缺休息声明与店长审核、计薪工时的扣除与加回。
    /// 夹具与造数方法共用 AttendanceReactServiceTests；BRI 店为 UTC+10，默认排班 5/18 09:00–17:00（8 小时 → 1 次用餐）。
    /// </summary>
    public sealed partial class AttendanceReactServiceTests
    {
        private const string MealClockInUtc = "2026-05-17T22:42:00Z"; // 本地 08:42
        private const string MealFinalClockOutUtc = "2026-05-18T07:22:00Z"; // 本地 17:22，已过排班结束前的宽限
        private const string MealBreakStartUtc = "2026-05-18T03:00:00Z"; // 本地 13:00
        private const string MealBreakEndUtc = "2026-05-18T03:30:00Z"; // 本地 13:30

        [Fact]
        public async Task StartMyMealBreakAsync_WithoutOpenSegment_Rejects()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            _timeProvider.SetUtcNow(DateTime.Parse(MealBreakStartUtc).ToUniversalTime());

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .StartMyMealBreakAsync(new AttendanceMealBreakRequestDto { StoreCode = "BRI" });

            Assert.False(result.Success);
            Assert.Equal("NO_OPEN_SEGMENT", result.ErrorCode);
            Assert.Empty(await _db.Queryable<AttendanceMealBreak>().ToListAsync());
        }

        [Fact]
        public async Task StartAndEndMyMealBreakAsync_RecordsBreakAndTodayReflectsIt()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            Assert.True((await service.PunchAsync(CreatePunchRequest("ClockIn", MealClockInUtc))).Success);

            _timeProvider.SetUtcNow(DateTime.Parse(MealBreakStartUtc).ToUniversalTime());
            var started = await service.StartMyMealBreakAsync(new AttendanceMealBreakRequestDto { StoreCode = "BRI" });
            var startedAgain = await service.StartMyMealBreakAsync(new AttendanceMealBreakRequestDto { StoreCode = "BRI" });
            var midBreak = await MealScheduleAsync(service);

            Assert.True(started.Success, started.Message);
            Assert.True(started.Data!.HasOpenBreak);
            Assert.True(startedAgain.Success, startedAgain.Message); // 重复点击幂等
            Assert.Single(await _db.Queryable<AttendanceMealBreak>().ToListAsync());
            Assert.True(midBreak.Meal!.HasOpenBreak);
            Assert.Null(midBreak.Meal.NextReminderAtUtc); // 休息中不再提醒

            _timeProvider.SetUtcNow(DateTime.Parse(MealBreakEndUtc).ToUniversalTime());
            var ended = await service.EndMyMealBreakAsync(new AttendanceMealBreakRequestDto { StoreCode = "BRI" });
            var endedAgain = await service.EndMyMealBreakAsync(new AttendanceMealBreakRequestDto { StoreCode = "BRI" });

            Assert.True(ended.Success, ended.Message);
            Assert.False(ended.Data!.HasOpenBreak);
            Assert.Equal(1, ended.Data.HandledCount);
            Assert.Single(ended.Data.Breaks);
            Assert.False(endedAgain.Success);
            Assert.Equal("NO_OPEN_MEAL_BREAK", endedAgain.ErrorCode);
        }

        [Fact]
        public async Task GetMyTodayAsync_OnShift_ReportsReminderTimeAndFinalClockOutFlag()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            Assert.True((await service.PunchAsync(CreatePunchRequest("ClockIn", MealClockInUtc))).Success);

            _timeProvider.SetUtcNow(DateTime.Parse("2026-05-17T23:00:00Z").ToUniversalTime());
            var early = (await MealScheduleAsync(service)).Meal!;
            _timeProvider.SetUtcNow(DateTime.Parse(MealFinalClockOutUtc).ToUniversalTime());
            var late = (await MealScheduleAsync(service)).Meal!;

            // 上班 08:42，连续工作满 4 小时＝12:42（UTC 02:42）提醒；此时下班还不是最后一次，不追问。
            Assert.Equal(DateTime.Parse("2026-05-18T02:42:00Z").ToUniversalTime(), early.NextReminderAtUtc);
            Assert.False(early.ClockOutWouldBeFinal);
            Assert.Equal(0, early.MissingCountIfClockOutNow);
            // 17:22 已过排班结束：最后一次下班，工作 8 小时 40 分、没休息记录 → 缺 1 次。
            Assert.True(late.ClockOutWouldBeFinal);
            Assert.Equal(1, late.RequiredCount);
            Assert.Equal(1, late.MissingCountIfClockOutNow);
        }

        [Fact]
        public async Task PunchAsync_FinalClockOutDeclaringNotTaken_CreatesPendingClaimAndApproval()
        {
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var clockOut = await MealRunFullDayAsync(service, new AttendanceMealDeclarationDto
            {
                NotTakenCount = 1,
                Reason = "太忙没时间",
            });

            Assert.True(clockOut.Success, clockOut.Message);
            Assert.NotNull(clockOut.Data!.MealClaim);
            Assert.Equal("Pending", clockOut.Data.MealClaim!.Status);
            Assert.Equal(1, clockOut.Data.MealClaim.MissingCount);
            Assert.Equal(1, clockOut.Data.MealClaim.NotTakenCount);
            Assert.Equal(30, clockOut.Data.MealClaim.ClaimedMinutes);
            var claim = Assert.Single(await _db.Queryable<AttendanceMealClaim>().ToListAsync());
            Assert.Equal(clockOut.Data.PunchGuid, claim.ClockOutPunchGuid);
            var approval = Assert.Single(await _db.Queryable<AttendanceApproval>()
                .Where(item => item.SourceType == "MealBreak")
                .ToListAsync());
            Assert.Equal(claim.ClaimGuid, approval.SourceGuid);
            Assert.Equal("Pending", approval.ReviewStatus);
            Assert.Equal("staff-user", approval.ApplicantUserGuid);
            // 审批前先扣、批准后才加回：待审的 30 分钟只展示，不计入计薪工时。
            var schedule = await MealScheduleAsync(service);
            Assert.Equal(520, schedule.WorkedMinutes);
            Assert.Equal(30, schedule.MealDeductionMinutes);
            Assert.Equal(30, schedule.PendingMealAddBackMinutes);
            Assert.Equal(0, schedule.ApprovedMealAddBackMinutes);
            Assert.Equal(490, schedule.PaidMinutes);
        }

        [Fact]
        public async Task PunchAsync_FinalClockOutDeclaringTaken_RecordsClaimWithoutApproval()
        {
            var clockOut = await MealRunFullDayAsync(CreateService("staff-user", "staff", "StoreStaff"), new AttendanceMealDeclarationDto { NotTakenCount = 0 });

            Assert.True(clockOut.Success, clockOut.Message);
            Assert.Equal("None", clockOut.Data!.MealClaim!.Status);
            Assert.Equal(0, clockOut.Data.MealClaim.ClaimedMinutes);
            Assert.Empty(await _db.Queryable<AttendanceApproval>().Where(item => item.SourceType == "MealBreak").ToListAsync());
        }

        [Fact]
        public async Task PunchAsync_FinalClockOutWithoutDeclaration_BehavesLikeOldApp()
        {
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var clockOut = await MealRunFullDayAsync(service, declaration: null);

            Assert.True(clockOut.Success, clockOut.Message);
            Assert.Null(clockOut.Data!.MealClaim);
            Assert.Empty(await _db.Queryable<AttendanceMealClaim>().ToListAsync());
            Assert.Empty(await _db.Queryable<AttendanceApproval>().Where(item => item.SourceType == "MealBreak").ToListAsync());
            // 旧 App 不带声明：按「已休息」处理，工时仍按排班扣除用餐。
            Assert.Equal(490, (await MealScheduleAsync(service)).PaidMinutes);
        }

        [Fact]
        public async Task PunchAsync_FinalClockOutWithRecordedBreak_IgnoresDeclaration()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            Assert.True((await service.PunchAsync(CreatePunchRequest("ClockIn", MealClockInUtc))).Success);
            _timeProvider.SetUtcNow(DateTime.Parse(MealBreakStartUtc).ToUniversalTime());
            Assert.True((await service.StartMyMealBreakAsync(new AttendanceMealBreakRequestDto { StoreCode = "BRI" })).Success);
            _timeProvider.SetUtcNow(DateTime.Parse(MealBreakEndUtc).ToUniversalTime());
            Assert.True((await service.EndMyMealBreakAsync(new AttendanceMealBreakRequestDto { StoreCode = "BRI" })).Success);

            var request = CreatePunchRequest("ClockOut", MealFinalClockOutUtc);
            request.MealDeclaration = new AttendanceMealDeclarationDto { NotTakenCount = 1 };
            var clockOut = await service.PunchAsync(request);

            Assert.True(clockOut.Success, clockOut.Message);
            Assert.Null(clockOut.Data!.MealClaim); // 已有休息记录，服务端重算缺口为 0，客户端声明不生效
            Assert.Empty(await _db.Queryable<AttendanceMealClaim>().ToListAsync());
        }

        [Fact]
        public async Task PunchAsync_MiddleClockOutOfTwoSegments_DoesNotCheckMeal()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            Assert.True((await service.PunchAsync(CreatePunchRequest("ClockIn", MealClockInUtc))).Success);

            // 08:42–12:00 先下班（班段中间的下班），工作 3 小时 18 分，不到 4 小时；带声明也不检查。
            var request = CreatePunchRequest("ClockOut", "2026-05-18T02:00:00Z");
            request.MealDeclaration = new AttendanceMealDeclarationDto { NotTakenCount = 1 };
            var middleOut = await service.PunchAsync(request);

            Assert.True(middleOut.Success, middleOut.Message);
            Assert.True(middleOut.Data!.IsBreakBoundary);
            Assert.Null(middleOut.Data.MealClaim);
            Assert.Empty(await _db.Queryable<AttendanceMealClaim>().ToListAsync());
        }

        [Fact]
        public async Task PunchAsync_DeclaredNotTakenAboveMissing_IsClampedToServerGap()
        {
            var clockOut = await MealRunFullDayAsync(CreateService("staff-user", "staff", "StoreStaff"), new AttendanceMealDeclarationDto { NotTakenCount = 5 });

            Assert.True(clockOut.Success, clockOut.Message);
            Assert.Equal(1, clockOut.Data!.MealClaim!.NotTakenCount);
            Assert.Equal(30, clockOut.Data.MealClaim.ClaimedMinutes);
        }

        [Fact]
        public async Task PunchAsync_ScheduleWithoutMeal_NeverCreatesClaim()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await SetScheduleMealBreakCountAsync("schedule-1", 0); // 店长取消用餐
            var service = CreateService("staff-user", "staff", "StoreStaff");
            Assert.True((await service.PunchAsync(CreatePunchRequest("ClockIn", MealClockInUtc))).Success);

            var request = CreatePunchRequest("ClockOut", MealFinalClockOutUtc);
            request.MealDeclaration = new AttendanceMealDeclarationDto { NotTakenCount = 1 };
            var clockOut = await service.PunchAsync(request);

            Assert.True(clockOut.Success, clockOut.Message);
            Assert.Null(clockOut.Data!.MealClaim);
            var schedule = await MealScheduleAsync(service);
            Assert.Equal(0, schedule.EffectiveMealBreakCount);
            Assert.Equal(0, schedule.MealDeductionMinutes);
            Assert.Equal(520, schedule.PaidMinutes);
        }

        [Fact]
        public async Task PunchAsync_ClockOutDuringOpenBreak_EndsTheBreakAtPunchTime()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            Assert.True((await service.PunchAsync(CreatePunchRequest("ClockIn", MealClockInUtc))).Success);
            _timeProvider.SetUtcNow(DateTime.Parse(MealBreakStartUtc).ToUniversalTime());
            Assert.True((await service.StartMyMealBreakAsync(new AttendanceMealBreakRequestDto { StoreCode = "BRI" })).Success);

            var clockOut = await service.PunchAsync(CreatePunchRequest("ClockOut", MealFinalClockOutUtc));

            Assert.True(clockOut.Success, clockOut.Message);
            var recorded = Assert.Single(await _db.Queryable<AttendanceMealBreak>().ToListAsync());
            Assert.Equal(clockOut.Data!.PunchTimeUtc, recorded.EndUtc);
        }

        [Fact]
        public async Task PunchAsync_ReplayedFinalClockOutToken_DoesNotDuplicateClaim()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            Assert.True((await service.PunchAsync(CreatePunchRequest("ClockIn", MealClockInUtc))).Success);
            var request = CreatePunchRequest("ClockOut", MealFinalClockOutUtc);
            request.MealDeclaration = new AttendanceMealDeclarationDto { NotTakenCount = 1 };

            var first = await service.PunchAsync(request);
            var replay = await service.PunchAsync(request);

            Assert.True(first.Success, first.Message);
            Assert.True(replay.Success, replay.Message);
            Assert.Equal(first.Data!.PunchGuid, replay.Data!.PunchGuid);
            Assert.Single(await _db.Queryable<AttendanceMealClaim>().ToListAsync());
            Assert.Single(await _db.Queryable<AttendanceApproval>().Where(item => item.SourceType == "MealBreak").ToListAsync());
        }

        [Fact]
        public async Task ApproveAsync_MealClaim_AddsApprovedMinutesBackToPaidMinutes()
        {
            var clockOut = await MealRunFullDayAsync(
                CreateService("staff-user", "staff", "StoreStaff"),
                new AttendanceMealDeclarationDto { NotTakenCount = 1 });
            Assert.True(clockOut.Success, clockOut.Message);
            var approval = await _db.Queryable<AttendanceApproval>().FirstAsync(item => item.SourceType == "MealBreak");

            var approved = await CreateService("manager-user", "manager", "StoreManager")
                .ApproveAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto { ReviewRemark = "确实太忙" });

            Assert.True(approved.Success, $"{approved.ErrorCode}: {approved.Message}");
            var claim = await _db.Queryable<AttendanceMealClaim>().FirstAsync();
            Assert.Equal("Approved", claim.Status);
            Assert.Equal(30, claim.ApprovedMinutes);
            Assert.NotNull(claim.ReviewedAtUtc);
            // HttpContextAccessor 的当前用户是进程内共享的 AsyncLocal：创建过店长服务后，员工视角要重新创建服务。
            var schedule = await MealScheduleAsync(CreateService("staff-user", "staff", "StoreStaff"));
            Assert.Equal(30, schedule.ApprovedMealAddBackMinutes);
            Assert.Equal(0, schedule.PendingMealAddBackMinutes);
            Assert.Equal(520, schedule.PaidMinutes); // 520 − 30 + 30
        }

        [Fact]
        public async Task ApprovedMealAddBack_IsCappedByCurrentDeduction_WhenScheduleMealCountLowered()
        {
            var clockOut = await MealRunFullDayAsync(
                CreateService("staff-user", "staff", "StoreStaff"),
                new AttendanceMealDeclarationDto { NotTakenCount = 1 });
            Assert.True(clockOut.Success, clockOut.Message);
            var approval = await _db.Queryable<AttendanceApproval>().FirstAsync(item => item.SourceType == "MealBreak");
            var approved = await CreateService("manager-user", "manager", "StoreManager")
                .ApproveAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto { ReviewRemark = "同意" });
            Assert.True(approved.Success, $"{approved.ErrorCode}: {approved.Message}");

            // 批准后店长把这天的用餐改成 0：不再扣除，也就没有可加回的分钟，计薪工时不能超过实际工时。
            await SetScheduleMealBreakCountAsync("schedule-1", 0);
            var schedule = await MealScheduleAsync(CreateService("staff-user", "staff", "StoreStaff"));

            Assert.Equal(0, schedule.MealDeductionMinutes);
            Assert.Equal(0, schedule.ApprovedMealAddBackMinutes);
            Assert.Equal(520, schedule.PaidMinutes);
        }

        [Fact]
        public async Task RejectAsync_MealClaim_RequiresRemark_AndKeepsDeduction()
        {
            var clockOut = await MealRunFullDayAsync(
                CreateService("staff-user", "staff", "StoreStaff"),
                new AttendanceMealDeclarationDto { NotTakenCount = 1 });
            Assert.True(clockOut.Success, clockOut.Message);
            var approval = await _db.Queryable<AttendanceApproval>().FirstAsync(item => item.SourceType == "MealBreak");
            var manager = CreateService("manager-user", "manager", "StoreManager");

            var withoutRemark = await manager.RejectAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto());
            var rejected = await manager.RejectAsync(
                approval.ApprovalGuid,
                new ReviewAttendanceApprovalDto { ReviewRemark = "当天有轮休" });

            Assert.False(withoutRemark.Success);
            Assert.Equal("REVIEW_REMARK_REQUIRED", withoutRemark.ErrorCode);
            Assert.True(rejected.Success, $"{rejected.ErrorCode}: {rejected.Message}");
            Assert.Equal("Rejected", (await _db.Queryable<AttendanceMealClaim>().FirstAsync()).Status);
            var schedule = await MealScheduleAsync(CreateService("staff-user", "staff", "StoreStaff"));
            Assert.Equal(0, schedule.ApprovedMealAddBackMinutes);
            Assert.Equal(490, schedule.PaidMinutes);
        }

        [Fact]
        public async Task ApproveAsync_MealClaim_WhenScheduleCancelled_CancelsClaimAndApproval()
        {
            var clockOut = await MealRunFullDayAsync(CreateService("staff-user", "staff", "StoreStaff"), new AttendanceMealDeclarationDto { NotTakenCount = 1 });
            Assert.True(clockOut.Success, clockOut.Message);
            var approval = await _db.Queryable<AttendanceApproval>().FirstAsync(item => item.SourceType == "MealBreak");
            await _db.Updateable<AttendanceSchedule>()
                .SetColumns(item => item.Status == "Cancelled")
                .Where(item => item.ScheduleGuid == "schedule-1")
                .ExecuteCommandAsync();

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .ApproveAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto { ReviewRemark = "同意" });

            Assert.False(result.Success);
            Assert.Equal("MEAL_CLAIM_STALE", result.ErrorCode);
            Assert.Equal("Cancelled", (await _db.Queryable<AttendanceMealClaim>().FirstAsync()).Status);
            Assert.Equal("Cancelled", (await _db.Queryable<AttendanceApproval>()
                .FirstAsync(item => item.ApprovalGuid == approval.ApprovalGuid)).ReviewStatus);
        }

        [Fact]
        public async Task ApproveAsync_MealClaim_ByApplicant_IsForbidden()
        {
            var staff = CreateService("staff-user", "staff", "StoreStaff");
            var clockOut = await MealRunFullDayAsync(staff, new AttendanceMealDeclarationDto { NotTakenCount = 1 });
            Assert.True(clockOut.Success, clockOut.Message);
            var approval = await _db.Queryable<AttendanceApproval>().FirstAsync(item => item.SourceType == "MealBreak");

            var result = await staff.ApproveAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto { ReviewRemark = "我批我自己" });

            Assert.False(result.Success);
            Assert.Equal("Pending", (await _db.Queryable<AttendanceMealClaim>().FirstAsync()).Status);
        }

        [Fact]
        public async Task GetPendingApprovalsAsync_MealClaim_ShowsTitleDetailAndClaim()
        {
            var clockOut = await MealRunFullDayAsync(CreateService("staff-user", "staff", "StoreStaff"), new AttendanceMealDeclarationDto
            {
                NotTakenCount = 1,
                Reason = "太忙没时间",
            });
            Assert.True(clockOut.Success, clockOut.Message);

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .GetPendingApprovalsAsync(new AttendanceApprovalQueryDto { StoreCode = "BRI" });

            Assert.True(result.Success, result.Message);
            var item = Assert.Single(result.Data!, row => row.SourceType == "MealBreak");
            Assert.Equal("用餐未休息加工时", item.Title);
            Assert.Equal(new DateTime(2026, 5, 18), item.WorkDate);
            Assert.Equal(30, item.MealClaim!.ClaimedMinutes);
            Assert.Contains("申请加回 30 分钟", item.Detail);
            Assert.Contains("太忙没时间", item.Detail);
        }

        [Fact]
        public async Task GetMyTodayAsync_ScheduleDto_ExposesEffectiveMealBreakCount()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await SetScheduleMealBreakCountAsync("schedule-1", 2);
            _timeProvider.SetUtcNow(DateTime.Parse("2026-05-17T23:00:00Z").ToUniversalTime());

            var schedule = await MealScheduleAsync(CreateService("staff-user", "staff", "StoreStaff"));

            Assert.Equal(2, schedule.MealBreakCount);
            Assert.Equal(2, schedule.EffectiveMealBreakCount);
        }

        /// <summary>
        /// 跑完一整天：08:42 上班、17:22 最后一次下班（可带用餐声明），返回下班结果。
        /// 服务必须由测试方法自己创建再传入：HttpContextAccessor 的当前用户存在 AsyncLocal 里，
        /// 在 async 辅助方法内创建的服务，返回到调用方后读不到当前用户。
        /// </summary>
        private async Task<ApiResponse<AttendancePunchDto>> MealRunFullDayAsync(
            AttendanceReactService service,
            AttendanceMealDeclarationDto? declaration)
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            var clockIn = await service.PunchAsync(CreatePunchRequest("ClockIn", MealClockInUtc));
            Assert.True(clockIn.Success, clockIn.Message);
            var request = CreatePunchRequest("ClockOut", MealFinalClockOutUtc);
            request.MealDeclaration = declaration;
            return await service.PunchAsync(request);
        }

        private static async Task<AttendanceScheduleDto> MealScheduleAsync(AttendanceReactService service)
        {
            var today = await service.GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI");
            Assert.True(today.Success, today.Message);
            return Assert.Single(today.Data!.Schedules);
        }
    }
}
