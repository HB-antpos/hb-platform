import assert from "node:assert/strict";
import test from "node:test";
import {
  beginAccountSwitch,
  endAccountSwitch,
  getAccountSessionGeneration,
  isAccountSwitchInProgress,
  isStaleSessionGeneration,
  resetAccountSwitchGateForTests,
  waitForAccountSwitchIdle,
} from "./account-switch-gate";

test("闸门：切换期间请求等待，结束后放行；会话变了代号 +1", async () => {
  resetAccountSwitchGateForTests();
  assert.equal(isAccountSwitchInProgress(), false);
  await waitForAccountSwitchIdle(); // 没切换：立即返回

  beginAccountSwitch();
  beginAccountSwitch(); // 重复调用无副作用
  assert.equal(isAccountSwitchInProgress(), true);
  const stamp = getAccountSessionGeneration();

  let released = false;
  const waiter = waitForAccountSwitchIdle().then(() => {
    released = true;
  });
  await Promise.resolve();
  assert.equal(released, false);

  endAccountSwitch({ sessionChanged: true });
  await waiter;
  assert.equal(released, true);
  assert.equal(isAccountSwitchInProgress(), false);
  assert.equal(getAccountSessionGeneration(), stamp + 1);
  // 切换前盖的代号现在过期；没盖代号的不算过期
  assert.equal(isStaleSessionGeneration(stamp), true);
  assert.equal(isStaleSessionGeneration(getAccountSessionGeneration()), false);
  assert.equal(isStaleSessionGeneration(undefined), false);
});

test("闸门：会话没变（失败回滚）时代号不动", async () => {
  resetAccountSwitchGateForTests();
  const stamp = getAccountSessionGeneration();
  beginAccountSwitch();
  endAccountSwitch({ sessionChanged: false });
  assert.equal(getAccountSessionGeneration(), stamp);
  assert.equal(isAccountSwitchInProgress(), false);
});
