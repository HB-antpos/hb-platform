import type { RoleMenuDefinition } from "./role-logic";

type MenuSource = Omit<RoleMenuDefinition, "title"> & { en: string; zh: string };
type MenuRow = [string, string, string, string[], Partial<MenuSource>?];

// 与 NavigationService 的 FullMenu / FullAppMenu 叶节点保持一一对应；父分组由可见叶节点自然决定。
const WEB_MENU: MenuSource[] = ([
  ["/dashboard", "Dashboard", "数据看板", ["Dashboard"]],
  ["/system/stores", "Stores", "分店管理", ["Stores.View"]],
  // 与 NavigationService 一致：Web 用户管理菜单只认 Users.ViewWebConsole，移动端 users / user-admin 仍用 Users.View。
  ["/system/users", "Users", "用户管理", ["Users.ViewWebConsole"]],
  ["/system/employee-profiles", "Employee profiles", "员工档案", ["EmployeeProfiles.View"]],
  ["/system/roles", "Roles", "角色管理", ["Roles.View"]],
  ["/system/permissions", "Permissions", "权限管理", ["Roles.View"]],
  ["/system/invoice-email-settings", "Invoice email settings", "发票邮箱设置", ["System.ManageSettings"]],
  ["/system/payment-terminal-settings", "Payment terminal settings", "支付终端设置", ["System.ManageSettings"]],
  ["/system/emergency-login-keys", "Emergency login keys", "紧急登录密钥", ["System.ManageSettings"]],
  ["/system/device-registration", "Device management", "设备管理", ["DeviceRegistration.View"]],
  ["/system/app-downloads", "App downloads", "应用下载", ["System.ViewAppDownloads"]],
  ["/system/wpf-versions", "WPF versions", "WPF 版本", ["System.ViewAppDownloads"]],
  ["/warehouse/store-orders", "Store orders", "分店订货", ["Warehouse.ManageOrders"]],
  ["/warehouse/preorders", "Preorders", "预订单", ["Warehouse.ManageOrders"]],
  ["/warehouse/store-order-import-price-variance", "Import price variance", "导入价格差异", ["Warehouse.ManageOrders"]],
  ["/warehouse/containers", "Containers", "货柜管理", ["Container.View"]],
  ["/warehouse/products", "Warehouse products", "仓库商品", ["Warehouse.ManageProducts"]],
  ["/warehouse/categories", "Categories", "分类管理", ["Warehouse.ManageCategories"]],
  ["/warehouse/locations", "Locations", "库位管理", ["Warehouse.ManageLocations"]],
  ["/warehouse/product-grade-management", "Product grades", "商品等级", ["Warehouse.ManageProducts"]],
  ["/domestic-purchase/china-suppliers", "China suppliers", "国内供应商", ["DomesticPurchase.ManageSuppliers"]],
  ["/domestic-purchase/domestic-products", "Domestic products", "国内商品", ["Products.View"]],
  ["/domestic-purchase/prefix-code-management", "Prefix codes", "前缀编码", ["DomesticPurchase.ManagePrefixCodes"]],
  ["/domestic-purchase/product-creation", "Product creation", "商品创建", ["DomesticPurchase.ManageProducts"]],
  ["/domestic-purchase/product-import", "Product import", "商品导入", ["DomesticPurchase.ManageProducts"]],
  ["/executive-sales-intelligence/overview", "Sales overview", "销售概览", ["SalesDashboard.SalesData.View"]],
  ["/executive-sales-intelligence/sales-detail-v2", "Sales detail", "销售明细", ["SalesDashboard.SalesDetail.View"]],
  ["/executive-sales-intelligence/compact-sales-board", "Compact sales board", "销售简报", ["SalesDashboard.CompactBoard.View"]],
  ["/executive-sales-intelligence/monthly-daily-sales-download", "Monthly daily sales download", "月度日销售下载", ["SalesDashboard.MonthlyDailySalesDownload.View"]],
  ["/executive-sales-intelligence/product-movement-report", "Product movement", "商品流动", ["SalesDashboard.ProductMovement.View"]],
  ["/executive-sales-intelligence/warehouse-product-flow-analysis", "Warehouse product flow", "仓库商品流向", ["SalesDashboard.WarehouseFlow.View"]],
  ["/executive-sales-intelligence/local-product-sales-analysis", "Local product sales", "本地商品销售", ["SalesDashboard.LocalProductAnalysis.View"]],
  // 批量货号销量与分店进货销量分析已合并为「进货销量分析」双标签页，任一标签权限即显示入口。
  ["/executive-sales-intelligence/purchase-sales-analysis", "Purchase & sales analysis", "进货销量分析", ["SalesDashboard.BatchProductSales.View", "SalesDashboard.LocalSupplierPurchaseSales.View"]],
  ["/executive-sales-intelligence/purchase-amount-dashboard", "Purchase amount", "采购金额", ["SalesDashboard.PurchaseAmount.View"]],
  ["/pos-admin/suppliers", "Suppliers", "供应商", ["AustralianSuppliers.View"]],
  ["/pos-admin/products", "Product management", "商品管理", ["PosProducts.View"]],
  ["/pos-admin/store-product-price", "Store product prices", "分店商品价格", ["StoreProducts.View"]],
  ["/pos-admin/pricing-strategies", "Pricing strategies", "定价策略", ["PricingStrategy.View"]],
  ["/pos-admin/promotions", "Promotions", "促销管理", ["Promotions.View"]],
  ["/pos-admin/advertisements", "Advertisements", "广告管理", ["Advertisements.View"]],
  ["/pos-admin/cash-register-users", "Cash register users", "收银用户", ["Store.ManageOperations"]],
  ["/pos-admin/operation-logs", "Operation logs", "操作日志", ["Permissions.PosTerminal.Audit.View"]],
  ["/pos-admin/linkly-settlements", "Linkly settlements", "Linkly 结算", [], { requireAdmin: true }],
  ["/pos-admin/schedule-attendance", "Schedule and attendance", "排班考勤", ["Attendance.Schedule.ViewStore"]],
  ["/pos-admin/sales-orders", "Sales orders", "销售订单", ["Orders.View"]],
  ["/pos-admin/local-supplier-invoices", "Local supplier invoices", "本地供应商发票", ["LocalPurchase.View"]],
] as MenuRow[]).map(toSource("web"));

const MOBILE_MENU: MenuSource[] = ([
  ["home", "Home", "首页", ["Orders.Create"]],
  ["orders", "Orders", "订单", ["OrderFront", "Orders.View", "Warehouse.ManageOrders", "Warehouse.Manage"]],
  ["sales-orders", "Sales records", "销售订单", ["SalesOrders.View"]],
  ["cart", "Cart", "购物车", ["Orders.Create"]],
  ["warehouse", "Products & locations", "商品和货位管理", ["Warehouse.ManageProducts"]],
  ["containers", "Containers", "货柜管理", ["Container.View"]],
  // 与后端 FullAppMenu 的 AnyPermissions 一致：拣货专用权限，或管理仓库 / 管理仓库订货任一即可见。
  ["warehouse-picking", "Order picking", "订单拣货", ["Warehouse.Picking", "Warehouse.Manage", "Warehouse.ManageOrders"]],
  ["domestic-purchase", "Domestic purchase", "国内采购", ["DomesticPurchase.ManageProducts"]],
  ["local-supplier-invoices", "Local supplier invoices", "本地供应商发票", ["LocalPurchase.MobileView", "LocalPurchase.View"]],
  ["advertisements", "Advertisements", "广告", ["Advertisements.View"]],
  ["promotions", "Promotions", "促销", ["Promotions.View"]],
  ["product-query", "Product query", "商品查询", ["StoreProducts.View"]],
  ["seasonal-product-insights", "Seasonal products", "季节商品查询", ["SeasonalProductInsights.View"]],
  ["container-new-products", "HB new arrivals", "HB新品", ["Container.MobileNewProductsView"]],
  ["installment-orders", "Installment orders", "分期订单", ["InstallmentOrders.View"]],
  ["store-vouchers", "Store vouchers", "门店代金券", ["StoreVouchers.View"]],
  ["store-cash", "Cash", "现金", ["Cash.Overview.View"]],
  ["attendance-personal", "My attendance", "我的考勤", ["Attendance.Schedule.ViewSelf"]],
  ["attendance-management", "Attendance management", "考勤管理", [
    "Attendance.Schedule.ViewStore", "Attendance.Schedule.EditManagedStore", "Attendance.Availability.ViewManagedStore",
    "Attendance.Punch.ViewManagedStore", "Attendance.Approval.ViewManagedStore", "Attendance.Approval.ReviewManagedStore",
    "Attendance.Holiday.ViewStore", "Attendance.Holiday.EditManagedStore", "Attendance.Leave.ViewManagedStore",
    "Attendance.Leave.ReviewManagedStore", "Attendance.Settings.Edit", "Attendance.Admin.View",
  ]],
  ["seasonal-cards", "Seasonal cards", "节庆卡", ["SeasonalCards.Remaining.ViewManagedStore", "SeasonalCards.Remaining.SubmitManagedStore"]],
  ["users", "Users", "用户", ["Users.View"]],
  ["user-admin", "User management", "用户管理", ["Users.View"]],
  ["cash-register-users", "Cash register barcodes", "收银用户条码", ["CashRegisterUsers.MobileManage", "CashRegisterUsers.MobilePrint"]],
  ["roles", "Role management", "角色管理", ["Roles.View"]],
  ["permissions", "Permission management", "权限管理", ["Roles.View"]],
  ["employee-profile", "Employee profile", "员工档案", ["EmployeeProfiles.View"]],
  ["employee-profile-review", "Profile review", "档案审核", ["EmployeeProfiles.ReviewSensitiveManagedStore"]],
  ["device-management", "Device management", "设备管理", ["DeviceRegistration.View"]],
  ["reports", "Reports", "报表", ["Reports.ProductMovement.View"]],
  // 与后端 FullAppMenu 的 AnyPermissions 一致：老收银或新收银任一查看权限即可见。
  ["legacy-employee-logs", "Employee operation logs", "员工操作日志", ["LegacyEmployeeLogs.View", "Permissions.PosTerminal.Audit.View"]],
  ["app-install", "Install app", "App 安装", ["System.ViewMobileAppInstallLinks"]],
  ["app-downloads", "App downloads", "应用下载", ["System.ViewAppDownloads"], { requireAdmin: true }],
  ["wpf-versions", "WPF versions", "WPF 版本", ["System.ViewAppDownloads"], { requireAdmin: true }],
  ["settings", "Settings", "我的", [], { fixed: true }],
] as MenuRow[]).map(toSource("mobile"));

function toSource(platform: "web" | "mobile") {
  return ([key, en, zh, permissionCodes, options = {}]: MenuRow): MenuSource => ({
    key, en, zh, permissionCodes, platform, ...options,
  });
}

export function getRoleMenuDefinitions(language: string): RoleMenuDefinition[] {
  return [...WEB_MENU, ...MOBILE_MENU].map(({ en, zh, ...item }) => ({
    ...item,
    title: language === "zh" ? zh : en,
  }));
}
