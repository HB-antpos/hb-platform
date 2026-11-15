import { printPackCartonLabel, printPackPalletLabel, printWarehouseLocationLabel } from "@/modules/printer/api";
import { packPrintType } from "./pack-preferences";
import {
  buildBlankCartonLabelPayload,
  buildCartonLabelPayload,
  buildPalletLabelPayload,
  isUnusedBlank,
} from "./packing-logic";
import type { PackCartonDetail, PackCartonSummary, PackLocation, PackPalletDetail } from "./types";

export {
  PACKING_BATCH_LIST,
  PACKING_BATCH_NEW,
  PACKING_HOME,
  PACKING_LOCATE,
  PACKING_RECEIVE,
  batchRoute,
  cartonRoute,
  palletRoute,
  receiveRoute,
} from "./pack-routes";

/**
 * 打印箱标签：内容取服务端最新的箱数据，时间用本机当前时间。
 * 还没启用的空箱标按空箱标样式（无 SKU / PCS、LABEL i / n），其余（含已启用的预打印箱）按内容标；
 * 空箱标的操作人用打印的这个人（operatorName）。
 */
export function printCartonLabel(
  carton: PackCartonDetail | PackCartonSummary,
  copies: number,
  smallLabel: boolean,
  operatorName?: string | null,
) {
  const now = new Date();
  const payload = isUnusedBlank(carton)
    ? buildBlankCartonLabelPayload(carton, now, operatorName)
    : buildCartonLabelPayload(carton, now);
  return printPackCartonLabel(payload, { printType: packPrintType(smallLabel), copies });
}

/** 打印托盘标签：copies 张，每张带 1/N。 */
export function printPalletLabel(pallet: PackPalletDetail, copies: number, smallLabel: boolean) {
  return printPackPalletLabel(buildPalletLabelPayload(pallet, new Date()), {
    printType: packPrintType(smallLabel),
    copies,
  });
}

/** 打印存放位置标签：沿用仓库货位标签（位置名称大字 + 位置条码 Code128）。 */
export function printLocationLabel(location: Pick<PackLocation, "locationGuid" | "name" | "barcode">) {
  return printWarehouseLocationLabel({
    locationGuid: location.locationGuid,
    locationCode: location.name,
    locationBarcode: location.barcode,
    productCount: 0,
  });
}
