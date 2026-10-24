// 与后端 BlazorApp.Shared/DTOs/PricingStrategyReactDtos.cs、Web types/pricingStrategy.ts 同一套字段。
export type PricingStrategyLevel = "Supplier" | "Store" | "Global";
export type PricingStrategyAlgorithm = "Linear" | "ArcUp" | "ArcDown" | "Exponential" | "Step" | "Fixed";
/** Fixed 单区间固定成率 / Curve 曲线 / Mixed 分段（含固定区间的多区间）/ Legacy 旧版区间规则 / Empty 无区间 */
export type PricingStrategyMode = "Fixed" | "Curve" | "Mixed" | "Legacy" | "Empty";

export interface PricingStrategyIntervalSummary {
  minPrice: number;
  maxPrice: number;
  type: "Fixed" | "Curve" | "Legacy";
  rate: number | null;
  startPrice: number;
  endPrice: number;
}

export interface PricingStrategyListItem {
  id: string;
  name: string;
  level: PricingStrategyLevel;
  priority: number;
  isEnabled: boolean;
  storeCodes: string[];
  supplierCodes: string[];
  pricingMode: PricingStrategyMode | null;
  fixedRate: number | null;
  startRate: number | null;
  endRate: number | null;
  intervals: PricingStrategyIntervalSummary[];
}

export interface PricingStrategyListPage {
  items: PricingStrategyListItem[];
  total: number;
}

export interface PricingStrategyRule {
  id?: string;
  minPrice: number;
  maxPrice: number;
  startRetailPrice?: number | null;
  endRetailPrice?: number | null;
  curveBend?: number | null;
  startRate: number;
  endRate: number;
  algorithm: PricingStrategyAlgorithm;
}

export interface PricingStrategyTarget {
  targetType: "Store" | "Supplier" | "Global";
  targetCode?: string;
}

export interface PricingStrategyDetail {
  id: string;
  name: string;
  level: PricingStrategyLevel;
  priority: number;
  isEnabled: boolean;
  details: PricingStrategyRule[];
  targets: PricingStrategyTarget[];
}

export interface PricingStrategySavePayload {
  name: string;
  level: PricingStrategyLevel;
  priority: number;
  isEnabled: boolean;
  details: PricingStrategyRule[];
  targets: PricingStrategyTarget[];
}

export interface PricingEvaluateResult {
  retailPrice: number;
  /** 实际成率 = 零售价 / 进货价（后端较旧时由前端兜底计算）。 */
  effectiveRate: number;
  strategyId: string | null;
  strategyName: string | null;
}

export interface PricingListFilters {
  keyword: string;
  storeCode: string | null;
  supplierCode: string | null;
}

export interface NamedOption {
  code: string;
  name: string;
}
