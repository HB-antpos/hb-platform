namespace BlazorApp.Shared.Constants
{
    /// <summary>
    /// 权限的「使用端」：该权限在哪些终端的界面或接口里生效。
    /// 同一个权限码可以同时服务多个终端（例如排班考勤大多数权限后台与移动端共用），所以是位标志。
    /// </summary>
    [Flags]
    public enum PermissionSurface
    {
        None = 0,

        /// <summary>收银端（WPF / 手持 / iPad 收银机）</summary>
        Pos = 1,

        /// <summary>Web 后台</summary>
        Web = 2,

        /// <summary>移动端 App</summary>
        Mobile = 4,
    }

    /// <summary>
    /// 权限码 → 使用端登记表（权威来源）。
    /// 为什么按码登记而不是按分类名推断：「POS 管理 / POS 日结 / POS 审计」等分类里混着后台和 App 页面的权限
    /// （如 PosProducts.*、DailyCloseRecords.View），分类名并不代表终端。
    /// 新增权限码必须在这里登记；PermissionSurfaceCatalogTests 会拦住遗漏。
    /// </summary>
    public static class PermissionSurfaceCatalog
    {
        /// <summary>收银端权限统一前缀：Permissions.PosTerminal.* 只下发给收银机，天然属于收银端。</summary>
        public const string PosTerminalPrefix = "Permissions.PosTerminal.";

        /// <summary>只在 Web 后台生效。</summary>
        private static readonly string[] WebOnlyCodes =
        {
            "Advertisements.Edit",
            "AustralianSuppliers.Edit",
            "Cash.AllStores.View",
            "Cash.Void",
            "Container.Create",
            "Container.Delete",
            "Container.Edit",
            "Dashboard",
            "DeviceRegistration.ActivationCodes.Manage",
            "DeviceRegistration.Manage",
            "DeviceRegistration.MobileActivationCodes.Manage",
            "DomesticPurchase.View",
            "EmployeeProfiles.EditPositionType",
            "FaceMatch.Traffic.View",
            "LegacyEmployeeLogs.View",
            "LocalPurchase.Edit",
            "LocalPurchase.PushToHq",
            "OrderFront.BatchProductSales.View",
            "Orders.Delete",
            "Orders.Edit",
            "PosProducts.Manage",
            "PosProducts.View",
            "Prices.Delete",
            "Prices.Modify",
            "Prices.View",
            "PricingStrategy.Edit",
            "Products.Create",
            "Products.Delete",
            "Promotions.Edit",
            "Reports.Export",
            "Roles.Delete",
            "SalesDashboard.BatchProductSales.View",
            "SalesDashboard.CompactBoard.View",
            "SalesDashboard.KfcRestockSignal.AllStores",
            "SalesDashboard.KfcRestockSignal.View",
            "SalesDashboard.LocalProductAnalysis.View",
            "SalesDashboard.LocalSupplierPurchaseSales.View",
            "SalesDashboard.MonthlyDailySalesDownload.View",
            "SalesDashboard.ProductMovement.View",
            "SalesDashboard.PurchaseAmount.View",
            "SalesDashboard.SalesData.View",
            "SalesDashboard.SalesDetail.View",
            "SeasonalCards.Remaining.ViewAllStores",
            "Store.ManageInfo",
            "StoreProducts.Create",
            "StoreProducts.Edit",
            "Stores.Create",
            "Stores.Delete",
            "Stores.Edit",
            "Stores.Sync",
            "SupplierPreorder.Manage",
            "SupplierPreorder.Order",
            "System.ManageAppDownloads",
            "System.ManagePerformanceBaseline",
            "System.ManageScheduledTasks",
            "System.ViewLogs",
            "System.ViewPerformanceBaseline",
            "Users.Delete",
            "Users.ViewWebConsole",
            "Warehouse.StartPicking",
            "Warehouse.StoreOrderReturn",
            "Warehouse.View",
        };

        /// <summary>只在移动端 App 生效。</summary>
        private static readonly string[] MobileOnlyCodes =
        {
            "Attendance.Availability.SubmitSelf",
            "Attendance.Leave.ApplySelf",
            "Attendance.Punch.AdjustManagedStore",
            "Attendance.Punch.Self",
            "Attendance.Schedule.ViewSelf",
            "Cash.Deposit.Create",
            "Cash.Expense.Create",
            "Cash.Overview.View",
            "CashRegisterUsers.MobileManage",
            "CashRegisterUsers.MobilePrint",
            "Container.MobileNewProductsView",
            "InstallmentOrders.View",
            "LocalPurchase.MobileView",
            "Orders.Create",
            "SalesOrders.View",
            "SeasonalProductInsights.View",
            "StoreProducts.EditName",
            "StoreProducts.PriceUpdates",
            "StoreProducts.SyncToOtherStores",
            "StoreVouchers.View",
            "System.ViewMobileAppInstallLinks",
            "Users.CreateStoreStaff",
            "Users.EditStoreStaff",
            "Users.ResetStoreStaffPassword",
            "Users.ViewStoreStaff",
            "Warehouse.Picking",
        };

        /// <summary>后台与移动端共用同一个权限码。</summary>
        private static readonly string[] WebAndMobileCodes =
        {
            "Advertisements.View",
            "Attendance.Admin.View",
            "Attendance.Approval.ReviewManagedStore",
            "Attendance.Approval.ReviewSelf",
            "Attendance.Approval.ViewManagedStore",
            "Attendance.Availability.ViewManagedStore",
            "Attendance.Holiday.EditManagedStore",
            "Attendance.Holiday.ViewStore",
            "Attendance.Leave.ReviewManagedStore",
            "Attendance.Leave.ViewManagedStore",
            "Attendance.Punch.ViewManagedStore",
            "Attendance.Schedule.EditManagedStore",
            "Attendance.Schedule.ViewStore",
            "Attendance.Settings.Edit",
            "AustralianSuppliers.View",
            "Container.View",
            "Container.ViewFullNumber",
            "DailyCloseRecords.View",
            "DeviceRegistration.View",
            "DomesticPurchase.ManagePrefixCodes",
            "DomesticPurchase.ManageProducts",
            "DomesticPurchase.ManageSuppliers",
            "EmployeeProfiles.Edit",
            "EmployeeProfiles.ReviewSensitiveManagedStore",
            "EmployeeProfiles.View",
            "FaceCapture.Cameras.Manage",
            "FaceCapture.Images.View",
            "FaceMatch.Manage",
            "FaceMatch.Unwelcome.Approve",
            "FaceMatch.Unwelcome.Manage",
            "FaceMatch.Unwelcome.Review",
            "LegacyEmployeeLogs.Review",
            "LocalPurchase.View",
            "OrderFront",
            "Orders.View",
            "PricingStrategy.View",
            "Products.Edit",
            "Products.View",
            "Promotions.View",
            "Reports.ProductMovement.View",
            "Reports.View",
            "Roles.Create",
            "Roles.Edit",
            "Roles.ManagePermissions",
            "Roles.ManageUsers",
            "Roles.View",
            "SalesDashboard.WarehouseFlow.View",
            "SeasonalCards.Remaining.SubmitManagedStore",
            "SeasonalCards.Remaining.ViewManagedStore",
            "Store.ManageOperations",
            "StoreProducts.View",
            "Stores.View",
            "System.ManageSettings",
            "System.ViewAppDownloads",
            "Users.Create",
            "Users.Edit",
            "Users.ManagePosTerminalPermissions",
            "Users.ManageRoles",
            "Users.ManageStores",
            "Users.ResetPassword",
            "Users.View",
            "Warehouse.Manage",
            "Warehouse.ManageCategories",
            "Warehouse.ManageLocations",
            "Warehouse.ManageOrders",
            "Warehouse.ManageProducts",
            // 撤销价格变更任务：Web「价格变更任务」页与移动端「价格和标签」共用。
            "Warehouse.CancelPriceUpdateTasks",
        };

        private static readonly IReadOnlyDictionary<string, PermissionSurface> Registered = BuildRegistry();

        private static Dictionary<string, PermissionSurface> BuildRegistry()
        {
            var registry = new Dictionary<string, PermissionSurface>(StringComparer.OrdinalIgnoreCase);
            foreach (var code in WebOnlyCodes) registry.Add(code, PermissionSurface.Web);
            foreach (var code in MobileOnlyCodes) registry.Add(code, PermissionSurface.Mobile);
            foreach (var code in WebAndMobileCodes)
                registry.Add(code, PermissionSurface.Web | PermissionSurface.Mobile);
            return registry;
        }

        /// <summary>该权限码是否已显式登记（收银端前缀视为已登记）。</summary>
        public static bool IsRegistered(string code) =>
            IsPosTerminalCode(code) || Registered.ContainsKey(code);

        /// <summary>
        /// 解析权限码的使用端。未登记的码（例如管理员在页面里手工新增的权限）默认按后台处理，
        /// 因为手工新增的权限只能出现在后台的权限管理里。
        /// </summary>
        public static PermissionSurface Resolve(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return PermissionSurface.Web;
            if (IsPosTerminalCode(code)) return PermissionSurface.Pos;
            return Registered.TryGetValue(code, out var surface) ? surface : PermissionSurface.Web;
        }

        /// <summary>给前端的稳定字符串：pos / web / mobile，顺序固定。</summary>
        public static List<string> ResolveNames(string? code)
        {
            var surface = Resolve(code);
            var names = new List<string>(3);
            if (surface.HasFlag(PermissionSurface.Pos)) names.Add("pos");
            if (surface.HasFlag(PermissionSurface.Web)) names.Add("web");
            if (surface.HasFlag(PermissionSurface.Mobile)) names.Add("mobile");
            return names;
        }

        private static bool IsPosTerminalCode(string code) =>
            code.StartsWith(PosTerminalPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
