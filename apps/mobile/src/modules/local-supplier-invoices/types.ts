export type InvoiceListPageSize = 20 | 50 | 100;
export type InvoiceDetailPageSize = 50 | 100 | 200;
export type SortDirection = "asc" | "desc";
export type InvoiceDetailPriceChangeFilter = "all" | "up" | "down";

export interface InvoiceGridFilters {
  storeCode?: string;
  supplierCode?: string;
  invoiceNo?: string;
  inboundStatus?: 0 | 1 | 2;
  orderDateFrom?: string;
  orderDateTo?: string;
  /** 商品匹配：货号 / 条码 / 商品编码 / 商品名，命中任一明细即返回该进货单（后端 ProductKeyword 过滤）。 */
  productKeyword?: string;
}

export interface LocalSupplierOption {
  supplierCode: string;
  supplierName: string;
}

export interface InvoiceGridSort {
  colId: "OrderDate" | "InvoiceNo" | "StoreName" | "SupplierName";
  direction: SortDirection;
}

export interface InvoiceGridQuery {
  page?: number;
  pageSize?: number;
  filters?: InvoiceGridFilters;
  sort?: InvoiceGridSort;
}

export interface InvoiceDetailsGridQuery {
  page?: number;
  pageSize?: number;
  priceChange?: InvoiceDetailPriceChangeFilter;
  keyword?: string;
}

/** 后端入库状态：0 未入库，1 部分入库，2 已入库；其他值保持中性。 */
export type InvoiceInboundStatusLabel = "notReceived" | "partial" | "received" | "unknown";

export function getInvoiceInboundStatusLabel(status: number | null | undefined): InvoiceInboundStatusLabel {
  switch (status) {
    case 0: return "notReceived";
    case 1: return "partial";
    case 2: return "received";
    default: return "unknown";
  }
}

export interface GridFilterModel {
  filterType: "text" | "number" | "date" | "set";
  type: string;
  filter?: string;
  filterTo?: string;
  values?: string[];
}

export interface GridRequest {
  startRow: number;
  endRow: number;
  pageSize: number;
  filterModel?: Record<string, GridFilterModel>;
  sortModel?: { colId: string; sort: SortDirection }[];
}

export interface GridResult<T> {
  items: T[];
  total: number;
}

export interface LocalSupplierInvoice {
  invoiceGuid: string;
  storeCode: string;
  storeName: string;
  supplierCode: string;
  supplierName: string;
  invoiceNo: string;
  orderDate: string;
  inboundDate: string;
  totalAmount: number | null;
  receivedTotalAmount: number | null;
  priceIncreaseItemCount: number;
  priceDecreaseItemCount: number;
  flowStatus: number | null;
  inboundStatus: number | null;
  remarks: string;
  createdAt: string;
  updatedAt: string;
}

export interface LocalSupplierInvoiceItem {
  detailGuid: string;
  invoiceGuid: string;
  storeCode: string;
  supplierCode: string;
  productCode: string;
  storeProductCode: string;
  itemNumber: string;
  barcode: string;
  productName: string;
  specification: string;
  unit: string;
  quantity: number | null;
  lastPurchasePrice: number | null;
  purchasePrice: number | null;
  retailPrice: number | null;
  amount: number | null;
  productImage: string;
}
