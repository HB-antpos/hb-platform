// 「人员管理」Hub（路由 /users）的纯逻辑：页签可见性与 ?tab= 参数规范化、人员页签用哪种列表。
// 不依赖 React Native，可在 Node 下测试。
//
// 背景：原「员工管理」（店长）与「用户管理 / 角色管理 / 权限管理」（管理员）合并成一个入口，
// 页签 = 人员 | 资料 | 角色 | 权限。合并的是入口不是权限：
// - 「人员」页签：持有任一人员查看权限即显示（店长走本店店员列表，管理员走全局用户列表）；
// - 「资料」页签（员工个人信息维护）：只有持有 EmployeeProfiles.ViewManagedStore（且 account 会话、非设备、非 iOS 审核）才显示；
//   没有该权限的账号看不到这个页签，页面行为与之前完全一样；只持有该权限（没有任何 Users.*）的账号只会看到「资料」；
// - 「角色」「权限」页签：只有满足原 roles / permissions 入口门禁（account 会话、非设备、非 iOS 审核、Roles.View）才显示；
// - 只剩一个页签时不画页签条。

export type PeopleHubTab = "people" | "profiles" | "roles" | "permissions";

/** 页签顺序即界面顺序。 */
export const PEOPLE_HUB_TAB_ORDER: readonly PeopleHubTab[] = ["people", "profiles", "roles", "permissions"];

export function isPeopleHubTab(value: unknown): value is PeopleHubTab {
  return typeof value === "string" && (PEOPLE_HUB_TAB_ORDER as readonly string[]).includes(value);
}

export interface PeopleHubAccess {
  /** 能看人员列表：全局用户查看（account 会话）或本店店员查看权限。 */
  canViewPeople: boolean;
  /** 能看「资料」页签：持有 EmployeeProfiles.ViewManagedStore 且满足 account 会话 / 非设备 / 非审核门禁。 */
  canViewProfiles?: boolean;
  /** 能看角色 / 权限页签：与 roles/ permissions/ 的入口门禁一致（identity-admin-access 的 Roles.View 判断）。 */
  canViewRolesAndPermissions: boolean;
}

/** 当前账号能看到的页签（按固定顺序）。 */
export function resolveHubTabs({ canViewPeople, canViewProfiles = false, canViewRolesAndPermissions }: PeopleHubAccess): PeopleHubTab[] {
  const tabs: PeopleHubTab[] = [];
  if (canViewPeople) tabs.push("people");
  // 「资料」紧跟「人员」之后（页签顺序：人员 | 资料 | 角色 | 权限）。
  if (canViewProfiles) tabs.push("profiles");
  if (canViewRolesAndPermissions) tabs.push("roles", "permissions");
  return tabs;
}

/** 路由参数可能是数组（重复参数）；取第一个。 */
function firstParam(raw: unknown): unknown {
  return Array.isArray(raw) ? raw[0] : raw;
}

/**
 * 规范化 ?tab=：非法值、没有权限的页签一律回落。
 * 回落顺序：「人员」可用就回「人员」，否则回第一个可见页签；一个都没有时返回「人员」（调用方据 tabs 为空显示无权限提示）。
 */
export function normalizeHubTab(raw: unknown, available: readonly PeopleHubTab[]): PeopleHubTab {
  const requested = firstParam(raw);
  if (isPeopleHubTab(requested) && available.includes(requested)) return requested;
  if (available.includes("people")) return "people";
  return available[0] ?? "people";
}

/** 只有多于一个页签才需要页签条。 */
export function shouldShowHubTabBar(tabs: readonly PeopleHubTab[]): boolean {
  return tabs.length > 1;
}

/** 人员页签内容：管理员（全局用户查看 + account 会话）看全局用户，其余人看本店店员。 */
export type PeopleListMode = "admin" | "store";

export function resolvePeopleListMode({
  identityUsersAllowed,
  isStoreLevelManager = false,
}: {
  identityUsersAllowed: boolean;
  /** 店长（非管理员、非仓库经理）：永远走店员口径——只看自己「管理分店」里角色是店员的账号，其他角色不可见。 */
  isStoreLevelManager?: boolean;
}): PeopleListMode {
  // 关键逻辑：店长可能碰巧持有 Users.View 等全局读权限（测试店长账号就有），但「全局用户列表」会把订货员、
  // 仓库员工、其他店长一并列出来。可见范围必须由「身份」而不是「恰好有哪个权限码」决定。
  return identityUsersAllowed && !isStoreLevelManager ? "admin" : "store";
}

export interface PeopleHubRoute {
  pathname: "/(shell)/users";
  params: { tab: PeopleHubTab };
}

/** 重定向壳（user-admin / roles / permissions）跳到 Hub 对应页签用的路由对象；写法与 face-hub 的 buildFaceHubRoute 一致。 */
export function buildPeopleHubRoute(tab: PeopleHubTab = "people"): PeopleHubRoute {
  return { pathname: "/(shell)/users", params: { tab } };
}
