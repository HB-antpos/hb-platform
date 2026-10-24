export type PricingStrategyLevel = 'Supplier' | 'Store' | 'Global'
export type PricingStrategyAlgorithm = 'Linear' | 'ArcUp' | 'ArcDown' | 'Exponential' | 'Step' | 'Fixed'

export interface PricingStrategyListDto {
  id: string
  name: string
  level: PricingStrategyLevel
  priority: number
  isEnabled: boolean
  storeCodes: string[]
  supplierCodes: string[]
  detailsCount: number
  targetsCount: number
  // 以下为列表摘要字段（后端较旧时可能缺失，界面需容忍 undefined）
  pricingMode?: PricingStrategyMode
  fixedRate?: number | null
  startRate?: number | null
  endRate?: number | null
  nodes?: PricingStrategyNodeDto[]
  intervals?: PricingStrategyIntervalDto[]
}

/** Fixed 单区间固定成率 / Curve 曲线 / Mixed 分段（含固定区间的多区间）/ Legacy 旧版区间规则 / Empty 无区间 */
export type PricingStrategyMode = 'Fixed' | 'Curve' | 'Mixed' | 'Legacy' | 'Empty'

export interface PricingStrategyNodeDto {
  cost: number
  price: number
}

/** 列表摘要里的单个区间：进货价上下限、类型与端点售价。 */
export interface PricingStrategyIntervalDto {
  minPrice: number
  maxPrice: number
  type: 'Fixed' | 'Curve' | 'Legacy'
  rate?: number | null
  startPrice: number
  endPrice: number
}

export interface PricingStrategyRuleDto {
  id?: string
  minPrice: number
  maxPrice: number
  startRetailPrice?: number | null
  endRetailPrice?: number | null
  curveBend?: number | null
  startRate: number
  endRate: number
  algorithm: PricingStrategyAlgorithm
}

export interface PricingStrategyTargetDto {
  id?: string
  targetType: 'Store' | 'Supplier' | 'Global'
  targetCode?: string
}

export interface PricingStrategyDetailDto {
  id: string
  name: string
  level: PricingStrategyLevel
  priority: number
  isEnabled: boolean
  details: PricingStrategyRuleDto[]
  targets: PricingStrategyTargetDto[]
}

export interface CreatePricingStrategyDto {
  name: string
  level?: PricingStrategyLevel
  priority?: number
  isEnabled?: boolean
  details: PricingStrategyRuleDto[]
  targets: PricingStrategyTargetDto[]
}

export type UpdatePricingStrategyDto = CreatePricingStrategyDto

export interface PricingEvaluateRequest {
  storeCode?: string
  supplierCode?: string
  purchasePrice: number
}

export interface PricingEvaluateResponse {
  retailPrice: number
  /** 理论成率（未做尾数调整） */
  rate?: number
  /** 实际成率 = 零售价 / 进货价 */
  effectiveRate?: number
  strategyId?: string | null
  strategyName?: string | null
}
