import { destinationStoreLabel, isCarrierDestination } from "../packing-logic";
import type { PackDestination } from "../types";

type Translate = (key: string, options?: Record<string, unknown>) => string;

/** 去向的一行文字：分店名；运输公司 → 收货分店；未指定。 */
export function destinationText(destination: PackDestination, t: Translate): string {
  const store = destinationStoreLabel(destination);
  if (isCarrierDestination(destination)) {
    const carrier = destination.carrierName?.trim() || t("destination.typeCarrier");
    return store ? t("destination.carrierTo", { carrier, store }) : carrier;
  }
  return store ?? t("destination.unassigned");
}
