export interface ContainerMain {
  id?: number;
  ID?: number;
  hguid?: string;
  HGUID?: string;
  货柜编号?: string;
  装柜日期?: string;
  预计到岸日期?: string;
  实际到货日期?: string;
  合计件数?: number;
  合计数量?: number;
  合计金额?: number;
  总体积?: number;
  成本浮率?: number;
  汇率?: number;
  运费?: number;
  备注?: string;
  状态?: number;
}

export interface ContainerProductInfo {
  商品编码?: string;
  货号?: string;
  localSupplierCode?: string;
  条形码?: string;
  商品名称?: string;
  英文名称?: string;
  商品图片?: string;
  零售价格?: number;
  商品规格?: string;
  单位?: string;
  单件装箱数?: number;
  单件体积?: number;
  商品类型?: string;
  套装数量?: number;
}

export interface ContainerDetail {
  id?: number;
  ID?: number;
  hguid?: string;
  HGUID?: string;
  主表GUID?: string;
  商品编码?: string;
  localSupplierCode?: string;
  商品名称?: string;
  英文名称?: string;
  商品图片?: string;
  装柜类型?: string;
  商品类型?: string;
  套装数量?: number;
  装柜件数?: number;
  中包数?: number;
  装柜数量?: number;
  国内价格?: number;
  调整浮率?: number;
  进口价格?: number;
  贴牌价格?: number;
  单件装箱数?: number;
  单件体积?: number;
  合计装柜金额?: number;
  合计装柜体积?: number;
  运输成本?: number;
  备注?: string;
  商品信息?: ContainerProductInfo;
  /** 本地主档尚未建档；建档后即为 false，推送/价格等操作以它为准 */
  是否新商品?: boolean;
  /** 本柜新品：未建档或由本货柜建档，建档后仍为 true，仅用于展示 */
  isContainerNewProduct?: boolean;
  IsActive?: boolean;
  warehouseIsActive?: boolean;
  lastImportPrice?: number;
  LastImportPrice?: number;
  lastOEMPrice?: number;
  LastOEMPrice?: number;
  warehouseImportPrice?: number;
  WarehouseImportPrice?: number;
  warehouseOEMPrice?: number;
  WarehouseOEMPrice?: number;
  readonlyOemPrice?: number;
  ReadonlyOemPrice?: number;
  matchType?: "productCode" | "supplierItem" | "unmatched";
  MatchType?: string;
  localProductCode?: string;
  LocalProductCode?: string;
  domesticProductCode?: string;
  DomesticProductCode?: string;
  hasProductCodeConflict?: boolean;
  HasProductCodeConflict?: boolean;
  conflictReason?: string;
  ConflictReason?: string;
  /** 服务器为每个可编辑字段签发的并发基线令牌。 */
  serverFieldTokens?: Record<string, string>;
  ServerFieldTokens?: Record<string, string>;
}

export interface DetectionItem {
  productCode?: string;
  ProductCode?: string;
  itemNumber?: string;
  ItemNumber?: string;
  barcode?: string;
  Barcode?: string;
  supplierCode?: string;
  SupplierCode?: string;
}

export interface DetectionResult extends DetectionItem {
  exists?: boolean;
  Exists?: boolean;
  matchType?: string;
  MatchType?: string;
  localProductCode?: string;
  LocalProductCode?: string;
  domesticProductCode?: string;
  DomesticProductCode?: string;
  hasProductCodeConflict?: boolean;
  HasProductCodeConflict?: boolean;
  conflictReason?: string;
  ConflictReason?: string;
}

export type ContainerDetailQueryTag =
  | "all"
  | "new"
  | "existing"
  | "noOemPrice"
  | "abnormalImport"
  | "active"
  | "inactive";

export type ContainerDetailQueryProductType = "normal" | "set" | "multi" | "setChild";
export type ContainerDetailQueryNewProductState = "new" | "existing";
export type ContainerDetailQueryMatchType = "productCode" | "supplierItem" | "unmatched";
export type ContainerDetailQueryWarehouseStatus = "active" | "inactive";
export type ContainerDetailQuerySortOrder = "ascend" | "descend";
export type ContainerExportFormat = "excel" | "pdf";

/** 搜索框可选字段：与 Web 货柜明细工具栏一致，后端只支持按单列文字筛选。 */
export type ContainerDetailSearchField = "itemNumber" | "productName" | "barcode" | "englishName";

/**
 * 移动端货柜明细提供的排序字段，均为后端 ApplyContainerDetailSort 白名单内的 key
 * （未识别的 key 后端会静默回退到货号排序，所以必须与白名单一致）。
 */
export type ContainerDetailSortField =
  | "itemNumber"
  | "productName"
  | "englishName"
  | "barcode"
  | "containerQuantity"
  | "containerPieces"
  | "packingQuantity"
  | "unitVolume"
  | "domesticPrice"
  | "importPrice"
  | "warehouseImportPrice"
  | "oemPrice"
  | "newProduct"
  | "warehouseStatus";

export interface ContainerDetailSort {
  field: ContainerDetailSortField;
  order: ContainerDetailQuerySortOrder;
}

/** 区间筛选的 8 个输入框（界面用字符串，空串表示未填）。 */
export type ContainerDetailRangeFilterKey =
  | "containerQuantityMin"
  | "containerQuantityMax"
  | "middlePackQuantityMin"
  | "middlePackQuantityMax"
  | "warehouseImportPriceMin"
  | "warehouseImportPriceMax"
  | "oemPriceMin"
  | "oemPriceMax";

/** 筛选面板的完整状态：区间 + 商品类型 + 仓库上下架 + 匹配方式（标签 chips 与搜索词另管）。 */
export interface ContainerDetailFilterState {
  ranges: Record<ContainerDetailRangeFilterKey, string>;
  productTypes: ContainerDetailQueryProductType[];
  warehouseStatus: ContainerDetailQueryWarehouseStatus[];
  matchTypes: ContainerDetailQueryMatchType[];
}

export type ContainerDetailPageSize = 50 | 100 | 200 | 500;

/** 货柜头部概览卡所需数据；取不到的字段为 undefined，界面显示 "--"。 */
export interface ContainerDetailOverview {
  /** 装柜金额（货柜主表合计金额，国内价格口径） */
  totalAmount?: number;
  totalVolume?: number;
  /** 装载率（%），按 68m3 标准柜折算，可超过 100 */
  loadRatePercent?: number;
  totalPieces?: number;
  totalQuantity?: number;
  freight?: number;
  exchangeRate?: number;
  loadingDate?: string;
  etaDate?: string;
  actualArrivalDate?: string;
  /** 传入 today 时才有：逾期/今天/N 天后/晚到/早到 提示 */
  arrivalInsight: { text: string; tone: "warning" | "muted" | "accent"; target: "estimated" | "actual" } | null;
  /** 本柜新品数（tagStats.new） */
  newCount?: number;
  /** 已有商品数（tagStats.existing） */
  existingCount?: number;
  /** 明细行总数（tagStats.all） */
  rowCount?: number;
  /** 统计缺失（未传 tagStats）时为 true，对应 newCount/existingCount/rowCount 为 undefined */
  statsMissing: boolean;
}

export interface MissingRetailPriceDetail {
  hguid: string;
  /** 提示用名称：货号 > 商品编码 > 明细 GUID */
  label: string;
  retailPrice?: number;
}

export type CreatedProductsHqPushWarning =
  /** 部分新建商品没能变成发送候选（找不到对应明细或缺编码/供应商+货号），count 为个数 */
  | { code: "UNSENT_CREATED"; count: number }
  /** 已有发送到 HQ 的任务正在提交，本次不自动发送 */
  | { code: "PUSH_BUSY" };

export interface CreatedProductsHqPushPlan {
  selection: ContainerDetailHqPushSelection;
  /** 本次创建成功、但没能变成发送候选的数量 */
  unsentCreatedCount: number;
  /** 是否应当提交推送任务（有候选且没有在途推送） */
  shouldPush: boolean;
  warnings: CreatedProductsHqPushWarning[];
}

export type CreateNewProductsHqPushOutcome =
  | { status: "skipped"; reason: "not-run" | "sync-disabled" | "nothing-created" | "no-candidates" | "push-busy" }
  | { status: "succeeded" | "failed"; job: PushProductsToHqJob }
  | { status: "error"; message: string };

export interface CreateNewProductsRunResult {
  /** 被前置校验拦下时为 blocked，此时没有调用任何接口 */
  status: "blocked" | "completed";
  blockedReason?: "NO_DETAILS" | "MISSING_RETAIL_PRICE";
  missingRetailPrice: MissingRetailPriceDetail[];
  job?: ContainerJob;
  plan?: CreatedProductsHqPushPlan;
  push: CreateNewProductsHqPushOutcome;
}

export interface ContainerQueryRequest {
  dateType?: string;
  startDate?: string;
  endDate?: string;
  loadingDateStart?: string;
  loadingDateEnd?: string;
  estimatedArrivalDateStart?: string;
  estimatedArrivalDateEnd?: string;
  actualArrivalDateStart?: string;
  actualArrivalDateEnd?: string;
  page?: number;
  pageSize?: number;
  itemNumberFilter?: string;
  containerNumberFilter?: string;
  statuses?: number[];
  totalPiecesMin?: number;
  totalPiecesMax?: number;
  totalAmountMin?: number;
  totalAmountMax?: number;
  totalVolumeMin?: number;
  totalVolumeMax?: number;
  sortBy?: string;
  sortDirection?: "asc" | "desc" | string;
}

export interface ContainerListResponse {
  containers: ContainerMain[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

export interface ContainerDetailQuery {
  containerGuid: string;
  pageNumber: number;
  pageSize: number;
  itemNumber?: string;
  barcode?: string;
  productName?: string;
  englishName?: string;
  remark?: string;
  productTypes?: ContainerDetailQueryProductType[];
  newProductStates?: ContainerDetailQueryNewProductState[];
  matchTypes?: ContainerDetailQueryMatchType[];
  warehouseStatus?: ContainerDetailQueryWarehouseStatus[];
  containerPiecesMin?: number;
  containerPiecesMax?: number;
  middlePackQuantityMin?: number;
  middlePackQuantityMax?: number;
  containerQuantityMin?: number;
  containerQuantityMax?: number;
  packingQuantityMin?: number;
  packingQuantityMax?: number;
  unitVolumeMin?: number;
  unitVolumeMax?: number;
  domesticPriceMin?: number;
  domesticPriceMax?: number;
  floatRateMin?: number;
  floatRateMax?: number;
  transportCostMin?: number;
  transportCostMax?: number;
  unitTransportCostMin?: number;
  unitTransportCostMax?: number;
  warehouseImportPriceMin?: number;
  warehouseImportPriceMax?: number;
  lastOEMPriceMin?: number;
  lastOEMPriceMax?: number;
  importPriceMin?: number;
  importPriceMax?: number;
  oemPriceMin?: number;
  oemPriceMax?: number;
  selectedTags?: ContainerDetailQueryTag[];
  sortBy?: string;
  sortOrder?: ContainerDetailQuerySortOrder;
  includeTotal?: boolean;
  includeStats?: boolean;
}

export interface ContainerDetailTagStats {
  all: number;
  new: number;
  existing: number;
  noOemPrice: number;
  abnormalImport: number;
  active: number;
  inactive: number;
}

export interface ContainerDetailQueryResult {
  items: ContainerDetail[];
  itemsTotal: number;
  pageNumber: number;
  pageSize: number;
  hasMore: boolean;
  totalComputed?: boolean;
  statsComputed?: boolean;
  tagStats: ContainerDetailTagStats;
}

export interface ContainerDetailBatchScope {
  selectedHguids?: string[];
  query?: ContainerDetailQuery;
}

export interface ContainerDetailBatchActionResult {
  totalUpdated: number;
  totalRequested?: number;
  totalDeleted?: number;
}

export interface ContainerDetailBatchPreview {
  previewToken: string;
  affectedCount: number;
  fieldSummary: string[];
  expiresAt?: string;
}

export interface ContainerDetailPresenceUser {
  userGuid: string;
  userName: string;
  lastActiveAt?: string;
}

export interface ContainerDetailPresence {
  viewers: ContainerDetailPresenceUser[];
  editors: ContainerDetailPresenceUser[];
}

export interface ContainerDetailSaveValidationError {
  hguid: string;
  field: string;
  code: string;
  message: string;
}

export interface ContainerDetailConcurrentConflict {
  hguid: string;
  field: string;
  code: "CONCURRENT_FIELD_UPDATE";
  message: string;
  serverValue: unknown;
  submittedValue: unknown;
  currentServerFieldToken: string;
}

export interface ContainerDetailBatchUpdateResult {
  totalUpdated: number;
  totalRequested: number;
  validationErrors: ContainerDetailSaveValidationError[];
  conflicts: ContainerDetailConcurrentConflict[];
}

export interface CreateContainerRequest {
  货柜编号: string;
  装柜日期?: string;
  预计到岸日期?: string;
  汇率?: number;
  运费?: number;
  备注?: string;
}

export interface UpdateContainerRequest {
  货柜编号?: string;
  装柜日期?: string;
  预计到岸日期?: string;
  实际到货日期?: string;
  /** 显式清空预计到岸日期；与 预计到岸日期 互斥 */
  ClearEstimatedArrivalDate?: boolean;
  /** 显式清空实际到货日期；与 实际到货日期 互斥 */
  ClearActualArrivalDate?: boolean;
  汇率?: number;
  运费?: number;
  备注?: string;
  状态?: number;
}

export interface UpdateContainerDetailRequest {
  hguid: string;
  调整浮率?: number;
  国内价格?: number;
  进口价格?: number;
  运输成本?: number;
  商品名称?: string;
  英文名称?: string;
  ClearEnglishName?: boolean;
  贴牌价格?: number;
  单件装箱数?: number;
  中包数?: number;
  单件体积?: number;
  装柜数量?: number;
  合计装柜体积?: number;
  合计装柜金额?: number;
  IsActive?: boolean;
  SkipRelatedProductSync?: boolean;
  expectedServerFieldTokens?: Record<string, string>;
  overrideAcknowledgements?: Record<string, string>;
}

export interface SyncResult {
  isSuccess?: boolean;
  IsSuccess?: boolean;
  message?: string;
  Message?: string;
  addedCount?: number;
  AddedCount?: number;
  updatedCount?: number;
  UpdatedCount?: number;
  deletedCount?: number;
  DeletedCount?: number;
  errorCount?: number;
  ErrorCount?: number;
}

export type ContainerJobStatus = "Queued" | "Running" | "Succeeded" | "Failed";

export interface ContainerJobResultItem {
  productCode?: string;
  itemNumber?: string;
  detailHguid?: string;
  reasonCode?: string;
  message?: string;
}

export interface ContainerJobResult {
  createdCount: number;
  updatedCount: number;
  skippedCount: number;
  failedCount: number;
  containerCompleted: boolean;
  created: ContainerJobResultItem[];
  updated: ContainerJobResultItem[];
  skipped: ContainerJobResultItem[];
  errors: ContainerJobResultItem[];
}

export interface ContainerJob {
  jobId: string;
  status: ContainerJobStatus;
  operationId?: string;
  message?: string;
  result: ContainerJobResult;
}

export type PushProductsToHqUpdateField =
  | "itemNumber"
  | "barcode"
  | "productName"
  | "englishName"
  | "productType"
  | "image"
  | "purchasePrice"
  | "retailPrice"
  | "middlePackQuantity"
  | "supplierCode"
  | "storePurchasePrice"
  | "storeRetailPrice"
  | "inventoryDomesticPrice"
  | "inventoryImportPrice"
  | "inventoryOemPrice"
  | "productSetCodes"
  | "storeMultiCodes";

export interface PushProductsToHqItem {
  productCode?: string;
  localSupplierCode?: string;
  itemNumber?: string;
  productName?: string;
  englishName?: string;
  barcode?: string;
  imageUrl?: string;
  domesticPrice?: number;
  importPrice?: number;
  oemPrice?: number;
  isNewProduct: boolean;
  warehouseIsActive?: boolean;
}

export interface ContainerDetailHqPushSelection {
  productCodes: string[];
  items: PushProductsToHqItem[];
}

export interface PushProductsToHqJobRequest {
  productCodes: string[];
  items?: PushProductsToHqItem[];
  updateFields?: PushProductsToHqUpdateField[];
  operationId?: string;
}

export interface PushProductsToHqResult {
  successCount: number;
  failedCount: number;
  totalCount: number;
  affectedRowCount?: number;
  errors: string[];
  message?: string;
}

export interface PushProductsToHqJob {
  jobId: string;
  status: ContainerJobStatus;
  operationId?: string;
  result?: PushProductsToHqResult;
  message?: string;
  errors?: string[];
}

export interface AlignDomesticProductCodeRequest {
  detailHguid: string;
  expectedDomesticProductCode: string;
  targetProductCode: string;
  supplierCode?: string;
  /** 预览为 Merge 且用户确认后才传 true：合并到已有国内商品 */
  mergeIntoExistingDomesticProduct?: boolean;
}

/** Rename：直接改码；Merge：目标编码已存在，合并到已有国内商品 */
export type AlignDomesticProductCodeMode = "Rename" | "Merge";

export interface AlignDomesticProductFieldDiff {
  field: string;
  label: string;
  existingValue: string | null;
  oldValue: string | null;
  mergedValue: string | null;
  filledFromOld: boolean;
}

export interface AlignDomesticProductCodePreview {
  mode: AlignDomesticProductCodeMode;
  oldProductCode: string;
  newProductCode: string;
  affectedContainerDetails: number;
  affectedContainers: number;
  fields: AlignDomesticProductFieldDiff[];
}

export interface AlignDomesticProductCodeResult {
  mode: AlignDomesticProductCodeMode;
  filledFields: string[];
  oldProductCode: string;
  OldProductCode?: string;
  newProductCode: string;
  NewProductCode?: string;
  updatedDomesticProducts: number;
  UpdatedDomesticProducts?: number;
  updatedContainerDetails: number;
  UpdatedContainerDetails?: number;
  updatedDomesticSetProducts: number;
  UpdatedDomesticSetProducts?: number;
  updatedProductGrades: number;
  UpdatedProductGrades?: number;
  updatedDomesticProductCreationLogs: number;
  UpdatedDomesticProductCreationLogs?: number;
}

export interface ContainerExportRequest {
  format: ContainerExportFormat;
  query?: ContainerDetailQuery;
  selectedHguids?: string[];
  columns?: string[];
  fileNameHint?: string;
}

export interface ContainerExportResult {
  fileUri: string;
  fileName: string;
  contentType: string;
}
