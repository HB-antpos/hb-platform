import assert from "node:assert/strict";
import { test } from "node:test";
import {
  LOCATION_SAMPLE_BATCH_MAX,
  LocationSampleBatchResponseError,
  buildLocationSampleBatchRequest,
  collectAcknowledgedSampleIds,
  normalizeLocationSampleBatchResult,
  type QueuedLocationSample,
} from "./location-sample-batch";

function sample(id: string, capturedAtUtc: string, extra: Partial<QueuedLocationSample> = {}): QueuedLocationSample {
  return {
    clientSampleId: id,
    ownerUserId: "user-a",
    storeCode: "1042",
    hardwareId: "hw-1",
    systemDeviceNumber: "SYS-1",
    deviceSystem: "android",
    eventType: "ShiftSample",
    locationLatitude: -33.8688,
    locationLongitude: 151.2093,
    locationCapturedAtUtc: capturedAtUtc,
    ...extra,
  };
}

test("请求体：顶层门店/设备信息 + 契约样本字段，按采集时间升序，权限固定 granted", () => {
  const request = buildLocationSampleBatchRequest([
    sample("b", "2026-10-13T02:01:00.000Z", { locationAccuracy: 8, isMocked: false, eventType: "ShiftHeartbeat" }),
    sample("a", "2026-10-13T02:00:00.000Z"),
  ]);
  assert.deepEqual(request, {
    storeCode: "1042",
    hardwareId: "hw-1",
    systemDeviceNumber: "SYS-1",
    deviceSystem: "android",
    samples: [
      {
        clientSampleId: "a",
        eventType: "ShiftSample",
        locationLatitude: -33.8688,
        locationLongitude: 151.2093,
        locationPermissionStatus: "granted",
        locationCapturedAtUtc: "2026-10-13T02:00:00.000Z",
      },
      {
        clientSampleId: "b",
        eventType: "ShiftHeartbeat",
        locationLatitude: -33.8688,
        locationLongitude: 151.2093,
        locationAccuracy: 8,
        isMocked: false,
        locationPermissionStatus: "granted",
        locationCapturedAtUtc: "2026-10-13T02:01:00.000Z",
      },
    ],
  });
});

test("请求体：空白的设备字段不下发", () => {
  const request = buildLocationSampleBatchRequest([
    sample("a", "2026-10-13T02:00:00.000Z", { hardwareId: "  ", systemDeviceNumber: undefined, deviceSystem: "" }),
  ]);
  assert.equal("hardwareId" in request, false);
  assert.equal("systemDeviceNumber" in request, false);
  assert.equal("deviceSystem" in request, false);
});

test("请求体：样本数必须在 1~200，混入不同门店/设备分组会报错", () => {
  assert.equal(LOCATION_SAMPLE_BATCH_MAX, 200);
  assert.throws(() => buildLocationSampleBatchRequest([]), RangeError);
  const many = Array.from({ length: 201 }, (_, index) => sample(`id-${index}`, "2026-10-13T02:00:00.000Z"));
  assert.throws(() => buildLocationSampleBatchRequest(many), RangeError);
  assert.equal(buildLocationSampleBatchRequest(many.slice(0, 200)).samples.length, 200);
  assert.throws(
    () => buildLocationSampleBatchRequest([
      sample("a", "2026-10-13T02:00:00.000Z"),
      sample("b", "2026-10-13T02:01:00.000Z", { storeCode: "2000" }),
    ]),
    /不能混入/,
  );
});

test("响应：合法业务体", () => {
  const result = normalizeLocationSampleBatchResult({
    accepted: ["a"],
    duplicates: ["b"],
    rejected: [{ clientSampleId: "c", errorCode: "SAMPLE_TOO_OLD", message: "too old" }],
  });
  assert.deepEqual(result, {
    accepted: ["a"],
    duplicates: ["b"],
    rejected: [{ clientSampleId: "c", errorCode: "SAMPLE_TOO_OLD", message: "too old" }],
  });
  assert.deepEqual(collectAcknowledgedSampleIds(result), ["a", "b", "c"]);
});

test("响应：外层 ApiResponse 信封会被解开；PascalCase 与缺省的空数组被容忍", () => {
  const wrapped = normalizeLocationSampleBatchResult({
    success: true,
    data: { Accepted: ["a"], Duplicates: [], Rejected: [{ ClientSampleId: "c", ErrorCode: "X", Message: "m" }] },
  });
  assert.deepEqual(wrapped.accepted, ["a"]);
  assert.deepEqual(wrapped.rejected, [{ clientSampleId: "c", errorCode: "X", message: "m" }]);

  const partial = normalizeLocationSampleBatchResult({ accepted: ["a"] });
  assert.deepEqual(partial, { accepted: ["a"], duplicates: [], rejected: [] });
});

test("响应格式异常一律抛 LocationSampleBatchResponseError（调用方据此保留整批）", () => {
  const malformed: unknown[] = [
    null,
    undefined,
    "ok",
    [],
    {},
    { success: true },
    { accepted: "a", duplicates: [], rejected: [] },
    { accepted: [1], duplicates: [], rejected: [] },
    { accepted: [], duplicates: {}, rejected: [] },
    { accepted: [], duplicates: [], rejected: "x" },
    { accepted: [], duplicates: [], rejected: [null] },
    { accepted: [], duplicates: [], rejected: [{ errorCode: "X" }] },
  ];
  for (const value of malformed) {
    assert.throws(
      () => normalizeLocationSampleBatchResult(value),
      LocationSampleBatchResponseError,
      `应当判为格式异常: ${JSON.stringify(value)}`,
    );
  }
});

test("响应：success:false 的业务失败信封同样抛错（整单失败保留重试）", () => {
  assert.throws(() => normalizeLocationSampleBatchResult({ success: false, message: "boom" }));
});
