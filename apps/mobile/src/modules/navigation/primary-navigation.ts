export type PrimaryNavigationKey =
  | "workbench"
  | "scan"
  | "attendance"
  | "reports"
  | "me";

export interface PrimaryNavigationItem {
  key: PrimaryNavigationKey;
  targetRouteName:
    | "workbench"
    | "product-query"
    | "attendance-personal"
    | "reports"
    | "settings";
  labelKey:
    | "tabs.workbench"
    | "tabs.scan"
    | "tabs.checkIn"
    | "tabs.reports"
    | "tabs.me";
  icon:
    | "view-dashboard-outline"
    | "barcode-scan"
    | "clock-outline"
    | "chart-box-outline"
    | "account-circle-outline";
  active: boolean;
}

export type PrimaryNavigationAction = "none" | "dismiss-to" | "navigate";

export function resolvePrimaryNavigationAction(
  activeRouteName: string | undefined,
  item: PrimaryNavigationItem
): PrimaryNavigationAction {
  if (activeRouteName === item.targetRouteName) {
    return "none";
  }

  if (item.key === "workbench" || item.active) {
    return "dismiss-to";
  }

  return "navigate";
}

interface BuildPrimaryNavigationOptions {
  activeRouteName?: string;
  visibleRouteNames: Iterable<string>;
  isDeviceMode?: boolean;
}

const ATTENDANCE_CONTEXT_ROUTE_NAMES = new Set([
  "attendance",
  "attendance-personal",
  "attendance-management",
]);
const ME_CONTEXT_ROUTE_NAMES = new Set(["employee-profile", "settings"]);
// 促销海报编辑 / 待打印都从扫码查询页进入，属于扫码子页，继续高亮「扫码查询」。
const SCAN_CONTEXT_ROUTE_NAMES = new Set([
  "product-query",
  "promo-poster-editor",
  "promo-poster-queue",
]);

function resolveActivePrimaryKey(routeName: string | undefined): PrimaryNavigationKey {
  if (routeName && SCAN_CONTEXT_ROUTE_NAMES.has(routeName)) {
    return "scan";
  }

  // 商品进销、仓库商品进销、订单拣货、销售订单查询与员工操作日志都从工作台进入，是子页，不额外占用一级导航。
  if (
    routeName === "product-insights" ||
    routeName === "warehouse-picking" ||
    routeName === "warehouse-product-insights" ||
    routeName === "sales-orders" ||
    routeName === "pos-operation-logs"
  ) {
    return "workbench";
  }

  if (routeName && ATTENDANCE_CONTEXT_ROUTE_NAMES.has(routeName)) {
    return "attendance";
  }

  if (routeName === "reports") {
    return "reports";
  }

  if (routeName && ME_CONTEXT_ROUTE_NAMES.has(routeName)) {
    return "me";
  }

  return "workbench";
}

// 全屏作业页：PDA 小屏上底部留给页面自己的操作栏（如“完成拣货”），不显示全局底栏。
const FULL_SCREEN_TASK_ROUTE_PREFIXES = ["/warehouse-picking"] as const;

export function shouldHidePrimaryTabBar(pathname: string | null | undefined) {
  if (!pathname) return false;
  return FULL_SCREEN_TASK_ROUTE_PREFIXES.some(
    (prefix) => pathname === prefix || pathname.startsWith(`${prefix}/`)
  );
}

export function buildPrimaryNavigation({
  activeRouteName,
  visibleRouteNames,
  isDeviceMode = false,
}: BuildPrimaryNavigationOptions): PrimaryNavigationItem[] {
  const visibleRoutes = new Set(visibleRouteNames);
  const visiblePrimaryKeys = new Set<PrimaryNavigationKey>(["workbench", "me"]);

  if (visibleRoutes.has("product-query")) {
    visiblePrimaryKeys.add("scan");
  }
  if (!isDeviceMode && visibleRoutes.has("attendance-personal")) {
    visiblePrimaryKeys.add("attendance");
  }
  if (!isDeviceMode && visibleRoutes.has("reports")) {
    visiblePrimaryKeys.add("reports");
  }

  const requestedActiveKey = resolveActivePrimaryKey(activeRouteName);
  const activeKey = visiblePrimaryKeys.has(requestedActiveKey)
    ? requestedActiveKey
    : "workbench";
  const items: PrimaryNavigationItem[] = [
    {
      key: "workbench",
      targetRouteName: "workbench",
      labelKey: "tabs.workbench",
      icon: "view-dashboard-outline",
      active: activeKey === "workbench",
    },
  ];

  // 底栏只呈现当前会话真正可进入的主入口；工作台和我的保留为本地安全壳。
  if (visiblePrimaryKeys.has("scan")) {
    items.push({
      key: "scan",
      targetRouteName: "product-query",
      labelKey: "tabs.scan",
      icon: "barcode-scan",
      active: activeKey === "scan",
    });
  }

  if (visiblePrimaryKeys.has("attendance")) {
    items.push({
      key: "attendance",
      targetRouteName: "attendance-personal",
      labelKey: "tabs.checkIn",
      icon: "clock-outline",
      active: activeKey === "attendance",
    });
  }

  if (visiblePrimaryKeys.has("reports")) {
    items.push({
      key: "reports",
      targetRouteName: "reports",
      labelKey: "tabs.reports",
      icon: "chart-box-outline",
      active: activeKey === "reports",
    });
  }

  items.push({
    key: "me",
    targetRouteName: "settings",
    labelKey: "tabs.me",
    icon: "account-circle-outline",
    active: activeKey === "me",
  });

  return items;
}

interface ResolveMeTabLabelOptions {
  fullName?: string | null;
  username?: string | null;
  isDeviceMode?: boolean;
  fallbackLabel: string;
}

/**
 * 底栏「我的」直接显示当前登录人，方便在共用设备上一眼确认账号。
 * 优先姓名，其次用户名；设备模式没有个人账号，或两者都为空时回落到「我的」。
 */
export function resolveMeTabLabel({
  fullName,
  username,
  isDeviceMode = false,
  fallbackLabel,
}: ResolveMeTabLabelOptions): string {
  if (isDeviceMode) {
    return fallbackLabel;
  }

  return fullName?.trim() || username?.trim() || fallbackLabel;
}
