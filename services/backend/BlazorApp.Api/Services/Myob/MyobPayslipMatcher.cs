using BlazorApp.Api.Models.Myob;

namespace BlazorApp.Api.Services.Myob;

/// <summary>某张工资单归到哪些分店（Kind 见 <see cref="MyobPayslipPlacements"/>），StoreCodes 已按店号排序。</summary>
internal sealed record MyobPayslipPlacement(string Kind, IReadOnlyList<string> StoreCodes);

/// <summary>当前账号能看哪些工资单：All = 全部分店口径（含未绑定 / 无法确定分店）；否则只看与 StoreCodes 有交集的已绑定员工。</summary>
internal sealed record MyobPayslipAccess(bool All, IReadOnlySet<string> StoreCodes)
{
    internal static MyobPayslipAccess Everything { get; } = new(true, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    internal static MyobPayslipAccess Stores(IEnumerable<string> storeCodes) =>
        new(false, new HashSet<string>(storeCodes, StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// 工资单 ↔ 员工 ↔ 分店 的归类规则（方案第 2.2 节）。纯函数，不访问数据库，便于单测。
/// MYOB 工资单只有「公司 + 员工卡」，没有分店，所以分店只能从我方数据推：绑定 → 周期内的考勤分店 → 支付日当天的发薪公司。
/// </summary>
internal static class MyobPayslipMatcher
{
    /// <summary>
    /// 找工资单对应的绑定：同公司文件、同员工卡 UID；优先当前有效绑定，没有则取最近解除的一条（解绑后历史工资单不能消失）。
    /// </summary>
    internal static EmployeeMyobLink? ResolveLink(IEnumerable<EmployeeMyobLink> links, string connectionGuid, string employeeUid)
    {
        var matched = links
            .Where(link => link.ConnectionGuid.Equals(connectionGuid, StringComparison.OrdinalIgnoreCase)
                && link.MyobEmployeeUid.Equals(employeeUid, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matched.FirstOrDefault(link => link.UnboundAt is null)
            ?? matched.OrderByDescending(link => link.UnboundAt ?? link.BoundAt).FirstOrDefault();
    }

    /// <summary>
    /// 分店归类（有序回退）：
    /// ① 该周期有考勤 / 请假 / 带薪公假数据的分店，且支付日当天该员工在该分店的发薪公司 = 本工资单的公司 → Attendance；
    /// ② 没有考勤数据：员工当前所属分店里，支付日当天发薪公司 = 本公司的分店 → Inferred；
    /// ③ 仍然没有 → Unknown。员工卡没有绑定的情况由调用方直接标 Unbound，不走这里。
    /// 「支付日当天」的发薪公司按 <see cref="MyobPayrollEmployerRules.Resolve"/>（分店映射 + 员工覆盖的生效日）算，
    /// 这样一年前的工资单仍归到当时的分店，而不是员工今天的分店。
    /// </summary>
    internal static MyobPayslipPlacement Place(
        string connectionGuid,
        DateTime paymentDate,
        IEnumerable<string> attendanceStores,
        IEnumerable<string> currentStores,
        IReadOnlyDictionary<string, string?> defaultConnectionByStore,
        IReadOnlyList<EmployeePayrollEmployer> overrideRowsOfUser)
    {
        bool PaidByThisCompany(string storeCode)
        {
            defaultConnectionByStore.TryGetValue(storeCode, out var defaultConnection);
            var rows = overrideRowsOfUser.Where(row => row.StoreCode.Equals(storeCode, StringComparison.OrdinalIgnoreCase));
            var resolved = MyobPayrollEmployerRules.Resolve(defaultConnection, rows, paymentDate);
            return string.Equals(resolved.ConnectionGuid, connectionGuid, StringComparison.OrdinalIgnoreCase);
        }

        static List<string> Distinct(IEnumerable<string> codes) =>
            codes.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(code => code, StringComparer.OrdinalIgnoreCase).ToList();

        var fromAttendance = Distinct(attendanceStores.Where(PaidByThisCompany));
        if (fromAttendance.Count > 0)
        {
            return new MyobPayslipPlacement(MyobPayslipPlacements.Attendance, fromAttendance);
        }

        var inferred = Distinct(currentStores.Where(PaidByThisCompany));
        return inferred.Count > 0
            ? new MyobPayslipPlacement(MyobPayslipPlacements.Inferred, inferred)
            : new MyobPayslipPlacement(MyobPayslipPlacements.Unknown, []);
    }

    /// <summary>
    /// 可见性：全部分店口径什么都能看；管辖分店口径只看「已绑定」且分店集合与管辖分店有交集的员工，
    /// 未绑定 / 无法确定分店的工资单对他们不可见（也不暴露存在与否）。
    /// </summary>
    internal static bool IsVisible(MyobPayslipAccess access, bool linked, MyobPayslipPlacement placement)
    {
        if (access.All)
        {
            return true;
        }

        return linked && placement.StoreCodes.Any(access.StoreCodes.Contains);
    }

    /// <summary>该工资单是否「无法归到分店」（未绑定或无法确定分店）。</summary>
    internal static bool IsUnassigned(string placementKind) =>
        placementKind is MyobPayslipPlacements.Unbound or MyobPayslipPlacements.Unknown;
}
