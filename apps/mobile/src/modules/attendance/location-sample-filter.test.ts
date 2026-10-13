import assert from "node:assert/strict";
import { test } from "node:test";
import {
  LOCATION_HEARTBEAT_INTERVAL_MS,
  LOCATION_KEEP_DISTANCE_METERS,
  LOCATION_KEEP_INTERVAL_MS,
  distanceMeters,
  filterShiftLocationFixes,
  type LastKeptLocationPoint,
  type RawLocationFix,
} from "./location-sample-filter";
import type { AttendanceLocationTrackingWindow } from "./location-tracking-window";

const window: AttendanceLocationTrackingWindow = {
  startedAtUtc: "2026-10-13T01:00:00Z",
  workDate: "2026-10-13",
  storeTimeZone: "UTC",
};
const NOW = "2026-10-13T05:00:00Z";
const T0 = Date.parse("2026-10-13T02:00:00Z");
// 纬度 0.001 度约 111.2 米；0.0003 度约 33 米；0.0005 度约 55.6 米。
const LAT0 = -33.8688;
const LNG0 = 151.2093;

function fix(offsetSeconds: number, extra: Partial<RawLocationFix> = {}): RawLocationFix {
  return {
    timestamp: T0 + offsetSeconds * 1000,
    latitude: LAT0,
    longitude: LNG0,
    accuracy: 10,
    ...extra,
  };
}

function run(fixes: RawLocationFix[], lastKept?: LastKeptLocationPoint, nowUtc = NOW) {
  return filterShiftLocationFixes({ window, fixes, lastKept, nowUtc });
}

test("阈值常量与需求一致", () => {
  assert.equal(LOCATION_KEEP_DISTANCE_METERS, 50);
  assert.equal(LOCATION_KEEP_INTERVAL_MS, 60_000);
  assert.equal(LOCATION_HEARTBEAT_INTERVAL_MS, 5 * 60_000);
});

test("distanceMeters 对已知经纬度差给出合理米数", () => {
  assert.equal(distanceMeters({ latitude: 0, longitude: 0 }, { latitude: 0, longitude: 0 }), 0);
  const d = distanceMeters({ latitude: LAT0, longitude: LNG0 }, { latitude: LAT0 + 0.001, longitude: LNG0 });
  assert.ok(d > 110 && d < 112.5, `0.001 度纬度应约 111 米，实际 ${d}`);
});

test("没有上一个保留点时第一个点必定保留，且为 ShiftSample", () => {
  const result = run([fix(0)]);
  assert.equal(result.samples.length, 1);
  assert.equal(result.samples[0].eventType, "ShiftSample");
  assert.equal(result.samples[0].capturedAtUtc, "2026-10-13T02:00:00.000Z");
  assert.deepEqual(result.lastKept, {
    capturedAtUtc: "2026-10-13T02:00:00.000Z",
    latitude: LAT0,
    longitude: LNG0,
  });
});

test("相隔不足 60 秒且位移不足 50 米的点被丢弃", () => {
  const result = run([fix(0), fix(30), fix(59, { latitude: LAT0 + 0.0003 })]);
  assert.deepEqual(result.samples.map((sample) => sample.capturedAtUtc), ["2026-10-13T02:00:00.000Z"]);
});

test("相隔恰好 60 秒保留（满足其一即保留）", () => {
  const result = run([fix(0), fix(60)]);
  assert.equal(result.samples.length, 2);
  assert.equal(result.samples[1].eventType, "ShiftSample");
});

test("位移 ≥ 50 米即使不足 60 秒也保留", () => {
  const result = run([fix(0), fix(10, { latitude: LAT0 + 0.0005 })]);
  assert.equal(result.samples.length, 2);
  assert.equal(result.samples[1].eventType, "ShiftSample");
});

test("相隔 ≥ 5 分钟即使位移为 0 也保留，并标记 ShiftHeartbeat", () => {
  const result = run([fix(0), fix(300)]);
  assert.equal(result.samples.length, 2);
  assert.equal(result.samples[1].eventType, "ShiftHeartbeat");
});

test("相隔 4 分 59 秒（不足 5 分钟）的点是 ShiftSample 而不是心跳", () => {
  const result = run([fix(0), fix(299)]);
  assert.equal(result.samples[1].eventType, "ShiftSample");
});

test("心跳按“距上一个保留点”计算，保留点推进后重新计时", () => {
  const result = run([fix(0), fix(300), fix(360), fix(660)]);
  assert.deepEqual(
    result.samples.map((sample) => sample.eventType),
    ["ShiftSample", "ShiftHeartbeat", "ShiftSample", "ShiftHeartbeat"],
  );
});

test("一批里的点乱序时按时间升序依次处理", () => {
  const result = run([fix(120), fix(0), fix(60)]);
  assert.deepEqual(
    result.samples.map((sample) => sample.capturedAtUtc),
    ["2026-10-13T02:00:00.000Z", "2026-10-13T02:01:00.000Z", "2026-10-13T02:02:00.000Z"],
  );
});

test("上一个保留点跨批延续：不足阈值的点被丢弃，lastKept 不回退", () => {
  const lastKept: LastKeptLocationPoint = {
    capturedAtUtc: "2026-10-13T02:00:00.000Z",
    latitude: LAT0,
    longitude: LNG0,
  };
  const result = run([fix(30)], lastKept);
  assert.equal(result.samples.length, 0);
  assert.deepEqual(result.lastKept, lastKept);
});

test("早于或等于上一个保留点的点（乱序/重复回调）被丢弃", () => {
  const lastKept: LastKeptLocationPoint = {
    capturedAtUtc: "2026-10-13T02:10:00.000Z",
    latitude: LAT0,
    longitude: LNG0,
  };
  const result = run([fix(0), fix(600), fix(660)], lastKept);
  assert.deepEqual(result.samples.map((sample) => sample.capturedAtUtc), ["2026-10-13T02:11:00.000Z"]);
});

test("窗口外的点被排除：上班前、未来、门店本地跨天、满 10 小时", () => {
  const before = run([fix(-3600 - 1)]); // 01:00 之前
  assert.equal(before.samples.length, 0);

  const future = run([fix(0)], undefined, "2026-10-13T01:59:59Z");
  assert.equal(future.samples.length, 0, "晚于当前时间的点不应保留");

  const sydneyWindow: AttendanceLocationTrackingWindow = {
    startedAtUtc: "2026-10-13T12:00:00Z", // 悉尼 23:00
    workDate: "2026-10-13",
    storeTimeZone: "Australia/Sydney",
  };
  const crossDay = filterShiftLocationFixes({
    window: sydneyWindow,
    fixes: [
      { timestamp: Date.parse("2026-10-13T12:30:00Z"), latitude: LAT0, longitude: LNG0 },
      { timestamp: Date.parse("2026-10-13T13:30:00Z"), latitude: LAT0, longitude: LNG0 }, // 悉尼 00:30 次日
    ],
    nowUtc: "2026-10-13T14:00:00Z",
  });
  assert.deepEqual(crossDay.samples.map((sample) => sample.capturedAtUtc), ["2026-10-13T12:30:00.000Z"]);

  const tenHours = filterShiftLocationFixes({
    window,
    fixes: [
      { timestamp: Date.parse("2026-10-13T10:59:00Z"), latitude: LAT0, longitude: LNG0 },
      { timestamp: Date.parse("2026-10-13T11:00:00Z"), latitude: LAT0, longitude: LNG0 }, // 恰满 10 小时
    ],
    nowUtc: "2026-10-13T11:00:00Z",
  });
  assert.deepEqual(tenHours.samples.map((sample) => sample.capturedAtUtc), ["2026-10-13T10:59:00.000Z"]);
});

test("窗口无效（工作日与上班日期不符）时所有点都被排除", () => {
  const result = filterShiftLocationFixes({
    window: { ...window, workDate: "2026-10-12" },
    fixes: [fix(0)],
    nowUtc: NOW,
  });
  assert.equal(result.samples.length, 0);
});

test("非法时间戳与非法坐标被丢弃，不影响其他点", () => {
  const result = run([
    fix(0, { timestamp: Number.NaN }),
    fix(0, { latitude: 91 }),
    fix(0, { longitude: -181 }),
    fix(0, { latitude: Number.NaN }),
    fix(0),
  ]);
  assert.equal(result.samples.length, 1);
});

test("精度与 Android mocked 标记：有则带上，没有则不带", () => {
  const result = run([
    fix(0, { accuracy: 12.5, mocked: true }),
    fix(60, { accuracy: null, mocked: undefined }),
    fix(120, { accuracy: undefined, mocked: false }),
  ]);
  assert.equal(result.samples[0].accuracy, 12.5);
  assert.equal(result.samples[0].isMocked, true);
  assert.equal("accuracy" in result.samples[1], false);
  assert.equal("isMocked" in result.samples[1], false);
  assert.equal(result.samples[2].isMocked, false);
});

test("损坏的上一个保留点按没有基准处理，下一个点仍会保留", () => {
  const result = run([fix(0)], { capturedAtUtc: "not-a-date", latitude: LAT0, longitude: LNG0 });
  assert.equal(result.samples.length, 1);
});
