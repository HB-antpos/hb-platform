import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { readPickingError } from "./api-normalization";

const KNOWN_ERROR_CODES = new Set([
  "PICKER_REQUIRED",
  "PICKER_TICKET_INVALID",
  "PICKER_TICKET_EXPIRED",
  "PICKER_BARCODE_NOT_FOUND",
  "PICKER_BARCODE_AMBIGUOUS",
  "PICKER_NOT_ALLOWED",
  "ORDER_NOT_FOUND",
  "ORDER_NOT_PICKABLE",
  "SESSION_NOT_STARTED",
  "SESSION_SUBMITTED",
  "LINE_NOT_FOUND",
  "MIN_ORDER_QUANTITY_MISSING",
  "MIN_ORDER_QUANTITY_ALREADY_SET",
  "MIN_ORDER_QUANTITY_INVALID",
  "PICKED_BELOW_ZERO",
  "PICKED_TOTAL_CHANGED",
  "CODE_NOT_FOUND",
  "LINE_ALREADY_COMPLETE",
  "SLIP_STALE",
  "INVALID_REQUEST",
]);

/** 拣货人需要重新确认的错误：凭证失效或员工已不能拣货。 */
export const PICKER_RECONFIRM_CODES = new Set([
  "PICKER_REQUIRED",
  "PICKER_TICKET_INVALID",
  "PICKER_TICKET_EXPIRED",
  "PICKER_NOT_ALLOWED",
]);

type Translate = (key: string, options?: Record<string, unknown>) => string;

/** 业务错误码优先映射本地文案（中英一致），其余交给通用错误本地化。 */
export function pickingErrorMessage(error: unknown, t: Translate, language: string) {
  const { code } = readPickingError(error);
  if (code && KNOWN_ERROR_CODES.has(code)) {
    return t(`errors.${code}`);
  }
  return resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "errors.generic" });
}
