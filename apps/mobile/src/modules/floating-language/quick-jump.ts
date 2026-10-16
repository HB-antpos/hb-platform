// 快速跳转面板的入口与跳转规则（纯函数）。入口完全复用工作台那份权限过滤后的路由列表，
// 所以设备模式、iOS 审核模式、角色差异都自动继承，这里不再做任何权限判断。
import { TAB_PATHS } from "@/modules/navigation/default-route";
import {
  buildWorkbenchSections,
  type WorkbenchNavigationItem,
  type WorkbenchNavigationSection,
} from "@/modules/navigation/workbench";

/** 每个账号最多记几个最近访问。 */
export const QUICK_JUMP_RECENT_LIMIT = 4;

export interface QuickJumpModel {
  sections: WorkbenchNavigationSection[];
  /** 最近访问：只保留当前仍可见、且不是当前页的入口。 */
  recent: WorkbenchNavigationItem[];
  canOpenWorkbench: boolean;
  canOpenSettings: boolean;
}

export function buildQuickJumpModel(input: {
  orderedVisibleRouteNames: readonly string[];
  recentRouteNames: readonly string[];
  currentRouteName?: string;
  /** 菜单加载中或出错时不列入口（与工作台一致），只保留回工作台 / 设置。 */
  menuUsable: boolean;
}): QuickJumpModel {
  const visible = new Set(input.orderedVisibleRouteNames);
  const sections = input.menuUsable ? buildWorkbenchSections(input.orderedVisibleRouteNames) : [];
  const itemsByRoute = new Map(
    sections.flatMap((section) => section.items).map((item) => [item.routeName, item])
  );
  const recent = input.recentRouteNames
    .filter((routeName) => routeName !== input.currentRouteName)
    .map((routeName) => itemsByRoute.get(routeName))
    .filter((item): item is WorkbenchNavigationItem => Boolean(item))
    .slice(0, QUICK_JUMP_RECENT_LIMIT);
  return {
    sections,
    recent,
    canOpenWorkbench: Boolean(TAB_PATHS.workbench) && visible.has("workbench"),
    canOpenSettings: Boolean(TAB_PATHS.settings) && visible.has("settings"),
  };
}

/** 把刚打开的入口放到最近访问最前面（去重、限量）；不是工作台入口的路由不记。 */
export function pushRecentRoute(
  recent: readonly string[],
  routeName: string | undefined,
  trackableRouteNames: ReadonlySet<string>
): string[] {
  if (!routeName || !trackableRouteNames.has(routeName)) {
    return [...recent];
  }
  return [routeName, ...recent.filter((name) => name !== routeName)].slice(0, QUICK_JUMP_RECENT_LIMIT);
}

export type QuickJumpAction =
  | { kind: "none" }
  | { kind: "dismiss-to"; path: string }
  | { kind: "push"; path: string }
  | { kind: "price-updates-overdue" };

/**
 * 跳转规则与工作台磁贴保持一致：已在目标页什么都不做；回工作台弹回栈底锚点（不叠重复根页）；
 * 其他入口 push（可以手势逐级返回）；报表被超期价格任务锁住时改去价格任务页。
 */
export function resolveQuickJumpAction(input: {
  targetRouteName: string;
  currentRouteName?: string;
  reportsLocked: boolean;
}): QuickJumpAction {
  const path = TAB_PATHS[input.targetRouteName];
  if (!path || input.targetRouteName === input.currentRouteName) {
    return { kind: "none" };
  }
  if (input.targetRouteName === "workbench") {
    return { kind: "dismiss-to", path };
  }
  if (input.targetRouteName === "reports" && input.reportsLocked) {
    return { kind: "price-updates-overdue" };
  }
  return { kind: "push", path };
}
