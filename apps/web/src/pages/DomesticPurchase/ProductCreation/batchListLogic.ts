// 批次记录列表的纯逻辑：创建时间分段 → 请求参数、时间展示。
import dayjs from 'dayjs'
import type { BatchListParams } from '../../../types/domesticProductCreation'

export type BatchCreatedRange = 'all' | 'today' | 'last7' | 'last30'

export const BATCH_CREATED_RANGES: readonly BatchCreatedRange[] = ['all', 'today', 'last7', 'last30']

/** 列表查询条件。loadData(overrides) 用对象合并，显式写 undefined 表示「清除该筛选」。 */
export interface BatchListQuery {
  page: number
  pageSize: number
  supplierCode?: string
  range: BatchCreatedRange
}

const RANGE_DAYS: Record<Exclude<BatchCreatedRange, 'all'>, number> = {
  today: 1,
  last7: 7,
  last30: 30,
}

/**
 * 创建时间分段换算成后端的 startDate。「近 N 天」按自然日含今天计算（今天 = 1 天）。
 * 故意不传 endDate：后端用 CreatedAt <= endDate 比较，且 endDate 只带日期时等于当天 0 点，
 * 会把结束当天创建的批次全部排除；而所有分段都延伸到「现在」，不设上界即可。
 */
export function resolveCreatedRange(range: BatchCreatedRange, now: Date = new Date()): Pick<BatchListParams, 'startDate' | 'endDate'> {
  if (range === 'all') return {}
  const startDate = dayjs(now).startOf('day').subtract(RANGE_DAYS[range] - 1, 'day').format('YYYY-MM-DD')
  return { startDate }
}

export function buildBatchListParams(query: BatchListQuery, now: Date = new Date()): BatchListParams {
  return {
    page: query.page,
    pageSize: query.pageSize,
    ...(query.supplierCode ? { supplierCode: query.supplierCode } : {}),
    ...resolveCreatedRange(query.range, now),
  }
}

/** 批次创建时间展示为 YYYY-MM-DD HH:mm；无法解析时返回占位符，不显示 Invalid Date。 */
export function formatBatchTime(value?: string | null): string {
  if (!value) return '-'
  const parsed = dayjs(value)
  return parsed.isValid() ? parsed.format('YYYY-MM-DD HH:mm') : '-'
}
