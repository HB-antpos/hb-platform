import assert from "node:assert/strict";
import test from "node:test";

import {
  formatRuntimeAppVersion,
  RuntimeStatusReporter,
  type RuntimeStatusPayload,
} from "./runtime-status-reporter";

function createApi() {
  const calls: RuntimeStatusPayload[] = [];
  let release: (() => void) | null = null;
  let failure: unknown = null;
  let blocked = false;
  return {
    calls,
    block: () => { blocked = true; },
    release: () => { blocked = false; release?.(); },
    fail: (error: unknown) => { failure = error; },
    api: {
      async reportRuntimeStatus(payload: RuntimeStatusPayload): Promise<void> {
        calls.push(payload);
        if (blocked) await new Promise<void>((resolve) => { release = resolve; });
        if (failure) throw failure;
      },
    },
  };
}

test("上报在线心跳并带上版本号；收银员可选", async () => {
  const fake = createApi();
  await new RuntimeStatusReporter({ api: fake.api, appVersion: " 1.0.10 " }).report();
  assert.deepEqual(fake.calls, [{ isOnline: true, appVersion: "1.0.10" }]);

  const withCashier = createApi();
  await new RuntimeStatusReporter({
    api: withCashier.api,
    appVersion: "1.0.10",
    getCurrentCashier: () => ({ id: "c1", name: "Alice" }),
  }).report();
  assert.deepEqual(withCashier.calls, [
    { isOnline: true, appVersion: "1.0.10", currentCashierId: "c1", currentCashierName: "Alice" },
  ]);
});

test("超长或空白版本号不上报（服务端保留旧值），收银员读取异常不影响上报", async () => {
  const fake = createApi();
  const reporter = new RuntimeStatusReporter({
    api: fake.api,
    appVersion: "x".repeat(51),
    getCurrentCashier: () => { throw new Error("boom"); },
  });
  await reporter.report();
  assert.deepEqual(fake.calls, [{ isOnline: true, appVersion: null }]);
});

test("同一时刻只保留一个在途请求；完成后可再次上报；失败原样抛出", async () => {
  const fake = createApi();
  let nowMs = 1_000_000;
  const reporter = new RuntimeStatusReporter({
    api: fake.api,
    appVersion: "1.0.10",
    now: () => nowMs,
  });
  fake.block();
  const first = reporter.report();
  const second = reporter.report();
  assert.equal(first, second);
  await new Promise((resolve) => setImmediate(resolve));
  fake.release();
  await first;
  assert.equal(fake.calls.length, 1);

  fake.fail(new Error("offline"));
  await assert.rejects(() => reporter.report(), /offline/u);
  nowMs += 61_000; // 越过一般失败的退避期
  fake.fail(null);
  await reporter.report();
  assert.equal(fake.calls.length, 3);
});

test("失败后退避：一般失败 1 分钟内不再上报，401/403 拒绝静默 10 分钟，之后恢复", async () => {
  let nowMs = 1_000_000;
  const fake = createApi();
  const reporter = new RuntimeStatusReporter({
    api: fake.api,
    appVersion: "1.0.10",
    now: () => nowMs,
  });

  fake.fail(new Error("network"));
  await assert.rejects(() => reporter.report(), /network/u);
  assert.equal(fake.calls.length, 1);
  nowMs += 30_000;
  await reporter.report(); // 退避期内被抑制，不发请求也不抛错
  assert.equal(fake.calls.length, 1);
  nowMs += 31_000;
  fake.fail(Object.assign(new Error("forbidden"), { status: 403 }));
  await assert.rejects(() => reporter.report(), /forbidden/u);
  assert.equal(fake.calls.length, 2);

  nowMs += 5 * 60_000;
  await reporter.report();
  assert.equal(fake.calls.length, 2, "拒绝后 10 分钟内静默");
  nowMs += 5 * 60_000 + 1_000;
  fake.fail(null);
  await reporter.report();
  assert.equal(fake.calls.length, 3);
});

test("版本标签：OTA 启动附加更新 ID 前 8 位，内嵌启动只用原生版本", () => {
  assert.equal(
    formatRuntimeAppVersion("1.0.10", { updateId: "1234abcd-5678-4000-8000-000000000000", isEmbeddedLaunch: false }),
    "1.0.10+ota.1234abcd",
  );
  assert.equal(formatRuntimeAppVersion("1.0.10", { updateId: null, isEmbeddedLaunch: true }), "1.0.10");
  assert.equal(
    formatRuntimeAppVersion("1.0.10", { updateId: "1234abcd-5678-4000-8000-000000000000", isEmbeddedLaunch: true }),
    "1.0.10",
  );
});
