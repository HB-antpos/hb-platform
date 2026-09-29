export interface LocalSupplierInvoiceListDto {
  invoiceGUID: string
  storeCode?: string
  storeName?: string
  supplierCode?: string
  supplierName?: string
  invoiceNo?: string
  remarks?: string
  orderDate?: string
  inboundDate?: string
  totalAmount?: number
  receivedTotalAmount?: number
  isProductChecked?: boolean
  flowStatus?: number
  inboundStatus?: number
  createdAt: string
  createdBy?: string
  updatedAt?: string
  updatedBy?: string
}

export interface LocalSupplierInvoiceDetailDto {
  invoiceGUID: string
  appGUID?: string
  pcGUID?: string
  storeCode?: string
  storeName?: string
  supplierCode?: string
  supplierName?: string
  invoiceNo?: string
  voucherType?: number
  orderDate?: string
  inboundDate?: string
  totalAmount?: number
  receivedTotalAmount?: number
  voucherImage?: string
  remarks?: string
  importTemplate?: string
  flowStatus?: number
  inboundStatus?: number
  createdAt: string
  updatedAt?: string
}

export interface LocalSupplierInvoiceItemDto {
  detailGUID: string
  invoiceGUID?: string
  storeCode?: string
  supplierCode?: string
  productTagGUID?: string
  productCategoryGUID?: string
  storeProductCode?: string
  productCode?: string
  itemNumber?: string
  barcode?: string
  additionalBarcodes?: string[]
  productName?: string
  /** 商品主档当前类型；未匹配主档时为空，仅用于展示。 */
  productType?: number | null
  specification?: string
  unit?: string
  quantity?: number
  lastPurchasePrice?: number
  purchasePrice?: number
  retailPrice?: number
  amount?: number
  existingProductCount?: number
  barcodeStatus?: number
  barcodeMatchCount?: number
  productImage?: string
  activityType?: number
  discountRate?: number
  autoPricing?: boolean
  pricingFloatRate?: number
  newAutoRetailPrice?: number
  isSpecialProduct?: boolean
  oldStoreProductCode?: string
}

export interface LocalSupplierInvoiceSalesAnalysisItemDto {
  detailGUID: string
  productCode?: string
  itemNumber?: string
  barcode?: string
  productName?: string
  productImage?: string
  specification?: string
  unit?: string
  quantity?: number
  purchasePrice?: number
  retailPrice?: number
  amount?: number
  salesQty30: number
  salesQty60: number
  salesQty90: number
  previousPurchaseDate?: string | null
  previousToCurrentDays?: number | null
  salesSincePreviousPurchase?: number | null
  salesSincePreviousPurchase30?: number | null
  salesSincePreviousPurchase60?: number | null
  salesSincePreviousPurchase90?: number | null
  salesStatisticLastUpdate?: string | null
}

export interface LocalSupplierInvoiceSalesAnalysisResponseDto {
  invoiceGUID: string
  invoiceNo?: string
  storeCode?: string
  storeName?: string
  supplierCode?: string
  supplierName?: string
  orderDate?: string | null
  inboundDate?: string | null
  analysisDate?: string | null
  salesStatisticLastUpdate?: string | null
  items: LocalSupplierInvoiceSalesAnalysisItemDto[]
  calculationNote: string
}

export type LocalSupplierPurchaseSalesAnalysisSortOrder = 'asc' | 'desc'

export interface LocalSupplierPurchaseSalesAnalysisQueryDto {
  storeCode?: string
  supplierCode?: string
  supplierCategoryGuids?: string[]
  orderDateStart?: string
  orderDateEnd?: string
  keyword?: string
  sortBy?: string
  sortOrder?: LocalSupplierPurchaseSalesAnalysisSortOrder
  page?: number
  pageSize?: number
}

export interface LocalSupplierPurchaseSalesAnalysisRowDto {
  storeCode: string
  storeName?: string
  productCode: string
  itemNumber?: string
  barcode?: string
  productName?: string
  productImage?: string
  supplierCode: string
  supplierName?: string
  latestPurchaseDate?: string | null
  latestPurchaseQty?: number | null
  previousPurchaseDate?: string | null
  previousPurchaseQty?: number | null
  purchaseIntervalDays?: number | null
  salesBetweenPurchases?: number | null
  salesQty30: number
  salesQty60: number
  salesQty90: number
  /** 最近进货当天起至今的累计净销量；由后端聚合，用于服务端排序。 */
  totalSalesSinceLatestPurchase: number
  salesStatisticLastUpdate?: string | null
  /** 上次进货（无则最近进货前 30 天）起至今的逐日净销量，缺失日期已补 0。 */
  dailySales: LocalSupplierPurchaseSalesDailyPointDto[]
  /** 图表窗口内的进货事件（上次 + 最近），数量为整数。 */
  purchases: LocalSupplierPurchaseSalesPurchaseEventDto[]
}

export interface LocalSupplierPurchaseSalesDailyPointDto {
  date: string
  quantity: number
}

export interface LocalSupplierPurchaseSalesPurchaseEventDto {
  date: string
  quantity: number
}

export interface LocalSupplierPurchaseSalesAnalysisResponseDto {
  items: LocalSupplierPurchaseSalesAnalysisRowDto[]
  total: number
  page: number
  pageSize: number
  salesStatisticLastUpdate?: string | null
  calculationNote: string
}

export interface LocalSupplierPurchaseSalesAnalysisStoreOptionDto {
  label: string
  value: string
}

export interface LocalSupplierPurchaseSalesAnalysisSupplierOptionDto {
  label: string
  value: string
}

export type ShopLocalSupplierInvoiceListPageSize = 20 | 50 | 100
export type ShopLocalSupplierInvoiceDetailsPageSize = 50 | 100 | 200

export interface ShopLocalSupplierInvoiceGridQuery {
  page?: number
  pageSize?: number
  storeCode?: string
  supplierCode?: string
  productKeyword?: string
}

export interface ShopLocalSupplierInvoiceGridTextFilter {
  filterType: 'text'
  type: 'equals' | 'contains'
  filter: string
}

export interface ShopLocalSupplierInvoiceGridRequest {
  startRow: number
  endRow: number
  pageSize: ShopLocalSupplierInvoiceListPageSize
  filterModel: Record<string, ShopLocalSupplierInvoiceGridTextFilter>
  sortModel: Array<{
    colId: 'OrderDate'
    sort: 'desc'
  }>
}

export interface ShopLocalSupplierInvoiceFilterOptionDto {
  value: string
  label: string
}

export interface ShopLocalSupplierInvoiceFilterOptionsDto {
  suppliers: ShopLocalSupplierInvoiceFilterOptionDto[]
}

export interface ShopLocalSupplierInvoiceDto {
  invoiceGUID: string
  storeCode?: string
  storeName?: string
  supplierCode?: string
  supplierName?: string
  invoiceNo?: string
  orderDate?: string
  inboundDate?: string
  totalAmount?: number
  receivedTotalAmount?: number
  flowStatus?: number
  inboundStatus?: number
  remarks?: string
}

export type ShopLocalSupplierInvoiceListItemDto = ShopLocalSupplierInvoiceDto

export interface ShopLocalSupplierInvoiceGridResult {
  items: ShopLocalSupplierInvoiceListItemDto[]
  total: number
}

export interface ShopLocalSupplierInvoiceItemDto {
  detailGUID: string
  storeProductCode?: string
  productCode?: string
  itemNumber?: string
  barcode?: string
  productName?: string
  productImage?: string
  specification?: string
  unit?: string
  quantity?: number
  lastPurchasePrice?: number
  purchasePrice?: number
  retailPrice?: number
  amount?: number
  newAutoRetailPrice?: number
}

export interface ShopLocalSupplierInvoiceDetailsGridQuery {
  page?: number
  pageSize?: number
}

export interface ShopLocalSupplierInvoiceDetailsGridResult {
  items: ShopLocalSupplierInvoiceItemDto[]
  total: number
}

export interface UpdateInvoiceRequest {
  storeCode?: string
  supplierCode?: string
  invoiceNo?: string
  orderDate?: string
  inboundDate?: string
  remarks?: string
  voucherImage?: string
  flowStatus?: number
  inboundStatus?: number
}

export interface InvoiceDetailUpsertItemDto {
  detailGUID?: string
  itemNumber?: string
  barcode?: string
  additionalBarcodes?: string[]
  productName?: string
  productCategoryGUID?: string
  storeProductCode?: string
  productCode?: string
  quantity?: number
  lastPurchasePrice?: number
  purchasePrice?: number
  retailPrice?: number
  amount?: number
  activityType?: number
  discountRate?: number
  autoPricing?: boolean
  pricingFloatRate?: number
  newAutoRetailPrice?: number
  isSpecialProduct?: boolean
}

export interface UpdateToStorePricesFields {
  updatePurchasePrice: boolean
  purchasePrice?: number
  updateRetailPrice: boolean
  retailPrice?: number
  updateIsAutoPricing: boolean
  isAutoPricing?: boolean
  updateIsSpecialProduct: boolean
  isSpecialProduct?: boolean
  updateDiscountRate: boolean
  discountRate?: number
}

export interface BatchEditFields {
  updatePurchasePrice: boolean
  purchasePrice?: number
  updateRetailPrice: boolean
  retailPrice?: number
  updateIsAutoPricing: boolean
  isAutoPricing?: boolean
  updateIsSpecialProduct: boolean
  isSpecialProduct?: boolean
  updateDiscountRate: boolean
  discountRate?: number
  updateAction: boolean
  action?: DetailAction
}

export interface UpdateToStorePricesRequest {
  invoiceGuid: string
  detailGuids: string[]
  targetStoreCodes: string[]
  updateFields: UpdateToStorePricesFields
}

export interface BatchResultDto {
  inserted: number
  updated: number
  failed: number
}

export interface UpdateToStorePricesResult extends BatchResultDto {
  skipped?: number
  updatedPurchasePrices?: number
  errors?: string[]
}

export interface UpdateLastPurchasePricesRequest {
  detailGuids?: string[]
}

export interface UpdateLastPurchasePricesResult {
  total: number
  updated: number
  skipped: number
  errors: string[]
}

export type LocalSupplierInvoiceBatchJobStatus = 'Running' | 'Succeeded' | 'Failed' | string

export interface LocalSupplierInvoiceJobBase {
  jobId: string
  invoiceGuid?: string
  targetStoreCodes?: string[]
  operationId: string
  status: LocalSupplierInvoiceBatchJobStatus
  isDuplicateRequest?: boolean
  createdAt?: string
  completedAt?: string
  expiresAt?: string
  message?: string
}

export interface UpdateToStorePricesJobDto extends LocalSupplierInvoiceJobBase {
  result?: UpdateToStorePricesResult
}

export type LocalSupplierInvoiceBatchJobBase = LocalSupplierInvoiceJobBase
export type UpdateToStorePricesJobResult = UpdateToStorePricesJobDto

export interface EnsureHqProductError {
  detailGuid: string
  storeCode?: string
  message: string
}

export interface EnsureHqProductsRequest {
  detailGuids: string[]
  targetStoreCodes: string[]
  idempotencyKey?: string
}

export interface EnsureHqProductsResult {
  total: number
  hqExisting: number
  hbwebCreated: number
  hqCreated: number
  hqSynced: number
  hqPurchasePricesUpdated: number
  skipped: number
  failed: number
  errors: EnsureHqProductError[]
}

export interface UpdateHqProductsRequest {
  detailGuids: string[]
  targetStoreCodes: string[]
  updateFields: UpdateToStorePricesFields
  idempotencyKey?: string
}

export interface UpdateHqProductsResult {
  total: number
  updated: number
  failed: number
  skipped?: number
  hqExisting?: number
  hbwebCreated?: number
  hqCreated?: number
  hqSynced?: number
  hqPurchasePricesUpdated?: number
  hqRetailPricesUpdated?: number
  hqAutoPricingUpdated?: number
  hqSpecialProductsUpdated?: number
  hqDiscountRatesUpdated?: number
  hqProductSetCodesCreated?: number
  hqProductSetCodesUpdated?: number
  hqStoreMultiCodesCreated?: number
  hqStoreMultiCodesUpdated?: number
  errors: EnsureHqProductError[]
}

export interface UpdateHqProductsJobDto extends LocalSupplierInvoiceJobBase {
  result?: UpdateHqProductsResult
}

export type UpdateHqProductsJobResult = UpdateHqProductsJobDto

export interface PasteDetailsJobDto extends LocalSupplierInvoiceJobBase {
  result?: BatchResultDto
}

export type PasteDetailsJobResult = PasteDetailsJobDto

export interface CheckProductsJobDto extends LocalSupplierInvoiceJobBase {
  result?: CheckProductsResponse
}

export type CheckProductsJobResult = CheckProductsJobDto

export interface LocalSupplierInvoiceHqSyncRequest {
  selectedStoreCodes?: string[]
  startDate?: string
  endDate?: string
}

export interface LocalSupplierInvoiceHqSyncResult {
  requestId: string
  status: string
  startedAt: string
  completedAt?: string
  durationMs: number
  invoiceAddedCount: number
  invoiceUpdatedCount: number
  detailAddedCount: number
  detailUpdatedCount: number
  totalProcessed: number
  errors: string[]
}

export type LocalSupplierInvoiceImportField =
  | 'itemNumber'
  | 'barcode'
  | 'productName'
  | 'quantity'
  | 'price'

export interface LocalSupplierInvoiceImportSourceColumn {
  key: string
  header?: string
  sampleValue?: string
}

export interface LocalSupplierInvoiceImportColumnMapping {
  itemNumberColumnKey?: string | null
  barcodeColumnKey?: string | null
  productNameColumnKey?: string | null
  quantityColumnKey?: string | null
  priceColumnKey?: string | null
}

export interface LocalSupplierInvoiceImportPreviewHeader {
  storeCode?: string
  storeName?: string
  supplierCode?: string
  supplierName?: string
  invoiceNo?: string
  orderDate?: string
  inboundDate?: string
  totalAmount?: number
  remarks?: string
}

export interface LocalSupplierInvoiceImportPreviewLine {
  rowNumber?: number
  rawValues: Record<string, string | null | undefined>
}

export interface LocalSupplierInvoiceImportPreviewResponse {
  sourceColumns: LocalSupplierInvoiceImportSourceColumn[]
  recommendedMapping?: LocalSupplierInvoiceImportColumnMapping
  header: LocalSupplierInvoiceImportPreviewHeader
  lines: LocalSupplierInvoiceImportPreviewLine[]
  warnings: string[]
  errors: string[]
}

export interface LocalSupplierInvoiceImportConfirmRequest {
  sourceColumns: LocalSupplierInvoiceImportSourceColumn[]
  header: LocalSupplierInvoiceImportPreviewHeader
  mapping: Required<{
    itemNumberColumnKey: string
    barcodeColumnKey: string
    productNameColumnKey: string
    quantityColumnKey: string
    priceColumnKey: string
  }>
  lines: LocalSupplierInvoiceImportPreviewLine[]
}

export interface LocalSupplierInvoiceImportConfirmResponse {
  invoiceGuid: string
  warnings?: string[]
}

export interface GetInvoiceDetailResponse {
  invoice: LocalSupplierInvoiceDetailDto
  details: LocalSupplierInvoiceItemDto[]
}

export enum ProductStatus {
  Unknown = 0,
  Exists = 1,
  NotExists = 2,
}

export enum BarcodeStatus {
  Unknown = 0,
  Normal = 1,
  Abnormal = 2,
}

export enum DetailAction {
  None = 0,
  CreateProduct = 1,
  UpdatePurchasePrice = 2,
  WaitForOperation = 3,
  UpdateItemNumber = 4,
  AddMultiCode = 5,
}

export interface ProductCheckResult {
  detailGuid: string
  productStatus: ProductStatus
  barcodeStatus: BarcodeStatus
  existingProductCount: number
  autoPricing?: boolean
  isSpecialProduct?: boolean
  discountRate?: number
  storeProductCode?: string
  lastPurchasePrice?: number
  pricingFloatRate?: number
  newAutoRetailPrice?: number
  productInfo?: {
    productCode?: string
    productName?: string
    purchasePrice?: number
    retailPrice?: number
    productImage?: string
    storeProductCode?: string
  }
  barcodeMatchCount?: number
  defaultAction?: DetailAction
}

export interface CheckProductsRequest {
  invoiceGuid: string
  detailGuids?: string[]
}

export interface CheckProductsResponse {
  results: ProductCheckResult[]
  summary: {
    total: number
    productExists: number
    productNotExists: number
    barcodeNormal: number
    barcodeAbnormal: number
  }
}

export interface PasteDetailsRequest {
  invoiceGuid: string
  mode: 'append' | 'replace'
  items: {
    itemNumber?: string
    barcode?: string
    additionalBarcodes?: string[]
    productName?: string
    quantity?: number
    purchasePrice?: number
    newAutoRetailPrice?: number
    retailPrice?: number
  }[]
}

export interface BarcodeAbnormalMatchedProductDto {
  productCode: string
  productName: string
  supplierCode: string
  supplierName?: string
  itemNumber?: string
  barcode: string
  productImage?: string
  isMultiCode: boolean
  isBundle: boolean
  productType?: number
}

export interface BarcodeAbnormalDetailDto {
  detailGuid: string
  itemNumber: string
  barcode: string
  productCode: string
  productName: string
  productStatus: number
  matchedProductCode?: string
  matchedProducts: BarcodeAbnormalMatchedProductDto[]
}

export interface ProductsByBarcodeResponse {
  barcode: string
  matchedProducts: BarcodeAbnormalMatchedProductDto[]
}

export interface BatchExecuteActionsRequest {
  invoiceGuid: string
  detailGuids: string[]
  expectedActions: BatchExecuteExpectedAction[]
  confirmedCreateProductCount: number
  confirmedAt: string
  newProductProductTypeSelections?: BatchExecuteNewProductProductTypeSelection[]
}

export interface BatchExecuteExpectedAction {
  detailGuid: string
  action: DetailAction
  activityType: DetailAction
}

export interface BatchExecuteNewProductProductTypeSelection {
  detailGuid: string
  productType: 1 | 2
}

export interface BatchExecuteActionsResult {
  createdProducts: number
  updatedPurchasePrices: number
  updatedItemNumbers: number
  addedMultiCodes: number
  skipped: number
  failed: number
  errors: string[]
}

export interface CheckInvoiceNoRequest {
  storeCode: string
  supplierCode: string
  invoiceNo: string
  excludeInvoiceGuid?: string
}

export interface CheckInvoiceNoResponse {
  exists: boolean
  existingInvoiceGuid?: string
}
