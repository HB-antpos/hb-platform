import {
  buildPackCartonLabelSpec,
  buildPackPalletLabelSpec,
  type PackLabelSpec,
} from "../printer/cpcl-labels";
import type { PackCartonLabelPrintPayload, PackPalletLabelPrintPayload } from "../printer/types";
import {
  CARTON_STATUS,
  DESTINATION_TYPE,
  PALLET_STATUS,
  type DestinationFormValue,
  type LocateGroupKey,
  type PackCartonDetail,
  type PackCartonItem,
  type PackCartonSummary,
  type PackDestination,
  type PackDestinationRequest,
  type PackLocateRow,
  type PackOwner,
  type PackPalletDetail,
  type PackPalletSummary,
  type PackReceiveLookup,
  type ScanCountMode,
} from "./types";

export const MAX_PIECES_PER_ITEM = 100000;
export const MAX_LABEL_COPIES = 5;

/** 箱号 BX… / 托盘号 PL…：首页扫码据此判断是不是箱 / 托盘标签。 */
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

export function sameCode(left: string | null | undefined, right: string | null | undefined) {
  return Boolean(left) && Boolean(right) && left!.trim().toUpperCase() === right!.trim().toUpperCase();
}

// ---- 所属地点 ----

/** 本机记住的地点仍然在账号可操作范围内就用它，否则用第一个；账号没有任何地点返回 null。 */
export function resolveActiveOwner(owners: readonly PackOwner[], savedCode: string | null | undefined): PackOwner | null {
  if (owners.length === 0) return null;
  return owners.find((owner) => sameCode(owner.code, savedCode)) ?? owners[0];
}

/** 每个账号各记各的地点选择：key 统一成小写账号 GUID。 */
export function normalizeUserKey(userGuid: string) {
  return userGuid.trim().toLowerCase();
}

export function savedOwnerCode(selections: Record<string, string>, userGuid: string | null | undefined) {
  return userGuid ? selections[normalizeUserKey(userGuid)] ?? null : null;
}

export function ownerCodesOf(owners: readonly PackOwner[]) {
  return owners.map((owner) => owner.code);
}

export function hasOwner(ownerCodes: readonly string[], code: string | null | undefined) {
  return ownerCodes.some((owner) => sameCode(owner, code));
}

// ---- 装箱计数 ----

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

// ---- 去向 ----

/** 要发出的去向：其他分店或总仓（发出后由对方扫码签收）。 */
export function isShippableType(destinationType: number) {
  return destinationType === DESTINATION_TYPE.store || destinationType === DESTINATION_TYPE.warehouse;
}

export function isShippableDestination(destination: PackDestination) {
  return isShippableType(destination.destinationType);
}

/** 「可发往的地点」候选拆成总仓项（接口带 isWarehouse 才有）和分店列表，界面据此决定要不要显示「总仓」。 */
export function splitDestinations(options: readonly PackOwner[]): { warehouse: PackOwner | null; stores: PackOwner[] } {
  return {
    warehouse: options.find((option) => option.isWarehouse) ?? null,
    stores: options.filter((option) => !option.isWarehouse),
  };
}

/** 分店候选按名称或编码搜索；空关键字返回全部。 */
export function filterDestinationStores(stores: readonly PackOwner[], keyword: string): PackOwner[] {
  const needle = keyword.trim().toLowerCase();
  if (!needle) return [...stores];
  return stores.filter((store) => store.name.toLowerCase().includes(needle) || store.code.toLowerCase().includes(needle));
}

/** 去向里的收货地点显示名：有名字用名字，否则用编码。 */
export function destinationLabel(destination: PackDestination): string | null {
  return destination.name?.trim() || destination.code?.trim() || null;
}

/** 请求体里的去向；运输公司只对要发出的去向有意义，本地存放 / 未指定一律不带。 */
export function toDestinationRequest(
  value: Pick<DestinationFormValue, "destinationType" | "code" | "carrierGuid">,
): PackDestinationRequest {
  if (value.destinationType === DESTINATION_TYPE.store) {
    return { destinationType: DESTINATION_TYPE.store, destinationCode: value.code, carrierGuid: value.carrierGuid };
  }
  if (value.destinationType === DESTINATION_TYPE.warehouse) {
    // 总仓编码固定，不用传。
    return { destinationType: DESTINATION_TYPE.warehouse, destinationCode: null, carrierGuid: value.carrierGuid };
  }
  if (value.destinationType === DESTINATION_TYPE.local) {
    return { destinationType: DESTINATION_TYPE.local, destinationCode: null, carrierGuid: null };
  }
  return { destinationType: DESTINATION_TYPE.unassigned, destinationCode: null, carrierGuid: null };
}

export function destinationRequestOf(destination: PackDestination): PackDestinationRequest {
  return toDestinationRequest({
    destinationType: destination.destinationType,
    code: destination.code,
    carrierGuid: destination.carrierGuid,
  });
}

export function emptyDestinationForm(): DestinationFormValue {
  return {
    destinationType: DESTINATION_TYPE.unassigned,
    code: null,
    name: null,
    carrierGuid: null,
    carrierName: null,
    consignmentNo: "",
    remark: "",
  };
}

export function destinationFormOf(
  destination: PackDestination,
  extra?: Partial<Pick<DestinationFormValue, "consignmentNo" | "remark">>,
): DestinationFormValue {
  return {
    ...emptyDestinationForm(),
    destinationType: destination.destinationType,
    code: destination.code,
    name: destination.name,
    carrierGuid: destination.carrierGuid,
    carrierName: destination.carrierName,
    ...extra,
  };
}

/** 去向表单能否提交：其他分店必须选店；选了「交运输公司」必须选一家（carrierRequired 由弹层按当前选择判断）。 */
export function destinationFormError(
  value: DestinationFormValue,
  viaCarrier: boolean,
): "storeRequired" | "carrierRequired" | null {
  if (value.destinationType === DESTINATION_TYPE.store && !value.code) return "storeRequired";
  if (isShippableType(value.destinationType) && viaCarrier && !value.carrierGuid) return "carrierRequired";
  return null;
}

// ---- 箱 / 托盘在当前账号眼里的状态 ----

export type CartonDisplayState = "packing" | "stored" | "toDispatch" | "onPallet" | "outgoing" | "received" | "voided";

/**
 * 首页列表里一只箱的状态（ownerCode 是当前地点）：
 * 装箱中、本店存放（含待发出）、已上托盘、已发出（在路上）、对方已签收（箱的所属地点已不是当前地点）。
 */
export function cartonDisplayState(
  carton: Pick<PackCartonSummary, "status" | "palletGuid" | "ownerCode" | "originCode" | "destination">,
  currentOwnerCode: string,
): CartonDisplayState {
  if (carton.status === CARTON_STATUS.voided) return "voided";
  if (carton.status === CARTON_STATUS.packing) return "packing";
  if (carton.status === CARTON_STATUS.inTransit) return "outgoing";
  if (!sameCode(carton.ownerCode, currentOwnerCode) && sameCode(carton.originCode, currentOwnerCode)) return "received";
  if (carton.palletGuid) return "onPallet";
  return isShippableDestination(carton.destination) ? "toDispatch" : "stored";
}

export type PalletDisplayState = "building" | "stored" | "toDispatch" | "outgoing" | "received" | "voided";

export function palletDisplayState(
  pallet: Pick<PackPalletSummary, "status" | "ownerCode" | "originCode" | "destination">,
  currentOwnerCode: string,
): PalletDisplayState {
  if (pallet.status === PALLET_STATUS.voided) return "voided";
  if (pallet.status === PALLET_STATUS.building) return "building";
  if (pallet.status === PALLET_STATUS.inTransit) return "outgoing";
  if (!sameCode(pallet.ownerCode, currentOwnerCode) && sameCode(pallet.originCode, currentOwnerCode)) return "received";
  return isShippableDestination(pallet.destination) ? "toDispatch" : "stored";
}

/**
 * 详情页的使用方式（ownerCodes 是账号能操作的全部地点）：
 * editable 属于你的地点且没发出：可改；outgoing 你发出、还在路上；incoming 在路上、发给你；
 * received 你发出、对方已签收；readonly 其他。
 */
export type ViewMode = "editable" | "outgoing" | "incoming" | "received" | "readonly";

export function cartonViewMode(
  carton: Pick<PackCartonSummary, "status" | "ownerCode" | "originCode" | "destination">,
  ownerCodes: readonly string[],
): ViewMode {
  const mine = hasOwner(ownerCodes, carton.ownerCode);
  if (carton.status === CARTON_STATUS.inTransit) {
    if (mine) return "outgoing";
    return hasOwner(ownerCodes, carton.destination.code) ? "incoming" : "readonly";
  }
  if (mine && (carton.status === CARTON_STATUS.packing || carton.status === CARTON_STATUS.sealed)) return "editable";
  if (!mine && hasOwner(ownerCodes, carton.originCode)) return "received";
  return "readonly";
}

export function palletViewMode(
  pallet: Pick<PackPalletSummary, "status" | "ownerCode" | "originCode" | "destination">,
  ownerCodes: readonly string[],
): ViewMode {
  const mine = hasOwner(ownerCodes, pallet.ownerCode);
  if (pallet.status === PALLET_STATUS.inTransit) {
    if (mine) return "outgoing";
    return hasOwner(ownerCodes, pallet.destination.code) ? "incoming" : "readonly";
  }
  if (mine && (pallet.status === PALLET_STATUS.building || pallet.status === PALLET_STATUS.sealed)) return "editable";
  if (!mine && hasOwner(ownerCodes, pallet.originCode)) return "received";
  return "readonly";
}

// ---- 签收 ----

/** 托盘上还在路上的箱（待核对 / 待签收）。 */
export function inTransitCartons(pallet: Pick<PackPalletDetail, "cartons">) {
  return pallet.cartons.filter((carton) => carton.status === CARTON_STATUS.inTransit);
}

/** 扫到的箱号在不在这个托盘的待签收箱里：在就返回那只箱（逐箱核对打勾）。 */
export function findPalletCartonByCode(pallet: Pick<PackPalletDetail, "cartons">, code: string): PackCartonSummary | null {
  const normalized = code.trim().toUpperCase();
  if (!normalized) return null;
  return pallet.cartons.find((carton) => sameCode(carton.cartonNo, normalized) || sameCode(carton.cartonGuid, normalized)) ?? null;
}

/** 已核对集合里仍然有效的箱（只算还在路上的，托盘刷新后已签收的箱自动剔除）。 */
export function validCheckedGuids(pallet: Pick<PackPalletDetail, "cartons">, checked: ReadonlySet<string>) {
  return inTransitCartons(pallet)
    .map((carton) => carton.cartonGuid)
    .filter((guid) => checked.has(guid));
}

/** 全部在路上的箱都已核对：整托签收可以直接用总箱数，不必再数。 */
export function allCartonsChecked(pallet: Pick<PackPalletDetail, "cartons">, checked: ReadonlySet<string>) {
  const transit = inTransitCartons(pallet);
  return transit.length > 0 && validCheckedGuids(pallet, checked).length === transit.length;
}

export type ReceiveBlockKind = "notForYou" | "notInTransit" | "empty" | null;

/** 不能签收时给界面的原因分类；能签收返回 null。 */
export function receiveBlockKind(lookup: Pick<PackReceiveLookup, "canReceive" | "blockReason">): ReceiveBlockKind {
  if (lookup.canReceive) return null;
  if (lookup.blockReason === "NOT_FOR_YOU") return "notForYou";
  if (lookup.blockReason === "NOT_IN_TRANSIT") return "notInTransit";
  // 后端没给原因但不能签收：托盘上没有待签收的箱了。
  return "empty";
}

/** 这个签收对象是不是已经发给了账号能操作的某个地点（决定签收后放位置时用哪家的位置表）。 */
export function receiveOwnerCode(lookup: Pick<PackReceiveLookup, "pallet" | "carton">): string | null {
  return lookup.pallet?.destination.code ?? lookup.carton?.destination.code ?? null;
}

// ---- 位置查询 ----

export interface LocateGroup {
  key: string;
  locationCode: string | null;
  palletGuid: string | null;
  palletNo: string | null;
  quantity: number;
  rows: PackLocateRow[];
}

/** 本店存放内部按「位置 → 托盘」分组，组内是箱；没有位置的排在有位置的后面。 */
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

export interface LocateSection {
  group: LocateGroupKey;
  quantity: number;
  rows: PackLocateRow[];
}

const LOCATE_GROUP_ORDER: readonly LocateGroupKey[] = ["stored", "transit", "elsewhere", "other"];

/** 位置查询结果按后端的 group 分段：本店存放 / 在路上 / 对方已签收 / 其他分店；空的分段不出现。 */
export function groupLocateSections(rows: readonly PackLocateRow[]): LocateSection[] {
  return LOCATE_GROUP_ORDER.map((group) => {
    const own = rows.filter((row) => row.group === group);
    return { group, quantity: own.reduce((total, row) => total + row.quantity, 0), rows: own };
  }).filter((section) => section.rows.length > 0);
}

// ---- 时间 ----

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

// ---- 标签内容 ----

/** 箱标签内容：FROM 取后端的发出地；TO / VIA / DIRECT 由 cpcl-labels 按去向类型决定；操作人用封箱人，没有则用建箱人。 */
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
    destinationType: carton.destination.destinationType,
    destinationName: destinationLabel(carton.destination),
    carrierName: carton.destination.carrierName,
    skuCount: carton.skuCount,
    pieceCount: carton.pieceCount,
    orderNo: carton.orderNo,
    printedAt: formatLabelTime(now),
    operatorName: carton.sealedByName || carton.createdByName || null,
  };
}

/** 托盘上所有箱都属于同一张订货单时返回该单号（没有运单号时印在右下）。 */
export function sharedOrderNo(cartons: readonly Pick<PackCartonSummary, "orderNo">[]): string | null {
  const orderNos = new Set(cartons.map((carton) => carton.orderNo?.trim() || ""));
  if (orderNos.size !== 1) return null;
  const [only] = [...orderNos];
  return only || null;
}

/** 托盘标签内容；张数由打印入口逐张填。 */
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
  return {
    palletNo: pallet.palletNo,
    originName: pallet.originName,
    destinationType: pallet.destination.destinationType,
    destinationName: destinationLabel(pallet.destination),
    carrierName: pallet.destination.carrierName,
    cartonCount: pallet.cartonCount,
    pieceCount: pallet.pieceCount,
    consignmentNo: pallet.consignmentNo,
    orderNo: sharedOrderNo(pallet.cartons),
    printedAt: formatLabelTime(now),
    operatorName: pallet.sealedByName || pallet.createdByName || null,
  };
}

/** 弹层里标签预览用的文字；与 cpcl-labels.ts 打印出来的英文版同源（全部大写、" - " 分隔）。 */
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

function previewOf(spec: PackLabelSpec): LabelPreviewContent {
  return {
    kind: spec.kind,
    number: spec.number.trim().toUpperCase(),
    fromLine: spec.fromLine,
    destinationName: spec.destinationName,
    statLine: spec.statLine,
    referenceLine: spec.referenceLine,
    timeLine: [spec.printedAt, spec.operatorName].filter(Boolean).join(" "),
    sheetText: spec.sheetText || undefined,
  };
}

export function cartonLabelPreview(payload: PackCartonLabelPrintPayload): LabelPreviewContent {
  return previewOf(buildPackCartonLabelSpec(payload));
}

export function palletLabelPreview(payload: PackPalletLabelPrintPayload, copies: number): LabelPreviewContent {
  return previewOf(buildPackPalletLabelSpec({ ...payload, sheetIndex: 1, sheetTotal: clampCopies(copies) }));
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
