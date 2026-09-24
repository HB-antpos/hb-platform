import type { SupplyPlan } from './supplyNotice'

export enum StoreOrderFlowStatus {
  ShoppingCart = 0,
  Submitted = 1,
  Completed = 2,
  Picking = 3,
}

export interface StoreOrderStatusOption {
  value: StoreOrderFlowStatus
  label: string
  color: string
}

export const StoreOrderStatusOptions: StoreOrderStatusOption[] = [
  { value: StoreOrderFlowStatus.ShoppingCart, label: '购物车', color: 'default' },
  { value: StoreOrderFlowStatus.Submitted, label: '已提交', color: 'processing' },
  { value: StoreOrderFlowStatus.Completed, label: '已完成', color: 'success' },
  { value: StoreOrderFlowStatus.Picking, label: '配货中', color: 'warning' },
]

export const StoreOrderStatusLabelMap = Object.fromEntries(
  StoreOrderStatusOptions.map((item) => [item.value, item.label]),
) as Record<StoreOrderFlowStatus, string>

export const StoreOrderStatusColorMap = Object.fromEntries(
  StoreOrderStatusOptions.map((item) => [item.value, item.color]),
) as Record<StoreOrderFlowStatus, string>

export interface StoreOrderBranchOption {
  guid: string
  code: string
  name: string
}

export interface StoreOrderListQuery {
  keyword?: string
  /** 商品维度关键字：按货号、条码或商品名筛选包含该商品的订单。 */
  productKeyword?: string
  storeCode?: string
  storeCodes?: string[]
  startDate?: string
  endDate?: string
  statusList?: StoreOrderFlowStatus[]
  columnFilters?: StoreOrderListColumnFilters
  pageNumber: number
  pageSize: number
  sortBy?: string
  sortDescending?: boolean
}

export interface StoreOrderListColumnFilters {
  orderNo?: string
  outboundDateStart?: string
  outboundDateEnd?: string
  totalQuantityMin?: number
  totalQuantityMax?: number
  totalOrderAmountMin?: number
  totalOrderAmountMax?: number
  totalOrderVolumeMin?: number
  totalOrderVolumeMax?: number
  totalAllocVolumeMin?: number
  totalAllocVolumeMax?: number
  totalAllocQuantityMin?: number
  totalAllocQuantityMax?: number
  importTotalAmountMin?: number
  importTotalAmountMax?: number
  remarks?: string
  createdAtStart?: string
  createdAtEnd?: string
  updatedBy?: string
  updatedAtStart?: string
  updatedAtEnd?: string
}

export interface StoreOrderListItem {
  orderGUID: string
  orderNo: string
  storeCode?: string
  storeName?: string
  orderDate?: string
  outboundDate?: string
  flowStatus: StoreOrderFlowStatus
  totalAmount: number
  oemTotalAmount: number
  importTotalAmount: number
  totalOrderAmount: number
  totalQuantity: number
  totalAllocQuantity: number
  totalOrderVolume?: number
  totalAllocVolume?: number
  remarks?: string
  createdAt?: string
  createdBy?: string
  updatedAt?: string
  updatedBy?: string
}

export interface StoreOrderListResult {
  items: StoreOrderListItem[]
  total: number
  page: number
  pageSize: number
}

export type StoreOrderImportPriceVarianceDirection = 'all' | 'increase' | 'decrease'

export interface StoreOrderImportPriceVarianceQuery {
  keyword?: string
  storeCode?: string
  storeCodes?: string[]
  supplierCode?: string
  orderNo?: string
  startDate?: string
  endDate?: string
  varianceDirection?: StoreOrderImportPriceVarianceDirection
  pageNumber: number
  pageSize: number
  sortBy?: string
  sortDescending?: boolean
}

export interface StoreOrderImportPriceVarianceItem {
  productCode: string
  itemNumber?: string
  productName?: string
  productImage?: string
  supplierCode?: string
  supplierName?: string
  domesticPrice?: number
  warehouseImportPrice?: number
  unitVolume?: number
  packingQuantity?: number
  firstContainerImportPrice: number
  allocQuantityTotal: number
  originalImportAmountTotal: number
  baselineImportAmountTotal: number
  varianceAmountTotal: number
  detailCount: number
  firstContainerCode?: string
  firstContainerNumber?: string
  firstContainerDate?: string
}

export interface StoreOrderImportPriceVarianceSummary {
  totalRows: number
  originalImportAmountTotal: number
  baselineImportAmountTotal: number
  varianceAmountTotal: number
}

export interface StoreOrderImportPriceVarianceSupplierSummary {
  supplierCode?: string
  supplierName?: string
  productCount: number
  detailCount: number
  originalImportAmountTotal: number
  baselineImportAmountTotal: number
  increaseVarianceAmountTotal: number
  decreaseVarianceAmountTotal: number
  varianceAmountTotal: number
}

export interface StoreOrderImportPriceVarianceResult {
  items: StoreOrderImportPriceVarianceItem[]
  total: number
  page: number
  pageSize: number
  summary: StoreOrderImportPriceVarianceSummary
  supplierSummaries: StoreOrderImportPriceVarianceSupplierSummary[]
}

export interface StoreOrderImportPriceVarianceDomesticPriceUpdatePayload {
  productCode: string
  domesticPrice: number
}

export interface StoreOrderImportPriceVarianceDomesticPriceUpdateResult {
  productCode: string
  domesticPrice: number
}

export interface StoreOrderImportPriceVarianceWarehouseImportPriceUpdatePayload {
  productCode: string
  warehouseImportPrice: number
}

export interface StoreOrderImportPriceVarianceWarehouseImportPriceUpdateResult {
  productCode: string
  warehouseImportPrice: number
}

export interface StoreOrderImportPriceVarianceWarehouseImportPriceBatchUpdatePayload {
  productCodes: string[]
  warehouseImportPrice: number
}

export interface StoreOrderImportPriceVarianceWarehouseImportPriceBatchUpdateResult {
  updatedCount: number
  warehouseImportPrice: number
  productCodes: string[]
}

export interface StoreOrderImportPriceVarianceDetailQuery extends StoreOrderImportPriceVarianceQuery {
  productCode: string
}

export interface StoreOrderImportPriceVarianceDetailItem {
  orderGUID: string
  detailGUID: string
  orderNo?: string
  orderDate?: string
  storeCode?: string
  storeName?: string
  productCode?: string
  itemNumber?: string
  productName?: string
  orderImportPrice: number
  firstContainerImportPrice: number
  allocQuantity: number
  originalImportAmount: number
  baselineImportAmount: number
  varianceAmount: number
  firstContainerCode?: string
  firstContainerNumber?: string
  firstContainerDate?: string
}

export interface StoreOrderImportPriceVarianceDetailResult {
  items: StoreOrderImportPriceVarianceDetailItem[]
  total: number
  page: number
  pageSize: number
  summary: StoreOrderImportPriceVarianceSummary
}

export interface CreateStoreOrderPayload {
  storeCode: string
  remarks?: string
}

export interface CopyStoreOrderPayload {
  sourceOrderGUID: string
  targetStoreCode: string
  copyOrderQuantity: boolean
  copyAllocQuantity: boolean
}

export interface CopyStoreOrderResult {
  orderGUID: string
  orderNo?: string
}

export interface StoreOrderStatusUpdatePayload {
  orderGUID: string
  newStatus: StoreOrderFlowStatus
}

export interface StoreOrderBatchStatusUpdatePayload {
  orderGUIDs: string[]
  newStatus: StoreOrderFlowStatus
}

export interface UnmatchedStoreOrderGroup {
  sourceStoreCode: string
  sourceStoreName?: string
  orderCount: number
  latestOrderDate?: string
}

export interface StoreOrderStoreCodeMapping {
  sourceStoreCode: string
  targetStoreCode: string
}

export interface StoreOrderBatchMapStoreCodePayload {
  mappings: StoreOrderStoreCodeMapping[]
}

export interface StoreOrderStoreCodeMappingResultItem {
  sourceStoreCode: string
  targetStoreCode: string
  updatedCount: number
  skippedCount: number
}

export interface StoreOrderBatchMapStoreCodeResult {
  updatedCount: number
  skippedCount: number
  items: StoreOrderStoreCodeMappingResultItem[]
}

export interface SyncMissingStoreOrdersResult {
  success: boolean
  message: string
  mode?: StoreOrderSyncMode
  runId?: string
  ordersSynced: number
  detailsSynced: number
  ordersUpdated: number
  detailsUpdated: number
  ordersSoftDeleted?: number
  detailsSoftDeleted?: number
  hqOrderCount?: number
  hqDetailCount?: number
  shadowRowCount?: number
  durationMs?: number
  errors?: string[]
}

export interface SyncMissingStoreOrdersPayload {
  storeCodes?: string[]
  storeCode?: string
}

export type StoreOrderSyncMode = 'Full' | 'Incremental'
export type StoreOrderSyncConflictStrategy = 'LatestWins' | 'HqWins'

export interface StoreOrderHqSyncPayload extends SyncMissingStoreOrdersPayload {
  startDate?: string
  endDate?: string
  conflictStrategy?: StoreOrderSyncConflictStrategy
}

export type StoreOrderSyncJobStatus = 'Queued' | 'Running' | 'Succeeded' | 'Failed'

export interface StoreOrderSyncJobResult {
  jobId: string
  status: StoreOrderSyncJobStatus
  mode?: StoreOrderSyncMode
  conflictStrategy?: StoreOrderSyncConflictStrategy
  message?: string
  success?: boolean
  storeCodes?: string[]
  startDate?: string
  endDate?: string
  ordersSynced?: number
  detailsSynced?: number
  ordersUpdated?: number
  detailsUpdated?: number
  ordersSoftDeleted?: number
  detailsSoftDeleted?: number
  skippedOrdersBecauseLocalNewer?: number
  skippedDetailsBecauseLocalNewer?: number
  hqOrderCount?: number
  hqDetailCount?: number
  shadowRowCount?: number
  durationMs?: number
  errors?: string[]
}

export type StoreOrderDetailStatFilter = 'all' | 'orderedNotShipped' | 'shippedWithoutOrder'

export type StoreOrderDetailSortField =
  | 'itemNumber'
  | 'productName'
  | 'barcode'
  | 'locationCode'
  | 'quantity'
  | 'allocQuantity'
  | 'importPrice'
  | 'importAmount'
  | 'allocatedImportAmount'
  | 'isActive'

export interface StoreOrderDetailColumnFilters {
  itemNumber?: string
  productName?: string
  barcode?: string
  locationCode?: string
  quantityMin?: number
  quantityMax?: number
  allocQuantityMin?: number
  allocQuantityMax?: number
  importPriceMin?: number
  importPriceMax?: number
  isActive?: boolean
}

export interface StoreOrderDetailQuery {
  pageNumber: number
  pageSize: number
  keyword?: string
  statFilter?: StoreOrderDetailStatFilter
  columnFilters?: StoreOrderDetailColumnFilters
  sortBy?: StoreOrderDetailSortField
  sortDescending?: boolean
}

export interface StoreOrderDetailLine {
  detailGUID: string
  productCode: string
  itemNumber?: string
  barcode?: string
  productName?: string
  productImage?: string
  quantity: number
  allocQuantity?: number
  price: number
  amount: number
  importPrice: number
  importAmount: number
  allocatedImportAmount?: number
  volume?: number
  totalVolume?: number
  orderVolume?: number
  allocVolume?: number
  minOrderQuantity: number
  isActive: boolean
  locationCode?: string
  rrp?: number
}

export interface StoreOrderInvoiceEmailSentInfo {
  hasSent: boolean
  sentAt?: string
  toEmail?: string
  jobId?: string
}

export interface StoreOrderDetail {
  orderGUID: string
  orderNo?: string
  storeCode?: string
  storeName?: string
  totalAmount: number
  totalQuantity: number
  totalImportAmount: number
  totalAllocatedImportAmount?: number
  totalVolume: number
  totalOrderVolume?: number
  totalAllocVolume?: number
  remarks?: string
  shippingFee?: number
  orderDate?: string
  outboundDate?: string
  storeAddress?: string
  storeContactEmail?: string
  flowStatus?: StoreOrderFlowStatus
  totalAllocQuantity?: number
  totalSKU?: number
  itemsTotal: number
  invoiceEmailSentInfo?: StoreOrderInvoiceEmailSentInfo
  orderedNotShippedCount?: number
  shippedWithoutOrderCount?: number
  items: StoreOrderDetailLine[]
}

export interface StoreOrderProductQuery {
  storeCode?: string
  itemNumber?: string
  productName?: string
  categoryGUID?: string
  localSupplierCode?: string
  supplierCode?: string
  excludeExistingWarehouseProducts?: boolean
  includeInactiveWarehouseProducts?: boolean
  excludeOrderGUID?: string
  pageNumber: number
  pageSize: number
  sortBy?: string
  sortDescending?: boolean
  grade?: string
  columnFilters?: StoreOrderProductColumnFilters
}

export interface StoreOrderProductColumnFilters {
  itemNumber?: string
  productName?: string
  supplierKeyword?: string
  barcode?: string
  stockQuantityMin?: number
  stockQuantityMax?: number
  minOrderQuantityMin?: number
  minOrderQuantityMax?: number
  importPriceMin?: number
  importPriceMax?: number
}

export interface StoreOrderProductItem {
  productCode: string
  itemNumber?: string
  barcode?: string
  productName?: string
  productImage?: string
  localSupplierCode?: string
  localSupplierName?: string
  domesticSupplierCode?: string
  domesticSupplierName?: string
  categoryName?: string
  warehouseCategoryGUID?: string
  oemPrice?: number
  minOrderQuantity: number
  stockQuantity: number
  isInStock: boolean
  packQty?: number
  importPrice?: number
  grade?: string
}

export interface StoreOrderDynamicData {
  productCode: string
  lastOrderDate?: string
  lastQuantity?: number
  lastAllocQuantity?: number
  cartQuantity: number
  // 最近来货日至今的销量；后端未返回或不可用时为 null/undefined，卡片据此隐藏 Sales 入口。
  salesQuantitySinceLastArrival?: number | null
}

export interface StoreOrderDynamicDataRequest {
  storeCode: string
  productCodes: string[]
  includeSales?: boolean
}

export interface StoreOrderProductSalesSummaryQuery {
  storeCode: string
  productCodes: string[]
}

export interface StoreOrderProductSalesSummaryItem {
  productCode: string
  salesQuantitySinceLastArrival: number | null
}

export interface StoreOrderCartItem {
  detailGUID: string
  productCode: string
  itemNumber?: string
  barcode?: string
  productName?: string
  productImage?: string
  price: number
  quantity: number
  allocQuantity?: number
  amount: number
  importPrice: number
  importAmount: number
  allocatedImportAmount?: number
  volume?: number
  totalVolume?: number
  minOrderQuantity: number
  isActive: boolean
  /** 仓库登记的后续供货计划；只在已下架且有未关闭说明时有值，旧后端不返回。 */
  supplyPlan?: SupplyPlan | null
  locationCode?: string
  rrp?: number
}

/** 提交时因暂停供货而保留在购物车里的一行。 */
export interface SubmitStoreOrderKeptLine {
  detailGUID?: string
  productCode: string
  itemNumber?: string | null
  productName?: string | null
  quantity?: number
  supplyPlan?: SupplyPlan | null
}

/** 提交订单结果：在供货的行进单，暂停供货的行保留在购物车。旧后端只返回 true，归一化后 keptLines 为空。 */
export interface SubmitStoreOrderResult {
  submitted: boolean
  orderGUID?: string | null
  orderNo?: string | null
  submittedLineCount: number
  keptLines: SubmitStoreOrderKeptLine[]
  keptCartOrderGUID?: string | null
}

export interface StoreOrderCart {
  orderGUID: string
  orderNo?: string
  storeCode?: string
  storeName?: string
  totalAmount: number
  totalQuantity: number
  totalSKU?: number
  totalImportAmount: number
  totalAllocatedImportAmount?: number
  totalVolume: number
  remarks?: string
  shippingFee?: number
  orderDate?: string
  outboundDate?: string
  storeAddress?: string
  storeContactEmail?: string
  flowStatus?: StoreOrderFlowStatus
  invoiceEmailSentInfo?: StoreOrderInvoiceEmailSentInfo
  isSummaryOnly?: boolean
  items: StoreOrderCartItem[]
}

export interface StoreOrderProductListResult {
  items: StoreOrderProductItem[]
  total: number
  page: number
  pageSize: number
}

export type StoreOrderPasteTargetField = 'quantity' | 'allocQuantity'
export type StoreOrderPasteAction = 'replace' | 'append' | 'skip'

export interface StoreOrderBatchLookupItem {
  lookupCode: string
  product?: StoreOrderProductItem
}

export interface StoreOrderScanLookupResult {
  barcode: string
  items: StoreOrderProductItem[]
}

export type StoreOrderScanStatus =
  | 'ready'
  | 'scanning'
  | 'added'
  | 'multiple'
  | 'not_found'
  | 'blocked'
  | 'error'

export interface AddStoreOrderLinePayload {
  orderGUID: string
  productCode: string
  quantity: number
}

export interface BatchAddStoreOrderLinePayload {
  orderGUID: string
  items: Array<{
    productCode: string
    quantity: number
    importPrice?: number
  }>
}

export interface UpdateStoreOrderLinePayload {
  orderGUID: string
  productCode: string
  allocQuantity: number
  importPrice?: number
  syncImportPrice?: boolean
}

export interface RemoveStoreOrderLinePayload {
  orderGUID: string
  detailGUID: string
}

export interface BatchUpdateStoreOrderLinePayload {
  orderGUID: string
  items: Array<{
    detailGUID?: string
    productCode: string
    quantity?: number
    importPrice?: number
    syncImportPrice?: boolean
  }>
}

export interface RefreshStoreOrderImportPricesPayload {
  orderGUID: string
  detailGUIDs?: string[]
}

export interface RefreshStoreOrderImportPricesResult {
  updatedCount: number
  unchangedCount: number
  skippedCount: number
  missingWarehousePriceCount: number
}

export interface UpdateStoreOrderProductStatusPayload {
  productCode: string
  isActive: boolean
}

export interface BatchUpdateStoreOrderProductStatusPayload {
  productCodes: string[]
  isActive: boolean
}

export interface UpdateStoreOrderHeaderPayload {
  orderGUID: string
  remarks?: string
  shippingFee?: number
  storeCode?: string
  orderDate?: string
}

export interface UpdateStoreOrderOutboundDatePayload {
  orderGUID: string
  outboundDate?: string
  completeOrder?: boolean
}

export interface UpdateStoreOrderStoreContactPayload {
  orderGUID: string
  storeCode: string
  address?: string
  contactEmail?: string
}

export interface SendStoreOrderInvoiceEmailPayload {
  orderGUID: string
  toEmail: string
  subject?: string
  body?: string
}

export interface TranslateStoreOrderInvoiceEmailTextPayload {
  orderGUID: string
  targetLanguage: 'zh' | 'en'
  subject?: string
  body?: string
}

export interface TranslateStoreOrderInvoiceEmailTextResult {
  subject?: string
  body?: string
}

export type StoreOrderInvoiceEmailJobStatus = 'Queued' | 'Running' | 'Succeeded' | 'Failed'

export interface StoreOrderInvoiceEmailJobResult {
  jobId: string
  status: StoreOrderInvoiceEmailJobStatus
  message?: string
  orderGUID?: string
  toEmail?: string
  createdAt?: string
  completedAt?: string
}

export type StoreOrderPasteReplaceJobStatus = 'Queued' | 'Running' | 'Succeeded' | 'Failed'

export interface StoreOrderPasteReplaceJobResult {
  jobId: string
  status: StoreOrderPasteReplaceJobStatus
  message?: string
  orderGUID?: string
  targetField?: StoreOrderPasteTargetField
  totalCount?: number
  importedCount?: number
  skippedCount?: number
  createdAt?: string
  completedAt?: string
}

export interface StoreOrderBatchLookupPayload {
  codes: string[]
}

export interface PasteReplaceStoreOrderLinesPayload {
  orderGUID: string
  targetField: StoreOrderPasteTargetField
  items: Array<{
    productCode: string
    quantity: number
    importPrice?: number
    action?: StoreOrderPasteAction
  }>
}

// 商品活动记录类型：订货发货行、销售行或区间小计行。
export type StoreOrderProductActivityRecordType = 'order' | 'sales' | 'salesSubtotal'

// 商品活动记录筛选维度；all 表示合并一张时间轴表格。
export type StoreOrderProductActivityFilter = 'all' | 'order' | 'sales'

// 商品“订货 · 发货 · 销售”合并活动历史分页查询参数。
export interface StoreOrderProductActivityHistoryQuery {
  storeCode: string
  productCode: string
  pageNumber: number
  pageSize: number
  recordType: StoreOrderProductActivityFilter
}

// 商品活动历史行。订货行带订单字段，销售行带销量字段；未知/缺失字段由服务归一化防御。
export interface StoreOrderProductActivityHistoryItem {
  recordType: StoreOrderProductActivityRecordType
  recordDate?: string
  periodStartDate?: string
  periodEndDate?: string
  orderGUID?: string
  orderNo?: string
  orderDate?: string
  outboundDate?: string
  flowStatus?: number
  quantity?: number
  allocQuantity?: number
  salesQuantity?: number
  averagePrice?: number | null
}

// 商品活动历史分页结果，含紧凑摘要。
export interface StoreOrderProductActivityHistoryResult {
  items: StoreOrderProductActivityHistoryItem[]
  total: number
  pageNumber: number
  pageSize: number
  lastArrivalDate?: string | null
  endDate?: string | null
  latestOrderQuantity?: number
  latestAllocQuantity?: number
  totalSalesQuantity?: number
}
