// 收银端设置接口的错误提示：后端对业务拒绝（如非管理员改全局层的 403、校验失败的 400）都给了可直接展示的 message，
// 这里优先展示它（extractApiErrorMessage 按项目约定先取 details，再取 message）；
// 网络、超时、5xx 等没有业务文案的错误交给通用的 resolveLocalizedErrorMessage。
import { extractApiErrorMessage } from "@/shared/api/error-message";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";

type TranslateFn = (key: string, options?: Record<string, unknown>) => string;

function asRecord(value: unknown): Record<string, unknown> | null {
  return typeof value === "object" && value !== null ? (value as Record<string, unknown>) : null;
}

function responseStatus(error: unknown): number | null {
  const status = asRecord(asRecord(error)?.response)?.status;
  return typeof status === "number" ? status : null;
}

/** 业务拒绝：服务端明确回了 400 / 403 / 404 / 409，或 200 + success=false（apiBusinessError）且带文案。 */
function isBusinessRejection(error: unknown): boolean {
  if (asRecord(error)?.apiBusinessError === true) return true;
  const status = responseStatus(error);
  return status === 400 || status === 403 || status === 404 || status === 409;
}

export function resolvePosSettingsErrorMessage(
  error: unknown,
  options: { t: TranslateFn; language: string; fallbackKey?: string },
): string {
  if (isBusinessRejection(error)) {
    const raw = extractApiErrorMessage(error, "").trim();
    if (raw) return raw;
  }
  return resolveLocalizedErrorMessage(error, {
    t: options.t,
    language: options.language,
    fallbackKey: options.fallbackKey ?? "posSettings:states.loadFailed",
  });
}
