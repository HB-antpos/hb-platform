import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { readPackingError } from "./api-normalization";

const KNOWN_ERROR_CODES = new Set([
  "INVALID_REQUEST",
  "PACKING_NOT_ALLOWED",
  "OWNER_REQUIRED",
  "OWNER_NOT_ALLOWED",
  "CARTON_NOT_FOUND",
  "PALLET_NOT_FOUND",
  "CODE_NOT_FOUND",
  "CODE_AMBIGUOUS",
  "CARTON_NOT_PACKING",
  "CARTON_NOT_SEALED",
  "CARTON_EMPTY",
  "CARTON_ON_PALLET",
  "CARTON_ON_OTHER_PALLET",
  "CARTON_IN_TRANSIT",
  "DESTINATION_MISMATCH",
  "DESTINATION_INVALID",
  "PALLET_NOT_BUILDING",
  "PALLET_NOT_SEALED",
  "PALLET_EMPTY",
  "PALLET_NOT_EMPTY",
  "PALLET_IN_TRANSIT",
  "ORDER_NOT_FOUND",
  "LOCATION_NOT_FOUND",
  "LOCATION_NAME_TAKEN",
  "CARRIER_NOT_FOUND",
  "CARRIER_NAME_TAKEN",
  "QUANTITY_OUT_OF_RANGE",
  "NOT_SHIPPABLE",
  "NOT_IN_TRANSIT",
  "NOT_FOR_YOU",
  "COUNT_MISMATCH",
  "BATCH_NOT_FOUND",
  "CARTON_NOT_BLANK",
]);

type Translate = (key: string, options?: Record<string, unknown>) => string;

/** 业务错误码优先映射本地文案（中英一致），其余交给通用错误本地化。 */
export function packingErrorMessage(error: unknown, t: Translate, language: string) {
  const { code, blankLimit } = readPackingError(error);
  if (code === "BLANK_LIMIT_EXCEEDED") {
    // 带着已有张数 / 上限时说清楚还能建几张；没带就用不含数字的通用说法。
    return blankLimit
      ? t("errors.BLANK_LIMIT_EXCEEDED", { existing: blankLimit.existing, limit: blankLimit.limit, remaining: Math.max(0, blankLimit.limit - blankLimit.existing) })
      : t("errors.BLANK_LIMIT_EXCEEDED_PLAIN");
  }
  if (code && KNOWN_ERROR_CODES.has(code)) {
    return t(`errors.${code}`);
  }
  return resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "errors.generic" });
}

/** 打印失败的提示：打印模块抛的是英文技术信息，这里只取一句给用户，主文案由调用方决定。 */
export function printErrorDetail(error: unknown) {
  const message = error instanceof Error ? error.message : String(error ?? "");
  return message.trim().slice(0, 120);
}
