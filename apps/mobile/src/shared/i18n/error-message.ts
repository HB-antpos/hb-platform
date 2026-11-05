import { isAxiosError } from "axios";

type TranslationOptions = Record<string, unknown>;

type TranslateFn = (key: string, options?: TranslationOptions) => string;

type ResolveLocalizedErrorOptions = {
  language: string;
  t: TranslateFn;
  fallbackKey?: string;
  fallbackText?: string;
  allowRawMessageInChinese?: boolean;
};

type ApiErrorBody = {
  message?: unknown;
  Message?: unknown;
  error?: unknown;
  Error?: unknown;
};

function asRecord(value: unknown): Record<string, unknown> | null {
  return typeof value === "object" && value !== null ? (value as Record<string, unknown>) : null;
}

function asString(value: unknown) {
  return typeof value === "string" ? value.trim() : "";
}

function getStatus(error: unknown) {
  if (isAxiosError(error)) {
    return error.response?.status;
  }

  const record = asRecord(error);
  const response = asRecord(record?.response);
  return typeof response?.status === "number" ? response.status : undefined;
}

function getCode(error: unknown) {
  if (isAxiosError(error)) {
    return error.code;
  }

  const record = asRecord(error);
  return typeof record?.code === "string" ? record.code : undefined;
}

function getMessage(error: unknown): string {
  if (isAxiosError<ApiErrorBody>(error)) {
    return asString(
      error.response?.data?.message
        ?? error.response?.data?.Message
        ?? error.response?.data?.error
        ?? error.response?.data?.Error
        ?? error.message
    );
  }

  if (error instanceof Error) {
    return error.message.trim();
  }

  const record = asRecord(error);
  return asString(record?.message ?? record?.Message ?? record?.error ?? record?.Error);
}

function includesAny(message: string, needles: string[]) {
  const normalized = message.toLowerCase();
  return needles.some((needle) => normalized.includes(needle.toLowerCase()));
}

function isChineseLanguage(language: string) {
  return language.toLowerCase().startsWith("zh");
}

function resolveCommonErrorKey(error: unknown) {
  const status = getStatus(error);
  const code = getCode(error);
  const message = getMessage(error);

  const printerPairingErrorKey = code ? {
    PRINTER_BLE_UNSUPPORTED: "common:errors.printerBleUnsupported",
    PRINTER_PAIRING_REQUIRED: "common:errors.printerPairingRequired",
    PRINTER_PAIRING_START_FAILED: "common:errors.printerPairingStartFailed",
    PRINTER_PAIRING_REJECTED: "common:errors.printerPairingRejected",
    PRINTER_PAIRING_CANCELLED: "common:errors.printerPairingRejected",
    PRINTER_PAIRING_TIMEOUT: "common:errors.printerPairingTimeout",
    PRINTER_PAIRING_UNAVAILABLE: "common:errors.printerPairingUnavailable",
  }[code] : undefined;
  if (printerPairingErrorKey) {
    return printerPairingErrorKey;
  }

  if (code === "CONNECT_ERROR" && includesAny(message, ["timeout", "timed out", "超时"])) {
    return "common:errors.printerConnectTimeout";
  }

  // 只识别原生打印错误，避免把网络请求中的同类 socket 错误误报为打印机断线。
  if (
    code
    && [
      "PRINT_ERROR",
      "PRINT_PRODUCT_LABEL_ERROR",
      "PRINT_DISCOUNT_LABEL_ERROR",
      "PRINT_CLEARANCE_LABEL_ERROR",
      "PRINT_BIG_DISCOUNT_LABEL_ERROR",
      "PRINT_WAREHOUSE_PRODUCT_LABEL_ERROR",
      "PRINT_WAREHOUSE_LOCATION_LABEL_ERROR",
    ].includes(code)
    && includesAny(message, [
      "broken pipe",
      "EPIPE",
      "socket closed",
      "socket is closed",
      "connection reset",
      "No Bluetooth printer is connected",
    ])
  ) {
    return "common:errors.printerDisconnected";
  }

  if (code === "ECONNABORTED" || code === "ETIMEDOUT" || includesAny(message, ["timeout", "timed out", "超时"])) {
    return "common:errors.timeout";
  }

  if (message === "Network Error" || code === "ERR_NETWORK") {
    return "common:errors.network";
  }

  if (
    status === 401
    || status === 403
    || includesAny(message, [
      "未登录",
      "请登录",
      "登录已过期",
      "登陆已过期",
      "登录失效",
      "登陆失效",
      "unauthorized",
      "unauthenticated",
      "forbidden",
      "token expired",
      "session expired",
      "no refresh token",
      // 不能用裸词 auth：错误信息里常带接口路径，/auth/... 会被误判成登录失效，盖住真实原因。
      "authenticat",
      "authoriz",
      "authoris",
      "设备未授权",
      "设备授权失败",
    ])
  ) {
    return "common:errors.unauthorized";
  }

  if (status && status >= 500) {
    return "common:errors.server";
  }

  if (includesAny(message, ["request failed"])) {
    return "common:errors.requestFailed";
  }

  return null;
}

export function resolveLocalizedErrorMessage(
  error: unknown,
  options: ResolveLocalizedErrorOptions
) {
  const commonErrorKey = resolveCommonErrorKey(error);
  if (commonErrorKey) {
    return options.t(commonErrorKey);
  }

  const rawMessage = getMessage(error);
  if (rawMessage && options.allowRawMessageInChinese !== false && isChineseLanguage(options.language)) {
    return rawMessage;
  }

  if (options.fallbackText) {
    return options.fallbackText;
  }

  if (options.fallbackKey) {
    return options.t(options.fallbackKey);
  }

  return options.t("common:errors.requestFailed");
}
