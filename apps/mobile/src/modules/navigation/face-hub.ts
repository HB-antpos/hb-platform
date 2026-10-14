// 「人脸比对」合并入口（路由名 face-hub）的纯逻辑：路由名、旧入口别名、一级页签的权限判定、路由参数解析、
// 红色数字（黑名单命中未读数）的取数条件与跳转目标。不依赖 React Native，可在 Node 下测试。
//
// 背景：原「抓拍与比对 / 不受欢迎人员名单 / 人脸比对设置」三个工作台入口合并成一个入口，进去后是「抓拍 / 名单 / 设置」三个一级页签。
// 合并的是入口，不是权限：每个页签、页签里的每个区域仍各用各的权限码。
import { FACE_MATCH_PERMISSIONS } from "../face-match/constants";

export const FACE_HUB_ROUTE_NAME = "face-hub";

/**
 * 旧三个入口的路由名。路由本身仍保留（深链、旧版通知、历史书签还会指向它们，页面会重定向到合并页对应页签），
 * 但新版 App 的菜单可见集合里不再出现它们：后端为兼容旧版 App 仍下发这三项，新版只显示 face-hub。
 */
export const LEGACY_FACE_ROUTE_NAMES = ["face-unwelcome", "face-match-settings", "face-capture-results"] as const;
export type LegacyFaceRouteName = (typeof LEGACY_FACE_ROUTE_NAMES)[number];

export function isLegacyFaceRouteName(routeName: string | undefined): routeName is LegacyFaceRouteName {
  return (LEGACY_FACE_ROUTE_NAMES as readonly string[]).includes(routeName ?? "");
}

/** 从菜单里去掉旧三个入口（保持其余顺序不变）。 */
export function filterLegacyFaceRouteNames(routeNames: readonly string[]): string[] {
  return routeNames.filter((routeName) => !isLegacyFaceRouteName(routeName));
}

type HasPermission = (permission: string) => boolean;

/** 合并入口的 any-of 权限（与后端 FullAppMenu 的 face-hub 一致）：持有任一即显示入口。 */
export const FACE_HUB_ACCESS_PERMISSIONS = [
  FACE_MATCH_PERMISSIONS.imagesView,
  FACE_MATCH_PERMISSIONS.unwelcomeManage,
  FACE_MATCH_PERMISSIONS.unwelcomeApprove,
  FACE_MATCH_PERMISSIONS.unwelcomeReview,
  FACE_MATCH_PERMISSIONS.manage,
  FACE_MATCH_PERMISSIONS.camerasManage,
] as const;

/**
 * 「人脸比对」入口可见性：已登录、非 iOS 审核离线会话，且持有上面六个码任一。
 * 参考照片与命中抓拍是真实的生物识别信息，审核模式没有离线演示数据，不进审核壳。
 */
export function canViewFaceHub(isAuthenticated: boolean, hasPermission: HasPermission, isReview: boolean): boolean {
  return isAuthenticated && !isReview && FACE_HUB_ACCESS_PERMISSIONS.some((code) => hasPermission(code));
}

// ───────────── 一级页签 ─────────────

export type FaceHubTabKey = "capture" | "list" | "settings";
/** 页签顺序即页面顺序。 */
export const FACE_HUB_TAB_KEYS: readonly FaceHubTabKey[] = ["capture", "list", "settings"];

/**
 * 按各自权限决定显示哪些页签：
 * - 抓拍：FaceCapture.Images.View；
 * - 名单：名单 Manage / Approve / Review 任一（内部「名单条目 / 命中复核」二级分段另按 GET /face-match/config 的权限标志）；
 * - 设置：FaceMatch.Manage 或 FaceCapture.Cameras.Manage（内部「门店档案 / 摄像头」两区各按自己的码）。
 */
export function resolveFaceHubTabs(hasPermission: HasPermission): FaceHubTabKey[] {
  const tabs: FaceHubTabKey[] = [];
  if (hasPermission(FACE_MATCH_PERMISSIONS.imagesView)) tabs.push("capture");
  if (
    hasPermission(FACE_MATCH_PERMISSIONS.unwelcomeManage) ||
    hasPermission(FACE_MATCH_PERMISSIONS.unwelcomeApprove) ||
    hasPermission(FACE_MATCH_PERMISSIONS.unwelcomeReview)
  ) {
    tabs.push("list");
  }
  if (hasPermission(FACE_MATCH_PERMISSIONS.manage) || hasPermission(FACE_MATCH_PERMISSIONS.camerasManage)) tabs.push("settings");
  return tabs;
}

/**
 * 红色数字挂在哪个页签：「他能处理的第一个页签」——有抓拍页签就挂抓拍；
 * 没有 Images.View 而有名单页签（持 Review 的人必然有）就挂名单；都没有则不挂。
 */
export function resolveFaceHubBadgeTab(tabs: readonly FaceHubTabKey[]): FaceHubTabKey | null {
  if (tabs.includes("capture")) return "capture";
  if (tabs.includes("list")) return "list";
  return null;
}

/** 当前应选中的页签：请求的页签可见就用它，否则退回第一个可见页签；一个都没有返回 null。 */
export function resolveFaceHubActiveTab(tabs: readonly FaceHubTabKey[], requested: FaceHubTabKey | null | undefined): FaceHubTabKey | null {
  if (requested && tabs.includes(requested)) return requested;
  return tabs[0] ?? null;
}

// ───────────── 路由参数 ─────────────

export type FaceHubSubKey = "entries" | "review";
export type FaceHubFilterKey = "all" | "blacklist";
export type FaceHubHitStatusKey = "all" | "unread" | "pending" | "autoConfirmed" | "confirmed" | "dismissed";

const HIT_STATUS_KEYS: readonly FaceHubHitStatusKey[] = ["all", "unread", "pending", "autoConfirmed", "confirmed", "dismissed"];

/** 合并页接受的路由参数（均可选）：tab 一级页签；sub 名单页签的二级分段；filter / hitStatus 只对抓拍页签有意义。 */
export interface FaceHubParams {
  tab?: FaceHubTabKey;
  sub?: FaceHubSubKey;
  filter?: FaceHubFilterKey;
  hitStatus?: FaceHubHitStatusKey;
}

type RawParam = string | string[] | undefined;

function firstValue(value: RawParam): string | undefined {
  return Array.isArray(value) ? value[0] : value;
}

/** 解析路由参数：未知值一律丢弃（深链、旧通知、手改地址都不能把页面带进未定义状态）。 */
export function parseFaceHubParams(raw: Partial<Record<keyof FaceHubParams, RawParam>>): FaceHubParams {
  const tab = firstValue(raw.tab);
  const sub = firstValue(raw.sub);
  const filter = firstValue(raw.filter);
  const hitStatus = firstValue(raw.hitStatus);
  const params: FaceHubParams = {};
  if (tab === "capture" || tab === "list" || tab === "settings") params.tab = tab;
  if (sub === "entries" || sub === "review") params.sub = sub;
  if (filter === "all" || filter === "blacklist") params.filter = filter;
  if (hitStatus && (HIT_STATUS_KEYS as readonly string[]).includes(hitStatus)) params.hitStatus = hitStatus as FaceHubHitStatusKey;
  return params;
}

export interface FaceHubRoute {
  pathname: "/(shell)/face-hub";
  params: Record<string, string>;
}

/** 生成跳转到合并页的路由对象（只带有值的参数）。 */
export function buildFaceHubRoute(params: FaceHubParams = {}): FaceHubRoute {
  const query: Record<string, string> = {};
  for (const key of ["tab", "sub", "filter", "hitStatus"] as const) {
    const value = params[key];
    if (value) query[key] = value;
  }
  return { pathname: "/(shell)/face-hub", params: query };
}

/** 旧三个路由重定向到合并页的哪个页签。 */
export function resolveLegacyFaceRedirectParams(routeName: LegacyFaceRouteName): FaceHubParams {
  switch (routeName) {
    case "face-capture-results":
      return { tab: "capture" };
    case "face-unwelcome":
      return { tab: "list" };
    case "face-match-settings":
      return { tab: "settings" };
  }
}

// ───────────── 红色数字：取数条件 ─────────────

export interface FaceHitUnreadQueryGate {
  isAuthenticated: boolean;
  /** iOS 审核离线会话。 */
  isReviewSession: boolean;
  /** 设备模式没有用户账号，收件箱按账号计算，不取。 */
  isDeviceMode: boolean;
  navigationReady: boolean;
  /** face-hub 是否在菜单可见集合里。 */
  faceHubVisible: boolean;
  /** 是否持有 FaceMatch.Unwelcome.Review：命中通知只发给持有它的人，其他人没有数字，也不必请求。 */
  hasUnwelcomeReview: boolean;
}

/** 是否要取「黑名单命中未读数」：导航就绪 + face-hub 可见 + 持有 Review（+ 账号会话、非审核）。 */
export function shouldFetchFaceHitUnread(gate: FaceHitUnreadQueryGate): boolean {
  return (
    gate.navigationReady &&
    gate.isAuthenticated &&
    !gate.isReviewSession &&
    !gate.isDeviceMode &&
    gate.faceHubVisible &&
    gate.hasUnwelcomeReview
  );
}
