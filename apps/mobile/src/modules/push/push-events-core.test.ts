// 前台推送与点击推送：登录态闸门、暂存、去重、冷启动过期、前台策略。
import assert from "node:assert/strict";
import {
  PUSH_COLD_START_MAX_AGE_MS,
  PUSH_DUPLICATE_WINDOW_MS,
  PUSH_PENDING_MAX_AGE_MS,
  createPushEventController,
  resolvePushSessionPhase,
  type PushSessionPhase,
} from "./push-events-core";
import type { PushTarget } from "./push-routing";
import { PUSH_FOREGROUND_BEHAVIOR } from "./push-types";
import { makeResponse } from "./test-support";

function harness(initial: PushSessionPhase = "ready") {
  const clock = { nowMs: 1_000_000 };
  const state = { phase: initial };
  const log = { refresh: 0, navigations: [] as PushTarget[], clears: 0 };
  const controller = createPushEventController({
    getPhase: () => state.phase,
    refreshUnreadCounts: () => {
      log.refresh += 1;
    },
    navigate: (target) => {
      log.navigations.push(target);
    },
    clearLastResponse: () => {
      log.clears += 1;
    },
    now: () => clock.nowMs,
  });
  return { controller, clock, state, log };
}

const HIT_TARGET: PushTarget = { kind: "face-hub", params: { tab: "list", sub: "review" } };
const INBOX: PushTarget = { kind: "notifications" };

// ───────── 前台策略：不弹横幅、不响铃、不改角标 ─────────
assert.deepEqual(PUSH_FOREGROUND_BEHAVIOR, { shouldShowBanner: false, shouldShowList: false, shouldPlaySound: false, shouldSetBadge: false });
assert.equal(Object.isFrozen(PUSH_FOREGROUND_BEHAVIOR), true);

// ───────── 会话阶段 ─────────
assert.equal(resolvePushSessionPhase({ eligible: false, ready: true }), "signed-out", "非个人账号会话即使 ready 也是未登录阶段");
assert.equal(resolvePushSessionPhase({ eligible: false, ready: false }), "signed-out");
assert.equal(resolvePushSessionPhase({ eligible: true, ready: false }), "busy");
assert.equal(resolvePushSessionPhase({ eligible: true, ready: true }), "ready");

// ───────── 前台收到推送：刷新未读数 ─────────
{
  const h = harness("ready");
  h.controller.onForegroundNotification();
  h.controller.onForegroundNotification();
  assert.equal(h.log.refresh, 2);
  h.state.phase = "busy";
  h.controller.onForegroundNotification();
  assert.equal(h.log.refresh, 3, "已登录但主界面未就绪也刷新（只是失效缓存）");
  h.state.phase = "signed-out";
  h.controller.onForegroundNotification();
  assert.equal(h.log.refresh, 3, "未登录不刷新");
}
{
  // 刷新抛错不能影响调用方（原生回调里）
  const h = harness();
  const controller = createPushEventController({
    getPhase: () => "ready",
    refreshUnreadCounts: () => {
      throw new Error("boom");
    },
    navigate: () => undefined,
    clearLastResponse: () => undefined,
    now: () => 0,
  });
  assert.doesNotThrow(() => controller.onForegroundNotification());
  void h;
}

// ───────── 点击：会话就绪才导航 ─────────
{
  const h = harness("ready");
  h.controller.handleResponse(makeResponse({ id: "a", type: "face.unwelcome-hit" }));
  assert.deepEqual(h.log.navigations, [HIT_TARGET]);
  assert.equal(h.log.clears, 1, "处理后清掉原生侧的最近点击，避免重复处理");
  h.controller.handleResponse(makeResponse({ id: "b", type: "approval.decided" }));
  assert.deepEqual(h.log.navigations, [HIT_TARGET, INBOX]);
  h.controller.handleResponse(makeResponse({ id: "c", type: null, data: {} }));
  assert.deepEqual(h.log.navigations[2], INBOX, "缺 type → 收件箱");
}

// 非「点击通知本身」的动作（关闭等）不导航
{
  const h = harness("ready");
  h.controller.handleResponse(makeResponse({ isDefaultAction: false }));
  assert.equal(h.log.navigations.length, 0);
}

// 未登录：丢弃，不暂存，之后登录 / 就绪也不会补跳
{
  const h = harness("signed-out");
  h.controller.handleResponse(makeResponse({ id: "x" }));
  assert.equal(h.log.navigations.length, 0);
  assert.equal(h.log.clears, 1, "清掉原生侧记录，防止之后登录时被当成新点击");
  h.state.phase = "ready";
  h.controller.flushPending();
  assert.equal(h.log.navigations.length, 0, "未登录时的点击没有被暂存");
}

// 已登录但未就绪：内存暂存，就绪后补跳；只保留最近一次
{
  const h = harness("busy");
  h.controller.handleResponse(makeResponse({ id: "p1", type: "approval.submitted" }));
  h.controller.handleResponse(makeResponse({ id: "p2", type: "face.unwelcome-hit" }));
  assert.equal(h.log.navigations.length, 0, "未就绪不导航");
  h.controller.flushPending();
  assert.equal(h.log.navigations.length, 0, "仍未就绪，继续等");
  h.state.phase = "ready";
  h.controller.flushPending();
  assert.deepEqual(h.log.navigations, [HIT_TARGET], "只补最近一次");
  h.controller.flushPending();
  assert.equal(h.log.navigations.length, 1, "补跳只发生一次");
}

// 暂存超时作废
{
  const h = harness("busy");
  h.controller.handleResponse(makeResponse({ id: "p1" }));
  h.clock.nowMs += PUSH_PENDING_MAX_AGE_MS + 1;
  h.state.phase = "ready";
  h.controller.flushPending();
  assert.equal(h.log.navigations.length, 0);
}

// 暂存期间登出（reset）→ 丢弃
{
  const h = harness("busy");
  h.controller.handleResponse(makeResponse({ id: "p1" }));
  h.controller.reset();
  h.state.phase = "ready";
  h.controller.flushPending();
  assert.equal(h.log.navigations.length, 0);
}

// ───────── 去重：监听器与冷启动读取交付同一次点击 ─────────
{
  const h = harness("ready");
  h.controller.handleResponse(makeResponse({ id: "same" }));
  h.controller.handleResponse(makeResponse({ id: "same" }));
  assert.equal(h.log.navigations.length, 1, "同一通知标识短时间内只处理一次");
  h.clock.nowMs += PUSH_DUPLICATE_WINDOW_MS + 1;
  h.controller.handleResponse(makeResponse({ id: "same" }));
  assert.equal(h.log.navigations.length, 2, "超过时间窗的再次点击是新的点击");
  h.controller.handleResponse(makeResponse({ id: null }));
  h.controller.handleResponse(makeResponse({ id: null }));
  assert.equal(h.log.navigations.length, 4, "没有标识的点击无法去重，各自处理");
  h.controller.reset();
  h.controller.handleResponse(makeResponse({ id: "same" }));
  assert.equal(h.log.navigations.length, 5, "reset 清掉去重记录");
}

// ───────── 冷启动读取 ─────────
{
  const h = harness("ready");
  h.controller.handleColdStartResponse(makeResponse({ id: "cold" }), 1_000);
  assert.deepEqual(h.log.navigations, [HIT_TARGET]);
  // 与监听器重复交付
  h.controller.handleResponse(makeResponse({ id: "cold" }));
  assert.equal(h.log.navigations.length, 1);
}
{
  // 进程启动很久之后才第一次读到（例如在登录页停留很久后才登录）：视为过时，丢弃并清掉
  const h = harness("ready");
  h.controller.handleColdStartResponse(makeResponse({ id: "old" }), PUSH_COLD_START_MAX_AGE_MS + 1);
  assert.equal(h.log.navigations.length, 0);
  assert.equal(h.log.clears, 1);
}
{
  const h = harness("signed-out");
  h.controller.handleColdStartResponse(makeResponse({ id: "cold" }), 10);
  assert.equal(h.log.navigations.length, 0, "冷启动时未登录：丢弃");
}

// ───────── 导航 / 清理抛错不外溢 ─────────
{
  const controller = createPushEventController({
    getPhase: () => "ready",
    refreshUnreadCounts: () => undefined,
    navigate: () => {
      throw new Error("router not ready");
    },
    clearLastResponse: () => {
      throw new Error("native");
    },
    now: () => 0,
  });
  assert.doesNotThrow(() => controller.handleResponse(makeResponse()));
}

console.log("push-events-core.test.ts: ok");
