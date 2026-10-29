import assert from "node:assert/strict";
import { classifyPayslipError, describePayslipError, extractPayslipErrorCode, isPayslipForbidden } from "./errors";

function ascii(text: string): ArrayBuffer {
  const bytes = new Uint8Array(text.length);
  for (let index = 0; index < text.length; index += 1) bytes[index] = text.charCodeAt(index);
  return bytes.buffer;
}

// HTTP 错误：JSON 体可能是二进制（PDF 下载用 arraybuffer）
const forbiddenBinary = { response: { status: 403, data: ascii('{"success":false,"errorCode":"MYOB_PAYSLIP_FORBIDDEN","message":"x"}') } };
assert.equal(extractPayslipErrorCode(forbiddenBinary), "MYOB_PAYSLIP_FORBIDDEN");
assert.equal(isPayslipForbidden(forbiddenBinary), true);
assert.equal(classifyPayslipError(forbiddenBinary), "errors.codes.MYOB_PAYSLIP_FORBIDDEN");

// 普通 JSON 响应体
const unconfirmed = { response: { status: 400, data: { errorCode: "MYOB_PAYSLIP_RECIPIENT_UNCONFIRMED" } } };
assert.equal(classifyPayslipError(unconfirmed), "errors.codes.MYOB_PAYSLIP_RECIPIENT_UNCONFIRMED");
for (const code of ["MYOB_PAYSLIP_EMAIL_INVALID", "MYOB_PAYSLIP_EMAIL_NOT_CONFIGURED", "MYOB_PAYSLIP_EMAIL_SEND_FAILED"]) {
  assert.equal(classifyPayslipError({ response: { status: 400, data: { errorCode: code } } }), `errors.codes.${code}`);
}

// success:false 的 200 由 unwrapApiEnvelope 抛出，错误码在 error.code
assert.equal(classifyPayslipError(Object.assign(new Error("x"), { code: "MYOB_PAYSLIP_EMAIL_SEND_FAILED" })), "errors.codes.MYOB_PAYSLIP_EMAIL_SEND_FAILED");
// 客户端自己的错误码
assert.equal(classifyPayslipError(Object.assign(new Error("x"), { code: "PAYSLIP_PDF_INVALID" })), "errors.codes.PAYSLIP_PDF_INVALID");

// 状态码兜底：403 → 无权限，404 → 找不到；其余交给通用解析
assert.equal(classifyPayslipError({ response: { status: 403, data: {} } }), "errors.codes.MYOB_PAYSLIP_FORBIDDEN");
assert.equal(classifyPayslipError({ response: { status: 404, data: {} } }), "errors.notFound");
assert.equal(classifyPayslipError({ response: { status: 500, data: {} } }), null);
assert.equal(classifyPayslipError(new Error("boom")), null);
assert.equal(isPayslipForbidden({ response: { status: 500, data: {} } }), false);

// 文案：已知码用本地化文案；未知错误中文可显示后端原因，英文用通用文案
const t = (key: string) => `T(${key})`;
assert.equal(describePayslipError(t, "zh", forbiddenBinary), "T(errors.codes.MYOB_PAYSLIP_FORBIDDEN)");
const unknown = { response: { status: 422, data: { message: "对账失败" } }, message: "对账失败" };
assert.equal(describePayslipError(t, "zh", unknown), "对账失败");
assert.equal(describePayslipError(t, "en", unknown), "T(errors.generic)");
