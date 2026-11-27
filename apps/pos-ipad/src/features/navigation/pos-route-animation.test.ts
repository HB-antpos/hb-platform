import assert from "node:assert/strict";
import test from "node:test";

import { POS_DESTINATION_ROUTES } from "./pos-destination-access";
import { posStackAnimationFor } from "./pos-route-animation";

test("显示左侧栏的页面之间切换不做动画，侧栏看起来固定", () => {
  for (const name of ["sales", "special-products", "settings", "held-orders", "supplier-assistant"]) {
    assert.equal(posStackAnimationFor(name), "none", name);
  }
  // 侧栏新增入口自动纳入。
  for (const path of Object.values(POS_DESTINATION_ROUTES)) {
    assert.equal(posStackAnimationFor(path.slice(1)), "none", path);
  }
});

test("付款等子流程与登录、注册页保留默认滑入", () => {
  for (const name of ["payment", "login", "registration", "index", "network-check"]) {
    assert.equal(posStackAnimationFor(name), "default", name);
  }
});
