export interface WorkbenchNavigationItem {
  routeName: string;
  labelKey: string;
  icon: string;
}

export interface WorkbenchNavigationSection {
  key:
    | "sales-product"
    | "warehouse-purchase"
    | "operations-reports"
    | "people-management";
  titleKey: string;
  items: WorkbenchNavigationItem[];
}

const WORKBENCH_SECTIONS: WorkbenchNavigationSection[] = [
  {
    key: "sales-product",
    titleKey: "groups.salesProduct",
    items: [
      { routeName: "product-query", labelKey: "routes.productQuery", icon: "barcode-scan" },
      { routeName: "product-insights", labelKey: "routes.productInsights", icon: "chart-timeline-variant" },
      { routeName: "seasonal-product-insights", labelKey: "routes.seasonalProductInsights", icon: "calendar-star" },
      { routeName: "container-new-products", labelKey: "routes.containerNewProducts", icon: "package-variant-closed" },
      { routeName: "price-updates", labelKey: "routes.priceUpdates", icon: "tag-arrow-up-outline" },
      { routeName: "home", labelKey: "routes.storeOrdering", icon: "storefront-outline" },
      { routeName: "cart", labelKey: "routes.cart", icon: "cart-outline" },
      { routeName: "orders", labelKey: "routes.orders", icon: "clipboard-list-outline" },
      { routeName: "sales-orders", labelKey: "routes.salesOrders", icon: "receipt-text-outline" },
      { routeName: "local-supplier-invoices", labelKey: "routes.localSupplierInvoices", icon: "receipt-text-outline" },
      { routeName: "installment-orders", labelKey: "routes.installmentOrders", icon: "cash-clock" },
      { routeName: "store-vouchers", labelKey: "routes.storeVouchers", icon: "ticket-confirmation-outline" },
      { routeName: "seasonal-cards", labelKey: "routes.seasonalCards", icon: "cards-outline" },
    ],
  },
  {
    key: "warehouse-purchase",
    titleKey: "groups.warehousePurchase",
    items: [
      { routeName: "warehouse", labelKey: "routes.warehouse", icon: "warehouse" },
      { routeName: "containers", labelKey: "routes.containers", icon: "archive-outline" },
      { routeName: "warehouse-picking", labelKey: "routes.warehousePicking", icon: "clipboard-check-outline" },
      { routeName: "warehouse-product-insights", labelKey: "routes.warehouseProductInsights", icon: "warehouse" },
      { routeName: "domestic-purchase", labelKey: "routes.domesticPurchase", icon: "shopping-outline" },
    ],
  },
  {
    key: "operations-reports",
    titleKey: "groups.operationsReports",
    items: [
      { routeName: "advertisements", labelKey: "routes.advertisements", icon: "bullhorn-outline" },
      { routeName: "promotions", labelKey: "routes.promotions", icon: "sale-outline" },
      { routeName: "reports", labelKey: "routes.reports", icon: "chart-line" },
      { routeName: "pos-operation-logs", labelKey: "routes.posOperationLogs", icon: "clipboard-text-clock-outline" },
    ],
  },
  {
    key: "people-management",
    titleKey: "groups.peopleManagement",
    items: [
      { routeName: "attendance-personal", labelKey: "routes.attendancePersonal", icon: "clock-check-outline" },
      { routeName: "attendance-management", labelKey: "routes.attendanceManagement", icon: "calendar-edit" },
      { routeName: "users", labelKey: "routes.users", icon: "account-group-outline" },
      { routeName: "user-admin", labelKey: "routes.userAdmin", icon: "account-cog-outline" },
      { routeName: "cash-register-users", labelKey: "routes.cashRegisterUsers", icon: "barcode" },
      { routeName: "roles", labelKey: "routes.roles", icon: "shield-account-outline" },
      { routeName: "permissions", labelKey: "routes.permissions", icon: "key-outline" },
      { routeName: "employee-profile", labelKey: "routes.employeeProfile", icon: "card-account-details-outline" },
      { routeName: "employee-profile-review", labelKey: "routes.employeeProfileReview", icon: "account-check-outline" },
      { routeName: "device-management", labelKey: "routes.deviceManagement", icon: "cellphone-cog" },
      { routeName: "app-install", labelKey: "routes.appInstall", icon: "qrcode" },
      { routeName: "app-downloads", labelKey: "routes.appDownloads", icon: "download-outline" },
      { routeName: "wpf-versions", labelKey: "routes.wpfVersions", icon: "microsoft-windows" },
    ],
  },
];

export function buildWorkbenchSections(
  visibleRouteNames: Iterable<string>
): WorkbenchNavigationSection[] {
  const visibleRoutes = new Set(visibleRouteNames);

  return WORKBENCH_SECTIONS.map((section) => ({
    ...section,
    items: section.items.filter((item) => visibleRoutes.has(item.routeName)),
  })).filter((section) => section.items.length > 0);
}
