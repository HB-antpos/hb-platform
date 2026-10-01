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
                    // Web 用户管理菜单只认 Users.ViewWebConsole；Users.View 继续点亮移动端「员工列表」「用户管理」。
                    new() { Path = "/system/users",    TitleKey = "menu.systemUsers",       Icon = "UserOutlined",     Permission = Permissions.Users.ViewWebConsole },
                    new() { Path = "/system/employee-profiles", TitleKey = "menu.employeeProfiles", Icon = "IdcardOutlined", Permission = Permissions.EmployeeProfiles.View },
                    new() { Path = "/system/roles",    TitleKey = "menu.systemRoles",       Icon = "TeamOutlined",     Permission = Permissions.Roles.View },
                    new() { Path = "/system/permissions", TitleKey = "menu.systemPermissions", Icon = "KeyOutlined", Permission = Permissions.Roles.View },
                    new() { Path = "/system/invoice-email-settings", TitleKey = "menu.invoiceEmailSettings", Icon = "SettingOutlined", Permission = Permissions.System.ManageSettings },
                    new() { Path = "/system/payment-terminal-settings", TitleKey = "menu.paymentTerminalSettings", Icon = "WalletOutlined", Permission = Permissions.System.ManageSettings },
                    new() { Path = "/system/emergency-login-keys", TitleKey = "menu.emergencyLoginKeys", Icon = "KeyOutlined", Permission = Permissions.System.ManageSettings },
                    new() { Path = "/system/device-registration", TitleKey = "menu.deviceRegistration", Icon = "BuildOutlined", Permission = Permissions.DeviceRegistration.View },
                    new() { Path = "/system/app-downloads", TitleKey = "menu.appDownloads", Icon = "QrcodeOutlined", Permission = Permissions.System.ViewAppDownloads },
                    new() { Path = "/system/wpf-versions", TitleKey = "menu.wpfVersions", Icon = "DownloadOutlined", Permission = Permissions.System.ViewAppDownloads },
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
                Path = "/executive-sales-intelligence",
                TitleKey = "menu.executiveSalesIntelligence",
                Icon = "BarChartOutlined",
                Children = new List<NavigationMenuDto>
                {
                    new() { Path = "/executive-sales-intelligence/overview",       TitleKey = "menu.salesData",   Icon = "DashboardOutlined", Permission = Permissions.SalesDashboard.SalesDataView },
                    new() { Path = "/executive-sales-intelligence/sales-detail-v2", TitleKey = "menu.salesDetail", Icon = "FileTextOutlined",  Permission = Permissions.SalesDashboard.SalesDetailView },
                    new() { Path = "/executive-sales-intelligence/compact-sales-board", TitleKey = "menu.compactSalesBoard", Icon = "BarChartOutlined", Permission = Permissions.SalesDashboard.CompactBoardView },
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
                    new() { Path = "/pos-admin/products",              TitleKey = "menu.productManagement",      Icon = "AppstoreOutlined",           Permission = Permissions.PosProducts.View },
                    new() { Path = "/pos-admin/store-product-price",   TitleKey = "menu.storeProductPrice",      Icon = "DollarOutlined",             Permission = Permissions.StoreProducts.View },
                    new() { Path = "/pos-admin/pricing-strategies",    TitleKey = "menu.pricingStrategies",      Icon = "FileTextOutlined",           Permission = Permissions.PricingStrategy.View },
                    new() { Path = "/pos-admin/promotions",            TitleKey = "menu.promotions",             Icon = "GiftOutlined",               Permission = Permissions.Promotions.View },
                    new() { Path = "/pos-admin/advertisements",        TitleKey = "menu.advertisements",         Icon = "PictureOutlined",            Permission = Permissions.Advertisements.View },
                    new() { Path = "/pos-admin/cash-register-users",   TitleKey = "menu.cashRegisterUsers",      Icon = "UserOutlined",               Permission = Permissions.Store.ManageOperations },
                    new() { Path = "/pos-admin/operation-logs",       TitleKey = "menu.operationLogs",          Icon = "FileTextOutlined",           Permission = Permissions.PosTerminal.Audit.View },
                    new() { Path = "/pos-admin/legacy-employee-logs", TitleKey = "menu.legacyEmployeeLogs",     Icon = "FileTextOutlined",           Permission = Permissions.LegacyEmployeeLogs.View },
                    new() { Path = "/pos-admin/linkly-settlements",   TitleKey = "menu.linklySettlements",      Icon = "ReconciliationOutlined",     RequireAdmin = true },
                    new() { Path = "/pos-admin/schedule-attendance",   TitleKey = "menu.scheduleAttendance",     Icon = "CalendarOutlined",           Permission = Permissions.Attendance.Schedule.ViewStore },
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
                Permission = Permissions.Users.View,
                Order = 57,
            },
            new()
            {
                RouteName = "legacy-employee-logs",
                TitleKey = "tabs.legacyEmployeeLogs",
                Icon = "clipboard-text-clock-outline",
                // 移动端「员工操作日志」看老收银日志（替换原新 POS 审计页，新 POS 审计仍在 Web 后台），
                // 与 Web /pos-admin/legacy-employee-logs 同一查看权限。
                // 设备模式菜单（BuildDeviceAppMenu）按 DeviceBaseRouteNames 白名单挑选，不会包含此项。
                Permission = Permissions.LegacyEmployeeLogs.View,
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
                Permissions.DeviceRegistration.ActivationCodes.Manage,
                Permissions.DeviceRegistration.MobileActivationCodes.Manage
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
