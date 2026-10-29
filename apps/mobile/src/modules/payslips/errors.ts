// 工资单接口 / 下载 / 发信的错误提示解析：业务错误码 → 本地化文案；不认识的错误码走通用解析。
// 下载 PDF 用 arraybuffer，失败响应体也是二进制 JSON，所以错误码要先解码再读（沿用人脸比对模块的解码工具）。
import { decodeBinaryJsonBody, extractHttpStatus } from "@/modules/face-match/errors";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";

type TranslateFn = (key: string, options?: Record<string, unknown>) => string;

/** 页面已知的业务错误码（后端 MYOB_PAYSLIP_*；PAYSLIP_* 是客户端自己抛的）。 */
export const PAYSLIP_ERROR_CODES = [
  "MYOB_PAYSLIP_FORBIDDEN",
  "MYOB_PAYSLIP_RECIPIENT_UNCONFIRMED",
  "MYOB_PAYSLIP_EMAIL_INVALID",
  "MYOB_PAYSLIP_EMAIL_NOT_CONFIGURED",
  "MYOB_PAYSLIP_EMAIL_SEND_FAILED",
  "PAYSLIP_PDF_INVALID",
  "PAYSLIP_SHARING_UNAVAILABLE",
  "PAYSLIP_INVALID_RESPONSE",
] as const;

export type PayslipErrorCode = (typeof PAYSLIP_ERROR_CODES)[number];

function asRecord(value: unknown): Record<string, unknown> | null {
  return typeof value === "object" && value !== null ? (value as Record<string, unknown>) : null;
}

/** 取业务错误码：HTTP 错误在 response.data.errorCode（可能是二进制），success:false 的 200 在 error.code。 */
export function extractPayslipErrorCode(error: unknown): string | null {
  const response = asRecord(asRecord(error)?.response);
  const body = response ? decodeBinaryJsonBody(response.data) : null;
  const fromBody = body?.errorCode ?? body?.ErrorCode ?? body?.code ?? body?.Code;
  if (typeof fromBody === "string" && fromBody.trim()) return fromBody.trim();
  const direct = asRecord(error)?.errorCode ?? asRecord(error)?.code;
  return typeof direct === "string" && direct.trim() ? direct.trim() : null;
}

export function isKnownPayslipErrorCode(code: string | null): code is PayslipErrorCode {
  return code !== null && (PAYSLIP_ERROR_CODES as readonly string[]).includes(code);
}

/** 无权限（业务码或 HTTP 403）：页面据此隐藏操作或提示联系管理员。 */
export function isPayslipForbidden(error: unknown): boolean {
  return extractPayslipErrorCode(error) === "MYOB_PAYSLIP_FORBIDDEN" || extractHttpStatus(error) === 403;
}

/** 错误 → 文案键（payslips 命名空间）：已知码优先，其次 403 / 404，其余 null 交给通用解析。 */
export function classifyPayslipError(error: unknown): string | null {
  const code = extractPayslipErrorCode(error);
  if (isKnownPayslipErrorCode(code)) return `errors.codes.${code}`;
  const status = extractHttpStatus(error);
  if (status === 403) return "errors.codes.MYOB_PAYSLIP_FORBIDDEN";
  if (status === 404) return "errors.notFound";
  return null;
}

/** 错误 → 面向用户的文案（payslips 命名空间的 t）。 */
export function describePayslipError(t: TranslateFn, language: string, error: unknown): string {
  const key = classifyPayslipError(error);
  if (key) return t(key);
  // 中文界面允许显示后端给出的中文原因，英文界面一律用通用文案。
  return resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "errors.generic" });
}
