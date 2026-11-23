export interface SeasonalRange {
  startDate: string;
  endDate: string;
}

/** 进货与销售各自一个区间；两者可以不同，理论存货 = 进货区间累计进货 − 销售区间累计销量。 */
export interface SeasonalRanges {
  inbound: SeasonalRange;
  sales: SeasonalRange;
}

export interface SeasonalCandidate {
  productCode: string;
  productName: string;
  itemNumber: string | null;
  barcode: string | null;
  productImage: string | null;
  theoreticalStock: number;
  /**
   * 当前分店的累计进货 / 累计销量 / 区间内最早进货日。
   * 旧版后端不返回这三项，此时为 null，界面只显示理论存货、不算售罄率。
   */
  inboundQuantity: number | null;
  salesQuantity: number | null;
  firstInboundDate: string | null;
}

export interface SeasonalLookupResult {
  matchMode: "barcode" | "itemNumber";
  truncated: boolean;
  ranges: SeasonalRanges;
  items: SeasonalCandidate[];
}

export interface SeasonalInboundRecord {
  id: string;
  date: string;
  documentNo: string;
  quantity: number;
}

export interface SeasonalDailySales {
  date: string;
  quantity: number;
  amount: number;
}

export interface SeasonalBranchStock {
  storeCode: string;
  storeName: string;
  inboundQuantity: number;
  salesQuantity: number;
  theoreticalStock: number;
}

export interface SeasonalProductInsight {
  generatedAt: string;
  store: { storeCode: string; storeName: string };
  product: {
    productCode: string;
    productName: string;
    itemNumber: string | null;
    barcode: string | null;
    productImage: string | null;
    sourceType: "local" | "warehouse";
  };
  ranges: SeasonalRanges;
  inbound: {
    quantity: number;
    documentCount: number;
    records: SeasonalInboundRecord[];
  };
  sales: {
    quantity: number;
    amount: number;
    daily: SeasonalDailySales[];
  };
  theoreticalStock: number;
  branches: SeasonalBranchStock[];
}

export type SeasonalTrendMode = "day" | "week";

export type SeasonalRangeError = "format" | "order" | "tooLong";

export interface DailyTrendPoint {
  /** 距销售区间起点的天数，用于在真实日期轴上定位；无记录的日期不出现。 */
  index: number;
  date: string;
  quantity: number;
}

/** 候选卡片上的状态标签。 */
export type SeasonalCandidateFlag = "negative" | "hot" | "slow";

export type SeasonalCandidateSort = "sellThrough" | "stock" | "itemNumber";

/** 「按周进销」的一行：每周的进货、销量，以及（两个区间一致时）周末理论存货。 */
export interface WeeklyLedgerRow {
  startDate: string;
  endDate: string;
  partial: boolean;
  inboundQuantity: number;
  salesQuantity: number;
  /** 从区间起点累计到本周末的「进货 − 销量」；进货与销售区间不一致时为 null。 */
  endStock: number | null;
  documentNos: string[];
}

/** 一周展开后的当天明细：进货单与日销量按日期混排。 */
export type LedgerDayEntry =
  | { kind: "sales"; date: string; quantity: number }
  | { kind: "inbound"; date: string; documentNo: string; quantity: number };

export interface WeeklyTrendPoint {
  /** 该周在销售区间内的第一天。 */
  startDate: string;
  endDate: string;
  quantity: number;
  inboundQuantity: number;
  /** 首尾两周可能被区间截断，天数不足 7 天。 */
  partial: boolean;
}
