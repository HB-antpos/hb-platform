import assert from "node:assert/strict";
import test from "node:test";

import { describeDeviceRegistrationFailure } from "./device-registration-failure";

import { HbposApiError } from "@/core/api/hbpos-api";

/** 把键和参数拼成可断言的字符串，避免依赖 i18n 资源。 */
const t = (key: string, options?: Record<string, unknown>): string =>
  options ? `${key}:${JSON.stringify(options)}` : key;

test("429 限流按 Retry-After 向上取整为分钟，缺省按完整 10 分钟窗口", () => {
  assert.equal(
    describeDeviceRegistrationFailure(
      new HbposApiError("x", { kind: "http", status: 429, retryAfterSeconds: 61 }),
      t,
    ),
    'registration.error.rateLimited:{"minutes":2}',
  );
  assert.equal(
    describeDeviceRegistrationFailure(
      new HbposApiError("x", { kind: "http", status: 429, retryAfterSeconds: 5 }),
      t,
    ),
    'registration.error.rateLimited:{"minutes":1}',
  );
  assert.equal(
    describeDeviceRegistrationFailure(
      new HbposApiError("x", { kind: "http", status: 429 }),
      t,
    ),
    'registration.error.rateLimited:{"minutes":10}',
  );
});

test("已知业务拒绝码给专属提示", () => {
  const cases: Record<string, string> = {
    DEVICE_ALREADY_REGISTERED: "registration.error.deviceConflict",
    STORE_UNAVAILABLE: "registration.error.storeUnavailable",
    DEVICE_STATE_CONFLICT: "registration.error.deviceStateConflict",
    TARGET_STORE_UNCHANGED: "registration.error.targetStoreUnchanged",
    ACTIVATION_CODE_NOT_AVAILABLE: "registration.error.codeNotAvailable",
    ACTIVATION_PLATFORM_MISMATCH: "registration.error.platformMismatch",
    ACTIVATION_CODE_INVALID_FORMAT: "registration.error.invalidCode",
  };
  for (const [code, expected] of Object.entries(cases)) {
    assert.equal(
      describeDeviceRegistrationFailure(
        new HbposApiError("raw", { kind: "envelope", code }),
        t,
      ),
      expected,
    );
  }
});

test("未知原因码或无原因码的 HTTP 拒绝附带状态码与原因码", () => {
  assert.equal(
    describeDeviceRegistrationFailure(
      new HbposApiError("raw", { kind: "http", status: 500, code: "BOOM" }),
      t,
    ),
    'registration.error.failedWithReason:{"reason":"HTTP 500 · BOOM"}',
  );
  assert.equal(
    describeDeviceRegistrationFailure(
      new HbposApiError("raw", { kind: "http", status: 502 }),
      t,
    ),
    'registration.error.failedWithReason:{"reason":"HTTP 502"}',
  );
  assert.equal(
    describeDeviceRegistrationFailure(new HbposApiError("raw", { kind: "envelope" }), t),
    "registration.error.failed",
  );
});

test("传输层错误保留已翻译的原因；非 API 错误回退自身消息", () => {
  assert.equal(
    describeDeviceRegistrationFailure(
      new HbposApiError("Network unreachable", { kind: "transport" }),
      t,
    ),
    "Network unreachable",
  );
  assert.equal(describeDeviceRegistrationFailure(new Error("boom"), t), "boom");
  assert.equal(describeDeviceRegistrationFailure("weird", t), "registration.error.failed");
});
