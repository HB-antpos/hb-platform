using System;
using System.Linq;
using System.Threading.Tasks;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Xunit;

namespace BlazorApp.Api.Tests
{
    /// <summary>
    /// 带薪假、带薪公假确认、工时统计 / 工资表与提交锁定（规格第 0.2、2 节）的服务层用例。
    /// 夹具共用 AttendanceReactServiceTests：BRI 门店按 Brisbane（UTC+10、无夏令时）算本地时间。
    /// 样例两周期 2026-10-05（周一，公共假期）~ 10-18；「今天」默认是 10-20 本地中午。
    /// </summary>
    public sealed partial class AttendanceReactServiceTests
    {
        private static readonly DateTime PayrollPeriodStart = new(2026, 10, 5);

        private const string OwnAttachmentUrl =
            "https://test-bucket-123.cos.ap-guangzhou.myqcloud.com/attendance/leave-attachments/staff-user/cert-1.jpg";

        private void SetStoreLocalNow(DateTime local) =>
            _timeProvider.SetUtcNow(DateTime.SpecifyKind(local.AddHours(-10), DateTimeKind.Utc));

        private async Task SeedPayrollStoreAsync(EmployeeType? staffType = EmployeeType.FullTime)
        {
            await SeedStoreScopeAsync();
            await SeedStoreManagerRoleAsync("manager-user");
            await SeedEmployeeProfileAsync("staff-user", staffType);
            await _db.Insertable(new AttendanceStoreHoliday
            {
                HolidayGuid = "kings-birthday",
                StoreCode = "BRI",
                HolidayDate = PayrollPeriodStart,
                HolidayName = "King's Birthday",
                IsPaidHoliday = true,
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            SetStoreLocalNow(new DateTime(2026, 10, 20, 12, 0, 0));
        }

        private async Task SeedWorkedScheduleAsync(string guid, DateTime workDate, string schedule, params string[] segments)
        {
            var range = schedule.Split('-');
            await SeedScheduleAsync(guid, "BRI", "staff-user", workDate, "Active", TimeSpan.Parse(range[0]), TimeSpan.Parse(range[1]));
            var index = 0;
            foreach (var segment in segments)
            {
                var times = segment.Split('-');
                await _db.Insertable(new[]
                {
                    CreateStoredPunchForSchedule($"{guid}-in-{index}", guid, "BRI", "staff-user", "ClockIn", workDate.Add(TimeSpan.Parse(times[0]))),
                    CreateStoredPunchForSchedule($"{guid}-out-{index}", guid, "BRI", "staff-user", "ClockOut", workDate.Add(TimeSpan.Parse(times[1]))),
                }).ExecuteCommandAsync();
                index++;
            }
        }

        private async Task SeedTransferRateAsync(decimal weekday, decimal saturday, decimal sunday, decimal publicHoliday)
        {
            await _db.Insertable(new EmployeePayRate
            {
                RateGuid = "rate-staff",
                UserGuid = "staff-user",
                PayMode = "Transfer",
                TransferWeekdayRate = weekday,
                TransferSaturdayRate = saturday,
                TransferSundayRate = sunday,
                TransferPublicHolidayRate = publicHoliday,
                EffectiveFrom = new DateTime(2026, 7, 1),
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
        }

        private AttendanceReactService Manager() => CreateService("manager-user", "manager", "StoreManager");
        private AttendanceReactService Staff() => CreateService("staff-user", "staff", "StoreStaff");

        private static AttendancePayrollQueryDto PayrollQuery() => new()
        {
            StoreCode = "BRI",
            PeriodStart = PayrollPeriodStart,
            Weeks = 2,
        };

        private static AttendancePayPeriodKeyDto PeriodKey() => new()
        {
            StoreCode = "BRI",
            PeriodStart = PayrollPeriodStart,
            Weeks = 2,
        };

        // ---------------- 工时统计与工资表 ----------------

        [Fact]
        public async Task 工时统计与工资表_只算排班窗口内工时_按档位与时薪计钱()
        {
            await SeedPayrollStoreAsync();
            // 10-06 周二：08:56–13:00、13:31–17:30（排班 09:00–17:30）→ 4 + 3.98 = 7.98 h（空档 31 分钟抵掉扣餐）。
            await SeedWorkedScheduleAsync("pay-tue", new DateTime(2026, 10, 6), "09:00-17:30", "08:56-13:00", "13:31-17:30");
            // 10-11 周日：09:00–12:30 → 3.5 h。
            await SeedWorkedScheduleAsync("pay-sun", new DateTime(2026, 10, 11), "09:00-12:30", "09:00-12:30");
            await SeedTransferRateAsync(30m, 35m, 40m, 60m);

            var hours = await Manager().GetPayrollHoursAsync(PayrollQuery());

            Assert.True(hours.Success, hours.Message);
            Assert.Equal(new DateTime(2026, 10, 18), hours.Data!.PeriodEnd);
            Assert.True(hours.Data.Days[0].IsPublicHoliday);
            Assert.Equal("King's Birthday", hours.Data.Days[0].HolidayName);
            var staff = Assert.Single(hours.Data.Employees);
            Assert.Equal("staff-user", staff.UserGuid);
            Assert.Equal("FullTime", staff.EmploymentType);
            Assert.Equal(7.98m, staff.Totals.Weekday);
            Assert.Equal(3.5m, staff.Totals.Sunday);
            Assert.Equal(11.48m, staff.Totals.Total);
            var tuesday = staff.Days.Single(item => item.Date == new DateTime(2026, 10, 6));
            Assert.Equal(479, tuesday.PaidMinutes);
            Assert.Equal(2, tuesday.Segments.Count);

            var pay = await Manager().GetPayrollPayAsync(PayrollQuery());

            Assert.True(pay.Success, pay.Message);
            var row = Assert.Single(pay.Data!.Rows);
            Assert.Equal("Transfer", row.PayMode);
            // 7.98 × 30 + 3.5 × 40
            Assert.Equal(379.40m, row.Transfer.Amount);
            Assert.Equal(379.40m, row.GrossPay);
            Assert.Equal("Open", pay.Data.PeriodStatus);
        }

        [Fact]
        public async Task 工资周期_待处理清零才能提交_锁定后写接口返回PERIOD_LOCKED_解锁后恢复()
        {
            await SeedPayrollStoreAsync();
            await SeedWorkedScheduleAsync("lock-tue", new DateTime(2026, 10, 6), "09:00-13:00", "09:00-13:00");
            await _db.Insertable(new AttendanceApproval
            {
                ApprovalGuid = "pending-punch",
                SourceType = "Punch",
                SourceGuid = "lock-tue-in-0",
                StoreCode = "BRI",
                ApplicantUserGuid = "staff-user",
                ReviewStatus = "Pending",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();

            var blocked = await Manager().SubmitPayPeriodAsync(PeriodKey());
            Assert.False(blocked.Success);
            Assert.Equal("PERIOD_HAS_PENDING", blocked.ErrorCode);

            await _db.Deleteable<AttendanceApproval>().Where(item => item.ApprovalGuid == "pending-punch").ExecuteCommandAsync();
            var submitted = await Manager().SubmitPayPeriodAsync(PeriodKey());
            Assert.True(submitted.Success, submitted.Message);
            Assert.Equal("Submitted", submitted.Data!.Status);
            Assert.Equal("PERIOD_ALREADY_SUBMITTED", (await Manager().SubmitPayPeriodAsync(PeriodKey())).ErrorCode);

            var schedule = await Manager().CreateScheduleAsync(new CreateAttendanceScheduleDto
            {
                StoreCode = "BRI",
                UserGuid = "staff-user",
                WorkDate = new DateTime(2026, 10, 7),
                StartTime = new TimeSpan(9, 0, 0),
                EndTime = new TimeSpan(13, 0, 0),
            });
            Assert.Equal(AttendanceReactService.PeriodLockedErrorCode, schedule.ErrorCode);
            var delete = await Manager().DeleteScheduleAsync("lock-tue");
            Assert.Equal(AttendanceReactService.PeriodLockedErrorCode, delete.ErrorCode);
            var note = await Manager().UpsertPayPeriodNoteAsync(new UpsertAttendancePayPeriodNoteDto
            {
                StoreCode = "BRI",
                PeriodStart = PayrollPeriodStart,
                Weeks = 2,
                UserGuid = "staff-user",
                Note = "补贴",
            });
            Assert.Equal(AttendanceReactService.PeriodLockedErrorCode, note.ErrorCode);
            // 时薪生效日早于锁定期末 → 会改动锁定期工资；生效日在期末之后则可以。
            var lockedRate = await Manager().CreatePayRateAsync(TransferRateRequest(new DateTime(2026, 10, 12)));
            Assert.Equal(AttendanceReactService.PeriodLockedErrorCode, lockedRate.ErrorCode);
            var laterRate = await Manager().CreatePayRateAsync(TransferRateRequest(new DateTime(2026, 10, 19)));
            Assert.True(laterRate.Success, laterRate.Message);
            // 锁定期外的日期不受影响。
            var outside = await Manager().CreateScheduleAsync(new CreateAttendanceScheduleDto
            {
                StoreCode = "BRI",
                UserGuid = "staff-user",
                WorkDate = new DateTime(2026, 10, 21),
                StartTime = new TimeSpan(9, 0, 0),
                EndTime = new TimeSpan(13, 0, 0),
            });
            Assert.True(outside.Success, outside.Message);

            var noReason = await Manager().UnlockPayPeriodAsync(new UnlockAttendancePayPeriodDto
            {
                StoreCode = "BRI", PeriodStart = PayrollPeriodStart, Weeks = 2, Reason = " ",
            });
            Assert.Equal("UNLOCK_REASON_REQUIRED", noReason.ErrorCode);
            var unlocked = await Manager().UnlockPayPeriodAsync(new UnlockAttendancePayPeriodDto
            {
                StoreCode = "BRI", PeriodStart = PayrollPeriodStart, Weeks = 2, Reason = "补一张漏打的卡",
            });
            Assert.True(unlocked.Success, unlocked.Message);
            Assert.Equal("Open", unlocked.Data!.Status);
            Assert.Equal("补一张漏打的卡", unlocked.Data.UnlockReason);
            Assert.True((await Manager().DeleteScheduleAsync("lock-tue")).Success);
        }

        [Fact]
        public async Task 工资周期_期起点必须对齐_否则INVALID_PERIOD()
        {
            await SeedPayrollStoreAsync();

            var result = await Manager().SubmitPayPeriodAsync(new AttendancePayPeriodKeyDto
            {
                StoreCode = "BRI",
                PeriodStart = new DateTime(2026, 10, 12),
                Weeks = 2,
            });

            Assert.Equal("INVALID_PERIOD", result.ErrorCode);
        }

        [Fact]
        public async Task 时薪表_只新增生效记录_同一生效日期不能重复_历史倒序()
        {
            await SeedPayrollStoreAsync();

            Assert.True((await Manager().CreatePayRateAsync(TransferRateRequest(new DateTime(2026, 7, 1)))).Success);
            var second = await Manager().CreatePayRateAsync(TransferRateRequest(new DateTime(2026, 10, 19), weekday: 31m));
            Assert.True(second.Success, second.Message);
            var duplicate = await Manager().CreatePayRateAsync(TransferRateRequest(new DateTime(2026, 10, 19)));
            Assert.Equal("RATE_EFFECTIVE_DATE_EXISTS", duplicate.ErrorCode);
            var invalid = await Manager().CreatePayRateAsync(new CreateEmployeePayRateDto
            {
                StoreCode = "BRI",
                UserGuid = "staff-user",
                PayMode = "Mixed",
                TransferWeekdayRate = 30m,
                CashRate = 25m,
                EffectiveFrom = new DateTime(2026, 11, 1),
            });
            Assert.Equal("INVALID_FIXED_TRANSFER_HOURS", invalid.ErrorCode);

            var history = await Manager().GetPayRateHistoryAsync("staff-user", "BRI");
            Assert.True(history.Success, history.Message);
            Assert.Equal(new[] { new DateTime(2026, 10, 19), new DateTime(2026, 7, 1) },
                history.Data!.Select(item => item.EffectiveFrom!.Value));
            var current = await Manager().GetPayRatesAsync(new AttendancePayRateQueryDto { StoreCode = "BRI" });
            // 今天 10-20：10-19 生效的那条已是当前记录。
            Assert.Equal(31m, current.Data!.Single(item => item.UserGuid == "staff-user").TransferWeekdayRate);
        }

        [Fact]
        public async Task 时薪表_公假转账价强制等于平日价_请求里传什么公假价都忽略()
        {
            await SeedPayrollStoreAsync();

            // TransferRateRequest 里公假价是 60、平日价 30：保存后以平日价为准。
            var created = await Manager().CreatePayRateAsync(TransferRateRequest(new DateTime(2026, 10, 19)));
            Assert.True(created.Success, created.Message);
            Assert.Equal(30m, created.Data!.TransferPublicHolidayRate);
            Assert.False(created.Data.PublicHolidayRateMismatch);
            var stored = await _db.Queryable<EmployeePayRate>().FirstAsync(item => item.RateGuid == created.Data.RateGuid);
            Assert.Equal(30m, stored.TransferPublicHolidayRate);

            // 转账 + 现金同样强制；全现金模式不用转账价，公假价存空。
            var mixed = await Manager().CreatePayRateAsync(new CreateEmployeePayRateDto
            {
                StoreCode = "BRI", UserGuid = "staff-user", PayMode = "Mixed", FixedTransferHoursPerWeek = 10m,
                TransferWeekdayRate = 32.5m, TransferPublicHolidayRate = 99m, CashRate = 25m,
                EffectiveFrom = new DateTime(2026, 11, 2),
            });
            Assert.True(mixed.Success, mixed.Message);
            Assert.Equal(32.5m, mixed.Data!.TransferPublicHolidayRate);

            var cash = await Manager().CreatePayRateAsync(new CreateEmployeePayRateDto
            {
                StoreCode = "BRI", UserGuid = "staff-user", PayMode = "Cash", TransferPublicHolidayRate = 99m, CashRate = 25m,
                EffectiveFrom = new DateTime(2026, 11, 9),
            });
            Assert.True(cash.Success, cash.Message);
            Assert.Null(cash.Data!.TransferPublicHolidayRate);
            Assert.False(cash.Data.PublicHolidayRateMismatch);
        }

        [Fact]
        public async Task 时薪表_历史记录公假价不一致_只标记不修改_列表可按不一致筛选()
        {
            await SeedPayrollStoreAsync();
            // 历史记录：平日 30、公假 60（旧规则）。保存新规则前已存在，不得被改动。
            await SeedTransferRateAsync(30m, 35m, 40m, 60m);

            var all = await Manager().GetPayRatesAsync(new AttendancePayRateQueryDto { StoreCode = "BRI" });
            var row = all.Data!.Single(item => item.UserGuid == "staff-user");
            Assert.Equal(60m, row.TransferPublicHolidayRate); // 历史值原样返回
            Assert.True(row.PublicHolidayRateMismatch);
            Assert.Equal(60m, (await _db.Queryable<EmployeePayRate>().FirstAsync(item => item.RateGuid == "rate-staff")).TransferPublicHolidayRate);

            var mismatchOnly = await Manager().GetPayRatesAsync(new AttendancePayRateQueryDto { StoreCode = "BRI", PublicHolidayMismatchOnly = true });
            Assert.Equal(["staff-user"], mismatchOnly.Data!.Select(item => item.UserGuid).ToArray());

            // 财务新建一条从下一个未锁定周期起生效的记录后，当前记录仍是旧的（未到生效日），再往后才变一致。
            var fixedRate = await Manager().CreatePayRateAsync(TransferRateRequest(new DateTime(2026, 10, 19)));
            Assert.True(fixedRate.Success, fixedRate.Message);
            var afterFix = await Manager().GetPayRatesAsync(new AttendancePayRateQueryDto { StoreCode = "BRI", PublicHolidayMismatchOnly = true });
            Assert.Empty(afterFix.Data!); // 今天 10-20，10-19 的新记录已是当前记录且一致
        }

        [Fact]
        public void 时薪表_不一致判定_全现金不算_一边为空算()
        {
            static EmployeePayRate Rate(string mode, decimal? weekday, decimal? holiday) => new()
            {
                PayMode = mode, TransferWeekdayRate = weekday, TransferPublicHolidayRate = holiday,
            };

            Assert.False(AttendanceReactService.HasPublicHolidayRateMismatch(Rate("Cash", null, 60m)));
            Assert.False(AttendanceReactService.HasPublicHolidayRateMismatch(Rate("Transfer", 30m, 30m)));
            Assert.True(AttendanceReactService.HasPublicHolidayRateMismatch(Rate("Transfer", 30m, 60m)));
            Assert.True(AttendanceReactService.HasPublicHolidayRateMismatch(Rate("Mixed", 30m, null)));
        }

        [Fact]
        public async Task MYOB推送供数_与工资表同源_转账工时与工资表一致_周期锁定状态随提交变化()
        {
            await SeedPayrollStoreAsync();
            await SeedWorkedScheduleAsync("pay-tue", new DateTime(2026, 10, 6), "09:00-17:30", "08:56-13:00", "13:31-17:30");
            await SeedWorkedScheduleAsync("pay-sun", new DateTime(2026, 10, 11), "09:00-12:30", "09:00-12:30");
            // 转账 + 现金：每周固定转账 4h × 2 周 = 8h；按 周日 → 平日 装入：周日 3.5h + 平日 4.5h
            await _db.Insertable(new EmployeePayRate
            {
                RateGuid = "rate-mixed", UserGuid = "staff-user", PayMode = "Mixed", FixedTransferHoursPerWeek = 4m,
                TransferWeekdayRate = 30m, TransferSaturdayRate = 35m, TransferSundayRate = 40m, TransferPublicHolidayRate = 30m,
                CashRate = 25m, EffectiveFrom = new DateTime(2026, 7, 1), CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();

            var source = (BlazorApp.Api.Services.Myob.IAttendancePayrollPushSource)Manager();
            var settings = await source.GetSettingsAsync();
            Assert.True(settings.Weeks is 1 or 2);

            var pairs = await source.FindDataPairsAsync(["staff-user", "nobody"], PayrollPeriodStart, 2);
            Assert.Equal([("staff-user", "BRI")], pairs.ToArray());

            var data = await source.GetStoreDataAsync("BRI", PayrollPeriodStart, 2, ["staff-user"]);
            Assert.False(data.IsLocked);
            var employee = Assert.Single(data.Employees);
            Assert.Equal("staff-user", employee.UserGuid);
            Assert.Equal(479, employee.WorkItems.Where(item => item.Bucket == BlazorApp.Api.Services.Attendance.PayrollBucket.Weekday).Sum(item => item.Minutes));
            Assert.Equal(210, employee.WorkItems.Where(item => item.Bucket == BlazorApp.Api.Services.Attendance.PayrollBucket.Sunday).Sum(item => item.Minutes));
            Assert.Single(employee.Rates);

            // 与工资表同源：规划器算出的转账工时 = 工资表「转账」小时（同一批工时、同一个固定转账额度）
            var items = employee.WorkItems
                .Select(item => new BlazorApp.Api.Services.Myob.MyobWorkItem(
                    "conn", "BRI", item.Date,
                    item.Bucket switch
                    {
                        BlazorApp.Api.Services.Attendance.PayrollBucket.Sunday => BlazorApp.Api.Services.Myob.MyobWorkKind.Sunday,
                        BlazorApp.Api.Services.Attendance.PayrollBucket.Saturday => BlazorApp.Api.Services.Myob.MyobWorkKind.Saturday,
                        BlazorApp.Api.Services.Attendance.PayrollBucket.PublicHoliday => BlazorApp.Api.Services.Myob.MyobWorkKind.PublicHolidayWork,
                        _ => BlazorApp.Api.Services.Myob.MyobWorkKind.Weekday,
                    },
                    item.Minutes))
                .ToList();
            var plan = BlazorApp.Api.Services.Myob.MyobTimesheetPlanner.Plan("Mixed", 4m, 2, items).Companies["conn"];
            var pay = await Manager().GetPayrollPayAsync(PayrollQuery());
            var row = Assert.Single(pay.Data!.Rows);
            Assert.Equal(row.Transfer.Hours, BlazorApp.Api.Services.Attendance.PayrollCalculator.RoundHours(plan.PushedMinutes));
            Assert.Equal(row.Transfer.Buckets.Sunday, BlazorApp.Api.Services.Attendance.PayrollCalculator.RoundHours(plan.Pushed.Where(line => line.CategoryKey == "sunday").Sum(line => line.Minutes)));

            // 提交锁定后供数里的 IsLocked 变为 true
            Assert.True((await Manager().SubmitPayPeriodAsync(PeriodKey())).Success);
            var locked = await source.GetStoreDataAsync("BRI", PayrollPeriodStart, 2, ["staff-user"]);
            Assert.True(locked.IsLocked);
        }

        private static CreateEmployeePayRateDto TransferRateRequest(DateTime effectiveFrom, decimal weekday = 30m) => new()
        {
            StoreCode = "BRI",
            UserGuid = "staff-user",
            PayMode = "Transfer",
            TransferWeekdayRate = weekday,
            TransferSaturdayRate = 35m,
            TransferSundayRate = 40m,
            TransferPublicHolidayRate = 60m,
            EffectiveFrom = effectiveFrom,
        };

        [Fact]
        public async Task 年假核定_工资按核定工时_不能超过申请工时()
        {
            await SeedPayrollStoreAsync();
            await SeedTransferRateAsync(30m, 35m, 40m, 60m);
            // 年假休息 10-05 ~ 10-08：10-05 是公假不计，申请 3 × 8 = 24 h。
            await _db.Insertable(new AttendanceLeaveRequest
            {
                LeaveGuid = "annual-approved",
                StoreCode = "BRI",
                UserGuid = "staff-user",
                LeaveType = "PaidAnnual",
                StartDate = new DateTime(2026, 10, 5),
                EndDate = new DateTime(2026, 10, 8),
                PaidHours = 24m,
                Status = "Approved",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();

            var before = await Manager().GetPayrollPayAsync(PayrollQuery());
            Assert.Equal(24m, before.Data!.Rows.Single().LeaveDetail.Annual.RequestedHours);
            Assert.Equal(720m, before.Data.Rows.Single().GrossPay);

            var tooMany = await Manager().UpsertPayPeriodLeaveApprovalAsync(new UpsertAttendancePayPeriodLeaveApprovalDto
            {
                StoreCode = "BRI", PeriodStart = PayrollPeriodStart, Weeks = 2,
                UserGuid = "staff-user", LeaveType = "PaidAnnual", ApprovedHours = 30m,
            });
            Assert.Equal("INVALID_APPROVED_HOURS", tooMany.ErrorCode);
            var approved = await Manager().UpsertPayPeriodLeaveApprovalAsync(new UpsertAttendancePayPeriodLeaveApprovalDto
            {
                StoreCode = "BRI", PeriodStart = PayrollPeriodStart, Weeks = 2,
                UserGuid = "staff-user", LeaveType = "PaidAnnual", ApprovedHours = 20m,
            });
            Assert.True(approved.Success, approved.Message);

            var after = await Manager().GetPayrollPayAsync(PayrollQuery());
            var row = after.Data!.Rows.Single();
            Assert.True(row.LeaveDetail.Annual.IsFinanceApproved);
            Assert.Equal(20m, row.Hours.Leave);
            Assert.Equal(600m, row.GrossPay);
            // 工时统计仍显示申请工时。
            var hours = await Manager().GetPayrollHoursAsync(PayrollQuery());
            Assert.Equal(24m, hours.Data!.Employees.Single().Totals.Annual);
        }

        // ---------------- 带薪假申请 ----------------

        [Fact]
        public async Task 带薪假_临时工申请被拒PAID_LEAVE_NOT_ELIGIBLE()
        {
            await SeedPayrollStoreAsync(EmployeeType.Temporary);

            var result = await Staff().CreateMyLeaveRequestAsync(new CreateAttendanceLeaveRequestDto
            {
                StoreCode = "BRI",
                LeaveType = "PaidAnnual",
                StartDate = new DateTime(2026, 11, 17),
                EndDate = new DateTime(2026, 11, 20),
            });

            Assert.False(result.Success);
            Assert.Equal("PAID_LEAVE_NOT_ELIGIBLE", result.ErrorCode);
        }

        [Fact]
        public async Task 带薪年假休息_提前与时长校验_工时按工作日乘8_批准写不能上班_撤销清除()
        {
            await SeedPayrollStoreAsync();

            var tooSoon = await Staff().CreateMyLeaveRequestAsync(AnnualRequest(new DateTime(2026, 11, 10), new DateTime(2026, 11, 13)));
            Assert.Equal("ANNUAL_LEAVE_LEAD_TIME", tooSoon.ErrorCode);
            var tooLong = await Staff().CreateMyLeaveRequestAsync(AnnualRequest(new DateTime(2026, 11, 17), new DateTime(2026, 12, 1)));
            Assert.Equal("ANNUAL_LEAVE_TOO_LONG", tooLong.ErrorCode);

            // 设计稿：11-17 周二 ~ 11-28 周六，工作日 9 天 × 8 h = 72 h。
            var created = await Staff().CreateMyLeaveRequestAsync(AnnualRequest(new DateTime(2026, 11, 17), new DateTime(2026, 11, 28)));
            Assert.True(created.Success, created.Message);
            Assert.Equal(72m, created.Data!.PaidHours);
            var approval = await _db.Queryable<AttendanceApproval>()
                .FirstAsync(item => item.SourceType == "Leave" && item.SourceGuid == created.Data.LeaveGuid);
            var approve = await Manager().ApproveAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto());
            Assert.True(approve.Success, approve.Message);

            var unavailable = await _db.Queryable<AttendanceAvailability>()
                .Where(item => item.SourceLeaveGuid == created.Data.LeaveGuid && !item.IsDeleted)
                .ToListAsync();
            Assert.Equal(12, unavailable.Count);
            Assert.All(unavailable, item => Assert.True(item.IsUnavailable));
            var blocked = await Manager().CreateScheduleAsync(new CreateAttendanceScheduleDto
            {
                StoreCode = "BRI",
                UserGuid = "staff-user",
                WorkDate = new DateTime(2026, 11, 18),
                StartTime = new TimeSpan(9, 0, 0),
                EndTime = new TimeSpan(13, 0, 0),
            });
            Assert.Equal("ANNUAL_LEAVE_UNAVAILABLE", blocked.ErrorCode);

            var revoked = await Manager().RevokeManagedLeaveRequestAsync(created.Data.LeaveGuid, new ReviewAttendanceApprovalDto { ReviewRemark = "改期" });
            Assert.True(revoked.Success, revoked.Message);
            Assert.Equal("Revoked", await _db.Queryable<AttendanceLeaveRequest>()
                .Where(item => item.LeaveGuid == created.Data.LeaveGuid).Select(item => item.Status).FirstAsync());
            Assert.False(await _db.Queryable<AttendanceAvailability>()
                .AnyAsync(item => item.SourceLeaveGuid == created.Data.LeaveGuid && !item.IsDeleted));
            var allowed = await Manager().CreateScheduleAsync(new CreateAttendanceScheduleDto
            {
                StoreCode = "BRI",
                UserGuid = "staff-user",
                WorkDate = new DateTime(2026, 11, 18),
                StartTime = new TimeSpan(9, 0, 0),
                EndTime = new TimeSpan(13, 0, 0),
            });
            Assert.True(allowed.Success, allowed.Message);
        }

        private static CreateAttendanceLeaveRequestDto AnnualRequest(DateTime start, DateTime end) => new()
        {
            StoreCode = "BRI",
            LeaveType = "PaidAnnual",
            StartDate = start,
            EndDate = end,
        };

        [Fact]
        public async Task 带薪病假_有排班没打卡且4天内_医嘱在本人目录_工时为排班扣餐()
        {
            await SeedPayrollStoreAsync();
            await SeedScheduleAsync("sick-ok", "BRI", "staff-user", new DateTime(2026, 10, 19), "Active", new TimeSpan(9, 0, 0), new TimeSpan(15, 0, 0));
            await SeedScheduleAsync("sick-old", "BRI", "staff-user", new DateTime(2026, 10, 14), "Active", new TimeSpan(9, 0, 0), new TimeSpan(15, 0, 0));
            await SeedWorkedScheduleAsync("sick-worked", new DateTime(2026, 10, 18), "09:00-13:00", "09:00-13:00");

            CreateAttendanceLeaveRequestDto Sick(string scheduleGuid, params string[] urls) => new()
            {
                StoreCode = "BRI",
                LeaveType = "PaidSick",
                ScheduleGuids = new() { scheduleGuid },
                AttachmentUrls = urls.ToList(),
                Reason = "发烧，看了 GP",
            };

            Assert.Equal("ATTACHMENT_REQUIRED", (await Staff().CreateMyLeaveRequestAsync(Sick("sick-ok"))).ErrorCode);
            Assert.Equal("INVALID_ATTACHMENT_URL", (await Staff().CreateMyLeaveRequestAsync(Sick("sick-ok",
                "https://test-bucket-123.cos.ap-guangzhou.myqcloud.com/attendance/leave-attachments/manager-user/cert.jpg"))).ErrorCode);
            Assert.Equal("SICK_LEAVE_SUBMIT_WINDOW", (await Staff().CreateMyLeaveRequestAsync(Sick("sick-old", OwnAttachmentUrl))).ErrorCode);
            Assert.Equal("SICK_LEAVE_SCHEDULE_PUNCHED", (await Staff().CreateMyLeaveRequestAsync(Sick("sick-worked", OwnAttachmentUrl))).ErrorCode);

            var created = await Staff().CreateMyLeaveRequestAsync(Sick("sick-ok", OwnAttachmentUrl));
            Assert.True(created.Success, created.Message);
            Assert.Equal(5.5m, created.Data!.PaidHours);
            Assert.Equal(new DateTime(2026, 10, 19), created.Data.StartDate);
            Assert.Equal(new[] { "sick-ok" }, created.Data.ScheduleGuids);
            Assert.Equal(new[] { OwnAttachmentUrl }, created.Data.AttachmentUrls);
            Assert.Equal(OwnAttachmentUrl, created.Data.AttachmentUrl);

            // 审核列表一屏：排班、打卡、医嘱。
            var pending = await Manager().GetPendingApprovalsAsync(new AttendanceApprovalQueryDto { StoreCode = "BRI", SourceType = "Leave" });
            var leave = Assert.Single(pending.Data!).Leave;
            Assert.NotNull(leave);
            Assert.Equal("PaidSick", leave!.LeaveType);
            var scheduleRow = Assert.Single(leave.Schedules);
            Assert.False(scheduleRow.HasPunch);
            Assert.Equal(5.5m, scheduleRow.PaidHours);
        }

        [Fact]
        public async Task 请假附件签名_员工本人只能拿到本人目录下的对象键()
        {
            await SeedPayrollStoreAsync();
            _deniedPermissions.Add(Permissions.Attendance.Leave.ReviewManagedStore);

            var result = await Staff().GetLeaveAttachmentUploadSignatureAsync(new DirectUploadRequest
            {
                FileName = "cert.jpg",
                ContentType = "image/jpeg",
                ObjectKey = "attendance/leave-attachments/manager-user/stolen.jpg",
            });

            Assert.True(result.Success, result.Message);
            Assert.StartsWith("attendance/leave-attachments/staff-user/", result.Data!.ObjectKey);

            _deniedPermissions.Add(Permissions.Attendance.Leave.ApplySelf);
            var forbidden = await Staff().GetLeaveAttachmentUploadSignatureAsync(new DirectUploadRequest { FileName = "cert.jpg" });
            Assert.Equal("FORBIDDEN", forbidden.ErrorCode);
        }

        // ---------------- 带薪公假确认 ----------------

        [Fact]
        public async Task 带薪公假确认_过去10周同一星期几排班满7次且当天没排班_打开审核列表时生成_批准填工时()
        {
            await SeedPayrollStoreAsync();
            // 公假 10-05 周一之前的 7 个周一都有 09:00–17:00 排班（8 h 扣一次餐 → 建议 7.5 h）。
            for (var week = 1; week <= 7; week++)
            {
                await SeedScheduleAsync($"monday-{week}", "BRI", "staff-user", PayrollPeriodStart.AddDays(-7 * week), "Active");
            }
            SetStoreLocalNow(new DateTime(2026, 10, 6, 12, 0, 0));

            var pending = await Manager().GetPendingApprovalsAsync(new AttendanceApprovalQueryDto { StoreCode = "BRI" });

            Assert.True(pending.Success, pending.Message);
            var approval = Assert.Single(pending.Data!, item => item.SourceType == AttendanceReactService.PublicHolidayPaySourceType);
            Assert.Equal(PayrollPeriodStart, approval.WorkDate);
            Assert.Equal(7, approval.PublicHolidayPay!.OrdinaryCount);
            Assert.Equal(7.5m, approval.PublicHolidayPay.SuggestedHours);
            Assert.Equal("King's Birthday", approval.PublicHolidayPay.HolidayName);
            // 再打开一次不会重复生成。
            await Manager().GetPendingApprovalsAsync(new AttendanceApprovalQueryDto { StoreCode = "BRI" });
            Assert.Equal(1, await _db.Queryable<AttendancePublicHolidayPay>().CountAsync());

            var invalid = await Manager().ApproveAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto { ApprovedPaidHours = 30m });
            Assert.Equal("INVALID_PAID_HOURS", invalid.ErrorCode);
            var approved = await Manager().ApproveAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto { ApprovedPaidHours = 7.6m });
            Assert.True(approved.Success, approved.Message);
            var record = await _db.Queryable<AttendancePublicHolidayPay>().FirstAsync();
            Assert.Equal("Confirmed", record.Status);
            Assert.Equal(7.6m, record.Hours);

            SetStoreLocalNow(new DateTime(2026, 10, 20, 12, 0, 0));
            var hours = await Manager().GetPayrollHoursAsync(PayrollQuery());
            var staff = hours.Data!.Employees.Single(item => item.UserGuid == "staff-user");
            Assert.Equal(7.6m, staff.Totals.PublicHolidayPay);
            Assert.Equal("Confirmed", staff.Days[0].PublicHolidayPay!.State);
        }

        [Fact]
        public async Task 带薪公假确认_次数不够或当天有排班_不生成()
        {
            await SeedPayrollStoreAsync();
            for (var week = 1; week <= 6; week++)
            {
                await SeedScheduleAsync($"monday-{week}", "BRI", "staff-user", PayrollPeriodStart.AddDays(-7 * week), "Active");
            }

            var hours = await Manager().GetPayrollHoursAsync(PayrollQuery());

            Assert.True(hours.Success, hours.Message);
            Assert.False(await _db.Queryable<AttendancePublicHolidayPay>().AnyAsync());
            Assert.DoesNotContain(hours.Data!.Alerts, item => item.Type == "PublicHolidayPay");
        }

        [Fact]
        public async Task 考勤设置_新参数可读可改_旧客户端不带时保持原值()
        {
            await SeedPayrollStoreAsync();
            var admin = CreateService("admin-user", "admin", "Admin");

            var updated = await admin.UpdateSettingsAsync(new UpdateAttendanceSettingsDto
            {
                DefaultPeriodWeeks = 2,
                FortnightAnchorDate = new DateTime(2026, 10, 7),
                SickLeaveSubmitDays = 5,
            });

            Assert.True(updated.Success, updated.Message);
            Assert.Equal(2, updated.Data!.DefaultPeriodWeeks);
            // 双周起始日存为所在周的周一。
            Assert.Equal(new DateTime(2026, 10, 5), updated.Data.FortnightAnchorDate);
            Assert.Equal(5, updated.Data.SickLeaveSubmitDays);
            Assert.Equal(4, updated.Data.AnnualLeaveLeadWeeks);
            Assert.Equal(10, updated.Data.PublicHolidayLookbackWeeks);
            Assert.Equal(7, updated.Data.PublicHolidayMinCount);
        }
    }
}
