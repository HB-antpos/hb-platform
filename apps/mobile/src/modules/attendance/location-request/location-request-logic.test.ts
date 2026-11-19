import assert from "node:assert/strict";
import { test } from "node:test";
import {
  LOCATION_REQUEST_ERROR_CODES,
  LOCATION_REQUEST_FETCH_MIN_INTERVAL_MS,
  LOCATION_REQUEST_MAX_PER_RUN,
  LOCATION_REQUEST_PENDING_PATH,
  LocationRequestResponseError,
  TERMINAL_RESPOND_ERROR_CODES,
  buildReportedNotice,
  buildRespondRequestBody,
  bypassesFetchThrottle,
  classifyLocationRequestError,
  isInteractiveTrigger,
  isLocationFreshForRequest,
  locationRequestRespondPath,
  normalizeLocationRequest,
  normalizeLocationRequestList,
  resolveLocationRequestUserGuid,
  selectActionableRequests,
  type AttendanceLocationRequest,
  type LocationRequestSessionSnapshot,
  type LocationRequestTrigger,
} from "./location-request-logic";

const NOW = Date.parse("2026-11-19T01:10:00Z");

function request(overrides: Partial<AttendanceLocationRequest> = {}): AttendanceLocationRequest {
  return {
    requestGuid: "r1",
    storeCode: "1042",
    storeName: "Charlestown",
    requestedByName: "Alex Manager",
    status: "Pending",
    createdAtUtc: "2026-11-19T01:05:00Z",
    expiresAtUtc: "2026-11-19T01:20:00Z",
    alreadyResponded: false,
    ...overrides,
  };
}

test("路径：待办与响应接口与后端契约一致，requestGuid 做 URL 编码", () => {
  assert.equal(LOCATION_REQUEST_PENDING_PATH, "/react/v1/attendance/my/location-requests/pending");
  assert.equal(locationRequestRespondPath("abc-123"), "/react/v1/attendance/my/location-requests/abc-123/respond");
  assert.equal(
    locationRequestRespondPath("a/b?c"),
    "/react/v1/attendance/my/location-requests/a%2Fb%3Fc/respond",
    "requestGuid 不能拼出别的路径",
  );
});

test("常量：拉取间隔 15 秒；终态错误码覆盖契约里的全部七个", () => {
  assert.equal(LOCATION_REQUEST_FETCH_MIN_INTERVAL_MS, 15_000);
  assert.deepEqual(
    [...TERMINAL_RESPOND_ERROR_CODES].sort(),
    [
      "ACTIVE_SHIFT_REQUIRED",
      "LOCATION_REQUEST_CANCELLED",
      "LOCATION_REQUEST_EXPIRED",
      "LOCATION_REQUEST_NOT_FOUND",
      "LOCATION_REQUIRED",
      "LOCATION_STALE",
      "SAMPLE_IN_FUTURE",
    ],
  );
  assert.equal(LOCATION_REQUEST_ERROR_CODES.locationStale, "LOCATION_STALE");
});

// ───────── 响应归一化 ─────────

test("normalizeLocationRequestList：直接数组与统一信封都能解析，字段 trim", () => {
  const dto = {
    requestGuid: " r1 ",
    storeCode: "1042",
    storeName: "Charlestown",
    requestedByName: " Alex ",
    status: "Pending",
    createdAtUtc: "2026-11-19T01:05:00Z",
    expiresAtUtc: "2026-11-19T01:20:00Z",
    alreadyResponded: false,
    locationLatitude: null,
  };
  const direct = normalizeLocationRequestList([dto]);
  assert.deepEqual(direct, [
    {
      requestGuid: "r1",
      storeCode: "1042",
      storeName: "Charlestown",
      requestedByName: "Alex",
      status: "Pending",
      createdAtUtc: "2026-11-19T01:05:00Z",
      expiresAtUtc: "2026-11-19T01:20:00Z",
      alreadyResponded: false,
    },
  ]);
  assert.deepEqual(normalizeLocationRequestList({ success: true, data: [dto] }), direct, "未解包的信封同样能处理");
  assert.deepEqual(normalizeLocationRequestList([]), []);
});

test("normalizeLocationRequestList：整体不是列表按格式错误（不能当成没有待办）；个别坏条目只丢这一条", () => {
  for (const bad of [null, undefined, "x", 42, {}, { success: true, data: { requestGuid: "r1" } }, { success: true, data: null }]) {
    assert.throws(() => normalizeLocationRequestList(bad), LocationRequestResponseError, JSON.stringify(bad));
  }
  const list = normalizeLocationRequestList([
    { requestGuid: "ok-1", status: "Pending" },
    null,
    "oops",
    { status: "Pending" },
    { requestGuid: "   " },
    { requestGuid: "ok-2" },
  ]);
  assert.deepEqual(list.map((item) => item.requestGuid), ["ok-1", "ok-2"]);
  assert.equal(list[1].alreadyResponded, false);
  assert.equal(list[1].storeName, undefined);
});

test("normalizeLocationRequestList：success:false 信封抛出带 errorCode 的业务错误，不当成列表", () => {
  try {
    normalizeLocationRequestList({ success: false, errorCode: "FORBIDDEN", message: "no" });
    assert.fail("应当抛错");
  } catch (error) {
    assert.equal((error as { code?: string }).code, "FORBIDDEN");
    assert.equal((error as { apiBusinessError?: boolean }).apiBusinessError, true);
  }
});

test("normalizeLocationRequest：响应结果必须是带 requestGuid 的对象；alreadyResponded 只认 true", () => {
  const ok = normalizeLocationRequest({ requestGuid: "r1", status: "Responded", alreadyResponded: true });
  assert.equal(ok.alreadyResponded, true);
  assert.equal(normalizeLocationRequest({ requestGuid: "r1", alreadyResponded: "true" }).alreadyResponded, false);
  assert.equal(normalizeLocationRequest({ success: true, data: { requestGuid: "r2" } }).requestGuid, "r2");
  for (const bad of [null, undefined, "x", [], {}, { requestGuid: "" }]) {
    assert.throws(() => normalizeLocationRequest(bad), LocationRequestResponseError, JSON.stringify(bad));
  }
});

// ───────── 待办筛选与排序 ─────────

test("selectActionableRequests：只留 Pending、未过期、非重放、本会话未处理的请求", () => {
  const list = [
    request({ requestGuid: "keep" }),
    request({ requestGuid: "responded", status: "Responded" }),
    request({ requestGuid: "expired-status", status: "Expired" }),
    request({ requestGuid: "cancelled", status: "Cancelled" }),
    request({ requestGuid: "replay", alreadyResponded: true }),
    request({ requestGuid: "expired-by-clock", expiresAtUtc: "2026-11-19T01:09:59Z" }),
    request({ requestGuid: "expires-exactly-now", expiresAtUtc: "2026-11-19T01:10:00Z" }),
    request({ requestGuid: "handled-before" }),
  ];
  const actionable = selectActionableRequests(list, NOW, new Set(["handled-before"]));
  assert.deepEqual(actionable.map((item) => item.requestGuid), ["keep"]);
});

test("selectActionableRequests：状态大小写不敏感；expiresAtUtc 解析不了就保留（交给服务端裁判）", () => {
  const actionable = selectActionableRequests(
    [request({ requestGuid: "lower", status: "pending" }), request({ requestGuid: "no-expiry", expiresAtUtc: "" }), request({ requestGuid: "junk-expiry", expiresAtUtc: "soon" })],
    NOW,
  );
  assert.deepEqual(actionable.map((item) => item.requestGuid).sort(), ["junk-expiry", "lower", "no-expiry"]);
});

test("selectActionableRequests：按创建时间从早到晚排序，同时间按 guid 稳定排序，创建时间无效的排最后", () => {
  const actionable = selectActionableRequests(
    [
      request({ requestGuid: "c-late", createdAtUtc: "2026-11-19T01:08:00Z" }),
      request({ requestGuid: "b-same", createdAtUtc: "2026-11-19T01:06:00Z" }),
      request({ requestGuid: "a-same", createdAtUtc: "2026-11-19T01:06:00Z" }),
      request({ requestGuid: "z-unknown", createdAtUtc: "" }),
      request({ requestGuid: "a-early", createdAtUtc: "2026-11-19T01:01:00Z" }),
    ],
    NOW,
  );
  assert.deepEqual(actionable.map((item) => item.requestGuid), ["a-early", "a-same", "b-same", "c-late", "z-unknown"]);
});

test("selectActionableRequests：同一 requestGuid 去重；单轮有数量上限；不修改入参", () => {
  const list = [request({ requestGuid: "dup" }), request({ requestGuid: "dup" })];
  assert.equal(selectActionableRequests(list, NOW).length, 1);

  const many = Array.from({ length: 12 }, (_, index) => request({ requestGuid: `r${String(index).padStart(2, "0")}` }));
  const snapshot = JSON.stringify(many);
  assert.equal(selectActionableRequests(many, NOW).length, LOCATION_REQUEST_MAX_PER_RUN);
  assert.equal(JSON.stringify(many), snapshot);
  assert.deepEqual(selectActionableRequests([], NOW), []);
});

// ───────── 采集时间复核 ─────────

test("isLocationFreshForRequest：采集时间不能比请求创建时间早超过 30 秒；时间无效放行", () => {
  const r = request({ createdAtUtc: "2026-11-19T01:05:00Z" });
  assert.equal(isLocationFreshForRequest("2026-11-19T01:05:00Z", r), true);
  assert.equal(isLocationFreshForRequest("2026-11-19T01:04:30Z", r), true, "恰好 30 秒的容差内");
  assert.equal(isLocationFreshForRequest("2026-11-19T01:04:29Z", r), false, "早于请求 30 秒以上：不是为这次请求取的");
  assert.equal(isLocationFreshForRequest("2026-11-19T01:09:00Z", r), true);
  assert.equal(isLocationFreshForRequest("garbage", r), true);
  assert.equal(isLocationFreshForRequest("2026-11-19T01:04:00Z", request({ createdAtUtc: "" })), true);
});

// ───────── 错误分类 ─────────

const businessError = (code?: string) => Object.assign(new Error("failed"), { apiBusinessError: true, ...(code ? { code } : {}) });
const httpError = (status: number | undefined, extra: Record<string, unknown> = {}) =>
  Object.assign(new Error("http"), { ...(status === undefined ? {} : { response: { status, data: {} } }), ...extra });

test("classifyLocationRequestError：七个终态错误码一律 terminal（静默丢弃，不重试）", () => {
  for (const code of TERMINAL_RESPOND_ERROR_CODES) {
    assert.deepEqual(classifyLocationRequestError(businessError(code)), { kind: "terminal", code, status: undefined }, code);
  }
  // 错误码也可能只在 HTTP 响应体里（非 200 的信封）
  const fromBody = httpError(409, { response: { status: 409, data: { success: false, errorCode: "LOCATION_REQUEST_EXPIRED" } } });
  assert.equal(classifyLocationRequestError(fromBody).kind, "terminal");
  assert.equal(classifyLocationRequestError(fromBody).code, "LOCATION_REQUEST_EXPIRED");
});

test("classifyLocationRequestError：账号切换打断是 aborted，不算失败", () => {
  assert.deepEqual(classifyLocationRequestError(Object.assign(new Error("ACCOUNT_SESSION_CHANGED"), { code: "ACCOUNT_SESSION_CHANGED" })), { kind: "aborted" });
});

test("classifyLocationRequestError：网络错误 / 超时 / 5xx / 408 / 429 / 响应格式异常 可重试", () => {
  assert.equal(classifyLocationRequestError(httpError(undefined, { code: "ERR_NETWORK" })).kind, "retryable");
  assert.equal(classifyLocationRequestError(httpError(undefined, { code: "ECONNABORTED" })).kind, "retryable");
  assert.equal(classifyLocationRequestError(new Error("timeout")).kind, "retryable");
  for (const status of [500, 502, 503, 504, 408, 429]) {
    assert.deepEqual(classifyLocationRequestError(httpError(status)), { kind: "retryable", status }, String(status));
  }
  assert.equal(classifyLocationRequestError(new LocationRequestResponseError("bad")).kind, "retryable");
  assert.equal(classifyLocationRequestError(null).kind, "retryable");
  assert.equal(classifyLocationRequestError("oops").kind, "retryable");
});

test("classifyLocationRequestError：其余 4xx 与不认识的业务错误码是终态（重试不会变），不会被误当成网络错误无限重试", () => {
  for (const status of [400, 403, 404, 409, 422]) {
    assert.equal(classifyLocationRequestError(httpError(status)).kind, "terminal", String(status));
  }
  const unknown = classifyLocationRequestError(businessError("SOMETHING_NEW"));
  assert.deepEqual(unknown, { kind: "terminal", code: "SOMETHING_NEW", status: undefined });
  assert.equal(classifyLocationRequestError(businessError()).kind, "terminal");
});

// ───────── 请求体 ─────────

test("buildRespondRequestBody：字段齐全，采集时间原样，权限固定 granted", () => {
  const body = buildRespondRequestBody(
    { hardwareId: " hw-1 ", systemDeviceNumber: "SYS-9", deviceSystem: "iOS" },
    {
      locationLatitude: -32.9,
      locationLongitude: 151.7,
      locationAccuracy: 8.5,
      locationPermissionStatus: "granted",
      locationCapturedAtUtc: "2026-11-19T01:08:09.123Z",
    },
  );
  assert.deepEqual(body, {
    hardwareId: "hw-1",
    systemDeviceNumber: "SYS-9",
    deviceSystem: "iOS",
    locationLatitude: -32.9,
    locationLongitude: 151.7,
    locationAccuracy: 8.5,
    locationPermissionStatus: "granted",
    locationCapturedAtUtc: "2026-11-19T01:08:09.123Z",
  });
});

test("buildRespondRequestBody：可选字段缺失 / 空白 / 非有限数时不出现在请求体里，不带 isMocked", () => {
  const body = buildRespondRequestBody(
    { hardwareId: "  ", systemDeviceNumber: undefined },
    {
      locationLatitude: 1,
      locationLongitude: 2,
      locationAccuracy: Number.NaN,
      locationPermissionStatus: "granted",
      locationCapturedAtUtc: "2026-11-19T01:08:09.123Z",
    },
  );
  assert.deepEqual(Object.keys(body).sort(), ["locationCapturedAtUtc", "locationLatitude", "locationLongitude", "locationPermissionStatus"]);
  assert.equal("isMocked" in body, false);
  const noAccuracy = buildRespondRequestBody(
    {},
    { locationLatitude: 1, locationLongitude: 2, locationPermissionStatus: "granted", locationCapturedAtUtc: "2026-11-19T01:08:09.123Z" },
  );
  assert.equal("locationAccuracy" in noAccuracy, false);
});

// ───────── 会话资格 ─────────

const eligibleSnapshot: LocationRequestSessionSnapshot = {
  isAuthenticated: true,
  sessionKind: "account",
  iosReview: false,
  hasPunchSelfPermission: true,
  mustChangePassword: false,
  userGuid: " user-1 ",
};

test("resolveLocationRequestUserGuid：个人账号会话 + Attendance.Punch.Self 才有资格，其余一律 null（不发请求）", () => {
  assert.equal(resolveLocationRequestUserGuid(eligibleSnapshot), "user-1");
  const cases: Partial<LocationRequestSessionSnapshot>[] = [
    { isAuthenticated: false },
    { sessionKind: "device" },
    { sessionKind: "deviceAccount" },
    { sessionKind: "iosReview" },
    { iosReview: true },
    { hasPunchSelfPermission: false },
    { mustChangePassword: true },
    { userGuid: null },
    { userGuid: undefined },
    { userGuid: "   " },
  ];
  for (const patch of cases) {
    assert.equal(resolveLocationRequestUserGuid({ ...eligibleSnapshot, ...patch }), null, JSON.stringify(patch));
  }
});

// ───────── 触发来源 ─────────

test("触发来源：只有点推送 / 点收件箱通知是交互；刚收到推送与交互不受 15 秒拉取间隔限制", () => {
  const expectations: Record<LocationRequestTrigger, { interactive: boolean; bypass: boolean }> = {
    launch: { interactive: false, bypass: false },
    foreground: { interactive: false, bypass: false },
    "push-received": { interactive: false, bypass: true },
    "push-tap": { interactive: true, bypass: true },
    "inbox-tap": { interactive: true, bypass: true },
  };
  for (const [trigger, expected] of Object.entries(expectations) as [LocationRequestTrigger, { interactive: boolean; bypass: boolean }][]) {
    assert.equal(isInteractiveTrigger(trigger), expected.interactive, `${trigger} interactive`);
    assert.equal(bypassesFetchThrottle(trigger), expected.bypass, `${trigger} bypass`);
  }
});

// ───────── 提示 ─────────

test("buildReportedNotice：单条写请求人姓名，没有姓名用泛称，多条合并条数，没有成功的不提示", () => {
  assert.deepEqual(buildReportedNotice([request({ requestedByName: " Alex Manager " })]), { kind: "reported-named", name: "Alex Manager" });
  assert.deepEqual(buildReportedNotice([request({ requestedByName: undefined })]), { kind: "reported-generic" });
  assert.deepEqual(buildReportedNotice([request({ requestedByName: "  " })]), { kind: "reported-generic" });
  assert.deepEqual(buildReportedNotice([request({ requestGuid: "a" }), request({ requestGuid: "b" })]), { kind: "reported-many", count: 2 });
  assert.equal(buildReportedNotice([]), null);
});
