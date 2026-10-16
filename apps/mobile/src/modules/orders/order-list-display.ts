import type { StoreOrderDetail, StoreOrderDetailLine } from "./types";

export const DEFAULT_ORDER_LIST_PAGE_SIZE = 10;

export interface StoreOrderListRequestParams {
  storeCode?: string;
  /** 账号模式的全部可管理分店范围；与 storeCode 二选一。 */
  storeCodes?: string[];
  pageNumber?: number;
  pageSize?: number;
  statusList?: number[];
  keyword?: string;
  /** 商品匹配：货号 / 条码 / 商品名，命中任一订单明细即返回该订单（后端 ProductKeyword，与 keyword 取交集）。 */
  productKeyword?: string;
  /** 下单日期区间，YYYY-MM-DD，含首尾；留空表示不限。 */
  startDate?: string;
  endDate?: string;
}

export function buildOrderListRequest(params: StoreOrderListRequestParams = {}) {
  return {
    storeCode: params.storeCode,
    storeCodes: params.storeCodes?.length ? params.storeCodes : undefined,
    pageNumber: params.pageNumber ?? 1,
    pageSize: params.pageSize ?? DEFAULT_ORDER_LIST_PAGE_SIZE,
    statusList: params.statusList ?? [1, 3, 2],
    keyword: params.keyword,
    productKeyword: params.productKeyword?.trim() || undefined,
    startDate: params.startDate?.trim() || undefined,
    endDate: params.endDate?.trim() || undefined,
  };
}

export function getOrderRowNumber(pageNumber: number, pageSize: number, index: number) {
  return (Math.max(1, pageNumber) - 1) * pageSize + index + 1;
}

export function formatOrderDate(value: string | undefined, localeTag: string) {
  if (!value) {
    return "--";
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }

  // 列表日期只展示业务日期，避免后端零点时间污染卡片信息。
  return date.toLocaleDateString(localeTag);
}

export function filterOrderDetailLinesByItemNumber(
  items: StoreOrderDetailLine[],
  keyword: string
) {
  const normalizedKeyword = keyword.trim().toLocaleLowerCase();
  if (!normalizedKeyword) {
    return items;
  }

  return items.filter((item) => {
    const itemNumber = item.itemNumber?.trim();
    // 货号存在时只按货号筛选；货号为空才允许条码兜底。
    const searchableValue = itemNumber || item.barcode?.trim() || "";
    return searchableValue.toLocaleLowerCase().includes(normalizedKeyword);
  });
}

export function getOrderDetailLineAllocatedImportAmount(item: StoreOrderDetailLine) {
  return item.allocatedImportAmount ?? Number(item.allocQuantity ?? 0) * Number(item.importPrice ?? 0);
}

export function getOrderDetailTotalAllocatedImportAmount(detail: StoreOrderDetail | null | undefined) {
  if (!detail) {
    return undefined;
  }

  return (
    detail.totalAllocatedImportAmount ??
    detail.items.reduce((sum, item) => sum + getOrderDetailLineAllocatedImportAmount(item), 0)
  );
}
