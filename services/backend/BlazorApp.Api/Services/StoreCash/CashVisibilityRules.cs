using BlazorApp.Shared.Constants;

namespace BlazorApp.Api.Services.StoreCash;

/// <summary>
/// 现金管理里所有「谁能看、谁能录、谁能作废」的口径集中在这里。
/// T2 的可见性是权限规则而不是前端隐藏：列表、详情、汇总、按日明细、导出、附件下载都必须经过这里，
/// 任何新增的读取路径都要先问 <see cref="CanSeeExpense"/>。
/// </summary>
public static class CashVisibilityRules
{
    /// <summary>无全部分店权限者能看到的 T2 最早日期：门店今天往前 13 天，含当天共 14 天。</summary>
    public static DateOnly T2VisibleFrom(DateOnly storeToday) =>
        storeToday.AddDays(-(StoreCashConstants.ManagerT2VisibleDays - 1));

    public static bool CanSeeExpense(
        CashAccess access,
        string category,
        DateOnly expenseDate,
        DateOnly storeToday
    ) =>
        access.AllStores
        || !string.Equals(category, StoreCashConstants.ExpenseCategory.T2, StringComparison.Ordinal)
        || expenseDate >= T2VisibleFrom(storeToday);

    /// <summary>
    /// 录入、手选日结、期初与盘点允许的发生日期范围：不能晚于门店今天；
    /// 无全部分店权限者最多回溯 7 天，更早的由财务补录（防止倒填日期）。
    /// </summary>
    public static (DateOnly Earliest, DateOnly Latest) EntryDateRange(CashAccess access, DateOnly storeToday) =>
        access.AllStores
            ? (DateOnly.MinValue, storeToday)
            : (storeToday.AddDays(-StoreCashConstants.ManagerMaxBackfillDays), storeToday);

    public static bool IsEntryDateAllowed(CashAccess access, DateOnly date, DateOnly storeToday)
    {
        var (earliest, latest) = EntryDateRange(access, storeToday);
        return date >= earliest && date <= latest;
    }

    /// <summary>
    /// 作废：持有 Cash.Void 的可作废范围内任何记录；否则只有录入人本人、仍持有对应录入权限、且在时限内才能作废。
    /// </summary>
    public static bool CanVoid(
        CashAccess access,
        bool hasCreatePermission,
        string createdByUserGuid,
        DateTime createdAtUtc,
        DateTimeOffset now
    )
    {
        if (access.CanVoid)
        {
            return true;
        }

        return hasCreatePermission
            && !string.IsNullOrEmpty(createdByUserGuid)
            && string.Equals(createdByUserGuid, access.UserGuid, StringComparison.Ordinal)
            && now.UtcDateTime - createdAtUtc <= TimeSpan.FromHours(StoreCashConstants.ManagerSelfVoidHours);
    }
}
