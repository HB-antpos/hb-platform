// 「人脸比对」合并入口（face-hub）的纯逻辑与登记契约测试：
// 权限 → 页签、红色数字挂哪个页签、路由参数白名单、旧入口别名、红色数字的取数条件；
// 以及 _layout / 导航上下文 / 工作台 / 角色菜单目录 / 审核模式里的接线（源码契约）。
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { FACE_MATCH_PERMISSIONS } from "../face-match/constants";
import {
  FACE_HUB_ACCESS_PERMISSIONS,
  FACE_HUB_ROUTE_NAME,
  LEGACY_FACE_ROUTE_NAMES,
  buildFaceHubRoute,
  canViewFaceHub,
  filterLegacyFaceRouteNames,
  isLegacyFaceRouteName,
  parseFaceHubParams,
  resolveFaceHubActiveTab,
  resolveFaceHubBadgeTab,
  resolveFaceHubTabs,
  resolveLegacyFaceRedirectParams,
  shouldFetchFaceHitUnread,
  type FaceHitUnreadQueryGate,
} from "./face-hub";

const P = FACE_MATCH_PERMISSIONS;
const holding = (...codes: string[]) => (permission: string) => codes.includes(permission);

// ───────── 入口：六个码任一 + 已登录 + 非审核 ─────────
assert.equal(FACE_HUB_ROUTE_NAME, "face-hub");
assert.deepEqual([...LEGACY_FACE_ROUTE_NAMES], ["face-unwelcome", "face-match-settings", "face-capture-results"]);
assert.equal(FACE_HUB_ACCESS_PERMISSIONS.length, 6);
assert.deepEqual(
  [...FACE_HUB_ACCESS_PERMISSIONS].sort(),
  [P.imagesView, P.unwelcomeManage, P.unwelcomeApprove, P.unwelcomeReview, P.manage, P.camerasManage].sort(),
  "入口 any-of 恰好是契约里的六个码",
);
for (const code of FACE_HUB_ACCESS_PERMISSIONS) {
  assert.equal(canViewFaceHub(true, holding(code), false), true, `${code} 单独持有就能看到入口`);
  assert.equal(canViewFaceHub(false, holding(code), false), false, `${code}：未登录不可见`);
  assert.equal(canViewFaceHub(true, holding(code), true), false, `${code}：iOS 审核离线会话不可见`);
}
assert.equal(canViewFaceHub(true, holding("Orders.View", "Users.View"), false), false, "不持有六个码任何一个：不可见");
assert.equal(canViewFaceHub(true, holding(), false), false);

// ───────── 页签：各按各自的码（设计稿 07 的四种人） ─────────
assert.deepEqual(resolveFaceHubTabs(holding(P.imagesView, P.unwelcomeReview, P.manage)), ["capture", "list", "settings"], "店长：三个页签");
assert.deepEqual(resolveFaceHubTabs(holding(P.unwelcomeReview)), ["list"], "只做复核的人：只有名单");
assert.deepEqual(resolveFaceHubTabs(holding(P.imagesView)), ["capture"], "只看抓拍的人：只有抓拍");
assert.deepEqual(resolveFaceHubTabs(holding(P.camerasManage)), ["settings"], "管摄像头的人：只有设置");
assert.deepEqual(resolveFaceHubTabs(holding(P.manage)), ["settings"], "FaceMatch.Manage 单独也只有设置");
assert.deepEqual(resolveFaceHubTabs(holding(P.unwelcomeManage)), ["list"]);
assert.deepEqual(resolveFaceHubTabs(holding(P.unwelcomeApprove)), ["list"]);
assert.deepEqual(resolveFaceHubTabs(holding()), [], "一个页签都没有：显示无权限提示");
// 入口可见 ⇔ 至少有一个页签（两处的权限口径不会漂移）
for (const code of FACE_HUB_ACCESS_PERMISSIONS) assert.ok(resolveFaceHubTabs(holding(code)).length > 0, `${code} 至少对应一个页签`);

// ───────── 红色数字挂在「他能处理的第一个页签」 ─────────
assert.equal(resolveFaceHubBadgeTab(["capture", "list", "settings"]), "capture", "有抓拍页签挂抓拍");
assert.equal(resolveFaceHubBadgeTab(["capture"]), "capture");
assert.equal(resolveFaceHubBadgeTab(["list", "settings"]), "list", "没有 Images.View 而有名单：挂名单");
assert.equal(resolveFaceHubBadgeTab(["list"]), "list");
assert.equal(resolveFaceHubBadgeTab(["settings"]), null, "只有设置页签：不挂");
assert.equal(resolveFaceHubBadgeTab([]), null);

// ───────── 当前页签：请求的可见就用，否则退回第一个 ─────────
assert.equal(resolveFaceHubActiveTab(["capture", "list"], "list"), "list");
assert.equal(resolveFaceHubActiveTab(["list", "settings"], "capture"), "list", "请求的页签没权限：退回第一个可见页签");
assert.equal(resolveFaceHubActiveTab(["settings"], null), "settings");
assert.equal(resolveFaceHubActiveTab(["capture", "list"], undefined), "capture");
assert.equal(resolveFaceHubActiveTab([], "capture"), null);

// ───────── 路由参数：白名单解析 ─────────
assert.deepEqual(parseFaceHubParams({ tab: "capture", filter: "blacklist", hitStatus: "unread" }), { tab: "capture", filter: "blacklist", hitStatus: "unread" });
assert.deepEqual(parseFaceHubParams({ tab: "list", sub: "review" }), { tab: "list", sub: "review" });
assert.deepEqual(parseFaceHubParams({ tab: "settings" }), { tab: "settings" });
assert.deepEqual(parseFaceHubParams({}), {});
assert.deepEqual(parseFaceHubParams({ tab: ["list", "capture"], sub: ["entries"] }), { tab: "list", sub: "entries" }, "数组参数取第一个");
for (const hitStatus of ["all", "unread", "pending", "autoConfirmed", "confirmed", "dismissed"]) {
  assert.equal(parseFaceHubParams({ hitStatus }).hitStatus, hitStatus);
}
assert.deepEqual(
  parseFaceHubParams({ tab: "admin", sub: "all", filter: "staff", hitStatus: "bogus" }),
  {},
  "未知值一律丢弃，不会把页面带进未定义状态",
);
assert.deepEqual(parseFaceHubParams({ tab: "CAPTURE", hitStatus: "Unread" }), {}, "大小写敏感");

assert.deepEqual(buildFaceHubRoute(), { pathname: "/(shell)/face-hub", params: {} });
assert.deepEqual(buildFaceHubRoute({ tab: "list", sub: "review" }), { pathname: "/(shell)/face-hub", params: { tab: "list", sub: "review" } });
assert.deepEqual(
  buildFaceHubRoute({ tab: "capture", filter: "blacklist", hitStatus: "unread" }),
  { pathname: "/(shell)/face-hub", params: { tab: "capture", filter: "blacklist", hitStatus: "unread" } },
);

// ───────── 旧入口：别名、过滤、重定向目标 ─────────
for (const name of LEGACY_FACE_ROUTE_NAMES) assert.equal(isLegacyFaceRouteName(name), true);
for (const name of ["face-hub", "notifications", "", undefined, "face-unwelcome/entry-new"]) {
  assert.equal(isLegacyFaceRouteName(name), false, `${String(name)} 不是旧入口`);
}
assert.deepEqual(
  filterLegacyFaceRouteNames(["home", "face-unwelcome", "face-hub", "face-match-settings", "face-capture-results", "notifications"]),
  ["home", "face-hub", "notifications"],
  "只去掉旧三项，其余顺序不变",
);
assert.deepEqual(resolveLegacyFaceRedirectParams("face-capture-results"), { tab: "capture" });
assert.deepEqual(resolveLegacyFaceRedirectParams("face-unwelcome"), { tab: "list" });
assert.deepEqual(resolveLegacyFaceRedirectParams("face-match-settings"), { tab: "settings" });

// ───────── 红色数字取数条件：导航就绪 + face-hub 可见 + 持有 Review ─────────
{
  const base: FaceHitUnreadQueryGate = {
    navigationReady: true,
    isAuthenticated: true,
    isReviewSession: false,
    isDeviceMode: false,
    faceHubVisible: true,
    hasUnwelcomeReview: true,
  };
  assert.equal(shouldFetchFaceHitUnread(base), true);
  assert.equal(shouldFetchFaceHitUnread({ ...base, navigationReady: false }), false, "导航没就绪不取");
  assert.equal(shouldFetchFaceHitUnread({ ...base, faceHubVisible: false }), false, "face-hub 不可见不取");
  assert.equal(shouldFetchFaceHitUnread({ ...base, hasUnwelcomeReview: false }), false, "没有 Review 的人收不到命中通知，不取");
  assert.equal(shouldFetchFaceHitUnread({ ...base, isAuthenticated: false }), false);
  assert.equal(shouldFetchFaceHitUnread({ ...base, isReviewSession: true }), false, "审核离线会话不取");
  assert.equal(shouldFetchFaceHitUnread({ ...base, isDeviceMode: true }), false, "设备模式没有用户账号，不取");
}

// ───────── 源码契约：接线 ─────────
{
  const mobileRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
  const read = (relative: string) => readFileSync(resolve(mobileRoot, relative), "utf8");

  const layout = read("app/(shell)/_layout.tsx");
  assert.ok(layout.includes("queryKey: faceHitUnreadQueryKey(userGuid)"), "红色数字的缓存键带账号编号");
  assert.ok(layout.includes("getUnreadCount(FACE_HIT_NOTIFICATION_TYPE, signal)"), "按命中通知类型取未读数");
  assert.ok(layout.includes("shouldFetchFaceHitUnread({") && layout.includes("faceHubVisible: visibleRouteNames.has(FACE_HUB_ROUTE_NAME)"));
  assert.ok(layout.includes("FACE_MATCH_PERMISSIONS.unwelcomeReview"), "只有持 Review 的人才取");
  assert.ok(/staleTime:\s*FACE_HIT_UNREAD_STALE_MS/.test(layout) && /FACE_HIT_UNREAD_STALE_MS\s*=\s*30_000/.test(layout), "staleTime 30 秒");
  assert.ok(/FACE_HIT_UNREAD_REFETCH_INTERVAL_MS\s*=\s*60_000/.test(layout), "轮询 60 秒");
  assert.ok(/refetchInterval:\s*appActive \? FACE_HIT_UNREAD_REFETCH_INTERVAL_MS : false/.test(layout), "只在前台轮询");
  assert.ok(layout.includes("appActive && !wasActive && faceHitUnreadEnabled"), "回到前台时刷新");
  assert.ok(layout.includes("faceHitUnreadCount: faceHitUnreadEnabled ? (faceHitUnreadQuery.data ?? 0) : 0"), "失败时沿用上一次的数据（data 不被清空），没有权限时为 0");

  assert.ok(/faceHitUnreadCount:\s*number/.test(read("src/modules/navigation/access-context.tsx")));
  const workbench = read("src/modules/workbench/workbench-screen.tsx");
  assert.ok(/"face-hub":\s*faceHitUnreadCount/.test(workbench), "工作台「人脸比对」入口角标 = 命中未读数");
  assert.ok(read("src/components/navigation/PrimaryTabBar.tsx").includes("faceHitUnreadCount"));

  // 通知收件箱标记已读后红色数字同步（全部已读直接写回，单条已读重新取）
  const notifications = read("src/modules/notifications/use-notifications.ts");
  assert.ok(notifications.includes("writeFaceHitUnreadCount(queryClient, userGuid, 0)"));
  assert.ok(notifications.includes("invalidateQueries({ queryKey: faceHitUnreadQueryKey(userGuid) })"));
}

console.log("face-hub.test.ts: ok");
