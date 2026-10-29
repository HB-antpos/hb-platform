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
  "管理员：三个页签按固定顺序（没有资料权限时不出现「资料」）",
);
assertEqual(
  resolveHubTabs({ canViewPeople: true, canViewProfiles: true, canViewRolesAndPermissions: true }),
  ["people", "profiles", "roles", "permissions"],
  "全部权限：人员 | 资料 | 角色 | 权限，「资料」排在「人员」后面",
);
assertEqual(
  resolveHubTabs({ canViewPeople: true, canViewProfiles: false, canViewRolesAndPermissions: false }),
  ["people"],
  "没有 ViewManagedStore：页面和之前一样，只有人员",
);
assertEqual(
  resolveHubTabs({ canViewPeople: true, canViewProfiles: true, canViewRolesAndPermissions: false }),
  ["people", "profiles"],
  "店长：人员 | 资料",
);
assertEqual(
  resolveHubTabs({ canViewPeople: false, canViewProfiles: true, canViewRolesAndPermissions: false }),
  ["profiles"],
  "只持有 ViewManagedStore（没有任何 Users.*）：只看到「资料」",
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
assertEqual(shouldShowHubTabBar(["profiles"]), false, "只有「资料」一个页签不画页签条");
assertEqual(shouldShowHubTabBar(["people", "profiles"]), true, "人员 + 资料画页签条");
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

// ── 资料页签的 ?tab= ──
const withProfiles = ["people", "profiles", "roles", "permissions"] as const;
assertEqual(normalizeHubTab("profiles", withProfiles), "profiles", "有权限时 tab=profiles 合法");
assertEqual(normalizeHubTab("profiles", all), "people", "没有资料权限时 tab=profiles 回落到人员");
assertEqual(normalizeHubTab(undefined, ["profiles"]), "profiles", "只有资料页签时缺省落到资料");
assertEqual(normalizeHubTab("people", ["profiles"]), "profiles", "只有资料页签时 tab=people 回落到资料");
assertEqual(normalizeHubTab("roles", ["profiles"]), "profiles", "只有资料页签时 tab=roles 回落到资料");
assertEqual(isPeopleHubTab("profiles"), true, "profiles 是合法页签");

// ── 类型守卫 ──
assertEqual(isPeopleHubTab("people"), true, "people 合法");
assertEqual(isPeopleHubTab("users"), false, "users 不是页签");
assertEqual(isPeopleHubTab(null), false, "null 不是页签");

// ── 列表模式 ──
assertEqual(resolvePeopleListMode({ identityUsersAllowed: true }), "admin", "全局用户查看 + account 会话 → 管理员列表");
assertEqual(resolvePeopleListMode({ identityUsersAllowed: false }), "store", "否则 → 本店店员列表");
// 回归（截图：店长在列表里看到订货员 / 仓库员工 / 其他店长）：店长即使持有 Users.View 也只能看自己管理分店里的店员。
assertEqual(
  resolvePeopleListMode({ identityUsersAllowed: true, isStoreLevelManager: true }),
  "store",
  "店长（持有全局用户查看权限）→ 仍是本店店员列表",
);
assertEqual(
  resolvePeopleListMode({ identityUsersAllowed: true, isStoreLevelManager: false }),
  "admin",
  "非店长的全局用户查看者 → 管理员列表",
);

// ── Hub 路由对象 ──
assertEqual(buildPeopleHubRoute("roles"), { pathname: "/(shell)/users", params: { tab: "roles" } }, "角色页签路由");
assertEqual(buildPeopleHubRoute("permissions"), { pathname: "/(shell)/users", params: { tab: "permissions" } }, "权限页签路由");
assertEqual(buildPeopleHubRoute(), { pathname: "/(shell)/users", params: { tab: "people" } }, "缺省回到人员页签");

console.log("people-hub-tabs.test.ts: ok");
