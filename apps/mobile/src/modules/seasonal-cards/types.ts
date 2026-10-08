export type SeasonalCardType = 1 | 2 | 3 | 4 | 5;
export type SeasonalCardPriceOption = 1 | 2 | 3 | 4;

export interface SeasonalCardCatalogItem {
  catalogGuid: string;
  cardType: SeasonalCardType | null;
  cardTypeName: string;
  priceOption: SeasonalCardPriceOption | null;
  priceOptionName: string;
  priceLabel: string;
  fixedUnitPrice: number | null;
  allowsCustomUnitPrice: boolean;
  isEnabled: boolean;
  sortOrder: number | null;
}

export interface SeasonalCardSubmissionRecord {
  submissionGuid: string;
  storeCode: string;
  catalogGuid: string;
  cardType: SeasonalCardType | null;
  cardTypeName: string;
  seasonYear: number | null;
  unitPrice: number | null;
  priceLabel: string;
  remainingQuantity: number | null;
  remark: string;
  submittedByName: string;
  submittedAt: string;
  /** 价格类型（1-4）；旧接口没有返回时为 null。 */
  priceOption: SeasonalCardPriceOption | null;
  /** 供应商；批量填报前的历史单条记录为空字符串。 */
  localSupplierCode: string;
  supplierName: string;
  /** 同一次整组提交共用的批次号；历史单条记录为空字符串。 */
  batchGuid: string;
}

export interface SeasonalCardSubmissionQuery {
  storeCode?: string;
  cardType?: SeasonalCardType | number | string | null;
  seasonYear?: number | string | null;
  localSupplierCode?: string | null;
  pageNumber?: number;
  pageSize?: number;
}

/** 批次中的一个价格行。 */
export interface SeasonalCardBatchLine {
  submissionGuid: string;
  catalogGuid: string;
  priceOption: SeasonalCardPriceOption | null;
  priceLabel: string;
  unitPrice: number;
  remainingQuantity: number;
}

/** 一次整组提交（分店 + 年份 + 节日 + 供应商）的快照。 */
export interface SeasonalCardBatch {
  /** 批次号；历史单条记录没有批次号时为空字符串。 */
  batchGuid: string;
  storeCode: string;
  storeName: string;
  seasonYear: number | null;
  cardType: SeasonalCardType | null;
  cardTypeName: string;
  localSupplierCode: string;
  supplierName: string;
  remark: string;
  submittedByName: string;
  /** UTC ISO 字符串（统一带 Z），显示时再转本地时区。 */
  submittedAt: string;
  totalQuantity: number;
  totalAmount: number;
  isCurrent: boolean;
  lines: SeasonalCardBatchLine[];
}

export interface SeasonalCardOverviewHoliday {
  cardType: SeasonalCardType;
  cardTypeName: string;
  /** 当前生效批次；没填过为 null。 */
  currentBatch: SeasonalCardBatch | null;
}

export interface SeasonalCardOverview {
  storeCode: string;
  seasonYear: number | null;
  localSupplierCode: string;
  supplierName: string;
  holidays: SeasonalCardOverviewHoliday[];
}

export interface SeasonalCardOverviewQuery {
  storeCode: string;
  seasonYear: number;
  localSupplierCode: string;
}

export interface SeasonalCardBatchItemPayload {
  catalogGuid: string;
  remainingQuantity: number;
  customUnitPrice?: number;
}

export interface SeasonalCardBatchPayload {
  storeCode: string;
  seasonYear: number;
  cardType: SeasonalCardType;
  localSupplierCode: string;
  /** 预填时看到的当前批次号；没填过为 null。 */
  expectedPreviousBatchGuid: string | null;
  remark?: string;
  items: SeasonalCardBatchItemPayload[];
}

export interface SeasonalCardSupplierOption {
  supplierCode: string;
  supplierName: string;
}

export interface SeasonalCardSubmissionPayload {
  storeCode: string;
  catalogGuid: string;
  seasonYear: number | string;
  remainingQuantity: number | string;
  customUnitPrice?: number | string | null;
  remark?: string | null;
}

export interface PagedResult<T> {
  items: T[];
  total: number;
  pageNumber: number;
  pageSize: number;
}
