import type { LogBadgeLevel } from "../legacy-employee-logs/types";
import { FACE_HUB_ROUTE_NAME, isLegacyFaceRouteName } from "./face-hub";

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

  // 商品进销、仓库商品进销、订单拣货、POS 订单查询、员工操作日志、日结记录、人脸比对（合并入口 face-hub 与保留的旧三个路由）与通知收件箱都从工作台进入，是子页，不额外占用一级导航。
  if (
    routeName === "product-insights" ||
    routeName === "warehouse-picking" ||
    routeName === "warehouse-product-insights" ||
    routeName === "sales-orders" ||
    routeName === "legacy-employee-logs" ||
    routeName === "daily-closes" ||
    routeName === FACE_HUB_ROUTE_NAME ||
    isLegacyFaceRouteName(routeName) ||
    routeName === "notifications"
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

// ───────────── 底部导航徽标（红色数字） ─────────────

/** 徽标封顶值：超过显示「99+」（与设计稿、工作台入口角标一致）。 */
export const PRIMARY_TAB_BADGE_MAX = 99;

/** 供底部导航徽标使用的各类计数；目前只有「工作台」页签挂「黑名单命中未读数」。 */
export interface PrimaryTabBadgeCounts {
  /** 当前登录人还没看的「黑名单命中」通知数（只有持 FaceMatch.Unwelcome.Review 的人才会 > 0）。 */
  faceHitUnreadCount: number;
  /** 员工操作日志圆点级别（红 = 有危险操作待核查，橙 = 有异常待核查）；没有权限或没有待核查时为 null / 缺省。 */
  legacyLogBadgeLevel?: LogBadgeLevel | null;
}

/** 把计数规整成非负整数：负数、NaN、Infinity 一律按 0。 */
function normalizeBadgeCount(count: number): number {
  return Number.isFinite(count) && count > 0 ? Math.trunc(count) : 0;
}

/** 徽标文字：> 0 才有；超过 99 显示「99+」；否则 null（不显示徽标）。 */
export function formatUnreadBadgeText(count: number, max: number = PRIMARY_TAB_BADGE_MAX): string | null {
  const safe = normalizeBadgeCount(count);
  if (safe <= 0) return null;
  return safe > max ? `${max}+` : String(safe);
}

export interface PrimaryTabBadge {
  /** 真实数字（给无障碍标签读，不封顶）。 */
  count: number;
  /** 徽标上显示的文字（已封顶）。 */
  text: string;
}

/**
 * 某个一级页签要不要挂徽标、挂什么。
 * 只有「工作台」页签挂红色数字：工作台里的「人脸比对」入口行有同一个数字，底部导航把它带到每个页面上。
 */
export function resolvePrimaryTabBadge(
  key: PrimaryNavigationKey,
  counts: PrimaryTabBadgeCounts,
): PrimaryTabBadge | null {
  if (key !== "workbench") return null;
  const count = normalizeBadgeCount(counts.faceHitUnreadCount);
  const text = formatUnreadBadgeText(count);
  return text ? { count, text } : null;
}

/**
 * 「工作台」页签的圆点（无数字）：员工操作日志是工作台子页，不进工作台也要能看到提醒。
 * 数字徽标优先——已有人脸命中未读数字时不叠加圆点（数字在先，进入工作台后再看日志入口的圆点）。
 */
export function resolvePrimaryTabDot(
  key: PrimaryNavigationKey,
  counts: PrimaryTabBadgeCounts,
): LogBadgeLevel | null {
  if (key !== "workbench") return null;
  if (resolvePrimaryTabBadge(key, counts)) return null;
  return counts.legacyLogBadgeLevel ?? null;
}
