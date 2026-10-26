namespace BlazorApp.Shared.Constants
{
    /// <summary>
    /// 系统权限常量定义
    /// 命名规范: {模块}.{操作} (PascalCase)
    /// 前后端共享此规范，前端 hasPermission('Users.View') 对齐后端 [Authorize(Policy = "Users.View")]
    /// </summary>
    public static class Permissions
    {
        public static readonly string[] SuperAdminRoleNames =
            ["Admin", "管理员", "SuperAdmin", "超级管理员"];

        public static readonly string[] StoreManagerRoleNames = ["StoreManager", "店长", "经理"];

        public static readonly string[] WarehouseManagerRoleNames =
            ["WarehouseManager", "仓库经理", "Warehouse", "仓库管理员", "WarehouseAdmin"];

        // 员工角色存在中英文历史别名；普通 User 不是可由店长委派的员工角色。
        public static readonly string[] EmployeeRoleNames =
            ["StoreStaff", "Employee", "店铺员工", "店员", "员工"];

        public static readonly string[] HighPrivilegeRoleNames = SuperAdminRoleNames
            .Concat(StoreManagerRoleNames)
            .Concat(WarehouseManagerRoleNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        public static class Users
        {
            public const string View = "Users.View";
            // Web 后台 /system/users 菜单与页面单独授权；移动端「员工列表」「用户管理」及用户接口仍由 Users.View 控制。
            public const string ViewWebConsole = "Users.ViewWebConsole";
            public const string Create = "Users.Create";
            public const string Edit = "Users.Edit";
            public const string Delete = "Users.Delete";
            public const string ManageRoles = "Users.ManageRoles";
            public const string ManageStores = "Users.ManageStores";
            public const string ManagePosTerminalPermissions =
                "Users.ManagePosTerminalPermissions";
            public const string ResetPassword = "Users.ResetPassword";

            // 移动端「员工列表」只管理本人主分店的普通店员（/react/v1/store-users）。
            // 与上面的全局用户权限分开：店长只授予这三项，Web 全局用户接口仍不可用。
            // 原全局权限通过别名自动包含对应店员权限，已授权的角色无需重新授权。
            // 「员工管理」入口与店员列表/详情的查看权限，与「用户管理」入口的 Users.View 分开授权；
            // Users.View 持有者通过别名自动满足，已授权角色无需重新授权。
            public const string ViewStoreStaff = "Users.ViewStoreStaff";
            public const string CreateStoreStaff = "Users.CreateStoreStaff";
            public const string EditStoreStaff = "Users.EditStoreStaff";
            public const string ResetStoreStaffPassword = "Users.ResetStoreStaffPassword";
        }

        public static class EmployeeProfiles
        {
            public const string View = "EmployeeProfiles.View";
            public const string Edit = "EmployeeProfiles.Edit";
            public const string EditPositionType = "EmployeeProfiles.EditPositionType";
            public const string ReviewSensitiveManagedStore =
                "EmployeeProfiles.ReviewSensitiveManagedStore";
        }

        public static class Roles
        {
            public const string View = "Roles.View";
            public const string Create = "Roles.Create";
            public const string Edit = "Roles.Edit";
            public const string Delete = "Roles.Delete";
            public const string ManagePermissions = "Roles.ManagePermissions";
            public const string ManageUsers = "Roles.ManageUsers";
        }

        public static class Stores
        {
            public const string View = "Stores.View";
            public const string Create = "Stores.Create";
            public const string Edit = "Stores.Edit";
            public const string Delete = "Stores.Delete";
            public const string Sync = "Stores.Sync";
        }

        public static class Products
        {
            public const string View = "Products.View";
            public const string Create = "Products.Create";
            public const string Edit = "Products.Edit";
            public const string Delete = "Products.Delete";
        }

        public static class PosProducts
        {
            public const string View = "PosProducts.View";
            public const string Manage = "PosProducts.Manage";
        }

        /// <summary>
        /// 旧版收银系统（POSM.EmployeeLogs）的员工操作日志只读查询。
        /// 刻意不放在 Permissions.PosTerminal.* 下：该前缀会被当作收银端权限下发到收银机与收银员授权。
        /// </summary>
        public static class LegacyEmployeeLogs
        {
            public const string View = "LegacyEmployeeLogs.View";

            /// <summary>
            /// 对命中异常规则的操作标记「确认正常 / 需跟进」或撤销；只读用户看得到结论但不能操作。
            /// 老收银与新收银共用这一个核查权限（新收银不能另设 PosTerminal.* 权限，见上）。
            /// </summary>
            public const string Review = "LegacyEmployeeLogs.Review";
        }

        /// <summary>
        /// 日结记录：Web 页面 /pos-admin/daily-closes 与员工 App「日结记录」，只读查看 WPF、手持、iPad 上传的日结与现金盘点明细。
        /// 刻意不放在 Permissions.PosTerminal.* 下：该前缀会被当作收银端权限下发到收银机与收银员授权
        /// （PosTerminal.DailyClose.* 是收银机上操作日结的权限，与后台查看记录无关）。
        /// </summary>
        public static class DailyCloseRecords
        {
            public const string View = "DailyCloseRecords.View";
        }

        /// <summary>
        /// 门店人脸抓拍：Web 后台 /system/face-cameras，管理抓拍摄像机（新增、停用、轮换 token、查看最近抓拍元数据）。
        /// 只给管理员（Admin 角色靠角色放行，不挂任何角色模板）；刻意不放在 Permissions.PosTerminal.* 下。
        /// </summary>
        public static class FaceCapture
        {
            public static class Cameras
            {
                public const string Manage = "FaceCapture.Cameras.Manage";
            }

            /// <summary>
            /// 查看人脸抓拍图片（敏感生物识别信息）：与 <see cref="Cameras.Manage"/> 完全分开授予，
            /// 管理摄像机不自动包含查看图片；Admin 角色靠角色放行，其他账号须由管理员显式授予。
            /// </summary>
            public static class Images
            {
                public const string View = "FaceCapture.Images.View";
            }
        }

        /// <summary>
        /// 门店人脸比对服务：Web 后台 /system/face-match（门店档案、员工同意与登记、客流统计、不受欢迎人员名单）。
        /// 全部只给管理员（Admin 角色靠角色放行，不挂任何角色模板）；刻意不放在 Permissions.PosTerminal.* 下。
        /// 五个码互相独立：管理档案不自动包含看客流，批准条目的人不自动能复核命中。
        /// </summary>
        public static class FaceMatch
        {
            /// <summary>门店档案与闸门、员工同意、员工登记与撤回、审计查询。</summary>
            public const string Manage = "FaceMatch.Manage";

            public static class Traffic
            {
                /// <summary>客流统计页（计数，没有任何生物信息）。</summary>
                public const string View = "FaceMatch.Traffic.View";
            }

            public static class Unwelcome
            {
                /// <summary>提交、查看、撤回不受欢迎人员名单条目。</summary>
                public const string Manage = "FaceMatch.Unwelcome.Manage";

                /// <summary>批准条目；批准人必须不同于提交人。</summary>
                public const string Approve = "FaceMatch.Unwelcome.Approve";

                /// <summary>收到命中提示并负责人工复核，可看命中图与参考照片（敏感生物识别信息，逐次审计）。</summary>
                public const string Review = "FaceMatch.Unwelcome.Review";
            }
        }

        public static class PosTerminal
        {
            public static class Sales
            {
                public const string View = "Permissions.PosTerminal.Sales.View";
                public const string AddItem = "Permissions.PosTerminal.Sales.AddItem";
                public const string AddOpenItem = "Permissions.PosTerminal.Sales.AddOpenItem";
                public const string RemoveLine = "Permissions.PosTerminal.Sales.RemoveLine";
                public const string ChangeQuantity = "Permissions.PosTerminal.Sales.ChangeQuantity";
                public const string ChangePrice = "Permissions.PosTerminal.Sales.ChangePrice";
                public const string LineManualDiscount =
                    "Permissions.PosTerminal.Sales.LineManualDiscount";
                public const string LineQuickDiscount10Percent =
                    "Permissions.PosTerminal.Sales.LineQuickDiscount10Percent";
                public const string LineQuickDiscount20Percent =
                    "Permissions.PosTerminal.Sales.LineQuickDiscount20Percent";
                public const string LineQuickDiscount30Percent =
                    "Permissions.PosTerminal.Sales.LineQuickDiscount30Percent";
                public const string LineQuickDiscount40Percent =
                    "Permissions.PosTerminal.Sales.LineQuickDiscount40Percent";
                public const string LineQuickDiscount50Percent =
                    "Permissions.PosTerminal.Sales.LineQuickDiscount50Percent";
                public const string OrderManualDiscount =
                    "Permissions.PosTerminal.Sales.OrderManualDiscount";
                public const string OrderQuickDiscount10Percent =
                    "Permissions.PosTerminal.Sales.OrderQuickDiscount10Percent";
                public const string OrderQuickDiscount20Percent =
                    "Permissions.PosTerminal.Sales.OrderQuickDiscount20Percent";
                public const string OrderQuickDiscount30Percent =
                    "Permissions.PosTerminal.Sales.OrderQuickDiscount30Percent";
                public const string OrderQuickDiscount40Percent =
                    "Permissions.PosTerminal.Sales.OrderQuickDiscount40Percent";
                public const string OrderQuickDiscount50Percent =
                    "Permissions.PosTerminal.Sales.OrderQuickDiscount50Percent";

                // 旧权限仅保留给旧版 POS 做运行时兼容，不再作为可分配权限种子。
                public const string LineDiscount = "Permissions.PosTerminal.Sales.LineDiscount";
                public const string OrderDiscount = "Permissions.PosTerminal.Sales.OrderDiscount";
                public const string ClearCart = "Permissions.PosTerminal.Sales.ClearCart";
                public const string HoldOrder = "Permissions.PosTerminal.Sales.HoldOrder";
                public const string RecallOrder = "Permissions.PosTerminal.Sales.RecallOrder";
            }

            public static class Payment
            {
                public const string View = "Permissions.PosTerminal.Payment.View";
                public const string TakeCash = "Permissions.PosTerminal.Payment.TakeCash";
                public const string TakeCard = "Permissions.PosTerminal.Payment.TakeCard";
                public const string TakeVoucher = "Permissions.PosTerminal.Payment.TakeVoucher";
                public const string RemoveTender = "Permissions.PosTerminal.Payment.RemoveTender";
                public const string Confirm = "Permissions.PosTerminal.Payment.Confirm";
            }

            public static class Returns
            {
                public const string View = "Permissions.PosTerminal.Returns.View";
                public const string AddReceiptLine = "Permissions.PosTerminal.Returns.AddReceiptLine";
                public const string AddNoReceiptItem = "Permissions.PosTerminal.Returns.AddNoReceiptItem";
                public const string Confirm = "Permissions.PosTerminal.Returns.Confirm";
            }

            public static class SpecialProducts
            {
                public const string View = "Permissions.PosTerminal.SpecialProducts.View";
                public const string AddToCart = "Permissions.PosTerminal.SpecialProducts.AddToCart";
                public const string Manage = "Permissions.PosTerminal.SpecialProducts.Manage";
            }

            public static class History
            {
                public const string View = "Permissions.PosTerminal.History.View";
                public const string Recall = "Permissions.PosTerminal.History.Recall";
                public const string Reprint = "Permissions.PosTerminal.History.Reprint";
            }

            public static class DailyClose
            {
                public const string View = "Permissions.PosTerminal.DailyClose.View";
                public const string Save = "Permissions.PosTerminal.DailyClose.Save";
                public const string Reprint = "Permissions.PosTerminal.DailyClose.Reprint";
            }

            public static class Installments
            {
                public const string View = "Permissions.PosTerminal.Installments.View";
                public const string Create = "Permissions.PosTerminal.Installments.Create";
                public const string AddRepayment = "Permissions.PosTerminal.Installments.AddRepayment";
                public const string Cancel = "Permissions.PosTerminal.Installments.Cancel";
                public const string ConfirmPickup = "Permissions.PosTerminal.Installments.ConfirmPickup";
                public const string AmendLines = "Permissions.PosTerminal.Installments.AmendLines";
            }

            public static class Settings
            {
                public const string View = "Permissions.PosTerminal.Settings.View";
                public const string PaymentTerminal = "Permissions.PosTerminal.Settings.PaymentTerminal";
                public const string ReceiptPrinter = "Permissions.PosTerminal.Settings.ReceiptPrinter";
                public const string CatalogDownload = "Permissions.PosTerminal.Settings.CatalogDownload";
                public const string CatalogReset = "Permissions.PosTerminal.Settings.CatalogReset";
                public const string TestDataReset = "Permissions.PosTerminal.Settings.TestDataReset";
                public const string DeviceRegistration = "Permissions.PosTerminal.Settings.DeviceRegistration";
                public const string AppUpdate = "Permissions.PosTerminal.Settings.AppUpdate";
            }

            public static class CashDrawer
            {
                public const string Open = "Permissions.PosTerminal.CashDrawer.Open";
            }

            public static class Receipt
            {
                public const string PrintLast = "Permissions.PosTerminal.Receipt.PrintLast";
            }

            public static class CustomerDisplay
            {
                public const string Manage = "Permissions.PosTerminal.CustomerDisplay.Manage";
            }

            public static class System
            {
                public const string Sync = "Permissions.PosTerminal.System.Sync";
            }

            public static class Audit
            {
                public const string View = "Permissions.PosTerminal.Audit.View";
            }
        }

        public static class Orders
        {
            public const string View = "Orders.View";
            public const string Create = "Orders.Create";
            public const string Edit = "Orders.Edit";
            public const string Delete = "Orders.Delete";
        }

        /// <summary>移动端销售订单查询；与 Web 收银记录页的 Orders.View 分开授权。</summary>
        public static class SalesOrders
        {
            public const string View = "SalesOrders.View";
        }

        public static class InstallmentOrders
        {
            public const string View = "InstallmentOrders.View";
        }

        public static class StoreVouchers
        {
            public const string View = "StoreVouchers.View";
        }

        /// <summary>
        /// 分店现金管理：店长把现金存银行、记现金支出，管理员与财务查看统计并对账。
        /// 新码不写入角色模板，上线后由管理员在角色管理里显式授予。
        /// </summary>
        public static class Cash
        {
            /// <summary>查看现金池总览、按日明细、存款与支出记录；店长只限自己关联的分店。</summary>
            public const string OverviewView = "Cash.Overview.View";

            /// <summary>登记存银行、上传存单、选择纳入现金池的日结、录入期初与盘点。</summary>
            public const string DepositCreate = "Cash.Deposit.Create";

            /// <summary>录入现金支出（现金工资、现金购物、T2、其他），店长录入即生效。</summary>
            public const string ExpenseCreate = "Cash.Expense.Create";

            /// <summary>作废范围内任何存款、支出与期初盘点记录；没有它的店长只能在时限内作废自己录入的。</summary>
            public const string Void = "Cash.Void";

            /// <summary>查看全部分店与全部历史 T2；没有它的店长只能看自己关联分店最近 14 天的 T2。</summary>
            public const string AllStoresView = "Cash.AllStores.View";
        }

        public static class Container
        {
            public const string View = "Container.View";
            public const string Create = "Container.Create";
            public const string Edit = "Container.Edit";
            public const string Delete = "Container.Delete";
            /// <summary>移动端查看近期新品到店预告。</summary>
            public const string MobileNewProductsView = "Container.MobileNewProductsView";
            /// <summary>
            /// 查看完整货柜编号：没有它的账号在接口返回与导出文件里只看到后 4 位（其余为 *）。
            /// 有它的账号界面上默认仍遮罩，点「显示完整编号」按钮后才显示全部。管理员角色自动具备。
            /// </summary>
            public const string ViewFullNumber = "Container.ViewFullNumber";
        }

        public static class Warehouse
        {
            public const string Manage = "Warehouse.Manage";
            public const string ManageProducts = "Warehouse.ManageProducts";
            public const string ManageCategories = "Warehouse.ManageCategories";
            public const string ManageLocations = "Warehouse.ManageLocations";
            public const string ManageOrders = "Warehouse.ManageOrders";
            public const string Picking = "Warehouse.Picking";
            /// <summary>
            /// 打印配货单时自动「开始配货」（订单 已提交 → 配货中）。与订单编辑权限分开授权：
            /// 纯仓库员工不能编辑订单，但可以被单独授予这一个动作。刻意不设别名，
            /// 持有 Warehouse.Manage 等宽泛权限不会因此自动具备，需显式授予。
            /// </summary>
            public const string StartPicking = "Warehouse.StartPicking";
            /// <summary>
            /// 在分店订货单里用负数发货录入退货（退货冲减金额，净额为负时开退款单）。
            /// 默认无人具备、管理员手动授权；刻意不设别名，持有 Warehouse.Manage / ManageOrders 不会自动具备。
            /// </summary>
            public const string StoreOrderReturn = "Warehouse.StoreOrderReturn";
            /// <summary>
            /// 撤销未完成的分店价格变更任务（Web「价格变更任务」页与移动端「价格和标签」）。撤销按商品覆盖所有分店，
            /// 只取消提醒、不改任何价格、不可恢复，所以单独成码，可以只授予需要的人。
            /// 持有 Warehouse.ManageProducts / Warehouse.Manage 的账号经别名自动具备，保证 Web 原有撤销能力不丢。
            /// </summary>
            public const string CancelPriceUpdateTasks = "Warehouse.CancelPriceUpdateTasks";
        }

        /// <summary>
        /// 供应商节日预订单（KFC / Dats / Brazco 等圣诞、万圣节订单表）。两码都刻意不设别名，需显式授予。
        /// </summary>
        public static class SupplierPreorder
        {
            /// <summary>导入订单表、开启分店、维护下单人员授权、截止锁定、导出汇总；截止后仍可修改。</summary>
            public const string Manage = "SupplierPreorder.Manage";
            /// <summary>
            /// 「预订单下单」入口。只决定能否看到菜单和进入页面；能替哪家店下单只认「下单人员授权」，
            /// 不看可管理分店、不看是否店长。
            /// </summary>
            public const string Order = "SupplierPreorder.Order";
        }

        public static class DomesticPurchase
        {
            public const string ManageSuppliers = "DomesticPurchase.ManageSuppliers";
            public const string ManageProducts = "DomesticPurchase.ManageProducts";
            public const string ManagePrefixCodes = "DomesticPurchase.ManagePrefixCodes";
        }

        public static class Reports
        {
            public const string View = "Reports.View";
            public const string ProductMovementView = "Reports.ProductMovement.View";
        }

        public static class SalesDashboard
        {
            public const string SalesDataView = "SalesDashboard.SalesData.View";
            public const string SalesDetailView = "SalesDashboard.SalesDetail.View";
            public const string CompactBoardView = "SalesDashboard.CompactBoard.View";

            // 月度日销售下载页：按分店、按日查看并下载营业额、刷卡、现金。
            public const string MonthlyDailySalesDownloadView =
                "SalesDashboard.MonthlyDailySalesDownload.View";
            public const string ProductMovementView = "SalesDashboard.ProductMovement.View";
            public const string BatchProductSalesView = "SalesDashboard.BatchProductSales.View";
            public const string WarehouseFlowView = "SalesDashboard.WarehouseFlow.View";
            public const string LocalProductAnalysisView =
                "SalesDashboard.LocalProductAnalysis.View";
            public const string PurchaseAmountView = "SalesDashboard.PurchaseAmount.View";

            // 销售看板「分店进货销量分析」页面的逐页权限码。
            public const string LocalSupplierPurchaseSalesView =
                "SalesDashboard.LocalSupplierPurchaseSales.View";

            // 静态报告「KFC 补货信号」（/reports/kfc-uncle-bills/）：查看权限只能看自己关联的门店；
            // 全部门店权限额外放行全链汇总与其它门店的数据文件。
            public const string KfcRestockSignalView = "SalesDashboard.KfcRestockSignal.View";
            public const string KfcRestockSignalAllStores = "SalesDashboard.KfcRestockSignal.AllStores";
        }

        public static class StoreProducts
        {
            public const string View = "StoreProducts.View";
            public const string Create = "StoreProducts.Create";
            public const string Edit = "StoreProducts.Edit";

            /// <summary>把本店零售价/折扣同步到其它分店。跨店改价风险高于改本店，单独授权。</summary>
            public const string SyncToOtherStores = "StoreProducts.SyncToOtherStores";

            /// <summary>
            /// 移动端「商品查询」修改商品主档的中文名称（显示名称）。
            /// 名称是全局主档，改动会影响所有分店、POS 小票/标签并同步 HQ，所以与 Edit 分开单独授权，
            /// 持有 Edit（改价）不会自动获得改名权限。
            /// </summary>
            public const string EditName = "StoreProducts.EditName";

            /// <summary>
            /// 移动端「价格更新」：查看本店通知、按仓库价更新本店价格、处理标签。
            /// 与 Edit 分开：持有者只能把价格改成仓库下发的目标值，不能随意改价。
            /// </summary>
            public const string PriceUpdates = "StoreProducts.PriceUpdates";
        }

        public static class Promotions
        {
            public const string View = "Promotions.View";
            public const string Edit = "Promotions.Edit";
        }

        public static class Advertisements
        {
            public const string View = "Advertisements.View";
            public const string Edit = "Advertisements.Edit";
        }

        public static class PricingStrategy
        {
            public const string View = "PricingStrategy.View";
            public const string Edit = "PricingStrategy.Edit";
        }

        public static class DeviceRegistration
        {
            public const string View = "DeviceRegistration.View";
            public const string Manage = "DeviceRegistration.Manage";

            public static class ActivationCodes
            {
                public const string Manage = "DeviceRegistration.ActivationCodes.Manage";
            }

            public static class MobileActivationCodes
            {
                public const string Manage =
                    "DeviceRegistration.MobileActivationCodes.Manage";
            }
        }

        public static class LocalPurchase
        {
            public const string View = "LocalPurchase.View";
            public const string MobileView = "LocalPurchase.MobileView";
            public const string Edit = "LocalPurchase.Edit";
            public const string PushToHq = "LocalPurchase.PushToHq";
        }

        private static readonly IReadOnlyDictionary<string, string[]> PermissionAliases =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                [LocalPurchase.View] = ["LocalInvocie.View"],
                [LocalPurchase.Edit] = ["LocalInvocie.Edit"],
                [Reports.ProductMovementView] = [Reports.View],
                // 前端仓库商品页面兼容旧的仓库总管理权限，API 使用同一访问边界。
                [Warehouse.ManageProducts] = [Warehouse.Manage],
                // 撤销价格变更任务原先由 ManageProducts 守卫（Web 页面已在用）；拆成独立码后两者都放行，已有账号无需重新授权。
                [Warehouse.CancelPriceUpdateTasks] = [Warehouse.ManageProducts, Warehouse.Manage],
                // 现有仓库员工与订货管理者无需重新授权即可拣货；单独授予 Picking 不会反向获得管理权限。
                [Warehouse.Picking] = [Warehouse.Manage, Warehouse.ManageOrders],
                // 管理下载权限天然包含查看下载，保证菜单可见性和列表 GET 授权一致。
                [System.ViewAppDownloads] = [System.ManageAppDownloads],
                // 能编辑管理分店排班的人天然可以查看排班，避免「能改不能看」导致排班页 403。
                [Attendance.Schedule.ViewStore] = [Attendance.Schedule.EditManagedStore],
                // 全局用户权限天然包含对应的店员管理权限；反向不成立，店员权限不会获得全局用户接口。
                [Users.ViewStoreStaff] = [Users.View],
                [Users.CreateStoreStaff] = [Users.Create],
                [Users.EditStoreStaff] = [Users.Edit],
                [Users.ResetStoreStaffPassword] = [Users.ResetPassword],
            };

        public static class AustralianSuppliers
        {
            public const string View = "AustralianSuppliers.View";
            public const string Edit = "AustralianSuppliers.Edit";
        }

        public static class Store
        {
            public const string ManageOperations = "Store.ManageOperations";
        }

        /// <summary>
        /// 移动端「收银用户条码」独立权限：与 Web 的 Store.ManageOperations 分开授权，
        /// 管理（查看/创建/更新/启停）与打印（查看/打印并累计打印次数）可以分别授予。
        /// </summary>
        public static class CashRegisterUsers
        {
            public const string MobileManage = "CashRegisterUsers.MobileManage";
            public const string MobilePrint = "CashRegisterUsers.MobilePrint";
        }

        /// <summary>移动端「季节商品查询」：查看本店及其他分店的累计进货、累计销量与理论存货。</summary>
        public static class SeasonalProductInsights
        {
            public const string View = "SeasonalProductInsights.View";
        }

        public static class Attendance
        {
            public static class Schedule
            {
                public const string ViewSelf = "Attendance.Schedule.ViewSelf";
                public const string ViewStore = "Attendance.Schedule.ViewStore";
                public const string EditManagedStore = "Attendance.Schedule.EditManagedStore";
            }

            public static class Availability
            {
                public const string SubmitSelf = "Attendance.Availability.SubmitSelf";
                public const string ViewManagedStore = "Attendance.Availability.ViewManagedStore";
            }

            public static class Punch
            {
                public const string Self = "Attendance.Punch.Self";
                public const string ViewManagedStore = "Attendance.Punch.ViewManagedStore";
                public const string AdjustManagedStore = "Attendance.Punch.AdjustManagedStore";
            }

            public static class Approval
            {
                public const string ViewManagedStore = "Attendance.Approval.ViewManagedStore";
                public const string ReviewManagedStore = "Attendance.Approval.ReviewManagedStore";
                // 审核本人提交的考勤申请；默认不授予任何角色，由管理员在生产手动授权（迁移 20261026.001 入库）。
                public const string ReviewSelf = "Attendance.Approval.ReviewSelf";
            }

            public static class Holiday
            {
                public const string ViewStore = "Attendance.Holiday.ViewStore";
                public const string EditManagedStore = "Attendance.Holiday.EditManagedStore";
            }

            public static class Leave
            {
                public const string ApplySelf = "Attendance.Leave.ApplySelf";
                public const string ViewManagedStore = "Attendance.Leave.ViewManagedStore";
                public const string ReviewManagedStore = "Attendance.Leave.ReviewManagedStore";
            }

            public static class Settings
            {
                public const string Edit = "Attendance.Settings.Edit";
            }

            // 工资表 / 时薪表；默认不授予任何角色，由管理员在生产手动授权（迁移 20261026.002 入库）。
            // Edit / Unlock 不隐含 View；Attendance.Admin.View 也不连带这三项。
            public static class Payroll
            {
                public const string View = "Attendance.Payroll.View";
                public const string Edit = "Attendance.Payroll.Edit";
                public const string Unlock = "Attendance.Payroll.Unlock";
            }

            public static class Admin
            {
                public const string View = "Attendance.Admin.View";
            }
        }

        public static class SeasonalCards
        {
            public static class Remaining
            {
                public const string ViewManagedStore = "SeasonalCards.Remaining.ViewManagedStore";
                public const string SubmitManagedStore = "SeasonalCards.Remaining.SubmitManagedStore";
                public const string ViewAllStores = "SeasonalCards.Remaining.ViewAllStores";
            }
        }

        public static class System
        {
            public const string ViewLogs = "System.ViewLogs";
            public const string ManageScheduledTasks = "System.ManageScheduledTasks";
            public const string ManageSettings = "System.ManageSettings";
            public const string ViewAppDownloads = "System.ViewAppDownloads";
            public const string ManageAppDownloads = "System.ManageAppDownloads";
            /// <summary>移动端「App 安装」页：查看 iOS / Android 最新正式版本及安装二维码，与版本管理权限相互独立。</summary>
            public const string ViewMobileAppInstallLinks = "System.ViewMobileAppInstallLinks";
            public const string ViewPerformanceBaseline = "System.ViewPerformanceBaseline";
            public const string ManagePerformanceBaseline = "System.ManagePerformanceBaseline";
        }

        public static class Dashboard
        {
            public const string View = "Dashboard";
        }

        public static class OrderFront
        {
            public const string View = "OrderFront";
            /// <summary>订货前台「货号销量」页面：允许前台用户按导入货号查看全部分店的销量，与后台销售看板权限相互独立。</summary>
            public const string BatchProductSalesView = "OrderFront.BatchProductSales.View";
        }

        /// <summary>
        /// 角色等级基准值（数值越大等级越高）。迁移回填与新环境种子共用，必须与
        /// RoleLevelSchema.ApplySql 的回填保持一致；未登记的自定义角色默认为 0，由管理员在角色页设置。
        /// </summary>
        public static class RoleLevels
        {
            public const int SuperAdmin = 100;
            public const int WarehouseManager = 80;
            public const int StoreManager = 70;
            public const int Manager = 60;
            public const int Staff = 30;
            public const int User = 10;
            public const int Max = 100;

            public static int ForRoleName(string? roleName)
            {
                if (string.IsNullOrWhiteSpace(roleName))
                {
                    return 0;
                }

                if (IsSuperAdminRole(roleName)) return SuperAdmin;
                if (WarehouseManagerRoleNames.Contains(roleName, StringComparer.OrdinalIgnoreCase))
                    return WarehouseManager;
                if (IsStoreManagerRole(roleName)) return StoreManager;
                if (string.Equals(roleName, "Manager", StringComparison.OrdinalIgnoreCase))
                    return Manager;
                if (IsEmployeeRole(roleName)
                    || string.Equals(roleName, "Order", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(roleName, "WarehouseStaff", StringComparison.OrdinalIgnoreCase))
                    return Staff;
                if (string.Equals(roleName, "User", StringComparison.OrdinalIgnoreCase)) return User;
                return 0;
            }
        }

        public static bool IsSuperAdminRole(string? roleName)
        {
            return !string.IsNullOrWhiteSpace(roleName)
                && SuperAdminRoleNames.Contains(roleName, StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsStoreManagerRole(string? roleName)
        {
            return !string.IsNullOrWhiteSpace(roleName)
                && StoreManagerRoleNames.Contains(roleName, StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsHighPrivilegeRole(string? roleName)
        {
            return !string.IsNullOrWhiteSpace(roleName)
                && HighPrivilegeRoleNames.Contains(roleName, StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsEmployeeRole(string? roleName)
        {
            return !string.IsNullOrWhiteSpace(roleName)
                && EmployeeRoleNames.Contains(roleName, StringComparer.OrdinalIgnoreCase);
        }

        public static IReadOnlyDictionary<string, string[]> GetPermissionAliases()
        {
            return PermissionAliases;
        }

        public static bool IsAttendanceSelfServiceGranted(string? permission)
        {
            return permission is
                Attendance.Schedule.ViewSelf
                or Attendance.Availability.SubmitSelf
                or Attendance.Punch.Self
                or Attendance.Leave.ApplySelf;
        }

        public static IReadOnlyCollection<string> GetEquivalentPermissionCodes(string? permission)
        {
            if (string.IsNullOrWhiteSpace(permission))
            {
                return Array.Empty<string>();
            }

            var codes = new List<string> { permission };
            if (PermissionAliases.TryGetValue(permission, out var aliases))
            {
                codes.AddRange(aliases);
            }

            return codes;
        }

        public static IReadOnlyCollection<string> ExpandPermissionCodes(
            IEnumerable<string>? permissions
        )
        {
            if (permissions == null)
            {
                return Array.Empty<string>();
            }

            var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var permission in permissions.Where(permission => !string.IsNullOrWhiteSpace(permission)))
            {
                codes.Add(permission);

                foreach (var alias in PermissionAliases)
                {
                    if (alias.Value.Contains(permission, StringComparer.OrdinalIgnoreCase))
                    {
                        codes.Add(alias.Key);
                    }
                }
            }

            return codes.ToList();
        }

        public static IEnumerable<(string Code, string Name, string Category)> GetAllPermissions() =>
            PermissionSeedData.AllPermissions
                .Select(seed => (seed.Code, seed.Name, seed.Category))
                .GroupBy(item => item.Code, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First());
    }
}
