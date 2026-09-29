export interface StoreProductPriceListDto {
  productCode?: string
  productName?: string
  productImage?: string
  itemNumber?: string
  barcode?: string
  localSupplierCode?: string
  localSupplierName?: string
  productType?: number
  middlePackageQuantity?: number
  isActive: boolean
  updatedAt?: string
  updatedBy?: string
  storeCode?: string
  storePurchasePrice?: number
  storeRetailPrice?: number
  isStoreAutoPricing: boolean
  isStoreSpecialProduct: boolean
  discountRate?: number
}

export interface StoreProductPriceQueryDto {
  storeCode?: string
  /** 多选分店；与 storeCode 合并去重，多个分店时每个商品在每个分店各占一行 */
  storeCodes?: string[]
  search?: string
  localSupplierCode?: string
  pageNumber: number
  pageSize: number
  sortBy?: string
  sortOrder?: string
  productName?: string
  productCode?: string
  itemNumber?: string
  barcode?: string
  productType?: number
  isActive?: boolean
  isSpecialProduct?: boolean
  purchasePriceGt?: number
  purchasePriceLt?: number
  retailPriceGt?: number
  retailPriceLt?: number
}

export interface BatchUpdateStoreRetailPriceDto {
  productCodes: string[]
  storeCode?: string
  purchasePrice?: number
  storeRetailPriceValue?: number
  isAutoPricing?: boolean
  isSpecialProduct?: boolean
  discountRate?: number
}

export interface SyncToOtherStoresDto {
  productCodes: string[]
  sourceStoreCode: string
  targetStoreCodes: string[]
  syncPurchasePrice: boolean
  syncRetailPrice: boolean
  syncIsAutoPricing: boolean
  syncIsSpecialProduct: boolean
  syncDiscountRate: boolean
  syncMode: 'Overwrite' | 'OnlyUpdateNull'
}

export interface CopyStoreDataDto {
  sourceStoreCode: string
  targetStoreCodes: string[]
  mode: 'Overwrite' | 'OnlyUpdateNull'
  syncPurchasePrice: boolean
  syncRetailPrice: boolean
  syncIsAutoPricing: boolean
  syncIsSpecialProduct: boolean
  syncDiscountRate: boolean
  syncMultiCode: boolean
  syncMultiCodeRetailPrice: boolean
}

export interface CopyStoreDataResultDto {
  storeRetailPriceCopied: number
  storeMultiCodeProductCopied: number
}

export interface CopyProgressDto {
  eventType: 'store_started' | 'store_completed' | 'batch_completed' | 'completed' | 'error'
  storeCode?: string
  storeIndex: number
  totalStores: number
  retailPriceCopied: number
  multiCodeCopied: number
  message: string
  batchCount?: number
  timestamp: string
}

export interface SyncFromHqRequest {
  selectedStoreCodes?: string[]
  startDate?: string
  endDate?: string
}

export interface SyncFromHqResult {
  addedCount: number
  updatedCount: number
  totalProcessed: number
  durationMs: number
  errors: string[]
}

export type StorePriceTransferDirection = 'HqToLocal' | 'LocalToHq'

export type StorePriceTransferJobStatus = 'Running' | 'Succeeded' | 'Failed' | string

export interface StorePriceTransferRequest {
  direction: StorePriceTransferDirection
  sourceStoreCode: string
  targetStoreCode: string
  syncRetailPrices: boolean
  syncMultiCodePrices: boolean
  syncPurchasePrice: boolean
  syncRetailPrice: boolean
  syncDiscountRate: boolean
  syncIsAutoPricing: boolean
  syncIsSpecialProduct: boolean
}

export interface StorePriceTransferResult {
  totalCount: number
  retailPriceTotal: number
  multiCodeTotal: number
  totalProcessed: number
  insertedCount: number
  updatedCount: number
  skippedCount: number
  failedCount: number
  retailPriceInserted: number
  retailPriceUpdated: number
  retailPriceSkipped: number
  multiCodeInserted: number
  multiCodeUpdated: number
  multiCodeSkipped: number
  errors: string[]
}

export interface StorePriceTransferJobDto {
  jobId: string
  operationId?: string
  status: StorePriceTransferJobStatus
  isDuplicateRequest?: boolean
  request?: StorePriceTransferRequest
  result?: StorePriceTransferResult
  message?: string
  errors?: string[]
  createdAt?: string
  startedAt?: string
  completedAt?: string
  expiresAt?: string
}
