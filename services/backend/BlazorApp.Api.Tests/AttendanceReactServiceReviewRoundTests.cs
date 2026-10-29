using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Api.Services.Attendance;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Xunit;

namespace BlazorApp.Api.Tests
{
    /// <summary>
    /// 考勤审核口径第一轮（规格 docs/plans/2026-10-21-attendance-payroll-leave.md 第 0.1 节 + 审查确认的缺陷）。
    /// 夹具共用 AttendanceReactServiceTests：BRI 门店按 Brisbane（UTC+10、无夏令时）算本地时间。
    /// </summary>
    public sealed partial class AttendanceReactServiceTests
    {
        private static readonly DateTime ReviewRoundWorkDate = new(2026, 5, 18);

        // ---------------------------------------------------------------------------------
        // 缺陷 1：段间满 30 分钟的空档不重复扣餐
        // ---------------------------------------------------------------------------------

        [Fact]
        public async Task 段间满30分钟空档_不再重复扣餐_计薪等于实际工时()
        {
            await SeedStoreScopeAsync();
            // 排班 9–18（9 小时，默认 1 次用餐）；打卡 9–13、13:30–18，空档 30 分钟本身就是那顿饭。
            await SeedScheduleAsync("gap-schedule", "BRI", "staff-user", ReviewRoundWorkDate, "Active", new TimeSpan(9, 0, 0), new TimeSpan(18, 0, 0));
            await _db.Insertable(new[]
            {
                CreateStoredPunchForSchedule("gap-in-1", "gap-schedule", "BRI", "staff-user", "ClockIn", ReviewRoundWorkDate.AddHours(9)),
                CreateStoredPunchForSchedule("gap-out-1", "gap-schedule", "BRI", "staff-user", "ClockOut", ReviewRoundWorkDate.AddHours(13)),
                CreateStoredPunchForSchedule("gap-in-2", "gap-schedule", "BRI", "staff-user", "ClockIn", ReviewRoundWorkDate.AddHours(13.5)),
                CreateStoredPunchForSchedule("gap-out-2", "gap-schedule", "BRI", "staff-user", "ClockOut", ReviewRoundWorkDate.AddHours(18)),
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 9, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .GetAttendanceRecordsAsync(new AttendanceScheduleQueryDto { StoreCode = "BRI", Page = 1, PageSize = 20 });

            Assert.True(result.Success, result.Message);
            var schedule = Assert.Single(result.Data!.Items!);
            Assert.Equal(510, schedule.WorkedMinutes);
            Assert.Equal(0, schedule.MealDeductionMinutes);
            Assert.Equal(schedule.WorkedMinutes, schedule.PaidMinutes);
        }

        [Theory]
        [InlineData(1, 510, 0, 30)]
        [InlineData(1, 510, 1, 0)]
        [InlineData(2, 540, 1, 30)]
        [InlineData(1, 200, 1, 0)]
        public void DeductionMinutes_扣除次数减去合格空档_下限为零(
            int effectiveCount,
            int workedMinutes,
            int gapCount,
            int expected)
        {
            Assert.Equal(expected, AttendanceMealBreakRules.DeductionMinutes(effectiveCount, workedMinutes, gapCount));
        }

        // ---------------------------------------------------------------------------------
        // 缺陷 2：漏下班批准 / 拒绝后不再重开
        // ---------------------------------------------------------------------------------

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task 漏下班已批准或已拒绝_再次对账不重开待审(bool approve)
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await _db.Insertable(CreateStoredPunch("missing-in", "ClockIn", ReviewRoundWorkDate.AddHours(9)))
                .ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 9, 0, 0, DateTimeKind.Utc));
            var staff = CreateService("staff-user", "staff", "StoreStaff");
            Assert.True((await staff.GetMyTodayAsync(ReviewRoundWorkDate, "BRI")).Success);
            var pending = await _db.Queryable<AttendanceApproval>()
                .SingleAsync(item => item.SourceType == "MissingClockOut" && item.ReviewStatus == "Pending");

            var manager = CreateService("manager-user", "manager", "StoreManager");
            var review = new ReviewAttendanceApprovalDto { ReviewRemark = "已与员工核对" };
            var reviewed = approve
                ? await manager.ApproveAsync(pending.ApprovalGuid, review)
                : await manager.RejectAsync(pending.ApprovalGuid, review);
            Assert.True(reviewed.Success, $"{reviewed.ErrorCode}: {reviewed.Message}");

            // 员工刷新今日、店长打开审核列表都会对账；漏下班状态仍在，但已结案就不能再生成待审。
            Assert.True((await staff.GetMyTodayAsync(ReviewRoundWorkDate, "BRI")).Success);
            Assert.True((await manager.GetPendingApprovalsAsync(new AttendanceApprovalQueryDto { StoreCode = "BRI" })).Success);

            var rows = await _db.Queryable<AttendanceApproval>()
                .Where(item => item.SourceType == "MissingClockOut")
                .ToListAsync();
            var row = Assert.Single(rows);
            Assert.Equal(approve ? "Approved" : "Rejected", row.ReviewStatus);
        }

        // ---------------------------------------------------------------------------------
        // 缺陷 3：Punch 审批被系统取消后，异常仍在时可以重新生成
        // ---------------------------------------------------------------------------------

        [Fact]
        public async Task 打卡异常待审被系统取消后_异常仍在时重新生成()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            // 9:30 上班，超出 5 分钟宽限 → 迟到需审批。
            await _db.Insertable(CreateStoredPunch("late-in", "ClockIn", ReviewRoundWorkDate.AddHours(9.5)))
                .ExecuteCommandAsync();
            await _db.Insertable(new AttendanceApproval
            {
                ApprovalGuid = "late-cancelled",
                SourceType = "Punch",
                SourceGuid = "late-in",
                StoreCode = "BRI",
                ApplicantUserGuid = "staff-user",
                ReviewStatus = "Cancelled",
                ReviewerUserGuid = "system",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 2, 0, 0, DateTimeKind.Utc));

            var today = await CreateService("staff-user", "staff", "StoreStaff")
                .GetMyTodayAsync(ReviewRoundWorkDate, "BRI");

            Assert.True(today.Success, today.Message);
            var rows = await _db.Queryable<AttendanceApproval>()
                .Where(item => item.SourceType == "Punch" && item.SourceGuid == "late-in")
                .ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Single(rows, item => item.ReviewStatus == "Pending");
        }

        [Fact]
        public async Task 打卡异常已批准_再次对账不重开()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await _db.Insertable(CreateStoredPunch("late-in", "ClockIn", ReviewRoundWorkDate.AddHours(9.5)))
                .ExecuteCommandAsync();
            await _db.Insertable(new AttendanceApproval
            {
                ApprovalGuid = "late-approved",
                SourceType = "Punch",
                SourceGuid = "late-in",
                StoreCode = "BRI",
                ApplicantUserGuid = "staff-user",
                ReviewStatus = "Approved",
                ReviewerUserGuid = "manager-user",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 2, 0, 0, DateTimeKind.Utc));

            Assert.True((await CreateService("staff-user", "staff", "StoreStaff")
                .GetMyTodayAsync(ReviewRoundWorkDate, "BRI")).Success);

            Assert.Equal(1, await _db.Queryable<AttendanceApproval>()
                .CountAsync(item => item.SourceType == "Punch" && item.SourceGuid == "late-in"));
        }

        // ---------------------------------------------------------------------------------
        // 缺陷 4：只能撤销待审核的请假
        // ---------------------------------------------------------------------------------

        [Fact]
        public async Task CancelMyLeaveRequestAsync_已批准的请假_不能撤销()
        {
            await SeedStoreScopeAsync();
            await _db.Insertable(new AttendanceLeaveRequest
            {
                LeaveGuid = "approved-leave",
                StoreCode = "BRI",
                UserGuid = "staff-user",
                LeaveType = "AnnualLeave",
                StartDate = ReviewRoundWorkDate,
                EndDate = ReviewRoundWorkDate,
                Status = "Approved",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .CancelMyLeaveRequestAsync("approved-leave");

            Assert.False(result.Success);
            Assert.Equal("LEAVE_NOT_PENDING", result.ErrorCode);
            var leave = await _db.Queryable<AttendanceLeaveRequest>().SingleAsync(item => item.LeaveGuid == "approved-leave");
            Assert.Equal("Approved", leave.Status);
            Assert.False(leave.IsDeleted);
        }

        [Fact]
        public async Task CancelMyLeaveRequestAsync_待审请假_撤销并取消审批()
        {
            await SeedStoreScopeAsync();
            var created = await CreateService("staff-user", "staff", "StoreStaff")
                .CreateMyLeaveRequestAsync(new CreateAttendanceLeaveRequestDto
                {
                    StoreCode = "BRI",
                    LeaveType = "AnnualLeave",
                    StartDate = new DateTime(2026, 10, 20),
                    EndDate = new DateTime(2026, 10, 21),
                });
            Assert.True(created.Success, created.Message);

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .CancelMyLeaveRequestAsync(created.Data!.LeaveGuid);

            Assert.True(result.Success, result.Message);
            var leave = await _db.Queryable<AttendanceLeaveRequest>().SingleAsync();
            Assert.Equal("Cancelled", leave.Status);
            Assert.Equal("Cancelled", (await _db.Queryable<AttendanceApproval>().SingleAsync()).ReviewStatus);
        }

        // ---------------------------------------------------------------------------------
        // 缺陷 5：本人请假的校验
        // ---------------------------------------------------------------------------------

        [Theory]
        [InlineData("", "AnnualLeave", "2026-10-20", "2026-10-21", null, "STORE_REQUIRED")]
        [InlineData("BRI", "AnnualLeave", "2026-10-22", "2026-10-21", null, "INVALID_DATE_RANGE")]
        [InlineData("BRI", "PersonalLeave", "2026-10-20", "2026-10-21", null, "INVALID_LEAVE_TYPE")]
        [InlineData("BRI", "SickLeave", "2026-10-20", "2026-10-20", "https://evil.example.com/a.jpg", "INVALID_ATTACHMENT_URL")]
        public async Task CreateMyLeaveRequestAsync_非法输入_拒绝且不落库(
            string storeCode,
            string leaveType,
            string startDate,
            string endDate,
            string? attachmentUrl,
            string expectedErrorCode)
        {
            await SeedStoreScopeAsync();

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .CreateMyLeaveRequestAsync(new CreateAttendanceLeaveRequestDto
                {
                    StoreCode = storeCode,
                    LeaveType = leaveType,
                    StartDate = DateTime.Parse(startDate),
                    EndDate = DateTime.Parse(endDate),
                    AttachmentUrl = attachmentUrl,
                });

            Assert.False(result.Success);
            Assert.Equal(expectedErrorCode, result.ErrorCode);
            Assert.Equal(0, await _db.Queryable<AttendanceLeaveRequest>().CountAsync());
            Assert.Equal(0, await _db.Queryable<AttendanceApproval>().CountAsync());
        }

        [Fact]
        public async Task CreateMyLeaveRequestAsync_病假附件为考勤附件目录_通过()
        {
            await SeedStoreScopeAsync();

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .CreateMyLeaveRequestAsync(new CreateAttendanceLeaveRequestDto
                {
                    StoreCode = "BRI",
                    LeaveType = "SickLeave",
                    StartDate = new DateTime(2026, 10, 20),
                    EndDate = new DateTime(2026, 10, 20),
                    AttachmentUrl = "https://cdn.example.com/attendance/leave-attachments/20261020-cert.jpg",
                });

            Assert.True(result.Success, result.Message);
            Assert.Equal("SickLeave", result.Data!.LeaveType);
        }

        // ---------------------------------------------------------------------------------
        // 缺陷 6：补卡所有路径禁止未来时间
        // ---------------------------------------------------------------------------------

        [Fact]
        public async Task 本人补卡_当天但晚于当前时间_拒绝()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            // 当前 Brisbane 10:00；申请 17:00 下班卡（同一天，过去版本只按日期判断会放行）。
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .PreviewMyPunchAdjustmentAsync(new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI",
                    ScheduleGuid = "schedule-1",
                    PunchType = "ClockIn",
                    RequestedPunchTimeUtc = new DateTimeOffset(2026, 5, 18, 7, 0, 0, TimeSpan.Zero),
                    Reason = "提前补卡",
                });

            Assert.False(result.Success);
            Assert.Equal("PUNCH_TIME_IN_FUTURE", result.ErrorCode);
        }

        [Fact]
        public async Task Admin补卡_未来时间_同样拒绝()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("admin-schedule", "BRI", "admin-user", ReviewRoundWorkDate, "Active");
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("admin-user", "admin", "Admin")
                .PreviewMyPunchAdjustmentAsync(new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI",
                    ScheduleGuid = "admin-schedule",
                    PunchType = "ClockIn",
                    RequestedPunchTimeUtc = new DateTimeOffset(2026, 5, 18, 7, 0, 0, TimeSpan.Zero),
                    Reason = "提前补卡",
                });

            Assert.False(result.Success);
            Assert.Equal("PUNCH_TIME_IN_FUTURE", result.ErrorCode);
        }

        // ---------------------------------------------------------------------------------
        // 缺陷 7、10：可上班时间
        // ---------------------------------------------------------------------------------

        [Fact]
        public async Task CreateMyAvailabilityAsync_空分店_拒绝()
        {
            await SeedStoreScopeAsync();

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .CreateMyAvailabilityAsync(new CreateAttendanceAvailabilityDto
                {
                    StoreCode = " ",
                    WeekStartDate = ReviewRoundWorkDate,
                    Segments = new List<AttendanceAvailabilitySegmentDto>
                    {
                        new() { AvailableDate = ReviewRoundWorkDate, StartTime = new TimeSpan(9, 0, 0), EndTime = new TimeSpan(17, 0, 0) },
                    },
                });

            Assert.False(result.Success);
            Assert.Equal("STORE_REQUIRED", result.ErrorCode);
            Assert.Equal(0, await _db.Queryable<AttendanceAvailability>().CountAsync());
        }

        [Fact]
        public async Task UpdateMyAvailabilityAsync_结束不晚于开始_拒绝且不改原值()
        {
            await SeedStoreScopeAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var created = await service.CreateMyAvailabilityAsync(new CreateAttendanceAvailabilityDto
            {
                StoreCode = "BRI",
                WeekStartDate = ReviewRoundWorkDate,
                Segments = new List<AttendanceAvailabilitySegmentDto>
                {
                    new() { AvailableDate = ReviewRoundWorkDate, StartTime = new TimeSpan(9, 0, 0), EndTime = new TimeSpan(17, 0, 0) },
                },
            });
            Assert.True(created.Success, created.Message);

            var result = await service.UpdateMyAvailabilityAsync(
                created.Data!.Single().AvailabilityGuid,
                new UpdateAttendanceAvailabilityDto
                {
                    AvailableDate = ReviewRoundWorkDate,
                    StartTime = new TimeSpan(17, 0, 0),
                    EndTime = new TimeSpan(9, 0, 0),
                });

            Assert.False(result.Success);
            Assert.Equal("INVALID_TIME_RANGE", result.ErrorCode);
            var stored = await _db.Queryable<AttendanceAvailability>().SingleAsync();
            Assert.Equal(new TimeSpan(9, 0, 0), stored.StartTime);
        }

        [Fact]
        public async Task GetAvailabilityAsync_带员工名_并按日期范围过滤()
        {
            await SeedStoreScopeAsync();
            await _db.Updateable<User>()
                .SetColumns(item => item.FullName == "Amy Staff")
                .Where(item => item.UserGUID == "staff-user")
                .ExecuteCommandAsync();
            var staff = CreateService("staff-user", "staff", "StoreStaff");
            Assert.True((await staff.CreateMyAvailabilityAsync(new CreateAttendanceAvailabilityDto
            {
                StoreCode = "BRI",
                WeekStartDate = ReviewRoundWorkDate,
                Segments = new List<AttendanceAvailabilitySegmentDto>
                {
                    new() { AvailableDate = ReviewRoundWorkDate, StartTime = new TimeSpan(9, 0, 0), EndTime = new TimeSpan(12, 0, 0) },
                    new() { AvailableDate = ReviewRoundWorkDate.AddDays(3), StartTime = new TimeSpan(9, 0, 0), EndTime = new TimeSpan(12, 0, 0) },
                },
            })).Success);

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .GetAvailabilityAsync(new AttendanceAvailabilityQueryDto
                {
                    StoreCode = "BRI",
                    FromDate = ReviewRoundWorkDate.AddDays(1),
                    ToDate = ReviewRoundWorkDate.AddDays(3),
                });

            Assert.True(result.Success, result.Message);
            var item = Assert.Single(result.Data!);
            Assert.Equal(ReviewRoundWorkDate.AddDays(3), item.AvailableDate);
            Assert.Equal("Amy Staff", item.UserName);
        }

        // ---------------------------------------------------------------------------------
        // 缺陷 8：删除 / 作废排班时取消挂在它下面的待审补卡并撤回通知
        // ---------------------------------------------------------------------------------

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task 删除或作废排班_待审补卡一并取消_待办通知撤回(bool delete)
        {
            await SeedStoreScopeAsync();
            await SeedApprovalReviewerAsync("manager-user");
            UseApprovalNotifications();
            await SeedScheduleAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            var adjustment = await SubmitPunchAdjustmentAfterPreviewAsync(
                CreateService("staff-user", "staff", "StoreStaff"),
                new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI",
                    ScheduleGuid = "schedule-1",
                    PunchType = "ClockIn",
                    RequestedPunchTimeUtc = new DateTimeOffset(2026, 5, 17, 23, 0, 0, TimeSpan.Zero),
                    Reason = "忘记打卡",
                });
            Assert.True(adjustment.Success, $"{adjustment.ErrorCode}: {adjustment.Message}");
            Assert.Equal("Pending", adjustment.Data!.Status);

            var manager = CreateService("manager-user", "manager", "StoreManager");
            if (delete)
            {
                Assert.True((await manager.DeleteScheduleAsync("schedule-1")).Success);
            }
            else
            {
                var cancelled = await manager.UpdateScheduleAsync("schedule-1", new UpdateAttendanceScheduleDto
                {
                    WorkDate = ReviewRoundWorkDate,
                    StartTime = new TimeSpan(9, 0, 0),
                    EndTime = new TimeSpan(17, 0, 0),
                    Status = "Cancelled",
                });
                Assert.True(cancelled.Success, cancelled.Message);
            }

            var storedAdjustment = await _db.Queryable<AttendancePunchAdjustment>().SingleAsync();
            Assert.Equal("Cancelled", storedAdjustment.Status);
            var approval = await _db.Queryable<AttendanceApproval>()
                .SingleAsync(item => item.SourceType == "PunchAdjustment");
            Assert.Equal("Cancelled", approval.ReviewStatus);
            var submitted = Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalSubmitted));
            Assert.NotNull(submitted.RevokedAtUtc);
        }

        // ---------------------------------------------------------------------------------
        // 缺陷 9：建排班与改排班一样持员工锁
        // ---------------------------------------------------------------------------------

        [Fact]
        public void CreateScheduleAsync_重叠检查与写入在员工锁和事务内()
        {
            var source = File.ReadAllText(Path.Combine(
                FindRepoRoot(),
                "services/backend/BlazorApp.Api/Services/React/AttendanceReactService.cs"));
            var methodStart = source.IndexOf("public async Task<ApiResponse<AttendanceScheduleDto>> CreateScheduleAsync(", StringComparison.Ordinal);
            var methodEnd = source.IndexOf("public async Task<ApiResponse<CopyAttendanceScheduleWeekResultDto>> CopyScheduleWeekAsync(", methodStart, StringComparison.Ordinal);
            Assert.True(methodStart >= 0 && methodEnd > methodStart);
            var method = source[methodStart..methodEnd];

            var processLock = method.IndexOf("AttendanceDailyMutationLock.AcquireProcessAsync", StringComparison.Ordinal);
            var databaseLock = method.IndexOf("AttendanceDailyMutationLock.AcquireDatabaseAsync", StringComparison.Ordinal);
            var overlap = method.IndexOf("HasOverlappingScheduleAsync", StringComparison.Ordinal);
            var insert = method.IndexOf("_db.Insertable(model)", StringComparison.Ordinal);
            Assert.True(processLock >= 0 && processLock < databaseLock && databaseLock < overlap && overlap < insert);
            Assert.Contains("BeginTranAsync", method);
        }

        [Fact]
        public async Task CreateScheduleAsync_同员工同日重叠_仍被拒绝()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            var service = CreateService("manager-user", "manager", "StoreManager");

            var result = await service.CreateScheduleAsync(new CreateAttendanceScheduleDto
            {
                StoreCode = "BRI",
                UserGuid = "staff-user",
                WorkDate = ReviewRoundWorkDate,
                StartTime = new TimeSpan(12, 0, 0),
                EndTime = new TimeSpan(18, 0, 0),
            });

            Assert.False(result.Success);
            Assert.Equal("SCHEDULE_OVERLAP", result.ErrorCode);
            Assert.Equal(1, await _db.Queryable<AttendanceSchedule>().CountAsync());
        }

        // ---------------------------------------------------------------------------------
        // 口径①：Attendance.Approval.ReviewSelf
        // ---------------------------------------------------------------------------------

        [Fact]
        public async Task 持有审核本人权限_可审自己的请假_标记本人审批且可筛选()
        {
            await SeedStoreScopeAsync();
            var manager = CreateService("manager-user", "manager", "StoreManager");
            var leave = await manager.CreateMyLeaveRequestAsync(new CreateAttendanceLeaveRequestDto
            {
                StoreCode = "BRI",
                LeaveType = "AnnualLeave",
                StartDate = new DateTime(2026, 10, 20),
                EndDate = new DateTime(2026, 10, 20),
            });
            Assert.True(leave.Success, leave.Message);
            var approval = await _db.Queryable<AttendanceApproval>().SingleAsync();

            var reviewed = await manager.ApproveAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto());

            Assert.True(reviewed.Success, $"{reviewed.ErrorCode}: {reviewed.Message}");
            Assert.True(reviewed.Data!.IsSelfReview);
            Assert.True((await _db.Queryable<AttendanceApproval>().SingleAsync()).IsSelfReview);
            var selfOnly = await manager.GetApprovalsAsync(new AttendanceApprovalQueryDto { StoreCode = "BRI", IsSelfReview = true });
            Assert.Equal(approval.ApprovalGuid, Assert.Single(selfOnly.Data!).ApprovalGuid);
            var others = await manager.GetApprovalsAsync(new AttendanceApprovalQueryDto { StoreCode = "BRI", IsSelfReview = false });
            Assert.Empty(others.Data!);
        }

        [Fact]
        public async Task 没有审核本人权限_审自己的请假_拒绝()
        {
            await SeedStoreScopeAsync();
            _deniedPermissions.Add(Permissions.Attendance.Approval.ReviewSelf);
            var manager = CreateService("manager-user", "manager", "StoreManager");
            Assert.True((await manager.CreateMyLeaveRequestAsync(new CreateAttendanceLeaveRequestDto
            {
                StoreCode = "BRI",
                LeaveType = "AnnualLeave",
                StartDate = new DateTime(2026, 10, 20),
                EndDate = new DateTime(2026, 10, 20),
            })).Success);
            var approval = await _db.Queryable<AttendanceApproval>().SingleAsync();

            var reviewed = await manager.ApproveAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto());

            Assert.False(reviewed.Success);
            Assert.Equal("SELF_REVIEW_FORBIDDEN", reviewed.ErrorCode);
            Assert.Equal("Pending", (await _db.Queryable<AttendanceApproval>().SingleAsync()).ReviewStatus);
        }

        [Fact]
        public async Task 审核他人申请_不标记本人审批()
        {
            await SeedStoreScopeAsync();
            Assert.True((await CreateService("staff-user", "staff", "StoreStaff").CreateMyLeaveRequestAsync(new CreateAttendanceLeaveRequestDto
            {
                StoreCode = "BRI",
                LeaveType = "AnnualLeave",
                StartDate = new DateTime(2026, 10, 20),
                EndDate = new DateTime(2026, 10, 20),
            })).Success);
            var approval = await _db.Queryable<AttendanceApproval>().SingleAsync();

            var reviewed = await CreateService("manager-user", "manager", "StoreManager")
                .ApproveAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto());

            Assert.True(reviewed.Success, reviewed.Message);
            Assert.False(reviewed.Data!.IsSelfReview);
        }

        // ---------------------------------------------------------------------------------
        // 口径②：排班软删除与恢复
        // ---------------------------------------------------------------------------------

        [Fact]
        public async Task 删除有打卡的排班_出现在已删除列表_恢复后重新对账生成漏下班()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await _db.Insertable(CreateStoredPunch("restore-in", "ClockIn", ReviewRoundWorkDate.AddHours(9)))
                .ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 9, 0, 0, DateTimeKind.Utc));
            Assert.True((await CreateService("staff-user", "staff", "StoreStaff").GetMyTodayAsync(ReviewRoundWorkDate, "BRI")).Success);
            var manager = CreateService("manager-user", "manager", "StoreManager");

            Assert.True((await manager.DeleteScheduleAsync("schedule-1")).Success);
            var afterDelete = await _db.Queryable<AttendanceApproval>()
                .SingleAsync(item => item.SourceType == "MissingClockOut");
            Assert.Equal("Cancelled", afterDelete.ReviewStatus);

            var deleted = await manager.GetDeletedSchedulesAsync(new AttendanceScheduleQueryDto { StoreCode = "BRI" });
            Assert.True(deleted.Success, deleted.Message);
            var deletedItem = Assert.Single(deleted.Data!);
            Assert.Equal("schedule-1", deletedItem.ScheduleGuid);
            Assert.NotNull(deletedItem.DeletedAt);
            Assert.Equal("manager", deletedItem.DeletedBy);
            Assert.Empty((await manager.GetSchedulesAsync(new AttendanceScheduleQueryDto { StoreCode = "BRI" })).Data!);

            var restored = await manager.RestoreScheduleAsync("schedule-1");

            Assert.True(restored.Success, $"{restored.ErrorCode}: {restored.Message}");
            Assert.Equal("Active", restored.Data!.Status);
            var stored = await _db.Queryable<AttendanceSchedule>().SingleAsync();
            Assert.False(stored.IsDeleted);
            Assert.Null(stored.DeletedAt);
            // 系统取消的漏下班不算结案，恢复后按当前打卡重新生成待处理。
            Assert.Single(
                await _db.Queryable<AttendanceApproval>()
                    .Where(item => item.SourceType == "MissingClockOut" && item.ReviewStatus == "Pending")
                    .ToListAsync());
            Assert.Empty((await manager.GetDeletedSchedulesAsync(new AttendanceScheduleQueryDto { StoreCode = "BRI" })).Data!);
        }

        [Fact]
        public async Task 恢复没有打卡的排班_回到草稿()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            var manager = CreateService("manager-user", "manager", "StoreManager");
            Assert.True((await manager.DeleteScheduleAsync("schedule-1")).Success);

            var restored = await manager.RestoreScheduleAsync("schedule-1");

            Assert.True(restored.Success, restored.Message);
            Assert.Equal("Draft", restored.Data!.Status);
        }

        [Fact]
        public async Task 恢复排班_同时段已有新排班_拒绝()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            var manager = CreateService("manager-user", "manager", "StoreManager");
            Assert.True((await manager.DeleteScheduleAsync("schedule-1")).Success);
            Assert.True((await manager.CreateScheduleAsync(new CreateAttendanceScheduleDto
            {
                StoreCode = "BRI",
                UserGuid = "staff-user",
                WorkDate = ReviewRoundWorkDate,
                StartTime = new TimeSpan(10, 0, 0),
                EndTime = new TimeSpan(16, 0, 0),
            })).Success);

            var restored = await manager.RestoreScheduleAsync("schedule-1");

            Assert.False(restored.Success);
            Assert.Equal("SCHEDULE_OVERLAP", restored.ErrorCode);
            Assert.True((await _db.Queryable<AttendanceSchedule>().SingleAsync(item => item.ScheduleGuid == "schedule-1")).IsDeleted);
        }

        [Fact]
        public async Task 恢复其他分店的已删除排班_拒绝()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("other-schedule", "OTHER", "staff-user", ReviewRoundWorkDate, "Active");
            await _db.Updateable<AttendanceSchedule>()
                .SetColumns(item => item.IsDeleted == true)
                .Where(item => item.ScheduleGuid == "other-schedule")
                .ExecuteCommandAsync();

            var restored = await CreateService("manager-user", "manager", "StoreManager")
                .RestoreScheduleAsync("other-schedule");

            Assert.False(restored.Success);
            Assert.Equal("FORBIDDEN_STORE", restored.ErrorCode);
        }

        // ---------------------------------------------------------------------------------
        // 口径③：店长打开审核列表时对近 14 天排班对账
        // ---------------------------------------------------------------------------------

        [Fact]
        public async Task GetPendingApprovalsAsync_员工没打开今日_也生成漏下班与加班()
        {
            await SeedStoreScopeAsync();
            // 两天前漏下班；昨天完成且迟走 30 分钟（候选加班）。员工都没打开过 App。
            await SeedScheduleAsync("missing", "BRI", "staff-user", ReviewRoundWorkDate.AddDays(-2), "Active");
            await SeedScheduleAsync("overtime", "BRI", "staff-user", ReviewRoundWorkDate.AddDays(-1), "Active");
            await _db.Insertable(new[]
            {
                CreateStoredPunchForSchedule("missing-in", "missing", "BRI", "staff-user", "ClockIn", ReviewRoundWorkDate.AddDays(-2).AddHours(9)),
                CreateStoredPunchForSchedule("ot-in", "overtime", "BRI", "staff-user", "ClockIn", ReviewRoundWorkDate.AddDays(-1).AddHours(9)),
                CreateStoredPunchForSchedule("ot-out", "overtime", "BRI", "staff-user", "ClockOut", ReviewRoundWorkDate.AddDays(-1).AddHours(17.5)),
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc));

            var pending = await CreateService("manager-user", "manager", "StoreManager")
                .GetPendingApprovalsAsync(new AttendanceApprovalQueryDto { StoreCode = "BRI" });

            Assert.True(pending.Success, pending.Message);
            Assert.Contains(pending.Data!, item => item.SourceType == "MissingClockOut" && item.SourceGuid == "missing");
            var overtime = Assert.Single(pending.Data!, item => item.SourceType == "Overtime");
            Assert.Equal(30, overtime.CandidateOvertimeMinutes);
        }

        [Fact]
        public async Task GetPendingApprovalsAsync_超过14天的排班_不对账()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("old", "BRI", "staff-user", ReviewRoundWorkDate.AddDays(-14), "Active");
            await _db.Insertable(CreateStoredPunchForSchedule("old-in", "old", "BRI", "staff-user", "ClockIn", ReviewRoundWorkDate.AddDays(-14).AddHours(9)))
                .ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc));

            var pending = await CreateService("manager-user", "manager", "StoreManager")
                .GetPendingApprovalsAsync(new AttendanceApprovalQueryDto { StoreCode = "BRI" });

            Assert.True(pending.Success, pending.Message);
            Assert.Empty(pending.Data!);
        }

        [Fact]
        public void GetPendingApprovalsAsync_对账失败只记日志不阻断列表()
        {
            var source = File.ReadAllText(Path.Combine(
                FindRepoRoot(),
                "services/backend/BlazorApp.Api/Services/React/AttendanceReactService.cs"));
            var methodStart = source.IndexOf("private async Task ReconcileRecentStoreSchedulesForReviewAsync(", StringComparison.Ordinal);
            var methodEnd = source.IndexOf("public Task<ApiResponse<AttendanceApprovalDto>> ApproveAsync(", methodStart, StringComparison.Ordinal);
            Assert.True(methodStart >= 0 && methodEnd > methodStart);
            var method = source[methodStart..methodEnd];
            Assert.Contains("catch (Exception exception)", method);
            Assert.Contains("_logger.LogWarning", method);
            Assert.DoesNotContain("throw;", method);
        }

        // ---------------------------------------------------------------------------------
        // 口径④：ExpectedPunchType
        // ---------------------------------------------------------------------------------

        [Fact]
        public async Task PunchAsync_期望类型与服务端判定不一致_返回PUNCH_TYPE_MISMATCH且不落库()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var request = CreatePunchRequest("ClockIn", "2026-05-17T23:00:00Z");
            request.ExpectedPunchType = "ClockOut";

            var result = await service.PunchAsync(request);

            Assert.False(result.Success);
            Assert.Equal("PUNCH_TYPE_MISMATCH", result.ErrorCode);
            Assert.Contains("已下班", result.Message);
            Assert.Equal(0, await _db.Queryable<AttendancePunch>().CountAsync());
        }

        [Fact]
        public async Task PunchAsync_已上班却期望上班_提示已上班()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            Assert.True((await service.PunchAsync(CreatePunchRequest("ClockIn", "2026-05-17T23:00:00Z"))).Success);
            var request = CreatePunchRequest("ClockIn", "2026-05-18T07:00:00Z");
            request.ExpectedPunchType = "ClockIn";

            var result = await service.PunchAsync(request);

            Assert.False(result.Success);
            Assert.Equal("PUNCH_TYPE_MISMATCH", result.ErrorCode);
            Assert.Contains("已上班", result.Message);
            Assert.Equal(1, await _db.Queryable<AttendancePunch>().CountAsync());
        }

        [Theory]
        [InlineData("ClockIn")]
        [InlineData("clockin")]
        [InlineData(null)]
        public async Task PunchAsync_期望类型一致或旧客户端未带_正常打卡(string? expectedPunchType)
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            var request = CreatePunchRequest("ClockIn", "2026-05-17T23:00:00Z");
            request.ExpectedPunchType = expectedPunchType;

            var result = await CreateService("staff-user", "staff", "StoreStaff").PunchAsync(request);

            Assert.True(result.Success, result.Message);
            Assert.Equal("ClockIn", result.Data!.PunchType);
        }

        // ---------------------------------------------------------------------------------
        // 口径⑤：午夜自动下班
        // ---------------------------------------------------------------------------------

        [Fact]
        public async Task 午夜自动下班_补系统下班卡_该段不计薪_生成漏下班_重复执行不重复补()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await _db.Insertable(CreateStoredPunch("night-in", "ClockIn", ReviewRoundWorkDate.AddHours(9)))
                .ExecuteCommandAsync();
            // Brisbane 2026-05-19 00:30。
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 14, 30, 0, DateTimeKind.Utc));
            var service = CreateService("manager-user", "manager", "StoreManager");

            Assert.Equal(1, await service.RunMidnightAutoClockOutAsync());
            Assert.Equal(0, await service.RunMidnightAutoClockOutAsync());

            var auto = await _db.Queryable<AttendancePunch>().SingleAsync(item => item.IsSystemAutoClockOut);
            Assert.Equal("ClockOut", auto.PunchType);
            Assert.Equal(new DateTime(2026, 5, 19), auto.PunchTimeLocal);
            Assert.Equal(new DateTime(2026, 5, 18, 14, 0, 0), auto.PunchTimeUtc);
            Assert.Equal(ReviewRoundWorkDate, auto.WorkDate);
            Assert.Equal("schedule-1", auto.ScheduleGuid);
            Assert.Single(await _db.Queryable<AttendanceApproval>()
                .Where(item => item.SourceType == "MissingClockOut" && item.ReviewStatus == "Pending")
                .ToListAsync());

            var records = await service.GetAttendanceRecordsAsync(new AttendanceScheduleQueryDto { StoreCode = "BRI", Page = 1, PageSize = 20 });
            var schedule = Assert.Single(records.Data!.Items!);
            Assert.Equal(0, schedule.WorkedMinutes);
            Assert.Equal(0, schedule.PaidMinutes);
            Assert.True(schedule.HasMissingClockOut);
            Assert.Equal("MissingClockOut", schedule.ScheduleState);
            Assert.Equal(0, schedule.CandidateOvertimeMinutes);
            Assert.True(schedule.Segments.Single().ClockOut!.IsSystemAutoClockOut);
        }

        [Fact]
        public async Task 午夜之前或已下班_不补自动下班卡()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await SeedScheduleAsync("closed", "BRI", "manager-user", ReviewRoundWorkDate, "Active");
            await _db.Insertable(new[]
            {
                CreateStoredPunch("open-in", "ClockIn", ReviewRoundWorkDate.AddHours(9)),
                CreateStoredPunchForSchedule("closed-in", "closed", "BRI", "manager-user", "ClockIn", ReviewRoundWorkDate.AddHours(9)),
                CreateStoredPunchForSchedule("closed-out", "closed", "BRI", "manager-user", "ClockOut", ReviewRoundWorkDate.AddHours(17)),
            }).ExecuteCommandAsync();
            var service = CreateService("manager-user", "manager", "StoreManager");

            // Brisbane 23:30：还没到午夜。
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 13, 30, 0, DateTimeKind.Utc));
            Assert.Equal(0, await service.RunMidnightAutoClockOutAsync());

            // 过了午夜：只补仍未闭合的那一段。
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 14, 5, 0, DateTimeKind.Utc));
            Assert.Equal(1, await service.RunMidnightAutoClockOutAsync());
            Assert.Equal("schedule-1", (await _db.Queryable<AttendancePunch>().SingleAsync(item => item.IsSystemAutoClockOut)).ScheduleGuid);
        }

        [Fact]
        public async Task 午夜自动下班开关关闭_不补卡()
        {
            await SeedStoreScopeAsync();
            await _db.Insertable(new AttendanceSettings { AutoClockOutAtMidnight = false, CreatedAt = DateTime.UtcNow })
                .ExecuteCommandAsync();
            await SeedScheduleAsync();
            await _db.Insertable(CreateStoredPunch("night-in", "ClockIn", ReviewRoundWorkDate.AddHours(9)))
                .ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 14, 30, 0, DateTimeKind.Utc));

            Assert.Equal(0, await CreateService("manager-user", "manager", "StoreManager").RunMidnightAutoClockOutAsync());
            Assert.Equal(0, await _db.Queryable<AttendancePunch>().CountAsync(item => item.IsSystemAutoClockOut));
        }

        [Fact]
        public async Task 店长填实际下班时间_覆盖自动下班卡_漏下班结案且不再重开()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await _db.Insertable(CreateStoredPunch("night-in", "ClockIn", ReviewRoundWorkDate.AddHours(9)))
                .ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 14, 30, 0, DateTimeKind.Utc));
            var manager = CreateService("manager-user", "manager", "StoreManager");
            Assert.Equal(1, await manager.RunMidnightAutoClockOutAsync());
            var auto = await _db.Queryable<AttendancePunch>().SingleAsync(item => item.IsSystemAutoClockOut);

            var request = new CreateManagedAttendancePunchAdjustmentDto
            {
                UserGuid = "staff-user",
                StoreCode = "BRI",
                ScheduleGuid = "schedule-1",
                OriginalPunchGuid = auto.PunchGuid,
                PunchType = "ClockOut",
                RequestedPunchTimeUtc = new DateTimeOffset(2026, 5, 18, 7, 0, 0, TimeSpan.Zero),
                Reason = "员工忘记下班，实际 17:00 离店",
            };
            var preview = await manager.PreviewManagedPunchAdjustmentAsync(request);
            Assert.True(preview.Success, $"{preview.ErrorCode}: {preview.Message}");
            request.PreviewRevision = preview.Data!.PreviewRevision;
            var applied = await manager.CreateManagedPunchAdjustmentAsync(request);
            Assert.True(applied.Success, $"{applied.ErrorCode}: {applied.Message}");

            var missing = await _db.Queryable<AttendanceApproval>().SingleAsync(item => item.SourceType == "MissingClockOut");
            Assert.Equal("Approved", missing.ReviewStatus);
            Assert.Equal("manager-user", missing.ReviewerUserGuid);

            // 之后任何对账都不重开，计薪按实际 9–17。
            Assert.True((await manager.GetPendingApprovalsAsync(new AttendanceApprovalQueryDto { StoreCode = "BRI" })).Success);
            Assert.Equal(1, await _db.Queryable<AttendanceApproval>().CountAsync(item => item.SourceType == "MissingClockOut"));
            var records = await manager.GetAttendanceRecordsAsync(new AttendanceScheduleQueryDto { StoreCode = "BRI", Page = 1, PageSize = 20 });
            var schedule = Assert.Single(records.Data!.Items!);
            Assert.Equal(480, schedule.WorkedMinutes);
            Assert.False(schedule.HasMissingClockOut);
            Assert.Equal("Completed", schedule.ScheduleState);
        }

        [Fact]
        public void 计算器_自动下班卡闭合的段_不计时长_视为漏下班_不定稿不算加班()
        {
            var schedule = new AttendanceSchedule
            {
                ScheduleGuid = "calc",
                StoreCode = "BRI",
                UserGuid = "staff-user",
                WorkDate = ReviewRoundWorkDate,
                StartTime = new TimeSpan(9, 0, 0),
                EndTime = new TimeSpan(17, 0, 0),
                Status = "Active",
            };
            var autoOut = CreateStoredPunchForSchedule("calc-out", "calc", "BRI", "staff-user", "ClockOut", ReviewRoundWorkDate.AddDays(1));
            autoOut.IsSystemAutoClockOut = true;
            var session = AttendanceWorkSessionCalculator.Calculate(
                schedule,
                new[]
                {
                    CreateStoredPunchForSchedule("calc-in", "calc", "BRI", "staff-user", "ClockIn", ReviewRoundWorkDate.AddHours(9)),
                    autoOut,
                },
                segmentLimit: 4,
                nowLocal: ReviewRoundWorkDate.AddDays(1).AddHours(1),
                earlyLeaveGraceMinutes: 5);

            Assert.False(session.HasOpenSegment);
            Assert.True(session.HasMissingClockOut);
            Assert.Equal("MissingClockOut", session.ScheduleState);
            Assert.Equal(0, session.WorkedMinutes);
            Assert.Equal(0, session.LateOvertimeMinutes);
            Assert.Equal(0, session.CandidateOvertimeMinutes);
        }

        // ---------------------------------------------------------------------------------
        // 口径⑦：段数上限从设置读取，默认员工 4、店长 6
        // ---------------------------------------------------------------------------------

        [Fact]
        public async Task 默认段数上限_员工4段_第5次上班被拒()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("long", "BRI", "staff-user", ReviewRoundWorkDate, "Active", new TimeSpan(8, 0, 0), new TimeSpan(20, 0, 0));
            var service = CreateService("staff-user", "staff", "StoreStaff");
            // Brisbane 08:00 起每小时一进一出，共 4 段。
            for (var segment = 0; segment < 4; segment++)
            {
                var inAt = new DateTime(2026, 5, 17, 22, 0, 0, DateTimeKind.Utc).AddHours(segment * 2);
                Assert.True((await service.PunchAsync(CreatePunchRequest("ClockIn", inAt.ToString("O")))).Success);
                Assert.True((await service.PunchAsync(CreatePunchRequest("ClockOut", inAt.AddHours(1).ToString("O")))).Success);
            }

            var fifth = await service.PunchAsync(CreatePunchRequest("ClockIn", "2026-05-18T06:30:00Z"));

            Assert.False(fifth.Success);
            Assert.Equal("SEGMENT_LIMIT_REACHED", fifth.ErrorCode);
            var today = await service.GetMyTodayAsync(ReviewRoundWorkDate, "BRI");
            Assert.Equal(4, Assert.Single(today.Data!.Schedules).SegmentLimit);
        }

        [Fact]
        public async Task 考勤设置_可调段数上限与午夜自动下班_旧客户端不带则保持原值()
        {
            await SeedStoreScopeAsync();
            var admin = CreateService("admin-user", "admin", "Admin");

            var defaults = await admin.GetSettingsAsync();
            Assert.Equal(4, defaults.Data!.EmployeeSegmentLimit);
            Assert.Equal(6, defaults.Data.ManagerSegmentLimit);
            Assert.True(defaults.Data.AutoClockOutAtMidnight);

            var updated = await admin.UpdateSettingsAsync(new UpdateAttendanceSettingsDto
            {
                EmployeeSegmentLimit = 3,
                ManagerSegmentLimit = 99,
                AutoClockOutAtMidnight = false,
            });
            Assert.True(updated.Success, updated.Message);
            Assert.Equal(3, updated.Data!.EmployeeSegmentLimit);
            Assert.Equal(AttendanceReactService.MaxSegmentLimit, updated.Data.ManagerSegmentLimit);
            Assert.False(updated.Data.AutoClockOutAtMidnight);

            var legacy = await admin.UpdateSettingsAsync(new UpdateAttendanceSettingsDto { LateGraceMinutes = 10 });
            Assert.True(legacy.Success, legacy.Message);
            Assert.Equal(3, legacy.Data!.EmployeeSegmentLimit);
            Assert.False(legacy.Data.AutoClockOutAtMidnight);
        }
    }
}
