import { highestLogBadgeLevel } from "../legacy-employee-logs/badge";
import type { LogBadgeLevel } from "../legacy-employee-logs/types";

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

export const SUPPORT_FAQ_ROUTE = "support-faq";

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
      { routeName: "store-cash", labelKey: "routes.storeCash", icon: "bank-transfer" },
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
      { routeName: "legacy-employee-logs", labelKey: "routes.legacyEmployeeLogs", icon: "clipboard-text-clock-outline" },
      { routeName: "daily-closes", labelKey: "routes.dailyCloses", icon: "cash-register" },
      { routeName: "notifications", labelKey: "routes.notifications", icon: "bell-outline" },
    ],
  },
  {
    key: "people-management",
    titleKey: "groups.peopleManagement",
    items: [
      { routeName: "attendance-personal", labelKey: "routes.attendancePersonal", icon: "clock-check-outline" },
      { routeName: "attendance-management", labelKey: "routes.attendanceManagement", icon: "calendar-edit" },
      // 「人员管理」合并入口（路由名 users）：原「员工列表 / 用户管理 / 角色管理 / 权限管理」四个入口合成一个，
      // 进去后是「人员 / 角色 / 权限」页签（角色、权限只有满足 Roles.View 门禁的账号才有，见 people/people-hub-tabs.ts）。
      // user-admin / roles / permissions 三个路由名保留为重定向壳（旧版 App、推送、书签仍可能指向它们），不再作为工作台入口。
      { routeName: "users", labelKey: "routes.users", icon: "account-group-outline" },
      { routeName: "cash-register-users", labelKey: "routes.cashRegisterUsers", icon: "barcode" },
      { routeName: "employee-profile", labelKey: "routes.employeeProfile", icon: "card-account-details-outline" },
      { routeName: "employee-profile-review", labelKey: "routes.employeeProfileReview", icon: "account-check-outline" },
      // 人脸比对：抓拍 / 名单 / 设置三个页签合并成一个入口（持有其中任一权限即显示，页签与区域仍按各自权限，见后端 FullAppMenu）。
      // 旧的「抓拍与比对 / 不受欢迎人员名单 / 人脸比对设置」三个入口只给旧版 App 用，新版菜单里已去掉（见 default-route 的 getVisibleTabRouteNames）。
      { routeName: "face-hub", labelKey: "routes.faceHub", icon: "face-recognition" },
      { routeName: "device-management", labelKey: "routes.deviceManagement", icon: "cellphone-cog" },
      { routeName: "app-install", labelKey: "routes.appInstall", icon: "qrcode" },
      // 版本发布中心：App 安装包 / OTA 与 WPF 收银端合并为一页；wpf-versions 路由仅保留为跳转到其 WPF 终端的旧入口。
      { routeName: "app-downloads", labelKey: "routes.appDownloads", icon: "rocket-launch-outline" },
      // 使用帮助：不是 App 内页面，也不由后端菜单下发；店长与管理员账号在本地补入，点开用系统浏览器打开 Web 公开 FAQ 页。
      { routeName: SUPPORT_FAQ_ROUTE, labelKey: "routes.supportFaq", icon: "help-circle-outline" },
    ],
  },
];

// FAQ 页固定指向生产 Web：非生产环境的 API 地址是后端端口（:5002），拼不出可访问的网页地址。
export const SUPPORT_FAQ_URL = "https://hotbargain.vip/support/faq";

export function buildSupportFaqUrl(language: "zh" | "en"): string {
  return `${SUPPORT_FAQ_URL}?lang=${language}&tab=mobile`;
}

/**
 * 在后端下发的可见路由之后补上本地入口。
 * 「使用帮助」给店长（由可管理分店派生的 StoreManager 角色）和管理员看；
 * 设备会话没有登录人、iOS 审核会话（审核账号是 Admin）不跳外部网页，都不显示。
 * 不登记到 TAB_PATHS / FullAppMenu，避免与后端菜单契约测试冲突。
 */
export function withLocalWorkbenchRoutes(
  visibleRouteNames: readonly string[],
  options: {
    isStoreManager: boolean;
    isAdmin: boolean;
    isDeviceMode: boolean;
    isIosReviewSession: boolean;
  }
): string[] {
  const routeNames = [...visibleRouteNames];
  if (
    (options.isStoreManager || options.isAdmin)
    && !options.isDeviceMode
    && !options.isIosReviewSession
    && !routeNames.includes(SUPPORT_FAQ_ROUTE)
  ) {
    routeNames.push(SUPPORT_FAQ_ROUTE);
  }
  return routeNames;
}

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

// 折叠分组的圆点汇总：取组内入口圆点的最高级别（红 > 橙）。圆点不带数字，与数字合计互不影响：
// 组头有数字合计时数字照旧，圆点另外显示在数字左侧；无数字时圆点单独显示。
export function summarizeWorkbenchSectionDot(
  section: Pick<WorkbenchNavigationSection, "items">,
  dotByRouteName: Readonly<Record<string, LogBadgeLevel | null | undefined>>
): LogBadgeLevel | null {
  return highestLogBadgeLevel(section.items.map((item) => dotByRouteName[item.routeName]));
}
