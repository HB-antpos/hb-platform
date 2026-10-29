using System.Security.Claims;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Services
{
    public interface INavigationService
    {
        List<NavigationMenuDto> BuildMenu(ClaimsPrincipal user);
        List<AppNavigationMenuDto> BuildAppMenu(ClaimsPrincipal user);
        List<AppNavigationMenuDto> BuildDeviceAppMenu(string? deviceType);
    }

    public class NavigationService : INavigationService
    {
        private readonly IRoleService? _roleService;

        private sealed class NavigationPermissionContext
        {
            public bool IsAdmin { get; init; }
            public HashSet<string> RoleNames { get; init; } =
                new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> PermissionCodes { get; init; } =
                new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> ExactPermissionCodes { get; init; } =
                new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class AppNavigationDefinition
        {
            public string RouteName { get; init; } = string.Empty;
            public string TitleKey { get; init; } = string.Empty;
            public string Icon { get; init; } = string.Empty;
            public string? Permission { get; init; }
            public IReadOnlyCollection<string>? AnyPermissions { get; init; }
            public bool RequireAdmin { get; init; }
            public int Order { get; init; }
        }

        private static readonly string[] AttendanceManagementPermissions =
        {
            Permissions.Attendance.Schedule.ViewStore,
            Permissions.Attendance.Schedule.EditManagedStore,
            Permissions.Attendance.Availability.ViewManagedStore,
            Permissions.Attendance.Punch.ViewManagedStore,
            Permissions.Attendance.Approval.ViewManagedStore,
            Permissions.Attendance.Approval.ReviewManagedStore,
            Permissions.Attendance.Holiday.ViewStore,
            Permissions.Attendance.Holiday.EditManagedStore,
            Permissions.Attendance.Leave.ViewManagedStore,
            Permissions.Attendance.Leave.ReviewManagedStore,
            Permissions.Attendance.Settings.Edit,
            Permissions.Attendance.Admin.View,
        };

        /// <summary>
        /// 完整导航树（与前端 routes.tsx 结构一致）
        /// Permission 字段为空表示所有人可见
        /// </summary>
        private static readonly List<NavigationMenuDto> FullMenu = new()
        {
            new()
            {
                Path = "/dashboard",
                TitleKey = "menu.dashboard",
                Icon = "DashboardOutlined",
                Permission = Permissions.Dashboard.View,
            },
            new()
            {
                Path = "/system",
                TitleKey = "menu.system",
                Icon = "SettingOutlined",
                Children = new List<NavigationMenuDto>
                {
                    new() { Path = "/system/stores",   TitleKey = "menu.systemStores",      Icon = "ShopOutlined",    Permission = Permissions.Stores.View },
                    // 合并页（一个菜单 + 页内标签）：菜单项为合并页地址，任一标签权限即可见；标签仍是原页面，地址与权限不变，
                    // 页内按各标签原权限显示（与 Web routes.tsx 的 *_NAV_GROUP 保持一致）。仅管理员的标签（RustDesk 远程维护、Linkly 结算）不列权限码。
                    // 设备与远程：设备管理 / 紧急登录密钥 / RustDesk 远程维护。
                    new() { Path = "/system/devices", TitleKey = "menu.groupDevicesRemote", Icon = "BuildOutlined", AnyPermissions = new List<string> { Permissions.DeviceRegistration.View, Permissions.System.ManageSettings } },
                    // 账号与权限：员工个人信息 / 用户 / 角色 / 权限。Web 用户管理只认 Users.ViewWebConsole；Users.View 继续点亮移动端「员工列表」「用户管理」。
                    new() { Path = "/system/accounts", TitleKey = "menu.groupAccounts", Icon = "TeamOutlined", AnyPermissions = new List<string> { Permissions.EmployeeProfiles.ViewManagedStore, Permissions.Users.ViewWebConsole, Permissions.Roles.View } },
                    // 系统监控：中心日志 / 性能质量基线 / 定时统计任务。
                    new() { Path = "/system/monitoring", TitleKey = "menu.groupMonitoring", Icon = "FileTextOutlined", AnyPermissions = new List<string> { Permissions.System.ViewLogs, Permissions.System.ViewPerformanceBaseline, Permissions.System.ManageScheduledTasks } },
                    // 支付与发票：支付终端配置 / 发票邮箱配置。
                    new() { Path = "/system/payments-invoices", TitleKey = "menu.groupPaymentsInvoices", Icon = "WalletOutlined", Permission = Permissions.System.ManageSettings },
                    // 人脸识别：人脸抓拍摄像机 / 人脸比对（比对五个独立权限，任一即可见，页内再按各自权限显示页签）。
                    new() { Path = "/system/face-recognition", TitleKey = "menu.groupFaceRecognition", Icon = "VideoCameraOutlined", AnyPermissions = new List<string> { Permissions.FaceCapture.Cameras.Manage, Permissions.FaceMatch.Manage, Permissions.FaceMatch.Traffic.View, Permissions.FaceMatch.Unwelcome.Manage, Permissions.FaceMatch.Unwelcome.Approve, Permissions.FaceMatch.Unwelcome.Review } },
                    // 版本发布中心：App 原生 / OTA 与 WPF 安装包合并为一页，旧 /system/wpf-versions 由 Web 重定向到其 WPF 终端。
                    new() { Path = "/system/app-downloads", TitleKey = "menu.appDownloads", Icon = "QrcodeOutlined", Permission = Permissions.System.ViewAppDownloads },
                },
            },
            new()
            {
                Path = "/warehouse",
                TitleKey = "menu.warehouse",
                Icon = "DatabaseOutlined",
                Children = new List<NavigationMenuDto>
                {
                    new() { Path = "/warehouse/store-orders", TitleKey = "menu.storeOrders", Icon = "ReconciliationOutlined", Permission = Permissions.Warehouse.ManageOrders },
                    new() { Path = "/warehouse/preorders", TitleKey = "menu.preorders", Icon = "ScheduleOutlined", Permission = Permissions.Warehouse.ManageOrders },
                    new() { Path = "/warehouse/store-order-import-price-variance", TitleKey = "menu.storeOrderImportPriceVariance", Icon = "BarChartOutlined", Permission = Permissions.Warehouse.ManageOrders },
                    new() { Path = "/warehouse/containers",   TitleKey = "menu.containers", Icon = "InboxOutlined",          Permission = Permissions.Container.View },
                    new() { Path = "/warehouse/products",     TitleKey = "menu.warehouseProducts", Icon = "AppstoreOutlined",   Permission = Permissions.Warehouse.ManageProducts },
                    new() { Path = "/warehouse/categories",   TitleKey = "menu.categories",        Icon = "TagsOutlined",       Permission = Permissions.Warehouse.ManageCategories },
                    new() { Path = "/warehouse/locations",    TitleKey = "menu.warehouseLocations", Icon = "EnvironmentOutlined", Permission = Permissions.Warehouse.ManageLocations },
                    new() { Path = "/warehouse/product-grade-management", TitleKey = "menu.productGradeManagement", Icon = "TrophyOutlined", Permission = Permissions.Warehouse.ManageProducts },
                },
            },
            new()
            {
                Path = "/domestic-purchase",
                TitleKey = "menu.domesticPurchase",
                Icon = "ShoppingCartOutlined",
                Children = new List<NavigationMenuDto>
                {
                    new() { Path = "/domestic-purchase/china-suppliers",       TitleKey = "menu.chinaSuppliers",        Icon = "BankOutlined",       Permission = Permissions.DomesticPurchase.ManageSuppliers },
                    new() { Path = "/domestic-purchase/domestic-products",     TitleKey = "menu.domesticProducts",      Icon = "AppstoreOutlined",   Permission = Permissions.Products.View },
                    new() { Path = "/domestic-purchase/prefix-code-management", TitleKey = "menu.prefixCodeManagement",  Icon = "NumberOutlined",     Permission = Permissions.DomesticPurchase.ManagePrefixCodes },
                    new() { Path = "/domestic-purchase/product-creation",       TitleKey = "menu.productCreation",       Icon = "BuildOutlined",      Permission = Permissions.DomesticPurchase.ManageProducts },
                    new() { Path = "/domestic-purchase/product-import",        TitleKey = "menu.productImport",         Icon = "InboxOutlined",      Permission = Permissions.DomesticPurchase.ManageProducts },
                },
            },
            new()
            {
                // 供应商节日预订单：下单入口给被授权的下单人员，管理两页只认 SupplierPreorder.Manage。
                Path = "/seasonal-preorders",
                TitleKey = "menu.seasonalPreorders",
                Icon = "GiftOutlined",
                Children = new List<NavigationMenuDto>
                {
                    new() { Path = "/supplier-preorder-ordering", TitleKey = "menu.supplierPreorderOrdering", Icon = "ShoppingCartOutlined", AnyPermissions = new List<string> { Permissions.SupplierPreorder.Order, Permissions.SupplierPreorder.Manage } },
                    new() { Path = "/supplier-preorders", TitleKey = "menu.supplierPreorders", Icon = "FileExcelOutlined", Permission = Permissions.SupplierPreorder.Manage },
                    new() { Path = "/supplier-preorders/orderers", TitleKey = "menu.supplierPreorderOrderers", Icon = "TeamOutlined", Permission = Permissions.SupplierPreorder.Manage },
                },
            },
            new()
            {
                Path = "/executive-sales-intelligence",
                TitleKey = "menu.executiveSalesIntelligence",
                Icon = "BarChartOutlined",
                Children = new List<NavigationMenuDto>
                {
                    new() { Path = "/executive-sales-intelligence/overview",       TitleKey = "menu.salesData",   Icon = "DashboardOutlined", Permission = Permissions.SalesDashboard.SalesDataView },
                    new() { Path = "/executive-sales-intelligence/sales-detail-v2", TitleKey = "menu.salesDetail", Icon = "FileTextOutlined",  Permission = Permissions.SalesDashboard.SalesDetailView },
                    new() { Path = "/executive-sales-intelligence/compact-sales-board", TitleKey = "menu.compactSalesBoard", Icon = "BarChartOutlined", Permission = Permissions.SalesDashboard.CompactBoardView },
                    new() { Path = "/executive-sales-intelligence/monthly-daily-sales-download", TitleKey = "menu.monthlyDailySalesDownload", Icon = "DownloadOutlined", Permission = Permissions.SalesDashboard.MonthlyDailySalesDownloadView },
                    new() { Path = "/executive-sales-intelligence/product-movement-report", TitleKey = "menu.productMovementReport", Icon = "ReconciliationOutlined", Permission = Permissions.SalesDashboard.ProductMovementView },
                    // 进货销量分析：批量货号销量与分店进货销量分析合为一页两个标签，任一页权限即显示入口，标签再按各自权限显示。
                    new() { Path = "/executive-sales-intelligence/purchase-sales-analysis", TitleKey = "menu.purchaseSalesAnalysis", Icon = "BarChartOutlined", AnyPermissions = new List<string> { Permissions.SalesDashboard.BatchProductSalesView, Permissions.SalesDashboard.LocalSupplierPurchaseSalesView } },
                    new() { Path = "/executive-sales-intelligence/warehouse-product-flow-analysis", TitleKey = "menu.warehouseProductFlowAnalysis", Icon = "BarChartOutlined", Permission = Permissions.SalesDashboard.WarehouseFlowView },
                    new() { Path = "/executive-sales-intelligence/local-product-sales-analysis", TitleKey = "menu.localProductSalesAnalysis", Icon = "BarChartOutlined", Permission = Permissions.SalesDashboard.LocalProductAnalysisView },
                    new() { Path = "/executive-sales-intelligence/purchase-amount-dashboard", TitleKey = "menu.purchaseAmountDashboard", Icon = "DollarOutlined", Permission = Permissions.SalesDashboard.PurchaseAmountView },
                },
            },
            new()
            {
                Path = "/pos-admin",
                TitleKey = "menu.posAdmin",
                Icon = "WalletOutlined",
                Children = new List<NavigationMenuDto>
                {
                    new() { Path = "/pos-admin/suppliers",              TitleKey = "menu.suppliers",              Icon = "ShopOutlined",               Permission = Permissions.AustralianSuppliers.View },
                    // 商品与价格：商品管理 / 分店商品价格 / 自动价格策略 / 促销管理。
                    new() { Path = "/pos-admin/products-pricing",      TitleKey = "menu.groupProductsPricing",   Icon = "AppstoreOutlined",           AnyPermissions = new List<string> { Permissions.PosProducts.View, Permissions.StoreProducts.View, Permissions.PricingStrategy.View, Permissions.Promotions.View } },
                    // 结算与现金：日结记录（独立权限码，分店范围由查询接口收口）/ Linkly 结算记录（仅管理员）/ 现金管理（店长只见关联分店）。
                    new() { Path = "/pos-admin/settlement-cash",       TitleKey = "menu.groupSettlementCash",    Icon = "MoneyCollectOutlined",       AnyPermissions = new List<string> { Permissions.DailyCloseRecords.View, Permissions.Cash.OverviewView } },
                    new() { Path = "/pos-admin/advertisements",        TitleKey = "menu.advertisements",         Icon = "PictureOutlined",            Permission = Permissions.Advertisements.View },
                    // 收银用户条码：Web 侧栏不再显示（路由保留，旧链接仍可打开）；移动端同名菜单不受影响。
                    // 员工操作日志合并页：老收银 / 新收银在页内切换，任一查看权限即可见。
                    new() { Path = "/pos-admin/operation-logs",       TitleKey = "menu.operationLogs",          Icon = "FileTextOutlined",           AnyPermissions = new List<string> { Permissions.LegacyEmployeeLogs.View, Permissions.PosTerminal.Audit.View } },
                    // 节日贺卡分店填报统计：总部查看全部分店的剩余贺卡填报；独立权限码，店长的填报 / 本店查看权限不能打开。
                    new() { Path = "/pos-admin/seasonal-card-stats",  TitleKey = "menu.seasonalCardStats",      Icon = "GiftOutlined",               Permission = Permissions.SeasonalCards.Remaining.ViewAllStores },
                    // 排班考勤合并页：排班考勤 / 考勤审核两个标签，任一权限即可见（标签再按各自权限显示）。
                    // 考勤审核与 Web canReviewAttendance 一致，只认审批 / 驳回接口的权限。
                    new() { Path = "/pos-admin/attendance",            TitleKey = "menu.scheduleAttendance",     Icon = "CalendarOutlined",           AnyPermissions = new List<string> { Permissions.Attendance.Schedule.ViewStore, Permissions.Attendance.Approval.ReviewManagedStore } },
                    // 工时与工资合并页：工时统计 / 工资表 / 时薪表 / MYOB 对接，任一权限即可见（页内再按各自权限显示标签与页签）。
                    // 工时统计看打卡查看权限，工资表 / 时薪表看 Payroll.View；MYOB 对接页签：连接 / 分店映射、员工绑定看独立权限码（默认不授予任何角色）；发薪公司页签看 Attendance.Payroll.Edit，但工资编辑不隐含查看，不单独打开本菜单。
                    new() { Path = "/pos-admin/payroll",               TitleKey = "menu.attendancePayroll",      Icon = "AccountBookOutlined",        AnyPermissions = new List<string> { Permissions.Attendance.Punch.ViewManagedStore, Permissions.Attendance.Payroll.View, Permissions.Myob.Connection.Manage, Permissions.Myob.Employee.Bind, Permissions.Myob.Payslip.ViewManagedStore, Permissions.Myob.Payslip.ViewAllStores } },
                    new() { Path = "/pos-admin/sales-orders",          TitleKey = "menu.salesOrders",            Icon = "FileDoneOutlined",           Permission = Permissions.Orders.View },
                    new() { Path = "/pos-admin/local-supplier-invoices", TitleKey = "menu.localSupplierInvoices", Icon = "ReconciliationOutlined",     Permission = Permissions.LocalPurchase.View },
                },
            },
        };

        private static readonly List<AppNavigationDefinition> FullAppMenu = new()
        {
            new()
            {
                RouteName = "home",
                TitleKey = "tabs.home",
                Icon = "home",
                Permission = Permissions.Orders.Create,
                Order = 10,
            },
            new()
            {
                RouteName = "orders",
                TitleKey = "tabs.orders",
                Icon = "clipboard-list",
                Permission = Permissions.Orders.View,
                AnyPermissions = new[]
                {
                    Permissions.OrderFront.View,
                    Permissions.Orders.View,
                    Permissions.Warehouse.ManageOrders,
                    Permissions.Warehouse.Manage,
                },
                Order = 20,
            },
            new()
            {
                RouteName = "sales-orders",
                TitleKey = "tabs.salesOrders",
                Icon = "receipt-text-outline",
                // 移动端销售订单查询使用独立权限，由管理员显式授予；不随 Web 收银记录页的 Orders.View 放行。
                Permission = Permissions.SalesOrders.View,
                Order = 21,
            },
            new()
            {
                RouteName = "cart",
                TitleKey = "tabs.cart",
                Icon = "cart-outline",
                Permission = Permissions.Orders.Create,
                Order = 30,
            },
            new()
            {
                // 商品和货位管理：仓库商品与货位维护；货柜管理已拆为独立入口，不再凭 Container.View 放行。
                RouteName = "warehouse",
                TitleKey = "tabs.warehouse",
                Icon = "warehouse",
                Permission = Permissions.Warehouse.ManageProducts,
                Order = 40,
            },
            new()
            {
                // 货柜管理：原先是仓库页内的卡片，现为工作台「仓库与采购」组下的独立入口，只看 Container.View。
                RouteName = "containers",
                TitleKey = "tabs.containers",
                Icon = "archive-outline",
                Permission = Permissions.Container.View,
                Order = 40,
            },
            new()
            {
                RouteName = "container-new-products",
                TitleKey = "tabs.containerNewProducts",
                Icon = "package-variant-closed",
                Permission = Permissions.Container.MobileNewProductsView,
                Order = 41,
            },
            new()
            {
                RouteName = "warehouse-picking",
                TitleKey = "tabs.warehousePicking",
                Icon = "clipboard-check-outline",
                // 拣货权限按别名覆盖管理仓库与管理订货；菜单显式列出，避免依赖客户端别名展开。
                Permission = Permissions.Warehouse.Picking,
                AnyPermissions = new[]
                {
                    Permissions.Warehouse.Picking,
                    Permissions.Warehouse.Manage,
                    Permissions.Warehouse.ManageOrders,
                },
                Order = 42,
            },
            new()
            {
                RouteName = "domestic-purchase",
                TitleKey = "tabs.domesticPurchase",
                Icon = "shopping-outline",
                Permission = Permissions.DomesticPurchase.ManageProducts,
                Order = 45,
            },
            new()
            {
                RouteName = "local-supplier-invoices",
                TitleKey = "tabs.localSupplierInvoices",
                Icon = "receipt-text-outline",
                Permission = Permissions.LocalPurchase.View,
                AnyPermissions = new[]
                {
                    Permissions.LocalPurchase.MobileView,
                    Permissions.LocalPurchase.View,
                },
                Order = 46,
            },
            new()
            {
                RouteName = "advertisements",
                TitleKey = "tabs.advertisements",
                Icon = "image-multiple",
                Permission = Permissions.Advertisements.View,
                Order = 47,
            },
            new()
            {
                RouteName = "promotions",
                TitleKey = "tabs.promotions",
                Icon = "ticket-percent-outline",
                Permission = Permissions.Promotions.View,
                Order = 48,
            },
            new()
            {
                RouteName = "product-query",
                TitleKey = "tabs.productQuery",
                Icon = "barcode-scan",
                Permission = Permissions.StoreProducts.View,
                Order = 50,
            },
            new()
            {
                RouteName = "price-updates",
                TitleKey = "tabs.priceUpdates",
                Icon = "tag-arrow-up-outline",
                // 专用权限：持有者只能按仓库目标价改本店价格，与"编辑分店商品"解耦。
                Permission = Permissions.StoreProducts.PriceUpdates,
                Order = 50,
            },
            new()
            {
                RouteName = "pricing-strategies",
                TitleKey = "tabs.pricingStrategies",
                Icon = "chart-bell-curve-cumulative",
                // 移动端「自动价格策略」：与 Web /pos-admin/pricing-strategies 同一对权限码；入口只认 View，
                // 新建 / 修改 / 删除 / 启停由接口按 PricingStrategy.Edit 再校验。设备会话没有用户账号，不在 DeviceBaseRouteNames 里。
                Permission = Permissions.PricingStrategy.View,
                Order = 50,
            },
            new()
            {
                RouteName = "product-insights",
                TitleKey = "tabs.productInsights",
                Icon = "chart-timeline-variant",
                // 与商品查询共用查看授权，避免客户端入口与设备菜单出现范围偏差。
                Permission = Permissions.StoreProducts.View,
                Order = 50,
            },
            new()
            {
                RouteName = "warehouse-product-insights",
                TitleKey = "tabs.warehouseProductInsights",
                Icon = "warehouse",
                // 与 Web 仓库商品流转分析共用授权，避免同一份数据在两端出现不同的可见范围。
                Permission = Permissions.SalesDashboard.WarehouseFlowView,
                Order = 50,
            },
            new()
            {
                RouteName = "seasonal-product-insights",
                TitleKey = "tabs.seasonalProductInsights",
                Icon = "calendar-star",
                // 独立权限：会展示其他分店库存，不借用商品查询的 StoreProducts.View。
                Permission = Permissions.SeasonalProductInsights.View,
                Order = 50,
            },
            new()
            {
                RouteName = "installment-orders",
                TitleKey = "tabs.installmentOrders",
                Icon = "cash-clock",
                Permission = Permissions.InstallmentOrders.View,
                Order = 51,
            },
            new()
            {
                RouteName = "store-vouchers",
                TitleKey = "tabs.storeVouchers",
                Icon = "ticket-percent-outline",
                Permission = Permissions.StoreVouchers.View,
                Order = 52,
            },
            new()
            {
                RouteName = "store-cash",
                TitleKey = "tabs.storeCash",
                Icon = "bank-transfer",
                Permission = Permissions.Cash.OverviewView,
                Order = 53,
            },
            new()
            {
                RouteName = "attendance-personal",
                TitleKey = "tabs.attendancePersonal",
                Icon = "calendar-clock",
                Permission = Permissions.Attendance.Schedule.ViewSelf,
                Order = 55,
            },
            new()
            {
                RouteName = "attendance-management",
                TitleKey = "tabs.attendanceManagement",
                Icon = "calendar-clock",
                Permission = Permissions.Attendance.Schedule.ViewStore,
                AnyPermissions = AttendanceManagementPermissions,
                Order = 56,
            },
            new()
            {
                RouteName = "attendance-hours",
                TitleKey = "tabs.attendanceHours",
                Icon = "clock-outline",
                // 移动端店长「工时统计」：与 Web 工时页签同用 Punch.ViewManagedStore，接口再按管理分店校验；
                // 设备会话没有用户账号，不在 DeviceBaseRouteNames 里。
                Permission = Permissions.Attendance.Punch.ViewManagedStore,
                Order = 56,
            },
            new()
            {
                RouteName = "seasonal-cards",
                TitleKey = "tabs.seasonalCards",
                Icon = "gift-outline",
                Permission = Permissions.SeasonalCards.Remaining.ViewManagedStore,
                AnyPermissions = new[]
                {
                    Permissions.SeasonalCards.Remaining.ViewManagedStore,
                    Permissions.SeasonalCards.Remaining.SubmitManagedStore,
                },
                Order = 56,
            },
            new()
            {
                RouteName = "users",
                TitleKey = "tabs.users",
                Icon = "account-group-outline",
                // 「员工管理」只认 ViewStoreStaff（Users.View 通过别名满足）；「用户管理」user-admin 仍用 Users.View。
                // 「人员管理」合并入口新增「资料」页签（员工个人信息维护），只持有 EmployeeProfiles.ViewManagedStore 的账号也要能进入。
                AnyPermissions = new[]
                {
                    Permissions.Users.ViewStoreStaff,
                    Permissions.EmployeeProfiles.ViewManagedStore,
                },
                Order = 57,
            },
            new()
            {
                RouteName = "legacy-employee-logs",
                TitleKey = "tabs.legacyEmployeeLogs",
                Icon = "clipboard-text-clock-outline",
                // 移动端「员工操作日志」：页内切换老收银 / 新收银，与 Web /pos-admin/operation-logs 同一组查看权限，任一即可见。
                // 路由名沿用 legacy-employee-logs，旧版本 App 收到新菜单仍能打开。
                // 设备模式菜单（BuildDeviceAppMenu）按 DeviceBaseRouteNames 白名单挑选，不会包含此项。
                AnyPermissions = new[] { Permissions.LegacyEmployeeLogs.View, Permissions.PosTerminal.Audit.View },
                Order = 57,
            },
            new()
            {
                RouteName = "daily-closes",
                TitleKey = "tabs.dailyCloses",
                Icon = "cash-register",
                // 移动端「日结记录」：与 Web /pos-admin/daily-closes 同一个独立权限码；店长只看自己关联的分店，由查询接口收口。
                // 设备模式菜单（BuildDeviceAppMenu）按 DeviceBaseRouteNames 白名单挑选，不会包含此项。
                Permission = Permissions.DailyCloseRecords.View,
                // 与声明位置（legacy-employee-logs 之后、user-admin 之前）同属 57：同值按声明顺序稳定排序，
                // Web 的 App 菜单预览（expoRoleMenuPreview）按源码声明顺序比对，必须一致。
                Order = 57,
            },
            new()
            {
                RouteName = "user-admin",
                TitleKey = "tabs.userAdmin",
                Icon = "account-cog-outline",
                Permission = Permissions.Users.View,
                Order = 57,
            },
            new()
            {
                RouteName = "cash-register-users",
                TitleKey = "tabs.cashRegisterUsers",
                Icon = "barcode",
                // 移动端独立权限：管理或打印任一即可进入，不借用 Web 的 Store.ManageOperations。
                AnyPermissions = new[]
                {
                    Permissions.CashRegisterUsers.MobileManage,
                    Permissions.CashRegisterUsers.MobilePrint,
                },
                Order = 57,
            },
            new()
            {
                RouteName = "roles",
                TitleKey = "tabs.roles",
                Icon = "shield-account-outline",
                Permission = Permissions.Roles.View,
                Order = 58,
            },
            new()
            {
                RouteName = "permissions",
                TitleKey = "tabs.permissions",
                Icon = "key-outline",
                // 与 Web 后台 /system/permissions 一致：读取只需 Roles.View，写操作由页面内按 Roles.ManagePermissions 控制。
                Permission = Permissions.Roles.View,
                Order = 58,
            },
            new()
            {
                RouteName = "employee-profile",
                TitleKey = "tabs.employeeProfile",
                Icon = "card-account-details-outline",
                Permission = Permissions.EmployeeProfiles.View,
                Order = 58,
            },
            new()
            {
                RouteName = "employee-profile-review",
                TitleKey = "tabs.employeeProfileReview",
                Icon = "account-check-outline",
                Permission = Permissions.EmployeeProfiles.ReviewSensitiveManagedStore,
                Order = 58,
            },
            new()
            {
                RouteName = "face-hub",
                TitleKey = "tabs.faceHub",
                Icon = "face-recognition",
                // 移动端「人脸比对」合并入口：把下面「不受欢迎人员名单 / 人脸比对设置 / 抓拍与比对」三个入口合成一个，
                // 进去是「抓拍 / 名单 / 设置」三个一级页签，页签与页内操作仍按各自的权限显示。
                // 六个权限码任一即可见，不合并成一个码、不新增权限码：
                //   抓拍 = FaceCapture.Images.View；名单 = FaceMatch.Unwelcome.Manage / Approve（条目）、Review（命中复核）；
                //   设置 = FaceMatch.Manage（门店档案）、FaceCapture.Cameras.Manage（摄像头）。
                // 命中通知红色数字只给持有 FaceMatch.Unwelcome.Review 的人（通知本来只发给他们），由客户端按权限决定是否取数。
                // 参考照片与命中抓拍是生物识别信息，设备会话没有用户账号，设备模式菜单（按 DeviceBaseRouteNames 白名单挑选）不会包含此项。
                AnyPermissions = new[]
                {
                    Permissions.FaceCapture.Images.View,
                    Permissions.FaceMatch.Unwelcome.Manage,
                    Permissions.FaceMatch.Unwelcome.Approve,
                    Permissions.FaceMatch.Unwelcome.Review,
                    Permissions.FaceMatch.Manage,
                    Permissions.FaceCapture.Cameras.Manage,
                },
                Order = 58,
            },
            // 下面三项（face-unwelcome / face-match-settings / face-capture-results）是合并入口 face-hub 之前的旧入口：
            // 为兼容还没升级的旧版 App 保持原样不动；新版 App 不再显示它们（路由保留并重定向到 face-hub，只供深链 / 通知跳转），
            // 待新版普及后再清理这三项。
            new()
            {
                RouteName = "face-unwelcome",
                TitleKey = "tabs.faceUnwelcome",
                Icon = "account-cancel-outline",
                // 移动端「不受欢迎人员名单」：条目维护 / 批准 / 命中复核共用 Web /system/face-match 的名单权限，三者任一即可见；
                // 页内按钮再按 GET /face-match/config 返回的权限标志精确控制。不新增权限码。
                // 参考照片和命中抓拍是生物识别信息，设备会话没有用户账号，设备模式菜单（按 DeviceBaseRouteNames 白名单挑选）不会包含此项。
                AnyPermissions = new[]
                {
                    Permissions.FaceMatch.Unwelcome.Manage,
                    Permissions.FaceMatch.Unwelcome.Approve,
                    Permissions.FaceMatch.Unwelcome.Review,
                },
                Order = 58,
            },
            new()
            {
                RouteName = "face-match-settings",
                TitleKey = "tabs.faceMatchSettings",
                Icon = "face-recognition",
                // 移动端「人脸比对设置」：门店档案区需要 FaceMatch.Manage，摄像头区需要 FaceCapture.Cameras.Manage，任一即可见；
                // 页内两个区域各自再按所需权限显示。不新增权限码；设备模式菜单不包含此项。
                AnyPermissions = new[]
                {
                    Permissions.FaceMatch.Manage,
                    Permissions.FaceCapture.Cameras.Manage,
                },
                Order = 58,
            },
            new()
            {
                RouteName = "face-capture-results",
                TitleKey = "tabs.faceCaptureResults",
                Icon = "face-recognition",
                // 移动端「抓拍与比对」：留存抓拍 + 比对结果 + 名单命中与通知发送情况，照片是生物识别信息，只认 FaceCapture.Images.View
                // （与 Web /system/face-match 的「抓拍与比对」页签同一权限，不新增权限码）；设备模式菜单不包含此项。
                Permission = Permissions.FaceCapture.Images.View,
                Order = 58,
            },
            new()
            {
                RouteName = "notifications",
                TitleKey = "tabs.notifications",
                Icon = "bell-outline",
                // 站内通知收件箱（通知平台 N1 的客户端）：任何登录账号都可见，不需要权限；设备会话没有用户账号，设备模式菜单不包含此项。
                Order = 59,
            },
            new()
            {
                RouteName = "device-management",
                TitleKey = "tabs.deviceManagement",
                Icon = "cellphone-cog",
                Permission = Permissions.DeviceRegistration.View,
                Order = 59,
            },
            new()
            {
                RouteName = "reports",
                TitleKey = "tabs.reports",
                Icon = "chart-box-outline",
                // 移动端报告入口承载营业额和商品报告；ProductMovementView 兼容 Reports.View。
                Permission = Permissions.Reports.ProductMovementView,
                Order = 59,
            },
            new()
            {
                RouteName = "traffic-stats",
                TitleKey = "tabs.trafficStats",
                Icon = "chart-line",
                // 移动端「客流统计」：只展示门店计数（进店人次 / 估算访客），不含任何生物信息；
                // 与 Web 客流统计共用 FaceMatch.Traffic.View，接口再按门店归属校验。设备会话没有用户账号，不在 DeviceBaseRouteNames 里。
                Permission = Permissions.FaceMatch.Traffic.View,
                Order = 59,
            },
            new()
            {
                // 普通员工安装入口：只读最新正式版安装二维码，不需要管理员身份。
                RouteName = "app-install",
                TitleKey = "tabs.appInstall",
                Icon = "qrcode",
                Permission = Permissions.System.ViewMobileAppInstallLinks,
                Order = 59,
            },
            new()
            {
                RouteName = "app-downloads",
                TitleKey = "tabs.appDownloads",
                Icon = "download-outline",
                Permission = Permissions.System.ViewAppDownloads,
                RequireAdmin = true,
                Order = 59,
            },
            new()
            {
                RouteName = "wpf-versions",
                TitleKey = "tabs.wpfVersions",
                Icon = "microsoft-windows",
                Permission = Permissions.System.ViewAppDownloads,
                RequireAdmin = true,
                Order = 59,
            },
            new()
            {
                RouteName = "settings",
                TitleKey = "tabs.settings",
                Icon = "account-circle-outline",
                Order = 60,
            },
        };

        private static readonly HashSet<string> DeviceBaseRouteNames = new(
            // price-updates：绑定分店的设备同样要处理本店的价格更新与换标签通知。
            new[] { "home", "orders", "cart", "product-query", "price-updates", "product-insights", "settings" },
            StringComparer.OrdinalIgnoreCase
        );

        // 仓库设备（纯设备会话，无个人账号）额外可见的入口；拣货人通过扫员工码确认。
        private static readonly HashSet<string> WarehouseDeviceRouteNames = new(
            new[] { "warehouse", "warehouse-picking" },
            StringComparer.OrdinalIgnoreCase
        );

        private static readonly HashSet<string> WarehouseDeviceTypes = new(
            new[] { "Warehouse", "PDA-Warehouse", "WarehousePDA", "PDAWarehouse", "仓库", "仓库设备" },
            StringComparer.OrdinalIgnoreCase
        );

        public NavigationService()
        {
        }

        public NavigationService(IRoleService roleService)
        {
            _roleService = roleService;
        }

        public List<NavigationMenuDto> BuildMenu(ClaimsPrincipal user)
        {
            var context = ResolvePermissionContext(user);

            if (context.IsAdmin)
            {
                return FullMenu;
            }

            if (IsOrderNavigationLimited(context))
            {
                return new List<NavigationMenuDto>();
            }

            if (IsWarehouseStaffNavigationLimited(context))
            {
                return BuildWarehouseStaffMenu(context);
            }

            if (!HasBackendNavigationAccess(context))
            {
                return new List<NavigationMenuDto>();
            }

            return FilterMenu(FullMenu, context);
        }

        private static List<NavigationMenuDto> BuildWarehouseStaffMenu(
            NavigationPermissionContext context
        )
        {
            var result = new List<NavigationMenuDto>();
            if (HasAnyPermission(
                    context,
                    Permissions.Warehouse.ManageOrders,
                    Permissions.Warehouse.Manage
                ))
            {
                // 仓库员工桌面端的仓库功能仍只暴露分店订货列表。
                result.Add(
                    new NavigationMenuDto
                    {
                        Path = "/warehouse",
                        TitleKey = "menu.warehouse",
                        Icon = "DatabaseOutlined",
                        Children = new List<NavigationMenuDto>
                        {
                            new()
                            {
                                Path = "/warehouse/store-orders",
                                TitleKey = "menu.storeOrders",
                                Icon = "ReconciliationOutlined",
                                Permission = Permissions.Warehouse.ManageOrders,
                            },
                        },
                    }
                );
            }

            // 新销售页只认各自显式权限；WarehouseStaff 的其他后台限制保持不变。
            var salesDefinition = FullMenu.First(node =>
                node.Path == "/executive-sales-intelligence"
            );
            result.AddRange(FilterMenu(new List<NavigationMenuDto> { salesDefinition }, context));
            return result;
        }

        public List<AppNavigationMenuDto> BuildAppMenu(ClaimsPrincipal user)
        {
            var context = ResolvePermissionContext(user);

            if (context.IsAdmin)
            {
                return FullAppMenu.OrderBy(node => node.Order).Select(ToAppNavigationMenuDto).ToList();
            }

            return FullAppMenu
                .Where(node => CanAccess(node, context))
                .OrderBy(node => node.Order)
                .Select(ToAppNavigationMenuDto)
                .ToList();
        }

        public List<AppNavigationMenuDto> BuildDeviceAppMenu(string? deviceType)
        {
            var isWarehouseDevice = IsWarehouseDevice(deviceType);

            return FullAppMenu
                .Where(node =>
                    DeviceBaseRouteNames.Contains(node.RouteName)
                    || (isWarehouseDevice && WarehouseDeviceRouteNames.Contains(node.RouteName))
                )
                .OrderBy(node => node.Order)
                .Select(ToAppNavigationMenuDto)
                .ToList();
        }

        private static List<NavigationMenuDto> FilterMenu(
            List<NavigationMenuDto> nodes,
            NavigationPermissionContext context
        )
        {
            var result = new List<NavigationMenuDto>();

            foreach (var node in nodes)
            {
                var hasChildren = node.Children != null && node.Children.Count > 0;
                var filteredChildren = hasChildren
                    ? FilterMenu(node.Children!, context)
                    : null;

                if (hasChildren)
                {
                    if (filteredChildren!.Count > 0)
                    {
                        result.Add(new NavigationMenuDto
                        {
                            Path = node.Path,
                            TitleKey = node.TitleKey,
                            Icon = node.Icon,
                            Permission = node.Permission,
                            RequireAdmin = node.RequireAdmin,
                            RequireExactPermission = node.RequireExactPermission,
                            AnyPermissions = node.AnyPermissions,
                            Children = filteredChildren,
                        });
                    }
                }
                else
                {
                    if (CanAccess(node, context))
                    {
                        result.Add(new NavigationMenuDto
                        {
                            Path = node.Path,
                            TitleKey = node.TitleKey,
                            Icon = node.Icon,
                            Permission = node.Permission,
                            RequireAdmin = node.RequireAdmin,
                            RequireExactPermission = node.RequireExactPermission,
                            AnyPermissions = node.AnyPermissions,
                            Children = null,
                        });
                    }
                }
            }

            return result;
        }

        private static bool CanAccess(NavigationMenuDto node, NavigationPermissionContext context)
        {
            if (node.RequireAdmin && !context.IsAdmin)
            {
                return false;
            }

            if (string.IsNullOrEmpty(node.Permission))
            {
                // 多权限入口：任一权限可见；未配置 AnyPermissions 的节点保持原有「无权限即公开」语义。
                if (node.AnyPermissions is { Count: > 0 })
                {
                    return context.IsAdmin
                        || node.AnyPermissions.Any(permission => HasPermission(context, permission));
                }

                return true;
            }

            if (node.RequireExactPermission)
            {
                return context.IsAdmin || HasExactPermission(context, node.Permission);
            }

            if (context.IsAdmin || HasPermission(context, node.Permission))
            {
                return true;
            }

            return false;
        }

        private static bool HasBackendNavigationAccess(NavigationPermissionContext context)
        {
            // WPF 版本管理和下载页都挂在后台导航下，具备查看或管理下载权限时都应能看到后台入口。
            return HasAnyPermission(
                context,
                Permissions.Dashboard.View,
                Permissions.Warehouse.ManageOrders,
                Permissions.Warehouse.Manage,
                Permissions.Container.View,
                Permissions.LocalPurchase.View,
                Permissions.SalesDashboard.SalesDataView,
                Permissions.SalesDashboard.SalesDetailView,
                Permissions.SalesDashboard.CompactBoardView,
                Permissions.SalesDashboard.MonthlyDailySalesDownloadView,
                Permissions.SalesDashboard.ProductMovementView,
                Permissions.SalesDashboard.BatchProductSalesView,
                Permissions.SalesDashboard.WarehouseFlowView,
                Permissions.SalesDashboard.LocalProductAnalysisView,
                Permissions.SalesDashboard.PurchaseAmountView,
                Permissions.SalesDashboard.LocalSupplierPurchaseSalesView,
                Permissions.System.ManageSettings,
                Permissions.System.ViewAppDownloads,
                Permissions.System.ManageAppDownloads,
                Permissions.PosTerminal.Audit.View,
                Permissions.LegacyEmployeeLogs.View,
                Permissions.DailyCloseRecords.View,
                // 人脸抓拍摄像机管理：只有该权限的账号也要能拿到后台菜单。
                Permissions.FaceCapture.Cameras.Manage,
                // 人脸比对：只有这五个权限之一的账号也要能拿到后台菜单。
                Permissions.FaceMatch.Manage,
                Permissions.FaceMatch.Traffic.View,
                Permissions.FaceMatch.Unwelcome.Manage,
                Permissions.FaceMatch.Unwelcome.Approve,
                Permissions.FaceMatch.Unwelcome.Review,
                // 现金管理页：只有该权限的店长 / 财务也要能拿到后台菜单，否则前端只能退回本地菜单。
                Permissions.Cash.OverviewView,
                // 节日贺卡分店填报统计：只有该权限的总部账号也要能拿到后台菜单。
                Permissions.SeasonalCards.Remaining.ViewAllStores,
                // 供应商节日预订单：只有下单入口或管理权限的店长 / 采购也要能拿到后台菜单。
                Permissions.SupplierPreorder.Order,
                Permissions.SupplierPreorder.Manage,
                Permissions.DeviceRegistration.ActivationCodes.Manage,
                Permissions.DeviceRegistration.MobileActivationCodes.Manage,
                // MYOB 对接：只有该权限的账号也要能拿到后台菜单，否则前端只能退回本地菜单。
                Permissions.Myob.Connection.Manage,
                Permissions.Myob.Employee.Bind,
                Permissions.Myob.Payslip.ViewManagedStore,
                Permissions.Myob.Payslip.ViewAllStores
            );
        }

        private static bool CanAccess(
            AppNavigationDefinition node,
            NavigationPermissionContext context
        )
        {
            // 版本管理属于系统管理员入口，单独授予下载权限不能开放移动端菜单。
            if (node.RequireAdmin && !context.IsAdmin)
            {
                return false;
            }

            if (string.IsNullOrEmpty(node.Permission) && (node.AnyPermissions == null || node.AnyPermissions.Count == 0))
            {
                return true;
            }

            if (context.IsAdmin)
            {
                return true;
            }

            if (HasPermission(context, node.Permission))
            {
                return true;
            }

            if (
                node.AnyPermissions != null
                && node.AnyPermissions.Any(permission => HasPermission(context, permission))
            )
            {
                return true;
            }

            return false;
        }

        private NavigationPermissionContext ResolvePermissionContext(ClaimsPrincipal user)
        {
            if (_roleService == null)
            {
                return BuildClaimPermissionContext(user);
            }

            var userId = GetUserId(user);
            if (string.IsNullOrWhiteSpace(userId))
            {
                return BuildClaimPermissionContext(user);
            }

            var result = _roleService
                .GetUserPermissionSnapshotAsync(userId)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();

            if (!result.Success || result.Data == null)
            {
                return BuildClaimPermissionContext(user);
            }

            return new NavigationPermissionContext
            {
                IsAdmin = result.Data.IsSuperAdmin,
                RoleNames = new HashSet<string>(
                    result.Data.RoleNames,
                    StringComparer.OrdinalIgnoreCase
                ),
                PermissionCodes = new HashSet<string>(
                    Permissions.ExpandPermissionCodes(result.Data.PermissionCodes),
                    StringComparer.OrdinalIgnoreCase
                ),
                ExactPermissionCodes = new HashSet<string>(
                    result.Data.ExactPermissionCodes ?? new List<string>(),
                    StringComparer.OrdinalIgnoreCase
                ),
            };
        }

        private static NavigationPermissionContext BuildClaimPermissionContext(ClaimsPrincipal user)
        {
            return new NavigationPermissionContext
            {
                IsAdmin = Permissions.SuperAdminRoleNames.Any(user.IsInRole),
                RoleNames = new HashSet<string>(
                    user.Claims
                        .Where(claim =>
                            string.Equals(
                                claim.Type,
                                ClaimTypes.Role,
                                StringComparison.OrdinalIgnoreCase
                            )
                            || string.Equals(
                                claim.Type,
                                "role",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        .Select(claim => claim.Value),
                    StringComparer.OrdinalIgnoreCase
                ),
                PermissionCodes = new HashSet<string>(
                    Permissions.ExpandPermissionCodes(
                        user.Claims
                            .Where(claim =>
                                string.Equals(
                                    claim.Type,
                                    "permission",
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                            .Select(claim => claim.Value)
                    ),
                    StringComparer.OrdinalIgnoreCase
                ),
                ExactPermissionCodes = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase
                ),
            };
        }

        private static bool IsWarehouseStaffNavigationLimited(NavigationPermissionContext context)
        {
            if (context.IsAdmin)
            {
                return false;
            }

            var isWarehouseStaff =
                context.RoleNames.Contains("WarehouseStaff") || context.RoleNames.Contains("仓库员工");
            var isWarehouseManager =
                context.RoleNames.Contains("WarehouseManager") || context.RoleNames.Contains("仓库经理");

            return isWarehouseStaff && !isWarehouseManager;
        }

        private static bool IsOrderNavigationLimited(NavigationPermissionContext context)
        {
            if (context.IsAdmin || context.RoleNames.Count == 0)
            {
                return false;
            }

            // 订货员仅使用 PDA；只要兼任任何其他角色，仍按该角色拥有的权限生成后台菜单。
            return context.RoleNames.All(roleName =>
                roleName.Equals("Order", StringComparison.OrdinalIgnoreCase)
                || roleName.Equals("订货员", StringComparison.OrdinalIgnoreCase)
            );
        }

        private static string? GetUserId(ClaimsPrincipal user)
        {
            return user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? user.FindFirst("nameidentifier")?.Value
                ?? user.FindFirst("userId")?.Value
                ?? user.FindFirst("userGuid")?.Value
                ?? user.FindFirst(ClaimTypes.Name)?.Value
                ?? user.FindFirst("sub")?.Value;
        }

        private static bool HasPermission(NavigationPermissionContext context, string? permission)
        {
            if (string.IsNullOrWhiteSpace(permission))
            {
                return false;
            }

            return GetNavigationEquivalentPermissionCodes(permission)
                .Any(code => context.PermissionCodes.Contains(code));
        }

        private static bool HasExactPermission(
            NavigationPermissionContext context,
            string? permission
        )
        {
            if (string.IsNullOrWhiteSpace(permission))
            {
                return false;
            }

            // 精确权限节点不做别名展开，只按原始精确代码判定。
            return context.ExactPermissionCodes.Contains(permission);
        }

        private static bool HasAnyPermission(
            NavigationPermissionContext context,
            params string[] permissions
        )
        {
            return permissions.Any(permission => HasPermission(context, permission));
        }

        private static bool HasPermissionClaim(ClaimsPrincipal user, string? permission)
        {
            if (string.IsNullOrWhiteSpace(permission))
            {
                return false;
            }

            return GetNavigationEquivalentPermissionCodes(permission)
                .Any(code => user.HasClaim("permission", code));
        }

        private static IReadOnlyCollection<string> GetNavigationEquivalentPermissionCodes(
            string permission
        )
        {
            var codes = new HashSet<string>(
                Permissions.GetEquivalentPermissionCodes(permission),
                StringComparer.OrdinalIgnoreCase
            );

            // WPF 版本页和下载页目前都绑定查看权限，但发布管理用户也需要保留可见性。
            if (string.Equals(permission, Permissions.System.ViewAppDownloads, StringComparison.OrdinalIgnoreCase))
            {
                codes.Add(Permissions.System.ManageAppDownloads);
            }

            // 设备开通码权限只扩展页面可见性，不等价于旧设备审核/维护权限。
            if (string.Equals(permission, Permissions.DeviceRegistration.View, StringComparison.OrdinalIgnoreCase))
            {
                codes.Add(Permissions.DeviceRegistration.ActivationCodes.Manage);
                codes.Add(Permissions.DeviceRegistration.MobileActivationCodes.Manage);
            }

            return codes;
        }

        private static bool IsWarehouseDevice(string? deviceType)
        {
            return !string.IsNullOrWhiteSpace(deviceType)
                && WarehouseDeviceTypes.Contains(deviceType.Trim());
        }

        private static AppNavigationMenuDto ToAppNavigationMenuDto(AppNavigationDefinition node)
        {
            return new AppNavigationMenuDto
            {
                RouteName = node.RouteName,
                TitleKey = node.TitleKey,
                Icon = node.Icon,
                Permission = node.Permission,
                Order = node.Order,
            };
        }
    }
}
