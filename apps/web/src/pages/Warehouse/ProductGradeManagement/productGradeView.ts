// 商品等级管理页的纯计算：等级页签、计数请求计划、等级与仓库状态不一致判定、筛选摘要。
// 只用列表接口已有字段在前端推导，不需要新接口。

export const GRADE_KEYS = ['A', 'B', 'C', 'D'] as const
export type GradeKey = (typeof GRADE_KEYS)[number]
export type GradeTabKey = 'all' | GradeKey

export function isGradeKey(value: unknown): value is GradeKey {
  return typeof value === 'string' && (GRADE_KEYS as readonly string[]).includes(value)
}

/** 页签 key → 列表接口的 grade 参数；「全部」不带等级。 */
export function gradeTabToFilter(tab: GradeTabKey): GradeKey | undefined {
  return tab === 'all' ? undefined : tab
}

export function gradeFilterToTab(grade?: string): GradeTabKey {
  return isGradeKey(grade) ? grade : 'all'
}

export type GradeWarehouseMismatch = 'coreDelisted' | 'noStockListed'

/**
 * 等级与仓库上下架状态不一致：
 * - A 核心 / B 观察 却已下架（门店订不到本该常备的商品）；
 * - D 清库存(无货) 却仍上架（门店会订到仓库没有的货）。
 * C 清库存(有货) 上下架都合理；仓库状态未知（无仓库商品行）不判定。
 */
export function getGradeWarehouseMismatch(
  grade: string | undefined,
  warehouseIsActive: boolean | null | undefined,
): GradeWarehouseMismatch | null {
  if (warehouseIsActive === false && (grade === 'A' || grade === 'B')) {
    return 'coreDelisted'
  }
  if (warehouseIsActive === true && grade === 'D') {
    return 'noStockListed'
  }
  return null
}

/**
 * 是否有除等级以外的筛选条件（关键词、供应商、分类、仓库状态、列头条件）。
 * 等级由页签承载，不算在内。注意 warehouseIsActive=false（只看下架）是有效条件，不能按假值忽略。
 */
export function hasNonGradeFilters(filters: object, search?: string): boolean {
  if (search?.trim()) {
    return true
  }
  return Object.entries(filters).some(
    ([key, value]) => key !== 'grade' && value !== undefined && value !== null && value !== '',
  )
}

export type GradeCountKey = GradeTabKey | 'graded'

export interface GradeCountRequest {
  key: GradeCountKey
  grade?: GradeKey
  /** 是否带当前筛选条件；「已分级总数」（页头副标题）不带任何筛选。 */
  withFilters: boolean
}

/**
 * 页签计数请求计划：每个页签一条 pageSize=1 的计数请求，带上除等级外的同样筛选。
 * 有筛选时另发一条不带筛选的请求，作为页头「已分级 N 个商品」；没有筛选时「全部」页签的数即总数。
 */
export function buildGradeCountPlan(hasFilters: boolean): GradeCountRequest[] {
  const plan: GradeCountRequest[] = [
    { key: 'all', withFilters: true },
    ...GRADE_KEYS.map((grade) => ({ key: grade, grade, withFilters: true })),
  ]
  if (hasFilters) {
    plan.push({ key: 'graded', withFilters: false })
  }
  return plan
}

export type GradeCounts = Partial<Record<GradeCountKey, number>>

/**
 * 汇总计数结果：失败的请求不给数（页签不显示计数），不用 0 冒充。
 */
export function collectGradeCounts(
  plan: GradeCountRequest[],
  results: Array<PromiseSettledResult<{ total: number }>>,
): GradeCounts {
  const counts: GradeCounts = {}
  plan.forEach((request, index) => {
    const result = results[index]
    if (result?.status === 'fulfilled' && Number.isFinite(result.value.total)) {
      counts[request.key] = result.value.total
    }
  })
  if (!plan.some((request) => request.key === 'graded') && counts.all !== undefined) {
    counts.graded = counts.all
  }
  return counts
}

function formatAmount(value: number) {
  return value.toFixed(2)
}

/** 价格区间条件摘要，用于已生效筛选条：`1.00 – 5.00`、`≥ 1.00`、`≤ 5.00`。 */
export function formatPriceRangeSummary(min?: number, max?: number): string | undefined {
  if (min !== undefined && max !== undefined) {
    return `${formatAmount(min)} – ${formatAmount(max)}`
  }
  if (min !== undefined) {
    return `≥ ${formatAmount(min)}`
  }
  if (max !== undefined) {
    return `≤ ${formatAmount(max)}`
  }
  return undefined
}
