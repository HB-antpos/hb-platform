import assert from "node:assert/strict";
import test from "node:test";

import {
  getAttendanceQrRefreshIntervalMs,
  getBackgroundPollingIntervalMs,
  isBackgroundPollDue,
  isQuietHours,
  QUIET_POLLING_INTERVAL_MS,
} from "./background-polling";

/** 构造本地时间（避免依赖运行机时区）。 */
const local = (hour: number, minute = 0) => new Date(2026, 9, 27, hour, minute, 0);

test("夜间静默窗口：22:00（含）至 06:00（不含）跨午夜", () => {
  assert.equal(isQuietHours(local(21, 59)), false);
  assert.equal(isQuietHours(local(22, 0)), true);
  assert.equal(isQuietHours(local(23, 59)), true);
  assert.equal(isQuietHours(local(0, 0)), true);
  assert.equal(isQuietHours(local(5, 59)), true);
  assert.equal(isQuietHours(local(6, 0)), false);
  assert.equal(isQuietHours(local(12, 0)), false);
});

test("夜间健康时放慢到 5 分钟，白天或异常时按原间隔，且绝不比原间隔更短", () => {
  assert.equal(getBackgroundPollingIntervalMs(local(23), 30_000), QUIET_POLLING_INTERVAL_MS);
  assert.equal(getBackgroundPollingIntervalMs(local(12), 30_000), 30_000);
  // 异常（离线/探测失败）夜里也按原间隔重试，恢复后尽快发现。
  assert.equal(getBackgroundPollingIntervalMs(local(23), 30_000, false), 30_000);
  // 原间隔本来就比夜间间隔长时不缩短。
  assert.equal(getBackgroundPollingIntervalMs(local(23), 10 * 60_000), 10 * 60_000);
});

test("考勤二维码：健康 5 分钟，异常 15 秒重试", () => {
  assert.equal(getAttendanceQrRefreshIntervalMs(true), 300_000);
  assert.equal(getAttendanceQrRefreshIntervalMs(false), 15_000);
});

test("基础定时器到期判定：首次/时钟回拨立即到期，未满间隔不到期，容差内视为到期", () => {
  assert.equal(isBackgroundPollDue(1_000_000, null, 300_000), true);
  assert.equal(isBackgroundPollDue(1_000_000, 2_000_000, 300_000), true);
  assert.equal(isBackgroundPollDue(1_000_000, 900_000, 300_000), false);
  assert.equal(isBackgroundPollDue(1_300_000, 1_000_000, 300_000), true);
  assert.equal(isBackgroundPollDue(1_299_500, 1_000_000, 300_000), true);
  assert.equal(isBackgroundPollDue(1_298_000, 1_000_000, 300_000), false);
});
