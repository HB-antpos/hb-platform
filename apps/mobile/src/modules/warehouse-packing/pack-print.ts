import type { Href } from "expo-router";
import { printPackCartonLabel, printPackPalletLabel } from "@/modules/printer/api";
import { packPrintType } from "./pack-preferences";
import { buildCartonLabelPayload, buildPalletLabelPayload } from "./packing-logic";
import type { PackCartonDetail, PackPalletDetail } from "./types";

/** 打包入口（首页）。 */
export const PACKING_HOME = "/(shell)/warehouse-packing" as Href;
export const PACKING_LOCATE = "/(shell)/warehouse-packing/locate" as Href;

export function cartonRoute(cartonGuid: string) {
  return `/warehouse-packing/carton/${encodeURIComponent(cartonGuid)}` as Href;
}

export function palletRoute(palletGuid: string) {
  return `/warehouse-packing/pallet/${encodeURIComponent(palletGuid)}` as Href;
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
