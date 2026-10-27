import { HbposApiError } from "@/core/api/hbpos-api";

/** 与 react-i18next 的 t 兼容的最小翻译函数，便于脱离 React 单测。 */
export type RegistrationTranslate = {
  (key: string): string;
  (key: string, options: Record<string, unknown>): string;
};

/** 服务端未返回 Retry-After 时按激活限流的完整窗口（10 分钟）提示。 */
const DEFAULT_RATE_LIMIT_WINDOW_SECONDS = 10 * 60;

/** 服务端激活拒绝原因码 → 文案键（对齐 WPF DeviceRegistrationViewModel.ActivationErrorResourceKey）。 */
const ACTIVATION_ERROR_KEYS: Readonly<Record<string, string>> = {
  ACTIVATION_CODE_INVALID_FORMAT: "registration.error.invalidCode",
  ACTIVATION_CODE_NOT_AVAILABLE: "registration.error.codeNotAvailable",
  ACTIVATION_PLATFORM_MISMATCH: "registration.error.platformMismatch",
  DEVICE_ALREADY_REGISTERED: "registration.error.deviceConflict",
  STORE_UNAVAILABLE: "registration.error.storeUnavailable",
  DEVICE_STATE_CONFLICT: "registration.error.deviceStateConflict",
  TARGET_STORE_UNCHANGED: "registration.error.targetStoreUnchanged",
};

/**
 * 设备激活失败的可读说明（对齐 WPF #915）：
 * - 429 限流：提示需要等待的分钟数，而不是“检测网络”（此时网络是通的）；
 * - 已知业务拒绝码：专属中英文提示；
 * - 其它 HTTP 拒绝：通用失败文案后附「HTTP 状态码 · 原因码」，现场能直接报给我们；
 * - 无 HTTP 响应（断网/超时等）：保留传输层已翻译好的原因，不加状态码。
 */
export function describeDeviceRegistrationFailure(
  error: unknown,
  t: RegistrationTranslate,
): string {
  if (!(error instanceof HbposApiError)) {
    return error instanceof Error && error.message
      ? error.message
      : t("registration.error.failed");
  }
  if (error.kind === "transport") return error.message;

  if (error.status === 429) {
    const seconds = error.retryAfterSeconds ?? DEFAULT_RATE_LIMIT_WINDOW_SECONDS;
    return t("registration.error.rateLimited", {
      minutes: Math.max(1, Math.ceil(seconds / 60)),
    });
  }

  const key = error.code ? ACTIVATION_ERROR_KEYS[error.code] : undefined;
  if (key) return t(key);

  const reason = [
    error.status !== undefined ? `HTTP ${error.status}` : null,
    error.code ?? null,
  ].filter((part): part is string => part !== null);
  return reason.length > 0
    ? t("registration.error.failedWithReason", { reason: reason.join(" · ") })
    : t("registration.error.failed");
}
