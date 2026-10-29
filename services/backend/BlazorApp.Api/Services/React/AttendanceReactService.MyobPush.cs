using BlazorApp.Api.Services.Attendance;
using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.React
{
    // partial：给 MYOB Timesheet 推送供数（只读）。口径全部沿用工资表（同一个 BuildPayrollSnapshotAsync），这里只做取数与整理：
    // 年假只在提交时才有「财务核定工时」，核定值 ≤ 申请值；按天分配时从早到晚填，每天不超过当天申请的工时。
    public partial class AttendanceReactService : IAttendancePayrollPushSource
    {
        private const int PushUserChunk = 500;

        async Task<PayrollPushSettings> IAttendancePayrollPushSource.GetSettingsAsync()
        {
            var settings = await GetOrCreateSettingsModelAsync();
            return new PayrollPushSettings(settings.DefaultPeriodWeeks == 2 ? 2 : 1, settings.FortnightAnchorDate);
        }

        async Task<IReadOnlyList<(string UserGuid, string StoreCode)>> IAttendancePayrollPushSource.FindDataPairsAsync(
            IReadOnlyCollection<string> userGuids,
            DateTime periodStart,
            int weeks)
        {
            var until = periodStart.AddDays(weeks * 7);
            var pairs = new HashSet<(string, string)>();
            foreach (var chunk in userGuids.Chunk(PushUserChunk))
            {
                var guids = chunk.ToList();
                foreach (var item in await _db.Queryable<AttendanceSchedule>()
                             .Where(item => !item.IsDeleted && item.Status == "Active" && guids.Contains(item.UserGuid)
                                 && item.WorkDate >= periodStart && item.WorkDate < until)
                             .Select(item => new { item.UserGuid, item.StoreCode })
                             .ToListAsync())
                {
                    pairs.Add((item.UserGuid, item.StoreCode));
                }

                foreach (var item in await _db.Queryable<AttendanceLeaveRequest>()
                             .Where(item => !item.IsDeleted && item.Status == "Approved" && guids.Contains(item.UserGuid)
                                 && item.StartDate < until && item.EndDate >= periodStart)
                             .Select(item => new { item.UserGuid, item.StoreCode })
                             .ToListAsync())
                {
                    pairs.Add((item.UserGuid, item.StoreCode));
                }

                foreach (var item in await _db.Queryable<AttendancePublicHolidayPay>()
                             .Where(item => guids.Contains(item.UserGuid) && item.HolidayDate >= periodStart && item.HolidayDate < until
                                 && item.Status != PublicHolidayPayStatusCancelled)
                             .Select(item => new { item.UserGuid, item.StoreCode })
                             .ToListAsync())
                {
                    pairs.Add((item.UserGuid, item.StoreCode));
                }
            }

            return pairs.ToList();
        }

        async Task<PayrollPushStoreData> IAttendancePayrollPushSource.GetStoreDataAsync(
            string storeCode,
            DateTime periodStart,
            int weeks,
            IReadOnlyCollection<string> onlyUsers)
        {
            var snapshot = await BuildPayrollSnapshotAsync(storeCode, periodStart, weeks, includePending: false, mergeStores: false);
            var wanted = onlyUsers.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var employees = snapshot.Employees.Where(item => wanted.Contains(item.UserGuid)).ToList();
            var guids = employees.Select(item => item.UserGuid).ToList();
            var periodEnd = snapshot.PeriodEnd;

            var rates = guids.Count == 0
                ? new List<EmployeePayRate>()
                : await _db.Queryable<EmployeePayRate>()
                    .Where(item => guids.Contains(item.UserGuid) && item.EffectiveFrom <= periodEnd)
                    .ToListAsync();
            var approvals = await _db.Queryable<AttendancePayPeriodLeaveApproval>()
                .Where(item => item.StoreCode == storeCode && item.PeriodStart == snapshot.PeriodStart && item.Weeks == snapshot.Weeks)
                .ToListAsync();

            var result = new List<PayrollPushEmployee>();
            foreach (var employee in employees)
            {
                var annual = BuildAnnualDetail(employee, approvals, PaidAnnualLeaveType);
                var cashOut = BuildAnnualDetail(employee, approvals, AnnualCashOutLeaveType);
                result.Add(new PayrollPushEmployee(
                    employee.UserGuid,
                    employee.UserName,
                    employee.Birthday,
                    employee.AgeYears,
                    rates
                        .Where(item => item.UserGuid.Equals(employee.UserGuid, StringComparison.OrdinalIgnoreCase))
                        .Select(PayrollRate.From)
                        .ToList(),
                    employee.WorkItems.ToList(),
                    employee.SickItems.ToList(),
                    employee.PublicHolidayPayItems.ToList(),
                    DistributeApproved(employee.AnnualByDay, annual.ApprovedHours),
                    DistributeApproved(employee.CashOutByDay, cashOut.ApprovedHours),
                    employee.EmploymentType));
            }

            return new PayrollPushStoreData(
                storeCode,
                snapshot.StoreName ?? storeCode,
                snapshot.PeriodStatus == PayPeriodStatusSubmitted,
                result);
        }

        /// <summary>把财务核定的小时数按天分配：按日期从早到晚填，每天不超过当天申请的小时（核定不会超过申请）。</summary>
        internal static List<PayrollLeaveItem> DistributeApproved(SortedDictionary<DateTime, decimal> requestedByDay, decimal approvedHours)
        {
            var left = Math.Max(0m, approvedHours);
            var items = new List<PayrollLeaveItem>();
            foreach (var (date, requested) in requestedByDay)
            {
                if (left <= 0)
                {
                    break;
                }

                var take = Math.Min(requested, left);
                if (take > 0)
                {
                    items.Add(new PayrollLeaveItem(date, PayrollCalculator.Round2(take)));
                    left -= take;
                }
            }

            return items;
        }
    }
}
