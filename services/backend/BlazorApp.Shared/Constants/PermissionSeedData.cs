namespace BlazorApp.Shared.Constants
{
    public sealed record PermissionSeedDefinition(
        string Code,
        string Name,
        string Category,
        string Description
    );

    public sealed record RolePermissionTemplateDefinition(
        string RoleName,
        IReadOnlyList<string> PermissionCodes
    );

    public static class PermissionSeedData
    {
        public static IReadOnlyList<PermissionSeedDefinition> AttendancePermissions { get; } =
            new List<PermissionSeedDefinition>
            {
                // 自助类权限只在移动端「个人考勤」与考勤自助接口生效，Web 排班页不读取这四项。
                new(Permissions.Attendance.Schedule.ViewSelf, "查看自己的排班", "排班考勤", "移动端「个人考勤」- 查看自己的排班"),
                new(Permissions.Attendance.Schedule.ViewStore, "查看相关分店排班", "排班考勤", "Web 页面 /pos-admin/schedule-attendance 与移动端「考勤管理」- 查看相关分店排班"),
                new(Permissions.Attendance.Schedule.EditManagedStore, "编辑管理分店排班", "排班考勤", "Web 页面 /pos-admin/schedule-attendance 与移动端「考勤管理」- 编辑管理分店排班"),
                new(Permissions.Attendance.Availability.SubmitSelf, "上报自己的可上班时间", "排班考勤", "移动端「个人考勤」- 上报自己的可上班时间"),
                new(Permissions.Attendance.Availability.ViewManagedStore, "查看管理分店可上班时间", "排班考勤", "Web 页面 /pos-admin/schedule-attendance 与移动端「考勤管理」- 查看管理分店可上班时间"),
                new(Permissions.Attendance.Punch.Self, "本人打卡", "排班考勤", "移动端「个人考勤」- 本人打卡"),
                new(Permissions.Attendance.Punch.ViewManagedStore, "查看管理分店打卡记录", "排班考勤", "Web 页面 /pos-admin/schedule-attendance 与移动端「考勤管理」- 查看管理分店打卡记录"),
                new(Permissions.Attendance.Approval.ViewManagedStore, "查看管理分店审核记录", "排班考勤", "Web 页面 /pos-admin/schedule-attendance 与移动端「考勤管理」- 查看管理分店审核记录"),
                new(Permissions.Attendance.Approval.ReviewManagedStore, "审核管理分店考勤", "排班考勤", "Web 页面 /pos-admin/schedule-attendance 与移动端「考勤管理」- 审核管理分店考勤"),
                new(Permissions.Attendance.Holiday.ViewStore, "查看分店公共假期", "排班考勤", "Web 页面 /pos-admin/schedule-attendance 与移动端「考勤管理」- 查看分店公共假期"),
                new(Permissions.Attendance.Holiday.EditManagedStore, "编辑管理分店公共假期", "排班考勤", "Web 页面 /pos-admin/schedule-attendance 与移动端「考勤管理」- 编辑管理分店公共假期"),
                new(Permissions.Attendance.Leave.ApplySelf, "本人提交请假申请", "排班考勤", "移动端「个人考勤」- 本人提交请假申请"),
                new(Permissions.Attendance.Leave.ViewManagedStore, "查看管理分店请假申请", "排班考勤", "Web 页面 /pos-admin/schedule-attendance 与移动端「考勤管理」- 查看管理分店请假申请"),
                new(Permissions.Attendance.Leave.ReviewManagedStore, "审核管理分店请假申请", "排班考勤", "Web 页面 /pos-admin/schedule-attendance 与移动端「考勤管理」- 审核管理分店请假申请"),
                new(Permissions.Attendance.Settings.Edit, "编辑考勤设置", "排班考勤", "Web 页面 /pos-admin/schedule-attendance 与移动端「考勤管理」- 编辑考勤设置"),
                new(Permissions.Attendance.Admin.View, "查看全部考勤管理", "排班考勤", "Web 页面 /pos-admin/schedule-attendance 与移动端「考勤管理」- 查看全部分店考勤管理"),
            };

        public static IReadOnlyList<PermissionSeedDefinition> SeasonalCardPermissions { get; } =
            new List<PermissionSeedDefinition>
            {
                new(
                    Permissions.SeasonalCards.Remaining.ViewManagedStore,
                    "查看管理分店季节卡剩余",
                    "季节卡片",
                    "移动端「季节卡」- 查看管理分店季节卡剩余上报记录"
                ),
                new(
                    Permissions.SeasonalCards.Remaining.SubmitManagedStore,
                    "提交管理分店季节卡剩余",
                    "季节卡片",
                    "移动端「季节卡」- 提交管理分店季节卡剩余"
                ),
            };

        public static IReadOnlyList<PermissionSeedDefinition> SalesDashboardPermissions { get; } =
            new List<PermissionSeedDefinition>
            {
                new(Permissions.SalesDashboard.SalesDataView, "查看销售数据", "销售看板", "Web 页面 /executive-sales-intelligence/overview - 查看营业额、订单、客流及分店销售表现"),
                new(Permissions.SalesDashboard.SalesDetailView, "查看销售明细", "销售看板", "Web 页面 /executive-sales-intelligence/sales-detail-v2 - 按供应商、分店和商品查看销售明细"),
                new(Permissions.SalesDashboard.CompactBoardView, "查看独立销售看板", "销售看板", "Web 页面 /executive-sales-intelligence/compact-sales-board - 查看紧凑布局的分店、供应商和商品销售数据"),
                new(Permissions.SalesDashboard.ProductMovementView, "查看商品经营分析", "销售看板", "Web 页面 /executive-sales-intelligence/product-movement-report - 查看商品经营表现和店长动作建议"),
                new(Permissions.SalesDashboard.BatchProductSalesView, "查看批量货号销量", "销售看板", "Web 页面 /executive-sales-intelligence/purchase-sales-analysis「批量货号销量」标签 - 按导入货号查看商品每日销量、分店销量及折扣成交情况"),
                new(Permissions.SalesDashboard.WarehouseFlowView, "查看仓库商品流转分析", "销售看板", "Web 页面 /executive-sales-intelligence/warehouse-product-flow-analysis 与移动端「仓库商品进销」- 查看仓库商品的进货、发货和销售流转"),
                new(Permissions.SalesDashboard.LocalProductAnalysisView, "查看澳洲本地商品分析", "销售看板", "Web 页面 /executive-sales-intelligence/local-product-sales-analysis - 查看澳洲本地商品的进货与销售表现"),
                new(Permissions.SalesDashboard.PurchaseAmountView, "查看进货金额看板", "销售看板", "Web 页面 /executive-sales-intelligence/purchase-amount-dashboard - 查看各分店仓库订单、本地供应商进货金额及营业额"),
                new(Permissions.SalesDashboard.LocalSupplierPurchaseSalesView, "查看分店进货销量分析", "销售看板", "Web 页面 /executive-sales-intelligence/purchase-sales-analysis「分店进货销量分析」标签 - 按分店和供应商查看最近进货、逐日销量图与售出比"),
                new(Permissions.SalesDashboard.KfcRestockSignalView, "查看 KFC 补货信号", "销售看板", "报告页面 /reports/kfc-uncle-bills/ - 查看 Uncle Bills (KFC) 的畅销、预测、库存缺口与进销累计，只能看自己关联的门店"),
                new(Permissions.SalesDashboard.KfcRestockSignalAllStores, "查看 KFC 补货信号全部门店", "销售看板", "报告页面 /reports/kfc-uncle-bills/ - 在「查看 KFC 补货信号」基础上查看全部门店的数据与全链汇总"),
            };

        private static IReadOnlyList<string> AttendanceSelfServicePermissionCodes { get; } =
            new[]
            {
                Permissions.Attendance.Schedule.ViewSelf,
                Permissions.Attendance.Availability.SubmitSelf,
                Permissions.Attendance.Punch.Self,
                Permissions.Attendance.Leave.ApplySelf,
            };

        private static IReadOnlyList<string> StoreManagerPermissionCodes { get; } =
            new[]
            {
                Permissions.Attendance.Schedule.ViewSelf,
                Permissions.Attendance.Schedule.ViewStore,
                Permissions.Attendance.Schedule.EditManagedStore,
                Permissions.Attendance.Availability.SubmitSelf,
                Permissions.Attendance.Availability.ViewManagedStore,
                Permissions.Attendance.Punch.Self,
                Permissions.Attendance.Punch.ViewManagedStore,
                Permissions.Attendance.Approval.ViewManagedStore,
                Permissions.Attendance.Approval.ReviewManagedStore,
                Permissions.Attendance.Holiday.ViewStore,
                Permissions.Attendance.Holiday.EditManagedStore,
                Permissions.Attendance.Leave.ApplySelf,
                Permissions.Attendance.Leave.ViewManagedStore,
                Permissions.Attendance.Leave.ReviewManagedStore,
                Permissions.SeasonalCards.Remaining.ViewManagedStore,
                Permissions.SeasonalCards.Remaining.SubmitManagedStore,
                Permissions.DeviceRegistration.View,
                Permissions.DeviceRegistration.Manage,
                Permissions.PosTerminal.Audit.View,
                Permissions.Users.View,
                Permissions.Users.ManagePosTerminalPermissions,
                Permissions.EmployeeProfiles.ReviewSensitiveManagedStore,
                Permissions.Container.MobileNewProductsView,
            };

        public static IReadOnlyList<string> PosTerminalLineDiscountPermissionCodes { get; } =
            new[]
            {
                Permissions.PosTerminal.Sales.LineManualDiscount,
                Permissions.PosTerminal.Sales.LineQuickDiscount10Percent,
                Permissions.PosTerminal.Sales.LineQuickDiscount20Percent,
                Permissions.PosTerminal.Sales.LineQuickDiscount30Percent,
                Permissions.PosTerminal.Sales.LineQuickDiscount40Percent,
                Permissions.PosTerminal.Sales.LineQuickDiscount50Percent,
            };

        public static IReadOnlyList<string> PosTerminalOrderDiscountPermissionCodes { get; } =
            new[]
            {
                Permissions.PosTerminal.Sales.OrderManualDiscount,
                Permissions.PosTerminal.Sales.OrderQuickDiscount10Percent,
                Permissions.PosTerminal.Sales.OrderQuickDiscount20Percent,
                Permissions.PosTerminal.Sales.OrderQuickDiscount30Percent,
                Permissions.PosTerminal.Sales.OrderQuickDiscount40Percent,
                Permissions.PosTerminal.Sales.OrderQuickDiscount50Percent,
            };

        public static IReadOnlyList<string> PosTerminalBusinessPermissionCodes { get; } =
            new[]
            {
                Permissions.PosTerminal.Sales.View,
                Permissions.PosTerminal.Sales.AddItem,
                Permissions.PosTerminal.Sales.AddOpenItem,
                Permissions.PosTerminal.Sales.RemoveLine,
                Permissions.PosTerminal.Sales.ChangeQuantity,
                Permissions.PosTerminal.Sales.ChangePrice,
                Permissions.PosTerminal.Sales.ClearCart,
                Permissions.PosTerminal.Sales.HoldOrder,
                Permissions.PosTerminal.Sales.RecallOrder,
                Permissions.PosTerminal.Payment.View,
                Permissions.PosTerminal.Payment.TakeCash,
                Permissions.PosTerminal.Payment.TakeCard,
                Permissions.PosTerminal.Payment.TakeVoucher,
                Permissions.PosTerminal.Payment.RemoveTender,
                Permissions.PosTerminal.Payment.Confirm,
                Permissions.PosTerminal.Returns.View,
                Permissions.PosTerminal.Returns.AddReceiptLine,
                Permissions.PosTerminal.Returns.AddNoReceiptItem,
                Permissions.PosTerminal.Returns.Confirm,
                Permissions.PosTerminal.History.View,
                Permissions.PosTerminal.History.Recall,
                Permissions.PosTerminal.History.Reprint,
                Permissions.PosTerminal.DailyClose.View,
                Permissions.PosTerminal.DailyClose.Save,
                Permissions.PosTerminal.DailyClose.Reprint,
                Permissions.PosTerminal.Installments.View,
                Permissions.PosTerminal.Installments.Create,
                Permissions.PosTerminal.Installments.AddRepayment,
                Permissions.PosTerminal.Installments.Cancel,
                Permissions.PosTerminal.Installments.ConfirmPickup,
                Permissions.PosTerminal.CashDrawer.Open,
                Permissions.PosTerminal.Receipt.PrintLast,
            }
            .Concat(PosTerminalLineDiscountPermissionCodes)
            .Concat(PosTerminalOrderDiscountPermissionCodes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        public static IReadOnlyList<string> OrderRolePermissionCodes { get; } =
            new[]
            {
                Permissions.OrderFront.View,
                Permissions.Orders.Create,
                Permissions.LocalPurchase.MobileView,
            };

        public static IReadOnlySet<string> DeprecatedPermissionCodes { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AustralianSuppliers",
                "LocalInvocie",
                "LocalInvocie.View",
                "LocalInvocie.Create",
                "LocalInvocie.Edit",
                "LocalInvocie.Delete",
                "LocalPurchase",
                "StoreProducts",
                "Promotions",
                "PricingStrategy",
                "ChinaProduct.View",
                "ChinaProduct.Create",
                "ChinaProduct.Edit",
                "ChinaProduct.Delete",
                Permissions.PosTerminal.Sales.LineDiscount,
                Permissions.PosTerminal.Sales.OrderDiscount,
                // 以下权限码从未被任何接口、页面或菜单消费，已从常量与种子中移除，启动时软删历史记录。
                "Prices.View",
                "Prices.Modify",
                "Prices.Delete",
                "Reports.Export",
                "Warehouse.View",
                "DomesticPurchase.View",
                "Store.ManageInfo",
            };

        private static IReadOnlyList<PermissionSeedDefinition> SharedPermissionSeeds { get; } =
            new List<PermissionSeedDefinition>
            {
                new(Permissions.Users.View, "查看用户", "用户管理", "移动端「员工列表」「用户管理」与用户查询接口 - 查看用户列表与详情；Web 后台 /system/users 菜单另由 Users.ViewWebConsole 控制"),
                // 店长主要用移动端管理店员，Web 后台用户管理页单独授权，避免 Users.View 同时点亮两端菜单。
                new(Permissions.Users.ViewWebConsole, "查看 Web 用户管理", "用户管理", "Web 页面 /system/users - 显示并进入 Web 后台用户管理页"),
                new(Permissions.Users.Create, "创建用户", "用户管理", "Web 页面 /system/users - 创建后台用户"),
                new(Permissions.Users.Edit, "编辑用户", "用户管理", "Web 页面 /system/users - 编辑用户基础信息"),
                new(Permissions.Users.Delete, "删除用户", "用户管理", "Web 页面 /system/users - 删除或停用用户"),
                new(Permissions.Users.ManageRoles, "管理用户角色", "用户管理", "Web 页面 /system/users - 分配或移除用户角色"),
                new(Permissions.Users.ManageStores, "管理用户分店", "用户管理", "Web 页面 /system/users - 维护用户关联分店"),
                new(Permissions.Users.ManagePosTerminalPermissions, "管理分店 POS 权限", "用户管理", "Web 页面 /system/users - 按分店维护收银员 POS 终端业务权限"),
                new(Permissions.Users.ResetPassword, "重置密码", "用户管理", "Web 页面 /system/users - 重置用户登录密码"),
                new(Permissions.EmployeeProfiles.View, "查看员工个人信息", "用户管理", "Web 页面 /system/employee-profiles 与移动端「员工资料」- 查看员工个人信息列表与详情"),
                new(Permissions.EmployeeProfiles.Edit, "维护员工个人信息", "用户管理", "移动端「员工资料」编辑本人资料；Web 页面 /system/employee-profiles 后台编辑另需管理员角色 - 维护身份、银行、养老金、地址等资料"),
                new(Permissions.EmployeeProfiles.EditPositionType, "修改职位类型", "用户管理", "个人信息 - 修改全职、兼职、临时工等职位类型，保存时仍需维护员工个人信息权限"),
                new(Permissions.EmployeeProfiles.ReviewSensitiveManagedStore, "审核管理分店员工敏感资料", "用户管理", "移动端员工敏感资料审核 - 审核管理分店内普通员工提交的敏感资料变更"),
                new(Permissions.Roles.View, "查看角色", "角色管理", "Web 页面 /system/roles、/system/permissions 与移动端「角色」- 查看角色和权限配置"),
                new(Permissions.Roles.Create, "创建角色", "角色管理", "Web 页面 /system/roles - 创建角色"),
                new(Permissions.Roles.Edit, "编辑角色", "角色管理", "Web 页面 /system/roles - 编辑角色基础信息"),
                new(Permissions.Roles.Delete, "删除角色", "角色管理", "Web 页面 /system/roles - 删除角色"),
                new(Permissions.Roles.ManagePermissions, "管理角色权限", "角色管理", "Web 页面 /system/roles 与 /system/permissions - 分配或移除角色权限"),
                new(Permissions.Roles.ManageUsers, "管理角色用户", "角色管理", "Web 页面 /system/roles - 管理角色关联用户"),
                new(Permissions.Stores.View, "查看分店", "分店管理", "Web 页面 /system/stores - 查看分店列表与详情"),
                new(Permissions.Stores.Create, "创建分店", "分店管理", "Web 页面 /system/stores - 创建分店"),
                new(Permissions.Stores.Edit, "编辑分店", "分店管理", "Web 页面 /system/stores - 编辑分店资料"),
                new(Permissions.Stores.Delete, "删除分店", "分店管理", "Web 页面 /system/stores - 删除分店"),
                new(Permissions.Stores.Sync, "同步分店数据", "分店管理", "Web 页面 /system/stores - 同步分店数据"),
                new(Permissions.Products.View, "查看商品", "商品管理", "Web 页面 /domestic-purchase/domestic-products - 查看国内商品"),
                // 商品创建/导入页面由 DomesticPurchase.ManageProducts 控制，这里只守国内商品维护接口。
                new(Permissions.Products.Create, "创建商品", "商品管理", "Web 页面 /domestic-purchase/domestic-products - 新增国内商品"),
                new(Permissions.Products.Edit, "编辑商品", "商品管理", "Web 页面 /domestic-purchase/domestic-products 与 /warehouse/container 明细 - 编辑国内商品、对齐货柜明细国内货号"),
                new(Permissions.Products.Delete, "删除商品", "商品管理", "Web 页面 /domestic-purchase/domestic-products - 删除国内商品"),
                // Orders.* 语义是分店订货单；Orders.View 按历史决策同时守 Web 收银记录页，移动端销售订单另用 SalesOrders.View。
                new(Permissions.Orders.View, "查看订单", "分店订货", "Web 页面 /pos-admin/sales-orders 收银记录与移动端「订货单」- 查看收银记录、分店订货单与到货预告"),
                new(Permissions.Orders.Create, "创建订单", "分店订货", "移动端「首页」「购物车」与浏览器扩展 - 提交分店订货单"),
                new(Permissions.Orders.Edit, "编辑订单", "分店订货", "分店订货接口 - 编辑分店订货单"),
                new(Permissions.Orders.Delete, "删除订单", "分店订货", "分店订货接口 - 删除分店订货单"),
                new(Permissions.SalesOrders.View, "查看销售订单", "分店订货", "移动端「销售订单」- 按分店、日期与订单类型查看销售订单、明细及发票"),
                new(Permissions.InstallmentOrders.View, "查看分期付款订单", "分店财务", "移动端「分期订单」- 查看分店分期付款订单与支付记录"),
                new(Permissions.StoreVouchers.View, "查看分店代金券", "分店财务", "移动端「分店代金券」- 查看分店代金券使用情况与关联订单"),
                new(Permissions.Container.View, "查看货柜", "货柜管理", "Web 页面 /warehouse/containers 与移动端「仓库」- 查看货柜列表与明细"),
                new(Permissions.Container.Create, "创建货柜", "货柜管理", "Web 页面 /warehouse/containers - 创建货柜"),
                new(Permissions.Container.Edit, "编辑货柜", "货柜管理", "Web 页面 /warehouse/containers - 编辑货柜"),
                new(Permissions.Container.Delete, "删除货柜", "货柜管理", "Web 页面 /warehouse/containers - 删除货柜"),
                new(Permissions.Container.MobileNewProductsView, "移动端查看新品到店", "货柜管理", "移动端「新品到店」- 查看过去 2 周至未来 4 周货柜中的新品及预计到店日期"),
                // Warehouse.Manage 是历史总权限：前端把它当作四项仓库管理权限的并集，后端预订/零售价变更/商品记录接口也直接放行。
                new(Permissions.Warehouse.Manage, "管理仓库", "仓库管理", "仓库兼容总权限 - 等价于同时拥有仓库商品、订货、分类、标签四项管理权限，并放行预订、零售价变更、商品记录接口"),
                new(Permissions.Warehouse.ManageProducts, "管理仓库商品", "仓库管理", "Web 页面 /warehouse/products、/warehouse/products/retail-price-changes、/warehouse/products/price-update-tasks、/warehouse/product-grade-management 与移动端「仓库」- 管理仓库商品、零售价变更、建议折扣、分店价格变更任务执行情况和等级"),
                new(Permissions.Warehouse.ManageCategories, "管理仓库分类", "仓库管理", "Web 页面 /warehouse/categories - 管理仓库分类"),
                new(Permissions.Warehouse.ManageLocations, "管理仓库标签", "仓库管理", "Web 页面 /warehouse/locations - 管理仓库标签"),
                new(Permissions.Warehouse.ManageOrders, "管理仓库订货", "仓库管理", "Web 页面 /warehouse/store-orders、/warehouse/preorders、/warehouse/store-order-import-price-variance 与移动端「订货单」- 管理分店订货、预订、配货单和发票"),
                new(Permissions.DomesticPurchase.ManageSuppliers, "管理国内供应商", "国内采购", "Web 页面 /domestic-purchase/china-suppliers - 管理国内供应商"),
                new(Permissions.DomesticPurchase.ManageProducts, "管理国内商品", "国内采购", "Web 页面 /domestic-purchase/product-creation 与 /product-import - 创建和导入商品"),
                new(Permissions.DomesticPurchase.ManagePrefixCodes, "管理前缀码", "国内采购", "Web 页面 /domestic-purchase/prefix-code-management - 管理商品前缀码"),
                // 销售看板各页已改由 SalesDashboard.* 逐页授权，Reports.View 只剩高管看板的三个聚合接口。
                new(Permissions.Reports.View, "查看报表", "报表", "高管看板接口 - 时段客流、分店日绩效与周绩效层级接口；同时视为 Reports.ProductMovement.View 的别名"),
                // 显示名与 SalesDashboard.ProductMovement.View 区分开，避免角色配置页出现两个同名权限。
                new(Permissions.Reports.ProductMovementView, "查看供应商与分店销售明细", "报表", "Web 页面 /warehouse/products/:code/records 销售分析、移动端「报表」与销售看板供应商/分店销售明细接口 - 查看跨分店供应商与商品销售明细"),
                new(Permissions.StoreProducts.View, "查看分店商品", "分店商品管理", "Web 页面 /pos-admin/store-product-price 与移动端「商品查询」「商品洞察」- 查看分店商品价格"),
                new(Permissions.SeasonalProductInsights.View, "查看季节商品查询", "分店商品管理", "移动端「季节商品查询」- 按货号/条码查看本店与其他分店的累计进货、累计销量和理论存货"),
                new(Permissions.StoreProducts.Create, "创建分店商品", "分店商品管理", "Web 页面 /pos-admin/store-product-price - 创建分店商品价格"),
                new(Permissions.StoreProducts.Edit, "编辑分店商品", "分店商品管理", "Web 页面 /pos-admin/store-product-price - 编辑分店商品价格"),
                new(Permissions.StoreProducts.PriceUpdates, "处理价格更新通知", "分店商品管理", "移动端「价格更新」- 查看本店价格更新与换标签通知，按仓库价更新本店零售价/折扣，打印或标记标签"),
                new(Permissions.StoreProducts.SyncToOtherStores, "同步价格到其它分店", "分店商品管理", "移动端「商品维护」- 把本店零售价/折扣同步到其它可管理分店，被同步分店会收到「待换标签」通知"),
                new(Permissions.PosProducts.View, "查看 POS 商品管理", "POS 管理", "Web 页面 /pos-admin/products - 查看 POS 商品、分类、套装码、同步和完整性检查入口"),
                new(Permissions.PosProducts.Manage, "管理 POS 商品", "POS 管理", "Web 页面 /pos-admin/products - 编辑 POS 商品、批量改价、同步总部/分店、维护分类/套装码、执行完整性修复"),
                new(Permissions.PosTerminal.Sales.View, "查看销售页", "POS 销售", "收银端销售页 - 进入销售页面"),
                new(Permissions.PosTerminal.Sales.AddItem, "添加扫码商品", "POS 销售", "收银端销售页 - 添加扫码商品按钮"),
                new(Permissions.PosTerminal.Sales.AddOpenItem, "添加开放商品", "POS 销售", "收银端销售页 - 添加开放商品按钮"),
                new(Permissions.PosTerminal.Sales.RemoveLine, "移除销售行", "POS 销售", "收银端销售页 - 移除购物车明细按钮"),
                new(Permissions.PosTerminal.Sales.ChangeQuantity, "修改销售数量", "POS 销售", "收银端销售页 - 修改商品数量按钮"),
                new(Permissions.PosTerminal.Sales.ChangePrice, "修改销售价格", "POS 销售", "收银端销售页 - 修改商品单价按钮"),
                new(Permissions.PosTerminal.Sales.LineManualDiscount, "单行手工折扣", "POS 销售", "收银端销售页 - 单行手工折扣"),
                new(Permissions.PosTerminal.Sales.LineQuickDiscount10Percent, "单行快捷九折", "POS 销售", "收银端销售页 - 单行快捷 10% 折扣"),
                new(Permissions.PosTerminal.Sales.LineQuickDiscount20Percent, "单行快捷八折", "POS 销售", "收银端销售页 - 单行快捷 20% 折扣"),
                new(Permissions.PosTerminal.Sales.LineQuickDiscount30Percent, "单行快捷七折", "POS 销售", "收银端销售页 - 单行快捷 30% 折扣"),
                new(Permissions.PosTerminal.Sales.LineQuickDiscount40Percent, "单行快捷六折", "POS 销售", "收银端销售页 - 单行快捷 40% 折扣"),
                new(Permissions.PosTerminal.Sales.LineQuickDiscount50Percent, "单行快捷五折", "POS 销售", "收银端销售页 - 单行快捷 50% 折扣"),
                new(Permissions.PosTerminal.Sales.OrderManualDiscount, "整单手工折扣", "POS 销售", "收银端销售页 - 整单手工折扣"),
                new(Permissions.PosTerminal.Sales.OrderQuickDiscount10Percent, "整单快捷九折", "POS 销售", "收银端销售页 - 整单快捷 10% 折扣"),
                new(Permissions.PosTerminal.Sales.OrderQuickDiscount20Percent, "整单快捷八折", "POS 销售", "收银端销售页 - 整单快捷 20% 折扣"),
                new(Permissions.PosTerminal.Sales.OrderQuickDiscount30Percent, "整单快捷七折", "POS 销售", "收银端销售页 - 整单快捷 30% 折扣"),
                new(Permissions.PosTerminal.Sales.OrderQuickDiscount40Percent, "整单快捷六折", "POS 销售", "收银端销售页 - 整单快捷 40% 折扣"),
                new(Permissions.PosTerminal.Sales.OrderQuickDiscount50Percent, "整单快捷五折", "POS 销售", "收银端销售页 - 整单快捷 50% 折扣"),
                new(Permissions.PosTerminal.Sales.ClearCart, "清空购物车", "POS 销售", "收银端销售页 - 清空购物车按钮"),
                new(Permissions.PosTerminal.Sales.HoldOrder, "挂起订单", "POS 销售", "收银端销售页 - 挂起订单按钮"),
                new(Permissions.PosTerminal.Sales.RecallOrder, "召回挂单", "POS 销售", "收银端销售页 - 召回挂单按钮"),
                new(Permissions.PosTerminal.Payment.View, "查看收款页", "POS 收款", "收银端收款页 - 进入收款页面"),
                new(Permissions.PosTerminal.Payment.TakeCash, "现金收款", "POS 收款", "收银端收款页 - 现金收款按钮"),
                new(Permissions.PosTerminal.Payment.TakeCard, "刷卡收款", "POS 收款", "收银端收款页 - 刷卡收款按钮"),
                new(Permissions.PosTerminal.Payment.TakeVoucher, "代金券收款", "POS 收款", "收银端收款页 - 代金券收款按钮"),
                new(Permissions.PosTerminal.Payment.RemoveTender, "移除收款", "POS 收款", "收银端收款页 - 移除已录入收款按钮"),
                new(Permissions.PosTerminal.Payment.Confirm, "确认收款", "POS 收款", "收银端收款页 - 确认完成收款按钮"),
                new(Permissions.PosTerminal.Returns.View, "查看退货页", "POS 退货", "收银端退货页 - 进入退货页面"),
                new(Permissions.PosTerminal.Returns.AddReceiptLine, "添加小票退货行", "POS 退货", "收银端退货页 - 添加原小票商品按钮"),
                new(Permissions.PosTerminal.Returns.AddNoReceiptItem, "添加无小票退货商品", "POS 退货", "收银端退货页 - 添加无小票商品按钮"),
                new(Permissions.PosTerminal.Returns.Confirm, "确认退货", "POS 退货", "收银端退货页 - 确认退货按钮"),
                new(Permissions.PosTerminal.SpecialProducts.View, "查看特价商品页", "POS 特价商品", "收银端特价商品页 - 进入特价商品页面"),
                new(Permissions.PosTerminal.SpecialProducts.AddToCart, "添加特价商品到购物车", "POS 特价商品", "收银端特价商品页 - 加入购物车按钮"),
                new(Permissions.PosTerminal.SpecialProducts.Manage, "管理特价商品", "POS 特价商品", "收银端特价商品页 - 管理特价商品按钮"),
                new(Permissions.PosTerminal.History.View, "查看历史订单页", "POS 历史订单", "收银端历史订单页 - 进入历史订单页面"),
                new(Permissions.PosTerminal.History.Recall, "召回历史订单", "POS 历史订单", "收银端历史订单页 - 召回订单按钮"),
                new(Permissions.PosTerminal.History.Reprint, "重打历史订单小票", "POS 历史订单", "收银端历史订单页 - 重打小票按钮"),
                new(Permissions.PosTerminal.DailyClose.View, "查看日结页", "POS 日结", "收银端日结页 - 进入日结页面"),
                new(Permissions.PosTerminal.DailyClose.Save, "保存日结", "POS 日结", "收银端日结页 - 保存日结按钮"),
                new(Permissions.PosTerminal.DailyClose.Reprint, "重打日结小票", "POS 日结", "收银端日结页 - 重打日结小票按钮"),
                new(Permissions.PosTerminal.Installments.View, "查看分期页", "POS 分期", "收银端分期页 - 进入分期页面"),
                new(Permissions.PosTerminal.Installments.Create, "创建分期", "POS 分期", "收银端分期页 - 创建分期按钮"),
                new(Permissions.PosTerminal.Installments.AddRepayment, "添加分期还款", "POS 分期", "收银端分期页 - 添加还款按钮"),
                new(Permissions.PosTerminal.Installments.Cancel, "取消分期", "POS 分期", "收银端分期页 - 取消分期按钮"),
                new(Permissions.PosTerminal.Installments.ConfirmPickup, "确认分期取货", "POS 分期", "收银端分期页 - 确认取货按钮"),
                new(Permissions.PosTerminal.Settings.View, "查看设置页", "POS 设置", "收银端设置页 - 进入设置页面"),
                new(Permissions.PosTerminal.Settings.PaymentTerminal, "设置支付终端", "POS 设置", "收银端设置页 - 支付终端设置按钮"),
                new(Permissions.PosTerminal.Settings.ReceiptPrinter, "设置小票打印机", "POS 设置", "收银端设置页 - 小票打印机设置按钮"),
                new(Permissions.PosTerminal.Settings.CatalogDownload, "下载商品目录", "POS 设置", "收银端设置页 - 下载商品目录按钮"),
                new(Permissions.PosTerminal.Settings.CatalogReset, "重置商品目录", "POS 设置", "收银端设置页 - 重置商品目录按钮"),
                new(Permissions.PosTerminal.Settings.TestDataReset, "重置测试数据", "POS 设置", "收银端设置页 - 重置测试数据按钮"),
                new(Permissions.PosTerminal.Settings.DeviceRegistration, "设备注册设置", "POS 设置", "收银端设置页 - 设备注册按钮"),
                new(Permissions.PosTerminal.Settings.AppUpdate, "应用更新设置", "POS 设置", "收银端设置页 - 应用更新按钮"),
                new(Permissions.PosTerminal.CashDrawer.Open, "打开钱箱", "POS 钱箱", "收银端钱箱 - 打开钱箱按钮"),
                new(Permissions.PosTerminal.Receipt.PrintLast, "打印上一张小票", "POS 小票", "收银端小票 - 打印上一张小票按钮"),
                new(Permissions.PosTerminal.CustomerDisplay.Manage, "管理客显", "POS 客显", "收银端客显 - 管理客显按钮"),
                new(Permissions.PosTerminal.System.Sync, "同步收银数据", "POS 同步", "收银端同步 - 手动同步按钮"),
                new(Permissions.PosTerminal.Audit.View, "查看员工操作日志", "POS 审计", "收银端操作审计 - Web 页面 /pos-admin/operation-logs 与移动端工作台「员工操作日志」查看管理分店的员工操作记录"),
                new(Permissions.Promotions.View, "查看促销", "促销管理", "Web 页面 /pos-admin/promotions 与移动端「促销」- 查看促销活动"),
                // 全局促销的写接口额外要求 Admin 角色，属有意设计；分店促销仅需本权限。
                new(Permissions.Promotions.Edit, "编辑促销", "促销管理", "Web 页面 /pos-admin/promotions - 编辑分店促销；全局促销的新增、修改、删除、启停另需管理员角色"),
                new(Permissions.Advertisements.View, "查看广告素材", "广告管理", "Web 页面 /pos-admin/advertisements 与移动端「广告」- 查看广告素材列表与详情"),
                new(Permissions.Advertisements.Edit, "编辑广告素材", "广告管理", "Web 页面 /pos-admin/advertisements - 新增、编辑、删除、启用与上传广告素材"),
                // 仅注册权限，不写入角色模板；定价策略直接影响分店售价，由管理员显式授予。
                new(Permissions.PricingStrategy.View, "查看定价策略", "定价策略", "Web 页面 /pos-admin/pricing-strategies - 查看与试算自动价格策略"),
                new(Permissions.PricingStrategy.Edit, "编辑定价策略", "定价策略", "Web 页面 /pos-admin/pricing-strategies - 新增、编辑、删除自动价格策略"),
                new(Permissions.DeviceRegistration.View, "查看设备注册", "系统管理", "Web 页面 /system/device-registration 与移动端「设备管理」- 查看 POS 设备注册列表与状态"),
                new(Permissions.DeviceRegistration.Manage, "管理设备注册", "系统管理", "Web 页面 /system/device-registration - 审核、维护或管理设备注册"),
                // 仅注册权限，不写入 StoreManager/WarehouseManager 角色模板；手工委派仍受可管理分店范围约束。
                new(Permissions.DeviceRegistration.ActivationCodes.Manage, "管理设备开通码", "系统管理", "Web 页面 /system/device-registration - 为可管理分店创建和撤销一次性 POS 设备开通码"),
                // Mobile 设备绑定可直接继承目标账号权限，必须与 POS 开通码使用独立的高敏权限。
                new(Permissions.DeviceRegistration.MobileActivationCodes.Manage, "管理移动端设备开通码", "系统管理", "Web 页面 /system/device-registration - 为指定账号创建和撤销一次性 Mobile 设备开通码"),
                new(Permissions.LocalPurchase.View, "查看本地进货", "本地进货管理", "Web 页面 /pos-admin/local-supplier-invoices 与移动端「进货单」- 查看分店进货单"),
                new(Permissions.LocalPurchase.MobileView, "移动端查看澳洲进货", "本地进货管理", "移动端「进货单」- 查看已授权分店的澳洲进货单列表与详情"),
                new(Permissions.LocalPurchase.Edit, "编辑本地进货", "本地进货管理", "Web 页面 /pos-admin/local-supplier-invoices - 新增、编辑、提交和维护分店进货单"),
                new(Permissions.LocalPurchase.PushToHq, "推送本地进货到 HQ", "本地进货管理", "Web 页面 /pos-admin/local-supplier-invoices - 推送本地进货单到 HQ"),
                new(Permissions.AustralianSuppliers.View, "查看澳洲供应商", "澳洲供应商", "Web 页面 /pos-admin/suppliers - 查看澳洲供应商分页列表"),
                new(Permissions.AustralianSuppliers.Edit, "编辑澳洲供应商", "澳洲供应商", "Web 页面 /pos-admin/suppliers - 新增、编辑、启停澳洲供应商并与 HQ 同步"),
                new(Permissions.Store.ManageOperations, "管理分店运营", "分店运营", "Web 页面 /pos-admin/cash-register-users - 管理收银用户条码"),
                new(Permissions.CashRegisterUsers.MobileManage, "移动端管理收银用户条码", "分店运营", "移动端「收银用户条码」- 查看、创建、更新和启停可管理分店的老收银系统员工条码"),
                new(Permissions.CashRegisterUsers.MobilePrint, "移动端打印收银用户条码", "分店运营", "移动端「收银用户条码」- 查看可管理分店的老收银系统员工条码，用蓝牙标签机打印并累计打印次数"),
                new(Permissions.System.ViewLogs, "查看日志", "系统管理", "Web 页面 /system/center-logs - 查看系统中心日志"),
                new(Permissions.System.ManageScheduledTasks, "管理定时任务", "系统管理", "Web 页面 /system/scheduled-statistics - 切换定时任务调度实例和运行开关"),
                new(Permissions.System.ManageSettings, "管理设置", "系统管理", "Web 页面 /system/invoice-email-settings、/system/payment-terminal-settings、/system/emergency-login-keys - 管理发票邮箱、支付终端与紧急登录密钥"),
                // 仅注册权限，不写入角色模板，避免默认扩大 App 下载入口访问面。
                new(Permissions.System.ViewAppDownloads, "查看 App 下载", "系统管理", "Web 页面 /system/app-downloads、/system/wpf-versions（移动端同名入口另需管理员）- 查看 App 下载与 WPF 版本"),
                new(Permissions.System.ManageAppDownloads, "管理 App 下载", "系统管理", "系统管理 - 登记 OTA 更新和生成回撤命令"),
                // 仅注册权限，不写入角色模板；由管理员按需授予，只读展示最新正式版安装链接。
                new(Permissions.System.ViewMobileAppInstallLinks, "移动端查看 App 安装二维码", "系统管理", "移动端「App 安装」- 查看 iOS 与 Android 最新正式版本及安装二维码"),
                // 仅注册权限，不写入角色模板；由管理员显式授予查看或冻结基线能力。
                new(Permissions.System.ViewPerformanceBaseline, "查看性能与质量基线", "系统管理", "Web 页面 /system/performance-baseline - 查看性能与质量指标"),
                new(Permissions.System.ManagePerformanceBaseline, "管理性能与质量基线", "系统管理", "Web 页面 /system/performance-baseline - 冻结已满足观察期和覆盖率的基线"),
                new(Permissions.Dashboard.View, "访问后台", "后台管理", "Web 页面 /dashboard - 访问后台工作台"),
                new(Permissions.OrderFront.View, "前台订货", "前台订货", "Web 订货前台与移动端「订货单」- 进入前台订货入口（Order / 订货员角色核心权限）"),
                // 仅注册权限，不写入订货员角色模板；由管理员显式授予后才能在前台查看全部分店销量。
                new(Permissions.OrderFront.BatchProductSalesView, "前台查看货号销量", "前台订货", "Web 订货前台页面 /shop/purchase-sales-analysis「粘贴数据查看」标签 - 按导入货号查看全部分店的销量、每日趋势及折扣成交情况"),
            };

        public static IReadOnlyList<RolePermissionTemplateDefinition> RolePermissionTemplates { get; } =
            new List<RolePermissionTemplateDefinition>
            {
                new("Admin", Array.Empty<string>()),
                new(
                    "WarehouseManager",
                    new[]
                    {
                        Permissions.Stores.View,
                        Permissions.Stores.Create,
                        Permissions.Stores.Edit,
                        Permissions.Stores.Delete,
                        Permissions.Stores.Sync,
                        Permissions.Products.View,
                        Permissions.Products.Create,
                        Permissions.Products.Edit,
                        Permissions.Products.Delete,
                        Permissions.Orders.View,
                        Permissions.Orders.Create,
                        Permissions.Orders.Edit,
                        Permissions.Orders.Delete,
                        Permissions.SalesOrders.View,
                        Permissions.InstallmentOrders.View,
                        Permissions.Container.View,
                        Permissions.Container.Create,
                        Permissions.Container.Edit,
                        Permissions.Container.Delete,
                        Permissions.Warehouse.Manage,
                        Permissions.Warehouse.ManageProducts,
                        Permissions.Warehouse.ManageCategories,
                        Permissions.Warehouse.ManageLocations,
                        Permissions.Warehouse.ManageOrders,
                        Permissions.DomesticPurchase.ManageSuppliers,
                        Permissions.DomesticPurchase.ManageProducts,
                        Permissions.DomesticPurchase.ManagePrefixCodes,
                        Permissions.Reports.View,
                        // 澳洲供应商接口原按 WarehouseManager 角色放行，改为权限策略后由模板保留原能力。
                        Permissions.AustralianSuppliers.View,
                        Permissions.AustralianSuppliers.Edit,
                        Permissions.Dashboard.View,
                    }
                ),
                new(
                    "WarehouseStaff",
                    new[]
                    {
                        Permissions.Warehouse.Manage,
                        Permissions.Warehouse.ManageProducts,
                        Permissions.Warehouse.ManageLocations,
                    }
                ),
                new("StoreManager", StoreManagerPermissionCodes),
                // 中文店长别名只补用户查看和 POS 权限管理，不扩大其他系统管理能力。
                new("店长", new[] { Permissions.PosTerminal.Audit.View, Permissions.Users.View, Permissions.Users.ManagePosTerminalPermissions, Permissions.EmployeeProfiles.ReviewSensitiveManagedStore }),
                new("经理", new[] { Permissions.PosTerminal.Audit.View, Permissions.Users.View, Permissions.Users.ManagePosTerminalPermissions, Permissions.EmployeeProfiles.ReviewSensitiveManagedStore }),
                new("Manager", AttendanceSelfServicePermissionCodes),
                new("User", AttendanceSelfServicePermissionCodes),
                new("StoreStaff", AttendanceSelfServicePermissionCodes),
                new("Order", OrderRolePermissionCodes),
                new("订货员", OrderRolePermissionCodes),
            };

        public static IReadOnlyList<PermissionSeedDefinition> AllPermissions { get; } =
            SharedPermissionSeeds
            .Concat(AttendancePermissions)
            .Concat(SeasonalCardPermissions)
            .Concat(SalesDashboardPermissions)
            .GroupBy(seed => seed.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
    }
}
