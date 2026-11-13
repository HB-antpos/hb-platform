import type {
  PackCartonLabelPrintPayload,
  PackPalletLabelPrintPayload,
} from "../printer/types";
import {
  CARTON_STATUS,
  DESTINATION_TYPE,
  type DestinationFormValue,
  type PackCartonDetail,
  type PackCartonItem,
  type PackCartonSummary,
  type PackDestination,
  type PackDestinationRequest,
  type PackLocateRow,
  type PackPalletDetail,
  type ScanCountMode,
} from "./types";

export const MAX_PIECES_PER_ITEM = 100000;
export const MAX_LABEL_COPIES = 5;

/** 箱号 BX… / 托盘号 PL…：首页扫码据此直接打开，不必先问服务端是不是商品码。 */
export function looksLikePackCode(code: string): "carton" | "pallet" | null {
  const normalized = code.trim().toUpperCase();
  if (/^BX\d{6,}$/.test(normalized)) return "carton";
  if (/^PL\d{6,}$/.test(normalized)) return "pallet";
  return null;
}

/** 编号拆成前 8 位（前缀 + 年月日）与流水尾号，界面和标签都把尾号放大。 */
export function splitPackNumber(no: string): { prefix: string; tail: string } {
  const normalized = no.trim().toUpperCase();
  return normalized.length > 8
    ? { prefix: normalized.slice(0, 8), tail: normalized.slice(8) }
    : { prefix: "", tail: normalized };
}

function sameCode(left: string | null | undefined, right: string) {
  return Boolean(left) && left!.trim().toUpperCase() === right;
}

/** 在箱内已有商品里按条码 / 货号 / 商品编码找扫到的码；找到才能在客户端提前知道中包数。 */
export function findCartonItemByCode(items: readonly PackCartonItem[], code: string): PackCartonItem | null {
  const normalized = code.trim().toUpperCase();
  if (!normalized) return null;
  const matches = items.filter(
    (item) => sameCode(item.barcode, normalized) || sameCode(item.itemNumber, normalized) || sameCode(item.productCode, normalized),
  );
  // 多个商品都匹配（一码多品）时交给服务端判断，不在客户端猜。
  return matches.length === 1 ? matches[0] : null;
}

export function middlePackOf(item: Pick<PackCartonItem, "middlePackageQuantity"> | null | undefined) {
  const value = item?.middlePackageQuantity;
  return typeof value === "number" && Number.isInteger(value) && value > 1 ? value : null;
}

/**
 * 一次扫码要加的件数：
 * - each：1；
 * - pack：商品已在箱里且有中包数时加一个中包，否则先加 1（见 packTopUp）；
 * - ask：用界面输入的件数。
 */
export function resolveScanQuantity(mode: ScanCountMode, known: PackCartonItem | null, asked?: number | null) {
  if (mode === "ask") {
    return clampPieces(asked ?? 1);
  }
  if (mode === "pack") {
    return middlePackOf(known) ?? 1;
  }
  return 1;
}

/**
 * 按中包计数但扫码前不知道中包数（商品第一次进这只箱）时，先加 1 件拿到商品资料，
 * 再把这一行补到整中包：返回应设置的总数；不需要补时返回 null。
 */
export function packTopUp(item: PackCartonItem | null | undefined, addedQuantity: number): number | null {
  const pack = middlePackOf(item);
  if (!item || !pack || addedQuantity !== 1) return null;
  const target = item.quantity + pack - 1;
  return target <= MAX_PIECES_PER_ITEM ? target : null;
}

export function clampPieces(value: number) {
  if (!Number.isFinite(value)) return 1;
  return Math.max(1, Math.min(MAX_PIECES_PER_ITEM, Math.trunc(value)));
}

export function clampCopies(value: number) {
  if (!Number.isFinite(value)) return 1;
  return Math.max(1, Math.min(MAX_LABEL_COPIES, Math.trunc(value)));
}

/** 数量输入框：只留数字；空串按无效处理，由调用方禁用确认按钮。 */
export function parseQuantityInput(text: string, allowZero = false): number | null {
  const digits = text.replace(/\D/g, "");
  if (!digits) return null;
  const value = Number.parseInt(digits, 10);
  if (!Number.isFinite(value) || value > MAX_PIECES_PER_ITEM) return null;
  return value === 0 && !allowZero ? null : value;
}

/** 重量输入：空 = 不填（null）；非法或超出后端范围返回 "invalid"。 */
export function parseWeightInput(text: string): number | null | "invalid" {
  const trimmed = text.trim().replace(",", ".");
  if (!trimmed) return null;
  if (!/^\d{1,5}(\.\d{1,3})?$/.test(trimmed)) return "invalid";
  const value = Number(trimmed);
  return Number.isFinite(value) && value > 0 && value < 100000 ? value : "invalid";
}

export function formatWeight(value: number | null | undefined) {
  return typeof value === "number" && Number.isFinite(value) ? String(Math.round(value * 1000) / 1000) : "";
}

/** 关联订货单时的「配货 / 已装 / 还差」；商品不在订货单上返回 null（界面橙色提示订货单外）。 */
export function orderProgress(item: PackCartonItem): { ordered: number; packed: number; remaining: number; over: number } | null {
  if (item.orderQuantity == null) return null;
  const ordered = Math.max(0, Math.round(item.orderQuantity));
  const packed = item.quantity + Math.max(0, item.packedInOtherCartons);
  return { ordered, packed, remaining: Math.max(0, ordered - packed), over: Math.max(0, packed - ordered) };
}

/** 刚扫入 / 刚改数量的商品置顶，其余保持服务端顺序（最近改动在前）。 */
export function orderCartonItems(items: readonly PackCartonItem[], highlightedProductCode: string | null) {
  const highlighted = highlightedProductCode
    ? items.find((item) => item.productCode === highlightedProductCode) ?? null
    : null;
  return {
    highlighted,
    rest: highlighted ? items.filter((item) => item !== highlighted) : [...items],
  };
}

export type CartonDisplayState = "packing" | "sealed" | "onPallet" | "voided";

export function cartonDisplayState(carton: Pick<PackCartonSummary, "status" | "palletGuid">): CartonDisplayState {
  if (carton.status === CARTON_STATUS.packing) return "packing";
  if (carton.status === CARTON_STATUS.voided) return "voided";
  return carton.palletGuid ? "onPallet" : "sealed";
}

export function isStoreDestination(destination: PackDestination) {
  return destination.destinationType === DESTINATION_TYPE.store;
}

export function isCarrierDestination(destination: PackDestination) {
  return destination.destinationType === DESTINATION_TYPE.carrier;
}

/** 去向里的收货分店显示名：有名字用名字，否则用编码。 */
export function destinationStoreLabel(destination: PackDestination): string | null {
  return destination.storeName?.trim() || destination.storeCode?.trim() || null;
}

export function toDestinationRequest(
  value: Pick<DestinationFormValue, "destinationType" | "storeCode" | "carrierGuid">,
): PackDestinationRequest {
  if (value.destinationType === DESTINATION_TYPE.store) {
    return { destinationType: DESTINATION_TYPE.store, destinationStoreCode: value.storeCode, carrierGuid: null };
  }
  if (value.destinationType === DESTINATION_TYPE.carrier) {
    return { destinationType: DESTINATION_TYPE.carrier, destinationStoreCode: value.storeCode, carrierGuid: value.carrierGuid };
  }
  return { destinationType: DESTINATION_TYPE.unassigned, destinationStoreCode: null, carrierGuid: null };
}

export function destinationRequestOf(destination: PackDestination): PackDestinationRequest {
  return toDestinationRequest({
    destinationType: destination.destinationType,
    storeCode: destination.storeCode,
    carrierGuid: destination.carrierGuid,
  });
}

export function emptyDestinationForm(): DestinationFormValue {
  return {
    destinationType: DESTINATION_TYPE.unassigned,
    storeCode: null,
    storeName: null,
    carrierGuid: null,
    carrierName: null,
    orderGuid: null,
    orderNo: null,
    consignmentNo: "",
    remark: "",
  };
}

export function destinationFormOf(
  destination: PackDestination,
  extra?: Partial<Pick<DestinationFormValue, "orderGuid" | "orderNo" | "consignmentNo" | "remark">>,
): DestinationFormValue {
  return {
    ...emptyDestinationForm(),
    destinationType: destination.destinationType,
    storeCode: destination.storeCode,
    storeName: destination.storeName,
    carrierGuid: destination.carrierGuid,
    carrierName: destination.carrierName,
    ...extra,
  };
}

/** 去向表单能否提交：分店必须选店，运输公司必须选一家（最终收货分店可空）。 */
export function destinationFormError(value: DestinationFormValue): "storeRequired" | "carrierRequired" | null {
  if (value.destinationType === DESTINATION_TYPE.store && !value.storeCode) return "storeRequired";
  if (value.destinationType === DESTINATION_TYPE.carrier && !value.carrierGuid) return "carrierRequired";
  return null;
}

export interface LocateGroup {
  key: string;
  locationCode: string | null;
  palletGuid: string | null;
  palletNo: string | null;
  quantity: number;
  rows: PackLocateRow[];
}

/** 位置查询按「货位 → 托盘」分组，组内是箱；没有货位的排在有货位的后面。 */
export function groupLocateRows(rows: readonly PackLocateRow[]): LocateGroup[] {
  const groups = new Map<string, LocateGroup>();
  for (const row of rows) {
    const key = `${row.locationCode ?? ""}|${row.palletGuid ?? ""}`;
    const group = groups.get(key) ?? {
      key,
      locationCode: row.locationCode,
      palletGuid: row.palletGuid,
      palletNo: row.palletNo,
      quantity: 0,
      rows: [],
    };
    group.quantity += row.quantity;
    group.rows.push(row);
    groups.set(key, group);
  }
  return [...groups.values()].sort((left, right) => {
    if (Boolean(left.locationCode) !== Boolean(right.locationCode)) return left.locationCode ? -1 : 1;
    return (
      (left.locationCode ?? "").localeCompare(right.locationCode ?? "") ||
      (left.palletNo ?? "").localeCompare(right.palletNo ?? "")
    );
  });
}

const LABEL_MONTHS = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"] as const;

/** 标签时间按本机时间印成 "13 NOV 14:32"（全英文，和标签其余文字一致）。 */
export function formatLabelTime(date: Date) {
  const pad = (value: number) => String(value).padStart(2, "0");
  return `${pad(date.getDate())} ${LABEL_MONTHS[date.getMonth()]} ${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

/** 界面上的本机时间：当天只显示时分，其他日期带月日。 */
export function formatLocalTime(iso: string | null | undefined, now = new Date()) {
  if (!iso) return "";
  // 后端的 UTC 时间可能不带 Z，统一按 UTC 解析。
  const date = new Date(/[zZ]|[+-]\d{2}:\d{2}$/.test(iso) ? iso : `${iso}Z`);
  if (Number.isNaN(date.getTime())) return "";
  const pad = (value: number) => String(value).padStart(2, "0");
  const time = `${pad(date.getHours())}:${pad(date.getMinutes())}`;
  const sameDay =
    date.getFullYear() === now.getFullYear() && date.getMonth() === now.getMonth() && date.getDate() === now.getDate();
  return sameDay ? time : `${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${time}`;
}

/** 箱标签内容：去向只印收货分店；操作人用封箱人，没有则用建箱人。 */
export function buildCartonLabelPayload(
  carton: Pick<
    PackCartonDetail,
    "cartonNo" | "originName" | "destination" | "skuCount" | "pieceCount" | "orderNo" | "sealedByName" | "createdByName"
  >,
  now: Date,
): PackCartonLabelPrintPayload {
  return {
    cartonNo: carton.cartonNo,
    originName: carton.originName,
    destinationName: destinationStoreLabel(carton.destination),
    skuCount: carton.skuCount,
    pieceCount: carton.pieceCount,
    orderNo: carton.orderNo,
    printedAt: formatLabelTime(now),
    operatorName: carton.sealedByName || carton.createdByName || null,
  };
}

/** 托盘上所有箱都属于同一张订货单时返回该单号（直送没有运单号时印在右下）。 */
export function sharedOrderNo(cartons: readonly Pick<PackCartonSummary, "orderNo">[]): string | null {
  const orderNos = new Set(cartons.map((carton) => carton.orderNo?.trim() || ""));
  if (orderNos.size !== 1) return null;
  const [only] = [...orderNos];
  return only || null;
}

/** 托盘标签内容：经运输公司印 VIA 与运单号，直送分店印 DIRECT；张数由打印入口逐张填。 */
export function buildPalletLabelPayload(
  pallet: Pick<
    PackPalletDetail,
    | "palletNo"
    | "originName"
    | "destination"
    | "cartonCount"
    | "pieceCount"
    | "consignmentNo"
    | "cartons"
    | "sealedByName"
    | "createdByName"
  >,
  now: Date,
): PackPalletLabelPrintPayload {
  const viaCarrier = isCarrierDestination(pallet.destination);
  return {
    palletNo: pallet.palletNo,
    originName: pallet.originName,
    destinationName: destinationStoreLabel(pallet.destination),
    carrierName: viaCarrier ? pallet.destination.carrierName : null,
    direct: isStoreDestination(pallet.destination),
    cartonCount: pallet.cartonCount,
    pieceCount: pallet.pieceCount,
    consignmentNo: pallet.consignmentNo,
    orderNo: sharedOrderNo(pallet.cartons),
    printedAt: formatLabelTime(now),
    operatorName: pallet.sealedByName || pallet.createdByName || null,
  };
}

/** 弹层里标签预览用的文字；与 cpcl-labels.ts 打印出来的英文版一致（全部大写、" - " 分隔）。 */
export interface LabelPreviewContent {
  kind: "BOX" | "PLT";
  number: string;
  fromLine: string;
  destinationName: string;
  statLine: string;
  referenceLine: string;
  timeLine: string;
  sheetText?: string;
}

const upper = (value: string | null | undefined) => (value ?? "").trim().toUpperCase();

export function cartonLabelPreview(payload: PackCartonLabelPrintPayload): LabelPreviewContent {
  return {
    kind: "BOX",
    number: upper(payload.cartonNo),
    fromLine: `FROM ${upper(payload.originName) || "--"}`,
    destinationName: upper(payload.destinationName) || "UNASSIGNED",
    statLine: `${payload.skuCount} SKU - ${payload.pieceCount} PCS`,
    referenceLine: upper(payload.orderNo) ? `ORDER ${upper(payload.orderNo)}` : "",
    timeLine: [upper(payload.printedAt), upper(payload.operatorName)].filter(Boolean).join(" "),
  };
}

export function palletLabelPreview(payload: PackPalletLabelPrintPayload, copies: number): LabelPreviewContent {
  const carrier = upper(payload.carrierName);
  const consignmentNo = upper(payload.consignmentNo);
  const orderNo = upper(payload.orderNo);
  return {
    kind: "PLT",
    number: upper(payload.palletNo),
    fromLine: `FROM ${upper(payload.originName) || "--"}${carrier ? ` - VIA ${carrier}` : payload.direct ? " - DIRECT" : ""}`,
    destinationName: upper(payload.destinationName) || "UNASSIGNED",
    statLine: `${payload.cartonCount} CTN - ${payload.pieceCount} PCS`,
    referenceLine: consignmentNo ? `CN ${consignmentNo}` : orderNo ? `ORDER ${orderNo}` : "",
    timeLine: [upper(payload.printedAt), upper(payload.operatorName)].filter(Boolean).join(" "),
    sheetText: `1/${clampCopies(copies)}`,
  };
}

/** 托盘上各箱重量之和；有箱没称重时返回 null，不显示一个偏小的合计。 */
export function sumCartonWeight(cartons: readonly Pick<PackCartonSummary, "weightKg">[]): number | null {
  if (!cartons.length || cartons.some((carton) => carton.weightKg == null)) return null;
  return Math.round(cartons.reduce((total, carton) => total + (carton.weightKg ?? 0), 0) * 1000) / 1000;
}

/** 加入托盘被拦下的三种原因；其余错误按普通错误提示。 */
export type PalletBlockReason = "CARTON_NOT_SEALED" | "CARTON_ON_OTHER_PALLET" | "DESTINATION_MISMATCH";

export function palletBlockReason(code: string | null): PalletBlockReason | null {
  return code === "CARTON_NOT_SEALED" || code === "CARTON_ON_OTHER_PALLET" || code === "DESTINATION_MISMATCH"
    ? code
    : null;
}
