import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import {
  applyAttendanceTrackingLifecycle,
  buildAttendanceQrPunchPayload,
  canOpenAttendanceQrScanner,
  canSubmitAttendanceQrPunch,
  getAttendancePunchErrorKey,
  getAttendancePunchErrorCode,
  getAttendanceTrackingAction,
  normalizeAttendanceQrTokenInput,
  normalizeAttendanceQrResolveResult,
  normalizeAttendancePunchMutationResult,
  prepareAttendanceQrPunch,
  requiresBackgroundPermissionBeforePunch,
  resolveAttendanceQrStore,
  shouldEnableAttendanceQrScanning,
  validateAttendanceQrToken,
} from "./attendance-qr";

const nonce = Buffer.alloc(12, 1).toString("base64url");
const cipher = Buffer.alloc(29, 2).toString("base64url");
const tag = Buffer.alloc(16, 3).toString("base64url");
const token = `HBATE1.kid_1.${nonce}.${cipher}.${tag}`;

assert.equal(validateAttendanceQrToken(token), token, "opaque 五段 token 应通过格式预检");
assert.equal(
  normalizeAttendanceQrTokenInput(` \uFEFF${token}\u200B\n`),
  token,
  "扫描输入前后空白、BOM 和零宽字符必须被清理",
);
assert.equal(
  normalizeAttendanceQrTokenInput(`pos-attendance:${token};expires=15s`),
  token,
  "包装字符串里可识别的 HBATE1 五段 token 必须被提取",
);
for (const invalid of [
  "HBATE1.bad",
  `HBATQ1.kid_1.${nonce}.${cipher}.${tag}`,
  `HBATE1.bad!.${nonce}.${cipher}.${tag}`,
  `HBATE1.kid_1.${Buffer.alloc(11).toString("base64url")}.${cipher}.${tag}`,
  `HBATE1.kid_1.${nonce}..${tag}`,
  `HBATE1.kid_1.${nonce}.${cipher}.${Buffer.alloc(15).toString("base64url")}`,
  `HBATE1.kid_1.${nonce}.${"a".repeat(500)}.${tag}`,
]) {
  assert.throws(
    () => validateAttendanceQrToken(normalizeAttendanceQrTokenInput(invalid)),
    /ATTENDANCE_QR_FORMAT_INVALID/,
    `非法 token 必须在 resolve 前拒绝：${invalid.slice(0, 40)}`,
  );
}

const qrSource = readFileSync(
  join(dirname(fileURLToPath(import.meta.url)), "attendance-qr.ts"),
  "utf8",
);
assert.doesNotMatch(qrSource, /base64-js|PayloadReader|TextDecoder|storeCode\s*=\s*reader|deviceCode\s*=\s*reader/,
  "客户端不得包含二维码 payload 解码器或读取门店/设备字段");

assert.equal(
  resolveAttendanceQrStore({ storeCode: "STORE-02" }, [
    { storeCode: "STORE-01", storeName: "Brisbane" },
    { storeCode: "STORE-02", storeName: "Gold Coast" },
  ]).storeCode,
  "STORE-02",
  "跨店扫码必须使用后端 resolve 返回的门店选择授权门店",
);
assert.throws(
  () => resolveAttendanceQrStore({ storeCode: "STORE-02" }, []),
  /ATTENDANCE_STORE_FORBIDDEN/,
  "后端返回的二维码门店不在员工授权列表时必须拒绝",
);

const punchAuthorizationToken = "attendance-punch-authorization";
const payload = buildAttendanceQrPunchPayload(token, punchAuthorizationToken, {
  locationLatitude: -27.47,
  locationLongitude: 153.03,
  locationAccuracy: 8,
  locationPermissionStatus: "granted",
  locationCapturedAtUtc: "2026-07-16T00:00:00Z",
  networkVerificationStatus: "online",
});
assert.deepEqual(
  Object.keys(payload).sort(),
  [
    "locationAccuracy",
    "locationCapturedAtUtc",
    "locationLatitude",
    "locationLongitude",
    "punchAuthorizationToken",
    "qrToken",
  ],
  "punch 必须同时提交原始 token、短时凭证与实时 GPS",
);
assert.equal(payload.punchAuthorizationToken, punchAuthorizationToken);

const legacyPayload = buildAttendanceQrPunchPayload(token, undefined, {
  locationLatitude: -27.47,
  locationLongitude: 153.03,
  locationCapturedAtUtc: "2026-07-16T00:00:00Z",
});
assert.equal("punchAuthorizationToken" in legacyPayload, false,
  "旧后端无短时凭证时必须保持原二维码打卡 payload");
assert.equal("mealDeclaration" in legacyPayload, false,
  "没问过用餐（上班或不缺休息）时不能带 mealDeclaration");
const mealPayload = buildAttendanceQrPunchPayload(token, punchAuthorizationToken, {
  locationLatitude: -27.47,
  locationLongitude: 153.03,
  locationCapturedAtUtc: "2026-07-16T00:00:00Z",
}, { notTakenCount: 1 });
assert.deepEqual(mealPayload.mealDeclaration, { notTakenCount: 1 },
  "扫码前回答「没休息」必须随下班打卡提交");

assert.equal(canOpenAttendanceQrScanner({ isLoading: false, isPunching: false, isToday: true, hasAuthorizedStores: true }), true);
assert.equal(canOpenAttendanceQrScanner({ isLoading: false, isPunching: false, isToday: true, hasAuthorizedStores: false }), false);
assert.equal(canSubmitAttendanceQrPunch({ hasCurrentLocation: true, isOnline: true, hasBackgroundLocationPermission: false }), false);
assert.equal(canSubmitAttendanceQrPunch({ hasCurrentLocation: true, isOnline: true, hasBackgroundLocationPermission: true }), true);
assert.equal(shouldEnableAttendanceQrScanning({ isVisible: true, isSubmitting: false, isPaused: true }), false);
assert.equal(shouldEnableAttendanceQrScanning({ isVisible: true, isSubmitting: false, isPaused: false }), true);

assert.equal(getAttendanceTrackingAction("ClockIn"), "start");
assert.equal(getAttendanceTrackingAction("ClockOut"), "stop");
assert.equal(getAttendanceTrackingAction("Unknown"), "none");
assert.equal(requiresBackgroundPermissionBeforePunch("ClockIn"), true);
assert.equal(requiresBackgroundPermissionBeforePunch("ClockOut"), false);

const errorCases: Record<string, string> = {
  ATTENDANCE_QR_DECRYPT_FAILED: "messages.qrDecryptFailed",
  ATTENDANCE_QR_KEY_DECRYPT_FAILED: "messages.qrDecryptFailed",
  ATTENDANCE_QR_KEY_INVALID: "messages.qrKeyInvalid",
  ATTENDANCE_QR_KEY_UNKNOWN: "messages.qrKeyUnknown",
  ATTENDANCE_QR_KEY_REVOKED: "messages.qrKeyRevoked",
  ATTENDANCE_QR_FORMAT_INVALID: "messages.qrFormatInvalid",
  ATTENDANCE_QR_NOT_ACTIVE: "messages.qrNotActive",
  ATTENDANCE_QR_EXPIRED: "messages.qrExpired",
  ATTENDANCE_PUNCH_AUTHORIZATION_INVALID: "messages.qrPunchAuthorizationInvalid",
  ATTENDANCE_PUNCH_AUTHORIZATION_EXPIRED: "messages.qrPunchAuthorizationExpired",
  ATTENDANCE_QR_DEVICE_MISMATCH: "messages.qrDeviceMismatch",
  POS_DEVICE_DISABLED: "messages.qrDeviceDisabled",
  STORE_ACCESS_DENIED: "messages.qrStoreForbidden",
  LOCATION_REQUIRED: "messages.qrGpsRequired",
  NETWORK_ERROR: "messages.qrNetworkRequired",
  ERR_NETWORK: "messages.qrNetworkRequired",
  DAY_COMPLETE: "messages.qrDayComplete",
};
for (const [code, key] of Object.entries(errorCases)) {
  assert.equal(getAttendancePunchErrorKey(code), key, `${code} 应映射明确文案`);
}
assert.equal(
  getAttendancePunchErrorCode(Object.assign(new Error("二维码已过期"), { code: "ATTENDANCE_QR_EXPIRED" })),
  "ATTENDANCE_QR_EXPIRED",
);
assert.equal(
  getAttendancePunchErrorCode({
    code: "ERR_BAD_REQUEST",
    response: { data: { errorCode: "ATTENDANCE_QR_EXPIRED" } },
  }),
  "ATTENDANCE_QR_EXPIRED",
  "响应业务码必须优先于 Axios 顶层传输码",
);
assert.equal(
  getAttendancePunchErrorCode({
    code: "ERR_BAD_RESPONSE",
    response: { data: { data: { ErrorCode: "ATTENDANCE_QR_KEY_REVOKED" } } },
  }),
  "ATTENDANCE_QR_KEY_REVOKED",
  "必须读取真实 API envelope 内嵌业务码",
);

assert.deepEqual(normalizeAttendanceQrResolveResult({
  StoreCode: "STORE-02",
  DeviceCode: "POS-09",
  StoreName: "Gold Coast",
  ExpiresAtUtc: "2026-07-16T00:00:30Z",
  PunchAuthorizationToken: punchAuthorizationToken,
  PunchAuthorizationExpiresAtUtc: "2026-07-16T00:02:00Z",
}), {
  storeCode: "STORE-02",
  deviceCode: "POS-09",
  storeName: "Gold Coast",
  expiresAtUtc: "2026-07-16T00:00:30Z",
  punchAuthorizationToken,
  punchAuthorizationExpiresAtUtc: "2026-07-16T00:02:00Z",
});
assert.deepEqual(normalizeAttendanceQrResolveResult({
  StoreCode: "STORE-02",
  DeviceCode: "POS-09",
  StoreName: "Gold Coast",
  ExpiresAtUtc: "2026-07-16T00:00:30Z",
}), {
  storeCode: "STORE-02",
  deviceCode: "POS-09",
  storeName: "Gold Coast",
  expiresAtUtc: "2026-07-16T00:00:30Z",
}, "旧后端 resolve 响应必须继续可用");
for (const malformed of [
  {},
  {
    storeCode: "",
    deviceCode: "POS-01",
    expiresAtUtc: "2026-07-16T00:00:30Z",
    punchAuthorizationToken,
    punchAuthorizationExpiresAtUtc: "2026-07-16T00:02:00Z",
  },
  {
    storeCode: "STORE-01",
    deviceCode: "",
    expiresAtUtc: "2026-07-16T00:00:30Z",
    punchAuthorizationToken,
    punchAuthorizationExpiresAtUtc: "2026-07-16T00:02:00Z",
  },
  {
    storeCode: "STORE-01",
    deviceCode: "POS-01",
    expiresAtUtc: "not-a-date",
    punchAuthorizationToken,
    punchAuthorizationExpiresAtUtc: "2026-07-16T00:02:00Z",
  },
  {
    storeCode: "STORE-01",
    deviceCode: "POS-01",
    expiresAtUtc: "2026-07-16T00:00:30Z",
    punchAuthorizationToken: "",
    punchAuthorizationExpiresAtUtc: "2026-07-16T00:02:00Z",
  },
  {
    storeCode: "STORE-01",
    deviceCode: "POS-01",
    expiresAtUtc: "2026-07-16T00:00:30Z",
    punchAuthorizationToken,
    punchAuthorizationExpiresAtUtc: "invalid",
  },
]) {
  assert.throws(
    () => normalizeAttendanceQrResolveResult(malformed),
    /ATTENDANCE_QR_FORMAT_INVALID/,
    "畸形 resolve 响应必须 fail closed",
  );
}
let malformedResolveError: unknown;
try {
  normalizeAttendanceQrResolveResult({
    storeCode: "STORE-01",
    deviceCode: "POS-01",
    expiresAtUtc: "invalid",
    punchAuthorizationToken,
    punchAuthorizationExpiresAtUtc: "2026-07-16T00:02:00Z",
  });
} catch (error) {
  malformedResolveError = error;
}
assert.equal(
  getAttendancePunchErrorCode(malformedResolveError),
  "ATTENDANCE_QR_FORMAT_INVALID",
  "畸形 resolve 响应必须抛出页面可映射的安全错误码",
);

const historyCompatiblePunch = {
  punchGuid: "",
  storeCode: undefined,
  workDate: "",
  punchType: "ClockIn" as const,
  status: "Normal" as const,
};
for (const malformed of [
  {},
  { punchType: "ClockIn", storeCode: "STORE-01", serverTimeUtc: "2026-07-16T00:00:00Z" },
  { punchGuid: "P-1", storeCode: "STORE-01", serverTimeUtc: "2026-07-16T00:00:00Z" },
  { punchGuid: "P-1", punchType: "Break", storeCode: "STORE-01", serverTimeUtc: "2026-07-16T00:00:00Z" },
  { punchGuid: "P-1", punchType: "ClockOut", storeCode: "", serverTimeUtc: "2026-07-16T00:00:00Z" },
  { punchGuid: "P-1", punchType: "ClockOut", storeCode: "STORE-01", serverTimeUtc: "" },
  { punchGuid: "P-1", punchType: "ClockOut", storeCode: "STORE-01", serverTimeUtc: "2026-07-16T00:00:00Z", workDate: "", storeTimeZone: "Australia/Sydney" },
  { punchGuid: "P-1", punchType: "ClockOut", storeCode: "STORE-01", serverTimeUtc: "2026-07-16T00:00:00Z", workDate: "2026-07-16T01:00:00", storeTimeZone: "Australia/Sydney" },
  { punchGuid: "P-1", punchType: "ClockOut", storeCode: "STORE-01", serverTimeUtc: "2026-07-16T00:00:00Z", workDate: "2026-07-16", storeTimeZone: "" },
]) {
  assert.throws(
    () => normalizeAttendancePunchMutationResult(malformed, historyCompatiblePunch),
    /ATTENDANCE_PUNCH_RESPONSE_INVALID/,
    "punch mutation 畸形响应必须在 tracking 前 fail closed",
  );
}
const strictPunch = normalizeAttendancePunchMutationResult({
  PunchGuid: "P-1",
  PunchType: "ClockOut",
  StoreCode: "STORE-02",
  ServerTimeUtc: "2026-07-16T00:00:00Z",
  WorkDate: "2026-07-16T00:00:00",
  StoreTimeZone: "Australia/Sydney",
}, historyCompatiblePunch);
assert.deepEqual(
  {
    punchGuid: strictPunch.punchGuid,
    punchType: strictPunch.punchType,
    storeCode: strictPunch.storeCode,
    serverTimeUtc: strictPunch.serverTimeUtc,
    workDate: strictPunch.workDate,
    storeTimeZone: strictPunch.storeTimeZone,
  },
  {
    punchGuid: "P-1",
    punchType: "ClockOut",
    storeCode: "STORE-02",
    serverTimeUtc: "2026-07-16T00:00:00Z",
    workDate: "2026-07-16",
    storeTimeZone: "Australia/Sydney",
  },
  "有效 mutation 响应必须保留服务端权威 tracking 窗口字段",
);

async function runAsyncTests() {
  const trackingEvents: string[] = [];
  await applyAttendanceTrackingLifecycle(
    { punchType: "ClockIn", storeCode: "STORE-02" },
    "STORE-01",
    {
      start: async (storeCode) => { trackingEvents.push(`start:${storeCode}`); },
      stop: async () => { trackingEvents.push("stop"); },
    },
  );
  await applyAttendanceTrackingLifecycle(
    { punchType: "ClockOut" },
    "STORE-01",
    {
      start: async (storeCode) => { trackingEvents.push(`start:${storeCode}`); },
      stop: async () => { trackingEvents.push("stop"); },
    },
  );
  assert.deepEqual(trackingEvents, ["start:STORE-02", "stop"],
    "tracking 生命周期只依赖服务端 punch 结果，不依赖 UI session");

  const readyVerification = {
    checkedAt: "2026-07-16T00:00:00Z",
    location: {
      status: "available" as const,
      reason: "captured" as const,
      permissionStatus: "granted" as const,
    },
    network: {
      status: "available" as const,
      reason: "captured" as const,
      verificationStatus: "online" as const,
    },
    payload: {
      locationLatitude: -27.47,
      locationLongitude: 153.03,
      locationCapturedAtUtc: "2026-07-16T00:00:00Z",
      networkVerificationStatus: "online" as const,
    },
  };
  const clockInOrder: string[] = [];
  const clockInPreparation = await prepareAttendanceQrPunch("ClockIn", {
    isActive: () => true,
    ensureBackgroundPermission: async () => {
      clockInOrder.push("background");
      return true;
    },
    refreshVerification: async () => {
      clockInOrder.push("refresh");
      return readyVerification;
    },
  });
  assert.deepEqual(clockInOrder, ["background", "refresh"],
    "预计 ClockIn 必须先完成后台定位授权，再紧邻 punch 刷新 GPS/network");
  assert.equal(clockInPreparation.status, "ready");

  const clockOutOrder: string[] = [];
  const clockOutPreparation = await prepareAttendanceQrPunch("ClockOut", {
    isActive: () => true,
    ensureBackgroundPermission: async () => {
      clockOutOrder.push("background");
      return false;
    },
    refreshVerification: async () => {
      clockOutOrder.push("refresh");
      return readyVerification;
    },
  });
  assert.deepEqual(clockOutOrder, ["refresh"], "ClockOut 不得要求后台定位权限");
  assert.equal(clockOutPreparation.status, "ready");
}

void runAsyncTests().then(() => console.log("attendance-qr.test.ts: ok"));
