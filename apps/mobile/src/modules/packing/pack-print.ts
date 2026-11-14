import type { Href } from "expo-router";
import { printPackCartonLabel, printPackPalletLabel, printWarehouseLocationLabel } from "@/modules/printer/api";
import { packPrintType } from "./pack-preferences";
import { buildCartonLabelPayload, buildPalletLabelPayload } from "./packing-logic";
import type { PackCartonDetail, PackLocation, PackPalletDetail } from "./types";

/** 打包入口（首页）。 */
export const PACKING_HOME = "/(shell)/packing" as Href;
export const PACKING_LOCATE = "/(shell)/packing/locate" as Href;
export const PACKING_RECEIVE = "/(shell)/packing/receive" as Href;

export function cartonRoute(cartonGuid: string) {
  return `/packing/carton/${encodeURIComponent(cartonGuid)}` as Href;
}

export function palletRoute(palletGuid: string) {
  return `/packing/pallet/${encodeURIComponent(palletGuid)}` as Href;
}

/** 签收页；带 code 时进入后直接按这个码查找（首页扫到发给本店的箱 / 托盘）。 */
export function receiveRoute(code?: string | null) {
  return (code
    ? { pathname: "/(shell)/packing/receive", params: { code } }
    : PACKING_RECEIVE) as unknown as Href;
}

/** 打印箱标签：内容取服务端最新的箱数据，时间用本机当前时间。 */
export function printCartonLabel(carton: PackCartonDetail, copies: number, smallLabel: boolean) {
  return printPackCartonLabel(buildCartonLabelPayload(carton, new Date()), {
    printType: packPrintType(smallLabel),
    copies,
  });
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
