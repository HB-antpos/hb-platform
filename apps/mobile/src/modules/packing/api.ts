import { apiClient } from "@/shared/api/client";
import {
  normalizeCarrier,
  normalizeCarriers,
  normalizeCartonDetail,
  normalizeCartonList,
  normalizeIncoming,
  normalizeLocate,
  normalizeLocation,
  normalizeLocations,
  normalizePalletDetail,
  normalizePalletList,
  normalizeReceiveLookup,
  normalizeResolve,
  normalizeScope,
} from "./api-normalization";
import type { CartonFilter, PackDestinationRequest, PalletFilter } from "./types";

const BASE = "/react/v1/packing";
const carton = (cartonGuid: string, suffix = "") => `${BASE}/cartons/${encodeURIComponent(cartonGuid)}${suffix}`;
const pallet = (palletGuid: string, suffix = "") => `${BASE}/pallets/${encodeURIComponent(palletGuid)}${suffix}`;

// ---- 所属地点 ----

/** 账号能操作的分店 / 总仓，以及是不是店长 / 管理员。 */
export async function fetchPackScope(signal?: AbortSignal) {
  const response = await apiClient.get(`${BASE}/scope`, { signal });
  return normalizeScope(response.data);
}

// ---- 箱 ----

export async function fetchCartons(
  ownerCode: string,
  filter: CartonFilter,
  keyword: string,
  mine: boolean,
  signal?: AbortSignal,
) {
  const response = await apiClient.get(`${BASE}/cartons`, {
    params: { ownerCode, filter, keyword: keyword.trim() || undefined, mine: mine || undefined },
    signal,
  });
  return normalizeCartonList(response.data);
}

export async function createCarton(input: PackDestinationRequest & { ownerCode: string; orderGuid?: string | null }) {
  const response = await apiClient.post(`${BASE}/cartons`, input);
  return normalizeCartonDetail(response.data);
}

export async function fetchCarton(cartonGuid: string, signal?: AbortSignal) {
  const response = await apiClient.get(carton(cartonGuid), { signal });
  return normalizeCartonDetail(response.data);
}

/** 扫商品进箱；一码多品时抛 409 CODE_AMBIGUOUS（候选在错误里），选定后带 productCode 重新提交。 */
export async function scanIntoCarton(
  cartonGuid: string,
  input: { code?: string | null; productCode?: string | null; quantity: number },
) {
  const response = await apiClient.post(carton(cartonGuid, "/scan"), input);
  return normalizeCartonDetail(response.data);
}

/** 直接设置某商品在箱里的总数量；0 表示移除。 */
export async function setCartonItemQuantity(cartonGuid: string, productCode: string, quantity: number) {
  const response = await apiClient.put(carton(cartonGuid, `/items/${encodeURIComponent(productCode)}`), { quantity });
  return normalizeCartonDetail(response.data);
}

/** 箱头是整体覆盖：去向、订货单、重量、备注都要带上现值。 */
export async function updateCartonHeader(
  cartonGuid: string,
  input: PackDestinationRequest & { orderGuid: string | null; weightKg: number | null; remark: string | null },
) {
  const response = await apiClient.put(carton(cartonGuid, "/header"), input);
  return normalizeCartonDetail(response.data);
}

export async function sealCarton(cartonGuid: string, weightKg: number | null) {
  const response = await apiClient.post(carton(cartonGuid, "/seal"), { weightKg });
  return normalizeCartonDetail(response.data);
}

/** 重新开箱：只有店长 / 仓库经理 / 管理员可以，店员返回 403 PACKING_NOT_ALLOWED。 */
export async function reopenCarton(cartonGuid: string) {
  const response = await apiClient.post(carton(cartonGuid, "/reopen"), {});
  return normalizeCartonDetail(response.data);
}

export async function voidCarton(cartonGuid: string) {
  const response = await apiClient.post(carton(cartonGuid, "/void"), {});
  return normalizeCartonDetail(response.data);
}

/** 存放位置名称或位置条码；传 null 清除。 */
export async function setCartonLocation(cartonGuid: string, locationCode: string | null) {
  const response = await apiClient.put(carton(cartonGuid, "/location"), { locationCode });
  return normalizeCartonDetail(response.data);
}

/** 发出散箱：去向必须是其他分店或总仓，发出后由对方扫码签收，发出方不能再改。 */
export async function dispatchCarton(cartonGuid: string) {
  const response = await apiClient.post(carton(cartonGuid, "/dispatch"), {});
  return normalizeCartonDetail(response.data);
}

/** 签收一只箱（散箱，或托盘上「只签收这一箱」）；locationCode 是签收后放到哪个存放位置。 */
export async function receiveCarton(cartonGuid: string, locationCode: string | null) {
  const response = await apiClient.post(carton(cartonGuid, "/receive"), { locationCode });
  return normalizeCartonDetail(response.data);
}

// ---- 托盘 ----

export async function fetchPallets(ownerCode: string, filter: PalletFilter, keyword: string, signal?: AbortSignal) {
  const response = await apiClient.get(`${BASE}/pallets`, {
    params: { ownerCode, filter, keyword: keyword.trim() || undefined },
    signal,
  });
  return normalizePalletList(response.data);
}

export async function createPallet(input: PackDestinationRequest & { ownerCode: string }) {
  const response = await apiClient.post(`${BASE}/pallets`, input);
  return normalizePalletDetail(response.data);
}

export async function fetchPallet(palletGuid: string, signal?: AbortSignal) {
  const response = await apiClient.get(pallet(palletGuid), { signal });
  return normalizePalletDetail(response.data);
}

/** 扫箱标签放上托盘；被拦下时抛 409，错误里带回那只箱（blockedCarton）。 */
export async function addCartonToPallet(palletGuid: string, code: string, allowDestinationMismatch = false) {
  const response = await apiClient.post(pallet(palletGuid, "/cartons"), { code, allowDestinationMismatch });
  return normalizePalletDetail(response.data);
}

export async function removeCartonFromPallet(palletGuid: string, cartonGuid: string) {
  const response = await apiClient.delete(pallet(palletGuid, `/cartons/${encodeURIComponent(cartonGuid)}`));
  return normalizePalletDetail(response.data);
}

/** 托盘头同样整体覆盖：去向、运单号、重量、备注都要带上现值。 */
export async function updatePalletHeader(
  palletGuid: string,
  input: PackDestinationRequest & { consignmentNo: string | null; weightKg: number | null; remark: string | null },
) {
  const response = await apiClient.put(pallet(palletGuid, "/header"), input);
  return normalizePalletDetail(response.data);
}

export async function sealPallet(palletGuid: string, weightKg: number | null) {
  const response = await apiClient.post(pallet(palletGuid, "/seal"), { weightKg });
  return normalizePalletDetail(response.data);
}

export async function reopenPallet(palletGuid: string) {
  const response = await apiClient.post(pallet(palletGuid, "/reopen"), {});
  return normalizePalletDetail(response.data);
}

export async function voidPallet(palletGuid: string) {
  const response = await apiClient.post(pallet(palletGuid, "/void"), {});
  return normalizePalletDetail(response.data);
}

export async function setPalletLocation(palletGuid: string, locationCode: string | null) {
  const response = await apiClient.put(pallet(palletGuid, "/location"), { locationCode });
  return normalizePalletDetail(response.data);
}

/** 发出整个托盘：托盘上的箱一起变成在路上，由对方扫码签收。 */
export async function dispatchPallet(palletGuid: string) {
  const response = await apiClient.post(pallet(palletGuid, "/dispatch"), {});
  return normalizePalletDetail(response.data);
}

/**
 * 签收托盘。整托签收必须带实到箱数（expectedCartonCount），和系统记录不一致返回 409 COUNT_MISMATCH，
 * 这时改走逐箱（传 cartonGuids，不要求数量）。viaCartonNo：托盘标签丢了、是扫箱标签找到托盘时传扫到的箱号。
 */
export async function receivePallet(
  palletGuid: string,
  input: {
    expectedCartonCount?: number | null;
    cartonGuids?: string[] | null;
    locationCode?: string | null;
    viaCartonNo?: string | null;
  },
) {
  const response = await apiClient.post(pallet(palletGuid, "/receive"), input);
  return normalizePalletDetail(response.data);
}

// ---- 签收 ----

/** 待签收（发给本地点、还没签完）和最近签收的托盘 / 散箱。 */
export async function fetchIncoming(ownerCode: string, signal?: AbortSignal) {
  const response = await apiClient.get(`${BASE}/incoming`, { params: { ownerCode }, signal });
  return normalizeIncoming(response.data);
}

/** 扫托盘标签或箱标签找到要签收的东西；托盘上任意一只箱的标签同样返回整个托盘。 */
export async function lookupForReceive(code: string, signal?: AbortSignal) {
  const response = await apiClient.get(`${BASE}/receive/lookup`, { params: { code }, signal });
  return normalizeReceiveLookup(response.data);
}

// ---- 扫码识别、位置查询、主数据 ----

/** 识别箱号 / 托盘号；都不是时后端返回 404 CODE_NOT_FOUND。 */
export async function resolvePackCode(code: string) {
  const response = await apiClient.get(`${BASE}/resolve`, { params: { code } });
  return normalizeResolve(response.data);
}

/** allStores 只有店长 / 管理员可用，否则 403。 */
export async function locateProduct(ownerCode: string, code: string, allStores: boolean, signal?: AbortSignal) {
  const response = await apiClient.get(`${BASE}/locate`, {
    params: { ownerCode, code, allStores: allStores || undefined },
    signal,
  });
  return normalizeLocate(response.data);
}

export async function fetchCarriers(signal?: AbortSignal) {
  const response = await apiClient.get(`${BASE}/carriers`, { signal });
  return normalizeCarriers(response.data);
}

/** 任何人都可以现场新增运输公司。 */
export async function createCarrier(name: string) {
  const response = await apiClient.post(`${BASE}/carriers`, { name, isActive: true });
  return normalizeCarrier(response.data);
}

/** 分店返回本店自己维护的存放位置；总仓返回现有货位。 */
export async function fetchLocations(ownerCode: string, signal?: AbortSignal) {
  const response = await apiClient.get(`${BASE}/locations`, { params: { ownerCode }, signal });
  return normalizeLocations(response.data);
}

/** 新增存放位置：只有店长；分店位置自动带条码（LC000001）。 */
export async function createLocation(ownerCode: string, name: string) {
  const response = await apiClient.post(`${BASE}/locations`, { ownerCode, name, isActive: true });
  return normalizeLocation(response.data);
}
