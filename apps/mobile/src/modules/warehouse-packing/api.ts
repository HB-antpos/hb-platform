import { apiClient } from "@/shared/api/client";
import {
  normalizeCarrier,
  normalizeCarriers,
  normalizeCartonDetail,
  normalizeCartonList,
  normalizeLocate,
  normalizeOrderOptions,
  normalizePalletDetail,
  normalizePalletList,
  normalizeResolve,
} from "./api-normalization";
import type { CartonFilter, PackDestinationRequest, PalletFilter } from "./types";

const BASE = "/react/v1/warehouse-packing";
const carton = (cartonGuid: string, suffix = "") => `${BASE}/cartons/${encodeURIComponent(cartonGuid)}${suffix}`;
const pallet = (palletGuid: string, suffix = "") => `${BASE}/pallets/${encodeURIComponent(palletGuid)}${suffix}`;

// ---- 箱 ----

export async function fetchCartons(filter: CartonFilter, keyword: string, mine: boolean, signal?: AbortSignal) {
  const response = await apiClient.get(`${BASE}/cartons`, {
    params: { filter, keyword: keyword.trim() || undefined, mine: mine || undefined },
    signal,
  });
  return normalizeCartonList(response.data);
}

export async function createCarton(input: PackDestinationRequest & { orderGuid?: string | null }) {
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

/** 重新开箱：只有仓库经理 / 管理员可以，其他人返回 403 PACKING_NOT_ALLOWED。 */
export async function reopenCarton(cartonGuid: string) {
  const response = await apiClient.post(carton(cartonGuid, "/reopen"), {});
  return normalizeCartonDetail(response.data);
}

export async function voidCarton(cartonGuid: string) {
  const response = await apiClient.post(carton(cartonGuid, "/void"), {});
  return normalizeCartonDetail(response.data);
}

/** 货位编码或货位条码；传 null 清除暂存货位。 */
export async function setCartonLocation(cartonGuid: string, locationCode: string | null) {
  const response = await apiClient.put(carton(cartonGuid, "/location"), { locationCode });
  return normalizeCartonDetail(response.data);
}

// ---- 托盘 ----

export async function fetchPallets(filter: PalletFilter, keyword: string, signal?: AbortSignal) {
  const response = await apiClient.get(`${BASE}/pallets`, {
    params: { filter, keyword: keyword.trim() || undefined },
    signal,
  });
  return normalizePalletList(response.data);
}

export async function createPallet(input: PackDestinationRequest) {
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

// ---- 扫码识别、位置查询、主数据 ----

/** 识别箱号 / 托盘号；都不是时后端返回 404 CODE_NOT_FOUND。 */
export async function resolvePackCode(code: string) {
  const response = await apiClient.get(`${BASE}/resolve`, { params: { code } });
  return normalizeResolve(response.data);
}

export async function locateProduct(code: string, signal?: AbortSignal) {
  const response = await apiClient.get(`${BASE}/locate`, { params: { code }, signal });
  return normalizeLocate(response.data);
}

export async function fetchCarriers(signal?: AbortSignal) {
  const response = await apiClient.get(`${BASE}/carriers`, { signal });
  return normalizeCarriers(response.data);
}

export async function createCarrier(name: string) {
  const response = await apiClient.post(`${BASE}/carriers`, { name, isActive: true });
  return normalizeCarrier(response.data);
}

export async function searchPackOrders(keyword: string, signal?: AbortSignal) {
  const response = await apiClient.get(`${BASE}/orders`, { params: { keyword: keyword.trim() || undefined }, signal });
  return normalizeOrderOptions(response.data);
}
