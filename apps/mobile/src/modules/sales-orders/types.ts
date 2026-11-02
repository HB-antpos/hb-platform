export interface SalesOrderRange {
  startDate: string;
  endDate: string;
}

export interface SalesOrderRangeInfo extends SalesOrderRange {
  dayCount: number;
}

/** 与后端 OrderType 枚举一致：-1 表示全部，其余对应订单 Status。 */
export type SalesOrderTypeFilter = -1 | 0 | 1 | 2 | 3 | 4;

/**
 * 「已退款」筛选的档位：all 全部含退款商品的订单；partial 部分退款（整单金额不为负，橙色）；
 * full 整单退款（整单金额为负，红色）。只在订单类型选「已退款」时生效。
 */
export type SalesOrderRefundTier = "all" | "partial" | "full";

/** 订单含退款商品时的着色：partial 橙色、full 红色；不含退款商品为 null。 */
export type SalesOrderRefundTone = "partial" | "full";

export type SalesOrderSortDirection = "asc" | "desc";

export type SalesOrderScope = "all-stores" | "authorized-stores";

export interface SalesOrderFilters {
  range: SalesOrderRange;
  /** 空数组表示不限分店（在授权范围内）。 */
  branchCodes: string[];
  orderType: SalesOrderTypeFilter;
  /** 订单类型选「已退款」时的档位；其它类型下恒为 all。 */
  refundTier: SalesOrderRefundTier;
  sortDirection: SalesOrderSortDirection;
}

export interface SalesOrderMatchedProduct {
  productCode: string;
  /** 货号来自商品主档，主档没有对应商品时为空。 */
  itemNumber: string | null;
  productName: string | null;
  barcode: string | null;
  quantity: number;
}

export interface SalesOrderListItem {
  orderGuid: string;
  branchCode: string | null;
  branchName: string | null;
  deviceCode: string | null;
  orderTime: string | null;
  skuCount: number | null;
  /** POS 写入的 ItemCount 是明细行数。 */
  itemCount: number | null;
  /** 件数：明细数量之和，列表接口聚合返回。 */
  quantityTotal: number | null;
  totalAmount: number | null;
  discountAmount: number | null;
  actualAmount: number | null;
  status: number | null;
  /**
   * 订单含退款商品：旧 POS 的退款单（状态=已退款），或订单里夹带了退货记录。
   * 颜色看整单金额正负（见 resolveSalesOrderRefundTone）。旧后端不返回时按 false。
   */
  hasRefundItems: boolean;
  matchedProducts: SalesOrderMatchedProduct[];
}

export interface SalesOrderListPage {
  items: SalesOrderListItem[];
  total: number;
  pageNumber: number;
  pageSize: number;
  scope: SalesOrderScope;
  range: SalesOrderRangeInfo;
  sortDirection: SalesOrderSortDirection;
}

export interface SalesOrderBranch {
  storeCode: string;
  storeName: string;
}

export interface SalesOrderBranchCatalog {
  scope: SalesOrderScope;
  branches: SalesOrderBranch[];
}

export interface SalesOrderLine {
  productCode: string | null;
  itemNumber: string | null;
  productName: string | null;
  productImage: string | null;
  quantity: number | null;
  unitPrice: number | null;
  discountAmount: number | null;
  actualAmount: number | null;
}

export interface SalesOrderPayment {
  paymentTime: string | null;
  paymentMethod: number | null;
  paymentMethodName: string | null;
  amount: number | null;
}

export interface SalesOrderDetail {
  order: Omit<SalesOrderListItem, "matchedProducts">;
  lines: SalesOrderLine[];
  payments: SalesOrderPayment[];
}

export interface SalesOrderQueryBody {
  startDate: string;
  endDate: string;
  branchCodes: string[];
  orderType: SalesOrderTypeFilter;
  /** 退货筛选：any / partial / full；不筛选为 null。选「已退款」时 orderType 发 -1，由它决定范围。 */
  refundFilter: "any" | "partial" | "full" | null;
  keyword: string | null;
  sortDirection: SalesOrderSortDirection;
  pageNumber: number;
  pageSize: number;
}

export type SalesOrderRangeError = "format" | "order" | "tooLong";

export interface SalesOrderRangeValidation {
  ok: boolean;
  dayCount: number;
  reason?: SalesOrderRangeError;
  /** 超限时给出以结束日为锚点收敛后的合法起始日，供一键修复使用。 */
  clampedStartDate?: string;
}

export type SalesOrderRangePreset = 1 | 7 | 30;

/**
 * 在线小票：后端已按收银机 42 列规则把整张小票排成逐行内容，移动端只按行绘制。
 * kind：text 文本行、separator 分隔线、blank 空行、barcode Code128（text 为条码内容）、qr 二维码（text 为内容）。
 */
export interface SalesOrderReceiptLine {
  kind: "text" | "separator" | "blank" | "barcode" | "qr";
  text: string;
  align: "left" | "center";
  bold: boolean;
}

export interface SalesOrderReceiptDocument {
  orderGuid: string;
  /** 订单 GUID 去横杠后末 8 位大写。 */
  shortCode: string;
  /** 只用于纸外状态着色，纸面文字保持黑白。 */
  statusKind: "paid" | "refund";
  lines: SalesOrderReceiptLine[];
}

export interface SalesOrderReceiptLink {
  url: string;
  expiresAtUtc: string;
  createdAtUtc: string;
  openCount: number;
}

export interface SalesOrderReceiptView {
  document: SalesOrderReceiptDocument;
  link: SalesOrderReceiptLink | null;
}
