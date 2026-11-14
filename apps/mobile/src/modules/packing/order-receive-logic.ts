import type { Href } from "expo-router";
import {
  CARTON_STATUS,
  type PackIncomingItem,
  type PackOrderReceive,
  type PackOrderReceiveCarton,
  type PackOrderReceiveProduct,
  type PackReceiveLookup,
} from "./types";

// 分店「按订单签收」的纯逻辑：箱分组、箱数核对、订货 / 配货 / 已发对照，以及从签收扫码结果找出可按订单签收的订单。

/** 按订单签收详情的查询键：页面和扫订单条码时预热缓存共用（引用统一用后端返回的 orderGuid）。 */
export const orderReceiveKey = (orderGuid: string) => ["packing", "orderReceive", orderGuid] as const;

/** 配货单订单条码前缀（与拣货的订单二维码同一个：HBSO:订单号）。 */
export const ORDER_BARCODE_PREFIX = "HBSO:";

/**
 * 从订单条码 HBSO:订单号 里取订单号：前缀不分大小写，扫码枪带的回车 / 制表符 / 首尾空格都去掉；
 * 订单号保持原样（不转大小写）。不是订单条码、或前缀后面是空的返回 null。
 * 后端 receive/orders/{orderRef} 不认 HBSO: 前缀，必须先在客户端解析出订单号再传。
 */
export function parseOrderBarcode(raw: string | null | undefined): string | null {
  const text = (raw ?? "").replace(/[\u0000-\u001f\u007f]/g, "").trim();
  if (text.length <= ORDER_BARCODE_PREFIX.length || text.slice(0, ORDER_BARCODE_PREFIX.length).toUpperCase() !== ORDER_BARCODE_PREFIX) {
    return null;
  }
  const orderNo = text.slice(ORDER_BARCODE_PREFIX.length).trim();
  return orderNo || null;
}

/** 按订单签收页路由。 */
export function receiveOrderRoute(orderGuid: string) {
  return `/packing/receive-order?orderGuid=${encodeURIComponent(orderGuid)}` as Href;
}

/** 在途的（等本店签收）与已签收归本店的，分开显示。 */
export function splitOrderCartons(cartons: readonly PackOrderReceiveCarton[]): {
  inTransit: PackOrderReceiveCarton[];
  received: PackOrderReceiveCarton[];
} {
  return {
    inTransit: cartons.filter((carton) => carton.status === CARTON_STATUS.inTransit),
    received: cartons.filter((carton) => carton.status !== CARTON_STATUS.inTransit),
  };
}

/**
 * 箱数核对：必须由收货人数过箱再输入，数完才能签收；1 起，且不超过上限。
 * 是否和系统记录一致由服务端判断（本机数据可能过期），不一致返回 409 COUNT_MISMATCH，一只都不签。
 */
export const MAX_RECEIVE_CARTONS = 999;

export function clampCartonCount(value: number): number {
  if (!Number.isFinite(value)) return 0;
  return Math.max(0, Math.min(MAX_RECEIVE_CARTONS, Math.trunc(value)));
}

/** 能不能发起按订单签收：要有在途的箱。 */
export function canReceiveOrder(detail: Pick<PackOrderReceive, "inTransitCount">): boolean {
  return detail.inTransitCount > 0;
}

/** 箱数对不上时提示用的两个数：实到（本次输入）与系统记录（服务端带回的，没有就用页面上看到的在途数）。 */
export function countMismatchCounts(
  entered: number,
  actualFromServer: number | null,
  shownInTransit: number,
): { entered: number; actual: number } {
  return { entered, actual: actualFromServer ?? shownInTransit };
}

export type ProductShipStatus = "match" | "short" | "over";

/** 该商品应发的件数：有配货数用配货数，否则（订单行都没有配货数）用订货数。 */
export function productShipTarget(product: Pick<PackOrderReceiveProduct, "orderedPieces" | "allocPieces">): number {
  return product.allocPieces ?? product.orderedPieces;
}

/** 已发出 − 应发：正数发多了，负数还没发够（后面还有箱在路上，或确实少发）。 */
export function productShipDiff(product: Pick<PackOrderReceiveProduct, "orderedPieces" | "allocPieces" | "shippedPieces">): number {
  return product.shippedPieces - productShipTarget(product);
}

export function productShipStatus(product: Pick<PackOrderReceiveProduct, "orderedPieces" | "allocPieces" | "shippedPieces">): ProductShipStatus {
  const diff = productShipDiff(product);
  return diff === 0 ? "match" : diff < 0 ? "short" : "over";
}

/** 对照表排序：有差异的商品排前面（少发在先），其余保持服务端顺序。 */
export function orderProductsByVariance<T extends Pick<PackOrderReceiveProduct, "orderedPieces" | "allocPieces" | "shippedPieces">>(products: readonly T[]): T[] {
  const rank = (product: T) => {
    const status = productShipStatus(product);
    return status === "short" ? 0 : status === "over" ? 1 : 2;
  };
  return products
    .map((product, index) => ({ product, index }))
    .sort((left, right) => rank(left.product) - rank(right.product) || left.index - right.index)
    .map((entry) => entry.product);
}

export interface OrderReceiveEntry {
  orderGuid: string;
  orderNo: string | null;
}

/**
 * 扫码签收找到的东西里，有哪些订单可以改成「按订单签收」：
 * 只在能签收（发给本店、在路上）时给；托盘取在途的箱，散箱取箱本身；同一订单只列一次，没有订单的箱不给。
 */
export function orderEntriesOf(lookup: Pick<PackReceiveLookup, "canReceive" | "carton" | "pallet">): OrderReceiveEntry[] {
  if (!lookup.canReceive) return [];
  const cartons = lookup.pallet
    ? lookup.pallet.cartons.filter((carton) => carton.status === CARTON_STATUS.inTransit)
    : lookup.carton
      ? [lookup.carton]
      : [];
  const seen = new Set<string>();
  const entries: OrderReceiveEntry[] = [];
  for (const carton of cartons) {
    const guid = carton.orderGuid?.trim();
    if (!guid || seen.has(guid.toLowerCase())) continue;
    seen.add(guid.toLowerCase());
    entries.push({ orderGuid: guid, orderNo: carton.orderNo });
  }
  return entries;
}

/**
 * 待签收 / 最近签收清单里一行可以「按订单签收」的订单：散箱取它自己的订单，托盘取后端给的 orders；
 * 没有订单关联返回空数组（不显示入口）。托盘 orders 里重复的订单只留一个。
 */
export function incomingOrderEntries(item: Pick<PackIncomingItem, "kind" | "orderGuid" | "orderNo" | "orders">): OrderReceiveEntry[] {
  const raw: OrderReceiveEntry[] =
    item.kind === "carton"
      ? item.orderGuid
        ? [{ orderGuid: item.orderGuid, orderNo: item.orderNo }]
        : []
      : item.orders.map((order) => ({ orderGuid: order.orderGuid, orderNo: order.orderNo }));
  const seen = new Set<string>();
  return raw.filter((entry) => {
    const key = entry.orderGuid.trim().toLowerCase();
    if (!key || seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}
