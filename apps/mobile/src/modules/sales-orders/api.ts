import { apiClient } from "@/shared/api/client";
import {
  normalizeSalesOrderBranchCatalog,
  normalizeSalesOrderDetail,
  normalizeSalesOrderListPage,
  normalizeSalesOrderReceiptLink,
  normalizeSalesOrderReceiptView,
} from "./api-normalization";
import type { SalesOrderQueryBody } from "./types";

const API_BASE = "/react/v1/posm-sales-orders";

export async function fetchSalesOrders(body: SalesOrderQueryBody, signal?: AbortSignal) {
  const response = await apiClient.post(`${API_BASE}/mobile-list`, body, { signal });
  const page = normalizeSalesOrderListPage(response.data);
  if (
    page.range.startDate !== body.startDate ||
    page.range.endDate !== body.endDate ||
    page.pageNumber !== body.pageNumber ||
    page.sortDirection !== body.sortDirection
  ) {
    // 区间、页码或排序不一致说明响应串台，宁可报错也不能把别的条件的数据拼进当前列表。
    throw Object.assign(new Error("Sales order list scope mismatch"), {
      code: "SALES_ORDER_INVALID_RESPONSE",
    });
  }
  return page;
}

export async function fetchSalesOrderBranches(signal?: AbortSignal) {
  const response = await apiClient.get(`${API_BASE}/mobile-branches`, { signal });
  return normalizeSalesOrderBranchCatalog(response.data);
}

export async function fetchSalesOrderDetail(orderGuid: string, signal?: AbortSignal) {
  const response = await apiClient.get(
    `${API_BASE}/detail/${encodeURIComponent(orderGuid)}`,
    { signal },
  );
  const detail = normalizeSalesOrderDetail(response.data);
  if (detail.order.orderGuid !== orderGuid) {
    throw Object.assign(new Error("Sales order detail mismatch"), {
      code: "SALES_ORDER_INVALID_RESPONSE",
    });
  }
  return detail;
}

/** 在线小票：完整纸面（含银行回单）+ 当前有效分享链接。 */
export async function fetchSalesOrderReceipt(orderGuid: string, signal?: AbortSignal) {
  const response = await apiClient.get(
    `${API_BASE}/${encodeURIComponent(orderGuid)}/receipt`,
    { signal },
  );
  const view = normalizeSalesOrderReceiptView(response.data);
  if (view.document.orderGuid.toLowerCase() !== orderGuid.toLowerCase()) {
    // 与订单详情同样的串台保护：宁可报错也不能把别的订单的小票显示出来。
    throw Object.assign(new Error("Sales order receipt mismatch"), {
      code: "SALES_ORDER_INVALID_RESPONSE",
    });
  }
  return view;
}

/** 已有有效链接就原样返回，没有才新建。 */
export async function createSalesOrderReceiptLink(orderGuid: string) {
  const response = await apiClient.post(
    `${API_BASE}/${encodeURIComponent(orderGuid)}/receipt-link`,
    {},
  );
  return normalizeSalesOrderReceiptLink(response.data);
}

/** 撤销该订单所有有效链接并新建一条，旧链接立即失效。 */
export async function regenerateSalesOrderReceiptLink(orderGuid: string) {
  const response = await apiClient.post(
    `${API_BASE}/${encodeURIComponent(orderGuid)}/receipt-link/regenerate`,
    {},
  );
  return normalizeSalesOrderReceiptLink(response.data);
}
