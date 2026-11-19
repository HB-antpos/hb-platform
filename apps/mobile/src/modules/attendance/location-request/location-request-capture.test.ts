// 响应位置请求时取位置：非交互只在权限已授予时取（绝不弹框），交互走 collectRequiredLocation，审核态不取真实位置。
import assert from "node:assert/strict";
import { test } from "node:test";
import type { CapturedLocationPayload } from "@/modules/attendance/required-location";
import {
  LOCATION_REQUEST_INTERACTIVE_CAPTURE_TIMEOUT_MS,
  LOCATION_REQUEST_SILENT_CAPTURE_TIMEOUT_MS,
  captureLocationForRequest,
  type LocationRequestCaptureBridge,
} from "./location-request-capture";

const FIX_TIME = Date.parse("2026-11-19T01:08:09.123Z");

interface BridgeOptions {
  review?: boolean;
  permission?: string;
  position?: unknown | "hang" | Error;
  collect?: CapturedLocationPayload | Error | "hang";
}

function createBridge(options: BridgeOptions = {}) {
  const calls: string[] = [];
  const bridge: LocationRequestCaptureBridge = {
    isIosReviewSessionActive: () => options.review === true,
    getForegroundPermissionsAsync: async () => {
      calls.push("getForegroundPermissions");
      return { status: options.permission ?? "granted" };
    },
    getCurrentPositionAsync: async () => {
      calls.push("getCurrentPosition");
      const position = options.position ?? { coords: { latitude: -32.9, longitude: 151.7, accuracy: 7.5 }, timestamp: FIX_TIME };
      if (position === "hang") return new Promise(() => undefined);
      if (position instanceof Error) throw position;
      return position as Awaited<ReturnType<LocationRequestCaptureBridge["getCurrentPositionAsync"]>>;
    },
    collectRequiredLocation: async () => {
      calls.push("collectRequiredLocation");
      const collected = options.collect ?? {
        locationLatitude: -32.9,
        locationLongitude: 151.7,
        locationAccuracy: 6,
        locationPermissionStatus: "granted" as const,
        locationCapturedAtUtc: "2026-11-19T01:08:10.000Z",
      };
      if (collected === "hang") return new Promise<CapturedLocationPayload>(() => undefined);
      if (collected instanceof Error) throw collected;
      return collected;
    },
  };
  return { bridge, calls };
}

test("非交互 + 权限已授予：只读查询权限后取一次点，采集时间取定位点自带的时间戳（不是现在）", async () => {
  const { bridge, calls } = createBridge();
  const outcome = await captureLocationForRequest({ interactive: false, bridge });
  assert.deepEqual(outcome, {
    status: "ok",
    location: {
      locationLatitude: -32.9,
      locationLongitude: 151.7,
      locationAccuracy: 7.5,
      locationPermissionStatus: "granted",
      locationCapturedAtUtc: "2026-11-19T01:08:09.123Z",
    },
  });
  assert.deepEqual(calls, ["getForegroundPermissions", "getCurrentPosition"], "不走 collectRequiredLocation（那里会申请权限）");
});

test("非交互 + 权限不是 granted：静默跳过，不取位置，也不走会弹框的 collectRequiredLocation", async () => {
  for (const permission of ["undetermined", "denied", "restricted", ""]) {
    const { bridge, calls } = createBridge({ permission });
    assert.deepEqual(await captureLocationForRequest({ interactive: false, bridge }), { status: "permission-not-granted" }, permission);
    assert.deepEqual(calls, ["getForegroundPermissions"], `${permission}：只查询，不取位置、不申请权限`);
  }
});

test("非交互：精度缺失 / 坐标无效 / 取点抛错 → unavailable（可稍后重试）", async () => {
  const noAccuracy = createBridge({ position: { coords: { latitude: 1, longitude: 2, accuracy: null }, timestamp: FIX_TIME } });
  const ok = await captureLocationForRequest({ interactive: false, bridge: noAccuracy.bridge });
  assert.equal(ok.status === "ok" && ok.location.locationAccuracy, undefined);

  for (const position of [
    { coords: { latitude: Number.NaN, longitude: 2 }, timestamp: FIX_TIME },
    { coords: { latitude: 1, longitude: 2 }, timestamp: Number.NaN },
    new Error("location services disabled"),
  ]) {
    const { bridge } = createBridge({ position });
    assert.deepEqual(await captureLocationForRequest({ interactive: false, bridge }), { status: "unavailable", reason: "error" });
  }
});

test("非交互：取点超时放弃（释放消费锁），迟到的结果被忽略", async () => {
  const { bridge } = createBridge({ position: "hang" });
  assert.deepEqual(await captureLocationForRequest({ interactive: false, bridge, timeoutMs: 20 }), { status: "unavailable", reason: "timeout" });
});

test("交互：走 collectRequiredLocation（含权限申请），不另外查权限 / 取点", async () => {
  const { bridge, calls } = createBridge();
  const outcome = await captureLocationForRequest({ interactive: true, bridge });
  assert.equal(outcome.status, "ok");
  assert.equal(outcome.status === "ok" && outcome.location.locationCapturedAtUtc, "2026-11-19T01:08:10.000Z", "采集时间原样来自 collectRequiredLocation 的定位点");
  assert.deepEqual(calls, ["collectRequiredLocation"]);
});

test("交互：权限被拒（LOCATION_REQUIRED）→ permission-denied；其它错误 → unavailable；超时 → unavailable", async () => {
  const denied = createBridge({ collect: Object.assign(new Error("Location permission is required"), { code: "LOCATION_REQUIRED" }) });
  assert.deepEqual(await captureLocationForRequest({ interactive: true, bridge: denied.bridge }), { status: "permission-denied" });

  const broken = createBridge({ collect: new Error("gps off") });
  assert.deepEqual(await captureLocationForRequest({ interactive: true, bridge: broken.bridge }), { status: "unavailable", reason: "error" });

  const slow = createBridge({ collect: "hang" });
  assert.deepEqual(await captureLocationForRequest({ interactive: true, bridge: slow.bridge, timeoutMs: 20 }), { status: "unavailable", reason: "timeout" });
});

test("非交互模式下 LOCATION_REQUIRED 不会被当成「用户拒绝」（只有交互模式才有申请权限这一步）", async () => {
  const { bridge } = createBridge({ position: Object.assign(new Error("x"), { code: "LOCATION_REQUIRED" }) });
  assert.deepEqual(await captureLocationForRequest({ interactive: false, bridge }), { status: "unavailable", reason: "error" });
});

test("iOS 审核态：两种模式都不读权限、不取真实位置", async () => {
  for (const interactive of [false, true]) {
    const { bridge, calls } = createBridge({ review: true });
    assert.deepEqual(await captureLocationForRequest({ interactive, bridge }), { status: "review-skipped" });
    assert.deepEqual(calls, [], `interactive=${interactive}：零调用`);
  }
});

test("默认期限：非交互 20 秒；交互要容纳员工看系统权限框的时间，90 秒", () => {
  assert.equal(LOCATION_REQUEST_SILENT_CAPTURE_TIMEOUT_MS, 20_000);
  assert.equal(LOCATION_REQUEST_INTERACTIVE_CAPTURE_TIMEOUT_MS, 90_000);
  assert.ok(LOCATION_REQUEST_INTERACTIVE_CAPTURE_TIMEOUT_MS > LOCATION_REQUEST_SILENT_CAPTURE_TIMEOUT_MS);
});
