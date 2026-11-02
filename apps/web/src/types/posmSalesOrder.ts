export enum OrderType {
  All = -1,
  Pending = 0,
  Paid = 1,
  Cancelled = 2,
  Refunded = 3,
  Installment = 4,
}

export enum OrderStatus {
  Pending = 0,
  Paid = 1,
  Cancelled = 2,
  Refunded = 3,
  Installment = 4,
}

/** 关键词命中的明细商品：告诉用户这单为什么被搜出来。 */
export interface PosmSalesOrderMatchedProduct {
  productCode: string
  itemNumber?: string | null
  productName?: string | null
  barcode?: string | null
  quantity: number
}

export interface PosmSalesOrder {
  orderGuid?: string
  branchCode?: string
  branchName?: string
  deviceCode?: string
  orderTime?: string
  skuCount?: number
  /** POS 写入的 ItemCount 实际是明细行数，不是件数；件数看 quantityTotal。 */
  itemCount?: number
  /** 件数：明细数量之和。 */
  quantityTotal?: number
  totalAmount?: number
  discountAmount?: number
  actualAmount?: number
  status?: number
  /** 支付方式（去重）：1 现金、2 刷卡、3 代金券。 */
  paymentMethods?: number[]
  matchedProducts?: PosmSalesOrderMatchedProduct[] | null
}

/** 按订单状态汇总；不受状态筛选影响，页面据此展示各状态并切换。 */
export interface PosmSalesOrderStatusSummary {
  status: number | null
  orderCount: number
  totalAmount: number
  discountAmount: number
}

export interface PosmSalesOrderListResult {
  items: PosmSalesOrder[]
  /** 当前状态筛选下的单数。 */
  total: number
  summary: PosmSalesOrderStatusSummary[]
}

export interface PosmSalesOrderDetail {
  productImage?: string
  productCode?: string
  itemNumber?: string
  productName?: string
  quantity?: number
  unitPrice?: number
  discountAmount?: number
  actualAmount?: number
}

export interface PosmPaymentDetail {
  paymentTime?: string
  paymentMethod?: number
  paymentMethodName?: string
  amount?: number
}

export interface PosmSalesOrderDetailResponse {
  order?: PosmSalesOrder
  orderDetails?: PosmSalesOrderDetail[]
  paymentDetails?: PosmPaymentDetail[]
}

export interface PosmSalesOrderQueryParams {
  startDate?: string
  endDate?: string
  branchCode?: string
  orderType?: OrderType
  keyword?: string
  deviceCodeKeyword?: string
  timeStart?: string
  timeEnd?: string
  skuCountMin?: number
  skuCountMax?: number
  quantityMin?: number
  quantityMax?: number
  actualPayMin?: number
  actualPayMax?: number
  sortField?: PosmSalesOrderSortField
  sortDirection?: PosmSalesOrderSortDirection
  pageNumber?: number
  pageSize?: number
}

export type PosmSalesOrderSortField =
  | 'orderTime'
  | 'branchCode'
  | 'totalAmount'
  | 'discountAmount'
  | 'actualPay'

export type PosmSalesOrderSortDirection = 'asc' | 'desc'

export interface PosmSalesOrderSortState {
  field: PosmSalesOrderSortField
  direction: PosmSalesOrderSortDirection
}

/**
 * 在线小票：后端已按收银机 42 列规则排好版，前端只按行绘制。
 * kind：text 文本行、separator 分隔线、blank 空行、barcode Code128（text 为条码内容）、qr 二维码（text 为内容）。
 */
export interface PosReceiptLine {
  kind: 'text' | 'separator' | 'blank' | 'barcode' | 'qr'
  text: string
  align: 'left' | 'center'
  bold: boolean
}

export interface PosReceiptDocument {
  orderGuid: string
  /** 订单 GUID 去横杠后末 8 位大写。 */
  shortCode: string
  /** 只用于纸外状态标签着色，纸面文字保持黑白。 */
  statusKind: 'paid' | 'refund'
  lines: PosReceiptLine[]
}

export interface PosReceiptShareLink {
  url: string
  expiresAtUtc: string
  createdAtUtc: string
  openCount: number
}

export interface PosReceiptStaffView {
  document: PosReceiptDocument
  link: PosReceiptShareLink | null
}
