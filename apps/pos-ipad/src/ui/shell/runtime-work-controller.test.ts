import assert from "node:assert/strict";
import test from "node:test";

import { RuntimeWorkController } from "./runtime-work-controller";

test("启动和前台同时触发时外设 drain 单飞，同步各走对应耐久入口", async () => {
  const calls: string[] = [];
  let releaseHardware: (() => void) | undefined;
  const hardwarePending = new Promise<void>((resolve) => {
    releaseHardware = resolve;
  });
  const controller = new RuntimeWorkController({
    sync: {
      async onApplicationStarted() {
        calls.push("sync-start");
      },
      async onForeground() {
        calls.push("sync-foreground");
      },
      async onNetworkChanged(isOnline) {
        calls.push(`sync-network:${isOnline}`);
      },
    },
    fulfilment: {
      async drainAutomaticQueue() {
        calls.push("hardware");
        await hardwarePending;
      },
    },
    appUpdates: {
      async refreshOnStartup() {
        calls.push("updates-start");
      },
      async refreshOnForeground() {
        calls.push("updates-foreground");
      },
      async refreshOnNetworkAvailable() {
        calls.push("updates-network");
      },
    },
  });

  const started = controller.onApplicationStarted();
  const foreground = controller.onForeground();
  await Promise.resolve();

  assert.deepEqual(calls, [
    "sync-start",
    "hardware",
    "sync-foreground",
  ]);
  releaseHardware?.();
  await Promise.all([started, foreground]);
  assert.deepEqual(calls, [
    "sync-start",
    "hardware",
    "sync-foreground",
    "updates-start",
    "updates-foreground",
  ]);
});

test("联网变化只触发同步协调器，不把打印或钱箱与网络状态错误绑定", async () => {
  const calls: string[] = [];
  const controller = new RuntimeWorkController({
    sync: {
      async onApplicationStarted() {},
      async onForeground() {},
      async onNetworkChanged(isOnline) {
        calls.push(`network:${isOnline}`);
      },
    },
    fulfilment: {
      async drainAutomaticQueue() {
        calls.push("hardware");
      },
    },
    appUpdates: {
      async refreshOnStartup() {
        calls.push("updates-start");
      },
      async refreshOnForeground() {
        calls.push("updates-foreground");
      },
      async refreshOnNetworkAvailable() {
        calls.push("updates-network");
      },
    },
  });

  await controller.onNetworkChanged(false);
  await controller.onNetworkChanged(true);

  assert.deepEqual(calls, [
    "network:false",
    "network:true",
    "updates-network",
  ]);
});

function basePort(calls: string[]) {
  return {
    sync: {
      async onApplicationStarted() {
        calls.push("sync-start");
      },
      async onForeground() {
        calls.push("sync-foreground");
      },
      async onNetworkChanged(isOnline: boolean) {
        calls.push(`sync-network:${isOnline}`);
      },
    },
    fulfilment: {
      async drainAutomaticQueue() {
        calls.push("hardware");
      },
    },
  };
}

test("总部下发小票资料同步：启动、回前台、联网恢复、定时各带对应触发源；断网不发请求", async () => {
  const calls: string[] = [];
  const triggers: string[] = [];
  const controller = new RuntimeWorkController({
    ...basePort(calls),
    receiptProfileSync: {
      async requestSync(trigger) {
        triggers.push(trigger);
      },
    },
  });

  await controller.onApplicationStarted();
  await controller.onForeground();
  await controller.onNetworkChanged(false);
  await controller.onNetworkChanged(true);
  controller.onReceiptProfileTimer();

  assert.deepEqual(triggers, ["startup", "foreground", "network", "timer"]);
});

test("下发资料同步不阻塞也不拖累订单同步、外设队列与更新检查：它挂起时其余流程照常完成", async () => {
  const calls: string[] = [];
  const controller = new RuntimeWorkController({
    ...basePort(calls),
    appUpdates: {
      async refreshOnStartup() {
        calls.push("updates-start");
      },
      async refreshOnForeground() {},
      async refreshOnNetworkAvailable() {},
    },
    // 永不返回：模拟一次卡住的 HTTP 请求
    receiptProfileSync: { requestSync: () => new Promise(() => undefined) },
  });

  await controller.onApplicationStarted();

  assert.deepEqual(calls, ["sync-start", "hardware", "updates-start"]);
});

test("下发资料同步入口同步抛错或返回 rejected Promise，都不影响启动、前台与联网流程", async () => {
  const calls: string[] = [];
  const throwing = new RuntimeWorkController({
    ...basePort(calls),
    receiptProfileSync: {
      requestSync() {
        throw new Error("sync entry crashed");
      },
    },
  });
  await throwing.onApplicationStarted();
  await throwing.onForeground();
  await throwing.onNetworkChanged(true);
  throwing.onReceiptProfileTimer();

  const rejecting = new RuntimeWorkController({
    ...basePort(calls),
    receiptProfileSync: { requestSync: async () => { throw new Error("rejected"); } },
  });
  await rejecting.onApplicationStarted();
  await rejecting.onForeground();
  await rejecting.onNetworkChanged(true);
  rejecting.onReceiptProfileTimer();
  await new Promise((resolve) => setImmediate(resolve));

  assert.deepEqual(calls.filter((call) => call.startsWith("sync-")), [
    "sync-start", "sync-foreground", "sync-network:true",
    "sync-start", "sync-foreground", "sync-network:true",
  ]);
});

test("未提供下发资料同步入口（设备未就绪或旧组合）时全部触发静默跳过", async () => {
  const calls: string[] = [];
  const controller = new RuntimeWorkController(basePort(calls));
  await controller.onApplicationStarted();
  await controller.onForeground();
  await controller.onNetworkChanged(true);
  controller.onReceiptProfileTimer();
  assert.deepEqual(calls, [
    "sync-start", "hardware", "sync-foreground", "hardware", "sync-network:true",
  ]);
});
