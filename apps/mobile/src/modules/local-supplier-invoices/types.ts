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
  // ---- 以下为与 Web 明细页对齐新增的字段（后端 details 接口本来就返回，之前被归一化丢掉）----
  /** 副码（除主条码外的其他条码）。 */
  additionalBarcodes: string[];
  /** 商品主档类型：0 单品 / 1 套装 / 2 多码；未匹配主档为 null。 */
  productType: number | null;
  /** 本单新品：关联商品由本进货单新建。 */
  isCreatedByThisInvoice: boolean;
  /** 主档命中数：0 不存在、>0 已存在；null = 还没做过商品检测（「未检测」，不能当成 0）。 */
  existingProductCount: number | null;
  /** 0 未检测 / 1 正常 / 其他按 barcodeMatchCount 区分无匹配、多匹配。 */
  barcodeStatus: number;
  barcodeMatchCount: number;
  /** 行操作类型 DetailAction：0 无 / 1 新建商品 / 2 更新进货价 / 3 等待操作 / 4 更新货号 / 5 添加多码 / 99 已执行。 */
  activityType: number;
  /** 折扣率，小数口径（0.1 = 10%）。 */
  discountRate: number | null;
  autoPricing: boolean | null;
  pricingFloatRate: number | null;
  newAutoRetailPrice: number | null;
  isSpecialProduct: boolean;
  /** 本店分店价格表里的自动定价状态，仅展示；与本行要写入的 autoPricing 不同。 */
  storeIsAutoPricing: boolean | null;
}

/** 表头可编辑字段（对应 Web UpdateInvoiceRequest 中本页实际发送的 5 项）。 */
export interface UpdateInvoiceHeaderRequest {
  storeCode?: string;
  supplierCode?: string;
  orderDate?: string;
  inboundDate?: string;
  remarks?: string;
}

/** 条码已被其他商品占用时，后端给出的匹配商品。 */
export interface BarcodeMatchedProduct {
  productCode: string;
  productName: string;
  supplierCode: string;
  supplierName: string;
  itemNumber: string;
  barcode: string;
  productImage: string;
  isMultiCode: boolean;
  isBundle: boolean;
  productType: number | null;
}

/** 商品检测单行结果（只取手机端复核「选用匹配商品」要用的字段，其余由后端落库、刷新明细即可见）。 */
export interface ProductCheckRowResult {
  detailGuid: string;
  /** 0 未知 / 1 已存在 / 2 不存在 */
  productStatus: number;
  /** 0 未检测 / 1 正常 / 2 异常 */
  barcodeStatus: number;
  existingProductCount: number;
  barcodeMatchCount: number;
  /** 检测命中的主档商品编码（productInfo.productCode）。 */
  productCode: string;
}
