export interface ProductLookupItem {
  productCode: string;
  productName: string;
  itemNumber?: string | null;
  barcode?: string | null;
  productImage?: string | null;
  matchSource?: string | null;
  matchValue?: string | null;
  productTypeLabel?: string | null;
  grade?: string | null;
}

export interface PricingEvaluationResult {
  rate?: number | null;
  strategySourceLabel?: string | null;
  strategyRuleLabel?: string | null;
}

export type ProductHqSyncStatus =
  | "pending"
  | "processing"
  | "retrying"
  | "succeeded"
  | "blocked"
  | "superseded";

export interface ProductHqSyncOperation {
  operationId: string;
  status: ProductHqSyncStatus;
  productCode: string;
  storeCode: string | null;
  attemptCount: number;
  nextAttemptAt: string | null;
  retryable: boolean;
  errorCode: string | null;
  message: string;
}

export interface ProductHqSyncCarrier {
  hqSync?: ProductHqSyncOperation | null;
}

export interface StorePriceEditable extends PricingEvaluationResult, ProductHqSyncCarrier {
  uuid: string;
  storeCode?: string | null;
  storeName?: string | null;
  productCode?: string | null;
  storeProductCode?: string | null;
  supplierCode?: string | null;
  purchasePrice?: number | null;
  retailPrice?: number | null;
  discountRate?: number | null;
  isAutoPricing: boolean;
  isSpecialProduct: boolean;
  isActive: boolean;
}

export interface MultiCodeEditableItem extends PricingEvaluationResult, ProductHqSyncCarrier {
  uuid: string;
  setCodeId: string;
  storeCode?: string | null;
  productCode?: string | null;
  multiCodeProductCode?: string | null;
  storeMultiCodeProductCode?: string | null;
  barcode?: string | null;
  purchasePrice?: number | null;
  retailPrice?: number | null;
  discountRate?: number | null;
  isAutoPricing: boolean;
  isSpecialProduct: boolean;
  isActive: boolean;
}

export interface ProductSetCodeItem extends ProductHqSyncCarrier {
  setCodeId: string;
  productCode: string;
  setProductCode: string;
  setItemNumber: string;
  setBarcode?: string | null;
  setPurchasePrice?: number | null;
  setRetailPrice?: number | null;
  setQuantity: number;
  setType: number;
  setTypeDescription?: string | null;
  isActive: boolean;
}

export interface StoreClearancePriceItem extends ProductHqSyncCarrier {
  uuid: string;
  storeCode?: string | null;
  storeName?: string | null;
  productCode?: string | null;
  clearanceBarcode?: string | null;
  clearancePrice?: number | null;
}

export interface ProductDetail {
  productCode: string;
  productName: string;
  itemNumber?: string | null;
  barcode?: string | null;
  productImage?: string | null;
  productType?: number | null;
  productTypeLabel?: string | null;
  grade?: string | null;
  localSupplierCode?: string | null;
  localSupplierName?: string | null;
  storePrice?: StorePriceEditable | null;
  clearancePrice?: StoreClearancePriceItem | null;
  setCodes: ProductSetCodeItem[];
  multiCodes: MultiCodeEditableItem[];
  setCodeCount: number;
  multiCodeCount: number;
  codesIncluded: boolean;
}

export interface ProductCodePage<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
  hasMore: boolean;
}

export interface StoreProductLookupRequest {
  keyword: string;
  storeCode?: string | null;
}

/** 扫码的一次在线查询结果。价格和可打印目标必须来自同一次服务器响应。 */
export interface ScanLabelPrintTarget {
  kind: "product" | "set" | "multi" | "clearance";
  barcode: string;
  retailPrice: number | null;
  discountRate: number | null;
  codeId: string | null;
  productCode: string;
  storeCode: string;
}

export interface ScanLabelResult {
  candidates: ProductLookupItem[];
  detail: ProductDetail | null;
  printTarget: ScanLabelPrintTarget | null;
}

export interface LocalSupplierOption {
  supplierCode: string;
  supplierName: string;
}

export interface CreateProductWithPricesRequest {
  localSupplierCode: string;
  itemNumber: string;
  barcode: string;
  productName: string;
  purchasePrice: number;
  retailPrice: number;
  isSpecialProduct: boolean;
  isAutoPricing: boolean;
}

export interface CreateProductWithPricesResult extends ProductHqSyncCarrier {
  productCode: string;
  storeProductCodes: Record<string, string>;
}

export interface UpdateStorePriceRequest {
  purchasePrice?: number | null;
  retailPrice?: number | null;
  discountRate?: number | null;
  isAutoPricing?: boolean;
  isSpecialProduct?: boolean;
  isActive?: boolean;
}

export type WarehousePriceSyncStatus =
  | "not_applicable"
  | "missing_source"
  | "synced"
  | "confirmation_required";

export interface WarehousePriceSyncRequest {
  confirmRetailPrice: boolean;
  expectedWarehousePurchasePrice?: number | null;
  expectedWarehouseRetailPrice?: number | null;
  expectedStorePurchasePrice?: number | null;
  expectedStoreRetailPrice?: number | null;
  expectedDiscountRate?: number | null;
}

export interface WarehousePriceSyncResult extends ProductHqSyncCarrier {
  status: WarehousePriceSyncStatus;
  purchaseUpdated: boolean;
  retailUpdated: boolean;
  retailConfirmationRequired: boolean;
  storePrice: StorePriceEditable | null;
  warehousePurchasePrice: number | null;
  warehouseRetailPrice: number | null;
  previousStorePurchasePrice: number | null;
  previousStoreRetailPrice: number | null;
  discountRate: number | null;
  previousDiscountedRetailPrice: number | null;
  newDiscountedRetailPrice: number | null;
}

export interface UpdateMultiCodeRequest {
  // 旧多码没有 setCodeId，只能通过 UUID 端点保存条码。
  barcode?: string;
  purchasePrice?: number | null;
  retailPrice?: number | null;
  isAutoPricing?: boolean;
  isSpecialProduct?: boolean;
  isActive?: boolean;
}

export interface CreateSetCodeRequest {
  productCode: string;
  storeCode: string;
  productType: number;
  barcode: string;
  retailPrice?: number | null;
  isActive?: boolean;
}

export interface UpdateSetCodeRequest {
  storeCode: string;
  barcode: string;
  retailPrice?: number | null;
  isActive?: boolean;
}

export interface UpsertClearancePriceRequest {
  storeCode: string;
  clearancePrice?: number | null;
}

export interface EvaluateAutoPricingRequest {
  productCode: string;
  storeCode?: string | null;
  forceAutoPricing?: boolean;
}

export interface EvaluateAutoPricingResult {
  productCode: string;
  storeCode?: string | null;
  storePriceUuid?: string | null;
  currentRetailPrice?: number | null;
  recalculatedRetailPrice?: number | null;
  currentRetailPriceFormatted: string;
  recalculatedRetailPriceFormatted: string;
  discountRate?: number | null;
  isAutoPricing: boolean;
  hasValidPurchasePrice: boolean;
  shouldUpdate: boolean;
}

export interface UpdateProductTypeRequest {
  productType: number;
  storeCode?: string | null;
}

export interface UpdateProductTypeResult extends ProductHqSyncCarrier {
  productCode: string;
  productType: number;
  productTypeLabel?: string | null;
}

export interface UpdateProductNameRequest {
  productName: string;
  storeCode?: string | null;
}

export interface UpdateProductNameResult extends ProductHqSyncCarrier {
  productCode: string;
  productName: string;
  /** 名称与原来相同时为 false：服务端没有写库，也没有触发 HQ 同步。 */
  changed: boolean;
}

/** 商品名称的一条修改记录：谁在什么时候把名称从什么改成什么。 */
export interface ProductNameHistoryItem {
  occurredAtUtc: string;
  actorName: string;
  beforeName: string | null;
  afterName: string | null;
  source: string;
}

export interface DeleteSetCodeResult extends ProductHqSyncCarrier {
  deleted: boolean;
}
