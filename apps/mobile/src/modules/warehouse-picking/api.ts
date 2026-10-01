import * as Crypto from "expo-crypto";
import { apiClient } from "@/shared/api/client";
import {
  normalizePickerResolve,
  normalizePickLookup,
  normalizePickMutation,
  normalizePickOrderList,
  normalizePickProgress,
  normalizePickSheet,
  normalizePickSlipClaim,
  normalizePickSubmit,
} from "./api-normalization";
import { usePickerStore } from "./picker-store";
import type { PickOrderFilter } from "./types";

const BASE = "/react/v1/warehouse-picking";

/**
 * 扫员工码确认的拣货人通过请求头带凭证；账号本人拣货不带，由后端按登录账号记人。
 * 设备会话的 X-Device-Id / X-Auth-Code 由请求拦截器统一补齐。
 */
function pickerConfig() {
  const ticket = usePickerStore.getState().picker?.ticket;
  return ticket ? { headers: { "X-Picker-Ticket": ticket } } : {};
}

export function newClientRequestId() {
  return Crypto.randomUUID();
}

export async function resolvePicker(barcode: string) {
  const response = await apiClient.post(`${BASE}/picker/resolve`, { barcode });
  return normalizePickerResolve(response.data);
}

export async function fetchPickOrders(filter: PickOrderFilter, keyword: string, signal?: AbortSignal) {
  const response = await apiClient.get(`${BASE}/orders`, {
    params: { filter, keyword: keyword.trim() || undefined },
    signal,
  });
  return normalizePickOrderList(response.data);
}

export async function resolvePickOrder(code: string) {
  const response = await apiClient.get(`${BASE}/orders/resolve`, { params: { code } });
  const data = response.data as { orderGuid?: unknown } | null;
  if (typeof data?.orderGuid !== "string" || !data.orderGuid) {
    throw Object.assign(new Error("Invalid order resolve response"), { code: "WAREHOUSE_PICKING_INVALID_RESPONSE" });
  }
  return data.orderGuid;
}

export async function joinPickOrder(orderGuid: string) {
  const response = await apiClient.post(`${BASE}/orders/${encodeURIComponent(orderGuid)}/join`, {}, pickerConfig());
  return normalizePickSheet(response.data);
}

export async function fetchPickSheet(orderGuid: string, signal?: AbortSignal) {
  const response = await apiClient.get(`${BASE}/orders/${encodeURIComponent(orderGuid)}/sheet`, {
    ...pickerConfig(),
    signal,
  });
  return normalizePickSheet(response.data);
}

export async function fetchPickProgress(orderGuid: string, signal?: AbortSignal) {
  const response = await apiClient.get(`${BASE}/orders/${encodeURIComponent(orderGuid)}/progress`, {
    ...pickerConfig(),
    signal,
  });
  return normalizePickProgress(response.data);
}

export interface PickRecordInput {
  detailGuid: string;
  source: number;
  scannedCode?: string | null;
  matchedBy?: number | null;
  pieces?: number | null;
  clientRequestId: string;
}

export async function postPickRecord(orderGuid: string, input: PickRecordInput) {
  const response = await apiClient.post(
    `${BASE}/orders/${encodeURIComponent(orderGuid)}/records`,
    input,
    pickerConfig(),
  );
  return normalizePickMutation(response.data);
}

export async function putPickLineTotal(
  orderGuid: string,
  detailGuid: string,
  input: { total: number; expectedTotal: number; clientRequestId: string },
) {
  const response = await apiClient.put(
    `${BASE}/orders/${encodeURIComponent(orderGuid)}/lines/${encodeURIComponent(detailGuid)}/total`,
    input,
    pickerConfig(),
  );
  return normalizePickMutation(response.data);
}

export async function putMinOrderQuantity(orderGuid: string, detailGuid: string, minOrderQuantity: number) {
  const response = await apiClient.put(
    `${BASE}/orders/${encodeURIComponent(orderGuid)}/lines/${encodeURIComponent(detailGuid)}/min-order-quantity`,
    { minOrderQuantity },
    pickerConfig(),
  );
  const data = response.data as { minOrderQuantity?: unknown } | null;
  return typeof data?.minOrderQuantity === "number" ? data.minOrderQuantity : minOrderQuantity;
}

/** 标记“货位没货”：已拣的保留，剩余记为拣不到；同一行再次标记覆盖原因。 */
export async function putPickStockout(orderGuid: string, detailGuid: string, reason: number) {
  const response = await apiClient.put(
    `${BASE}/orders/${encodeURIComponent(orderGuid)}/lines/${encodeURIComponent(detailGuid)}/stockout`,
    { reason },
    pickerConfig(),
  );
  return normalizePickMutation(response.data);
}

/** 撤销“货位没货”标记，重复撤销是幂等的。 */
export async function deletePickStockout(orderGuid: string, detailGuid: string) {
  const response = await apiClient.delete(
    `${BASE}/orders/${encodeURIComponent(orderGuid)}/lines/${encodeURIComponent(detailGuid)}/stockout`,
    pickerConfig(),
  );
  return normalizePickMutation(response.data);
}

export async function submitPickOrder(orderGuid: string) {
  const response = await apiClient.post(`${BASE}/orders/${encodeURIComponent(orderGuid)}/submit`, {}, pickerConfig());
  return normalizePickSubmit(response.data);
}

/** 扫分单领取：该段还没人领时记到当前拣货人名下；已被领取时不改，只返回负责人。 */
export async function claimPickSlip(code: string) {
  const response = await apiClient.post(`${BASE}/slips/claim`, { code }, pickerConfig());
  return normalizePickSlipClaim(response.data);
}

export async function lookupPickCode(code: string) {
  const response = await apiClient.get(`${BASE}/lookup`, { params: { code } });
  return normalizePickLookup(response.data);
}
