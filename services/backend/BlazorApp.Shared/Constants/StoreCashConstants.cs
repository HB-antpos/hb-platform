namespace BlazorApp.Shared.Constants;

/// <summary>
/// 分店现金管理（存银行 / 现金支出 / 现金池）的取值与业务阈值。
/// 类别码、接口、界面、导出里 T2 一律只叫 T2，不出现其他叫法。
/// </summary>
public static class StoreCashConstants
{
    public static class RecordStatus
    {
        public const string Active = "Active";
        public const string Voided = "Voided";
    }

    public static class ExpenseCategory
    {
        public const string Salary = "Salary";
        public const string Purchase = "Purchase";
        public const string T2 = "T2";
        public const string Other = "Other";

        public static readonly IReadOnlyList<string> All = new[] { Salary, Purchase, T2, Other };

        public static bool IsValid(string? category) =>
            category is not null && All.Contains(category, StringComparer.Ordinal);
    }

    public static class ReviewStatus
    {
        public const string None = "None";
        public const string Reviewed = "Reviewed";
        public const string Flagged = "Flagged";

        public static bool IsValid(string? value) => value is None or Reviewed or Flagged;
    }

    public static class AttachmentStatus
    {
        /// <summary>已签发上传，对象在 cash/pending/ 下，尚未被业务单据确认。</summary>
        public const string Pending = "Pending";

        /// <summary>已校验并转正到私有正式路径，尚未挂到单据（提交失败重试时可复用）。</summary>
        public const string Promoted = "Promoted";

        /// <summary>已挂到存单或支出。</summary>
        public const string Linked = "Linked";
    }

    public static class AttachmentOwner
    {
        public const string Slip = "Slip";
        public const string Expense = "Expense";
    }

    public static class SelectionMode
    {
        /// <summary>按默认规则：同设备同营业日取 savedAt 最新的一份日结。</summary>
        public const string Default = "Default";

        /// <summary>手动多选若干份日结求和。</summary>
        public const string Manual = "Manual";
    }

    public static class BalanceEntryType
    {
        /// <summary>期初现金：每店至多一条有效，自该日起纳入现金池。</summary>
        public const string Opening = "Opening";

        /// <summary>盘点：某日结束时店里实际现金，与现金池余额比对。</summary>
        public const string Count = "Count";
    }

    public static class ErrorCodes
    {
        public const string InvalidRequest = "CASH_INVALID_REQUEST";
        public const string StoreNotFound = "CASH_STORE_NOT_FOUND";
        public const string StoreForbidden = "CASH_STORE_FORBIDDEN";
        public const string RecordNotFound = "CASH_RECORD_NOT_FOUND";
        public const string DateOutOfRange = "CASH_DATE_OUT_OF_RANGE";
        public const string AttachmentInvalid = "CASH_ATTACHMENT_INVALID";
        public const string AttachmentRequired = "CASH_ATTACHMENT_REQUIRED";
        public const string OverrideReasonRequired = "CASH_OVERRIDE_REASON_REQUIRED";
        public const string OpeningExists = "CASH_OPENING_EXISTS";
        public const string VoidNotAllowed = "CASH_VOID_NOT_ALLOWED";
        public const string CloseSourceUnavailable = "CASH_CLOSE_SOURCE_UNAVAILABLE";
        public const string CloseNotFound = "CASH_CLOSE_NOT_FOUND";
        public const string Conflict = "CASH_CONFLICT";
    }

    /// <summary>店长（无全部分店权限者）只能看到最近 N 天（含当天）的 T2。</summary>
    public const int ManagerT2VisibleDays = 14;

    /// <summary>店长补录存款、支出的发生日期最多回溯天数；更早的由有全部分店权限者补录。</summary>
    public const int ManagerMaxBackfillDays = 7;

    /// <summary>店长作废自己录入的存款、支出的时限（小时）。</summary>
    public const int ManagerSelfVoidHours = 24;

    /// <summary>超过多少天未存银行就标记为逾期。</summary>
    public const int DepositOverdueDays = 3;

    /// <summary>存款合计与建议存款额相差超过该金额，必须填写差异原因。</summary>
    public const decimal DepositDifferenceReasonThreshold = 20m;

    public const int MaxSlipsPerDeposit = 10;
    public const int MaxImagesPerSlip = 3;
    public const int MaxImagesPerExpense = 5;
    public const decimal MaxAmount = 10_000_000m;
}
