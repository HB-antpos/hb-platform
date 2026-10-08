/**
 * 批量填报的业务错误码 → 文案键。
 * apiClient 遇到 HTTP 200 + success=false 会抛 Error，并把 errorCode 放在 error.code 上。
 * 注意 SEASONAL_CARD_STALE 的 details 是最新批次对象，通用错误提取会把它的字段拼成一串当文案，
 * 所以这些错误码一律用本地文案，不能显示原始 message。
 */
export const SEASONAL_CARD_STALE = "SEASONAL_CARD_STALE";
export const SEASONAL_CARD_NO_CHANGES = "SEASONAL_CARD_NO_CHANGES";
/** 节日不在开放窗口内（节日当天起 4 周）或年份不是窗口对应年份。 */
export const SEASONAL_CARD_WINDOW_CLOSED = "SEASONAL_CARD_WINDOW_CLOSED";

const ERROR_KEY_BY_CODE: Record<string, string> = {
  [SEASONAL_CARD_STALE]: "messages.staleSnackbar",
  [SEASONAL_CARD_NO_CHANGES]: "errors.noChanges",
  [SEASONAL_CARD_WINDOW_CLOSED]: "errors.windowClosed",
  BATCH_ITEMS_MISMATCH: "errors.catalogChanged",
  BATCH_ITEMS_REQUIRED: "errors.catalogChanged",
  SUPPLIER_NOT_FOUND: "errors.supplierNotFound",
  SUPPLIER_REQUIRED: "errors.supplierRequired",
  CUSTOM_PRICE_REQUIRED: "errors.customUnitPrice",
  INVALID_QUANTITY: "errors.remainingQuantity",
  FORBIDDEN_STORE: "errors.forbiddenStore",
};

export function getSeasonalCardErrorCode(error: unknown): string | null {
  if (!error || typeof error !== "object") {
    return null;
  }
  const code = (error as { code?: unknown }).code;
  return typeof code === "string" && code.trim() ? code.trim() : null;
}

/** 已知业务错误码返回本模块的文案键；未知错误返回 null，由调用方走通用错误提示。 */
export function getSeasonalCardSubmitErrorKey(error: unknown): string | null {
  const code = getSeasonalCardErrorCode(error);
  return code ? ERROR_KEY_BY_CODE[code] ?? null : null;
}
