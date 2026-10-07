import assert from "node:assert/strict";
import { describeNativeAppDownloadProgress } from "./native-app-download-progress";
import {
  createDownloadProgressForwarder,
  SLOW_NETWORK_BYTES_PER_SECOND,
  SLOW_NETWORK_STALL_MS,
  type DownloadProgressTimers,
  type NativeAppDownloadProgress,
} from "./native-app-update";

function fakeTimers() {
  const pending = new Map<number, { callback: () => void; delayMs: number }>();
  let nextId = 1;
  const timers: DownloadProgressTimers = {
    setTimer: (callback, delayMs) => {
      const id = nextId++;
      pending.set(id, { callback, delayMs });
      return id as unknown as ReturnType<typeof setTimeout>;
    },
    clearTimer: (timer) => {
      pending.delete(timer as unknown as number);
    },
  };
  const fireAll = () => {
    const due = [...pending.values()];
    pending.clear();
    for (const timer of due) timer.callback();
  };
  return { timers, pending, fireAll };
}

assert.deepEqual(
  describeNativeAppDownloadProgress({ bytesWritten: 12_884_902, totalBytes: 47_815_065 }),
  { percent: 26, label: "26% · 12.3 / 45.6 MB" },
  "百分比向下取整，MB 保留一位小数且与语言无关",
);
assert.deepEqual(
  describeNativeAppDownloadProgress({ bytesWritten: 99, totalBytes: 40 }),
  { percent: 100, label: "100% · 0.0 / 0.0 MB" },
  "已写入超过总大小时夹紧到 100%",
);
assert.deepEqual(
  describeNativeAppDownloadProgress({ bytesWritten: 5, totalBytes: 0 }),
  { percent: 0, label: "0% · 0.0 / 0.0 MB" },
);

{
  const received: unknown[] = [];
  const forward = createDownloadProgressForwarder(0, (progress) => received.push(progress));
  forward(0);
  assert.deepEqual(received, [], "总大小无效时不转发");
}

assert.equal(SLOW_NETWORK_BYTES_PER_SECOND, 128 * 1024, "低于 128KB/s 提示网络差");
assert.equal(SLOW_NETWORK_STALL_MS, 5_000);

{
  // 新原生包带实测速率：跌破 128KB/s 时即使百分比没前进也立刻转发提示，回升后撤掉提示。
  const total = 100 * 1024 * 1024;
  const received: NativeAppDownloadProgress[] = [];
  const { timers } = fakeTimers();
  const forward = createDownloadProgressForwarder(total, (progress) => received.push(progress), timers);
  forward(0);
  forward(1_000, 300 * 1024);
  forward(2_000, 100 * 1024);
  forward(3_000, 90 * 1024);
  forward(4_000, 128 * 1024);
  assert.deepEqual(received, [
    { bytesWritten: 0, totalBytes: total },
    { bytesWritten: 2_000, totalBytes: total, slowNetwork: true },
    { bytesWritten: 4_000, totalBytes: total },
  ], "只在百分比前进或网络差状态变化时转发");
  forward.dispose();
}

{
  // 旧原生包、JS 兼容下载没有速率：从不提示网络差，也不启动卡住检测。
  const total = 1_000;
  const received: NativeAppDownloadProgress[] = [];
  const { timers, pending } = fakeTimers();
  const forward = createDownloadProgressForwarder(total, (progress) => received.push(progress), timers);
  forward(0);
  forward(500);
  assert.equal(pending.size, 0);
  assert.ok(received.every((progress) => !progress.slowNetwork));
}

{
  // 已开始测速后 5 秒没有任何进度（下载卡住）：提示网络差；写满后与退订后不再检测。
  const total = 100 * 1024 * 1024;
  const received: NativeAppDownloadProgress[] = [];
  const { timers, pending, fireAll } = fakeTimers();
  const forward = createDownloadProgressForwarder(total, (progress) => received.push(progress), timers);
  forward(0);
  assert.equal(pending.size, 0, "预热期（还没有速率）不检测卡住");
  forward(5_000_000, 400 * 1024);
  assert.equal(pending.size, 1);
  assert.equal([...pending.values()][0].delayMs, SLOW_NETWORK_STALL_MS);
  forward(5_100_000, 400 * 1024);
  assert.equal(pending.size, 1, "每次上报都重置卡住检测，不会叠加定时器");
  fireAll();
  assert.deepEqual(received.at(-1), { bytesWritten: 5_100_000, totalBytes: total, slowNetwork: true });
  forward(total, 400 * 1024);
  assert.equal(pending.size, 0, "写满后不再检测卡住");
  assert.deepEqual(received.at(-1), { bytesWritten: total, totalBytes: total });

  const second = createDownloadProgressForwarder(total, (progress) => received.push(progress), timers);
  second(1_000, 400 * 1024);
  assert.equal(pending.size, 1);
  second.dispose();
  assert.equal(pending.size, 0, "退订时清掉卡住检测");
  const before = received.length;
  second(2_000_000, 10);
  assert.equal(received.length, before, "退订后不再转发");
}

console.log("native-app-download-progress.test.ts: ok");
