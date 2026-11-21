import assert from "node:assert/strict";
import test from "node:test";

import {
  __getBleScanLeaseCountForTests,
  __resetBleScanHubForTests,
  acquireBleScanLease,
  deliverBleScan,
  noteScanSource,
  setBleScanInterceptor,
  subscribeCrossSourceDuplicates,
} from "./ble-scan-hub";

let clock = 0;
function reset() {
  clock = 1_000;
  __resetBleScanHubForTests(() => clock);
}

test("没有租约时投递返回 false（调用方据此轻提示）", () => {
  reset();
  assert.equal(deliverBleScan("9313559558795"), false);
});

test("两个租约只投给最后一个；释放后回到前一个", () => {
  reset();
  const first: string[] = [];
  const second: string[] = [];
  const leaseA = acquireBleScanLease((value) => {
    first.push(value);
  });
  const leaseB = acquireBleScanLease((value) => {
    second.push(value);
  });
  assert.equal(deliverBleScan("A1"), true);
  assert.deepEqual(first, []);
  assert.deepEqual(second, ["A1"]);

  leaseB.release();
  clock += 1_000;
  assert.equal(deliverBleScan("A2"), true);
  assert.deepEqual(first, ["A2"]);
  assert.deepEqual(second, ["A1"]);

  leaseA.release();
  clock += 1_000;
  assert.equal(deliverBleScan("A3"), false);
});

test("中间的租约先释放不影响栈顶；重复 release 安全", () => {
  reset();
  const received: string[] = [];
  const bottom = acquireBleScanLease((value) => {
    received.push(`bottom:${value}`);
  });
  const middle = acquireBleScanLease((value) => {
    received.push(`middle:${value}`);
  });
  const top = acquireBleScanLease((value) => {
    received.push(`top:${value}`);
  });
  middle.release();
  middle.release();
  assert.equal(__getBleScanLeaseCountForTests(), 2);
  deliverBleScan("X");
  top.release();
  top.release();
  clock += 1_000;
  deliverBleScan("Y");
  assert.deepEqual(received, ["top:X", "bottom:Y"]);
  bottom.release();
  assert.equal(__getBleScanLeaseCountForTests(), 0);
});

test("handler 明确返回 false 时视为未接收", () => {
  reset();
  acquireBleScanLease(() => false);
  assert.equal(deliverBleScan("SHORT"), false);
});

test("测试拦截器打开时独占所有 BLE 码，关闭后恢复租约分发", () => {
  reset();
  const page: string[] = [];
  const intercepted: string[] = [];
  acquireBleScanLease((value) => {
    page.push(value);
  });
  setBleScanInterceptor((value) => intercepted.push(value));
  assert.equal(deliverBleScan("T1"), true);
  assert.deepEqual(page, []);
  assert.deepEqual(intercepted, ["T1"]);
  setBleScanInterceptor(null);
  clock += 1_000;
  deliverBleScan("T2");
  assert.deepEqual(page, ["T2"]);
  assert.deepEqual(intercepted, ["T1"]);
});

test("跨来源去重：BLE 先到，300ms 内同码 HID 被跳过；超过窗口放行", () => {
  reset();
  const page: string[] = [];
  const duplicates: string[] = [];
  subscribeCrossSourceDuplicates((duplicate) => duplicates.push(`${duplicate.droppedSource}:${duplicate.value}`));
  acquireBleScanLease((value) => {
    page.push(value);
  });
  assert.equal(deliverBleScan("9528503822012"), true);
  clock += 120;
  assert.equal(noteScanSource("9528503822012", "hid"), false);
  assert.deepEqual(duplicates, ["hid:9528503822012"]);
  clock += 400;
  assert.equal(noteScanSource("9528503822012", "hid"), true);
  assert.deepEqual(page, ["9528503822012"]);
});

test("跨来源去重：HID 先到，300ms 内同码 BLE 被吞掉且不投给页面（但算已处理）", () => {
  reset();
  const page: string[] = [];
  acquireBleScanLease((value) => {
    page.push(value);
  });
  assert.equal(noteScanSource("VD028", "hid"), true);
  clock += 50;
  assert.equal(deliverBleScan("VD028"), true);
  assert.deepEqual(page, []);
});

test("同一来源连续重复照常放行（连扫两件同款）；多个 HID hook 同码登记不互相吞", () => {
  reset();
  const page: string[] = [];
  acquireBleScanLease((value) => {
    page.push(value);
  });
  deliverBleScan("SAME");
  clock += 30;
  deliverBleScan("SAME");
  assert.deepEqual(page, ["SAME", "SAME"]);

  clock += 1_000;
  assert.equal(noteScanSource("HIDX", "hid"), true);
  assert.equal(noteScanSource("HIDX", "hid"), true);
});
