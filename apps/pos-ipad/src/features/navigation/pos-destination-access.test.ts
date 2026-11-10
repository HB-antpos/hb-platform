import assert from "node:assert/strict";
import test from "node:test";

import {
  passesPosDestinationGuard,
  POS_DESTINATION_ROUTES,
  registerPosDestinationGuard,
  resolvePosDestinationAccess,
} from "./pos-destination-access";

import { LOCAL_HISTORY_VIEW_PERMISSION } from "@/features/local-history/local-history-presenter";
import { SETTINGS_VIEW_PERMISSION } from "@/features/settings/settings-authorization";
import { POS_DESTINATION_IDS } from "@/ui/shell/pos-navigation";

test("没有收银员会话时不显示任何入口", () => {
  assert.equal(resolvePosDestinationAccess(null).size, 0);
  assert.equal(resolvePosDestinationAccess(undefined).size, 0);
});

test("不带任何权限的收银员只看到不需要权限的入口", () => {
  assert.deepEqual([...resolvePosDestinationAccess([])].sort(), [
    "attendance-audit",
    "held-orders",
    "payment-recovery",
    "returns",
    "sync-history",
  ]);
});

test("带有查看权限时显示对应入口，入口顺序固定", () => {
  const access = resolvePosDestinationAccess([
    SETTINGS_VIEW_PERMISSION,
    LOCAL_HISTORY_VIEW_PERMISSION,
  ]);
  assert.equal(access.has("settings"), true);
  // 本机历史与远程历史共用同一个「历史查看」权限码
  assert.equal(access.has("local-history"), true);
  assert.equal(access.has("remote-history"), true);
  assert.equal(access.has("daily-close"), false);
  assert.equal(access.has("installments"), false);
  assert.equal(access.has("catalog-maintenance"), false);
  assert.deepEqual(
    [...access],
    POS_DESTINATION_IDS.filter((id) => access.has(id)),
  );
});

test("每个入口都有对应的路由路径", () => {
  for (const id of POS_DESTINATION_IDS) {
    assert.match(POS_DESTINATION_ROUTES[id], /^\/[a-z-]+$/, id);
  }
});

test("进入前检查：未登记的入口放行，登记后按检查结果，撤销后恢复放行", () => {
  assert.equal(passesPosDestinationGuard("held-orders"), true);

  let allowed = false;
  const unregister = registerPosDestinationGuard("held-orders", () => allowed);
  assert.equal(passesPosDestinationGuard("held-orders"), false);
  assert.equal(passesPosDestinationGuard("returns"), true);
  allowed = true;
  assert.equal(passesPosDestinationGuard("held-orders"), true);

  // 新页面登记后，旧页面迟到的撤销不能把新登记清掉
  const unregisterNewer = registerPosDestinationGuard("held-orders", () => false);
  unregister();
  assert.equal(passesPosDestinationGuard("held-orders"), false);
  unregisterNewer();
  assert.equal(passesPosDestinationGuard("held-orders"), true);
});
