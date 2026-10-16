import {
  buildPeopleHubRoute,
  isPeopleHubTab,
  normalizeHubTab,
  resolveHubTabs,
  resolvePeopleListMode,
  shouldShowHubTabBar,
} from "./people-hub-tabs";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    throw new Error(`${label}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`);
  }
}

// ── 页签可见性 ──
assertEqual(
  resolveHubTabs({ canViewPeople: true, canViewRolesAndPermissions: true }),
  ["people", "roles", "permissions"],
  "管理员：三个页签按固定顺序",
);
assertEqual(
  resolveHubTabs({ canViewPeople: true, canViewRolesAndPermissions: false }),
  ["people"],
  "店长 / 无 Roles.View：只有人员",
);
assertEqual(
  resolveHubTabs({ canViewPeople: false, canViewRolesAndPermissions: true }),
  ["roles", "permissions"],
  "只有 Roles.View：没有人员页签",
);
assertEqual(resolveHubTabs({ canViewPeople: false, canViewRolesAndPermissions: false }), [], "什么权限都没有：没有页签");

// ── 页签条只在多页签时出现 ──
assertEqual(shouldShowHubTabBar(["people"]), false, "单页签不画页签条");
assertEqual(shouldShowHubTabBar(["roles", "permissions"]), true, "两个页签画页签条");
assertEqual(shouldShowHubTabBar([]), false, "无页签不画页签条");

// ── ?tab= 规范化 ──
const all = ["people", "roles", "permissions"] as const;
assertEqual(normalizeHubTab("roles", all), "roles", "合法且有权限");
assertEqual(normalizeHubTab("permissions", all), "permissions", "合法且有权限（权限）");
assertEqual(normalizeHubTab(undefined, all), "people", "缺省回落到人员");
assertEqual(normalizeHubTab("nonsense", all), "people", "非法值回落到人员");
assertEqual(normalizeHubTab("", all), "people", "空串回落到人员");
assertEqual(normalizeHubTab("roles", ["people"]), "people", "没有角色权限时 tab=roles 回落到人员");
assertEqual(normalizeHubTab("permissions", ["people"]), "people", "没有权限时 tab=permissions 回落到人员");
assertEqual(normalizeHubTab("people", ["roles", "permissions"]), "roles", "没有人员页签时回落到第一个可见页签");
assertEqual(normalizeHubTab("people", []), "people", "没有任何页签时仍返回人员（由页面显示无权限）");
assertEqual(normalizeHubTab(["permissions", "roles"], all), "permissions", "重复参数取第一个");
assertEqual(normalizeHubTab(42, all), "people", "非字符串回落到人员");

// ── 类型守卫 ──
assertEqual(isPeopleHubTab("people"), true, "people 合法");
assertEqual(isPeopleHubTab("users"), false, "users 不是页签");
assertEqual(isPeopleHubTab(null), false, "null 不是页签");

// ── 列表模式 ──
assertEqual(resolvePeopleListMode({ identityUsersAllowed: true }), "admin", "全局用户查看 + account 会话 → 管理员列表");
assertEqual(resolvePeopleListMode({ identityUsersAllowed: false }), "store", "否则 → 本店店员列表");

// ── Hub 路由对象 ──
assertEqual(buildPeopleHubRoute("roles"), { pathname: "/(shell)/users", params: { tab: "roles" } }, "角色页签路由");
assertEqual(buildPeopleHubRoute("permissions"), { pathname: "/(shell)/users", params: { tab: "permissions" } }, "权限页签路由");
assertEqual(buildPeopleHubRoute(), { pathname: "/(shell)/users", params: { tab: "people" } }, "缺省回到人员页签");

console.log("people-hub-tabs.test.ts: ok");
