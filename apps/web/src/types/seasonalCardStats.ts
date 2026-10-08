// 节日贺卡「分店填报统计」后台接口的类型（后端 SeasonalCardStats* DTO，JSON camelCase，枚举为数字）。

/** 节日：1 圣诞节 / 2 情人节 / 3 母亲节 / 4 复活节 / 5 父亲节（后端 SeasonalCardType）。 */
export type SeasonalCardType = 1 | 2 | 3 | 4 | 5

/** 卡片价格类型：1 $1 / 2 $2 / 3 $3 / 4 其他（后端 SeasonalCardPriceOptionType）。 */
export type SeasonalCardPriceOption = 1 | 2 | 3 | 4

export interface SeasonalCardStatsQuery {
  seasonYear: number
  cardType: SeasonalCardType
  localSupplierCode?: string
  priceOption?: SeasonalCardPriceOption
  /** 后端是 List<string>：须序列化成 storeCodes=a&storeCodes=b（request.ts 对数组正是这样拼的）。 */
  storeCodes?: string[]
}

export interface SeasonalCardStatsStoreRef {
  storeCode: string
  storeName: string
}

export interface SeasonalCardStatsSupplierRef {
  /** 历史行（批量填报前）没有供应商，为 null，名称为「未指定供应商」。 */
  localSupplierCode: string | null
  supplierName: string
}

export interface SeasonalCardStatsPriceQuantity {
  priceOption: SeasonalCardPriceOption
  priceLabel: string
  quantity: number
  amount: number
}

export interface SeasonalCardStatsStoreRow {
  storeCode: string
  storeName: string
  isFilled: boolean
  /** 固定 4 项（$1/$2/$3/其他），已按供应商、价格筛选。 */
  prices: SeasonalCardStatsPriceQuantity[]
  totalQuantity: number
  totalAmount: number
  suppliers: SeasonalCardStatsSupplierRef[]
  /** UTC 时间字符串（可能不带 Z），前端按浏览器本地时区显示。 */
  lastSubmittedAt: string | null
  lastSubmittedByName: string | null
}

export interface SeasonalCardStatsSupplierTotal {
  localSupplierCode: string | null
  supplierName: string
  storeCount: number
  quantity: number
  amount: number
}

export interface SeasonalCardStatsSummary {
  seasonYear: number
  cardType: SeasonalCardType
  cardTypeName: string
  storeCount: number
  filledStoreCount: number
  unfilledStoreCount: number
  totalQuantity: number
  totalAmount: number
  stores: SeasonalCardStatsStoreRow[]
  priceTotals: SeasonalCardStatsPriceQuantity[]
  supplierTotals: SeasonalCardStatsSupplierTotal[]
  unfilledStores: SeasonalCardStatsStoreRef[]
  /** 不参与统计的分店（测试店、仓库），页面口径说明里注明。 */
  excludedStores: SeasonalCardStatsStoreRef[]
}

export interface SeasonalCardStatsDetailQuery {
  seasonYear: number
  cardType: SeasonalCardType
}

export interface SeasonalCardBatchLine {
  submissionGuid: string
  catalogGuid: string
  priceOption: SeasonalCardPriceOption
  priceLabel: string
  unitPrice: number
  remainingQuantity: number
}

export interface SeasonalCardBatch {
  /** 批量填报前的历史单条记录没有批次号，为 null。 */
  batchGuid: string | null
  storeCode: string
  storeName: string | null
  seasonYear: number
  cardType: SeasonalCardType
  cardTypeName: string
  localSupplierCode: string | null
  supplierName: string | null
  remark: string | null
  submittedByName: string
  submittedAt: string
  totalQuantity: number
  totalAmount: number
  /** 是否为该组合当前生效的数据；被新提交取代的为 false。 */
  isCurrent: boolean
  lines: SeasonalCardBatchLine[]
}

export interface SeasonalCardStatsStoreDetail {
  storeCode: string
  storeName: string
  seasonYear: number
  cardType: SeasonalCardType
  cardTypeName: string
  isFilled: boolean
  totalQuantity: number
  totalAmount: number
  /** 每个供应商当前生效的批次（供应商 × 价格矩阵）。 */
  currentBatches: SeasonalCardBatch[]
  /** 全部批次，按提交时间倒序。 */
  history: SeasonalCardBatch[]
  /** 去年同节日当前生效的剩余数量合计；去年没填为 null。 */
  previousYearTotalQuantity: number | null
}
