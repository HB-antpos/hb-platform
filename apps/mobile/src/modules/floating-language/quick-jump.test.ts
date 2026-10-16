import assert from "node:assert/strict";
import test from "node:test";
import { TAB_PATHS } from "@/modules/navigation/default-route";
import {
  buildQuickJumpModel,
  pushRecentRoute,
  QUICK_JUMP_RECENT_LIMIT,
  resolveQuickJumpAction,
} from "./quick-jump";

const visible = ["workbench", "product-query", "price-updates", "reports", "users", "settings"];

test("入口只来自可见路由，按工作台分组；工作台 / 设置单独给按钮", () => {
  const model = buildQuickJumpModel({
    orderedVisibleRouteNames: visible,
    recentRouteNames: [],
    menuUsable: true,
  });
  const routes = model.sections.flatMap((section) => section.items.map((item) => item.routeName));
  assert.deepEqual(routes.sort(), ["price-updates", "product-query", "reports", "users"].sort());
  assert.equal(model.canOpenWorkbench, true);
  assert.equal(model.canOpenSettings, true);
});

test("菜单加载中或出错时不列入口，只留工作台 / 设置", () => {
  const model = buildQuickJumpModel({
    orderedVisibleRouteNames: visible,
    recentRouteNames: ["reports"],
    menuUsable: false,
  });
  assert.deepEqual(model.sections, []);
  assert.deepEqual(model.recent, []);
  assert.equal(model.canOpenWorkbench, true);
});

test("最近访问：去掉当前页和已无权限的入口，限量", () => {
  const model = buildQuickJumpModel({
    orderedVisibleRouteNames: visible,
    recentRouteNames: ["reports", "roles", "users", "price-updates", "product-query"],
    currentRouteName: "reports",
    menuUsable: true,
  });
  assert.deepEqual(model.recent.map((item) => item.routeName), ["users", "price-updates", "product-query"]);
});

test("记录最近访问：放最前、去重、限量，非工作台入口不记", () => {
  const trackable = new Set(["a", "b", "c", "d", "e"]);
  assert.deepEqual(pushRecentRoute(["a", "b"], "b", trackable), ["b", "a"]);
  assert.deepEqual(pushRecentRoute(["a", "b", "c", "d"], "e", trackable), ["e", "a", "b", "c"]);
  assert.equal(pushRecentRoute(["a", "b", "c", "d"], "e", trackable).length, QUICK_JUMP_RECENT_LIMIT);
  assert.deepEqual(pushRecentRoute(["a"], "settings", trackable), ["a"]);
  assert.deepEqual(pushRecentRoute(["a"], undefined, trackable), ["a"]);
});

test("跳转规则与工作台磁贴一致", () => {
  assert.deepEqual(
    resolveQuickJumpAction({ targetRouteName: "reports", currentRouteName: "reports", reportsLocked: false }),
    { kind: "none" }
  );
  assert.deepEqual(
    resolveQuickJumpAction({ targetRouteName: "workbench", currentRouteName: "users", reportsLocked: false }),
    { kind: "dismiss-to", path: TAB_PATHS.workbench }
  );
  assert.deepEqual(
    resolveQuickJumpAction({ targetRouteName: "reports", currentRouteName: "users", reportsLocked: true }),
    { kind: "price-updates-overdue" }
  );
  assert.deepEqual(
    resolveQuickJumpAction({ targetRouteName: "users", currentRouteName: "workbench", reportsLocked: true }),
    { kind: "push", path: TAB_PATHS.users }
  );
  assert.deepEqual(
    resolveQuickJumpAction({ targetRouteName: "no-such-route", reportsLocked: false }),
    { kind: "none" }
  );
});
