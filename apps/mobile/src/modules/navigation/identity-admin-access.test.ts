import assert from "node:assert/strict";
import { resolveIdentityAdminRouteNames } from "./identity-admin-access";
import { buildWorkbenchSections } from "./workbench";
import { resolveTabRouteCorrection } from "./default-route";

const admin = {
  isAuthenticated: true, sessionKind: "account", iosReviewOfflineGuardActive: false,
  isAdmin: true, canReadUsers: true, canReadRoles: true, menuReady: true,
};
const legacy = ["users", "employee-profile", "app-downloads", "settings"];
const routes = resolveIdentityAdminRouteNames(legacy, admin);
assert.deepEqual(routes, [...legacy, "user-admin", "roles", "permissions"]);
assert.deepEqual(legacy, ["users", "employee-profile", "app-downloads", "settings"]);
assert.deepEqual(resolveIdentityAdminRouteNames(routes, admin), routes, "新服务端菜单不得重复补齐");

// 「人员管理」合并后：工作台只有一个 users 入口，user-admin / roles / permissions 不再是工作台砖，
// 但路由保留为重定向壳（旧版 App、推送、书签），不能被 Shell 纠偏回工作台。
const section = buildWorkbenchSections(routes).find(item => item.key === "people-management");
assert.deepEqual(section?.items.map(item => item.routeName), [
  "users", "employee-profile", "app-downloads",
], "人员管理只留一个入口，旧的用户 / 角色 / 权限入口不再出现在工作台");
for (const route of ["user-admin", "roles", "permissions"]) {
  assert.equal(resolveTabRouteCorrection({
    currentRouteName: route, hasAppliedDefaultRoute: true, isDeviceMode: false, routeNames: routes,
  }), null, "重定向壳不能被 Shell 纠偏回工作台");
  assert.equal(resolveTabRouteCorrection({
    currentRouteName: route, hasAppliedDefaultRoute: true, isDeviceMode: false, routeNames: legacy,
  }), null, "即使菜单里没有这些旧入口，深链也要先让页面重定向到 Hub（Hub 里无权限的页签会回落到人员）");
}
assert.equal(resolveTabRouteCorrection({
  currentRouteName: "users", hasAppliedDefaultRoute: true, isDeviceMode: false, routeNames: routes,
}), null, "人员管理入口本身可见");
assert.equal(resolveTabRouteCorrection({
  currentRouteName: "users", hasAppliedDefaultRoute: true, isDeviceMode: false, routeNames: ["settings"],
}), "/(shell)/workbench", "没有 users 入口的账号仍按原规则回到工作台");

assert.deepEqual(resolveIdentityAdminRouteNames(legacy, { ...admin, isAdmin: false }), legacy,
  "普通账号即使持有读权限，也不能补回服务端未下发或关闭的菜单");
assert.deepEqual(resolveIdentityAdminRouteNames(routes, { ...admin, isAdmin: false, canReadRoles: false }),
  [...legacy, "user-admin"], "服务端菜单仍须匹配页面自身权限；权限管理与角色管理同样只认 Roles.View");
assert.deepEqual(resolveIdentityAdminRouteNames(routes, { ...admin, isAdmin: false, canReadUsers: false, canReadRoles: false }), legacy);

// 升级兜底：只持有旧入口权限的账号，服务端菜单里未必有 users，也要能从工作台进到人员管理。
const noUsersMenu = ["employee-profile", "settings"];
assert.deepEqual(
  resolveIdentityAdminRouteNames([...noUsersMenu, "roles", "permissions"], { ...admin, isAdmin: false, canReadUsers: false }),
  [...noUsersMenu, "roles", "permissions", "users"],
  "只有 Roles.View：角色 / 权限菜单还在，且补齐 users 入口",
);
assert.deepEqual(
  resolveIdentityAdminRouteNames([...noUsersMenu, "user-admin"], { ...admin, isAdmin: false, canReadRoles: false }),
  [...noUsersMenu, "user-admin", "users"],
  "只有旧的用户管理入口：补齐 users 入口",
);
assert.deepEqual(
  resolveIdentityAdminRouteNames([...noUsersMenu, "user-admin", "roles", "permissions"], { ...admin, isAdmin: false }),
  [...noUsersMenu, "user-admin", "roles", "permissions", "users"],
  "同时持有三个旧入口：只补一个 users",
);
assert.deepEqual(
  resolveIdentityAdminRouteNames([...noUsersMenu, "roles"], { ...admin, isAdmin: false, canReadRoles: false, canReadUsers: false }),
  noUsersMenu,
  "没有对应读权限时旧入口被收走，也不会因此补 users",
);
assert.deepEqual(
  resolveIdentityAdminRouteNames(noUsersMenu, { ...admin, isAdmin: false }),
  noUsersMenu,
  "菜单里没有任何旧入口的普通账号不会因为持有读权限而多出入口",
);
assert.deepEqual(
  resolveIdentityAdminRouteNames(["user-admin", "roles", "settings"], { ...admin, isAdmin: false }).filter((name) => name === "users"),
  ["users"],
  "补齐的 users 只出现一次",
);
assert.deepEqual(
  resolveIdentityAdminRouteNames(noUsersMenu, admin),
  [...noUsersMenu, "user-admin", "roles", "permissions", "users"],
  "管理员：服务端未下发新入口时一并补齐 users",
);

for (const sessionKind of ["device", "deviceAccount", "iosReview", "none"]) {
  assert.deepEqual(resolveIdentityAdminRouteNames(routes, { ...admin, sessionKind }), legacy,
    `${sessionKind} 不能利用缓存管理员角色或后端菜单进入全局管理`);
  assert.deepEqual(resolveIdentityAdminRouteNames([...noUsersMenu, "roles", "permissions"], { ...admin, isAdmin: false, canReadUsers: false, sessionKind }), noUsersMenu,
    `${sessionKind} 不会因为兜底而多出人员管理入口`);
}
assert.deepEqual(resolveIdentityAdminRouteNames(routes, { ...admin, isAuthenticated: false }), legacy);
assert.deepEqual(resolveIdentityAdminRouteNames(routes, { ...admin, iosReviewOfflineGuardActive: true }), legacy);
assert.deepEqual(resolveIdentityAdminRouteNames(legacy, { ...admin, menuReady: false }), legacy,
  "菜单加载中或失败不能补齐新入口");
assert.deepEqual(resolveIdentityAdminRouteNames([], admin), []);
assert.deepEqual(resolveIdentityAdminRouteNames(["settings"], admin), ["settings"]);
assert.deepEqual(resolveIdentityAdminRouteNames(["workbench", "settings"], admin), ["workbench", "settings"]);
console.log("identity-admin navigation access tests passed");
