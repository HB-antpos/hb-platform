import assert from "node:assert/strict";
import { buildAccess } from "../../shared/utils/access";
import {
  canAccessVersionManagement,
  filterVersionManagementRoutes,
  VERSION_MANAGEMENT_ROUTES,
} from "./version-management-access";
import { buildWorkbenchSections } from "./workbench";
import { buildPrimaryNavigation } from "./primary-navigation";
import { TAB_PATHS, getVisibleTabRouteNames } from "./default-route";

for (const role of ["Admin", "管理员", "SuperAdmin", "超级管理员"]) {
  const access = buildAccess({
    userGUID: "admin",
    userGuid: "admin",
    stores: [],
    username: "admin",
    email: "",
    storeNames: [],
    roleNames: [role],
    permissions: [],
  });
  for (const sessionKind of ["account", "deviceAccount"]) {
    assert.equal(
      canAccessVersionManagement({
        isAdmin: access.isAdmin,
        isAuthenticated: true,
        sessionKind,
      }),
      true,
    );
  }
}
for (const role of ["StoreManager", "WarehouseManager", "User"]) {
  const access = buildAccess({
    userGUID: "user",
    userGuid: "user",
    stores: [],
    username: "user",
    email: "",
    storeNames: [],
    roleNames: [role],
    permissions: ["System.ViewAppDownloads", "System.ManageAppDownloads"],
  });
  assert.equal(
    canAccessVersionManagement({
      isAdmin: access.isAdmin,
      isAuthenticated: true,
      sessionKind: "account",
    }),
    false,
  );
}
for (const sessionKind of ["device", "iosReview", "unknown"]) {
  assert.equal(
    canAccessVersionManagement({
      isAdmin: true,
      isAuthenticated: true,
      sessionKind,
    }),
    false,
  );
}
assert.equal(
  canAccessVersionManagement({
    isAdmin: true,
    isAuthenticated: false,
    sessionKind: "account",
  }),
  false,
);
const routes = ["workbench", ...VERSION_MANAGEMENT_ROUTES, "settings"];
assert.deepEqual(filterVersionManagementRoutes(routes, false), [
  "workbench",
  "settings",
]);
assert.deepEqual(filterVersionManagementRoutes(routes, true), routes);
const deviceRoutes = getVisibleTabRouteNames({
  routeNames: routes,
  isDeviceMode: true,
});
assert.equal(
  deviceRoutes.some((route) =>
    VERSION_MANAGEMENT_ROUTES.some((name) => name === route),
  ),
  false,
);
for (const route of VERSION_MANAGEMENT_ROUTES) {
  assert.equal(TAB_PATHS[route], `/(shell)/${route}`);
  // 版本发布中心合并了 App 下载与 WPF 版本：工作台只有它一个入口，wpf-versions 仅作旧深链的跳转。
  assert.equal(
    buildWorkbenchSections(routes).some((section) =>
      section.items.some((item) => item.routeName === route),
    ),
    route === "app-downloads",
  );
  assert.equal(
    buildPrimaryNavigation({
      activeRouteName: route,
      visibleRouteNames: routes,
    }).find((item) => item.active)?.key,
    "workbench",
  );
}
console.log("版本管理管理员访问、设备隔离与导航测试通过");
