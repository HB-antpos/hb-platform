using BlazorApp.Api.Services.Attendance;

namespace BlazorApp.Api.Services.Myob;

/// <summary>工资周期设置：周数与双周锚点（全局设置，所有分店一致）。</summary>
internal sealed record PayrollPushSettings(int Weeks, DateTime? FortnightAnchorDate);

/// <summary>某员工在某分店本期的计薪原料（只含本店的工时 / 请假，已按工资表同一口径算好）。</summary>
internal sealed record PayrollPushEmployee(
    string UserGuid,
    string UserName,
    DateTime? Birthday,
    int? AgeYears,
    IReadOnlyList<PayrollRate> Rates,
    IReadOnlyList<PayrollWorkItem> WorkItems,
    IReadOnlyList<PayrollLeaveItem> Sick,
    IReadOnlyList<PayrollLeaveItem> PublicHolidayPay,
    IReadOnlyList<PayrollLeaveItem> Annual,
    IReadOnlyList<PayrollLeaveItem> AnnualCashOut,
    // FullTime / PartTime / Casual；没填员工类型为 null。临时工的周末工时要推到单独的 MYOB 类别。
    string? EmploymentType = null);

internal sealed record PayrollPushStoreData(
    string StoreCode,
    string StoreName,
    bool IsLocked,
    IReadOnlyList<PayrollPushEmployee> Employees);

/// <summary>
/// 给 MYOB Timesheet 推送供数：从考勤工资模块取「某分店某周期每个员工按天的计薪工时」。
/// 与工资表同源（同一个快照构建器），所以推送工时与工资表口径一致；本接口只读，不改任何工资数据。
/// 实现在 <c>AttendanceReactService.MyobPush.cs</c>。
/// </summary>
internal interface IAttendancePayrollPushSource
{
    Task<PayrollPushSettings> GetSettingsAsync();

    /// <summary>这些员工本期在哪些分店有排班 / 请假 / 带薪公假：返回 (员工, 分店) 对，用来只加载有数据的分店。</summary>
    Task<IReadOnlyList<(string UserGuid, string StoreCode)>> FindDataPairsAsync(
        IReadOnlyCollection<string> userGuids, DateTime periodStart, int weeks);

    /// <summary>某分店本期数据，只返回 onlyUsers 里的员工。</summary>
    Task<PayrollPushStoreData> GetStoreDataAsync(
        string storeCode, DateTime periodStart, int weeks, IReadOnlyCollection<string> onlyUsers);
}
