using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services.Attendance;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests
{
    /// <summary>
    /// 考勤审批推送（N3-c）：提交 → 审批人，结果 → 申请人，审批失效 → 撤回审批人手里的待办。
    /// 夹具与造数方法共用 AttendanceReactServiceTests；通知落库到真实的通知发布器（SQLite），审批人解析用真实解析器 + 权限桩。
    /// 铁律：发布 / 撤回只能在业务事务提交后，且推送失败不改变业务结果。
    /// </summary>
    public sealed partial class AttendanceReactServiceTests
    {
        // 默认 null：不接通知平台（等价于线上未注册通知器）；推送用例通过 UseApprovalNotifications 注入。
        private IAttendanceApprovalNotifier? _approvalNotifier;
        private ApprovalPublisherProbe? _approvalPublisherProbe;
        // 持有审批权限码的用户（模拟 IRoleService.UserHasPermissionAsync，含直授）。
        private readonly HashSet<string> _approvalReviewers = new(StringComparer.OrdinalIgnoreCase);

        // ---------------------------------------------------------------------------------
        // 提交 → 审批人
        // ---------------------------------------------------------------------------------

        [Fact]
        public async Task CreateMyLeaveRequestAsync_通知有权审该门店的人_不含申请人_正文不含原因()
        {
            await SeedStoreScopeAsync();
            await SetApprovalUserFullNameAsync("staff-user", "Amy Staff");
            await SeedApprovalReviewerAsync("manager-user");
            // 申请人自己也是有权限的 BRI 主分店店长：仍不能收到自己申请的待办。
            await SeedApprovalReviewerAsync("staff-user", storeGuid: "store-bri");
            UseApprovalNotifications();

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .CreateMyLeaveRequestAsync(new CreateAttendanceLeaveRequestDto
                {
                    StoreCode = "BRI",
                    LeaveType = "AnnualLeave",
                    StartDate = new DateTime(2026, 10, 20),
                    EndDate = new DateTime(2026, 10, 22),
                    Reason = "SECRET-LEAVE-REASON",
                });

            Assert.True(result.Success, result.Message);
            var approval = await _db.Queryable<AttendanceApproval>().FirstAsync(item =>
                item.SourceType == "Leave" && item.SourceGuid == result.Data!.LeaveGuid);
            var notification = Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalSubmitted));
            Assert.Equal("approval.submitted", notification.Type);
            Assert.Equal("Leave request awaiting approval", notification.Title);
            Assert.Equal("Amy Staff submitted a leave request for 2026-10-20 to 2026-10-22.", notification.Body);
            Assert.Equal("BRI", notification.StoreCode);
            Assert.Equal(AppNotificationPriorities.Normal, notification.Priority);
            Assert.Equal($"approval:submitted:{approval.ApprovalGuid}", notification.DedupeKey);
            Assert.Null(notification.RevokedAtUtc);
            Assert.Equal(["manager-user"], await ApprovalRecipientsAsync(notification.NotificationId));
            AssertDataIsIdentifiersOnly(notification, approval.ApprovalGuid, "Leave");
            AssertNoSensitiveText(notification, "SECRET-LEAVE-REASON");
            AssertPublisherCalledOutsideTransactions();
        }

        [Fact]
        public async Task CreateMyLeaveRequestAsync_单日请假只写一个日期_姓名缺失用兜底词()
        {
            await SeedStoreScopeAsync();
            await SeedApprovalReviewerAsync("manager-user");
            UseApprovalNotifications();

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .CreateMyLeaveRequestAsync(new CreateAttendanceLeaveRequestDto
                {
                    StoreCode = "BRI",
                    LeaveType = "SickLeave",
                    StartDate = new DateTime(2026, 10, 20),
                    EndDate = new DateTime(2026, 10, 20),
                });

            Assert.True(result.Success, result.Message);
            var notification = Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalSubmitted));
            // 测试用户没有 FullName：不能把用户名（常是邮箱）放进正文。
            Assert.Equal("An employee submitted a leave request for 2026-10-20.", notification.Body);
            Assert.DoesNotContain("staff", notification.Body.Replace("submitted", string.Empty));
        }

        [Fact]
        public async Task CreateMyPunchAdjustmentAsync_待审补卡通知审批人_正文写补卡日期且不含原因()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await SetApprovalUserFullNameAsync("staff-user", "Amy Staff");
            await SeedApprovalReviewerAsync("manager-user");
            UseApprovalNotifications();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));

            var result = await SubmitPunchAdjustmentAfterPreviewAsync(
                CreateService("staff-user", "staff", "StoreStaff"),
                new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI",
                    ScheduleGuid = "schedule-1",
                    PunchType = "ClockIn",
                    RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 9, 0, 0),
                    Reason = "SECRET-补卡原因",
                });

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            Assert.Equal("Pending", result.Data!.Status);
            var approval = await _db.Queryable<AttendanceApproval>().FirstAsync(item =>
                item.SourceType == "PunchAdjustment");
            var notification = Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalSubmitted));
            Assert.Equal("Punch correction awaiting approval", notification.Title);
            Assert.Equal("Amy Staff submitted a punch correction for 2026-05-18.", notification.Body);
            Assert.Equal($"approval:submitted:{approval.ApprovalGuid}", notification.DedupeKey);
            Assert.Equal(["manager-user"], await ApprovalRecipientsAsync(notification.NotificationId));
            AssertDataIsIdentifiersOnly(notification, approval.ApprovalGuid, "PunchAdjustment");
            AssertNoSensitiveText(notification, "SECRET-补卡原因");
            AssertPublisherCalledOutsideTransactions();
        }

        [Fact]
        public async Task CreateMyPunchAdjustmentAsync_店长自己提交待审补卡_收件人不含他自己()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("manager-schedule", "BRI", "manager-user", new DateTime(2026, 5, 18), "Active");
            await SeedApprovalReviewerAsync("manager-user");
            await SeedApprovalReviewerAsync("admin-user", roleName: "Admin");
            UseApprovalNotifications();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            _deniedPermissions.Add(Permissions.Attendance.Punch.AdjustManagedStore); // 没有直接生效权限 → 进审批

            var result = await SubmitPunchAdjustmentAfterPreviewAsync(
                CreateService("manager-user", "manager", "StoreManager"),
                new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI",
                    ScheduleGuid = "manager-schedule",
                    PunchType = "ClockIn",
                    RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 9, 0, 0),
                    Reason = "忘记打卡",
                });

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            Assert.Equal("Pending", result.Data!.Status);
            var notification = Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalSubmitted));
            Assert.Equal(["admin-user"], await ApprovalRecipientsAsync(notification.NotificationId));
        }

        [Fact]
        public async Task CreateManagedPunchAdjustmentAsync_店长代改直接生效_不推送()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await SeedApprovalReviewerAsync("manager-user");
            UseApprovalNotifications();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            var service = CreateService("manager-user", "manager", "StoreManager");
            var request = new CreateManagedAttendancePunchAdjustmentDto
            {
                UserGuid = "staff-user",
                StoreCode = "BRI",
                ScheduleGuid = "schedule-1",
                PunchType = "ClockIn",
                RequestedPunchTimeUtc = new DateTimeOffset(2026, 5, 17, 23, 0, 0, TimeSpan.Zero),
                Reason = "员工漏打卡",
            };

            var preview = await service.PreviewManagedPunchAdjustmentAsync(request);
            Assert.True(preview.Success, $"{preview.ErrorCode}: {preview.Message}");
            request.PreviewRevision = preview.Data!.PreviewRevision;
            var result = await service.CreateManagedPunchAdjustmentAsync(request);

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            Assert.Equal("Applied", result.Data!.Status);
            Assert.Empty(await _db.Queryable<AppNotification>().ToListAsync());
        }

        [Fact]
        public async Task PunchAsync_下班声明没休息_事务提交后通知审批人()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await SetApprovalUserFullNameAsync("staff-user", "Amy Staff");
            await SeedApprovalReviewerAsync("manager-user");
            UseApprovalNotifications();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            Assert.True((await service.PunchAsync(CreatePunchRequest("ClockIn", MealClockInUtc))).Success);
            var request = CreatePunchRequest("ClockOut", MealFinalClockOutUtc);
            request.MealDeclaration = new AttendanceMealDeclarationDto { NotTakenCount = 1, Reason = "SECRET-太忙没时间" };

            var clockOut = await service.PunchAsync(request);

            Assert.True(clockOut.Success, clockOut.Message);
            var approval = await _db.Queryable<AttendanceApproval>().FirstAsync(item => item.SourceType == "MealBreak");
            var notification = Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalSubmitted));
            Assert.Equal("Meal break report awaiting approval", notification.Title);
            Assert.Equal("Amy Staff submitted a meal break report for 2026-05-18.", notification.Body);
            Assert.Equal($"approval:submitted:{approval.ApprovalGuid}", notification.DedupeKey);
            Assert.Equal(["manager-user"], await ApprovalRecipientsAsync(notification.NotificationId));
            AssertDataIsIdentifiersOnly(notification, approval.ApprovalGuid, "MealBreak");
            AssertNoSensitiveText(notification, "SECRET-太忙没时间");
            AssertPublisherCalledOutsideTransactions();
        }

        [Fact]
        public async Task PunchAsync_下班声明已休息_没有审批也不推送()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await SeedApprovalReviewerAsync("manager-user");
            UseApprovalNotifications();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            Assert.True((await service.PunchAsync(CreatePunchRequest("ClockIn", MealClockInUtc))).Success);
            var request = CreatePunchRequest("ClockOut", MealFinalClockOutUtc);
            request.MealDeclaration = new AttendanceMealDeclarationDto { NotTakenCount = 0 };

            var clockOut = await service.PunchAsync(request);

            Assert.True(clockOut.Success, clockOut.Message);
            Assert.Empty(await _db.Queryable<AppNotification>().ToListAsync());
        }

        [Fact]
        public async Task PunchAsync_重复扫码返回首次结果_不重复推送()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await SeedApprovalReviewerAsync("manager-user");
            UseApprovalNotifications();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            Assert.True((await service.PunchAsync(CreatePunchRequest("ClockIn", MealClockInUtc))).Success);
            var request = CreatePunchRequest("ClockOut", MealFinalClockOutUtc);
            request.MealDeclaration = new AttendanceMealDeclarationDto { NotTakenCount = 1 };

            Assert.True((await service.PunchAsync(request)).Success);
            Assert.True((await service.PunchAsync(request)).Success);

            Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalSubmitted));
        }

        [Fact]
        public async Task CreateManagedLeaveRequestAsync_店长代提请假_不推送()
        {
            await SeedStoreScopeAsync();
            await SeedEmployeeProfileAsync("staff-user", EmployeeType.FullTime);
            await SeedApprovalReviewerAsync("manager-user");
            await SeedApprovalReviewerAsync("admin-user", roleName: "Admin");
            UseApprovalNotifications();

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .CreateManagedLeaveRequestAsync(new CreateManagedAttendanceLeaveRequestDto
                {
                    StoreCode = "BRI",
                    UserGuid = "staff-user",
                    LeaveType = "AnnualLeave",
                    StartDate = new DateTime(2026, 5, 18),
                    EndDate = new DateTime(2026, 5, 19),
                    Reason = "Family trip",
                });

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, await _db.Queryable<AttendanceApproval>().CountAsync(item => item.SourceType == "Leave"));
            Assert.Empty(await _db.Queryable<AppNotification>().ToListAsync());
        }

        [Fact]
        public async Task 系统派生的审批_打卡_加班_漏下班_提交与处理都不推送()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await SeedApprovalReviewerAsync("manager-user");
            UseApprovalNotifications();
            // 迟到上班卡（本地 10:20，排班 09:00）→ Punch 待审。
            var lateIn = await CreateService("staff-user", "staff", "StoreStaff")
                .PunchAsync(CreatePunchRequest("ClockIn", "2026-05-18T00:20:00Z"));
            Assert.True(lateIn.Success, lateIn.Message);
            var punchApproval = await _db.Queryable<AttendanceApproval>().FirstAsync(item => item.SourceType == "Punch");
            // 派生的加班 / 漏下班待审，同样不推。
            await _db.Insertable(new[]
            {
                new AttendanceApproval
                {
                    ApprovalGuid = "derived-overtime",
                    SourceType = "Overtime",
                    SourceGuid = "schedule-1",
                    StoreCode = "BRI",
                    ApplicantUserGuid = "staff-user",
                    ReviewStatus = "Pending",
                    CandidateOvertimeMinutes = 15,
                    CreatedAt = DateTime.UtcNow,
                },
                new AttendanceApproval
                {
                    ApprovalGuid = "derived-missing",
                    SourceType = "MissingClockOut",
                    SourceGuid = "schedule-1",
                    StoreCode = "BRI",
                    ApplicantUserGuid = "staff-user",
                    ReviewStatus = "Pending",
                    CreatedAt = DateTime.UtcNow,
                },
            }).ExecuteCommandAsync();
            Assert.Empty(await _db.Queryable<AppNotification>().ToListAsync());

            // 处理系统派生的审批（批准 / 系统取消）也不产生通知。
            var approved = await CreateService("manager-user", "manager", "StoreManager")
                .ApproveAsync(punchApproval.ApprovalGuid, new ReviewAttendanceApprovalDto { ReviewRemark = "OK" });
            Assert.True(approved.Success, $"{approved.ErrorCode}: {approved.Message}");
            var cancelled = await CreateService("manager-user", "manager", "StoreManager")
                .DeleteScheduleAsync("schedule-1");
            Assert.True(cancelled.Success, cancelled.Message);

            Assert.Empty(await _db.Queryable<AppNotification>().ToListAsync());
        }

        [Fact]
        public async Task 重复提交同一来源_幂等键带审批Id_同一审批只推一次()
        {
            await SeedStoreScopeAsync();
            await SeedApprovalReviewerAsync("manager-user");
            UseApprovalNotifications();
            var leave = await CreateService("staff-user", "staff", "StoreStaff")
                .CreateMyLeaveRequestAsync(new CreateAttendanceLeaveRequestDto
                {
                    StoreCode = "BRI",
                    StartDate = new DateTime(2026, 10, 20),
                    EndDate = new DateTime(2026, 10, 21),
                });
            Assert.True(leave.Success, leave.Message);

            // 同一来源再次触发提交通知（如重试）：按审批 Id 去重，不重复推给审批人。
            await _approvalNotifier!.NotifySubmittedAsync("Leave", leave.Data!.LeaveGuid);
            await _approvalNotifier.NotifySubmittedAsync("Leave", leave.Data.LeaveGuid);

            Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalSubmitted));
        }

        [Fact]
        public async Task 提交通知发布期间审批已被处理_补一次撤回_不残留失效待办()
        {
            await SeedStoreScopeAsync();
            await SeedApprovalReviewerAsync("manager-user");
            // 模拟审批人在通知落库的同一瞬间抢先处理了申请：处理方的撤回会落空，发布后的复核要补撤回。
            UseApprovalNotifications(inner => new BeforePublishHookPublisher(
                inner,
                () => _db.Updateable<AttendanceApproval>()
                    .SetColumns(item => item.ReviewStatus == "Approved")
                    .Where(item => item.SourceType == "Leave")
                    .ExecuteCommandAsync()));

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .CreateMyLeaveRequestAsync(new CreateAttendanceLeaveRequestDto
                {
                    StoreCode = "BRI",
                    StartDate = new DateTime(2026, 10, 20),
                    EndDate = new DateTime(2026, 10, 21),
                });

            Assert.True(result.Success, result.Message);
            var submitted = Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalSubmitted));
            Assert.NotNull(submitted.RevokedAtUtc);
        }

        [Fact]
        public async Task 没有任何审批人时_不发布_也不影响提交()
        {
            await SeedStoreScopeAsync();
            // 没有任何人持有审批权限码。
            UseApprovalNotifications();

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .CreateMyLeaveRequestAsync(new CreateAttendanceLeaveRequestDto
                {
                    StoreCode = "BRI",
                    StartDate = new DateTime(2026, 10, 20),
                    EndDate = new DateTime(2026, 10, 21),
                });

            Assert.True(result.Success, result.Message);
            Assert.Empty(await _db.Queryable<AppNotification>().ToListAsync());
            Assert.Equal(1, await _db.Queryable<AttendanceApproval>().CountAsync(item => item.SourceType == "Leave"));
        }

        // ---------------------------------------------------------------------------------
        // 结果 → 申请人，并撤回审批人手里的待办
        // ---------------------------------------------------------------------------------

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task 请假通过或驳回_申请人收到结果_待办被撤回_重复审批不重复发(bool approve)
        {
            await SeedStoreScopeAsync();
            await SeedApprovalReviewerAsync("manager-user");
            UseApprovalNotifications();
            var leave = await CreateService("staff-user", "staff", "StoreStaff")
                .CreateMyLeaveRequestAsync(new CreateAttendanceLeaveRequestDto
                {
                    StoreCode = "BRI",
                    StartDate = new DateTime(2026, 10, 20),
                    EndDate = new DateTime(2026, 10, 22),
                    Reason = "家里有事",
                });
            Assert.True(leave.Success, leave.Message);
            var approval = await _db.Queryable<AttendanceApproval>().FirstAsync(item => item.SourceType == "Leave");

            var manager = CreateService("manager-user", "manager", "StoreManager");
            var review = new ReviewAttendanceApprovalDto { ReviewRemark = "SECRET-REVIEW-REMARK" };
            var first = approve
                ? await manager.ApproveAsync(approval.ApprovalGuid, review)
                : await manager.RejectAsync(approval.ApprovalGuid, review);
            var second = approve
                ? await manager.ApproveAsync(approval.ApprovalGuid, review)
                : await manager.RejectAsync(approval.ApprovalGuid, review);

            Assert.True(first.Success, $"{first.ErrorCode}: {first.Message}");
            Assert.False(second.Success);
            Assert.Equal("APPROVAL_ALREADY_REVIEWED", second.ErrorCode);
            var verdict = approve ? "approved" : "rejected";
            var decided = Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalDecided));
            Assert.Equal($"Your leave request was {verdict}", decided.Title);
            Assert.Equal($"Your leave request for 2026-10-20 to 2026-10-22 has been {verdict}.", decided.Body);
            Assert.Equal($"approval:decided:{approval.ApprovalGuid}", decided.DedupeKey);
            Assert.Equal(["staff-user"], await ApprovalRecipientsAsync(decided.NotificationId));
            AssertNoSensitiveText(decided, "SECRET-REVIEW-REMARK", "家里有事");
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(decided.DataJson!)!;
            Assert.Equal(approval.ApprovalGuid, data["approvalGuid"]);
            Assert.Equal(approve ? "Approved" : "Rejected", data["decision"]);
            // 提交时发给审批人的待办已撤回。
            var submitted = Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalSubmitted));
            Assert.NotNull(submitted.RevokedAtUtc);
            AssertPublisherCalledOutsideTransactions();
        }

        [Fact]
        public async Task 用餐加工时通过_申请人收到结果_待办被撤回()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await SeedApprovalReviewerAsync("manager-user");
            UseApprovalNotifications();
            var staff = CreateService("staff-user", "staff", "StoreStaff");
            Assert.True((await staff.PunchAsync(CreatePunchRequest("ClockIn", MealClockInUtc))).Success);
            var request = CreatePunchRequest("ClockOut", MealFinalClockOutUtc);
            request.MealDeclaration = new AttendanceMealDeclarationDto { NotTakenCount = 1 };
            Assert.True((await staff.PunchAsync(request)).Success);
            var approval = await _db.Queryable<AttendanceApproval>().FirstAsync(item => item.SourceType == "MealBreak");

            var approved = await CreateService("manager-user", "manager", "StoreManager")
                .ApproveAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto { ReviewRemark = "确实太忙" });

            Assert.True(approved.Success, $"{approved.ErrorCode}: {approved.Message}");
            var decided = Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalDecided));
            Assert.Equal("Your meal break report was approved", decided.Title);
            Assert.Equal("Your meal break report for 2026-05-18 has been approved.", decided.Body);
            Assert.Equal(["staff-user"], await ApprovalRecipientsAsync(decided.NotificationId));
            AssertNoSensitiveText(decided, "确实太忙");
            Assert.NotNull(Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalSubmitted)).RevokedAtUtc);
        }

        [Fact]
        public async Task 用餐加工时驳回_申请人收到结果()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await SeedApprovalReviewerAsync("manager-user");
            UseApprovalNotifications();
            var staff = CreateService("staff-user", "staff", "StoreStaff");
            Assert.True((await staff.PunchAsync(CreatePunchRequest("ClockIn", MealClockInUtc))).Success);
            var request = CreatePunchRequest("ClockOut", MealFinalClockOutUtc);
            request.MealDeclaration = new AttendanceMealDeclarationDto { NotTakenCount = 1 };
            Assert.True((await staff.PunchAsync(request)).Success);
            var approval = await _db.Queryable<AttendanceApproval>().FirstAsync(item => item.SourceType == "MealBreak");

            var rejected = await CreateService("manager-user", "manager", "StoreManager")
                .RejectAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto { ReviewRemark = "当天有轮休" });

            Assert.True(rejected.Success, $"{rejected.ErrorCode}: {rejected.Message}");
            var decided = Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalDecided));
            Assert.Equal("Your meal break report was rejected", decided.Title);
            AssertNoSensitiveText(decided, "当天有轮休");
        }

        [Fact]
        public async Task 补卡通过_申请人收到结果_待办被撤回()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await SeedApprovalReviewerAsync("manager-user");
            UseApprovalNotifications();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            var created = await SubmitPunchAdjustmentAfterPreviewAsync(
                CreateService("staff-user", "staff", "StoreStaff"),
                new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI",
                    ScheduleGuid = "schedule-1",
                    PunchType = "ClockIn",
                    RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 9, 0, 0),
                    Reason = "忘记打卡",
                });
            Assert.True(created.Success, $"{created.ErrorCode}: {created.Message}");
            var approval = await _db.Queryable<AttendanceApproval>().FirstAsync(item => item.SourceType == "PunchAdjustment");

            var approved = await CreateService("manager-user", "manager", "StoreManager")
                .ApproveAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto { ReviewRemark = "同意" });

            Assert.True(approved.Success, $"{approved.ErrorCode}: {approved.Message}");
            var decided = Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalDecided));
            Assert.Equal("Your punch correction was approved", decided.Title);
            Assert.Equal("Your punch correction for 2026-05-18 has been approved.", decided.Body);
            Assert.NotNull(Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalSubmitted)).RevokedAtUtc);
            AssertPublisherCalledOutsideTransactions();
        }

        [Fact]
        public async Task 申请人与审批人是同一人时_不发结果通知_但仍撤回待办()
        {
            await SeedStoreScopeAsync();
            await SeedApprovalReviewerAsync("manager-user");
            UseApprovalNotifications();
            var leave = await CreateService("staff-user", "staff", "StoreStaff")
                .CreateMyLeaveRequestAsync(new CreateAttendanceLeaveRequestDto
                {
                    StoreCode = "BRI",
                    StartDate = new DateTime(2026, 10, 20),
                    EndDate = new DateTime(2026, 10, 21),
                });
            Assert.True(leave.Success, leave.Message);
            var approval = await _db.Queryable<AttendanceApproval>().FirstAsync(item => item.SourceType == "Leave");
            // 接口本就拒绝自审（SELF_REVIEW_FORBIDDEN），这里直接验证通知器的兜底：模拟一条自审后的终态记录。
            approval.ReviewStatus = "Approved";
            approval.ReviewerUserGuid = approval.ApplicantUserGuid;

            await _approvalNotifier!.NotifyDecidedAsync(approval);

            Assert.Empty(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalDecided));
            Assert.NotNull(Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalSubmitted)).RevokedAtUtc);
        }

        // ---------------------------------------------------------------------------------
        // 审批失效 → 撤回审批人手里的待办
        // ---------------------------------------------------------------------------------

        [Fact]
        public async Task CancelMyLeaveRequestAsync_申请人撤销请假_撤回审批人待办()
        {
            await SeedStoreScopeAsync();
            await SeedApprovalReviewerAsync("manager-user");
            UseApprovalNotifications();
            var staff = CreateService("staff-user", "staff", "StoreStaff");
            var leave = await staff.CreateMyLeaveRequestAsync(new CreateAttendanceLeaveRequestDto
            {
                StoreCode = "BRI",
                StartDate = new DateTime(2026, 10, 20),
                EndDate = new DateTime(2026, 10, 21),
            });
            Assert.True(leave.Success, leave.Message);
            Assert.Null(Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalSubmitted)).RevokedAtUtc);

            var cancelled = await staff.CancelMyLeaveRequestAsync(leave.Data!.LeaveGuid);

            Assert.True(cancelled.Success, cancelled.Message);
            Assert.NotNull(Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalSubmitted)).RevokedAtUtc);
            AssertPublisherCalledOutsideTransactions();
        }

        [Fact]
        public async Task UpdateScheduleAsync_排班取消_带走待审用餐申报_撤回审批人待办()
        {
            await SeedPendingMealClaimWithSubmittedNotificationAsync();

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .UpdateScheduleAsync("schedule-1", new UpdateAttendanceScheduleDto
                {
                    WorkDate = new DateTime(2026, 5, 18),
                    StartTime = new TimeSpan(9, 0, 0),
                    EndTime = new TimeSpan(17, 0, 0),
                    Status = "Cancelled",
                });

            Assert.True(result.Success, result.Message);
            Assert.Equal("Cancelled", (await _db.Queryable<AttendanceMealClaim>().FirstAsync()).Status);
            Assert.NotNull(Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalSubmitted)).RevokedAtUtc);
            AssertPublisherCalledOutsideTransactions();
        }

        [Fact]
        public async Task DeleteScheduleAsync_删除排班_带走待审用餐申报_撤回审批人待办()
        {
            await SeedPendingMealClaimWithSubmittedNotificationAsync();

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .DeleteScheduleAsync("schedule-1");

            Assert.True(result.Success, result.Message);
            Assert.Equal("Cancelled", (await _db.Queryable<AttendanceMealClaim>().FirstAsync()).Status);
            Assert.NotNull(Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalSubmitted)).RevokedAtUtc);
            AssertPublisherCalledOutsideTransactions();
        }

        [Fact]
        public async Task 读接口对账发现排班已失效_取消用餐申报并撤回审批人待办()
        {
            await SeedPendingMealClaimWithSubmittedNotificationAsync();
            // 入口快照里排班还是有效的，锁内重读时已被取消 → 对账取消它名下待审的用餐申报。
            var snapshot = await _db.Queryable<AttendanceSchedule>().ToListAsync();
            await _db.Updateable<AttendanceSchedule>()
                .SetColumns(item => item.Status == "Cancelled")
                .Where(item => item.ScheduleGuid == "schedule-1")
                .ExecuteCommandAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");

            await InvokeReconcileAsync(service, snapshot);

            Assert.Equal("Cancelled", (await _db.Queryable<AttendanceMealClaim>().FirstAsync()).Status);
            Assert.Equal("Cancelled", (await _db.Queryable<AttendanceApproval>()
                .FirstAsync(item => item.SourceType == "MealBreak")).ReviewStatus);
            Assert.NotNull(Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalSubmitted)).RevokedAtUtc);
            AssertPublisherCalledOutsideTransactions();
        }

        [Fact]
        public async Task ApproveAsync_审批人点开时发现用餐申报已过期_自动取消并撤回待办()
        {
            await SeedPendingMealClaimWithSubmittedNotificationAsync();
            var approval = await _db.Queryable<AttendanceApproval>().FirstAsync(item => item.SourceType == "MealBreak");
            await _db.Updateable<AttendanceSchedule>()
                .SetColumns(item => item.Status == "Cancelled")
                .Where(item => item.ScheduleGuid == "schedule-1")
                .ExecuteCommandAsync();

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .ApproveAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto { ReviewRemark = "同意" });

            Assert.False(result.Success);
            Assert.Equal("MEAL_CLAIM_STALE", result.ErrorCode);
            Assert.NotNull(Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalSubmitted)).RevokedAtUtc);
            // 系统取消不是审批结果：不给申请人发「已通过 / 已驳回」。
            Assert.Empty(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalDecided));
            AssertPublisherCalledOutsideTransactions();
        }

        [Fact]
        public async Task RejectAsync_用餐申报缺备注被拒绝时事务回滚_不撤回也不通知()
        {
            await SeedPendingMealClaimWithSubmittedNotificationAsync();
            var approval = await _db.Queryable<AttendanceApproval>().FirstAsync(item => item.SourceType == "MealBreak");

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .RejectAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto());

            Assert.False(result.Success);
            Assert.Equal("REVIEW_REMARK_REQUIRED", result.ErrorCode);
            Assert.Null(Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalSubmitted)).RevokedAtUtc);
            Assert.Empty(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalDecided));
        }

        [Fact]
        public async Task ApproveAsync_补卡申请已失效_自动取消并撤回审批人待办()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await SeedApprovalReviewerAsync("manager-user");
            UseApprovalNotifications();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            var created = await SubmitPunchAdjustmentAfterPreviewAsync(
                CreateService("staff-user", "staff", "StoreStaff"),
                new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI",
                    ScheduleGuid = "schedule-1",
                    PunchType = "ClockIn",
                    RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 9, 0, 0),
                    Reason = "忘记打卡",
                });
            Assert.True(created.Success, $"{created.ErrorCode}: {created.Message}");
            var approval = await _db.Queryable<AttendanceApproval>().FirstAsync(item => item.SourceType == "PunchAdjustment");
            await _db.Updateable<AttendancePunchAdjustment>()
                .SetColumns(item => item.Status == "Cancelled")
                .Where(item => item.AdjustmentGuid == created.Data!.AdjustmentGuid)
                .ExecuteCommandAsync();

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .ApproveAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto { ReviewRemark = "同意" });

            Assert.False(result.Success);
            Assert.Equal("ADJUSTMENT_NOT_FOUND", result.ErrorCode);
            Assert.Equal("Cancelled", (await _db.Queryable<AttendanceApproval>()
                .FirstAsync(item => item.ApprovalGuid == approval.ApprovalGuid)).ReviewStatus);
            Assert.NotNull(Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalSubmitted)).RevokedAtUtc);
            Assert.Empty(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalDecided));
            AssertPublisherCalledOutsideTransactions();
        }

        // ---------------------------------------------------------------------------------
        // 推送失败绝不改变业务结果
        // ---------------------------------------------------------------------------------

        [Theory]
        [InlineData(PushFailureLayer.Publisher)]
        [InlineData(PushFailureLayer.Notifier)]
        public async Task 推送失败_请假提交_审批_撤销_业务结果不变(PushFailureLayer layer)
        {
            await SeedStoreScopeAsync();
            await SeedApprovalReviewerAsync("manager-user");
            UseFailingApprovalNotifications(layer);
            var staff = CreateService("staff-user", "staff", "StoreStaff");

            var leave = await staff.CreateMyLeaveRequestAsync(new CreateAttendanceLeaveRequestDto
            {
                StoreCode = "BRI",
                StartDate = new DateTime(2026, 10, 20),
                EndDate = new DateTime(2026, 10, 22),
            });
            Assert.True(leave.Success, leave.Message);
            var approval = await _db.Queryable<AttendanceApproval>().FirstAsync(item => item.SourceType == "Leave");
            Assert.Equal("Pending", approval.ReviewStatus);

            var approved = await CreateService("manager-user", "manager", "StoreManager")
                .ApproveAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto { ReviewRemark = "OK" });
            Assert.True(approved.Success, $"{approved.ErrorCode}: {approved.Message}");
            Assert.Equal("Approved", (await _db.Queryable<AttendanceApproval>()
                .FirstAsync(item => item.ApprovalGuid == approval.ApprovalGuid)).ReviewStatus);
            Assert.Equal("Approved", (await _db.Queryable<AttendanceLeaveRequest>().FirstAsync()).Status);

            // 另一条请假被申请人撤销：撤回失败同样不影响撤销结果。
            var second = await CreateService("staff-user", "staff", "StoreStaff")
                .CreateMyLeaveRequestAsync(new CreateAttendanceLeaveRequestDto
                {
                    StoreCode = "BRI",
                    StartDate = new DateTime(2026, 11, 1),
                    EndDate = new DateTime(2026, 11, 1),
                });
            Assert.True(second.Success, second.Message);
            var cancelled = await CreateService("staff-user", "staff", "StoreStaff")
                .CancelMyLeaveRequestAsync(second.Data!.LeaveGuid);
            Assert.True(cancelled.Success, cancelled.Message);
            Assert.Equal("Cancelled", (await _db.Queryable<AttendanceApproval>()
                .FirstAsync(item => item.SourceGuid == second.Data.LeaveGuid)).ReviewStatus);
        }

        [Theory]
        [InlineData(PushFailureLayer.Publisher)]
        [InlineData(PushFailureLayer.Notifier)]
        public async Task 推送失败_下班打卡声明没休息_打卡与审批照常落库(PushFailureLayer layer)
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await SeedApprovalReviewerAsync("manager-user");
            UseFailingApprovalNotifications(layer);
            var service = CreateService("staff-user", "staff", "StoreStaff");
            Assert.True((await service.PunchAsync(CreatePunchRequest("ClockIn", MealClockInUtc))).Success);
            var request = CreatePunchRequest("ClockOut", MealFinalClockOutUtc);
            request.MealDeclaration = new AttendanceMealDeclarationDto { NotTakenCount = 1 };

            var clockOut = await service.PunchAsync(request);

            Assert.True(clockOut.Success, $"{clockOut.ErrorCode}: {clockOut.Message}");
            Assert.Equal("Pending", clockOut.Data!.MealClaim!.Status);
            Assert.Equal(2, await _db.Queryable<AttendancePunch>().CountAsync());
            Assert.Equal("Pending", (await _db.Queryable<AttendanceApproval>()
                .FirstAsync(item => item.SourceType == "MealBreak")).ReviewStatus);
        }

        [Theory]
        [InlineData(PushFailureLayer.Publisher)]
        [InlineData(PushFailureLayer.Notifier)]
        public async Task 推送失败_补卡提交与通过_业务结果不变(PushFailureLayer layer)
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await SeedApprovalReviewerAsync("manager-user");
            UseFailingApprovalNotifications(layer);
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));

            var created = await SubmitPunchAdjustmentAfterPreviewAsync(
                CreateService("staff-user", "staff", "StoreStaff"),
                new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI",
                    ScheduleGuid = "schedule-1",
                    PunchType = "ClockIn",
                    RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 9, 0, 0),
                    Reason = "忘记打卡",
                });
            Assert.True(created.Success, $"{created.ErrorCode}: {created.Message}");
            Assert.Equal("Pending", created.Data!.Status);
            var approval = await _db.Queryable<AttendanceApproval>().FirstAsync(item => item.SourceType == "PunchAdjustment");

            var approved = await CreateService("manager-user", "manager", "StoreManager")
                .ApproveAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto { ReviewRemark = "同意" });

            Assert.True(approved.Success, $"{approved.ErrorCode}: {approved.Message}");
            Assert.Equal("Applied", (await _db.Queryable<AttendancePunchAdjustment>().FirstAsync()).Status);
            Assert.Equal(1, await _db.Queryable<AttendancePunch>().CountAsync(item => item.Source == "ManualAdjustment"));
        }

        [Theory]
        [InlineData(PushFailureLayer.Publisher)]
        [InlineData(PushFailureLayer.Notifier)]
        public async Task 推送失败_排班取消与删除_撤回失败不影响业务(PushFailureLayer layer)
        {
            await SeedPendingMealClaimWithSubmittedNotificationAsync();
            UseFailingApprovalNotifications(layer);

            var cancelled = await CreateService("manager-user", "manager", "StoreManager")
                .UpdateScheduleAsync("schedule-1", new UpdateAttendanceScheduleDto
                {
                    WorkDate = new DateTime(2026, 5, 18),
                    StartTime = new TimeSpan(9, 0, 0),
                    EndTime = new TimeSpan(17, 0, 0),
                    Status = "Cancelled",
                });

            Assert.True(cancelled.Success, cancelled.Message);
            Assert.Equal("Cancelled", (await _db.Queryable<AttendanceMealClaim>().FirstAsync()).Status);
            Assert.Equal("Cancelled", (await _db.Queryable<AttendanceApproval>()
                .FirstAsync(item => item.SourceType == "MealBreak")).ReviewStatus);

            var deleted = await CreateService("manager-user", "manager", "StoreManager")
                .DeleteScheduleAsync("schedule-1");
            Assert.True(deleted.Success, deleted.Message);
        }

        [Theory]
        [InlineData(PushFailureLayer.Publisher)]
        [InlineData(PushFailureLayer.Notifier)]
        public async Task 推送失败_读接口对账路径不报错_业务取消照常提交(PushFailureLayer layer)
        {
            await SeedPendingMealClaimWithSubmittedNotificationAsync();
            var snapshot = await _db.Queryable<AttendanceSchedule>().ToListAsync();
            await _db.Updateable<AttendanceSchedule>()
                .SetColumns(item => item.Status == "Cancelled")
                .Where(item => item.ScheduleGuid == "schedule-1")
                .ExecuteCommandAsync();
            UseFailingApprovalNotifications(layer);
            var service = CreateService("staff-user", "staff", "StoreStaff");

            await InvokeReconcileAsync(service, snapshot);

            Assert.Equal("Cancelled", (await _db.Queryable<AttendanceMealClaim>().FirstAsync()).Status);
            Assert.Equal("Cancelled", (await _db.Queryable<AttendanceApproval>()
                .FirstAsync(item => item.SourceType == "MealBreak")).ReviewStatus);
        }

        [Fact]
        public async Task 通知器为null时_业务行为不变_不产生任何通知()
        {
            await SeedStoreScopeAsync();
            await SeedApprovalReviewerAsync("manager-user");
            // 不调用 UseApprovalNotifications：_approvalNotifier 保持 null。
            var leave = await CreateService("staff-user", "staff", "StoreStaff")
                .CreateMyLeaveRequestAsync(new CreateAttendanceLeaveRequestDto
                {
                    StoreCode = "BRI",
                    StartDate = new DateTime(2026, 10, 20),
                    EndDate = new DateTime(2026, 10, 21),
                });
            Assert.True(leave.Success, leave.Message);
            var approval = await _db.Queryable<AttendanceApproval>().FirstAsync(item => item.SourceType == "Leave");

            var approved = await CreateService("manager-user", "manager", "StoreManager")
                .ApproveAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto { ReviewRemark = "OK" });

            Assert.True(approved.Success, $"{approved.ErrorCode}: {approved.Message}");
            Assert.Empty(await _db.Queryable<AppNotification>().ToListAsync());
        }

        // ---------------------------------------------------------------------------------
        // 推送口径与文案
        // ---------------------------------------------------------------------------------

        [Theory]
        [InlineData("Leave", true)]
        [InlineData("PunchAdjustment", true)]
        [InlineData("MealBreak", true)]
        [InlineData("Punch", false)]
        [InlineData("Overtime", false)]
        [InlineData("MissingClockOut", false)]
        [InlineData("leave", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void ApprovalNotificationPolicy_只有员工发起的三类审批才推送(string? sourceType, bool expected)
        {
            Assert.Equal(expected, AttendanceApprovalNotificationPolicy.ShouldNotify(sourceType));
        }

        [Fact]
        public void ApprovalNotificationPolicy_幂等键带审批Id_文案只含英文()
        {
            Assert.Equal("approval:submitted:abc", AttendanceApprovalNotificationPolicy.SubmittedDedupeKey("abc"));
            Assert.Equal("approval:decided:abc", AttendanceApprovalNotificationPolicy.DecidedDedupeKey("abc"));

            foreach (var sourceType in new[] { "Leave", "PunchAdjustment", "MealBreak" })
            {
                var texts = new[]
                {
                    AttendanceApprovalNotificationPolicy.SubmittedTitle(sourceType),
                    AttendanceApprovalNotificationPolicy.SubmittedBody(sourceType, "An employee", "2026-10-20"),
                    AttendanceApprovalNotificationPolicy.DecidedTitle(sourceType, approved: true),
                    AttendanceApprovalNotificationPolicy.DecidedTitle(sourceType, approved: false),
                    AttendanceApprovalNotificationPolicy.DecidedBody(sourceType, true, null),
                    AttendanceApprovalNotificationPolicy.DecidedBody(sourceType, false, "2026-10-20"),
                };
                Assert.All(texts, text => Assert.True(text.All(char.IsAscii), text));
            }

            Assert.Throws<ArgumentOutOfRangeException>(() => AttendanceApprovalNotificationPolicy.SubmittedTitle("Punch"));
        }

        // ---------------------------------------------------------------------------------
        // 夹具与辅助
        // ---------------------------------------------------------------------------------

        public enum PushFailureLayer
        {
            /// <summary>通知平台的发布器抛错（真实通知器兜底）。</summary>
            Publisher,

            /// <summary>通知器本身抛错（考勤服务自己的兜底）。</summary>
            Notifier,
        }

        /// <summary>
        /// 造一条「待审用餐申报 + 已发给审批人的待办通知」：员工跑完整天并声明没休息，店长有审批权限。
        /// 之后各用例各自让排班失效 / 过期，验证待办被撤回。
        /// </summary>
        private async Task SeedPendingMealClaimWithSubmittedNotificationAsync()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await SeedApprovalReviewerAsync("manager-user");
            UseApprovalNotifications();
            var staff = CreateService("staff-user", "staff", "StoreStaff");
            Assert.True((await staff.PunchAsync(CreatePunchRequest("ClockIn", MealClockInUtc))).Success);
            var request = CreatePunchRequest("ClockOut", MealFinalClockOutUtc);
            request.MealDeclaration = new AttendanceMealDeclarationDto { NotTakenCount = 1 };
            var clockOut = await staff.PunchAsync(request);
            Assert.True(clockOut.Success, clockOut.Message);
            Assert.Equal("Pending", clockOut.Data!.MealClaim!.Status);
            var submitted = Assert.Single(await ApprovalNotificationsAsync(AppNotificationTypes.ApprovalSubmitted));
            Assert.Null(submitted.RevokedAtUtc);
        }

        /// <summary>接入真实的通知发布器 + 审批人解析器 + 通知器；发布器外面包一层事务探针，记录每次调用时是否还在事务里。</summary>
        private void UseApprovalNotifications(Func<INotificationPublisher, INotificationPublisher>? decoratePublisher = null)
        {
            var context = CreateSqlSugarContext(_db);
            var roleService = new Mock<IRoleService>();
            roleService
                .Setup(service => service.UserHasPermissionAsync(
                    It.IsAny<string>(),
                    Permissions.Attendance.Approval.ReviewManagedStore))
                .Returns((string userGuid, string _) =>
                    Task.FromResult(ApiResponse<bool>.OK(_approvalReviewers.Contains(userGuid))));
            INotificationPublisher publisher = new NotificationPublisher(
                context,
                new NotificationRecipientResolver(
                    context,
                    roleService.Object,
                    NullLogger<NotificationRecipientResolver>.Instance),
                new NoopRealtimeDispatcher(),
                _timeProvider,
                NullLogger<NotificationPublisher>.Instance);
            if (decoratePublisher != null)
            {
                publisher = decoratePublisher(publisher);
            }

            _approvalPublisherProbe = new ApprovalPublisherProbe(publisher, _db);
            _approvalNotifier = new AttendanceApprovalNotifier(
                context,
                _approvalPublisherProbe,
                new AttendanceApproverResolver(
                    context,
                    roleService.Object,
                    NullLogger<AttendanceApproverResolver>.Instance),
                NullLogger<AttendanceApprovalNotifier>.Instance);
        }

        private void UseFailingApprovalNotifications(PushFailureLayer layer)
        {
            if (layer == PushFailureLayer.Publisher)
            {
                UseApprovalNotifications(_ => new ThrowingNotificationPublisher());
                return;
            }

            _approvalNotifier = new ThrowingApprovalNotifier();
        }

        private async Task SeedApprovalReviewerAsync(
            string userGuid,
            string roleName = "StoreManager",
            string? storeGuid = null)
        {
            if (!await _db.Queryable<User>().AnyAsync(item => item.UserGUID == userGuid))
            {
                await _db.Insertable(new User
                {
                    UserGUID = userGuid,
                    Username = userGuid,
                    Email = $"{userGuid}@example.com",
                    PasswordHash = "hash",
                    CreatedAt = DateTime.UtcNow,
                }).ExecuteCommandAsync();
            }

            var role = await _db.Queryable<Role>().FirstAsync(item => item.RoleName == roleName);
            if (role == null)
            {
                role = new Role
                {
                    RoleGUID = $"approval-role-{roleName}",
                    RoleName = roleName,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                };
                await _db.Insertable(role).ExecuteCommandAsync();
            }

            await _db.Insertable(new UserRole
            {
                UserRoleGUID = $"{userGuid}-{role.RoleGUID}",
                UserGUID = userGuid,
                RoleGUID = role.RoleGUID,
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            if (storeGuid != null)
            {
                await _db.Insertable(new UserStore
                {
                    UserStoreGUID = $"{userGuid}-{storeGuid}-primary",
                    UserGUID = userGuid,
                    StoreGUID = storeGuid,
                    IsPrimary = true,
                    CreatedAt = DateTime.UtcNow,
                }).ExecuteCommandAsync();
            }

            _approvalReviewers.Add(userGuid);
        }

        private Task SetApprovalUserFullNameAsync(string userGuid, string fullName) =>
            _db.Updateable<User>()
                .SetColumns(user => user.FullName == fullName)
                .Where(user => user.UserGUID == userGuid)
                .ExecuteCommandAsync();

        private Task<List<AppNotification>> ApprovalNotificationsAsync(string type) =>
            _db.Queryable<AppNotification>().Where(item => item.Type == type).ToListAsync();

        private async Task<List<string>> ApprovalRecipientsAsync(string notificationId) =>
            (await _db.Queryable<AppNotificationRecipient>()
                .Where(item => item.NotificationId == notificationId)
                .Select(item => item.UserGUID)
                .ToListAsync())
            .Order(StringComparer.Ordinal)
            .ToList();

        private static void AssertDataIsIdentifiersOnly(AppNotification notification, string approvalGuid, string sourceType)
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(notification.DataJson!)!;
            Assert.Equal(approvalGuid, data["approvalGuid"]);
            Assert.Equal(sourceType, data["sourceType"]);
            Assert.Equal("BRI", data["storeCode"]);
            Assert.Equal(3, data.Count);
        }

        private static void AssertNoSensitiveText(AppNotification notification, params string[] forbidden)
        {
            foreach (var text in forbidden)
            {
                Assert.DoesNotContain(text, notification.Title, StringComparison.Ordinal);
                Assert.DoesNotContain(text, notification.Body, StringComparison.Ordinal);
                Assert.DoesNotContain(text, notification.DataJson ?? string.Empty, StringComparison.Ordinal);
            }
        }

        private void AssertPublisherCalledOutsideTransactions()
        {
            Assert.NotNull(_approvalPublisherProbe);
            Assert.NotEmpty(_approvalPublisherProbe!.Calls);
            Assert.All(
                _approvalPublisherProbe.Calls,
                call => Assert.False(call.InTransaction, $"{call.Operation} 不能在业务事务未结束时调用"));
        }

        /// <summary>直接调用读接口背后的对账方法：入口快照里排班仍有效、锁内重读时已失效（GET 无法在测试里制造这个窗口）。</summary>
        private static async Task InvokeReconcileAsync(AttendanceReactService service, List<AttendanceSchedule> snapshot)
        {
            var method = typeof(AttendanceReactService).GetMethod(
                "ReconcileDerivedApprovalsUnderEmployeeLocksAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)method.Invoke(service, new object[] { snapshot })!;
        }

        private sealed class NoopRealtimeDispatcher : INotificationRealtimeDispatcher
        {
            public Task SendToUsersAsync(
                IReadOnlyCollection<string> userGuids,
                NotificationDto notification,
                CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        /// <summary>包住真实发布器，记录每次发布 / 撤回时连接上是否还有未结束的事务。</summary>
        private sealed class ApprovalPublisherProbe(INotificationPublisher inner, ISqlSugarClient db) : INotificationPublisher
        {
            public List<(string Operation, bool InTransaction)> Calls { get; } = [];

            public Task<NotificationPublishResult> PublishAsync(
                NotificationPublishRequest request,
                CancellationToken cancellationToken = default)
            {
                Calls.Add(($"Publish {request.DedupeKey}", db.Ado.IsAnyTran()));
                return inner.PublishAsync(request, cancellationToken);
            }

            public Task<bool> RevokeAsync(string dedupeKey, CancellationToken cancellationToken = default)
            {
                Calls.Add(($"Revoke {dedupeKey}", db.Ado.IsAnyTran()));
                return inner.RevokeAsync(dedupeKey, cancellationToken);
            }
        }

        /// <summary>发布前先执行一段动作（模拟并发的业务变化），再转交真实发布器。</summary>
        private sealed class BeforePublishHookPublisher(INotificationPublisher inner, Func<Task> beforePublish)
            : INotificationPublisher
        {
            public async Task<NotificationPublishResult> PublishAsync(
                NotificationPublishRequest request,
                CancellationToken cancellationToken = default)
            {
                await beforePublish();
                return await inner.PublishAsync(request, cancellationToken);
            }

            public Task<bool> RevokeAsync(string dedupeKey, CancellationToken cancellationToken = default) =>
                inner.RevokeAsync(dedupeKey, cancellationToken);
        }

        private sealed class ThrowingNotificationPublisher : INotificationPublisher
        {
            public Task<NotificationPublishResult> PublishAsync(
                NotificationPublishRequest request,
                CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("通知发布失败（测试注入）");

            public Task<bool> RevokeAsync(string dedupeKey, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("通知撤回失败（测试注入）");
        }

        private sealed class ThrowingApprovalNotifier : IAttendanceApprovalNotifier
        {
            public Task NotifySubmittedAsync(string sourceType, string sourceGuid) =>
                throw new InvalidOperationException("通知器失败（测试注入）");

            public Task NotifyDecidedAsync(AttendanceApproval approval) =>
                throw new InvalidOperationException("通知器失败（测试注入）");

            public Task RevokeSubmittedAsync(IEnumerable<string> approvalGuids) =>
                throw new InvalidOperationException("通知器失败（测试注入）");

            public Task RevokeSubmittedForSourcesAsync(string sourceType, IEnumerable<string> sourceGuids) =>
                throw new InvalidOperationException("通知器失败（测试注入）");
        }
    }
}
