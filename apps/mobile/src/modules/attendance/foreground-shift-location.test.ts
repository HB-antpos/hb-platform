import assert from "node:assert/strict";
import { test } from "node:test";
import {
  FOREGROUND_SHIFT_LOCATION_MAX_SHIFT_MS,
  FOREGROUND_SHIFT_LOCATION_MIN_INTERVAL_MS,
  buildForegroundShiftSampleRequest,
  createForegroundShiftLocationContext,
  isShiftEndedByUploadResult,
  markForegroundShiftLocationAttempt,
  parseForegroundShiftLocationContext,
  resolveForegroundShiftLocationAction,
  type ForegroundShiftLocationContext,
} from "./foreground-shift-location";

const CLOCK_IN = "2026-11-19T08:00:00.000Z";
const CLOCK_IN_MS = Date.parse(CLOCK_IN);
const USER = "11111111-2222-3333-4444-555555555555";

function context(overrides: Partial<ForegroundShiftLocationContext> = {}): ForegroundShiftLocationContext {
  return { ownerUserId: USER, storeCode: "1002", clockedInAtUtc: CLOCK_IN, ...overrides };
}

test("上班打卡生成标记：用户统一小写、门店去空白、时间归一为 ISO", () => {
  const created = createForegroundShiftLocationContext({
    punchType: "ClockIn",
    storeCode: " 1002 ",
    serverTimeUtc: "2026-11-19T08:00:00Z",
    ownerUserId: ` ${USER.toUpperCase()} `,
  });
  assert.deepEqual(created, { ownerUserId: USER, storeCode: "1002", clockedInAtUtc: CLOCK_IN });
});

test("下班打卡、缺门店 / 用户 / 时间或时间无法解析都不生成标记（调用方据此删旧标记）", () => {
  const base = { punchType: "ClockIn", storeCode: "1002", serverTimeUtc: CLOCK_IN, ownerUserId: USER };
  assert.equal(createForegroundShiftLocationContext({ ...base, punchType: "ClockOut" }), null);
  assert.equal(createForegroundShiftLocationContext({ ...base, storeCode: "  " }), null);
  assert.equal(createForegroundShiftLocationContext({ ...base, storeCode: undefined }), null);
  assert.equal(createForegroundShiftLocationContext({ ...base, ownerUserId: undefined }), null);
  assert.equal(createForegroundShiftLocationContext({ ...base, serverTimeUtc: "not-a-date" }), null);
  assert.equal(createForegroundShiftLocationContext({ ...base, serverTimeUtc: undefined }), null);
});

test("读出存储内容：形状不对一律当作没有，合法内容原样保留（含上次尝试时间）", () => {
  assert.equal(parseForegroundShiftLocationContext(null), null);
  assert.equal(parseForegroundShiftLocationContext("x"), null);
  assert.equal(parseForegroundShiftLocationContext([]), null);
  assert.equal(parseForegroundShiftLocationContext({ storeCode: "1002", clockedInAtUtc: CLOCK_IN }), null);
  assert.equal(parseForegroundShiftLocationContext({ ownerUserId: USER, clockedInAtUtc: CLOCK_IN }), null);
  assert.equal(parseForegroundShiftLocationContext({ ownerUserId: USER, storeCode: "1002", clockedInAtUtc: "bad" }), null);
  assert.deepEqual(
    parseForegroundShiftLocationContext({
      ownerUserId: USER,
      storeCode: "1002",
      clockedInAtUtc: CLOCK_IN,
      lastAttemptAtUtc: "2026-11-19T08:30:00.000Z",
    }),
    context({ lastAttemptAtUtc: "2026-11-19T08:30:00.000Z" }),
  );
  // 上次尝试时间损坏时只丢掉这一项，不连累整份标记。
  assert.deepEqual(
    parseForegroundShiftLocationContext({ ownerUserId: USER, storeCode: "1002", clockedInAtUtc: CLOCK_IN, lastAttemptAtUtc: "bad" }),
    context(),
  );
});

test("没有标记：什么都不做", () => {
  assert.equal(resolveForegroundShiftLocationAction({ context: null, currentUserId: USER, nowMs: CLOCK_IN_MS }), "none");
});

test("上班后第一次回前台就取；换了账号登录则清掉标记而不是取", () => {
  assert.equal(
    resolveForegroundShiftLocationAction({ context: context(), currentUserId: USER, nowMs: CLOCK_IN_MS + 60_000 }),
    "capture",
  );
  assert.equal(
    resolveForegroundShiftLocationAction({ context: context(), currentUserId: "other-user", nowMs: CLOCK_IN_MS + 60_000 }),
    "clear",
  );
  assert.equal(
    resolveForegroundShiftLocationAction({ context: context(), currentUserId: undefined, nowMs: CLOCK_IN_MS + 60_000 }),
    "clear",
  );
  // 用户标识大小写不同视为同一人。
  assert.equal(
    resolveForegroundShiftLocationAction({ context: context(), currentUserId: USER.toUpperCase(), nowMs: CLOCK_IN_MS + 60_000 }),
    "capture",
  );
});

test("限频：距上次尝试不足 10 分钟不取，满 10 分钟再取", () => {
  const attemptedAt = CLOCK_IN_MS + 30 * 60_000;
  const attempted = markForegroundShiftLocationAttempt(context(), attemptedAt);
  assert.equal(attempted.lastAttemptAtUtc, new Date(attemptedAt).toISOString());

  const resolve = (afterMs: number) =>
    resolveForegroundShiftLocationAction({ context: attempted, currentUserId: USER, nowMs: attemptedAt + afterMs });
  assert.equal(resolve(0), "none");
  assert.equal(resolve(FOREGROUND_SHIFT_LOCATION_MIN_INTERVAL_MS - 1), "none");
  assert.equal(resolve(FOREGROUND_SHIFT_LOCATION_MIN_INTERVAL_MS), "capture");
});

test("上次尝试时间在「未来」（设备时钟被回拨过）时不卡死，直接允许再取", () => {
  const attempted = context({ lastAttemptAtUtc: new Date(CLOCK_IN_MS + 5 * 60 * 60_000).toISOString() });
  assert.equal(
    resolveForegroundShiftLocationAction({ context: attempted, currentUserId: USER, nowMs: CLOCK_IN_MS + 60 * 60_000 }),
    "capture",
  );
});

test("上班超过 10 小时视为失效，清掉标记；刚好差 1 毫秒还可用", () => {
  assert.equal(
    resolveForegroundShiftLocationAction({
      context: context(),
      currentUserId: USER,
      nowMs: CLOCK_IN_MS + FOREGROUND_SHIFT_LOCATION_MAX_SHIFT_MS - 1,
    }),
    "capture",
  );
  assert.equal(
    resolveForegroundShiftLocationAction({
      context: context(),
      currentUserId: USER,
      nowMs: CLOCK_IN_MS + FOREGROUND_SHIFT_LOCATION_MAX_SHIFT_MS,
    }),
    "clear",
  );
});

test("设备时钟比上班时间早 5 分钟以内容忍；更早说明时钟明显不对，本次不取也不清", () => {
  assert.equal(
    resolveForegroundShiftLocationAction({ context: context(), currentUserId: USER, nowMs: CLOCK_IN_MS - 4 * 60_000 }),
    "capture",
  );
  assert.equal(
    resolveForegroundShiftLocationAction({ context: context(), currentUserId: USER, nowMs: CLOCK_IN_MS - 6 * 60_000 }),
    "none",
  );
});

test("构造上传请求：只有一个点、事件类型为班中样本、门店与精度原样带上、不带设备字段也合法", () => {
  const request = buildForegroundShiftSampleRequest({
    context: context(),
    fix: {
      locationLatitude: -27.365316,
      locationLongitude: 152.995174,
      locationAccuracy: 11,
      locationCapturedAtUtc: "2026-11-19T09:00:00.000Z",
    },
    clientSampleId: "sample-1",
  });
  assert.deepEqual(request, {
    storeCode: "1002",
    samples: [
      {
        clientSampleId: "sample-1",
        eventType: "ShiftSample",
        locationLatitude: -27.365316,
        locationLongitude: 152.995174,
        locationAccuracy: 11,
        locationPermissionStatus: "granted",
        locationCapturedAtUtc: "2026-11-19T09:00:00.000Z",
      },
    ],
  });
});

test("构造上传请求：没有精度时不带 locationAccuracy 字段", () => {
  const request = buildForegroundShiftSampleRequest({
    context: context(),
    fix: { locationLatitude: 1, locationLongitude: 2, locationCapturedAtUtc: "2026-11-19T09:00:00.000Z" },
    clientSampleId: "sample-2",
  });
  assert.equal("locationAccuracy" in request.samples[0], false);
});

test("上传结果：只有本次样本被报 ACTIVE_SHIFT_REQUIRED 才算班次已结束，其他拒绝 / 成功都不清标记", () => {
  const ended = {
    accepted: [],
    duplicates: [],
    rejected: [{ clientSampleId: "s", errorCode: "ACTIVE_SHIFT_REQUIRED", message: "" }],
  };
  assert.equal(isShiftEndedByUploadResult(ended, "s"), true);
  assert.equal(isShiftEndedByUploadResult(ended, "another"), false);
  assert.equal(
    isShiftEndedByUploadResult(
      { accepted: [], duplicates: [], rejected: [{ clientSampleId: "s", errorCode: "SAMPLE_TOO_OLD", message: "" }] },
      "s",
    ),
    false,
  );
  assert.equal(isShiftEndedByUploadResult({ accepted: ["s"], duplicates: [], rejected: [] }, "s"), false);
});
