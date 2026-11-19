import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import {
  appendPendingPunchLog,
  buildPunchDiagnosticLog,
  clipDiagnosticText,
  MAX_PENDING_PUNCH_LOGS,
  parsePendingPunchLogs,
  PUNCH_DIAGNOSTICS_STORAGE_KEY,
  readHttpStatus,
  summarizePermissionResponse,
  uploadPendingPunchLogs,
  type PunchDiagnosticEvent,
  type PunchDiagnosticLogItem,
  type PunchDiagnosticStorage,
  type PunchPermissionSnapshot,
} from "./punch-diagnostics";

const CONTEXT = {
  environment: "production",
  appVersion: "1.0.13+66",
  properties: { platform: "ios", osVersion: "27.2", updateId: undefined, channel: null },
};

// 与后端 ApplicationLogService.SensitiveKeyFragments 保持一致：键名（去掉非字母数字后小写）含这些片段的值会被替换成 [REDACTED]。
const BACKEND_SENSITIVE_FRAGMENTS = [
  "authorization", "bearer", "password", "pin", "apikey", "token", "secret", "credential", "cvv", "pan",
  "card", "cardnumber", "voucher", "vouchercode", "cookie", "header", "employeebarcode", "customeremail",
  "customerphone", "customeraddress", "customername", "requestbody", "responsebody", "rawrequest", "rawresponse",
];

const ALWAYS_BUT_DENIED: PunchPermissionSnapshot = {
  foreground: { status: "granted", granted: true, canAskAgain: true, scope: "always" },
  background: { status: "denied", granted: false, canAskAgain: false, scope: "whenInUse" },
  servicesEnabled: true,
};

function build(event: PunchDiagnosticEvent, id = "00000000-0000-4000-8000-000000000001") {
  return buildPunchDiagnosticLog({ event, nowMs: Date.UTC(2026, 10, 19, 0, 56, 18), id, context: CONTEXT });
}

test("summarizePermissionResponse：只取状态类字段，iOS scope / Android accuracy 都保留", () => {
  assert.deepEqual(
    summarizePermissionResponse({
      status: "granted",
      granted: true,
      canAskAgain: false,
      expires: "never",
      ios: { scope: "always" },
      latitude: -33.8,
    }),
    { status: "granted", granted: true, canAskAgain: false, scope: "always" },
  );
  assert.deepEqual(
    summarizePermissionResponse({ status: "denied", android: { accuracy: "coarse" } }),
    { status: "denied", accuracy: "coarse" },
  );
  assert.deepEqual(summarizePermissionResponse(null), {});
  assert.deepEqual(summarizePermissionResponse("granted"), {});
});

test("buildPunchDiagnosticLog：定位准备失败记 Warning，带前后台权限读数与校验结果", () => {
  const item = build({
    stage: "locating",
    outcome: "failed",
    reason: "backgroundRequired",
    punchType: "ClockIn",
    consentedBeforeScan: true,
    elapsedMs: 2345.6,
    permissionBefore: ALWAYS_BUT_DENIED,
    permissionAfter: ALWAYS_BUT_DENIED,
    verification: { locationStatus: "unavailable", locationReason: "timeout", networkStatus: "available" },
  });

  assert.equal(item.level, "Warning");
  assert.equal(item.category, "attendance.punch");
  assert.equal(item.sourceType, "Mobile");
  assert.equal(item.serviceName, "HbwebExpoApp");
  assert.equal(item.appVersion, "1.0.13+66");
  assert.equal(item.timestampUtc, "2026-11-19T00:56:18.000Z");
  assert.match(item.message, /定位准备失败：backgroundRequired/);
  assert.equal(item.properties.stage, "locating");
  assert.equal(item.properties.beforeFgScope, "always");
  assert.equal(item.properties.beforeBgStatus, "denied");
  assert.equal(item.properties.beforeBgScope, "whenInUse");
  assert.equal(item.properties.afterBgCanAskAgain, false);
  assert.equal(item.properties.beforeServicesEnabled, true);
  assert.equal(item.properties.consentedBeforeScan, true);
  assert.equal(item.properties.elapsedMs, 2346);
  assert.equal(item.properties.verifyLocationReason, "timeout");
  assert.equal(item.properties.verifyNetworkStatus, "available");
  // 设备环境并入，undefined / null 的不写。
  assert.equal(item.properties.platform, "ios");
  assert.equal(item.properties.osVersion, "27.2");
  assert.equal("updateId" in item.properties, false);
  assert.equal("channel" in item.properties, false);
});

test("buildPunchDiagnosticLog：权限放行是 Information，其余都是 Warning", () => {
  assert.equal(build({ stage: "permission", outcome: "granted", reason: "requestGranted" }).level, "Information");
  for (const outcome of ["failed", "denied", "cancelled"] as const) {
    assert.equal(build({ stage: "permission", outcome }).level, "Warning");
  }
});

test("buildPunchDiagnosticLog：提交失败只带错误码与 HTTP 状态，文本被压成单行并截断", () => {
  const item = build({
    stage: "punch",
    outcome: "failed",
    errorCode: "ATTENDANCE_QR_EXPIRED",
    httpStatus: 200,
    errorMessage: `line1\n  line2 ${"x".repeat(400)}`,
    punchType: "ClockIn",
  });
  assert.equal(item.properties.errorCode, "ATTENDANCE_QR_EXPIRED");
  assert.equal(item.properties.httpStatus, 200);
  const text = item.properties.errorMessage as string;
  assert.equal(text.includes("\n"), false);
  assert.ok(text.length <= 161, "超长文本需截断");
  assert.match(item.message, /提交打卡失败：ATTENDANCE_QR_EXPIRED/);
});

test("守卫：所有上报属性名都避开后端敏感片段，否则值会被替换成 [REDACTED]", () => {
  const item = build({
    stage: "locating",
    outcome: "failed",
    reason: "gpsRequired",
    errorCode: "X",
    httpStatus: 500,
    errorMessage: "m",
    punchType: "ClockIn",
    consentedBeforeScan: false,
    elapsedMs: 1,
    permissionBefore: { ...ALWAYS_BUT_DENIED, foreground: { ...ALWAYS_BUT_DENIED.foreground, accuracy: "fine", error: "e" } },
    permissionAfter: ALWAYS_BUT_DENIED,
    verification: {
      locationStatus: "a",
      locationReason: "b",
      locationPermission: "c",
      networkStatus: "d",
      networkVerification: "e",
    },
  });
  for (const key of Object.keys(item.properties)) {
    const normalized = key.replace(/[^A-Za-z0-9]/g, "").toLowerCase();
    for (const fragment of BACKEND_SENSITIVE_FRAGMENTS) {
      assert.equal(normalized.includes(fragment), false, `属性名 ${key} 含敏感片段 ${fragment}`);
    }
  }
});

test("clipDiagnosticText / readHttpStatus", () => {
  assert.equal(clipDiagnosticText("  a \n b  "), "a b");
  assert.equal(clipDiagnosticText(""), undefined);
  assert.equal(clipDiagnosticText(42), undefined);
  assert.equal(readHttpStatus({ response: { status: 401 } }), 401);
  assert.equal(readHttpStatus(new Error("x")), undefined);
  assert.equal(readHttpStatus(null), undefined);
});

test("parsePendingPunchLogs / appendPendingPunchLog：坏数据当空、同 id 去重、超过上限丢最旧", () => {
  assert.deepEqual(parsePendingPunchLogs(null), []);
  assert.deepEqual(parsePendingPunchLogs("not json"), []);
  assert.deepEqual(parsePendingPunchLogs('{"a":1}'), []);
  assert.equal(parsePendingPunchLogs('[{"clientEventId":"a"},{"x":1},null]').length, 1);

  const make = (id: string) => build({ stage: "punch", outcome: "failed" }, id);
  let list: PunchDiagnosticLogItem[] = [];
  list = appendPendingPunchLog(list, make("a"));
  list = appendPendingPunchLog(list, make("a"));
  assert.equal(list.length, 1);
  for (let index = 0; index < MAX_PENDING_PUNCH_LOGS + 5; index += 1) {
    list = appendPendingPunchLog(list, make(`id-${index}`));
  }
  assert.equal(list.length, MAX_PENDING_PUNCH_LOGS);
  assert.equal(list[list.length - 1].clientEventId, `id-${MAX_PENDING_PUNCH_LOGS + 4}`);
  assert.equal(list.some((item) => item.clientEventId === "a"), false);
});

function memoryStorage(initial: PunchDiagnosticLogItem[]): PunchDiagnosticStorage & { read(): PunchDiagnosticLogItem[] } {
  let raw: string | null = initial.length ? JSON.stringify(initial) : null;
  return {
    async getString() { return raw; },
    async setString(_key, value) { raw = value; },
    read: () => parsePendingPunchLogs(raw),
  };
}

test("uploadPendingPunchLogs：没有暂存不请求；上传成功清空；服务端明确拒收清掉；临时失败保留", async () => {
  const item = build({ stage: "punch", outcome: "failed" }, "a");

  const empty = memoryStorage([]);
  let calls = 0;
  assert.equal(await uploadPendingPunchLogs({ storage: empty, upload: async () => { calls += 1; } }), "skipped");
  assert.equal(calls, 0);

  const ok = memoryStorage([item]);
  assert.equal(await uploadPendingPunchLogs({ storage: ok, upload: async () => undefined }), "uploaded");
  assert.deepEqual(ok.read(), []);

  const rejected = memoryStorage([item]);
  assert.equal(
    await uploadPendingPunchLogs({ storage: rejected, upload: async () => { throw { response: { status: 403 } }; } }),
    "dropped",
  );
  assert.deepEqual(rejected.read(), []);

  // 网络失败（无状态码）、限流、服务端错误、401、接口未上线 404 都保留，下次再传。
  for (const error of [new Error("network"), { response: { status: 429 } }, { response: { status: 500 } }, { response: { status: 401 } }, { response: { status: 404 } }]) {
    const kept = memoryStorage([item]);
    assert.equal(await uploadPendingPunchLogs({ storage: kept, upload: async () => { throw error; } }), "kept");
    assert.equal(kept.read().length, 1);
  }
  assert.equal(PUNCH_DIAGNOSTICS_STORAGE_KEY, "hb.attendance.punchDiagnostics.v1");
});

test("源码契约：扫码打卡每个失败阶段都有上报点，且不把二维码 token / 授权凭证带进上报", () => {
  const source = readFileSync(
    join(dirname(fileURLToPath(import.meta.url)), "../../components/attendance/AttendanceScreen.tsx"),
    "utf8",
  );
  for (const stage of ["format", "resolve", "store", "today", "punch"]) {
    assert.ok(source.includes(`reportQrFailure("${stage}"`), `缺少 ${stage} 阶段的上报`);
  }
  for (const stage of ["locating", "permission"]) {
    assert.ok(source.includes(`stage: "${stage}"`), `缺少 ${stage} 阶段的上报`);
  }
  // 所有 reportPunchDiagnostic / reportQrFailure 调用的实参里不得出现二维码 token 与打卡授权凭证。
  const calls = source.match(/report(?:PunchDiagnostic|QrFailure)\([\s\S]*?\n\s*\}?\)\;?/g) ?? [];
  assert.ok(calls.length >= 6);
  for (const call of calls) {
    assert.equal(/normalizedQrToken|qrToken|punchAuthorizationToken/.test(call), false, `上报不得带凭证：${call.slice(0, 80)}`);
  }
  // 进入打卡页补传一次。
  assert.ok(source.includes("flushPunchDiagnostics()"));
});
