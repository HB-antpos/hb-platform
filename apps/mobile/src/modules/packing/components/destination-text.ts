import { destinationLabel, sameCode } from "../packing-logic";
import { DESTINATION_TYPE, WAREHOUSE_OWNER_CODE, type PackDestination } from "../types";

type Translate = (key: string, options?: Record<string, unknown>) => string;

/**
 * 去向的一行文字：本地存放（总仓叫「仓内存放」）、未指定、或要发出的收货方名称；
 * 交运输公司时在后面加公司名，如「Inala 分店 · Border Express」。
 */
export function destinationText(destination: PackDestination, t: Translate, ownerCode?: string | null): string {
  if (destination.destinationType === DESTINATION_TYPE.local) {
    return sameCode(ownerCode, WAREHOUSE_OWNER_CODE) ? t("destination.localWarehouse") : t("destination.local");
  }
  if (destination.destinationType === DESTINATION_TYPE.unassigned) {
    return t("destination.unassigned");
  }
  const target = destinationLabel(destination) ?? t("destination.unknownTarget");
  return destination.carrierName ? t("destination.targetVia", { target, carrier: destination.carrierName }) : target;
}
