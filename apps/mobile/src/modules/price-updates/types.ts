export type PriceUpdateTaskStatus = "Pending" | "Completed" | "Cancelled";
export type PriceUpdateTaskKind = "PriceUpdate" | "LabelOnly";
export type PriceUpdateChangedField = "retailPrice" | "discountRate";
export type PriceUpdateCompletionMode =
  | "Printed"
  | "MarkedReplaced"
  | "KeptStorePrice"
  | "PriceAligned";
export type PriceUpdateHqSyncStatus =
  | "pending"
  | "processing"
  | "retrying"
  | "succeeded"
  | "blocked"
  | "superseded";
export type PriceUpdateLabelMode = "Printed" | "MarkedReplaced";

export interface StorePriceUpdateTask {
  id: number;
  storeCode: string;
  storeName: string | null;
  productCode: string;
  storeRetailPriceUuid: string | null;
  productName: string | null;
  itemNumber: string | null;
  barcode: string | null;
  productImage: string | null;
  status: PriceUpdateTaskStatus;
  kind: PriceUpdateTaskKind;
  changedFields: PriceUpdateChangedField[];
  /** 货架标签上的旧值。 */
  shelfRetailPrice: number | null;
  shelfDiscountRate: number | null;
  /** 分店现值。 */
  storeRetailPrice: number | null;
  storeDiscountRate: number | null;
  /** 仓库目标；折扣 null = 不比较分店折扣。 */
  targetRetailPrice: number | null;
  targetDiscountRate: number | null;
  initiatorName: string;
  initiatorSource: string;
  initiatorReference: string | null;
  initiatedAtUtc: string;
  changeCount: number;
  /** 任务首次生成的时间。超期从它起算：仓库再次改价只刷新 initiatedAtUtc，不会让任务重新计时。 */
  firstInitiatedAtUtc: string;
  /** 未完成任务已等待的整天数；已完成 / 已取消为 0。 */
  waitingDays: number;
  /** 未完成且已超过时限（默认 7 天）。 */
  isOverdue: boolean;
  /** 超出时限的天数（至少 1）；未超期为 0。 */
  overdueByDays: number;
  priceAppliedBy: string | null;
  priceAppliedAtUtc: string | null;
  completionMode: PriceUpdateCompletionMode | null;
  completedBy: string | null;
  completedAtUtc: string | null;
  labelPrintCount: number;
  hqSyncOperationId: string | null;
  hqSyncStatus: PriceUpdateHqSyncStatus | null;
}

export interface StorePriceUpdateTaskPage {
  items: StorePriceUpdateTask[];
  total: number;
  page: number;
  pageSize: number;
  pendingCount: number;
  pendingPriceUpdateCount: number;
  pendingLabelOnlyCount: number;
  completedCount: number;
  /** 当前分店未完成任务里已超期的数量。 */
  pendingOverdueCount: number;
  /** 超期时限（天），由后端配置决定，用于写「应在 N 天内完成」。 */
  overdueDays: number;
  hqSyncEnabled: boolean;
}

/** 「按分店汇总」的一行：该分店未完成数量与其中超期的数量。 */
export interface StorePriceUpdateStoreSummary {
  storeCode: string;
  storeName: string;
  pendingPriceUpdateCount: number;
  pendingLabelOnlyCount: number;
  pendingCount: number;
  overdueCount: number;
}

export interface StorePriceUpdateSummary {
  /** 当前账号 / 设备可访问的全部启用分店，没有任务的分店也在其中（各计数为 0）。 */
  stores: StorePriceUpdateStoreSummary[];
  overdueDays: number;
}

export interface PriceUpdateTaskQuery {
  storeCode: string;
  status: "Pending" | "Completed";
  kind?: PriceUpdateTaskKind;
  /** 只看未完成且已超期的任务；后端按首次发起时间从早到晚排序。 */
  overdueOnly?: boolean;
  keyword?: string;
  hqSyncFailedOnly?: boolean;
  page: number;
  pageSize: number;
}

export type PriceUpdateBatchResultCode =
  | "ok"
  | "not_found"
  | "not_pending"
  | "target_changed"
  | "not_applicable"
  | "failed";

export interface PriceUpdateBatchResultItem {
  taskId: number;
  success: boolean;
  /** 未识别的 code 原样保留，调用方按失败处理。 */
  code: PriceUpdateBatchResultCode | string;
  message: string | null;
  task: StorePriceUpdateTask | null;
}

export interface PriceUpdateBatchResult {
  items: PriceUpdateBatchResultItem[];
  successCount: number;
  failedCount: number;
  hqSyncEnabled: boolean;
  hqSyncSubmittedCount: number;
}

export interface PriceUpdateApplyItem {
  taskId: number;
  expectedTargetRetailPrice: number | null;
  expectedTargetDiscountRate: number | null;
}

export interface SyncTargetStore {
  storeCode: string;
  storeName: string;
  hasRecord: boolean;
  retailPrice: number | null;
  discountRate: number | null;
  isSpecialProduct: boolean;
}

export interface SyncTargetsResult {
  sourceStoreCode: string;
  productCode: string;
  sourceRetailPrice: number | null;
  sourceDiscountRate: number | null;
  sourcePurchasePrice: number | null;
  targets: SyncTargetStore[];
}

export interface SyncToOtherStoresRequest {
  productCode: string;
  sourceStoreCode: string;
  targetStoreCodes: string[];
  syncRetailPrice: boolean;
  syncDiscountRate: boolean;
  syncPurchasePrice: boolean;
}

export interface PriceNotificationPreview {
  affectedStores: number;
  skippedSpecialStores: number;
}

export type SuggestedDiscountSource = "WarehouseProducts" | "MobileWarehouse" | "BatchUpdate";
