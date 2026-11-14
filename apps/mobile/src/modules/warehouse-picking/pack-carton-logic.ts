import { DESTINATION_TYPE, type PackCartonDetail } from "../packing/types";
import { readCartonSync } from "./pack-api-normalization";
import {
  PACK_CARTON_STATUS,
  type CartonSync,
  type CurrentCarton,
  type PackProgress,
  type PackProgressCarton,
} from "./pack-types";

// 边拣边装的纯逻辑：当前箱状态更新、写入错误分类、汇总与标签预览数据。界面与网络都不在这里。

const productKey = (productCode: string) => productCode.trim().toLowerCase();

export function sameGuid(left: string | null | undefined, right: string | null | undefined) {
  return Boolean(left) && Boolean(right) && left!.trim().toLowerCase() === right!.trim().toLowerCase();
}

/**
 * 写请求带上当前箱。没有当前箱时原样返回、不加 cartonGuid 字段，请求体与旧版逐字节一致（拣货不装箱时的行为契约）。
 */
export function withCartonGuid<T extends object>(input: T, cartonGuid: string | null | undefined): T & { cartonGuid?: string } {
  return cartonGuid ? { ...input, cartonGuid } : input;
}

/** 自己新建的箱 / 刚拿到完整箱详情：各商品件数都已知。 */
export function cartonFromDetail(detail: PackCartonDetail): CurrentCarton {
  const items: Record<string, number> = {};
  for (const item of detail.items) items[productKey(item.productCode)] = item.quantity;
  return {
    cartonGuid: detail.cartonGuid,
    cartonNo: detail.cartonNo,
    skuCount: detail.skuCount,
    pieceCount: detail.pieceCount,
    items,
    itemsComplete: true,
    base: detail,
  };
}

/**
 * 用 pack-progress 里的一只装箱中的箱作当前箱。
 * - 后端带了 items：各商品件数都已知（含空箱 []），不用再读箱详情，PDA 设备会话也能接着装；
 * - 旧后端没有 items：只有汇总，空箱（0 件）算已知，有商品的箱各商品件数未知（调用方再想办法读基线）。
 */
export function cartonFromProgress(carton: PackProgressCarton): CurrentCarton {
  const items: Record<string, number> = {};
  for (const item of carton.items ?? []) items[productKey(item.productCode)] = item.quantity;
  return {
    cartonGuid: carton.cartonGuid,
    cartonNo: carton.cartonNo,
    skuCount: carton.skuCount,
    pieceCount: carton.pieceCount,
    items,
    itemsComplete: carton.items !== null || carton.pieceCount === 0,
    base: null,
  };
}

/**
 * 进入页面时自动接着用的箱：自己建的、装箱中的里建箱时间最晚的一只；
 * 时间相同或缺失（旧后端）时取列表里靠后的（后端按建箱时间、箱号升序）。
 */
export function resumableCarton(progress: Pick<PackProgress, "cartons">): PackProgressCarton | null {
  let best: PackProgressCarton | null = null;
  let bestTime = Number.NEGATIVE_INFINITY;
  for (const carton of progress.cartons) {
    if (!carton.isMine || carton.status !== PACK_CARTON_STATUS.packing) continue;
    const time = parseUtcTime(carton.createdAtUtc);
    // >=：同样晚（或都没有时间）时后面的覆盖前面的。
    if (best === null || time >= bestTime) {
      best = carton;
      bestTime = time;
    }
  }
  return best;
}

/** 后端的 UTC 时间可能不带 Z，统一按 UTC 解析；解析不了返回负无穷（排在最早）。 */
function parseUtcTime(iso: string | null): number {
  if (!iso) return Number.NEGATIVE_INFINITY;
  const parsed = Date.parse(/[zZ]|[+-]\d{2}:\d{2}$/.test(iso) ? iso : `${iso}Z`);
  return Number.isNaN(parsed) ? Number.NEGATIVE_INFINITY : parsed;
}

/**
 * 拣货写入成功后按响应的 carton 更新「当前箱」。productCode 是这次写入的商品（响应里没有，由调用方告知）。
 * 响应对应的不是当前箱（比如写入排队期间已经换箱）时原样返回，不会把别的箱的数字写进来。
 */
export function applyCartonSync(current: CurrentCarton | null, sync: CartonSync, productCode: string | null): CurrentCarton | null {
  if (!current || !sameGuid(current.cartonGuid, sync.cartonGuid)) return current;
  return {
    ...current,
    cartonNo: sync.cartonNo || current.cartonNo,
    skuCount: sync.skuCount,
    pieceCount: sync.pieceCount,
    items: productCode ? { ...current.items, [productKey(productCode)]: sync.itemPieces } : current.items,
  };
}

/** 完整箱详情（先拣后装每次设置数量都返回）覆盖当前箱：各商品件数以服务端为准。 */
export function applyCartonDetail(current: CurrentCarton | null, detail: PackCartonDetail): CurrentCarton | null {
  if (!current || !sameGuid(current.cartonGuid, detail.cartonGuid)) return current;
  return cartonFromDetail(detail);
}

/** 该商品在当前箱里的件数；还不知道（接着用旧箱、没碰过这个商品）返回 null，界面不显示，不能当 0。 */
export function cartonPiecesOf(current: CurrentCarton | null, productCode: string): number | null {
  if (!current) return null;
  const known = current.items[productKey(productCode)];
  if (known !== undefined) return known;
  return current.itemsComplete ? 0 : null;
}

export type CartonWriteFailure =
  /** 减一时箱里不够：pieces 是箱内该商品现有件数。 */
  | { kind: "qtyInsufficient"; pieces: number | null; carton: CartonSync | null }
  /** 箱已封 / 已发出 / 不存在：当前箱不能再用。 */
  | { kind: "cartonUnavailable"; code: "CARTON_NOT_FOUND" | "CARTON_NOT_PACKING" | "CARTON_IN_TRANSIT" }
  | { kind: "outOfRange" };

/** 拣货写入带 cartonGuid 失败时的分类；不是装箱相关的错误返回 null（走原有的拣货错误提示）。 */
export function classifyCartonWriteError(code: string | null, data: unknown): CartonWriteFailure | null {
  switch (code) {
    case "CARTON_QTY_INSUFFICIENT": {
      const carton = readCartonSync((data as { carton?: unknown } | null)?.carton);
      return { kind: "qtyInsufficient", pieces: carton ? carton.itemPieces : null, carton };
    }
    case "CARTON_NOT_FOUND":
    case "CARTON_NOT_PACKING":
    case "CARTON_IN_TRANSIT":
      return { kind: "cartonUnavailable", code };
    case "QUANTITY_OUT_OF_RANGE":
      return { kind: "outOfRange" };
    default:
      return null;
  }
}

const KNOWN_MISSING_BUSINESS_CODES = new Set(["ORDER_NOT_FOUND", "CARTON_NOT_FOUND", "CODE_NOT_FOUND"]);

/**
 * 装箱接口不存在（后端还没部署二期）：404 且不是业务错误码。此时拣货页不显示装箱入口，行为与旧版一致。
 * 注意 axios 的 error.code（ERR_BAD_REQUEST 之类）也是大写下划线，调用方传进来的 code 可能混有它，所以按「已知业务码」排除。
 */
export function isPackApiMissing(status: number | null, code: string | null): boolean {
  return status === 404 && !(code && KNOWN_MISSING_BUSINESS_CODES.has(code));
}

/** 已装箱的箱数与件数（只算有商品的箱）；当前箱用客户端的实时数字覆盖（刚写入的比上次拉取的新）。 */
export function summarizeCartons(
  cartons: readonly Pick<PackProgressCarton, "cartonGuid" | "pieceCount">[],
  current: Pick<CurrentCarton, "cartonGuid" | "pieceCount"> | null,
): { cartonCount: number; pieceCount: number } {
  let cartonCount = 0;
  let pieceCount = 0;
  let currentSeen = false;
  for (const carton of cartons) {
    const isCurrent = current !== null && sameGuid(carton.cartonGuid, current.cartonGuid);
    if (isCurrent) currentSeen = true;
    const pieces = isCurrent ? current.pieceCount : carton.pieceCount;
    if (pieces > 0) {
      cartonCount += 1;
      pieceCount += pieces;
    }
  }
  // 刚新建、还没重新拉取进度的箱不在列表里。
  if (current && !currentSeen && current.pieceCount > 0) {
    cartonCount += 1;
    pieceCount += current.pieceCount;
  }
  return { cartonCount, pieceCount };
}

/** 封箱弹层里的标签预览用的箱数据：有建箱时的完整数据就用它，接着用的旧箱只能按订单信息近似（打印用封箱返回的完整数据）。 */
export function previewCartonOf(
  current: CurrentCarton,
  progress: Pick<PackProgress, "orderNo" | "storeCode" | "storeName"> | null,
  operatorName: string | null,
): Pick<PackCartonDetail, "cartonNo" | "originName" | "destination" | "skuCount" | "pieceCount" | "orderNo" | "sealedByName" | "createdByName"> {
  if (current.base) {
    return { ...current.base, skuCount: current.skuCount, pieceCount: current.pieceCount };
  }
  return {
    cartonNo: current.cartonNo,
    originName: "",
    destination: {
      destinationType: progress?.storeCode ? DESTINATION_TYPE.store : DESTINATION_TYPE.unassigned,
      code: progress?.storeCode ?? null,
      name: progress?.storeName ?? null,
      carrierGuid: null,
      carrierName: null,
    },
    skuCount: current.skuCount,
    pieceCount: current.pieceCount,
    orderNo: progress?.orderNo ?? null,
    sealedByName: null,
    createdByName: operatorName ?? "",
  };
}

/** 提交成功后带回订单列表的「共 N 只箱 · M 件」提示参数（路由参数只能是字符串）。 */
export function encodePackedParam(cartons: number, pieces: number): string {
  return `${Math.max(0, Math.trunc(cartons))}:${Math.max(0, Math.trunc(pieces))}`;
}

export function parsePackedParam(value: string | string[] | null | undefined): { cartons: number; pieces: number } | null {
  const text = Array.isArray(value) ? value[0] : value;
  const match = /^(\d{1,6}):(\d{1,9})$/.exec(text ?? "");
  return match ? { cartons: Number(match[1]), pieces: Number(match[2]) } : null;
}
