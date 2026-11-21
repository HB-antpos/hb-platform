import assert from "node:assert/strict";
import test, { afterEach } from "node:test";

import {
  beginOpenItemTiming,
  finishOpenItemTiming,
  markOpenItemTiming,
  setOpenItemTimingSink,
  type OpenItemTimingReport,
} from "./open-item-timing";

/** 可手动推进的假时钟：每次 tick 返回当前值，测试里按需 set。 */
function clock(start = 1_000) {
  let value = start;
  return {
    now: () => value,
    set: (next: number) => {
      value = next;
    },
  };
}

let release: (() => void) | null = null;
afterEach(() => {
  release?.();
  release = null;
  // 清掉可能残留的在途计时，避免用例互相污染。
  finishOpenItemTiming("failure");
});

function collect(): OpenItemTimingReport[] {
  const reports: OpenItemTimingReport[] = [];
  release = setOpenItemTimingSink((report) => reports.push(report));
  return reports;
}

test("各阶段差值分别归入授权 / 目录 / 购物车 / 审计 / presenter，并给出总耗时", () => {
  const reports = collect();
  const time = clock(1_000);

  beginOpenItemTiming(time.now);
  time.set(1_120);
  markOpenItemTiming("authorized", time.now);
  time.set(1_150);
  markOpenItemTiming("catalog", time.now);
  time.set(1_155);
  markOpenItemTiming("cart", time.now);
  time.set(1_455);
  markOpenItemTiming("audited", time.now);
  time.set(1_480);
  finishOpenItemTiming("success", null, time.now);

  assert.deepEqual(reports, [
    {
      outcome: "success",
      errorCode: null,
      authorizeMs: 120,
      catalogMs: 30,
      cartMs: 5,
      auditMs: 300,
      presenterMs: 25,
      totalMs: 480,
    },
  ]);
});

test("没跑到的阶段记 null，不把「没执行」误报成 0ms；失败带固定错误码", () => {
  const reports = collect();
  const time = clock(0);

  beginOpenItemTiming(time.now);
  time.set(80);
  markOpenItemTiming("authorized", time.now);
  // 目录查询失败：后面的 catalog / cart / audited 都没有标记。
  time.set(95);
  finishOpenItemTiming("failure", "product-add-failed", time.now);

  assert.deepEqual(reports, [
    {
      outcome: "failure",
      errorCode: "product-add-failed",
      authorizeMs: 80,
      catalogMs: null,
      cartMs: null,
      auditMs: null,
      presenterMs: null,
      totalMs: 95,
    },
  ]);
});

test("没有 begin 时 mark / finish 都是空操作；同一次计时只上报一次", () => {
  const reports = collect();
  const time = clock(0);

  markOpenItemTiming("authorized", time.now);
  finishOpenItemTiming("success", null, time.now);
  assert.equal(reports.length, 0);

  beginOpenItemTiming(time.now);
  finishOpenItemTiming("success", null, time.now);
  finishOpenItemTiming("success", null, time.now);
  assert.equal(reports.length, 1);
});

test("错误码只放行固定格式，含空格或业务文本的内容一律丢弃", () => {
  const reports = collect();
  const time = clock(0);

  beginOpenItemTiming(time.now);
  finishOpenItemTiming("failure", "Price 12.50 rejected for John", time.now);
  assert.equal(reports[0]?.errorCode, null);

  beginOpenItemTiming(time.now);
  finishOpenItemTiming("failure", "NEW_TRANSACTIONS_DISABLED", time.now);
  assert.equal(reports[1]?.errorCode, "NEW_TRANSACTIONS_DISABLED");
});

test("sink 抛错不外溢，诊断旁路不能影响加购；注销只在 sink 仍是自己时生效", () => {
  const reports: OpenItemTimingReport[] = [];
  const unsubscribeThrowing = setOpenItemTimingSink(() => {
    throw new Error("sink failed");
  });
  const time = clock(0);

  beginOpenItemTiming(time.now);
  assert.doesNotThrow(() => finishOpenItemTiming("success", null, time.now));

  // 新 sink 顶替旧 sink 后，旧的注销函数不能把新 sink 清掉。
  release = setOpenItemTimingSink((report) => reports.push(report));
  unsubscribeThrowing();
  beginOpenItemTiming(time.now);
  finishOpenItemTiming("success", null, time.now);
  assert.equal(reports.length, 1);
});
