import assert from "node:assert/strict";
import {
  cashErrorMessageKey,
  extractCashErrorCode,
  isAttachmentInvalidError,
  isKnownCashErrorCode,
  isOverrideReasonRequiredError,
  isRecordNotFoundError,
  resolveCashErrorMessage,
} from "./errors";
import { CASH_ERROR_CODES } from "./constants";

const t = (key: string) => `T(${key})`;

// ───────── 错误码提取：success=false 的 200 响应与 HTTP 4xx 两条路径 ─────────
assert.equal(extractCashErrorCode(Object.assign(new Error("x"), { code: "CASH_DATE_OUT_OF_RANGE" })), "CASH_DATE_OUT_OF_RANGE");
assert.equal(
  extractCashErrorCode({ response: { status: 400, data: { success: false, message: "m", errorCode: "CASH_CONFLICT" } } }),
  "CASH_CONFLICT",
);
assert.equal(extractCashErrorCode({ response: { data: { ErrorCode: "CASH_VOID_NOT_ALLOWED" } } }), "CASH_VOID_NOT_ALLOWED");
assert.equal(extractCashErrorCode(new Error("plain")), null);
assert.equal(extractCashErrorCode(null), null);
assert.equal(extractCashErrorCode("str"), null);
assert.equal(isKnownCashErrorCode("CASH_CONFLICT"), true);
assert.equal(isKnownCashErrorCode("CASH_UNKNOWN"), false);
assert.equal(isKnownCashErrorCode(null), false);
assert.equal(CASH_ERROR_CODES.length, 13, "与后端 StoreCashConstants.ErrorCodes 个数一致");
assert.equal(cashErrorMessageKey("CASH_CONFLICT"), "errors.codes.CASH_CONFLICT");

// ───────── 与业务流程有关的错误码判断 ─────────
const overrideError = {
  response: { status: 400, data: { success: false, message: "请填写差异原因", errorCode: "CASH_OVERRIDE_REASON_REQUIRED" } },
};
assert.equal(isOverrideReasonRequiredError(overrideError), true, "服务端要求差异原因时表单要显示并聚焦输入框");
assert.equal(isOverrideReasonRequiredError(Object.assign(new Error("x"), { code: "CASH_OVERRIDE_REASON_REQUIRED" })), true);
assert.equal(isOverrideReasonRequiredError({ response: { data: { errorCode: "CASH_CONFLICT" } } }), false);
assert.equal(isOverrideReasonRequiredError(new Error("x")), false);
assert.equal(isAttachmentInvalidError({ response: { status: 400, data: { errorCode: "CASH_ATTACHMENT_INVALID" } } }), true);
assert.equal(isAttachmentInvalidError({ response: { data: { errorCode: "CASH_ATTACHMENT_REQUIRED" } } }), false);
assert.equal(isRecordNotFoundError({ response: { status: 404, data: { errorCode: "CASH_RECORD_NOT_FOUND" } } }), true);
assert.equal(isRecordNotFoundError({ response: { status: 404, data: { errorCode: "CASH_STORE_NOT_FOUND" } } }), true);
assert.equal(isRecordNotFoundError({ response: { status: 403, data: { errorCode: "CASH_STORE_FORBIDDEN" } } }), false);

// ───────── 文案：中文界面优先用后端 message（带具体数值），英文界面用映射文案 ─────────
const businessError = {
  response: { status: 400, data: { success: false, message: "存款日期超出可补录范围（最多回溯 7 天）", errorCode: "CASH_DATE_OUT_OF_RANGE" } },
  message: "存款日期超出可补录范围（最多回溯 7 天）",
};
assert.equal(
  resolveCashErrorMessage(businessError, { t, language: "zh" }),
  "存款日期超出可补录范围（最多回溯 7 天）",
);
assert.equal(
  resolveCashErrorMessage(businessError, { t, language: "en" }),
  "T(storeCash:errors.codes.CASH_DATE_OUT_OF_RANGE)",
);
// 403 的业务码不会被通用的「登录失效」文案盖住
assert.equal(
  resolveCashErrorMessage(
    { response: { status: 403, data: { errorCode: "CASH_STORE_FORBIDDEN", message: "无权操作该分店" } }, message: "无权操作该分店" },
    { t, language: "en" },
  ),
  "T(storeCash:errors.codes.CASH_STORE_FORBIDDEN)",
);
// 后端把错误码映射到 HTTP 403 / 404 / 409 / 400：响应体仍是 ApiResponse，中文界面直接展示其中的 message
for (const [status, code] of [
  [403, "CASH_STORE_FORBIDDEN"],
  [403, "CASH_VOID_NOT_ALLOWED"],
  [404, "CASH_RECORD_NOT_FOUND"],
  [404, "CASH_CLOSE_NOT_FOUND"],
  [409, "CASH_CONFLICT"],
  [409, "CASH_OPENING_EXISTS"],
  [400, "CASH_INVALID_REQUEST"],
] as const) {
  const failure = { response: { status, data: { success: false, message: `消息-${code}`, errorCode: code } }, message: `消息-${code}` };
  assert.equal(resolveCashErrorMessage(failure, { t, language: "zh" }), `消息-${code}`, `${status} ${code} 直接展示后端 message`);
  assert.equal(
    resolveCashErrorMessage(failure, { t, language: "en" }),
    `T(storeCash:errors.codes.${code})`,
    `${status} ${code} 英文界面用映射文案`,
  );
}
// 没有业务码时回退到通用解析：网络错误走通用文案，其余走模块兜底
assert.equal(
  resolveCashErrorMessage(Object.assign(new Error("Network Error"), { code: "ERR_NETWORK" }), { t, language: "en" }),
  "T(common:errors.network)",
);
assert.equal(
  resolveCashErrorMessage(new Error("boom"), { t, language: "en" }),
  "T(storeCash:errors.generic)",
);

console.log("errors.test.ts: ok");
