// 现金接口错误的提示解析：优先按后端业务错误码（CASH_*）映射文案，
// 其余（网络、超时、登录失效等）交给通用的 resolveLocalizedErrorMessage。
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { extractApiErrorMessage } from "@/shared/api/error-message";
import { CASH_ERROR_CODES, type CashErrorCode } from "./constants";

type TranslateFn = (key: string, options?: Record<string, unknown>) => string;

function asRecord(value: unknown): Record<string, unknown> | null {
  return typeof value === "object" && value !== null ? (value as Record<string, unknown>) : null;
}

/**
 * 取出错误里的业务错误码：
 * - success=false 的 200 响应由 unwrapApiEnvelope 抛出，错误码在 error.code；
 * - HTTP 400/403/404/409 走 axios 错误，错误码在 response.data.errorCode。
 */
export function extractCashErrorCode(error: unknown): string | null {
  const record = asRecord(error);
  if (!record) return null;
  const direct = record.code ?? record.errorCode;
  if (typeof direct === "string" && direct.trim()) return direct.trim();
  const body = asRecord(asRecord(record.response)?.data);
  const fromBody = body?.errorCode ?? body?.ErrorCode;
  return typeof fromBody === "string" && fromBody.trim() ? fromBody.trim() : null;
}

export function isKnownCashErrorCode(code: string | null): code is CashErrorCode {
  return code != null && (CASH_ERROR_CODES as readonly string[]).includes(code);
}

/** 服务端要求填写存款差异原因（HTTP 400 + CASH_OVERRIDE_REASON_REQUIRED）：表单要显示并聚焦原因输入框。 */
export function isOverrideReasonRequiredError(error: unknown): boolean {
  return extractCashErrorCode(error) === "CASH_OVERRIDE_REASON_REQUIRED";
}

/** 附件失效（签名过期、待确认附件被清理等）：本地图片还在，下次提交重新上传即可。 */
export function isAttachmentInvalidError(error: unknown): boolean {
  return extractCashErrorCode(error) === "CASH_ATTACHMENT_INVALID";
}

/** 记录不存在或无权查看（HTTP 404）。 */
export function isRecordNotFoundError(error: unknown): boolean {
  const code = extractCashErrorCode(error);
  return code === "CASH_RECORD_NOT_FOUND" || code === "CASH_STORE_NOT_FOUND";
}

/** 错误码对应的 i18n 键（storeCash 命名空间）。 */
export function cashErrorMessageKey(code: CashErrorCode): string {
  return `errors.codes.${code}`;
}

export function resolveCashErrorMessage(
  error: unknown,
  options: { t: TranslateFn; language: string; fallbackKey?: string },
): string {
  const { t, language } = options;
  const code = extractCashErrorCode(error);
  if (isKnownCashErrorCode(code)) {
    const raw = extractApiErrorMessage(error, "").trim();
    // 后端 message 是可直接展示的中文，中文界面优先用它（带具体数值），英文界面用映射文案。
    if (language.toLowerCase().startsWith("zh") && raw) return raw;
    return t(`storeCash:${cashErrorMessageKey(code)}`);
  }
  return resolveLocalizedErrorMessage(error, {
    t,
    language,
    fallbackKey: options.fallbackKey ?? "storeCash:errors.generic",
  });
}
