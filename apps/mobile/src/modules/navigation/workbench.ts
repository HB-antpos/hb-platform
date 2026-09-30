export interface WorkbenchNavigationItem {
  routeName: string;
  labelKey: string;
  icon: string;
}

export interface WorkbenchNavigationSection {
  key:
    | "product-purchasing"
    | "product-sales"
    | "warehouse-purchase"
    | "operations-reports"
    | "people-management";
  titleKey: string;
  items: WorkbenchNavigationItem[];
}

const WORKBENCH_SECTIONS: WorkbenchNavigationSection[] = [
  // 原「销售与商品」13 项过长，按业务流拆成进货与销售两组：先进货后销售。
  {
    key: "product-purchasing",
    titleKey: "groups.productPurchasing",
    items: [
      { routeName: "container-new-products", labelKey: "routes.containerNewProducts", icon: "package-variant-closed" },
      { routeName: "home", labelKey: "routes.storeOrdering", icon: "storefront-outline" },
      { routeName: "cart", labelKey: "routes.cart", icon: "cart-outline" },
      { routeName: "orders", labelKey: "routes.orders", icon: "clipboard-list-outline" },
      { routeName: "local-supplier-invoices", labelKey: "routes.localSupplierInvoices", icon: "receipt-text-outline" },
    ],
  },
  {
    key: "product-sales",
    titleKey: "groups.productSales",
    items: [
      { routeName: "product-query", labelKey: "routes.productQuery", icon: "barcode-scan" },
      { routeName: "product-insights", labelKey: "routes.productInsights", icon: "chart-timeline-variant" },
      { routeName: "seasonal-product-insights", labelKey: "routes.seasonalProductInsights", icon: "calendar-star" },
      { routeName: "price-updates", labelKey: "routes.priceUpdates", icon: "tag-arrow-up-outline" },
      { routeName: "sales-orders", labelKey: "routes.salesOrders", icon: "receipt-text-outline" },
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

// 工作台分组折叠状态只记录「已折叠」的分组键：空集合即默认全部展开，
// 权限变化后新出现的分组也自然是展开的。
export function toggleWorkbenchSectionCollapsed(
  collapsedKeys: ReadonlySet<string>,
  sectionKey: string
): Set<string> {
  const next = new Set(collapsedKeys);
  if (next.has(sectionKey)) {
    next.delete(sectionKey);
  } else {
    next.add(sectionKey);
  }
  return next;
}

// 只看当前可见分组：残留的已失效分组键不影响判断；没有分组时视为未折叠。
export function areAllWorkbenchSectionsCollapsed(
  sections: readonly Pick<WorkbenchNavigationSection, "key">[],
  collapsedKeys: ReadonlySet<string>
): boolean {
  return sections.length > 0
    && sections.every((section) => collapsedKeys.has(section.key));
}

// 全部折叠/展开：已全部折叠时清空（全部展开），否则折叠当前所有可见分组。
export function toggleAllWorkbenchSections(
  sections: readonly Pick<WorkbenchNavigationSection, "key">[],
  collapsedKeys: ReadonlySet<string>
): Set<string> {
  return areAllWorkbenchSectionsCollapsed(sections, collapsedKeys)
    ? new Set()
    : new Set(sections.map((section) => section.key));
}

/** 工作台角标默认封顶值：超过显示「N+」。 */
export const WORKBENCH_BADGE_DEFAULT_MAX = 99;

export function formatWorkbenchBadgeCount(
  count: number,
  maxCount: number = WORKBENCH_BADGE_DEFAULT_MAX
): string {
  return count > maxCount ? `${maxCount}+` : String(count);
}

// 折叠分组的汇总角标：累加组内各入口角标；封顶值取组内最大（含 HB新品 999 时按 999 封顶），
// 避免数百款新品被压成「99+」而和展开时的数字不一致。
export function summarizeWorkbenchSectionBadge(
  section: Pick<WorkbenchNavigationSection, "items">,
  countByRouteName: Readonly<Record<string, number>>,
  maxCountByRouteName: Readonly<Record<string, number>>
): { count: number; maxCount: number } {
  let count = 0;
  let maxCount = WORKBENCH_BADGE_DEFAULT_MAX;
  for (const item of section.items) {
    const itemCount = countByRouteName[item.routeName] ?? 0;
    if (itemCount <= 0) {
      continue;
    }
    count += itemCount;
    maxCount = Math.max(
      maxCount,
      maxCountByRouteName[item.routeName] ?? WORKBENCH_BADGE_DEFAULT_MAX
    );
  }
  return { count, maxCount };
}
