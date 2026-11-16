import assert from "node:assert/strict";
import Module from "node:module";
import React, { act } from "react";

function mockModule(name: string, exports: object) {
  const filename = require.resolve(name);
  const module = new Module(filename);
  module.filename = filename;
  module.loaded = true;
  module.exports = exports;
  require.cache[filename] = module;
}

async function run() {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true });
  const rendererReact = require.resolve("react", { paths: [require.resolve("test-renderer")] });
  if (rendererReact !== require.resolve("react")) mockModule(rendererReact, React);
  const { createRoot } = await import("test-renderer");
  const { usePrinterStore } = await import("./state");
  const savedPrinter = { address: "label", name: "XP label" };
  usePrinterStore.setState({ savedPrinter, hydrated: true, status: "connected", autoReconnectPaused: false });
  let nativeStatus = { supported: true, enabled: true, connected: true, address: "label" };
  let connects = 0;
  let connectFails = false;
  let now = 10_000;
  let interval: (() => void) | undefined;
  let nativeListener: (() => void) | undefined;
  let appListener: ((state: string) => void) | undefined;
  const originalInterval = globalThis.setInterval;
  const originalClearInterval = globalThis.clearInterval;
  const originalNow = Date.now;
  globalThis.setInterval = ((callback: () => void) => {
    interval = callback;
    return 1 as unknown as ReturnType<typeof setInterval>;
  }) as typeof setInterval;
  globalThis.clearInterval = (() => { interval = undefined; }) as typeof clearInterval;
  Date.now = () => now;
  const appState = {
    currentState: "active",
    addEventListener: (_name: string, listener: typeof appListener) => {
      appListener = listener;
      return { remove: () => { appListener = undefined; } };
    },
  };
  mockModule("react-native", { AppState: appState });
  mockModule("../../shared/i18n/i18n", { i18n: { t: (key: string) => key } });
  mockModule("./native", {
    subscribePrinterStatusChanged: (listener: () => void) => {
      nativeListener = listener;
      return () => { nativeListener = undefined; };
    },
  });
  // 链路诊断桩：只收集 auto.reconnect 等事件，断言阶段变化与恢复原因。
  const linkEvents: { kind: string; fields: Record<string, unknown> }[] = [];
  mockModule("./link-diagnostics", {
    recordPrinterLink: (kind: string, fields?: Record<string, unknown>) => {
      linkEvents.push({ kind, fields: fields ?? {} });
    },
  });
  mockModule("./api", {
    hydrateSavedPrinter: async () => savedPrinter,
    syncPrinterStatus: async () => {
      const store = usePrinterStore.getState();
      store.setStatus(store.autoReconnectPaused ? "paused" : nativeStatus.connected ? "connected" : "disconnected");
      return { ...nativeStatus };
    },
    connectSavedPrinter: async () => {
      connects++;
      if (connectFails) throw new Error("Printer is offline");
      nativeStatus.connected = true;
      usePrinterStore.getState().setStatus("connected");
      return true;
    },
  });
  const {
    MAX_CONSECUTIVE_RECONNECT_FAILURES,
    PROBE_FAST_ATTEMPTS,
    PROBE_FAST_INTERVAL_MS,
    PROBE_SLOW_INTERVAL_MS,
    getReconnectDelayMs,
    getReconnectPhase,
    usePrinterAutoConnect,
  } = await import("./use-printer-auto-connect");

  // 间隔序列：前 10 次 5s、10s、20s、40s、之后 60s；第 10～19 次失败后每 60s（探测档前半段，约 10 分钟）；
  // 第 20 次起每 5 分钟。异常入参按 0 次处理。
  assert.equal(MAX_CONSECUTIVE_RECONNECT_FAILURES, 10);
  assert.equal(PROBE_FAST_ATTEMPTS, 10);
  assert.equal(PROBE_FAST_INTERVAL_MS, 60_000);
  assert.equal(PROBE_SLOW_INTERVAL_MS, 300_000);
  assert.deepEqual(
    [0, 1, 2, 3, 4, 5, 9, 10, 19, 20, 100].map(getReconnectDelayMs),
    [5_000, 10_000, 20_000, 40_000, 60_000, 60_000, 60_000, 60_000, 60_000, 300_000, 300_000],
  );
  assert.equal(getReconnectDelayMs(-3), 5_000);
  assert.equal(getReconnectDelayMs(Number.NaN), 5_000);
  assert.deepEqual(
    [0, 1, 9, 10, 19, 20, 500].map(getReconnectPhase),
    ["idle", "backoff", "backoff", "probe_fast", "probe_fast", "probe_slow", "probe_slow"],
  );
  function Harness() {
    usePrinterAutoConnect();
    return null;
  }
  const root = createRoot();
  try {
    await act(async () => { root.render(React.createElement(Harness)); });
    assert.ok(nativeListener, "挂载后必须订阅原生连接状态事件");
    assert.equal(connects, 0, "已连接时不能重新建立 socket");

    await act(async () => {
      nativeStatus.connected = false;
      nativeListener?.();
    });
    assert.equal(connects, 1, "断线事件应立即发起重连，无需等待五秒轮询");
    assert.equal(usePrinterStore.getState().status, "connected");

    await act(async () => { usePrinterStore.getState().setAutoReconnectPaused(true); });
    await act(async () => { nativeStatus.connected = false; nativeListener?.(); });
    assert.equal(connects, 1, "暂停时只能同步状态，不能自动连接");
    assert.equal(usePrinterStore.getState().status, "paused");

    await act(async () => {
      nativeStatus.enabled = false;
      usePrinterStore.getState().setAutoReconnectPaused(false);
      nativeListener?.();
    });
    assert.equal(connects, 1, "蓝牙关闭时不能反复请求连接");

    await act(async () => {
      appState.currentState = "background";
      appListener?.("background");
      nativeStatus.enabled = true;
      nativeListener?.();
      interval?.();
    });
    assert.equal(connects, 1, "后台不主动重连");
    now += 5_000;
    await act(async () => {
      appState.currentState = "active";
      appListener?.("active");
    });
    assert.equal(connects, 2, "回到前台立即检查连接");

    // 旧 APK 没有状态事件，轮询仍需发现断线；连接失败不能因状态更新形成热循环。
    now += 5_000;
    await act(async () => {
      nativeStatus.connected = false;
      connectFails = true;
      interval?.();
    });
    assert.equal(connects, 3);
    await act(async () => { nativeListener?.(); nativeListener?.(); interval?.(); });
    assert.equal(connects, 3, "同一重试间隔内的重复事件不能形成连接风暴");
    // 失败 1 次后退避到 10 秒：5 秒时还不能再连，满 10 秒才重试。
    now += 5_000;
    await act(async () => { connectFails = false; interval?.(); });
    assert.equal(connects, 3, "失败后必须退避，不能 5 秒就重连");
    now += 5_000;
    await act(async () => { interval?.(); });
    assert.equal(connects, 4);
    assert.equal(usePrinterStore.getState().status, "connected");

    // 打印机持续连不上：不再「10 次后放弃」，而是继续探测——前 10 次退避，
    // 第 10～19 次失败后每分钟一次（约 10 分钟），之后每 5 分钟一次。
    await act(async () => {
      nativeStatus.connected = false;
      connectFails = true;
    });
    const beforeFailures = connects;
    const probeEventsFrom = linkEvents.length;
    const failureTotal = MAX_CONSECUTIVE_RECONNECT_FAILURES + PROBE_FAST_ATTEMPTS;
    for (let i = 1; i <= failureTotal; i += 1) {
      now += 61_000; // 比任何一档的 60 秒都长，每次轮询都应当尝试
      await act(async () => { interval?.(); });
      const info = usePrinterStore.getState().reconnectInfo;
      assert.equal(info.failures, i, `第 ${i} 次失败后界面能看到失败次数`);
      assert.equal(info.phase, getReconnectPhase(i));
    }
    assert.equal(connects - beforeFailures, failureTotal, "连续失败 10 次后仍继续自动探测，没有放弃");
    // 进入探测档与进入慢探测档各记一条（原来「10 次后停止」的那一刻），后续失败不重复记。
    assert.deepEqual(
      linkEvents.slice(probeEventsFrom).filter((event) => event.kind === "auto.reconnect").map((event) => event.fields),
      [
        { phase: "probe_fast", failures: 10, nextInMs: 60_000 },
        { phase: "probe_slow", failures: 20, nextInMs: 300_000 },
      ],
    );
    assert.notEqual(usePrinterStore.getState().status, "connected");

    // 慢探测档：每 5 分钟一次；5 分钟内的轮询与原生事件都不能连。
    const slowBase = connects;
    now += 61_000;
    await act(async () => { nativeListener?.(); interval?.(); });
    assert.equal(connects, slowBase, "慢探测档 61 秒内不能再连");
    now += 239_000; // 距上次尝试共 300 秒
    await act(async () => { interval?.(); });
    assert.equal(connects, slowBase + 1, "满 5 分钟再探测一次");

    // 蓝牙关闭后重新打开：重新开始自动重连（失败计数清零，并记录恢复原因）。
    const afterGiveUp = connects;
    await act(async () => { nativeStatus.enabled = false; interval?.(); });
    assert.equal(connects, afterGiveUp, "蓝牙关闭时不连接");
    now += 5_000;
    await act(async () => { nativeStatus.enabled = true; interval?.(); });
    assert.equal(connects, afterGiveUp + 1, "蓝牙重新打开应恢复自动重连");
    assert.deepEqual(
      linkEvents.filter((event) => event.fields.resumeReason === "bluetooth_on").map((event) => event.fields),
      [{ phase: "resumed", failures: failureTotal + 1, resumeReason: "bluetooth_on" }],
    );
    assert.equal(usePrinterStore.getState().reconnectInfo.failures, 1, "重开蓝牙后从第 1 次重新计数");

    // 失败 1 次后间隔已是 10 秒；回到前台视为新一轮，5 秒后即可重试。
    now += 5_000;
    await act(async () => { interval?.(); });
    assert.equal(connects, afterGiveUp + 1, "未回前台时仍按退避等待");
    await act(async () => {
      appState.currentState = "background";
      appListener?.("background");
      appState.currentState = "active";
      appListener?.("active");
    });
    assert.equal(connects, afterGiveUp + 2, "回到前台应清零失败计数并立即重连");
    assert.ok(linkEvents.some((event) => event.fields.resumeReason === "foreground"), "回前台恢复记录原因 foreground");

    // 用户点「重新连接」（成败都一样）后 bump 信号：失败计数清零、重新开始一轮，且不紧接着再连一次。
    const beforeRetry = connects;
    await act(async () => { usePrinterStore.getState().bumpRetryNonce(); });
    assert.equal(connects, beforeRetry, "手动重连刚结束，自动重连不能马上再连一次");
    assert.deepEqual(usePrinterStore.getState().reconnectInfo, { failures: 0, phase: "idle" });
    assert.ok(linkEvents.some((event) => event.fields.resumeReason === "retry_nonce"), "恢复记录原因 retry_nonce");
    now += 5_000;
    await act(async () => { interval?.(); });
    assert.equal(connects, beforeRetry + 1, "满一个基础间隔后自动重连恢复，且从第 1 次重新计数");
    assert.equal(usePrinterStore.getState().reconnectInfo.failures, 1);
    connectFails = false;
    now += 10_000;
    await act(async () => { interval?.(); });
    assert.equal(usePrinterStore.getState().status, "connected", "连上后恢复正常");
    assert.deepEqual(usePrinterStore.getState().reconnectInfo, { failures: 0, phase: "idle" }, "连上后失败计数与阶段清零");
  } finally {
    await act(async () => { root.unmount(); });
    globalThis.setInterval = originalInterval;
    globalThis.clearInterval = originalClearInterval;
    Date.now = originalNow;
  }
  assert.equal(nativeListener, undefined, "卸载应取消原生事件监听");
  assert.equal(appListener, undefined, "卸载应取消前后台监听");
  assert.equal(interval, undefined, "卸载应取消轮询");
  console.log("use-printer-auto-connect.test.ts: ok");
}

void run();
