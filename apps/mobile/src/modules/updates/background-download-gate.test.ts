import assert from "node:assert/strict";
import { test } from "node:test";
import {
  BACKGROUND_DOWNLOAD_BANDWIDTH_SHARE,
  BACKGROUND_DOWNLOAD_IDLE_MS,
  createBackgroundDownloadGate,
} from "./background-download-gate";

const IDLE = BACKGROUND_DOWNLOAD_IDLE_MS;

function setup(idleMs = IDLE) {
  let now = 0;
  const timers = new Map<number, { callback: () => void; at: number }>();
  let nextId = 1;
  const gate = createBackgroundDownloadGate({
    idleMs,
    now: () => now,
    setTimer: (callback, delayMs) => {
      const id = nextId++;
      timers.set(id, { callback, at: now + delayMs });
      return id as unknown as ReturnType<typeof setTimeout>;
    },
    clearTimer: (timer) => {
      timers.delete(timer as unknown as number);
    },
  });
  const notified: number[] = [];
  gate.subscribe(() => notified.push(now));
  const advance = (ms: number) => {
    now += ms;
    for (const [id, timer] of [...timers]) {
      if (timer.at <= now) {
        timers.delete(id);
        timer.callback();
      }
    }
  };
  return { gate, advance, notified, timers };
}

test("默认值：30 秒空闲、后台下载最多占 80% 带宽", () => {
  assert.equal(BACKGROUND_DOWNLOAD_IDLE_MS, 30_000);
  assert.equal(BACKGROUND_DOWNLOAD_BANDWIDTH_SHARE, 0.8);
});

test("未接上登录状态时视为打开，保持原有立即下载行为", () => {
  const { gate } = setup();
  assert.equal(gate.isOpen(), true);
});

test("未登录时关闭；登录满空闲时长才打开，并在打开时通知一次", () => {
  const { gate, advance, notified } = setup();
  gate.bind(false);
  assert.equal(gate.isOpen(), false, "登录页上不下载");

  gate.setAuthenticated(true);
  assert.equal(gate.isOpen(), false, "刚登录时不和登录后的首屏请求抢带宽");
  advance(IDLE - 1);
  assert.equal(gate.isOpen(), false);
  assert.deepEqual(notified, []);

  advance(1);
  assert.equal(gate.isOpen(), true);
  assert.deepEqual(notified, [IDLE], "打开时通知被挡下的下载重试");
});

test("会话恢复（启动时已登录）同样从接上那一刻开始计时", () => {
  const { gate, advance } = setup();
  gate.bind(true);
  assert.equal(gate.isOpen(), false);
  advance(IDLE);
  assert.equal(gate.isOpen(), true);
});

test("重复上报已登录不重新计时；登出立即关闭并取消待定通知", () => {
  const { gate, advance, notified, timers } = setup();
  gate.bind(true);
  advance(IDLE / 2);
  gate.setAuthenticated(true);
  advance(IDLE / 2);
  assert.equal(gate.isOpen(), true, "刷新令牌等重复上报不能把计时清零");
  assert.equal(notified.length, 1);

  gate.setAuthenticated(false);
  assert.equal(gate.isOpen(), false);
  gate.setAuthenticated(true);
  gate.setAuthenticated(false);
  assert.equal(timers.size, 0, "登出要取消还没到点的通知");
  advance(IDLE * 2);
  assert.equal(notified.length, 1, "登出后不应再通知");
});

test("解绑后回到打开并通知；订阅者异常不影响其它订阅者", () => {
  const { gate, notified } = setup();
  let healthyCalls = 0;
  gate.subscribe(() => {
    throw new Error("boom");
  });
  gate.subscribe(() => {
    healthyCalls += 1;
  });
  gate.bind(false);
  assert.equal(gate.isOpen(), false);
  gate.unbind();
  assert.equal(gate.isOpen(), true);
  assert.equal(notified.length, 1);
  assert.equal(healthyCalls, 1);
});
