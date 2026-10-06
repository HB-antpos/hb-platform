import type { LocalSupplierInvoiceItemDto } from '../../../../types/localSupplierInvoice'
import { DetailAction } from '../../../../types/localSupplierInvoice'
import { getProductStatusFilter } from './statusFilters'

/**
 * 明细处理进度分段。五段互斥、合计等于明细行数，用于工作台顶部的进度条与筛选：
 * - executed：批量执行成功后后端写回的 99「已执行」
 * - unchecked：还没做商品检测（existingProductCount 为空）；未检测行即使手工设了操作类型也先归这里，
 *   避免「执行全部待执行」把没检测过的行也送去新建商品
 * - pending：已设置会被批量执行处理的操作（新建商品 / 更新进货价 / 更新货号 / 添加多码）
 * - waiting：等待操作，需要人工确认
 * - none：无需操作（如进货价为空时后端默认的「无」）
 */
export type DetailProgressBucket = 'executed' | 'pending' | 'waiting' | 'unchecked' | 'none'

export type DetailProgressBucketFilter = 'all' | DetailProgressBucket

export const DETAIL_PROGRESS_BUCKETS: readonly DetailProgressBucket[] = [
  'executed',
  'pending',
  'waiting',
  'unchecked',
  'none',
]

/** 后端批量执行成功后写回的「已执行」操作类型。 */
export const EXECUTED_DETAIL_ACTION = 99

export const PENDING_DETAIL_ACTIONS = [
  DetailAction.CreateProduct,
  DetailAction.UpdatePurchasePrice,
  DetailAction.UpdateItemNumber,
  DetailAction.AddMultiCode,
] as const

export type PendingDetailAction = (typeof PENDING_DETAIL_ACTIONS)[number]

export interface DetailProgressStats {
  total: number
  executed: number
  pending: number
  waiting: number
  unchecked: number
  none: number
  /** 待执行按操作类型拆分，用于「新建 5 · 进货价 11」这类提示。 */
  pendingByAction: Record<PendingDetailAction, number>
}

function resolveDetailAction(detail: LocalSupplierInvoiceItemDto, rowActions: Record<string, number>) {
  return rowActions[detail.detailGUID] ?? detail.activityType ?? DetailAction.None
}

export function getDetailProgressBucket(
  detail: LocalSupplierInvoiceItemDto,
  rowActions: Record<string, number> = {},
): DetailProgressBucket {
  const action = resolveDetailAction(detail, rowActions)
  if (action === EXECUTED_DETAIL_ACTION) return 'executed'
  if (getProductStatusFilter(detail) === 'notDetected') return 'unchecked'
  if ((PENDING_DETAIL_ACTIONS as readonly number[]).includes(action)) return 'pending'
  if (action === DetailAction.WaitForOperation) return 'waiting'
  return 'none'
}

export function getDetailProgressStats(
  details: LocalSupplierInvoiceItemDto[],
  rowActions: Record<string, number> = {},
): DetailProgressStats {
  const stats: DetailProgressStats = {
    total: details.length,
    executed: 0,
    pending: 0,
    waiting: 0,
    unchecked: 0,
    none: 0,
    pendingByAction: {
      [DetailAction.CreateProduct]: 0,
      [DetailAction.UpdatePurchasePrice]: 0,
      [DetailAction.UpdateItemNumber]: 0,
      [DetailAction.AddMultiCode]: 0,
    },
  }

  for (const detail of details) {
    const bucket = getDetailProgressBucket(detail, rowActions)
    stats[bucket] += 1
    if (bucket === 'pending') {
      stats.pendingByAction[resolveDetailAction(detail, rowActions) as PendingDetailAction] += 1
    }
  }

  return stats
}

export function filterDetailsByProgressBucket(
  details: LocalSupplierInvoiceItemDto[],
  bucket: DetailProgressBucketFilter,
  rowActions: Record<string, number> = {},
) {
  if (bucket === 'all') return details
  return details.filter((detail) => getDetailProgressBucket(detail, rowActions) === bucket)
}

/** 「执行全部待执行」的目标行：按全部明细计算，不受当前搜索和筛选影响。 */
export function getPendingExecutionDetailGuids(
  details: LocalSupplierInvoiceItemDto[],
  rowActions: Record<string, number> = {},
) {
  return details
    .filter((detail) => detail.detailGUID && getDetailProgressBucket(detail, rowActions) === 'pending')
    .map((detail) => detail.detailGUID)
}

/** 已执行占比（整数百分比），明细为空时为 0。 */
export function getExecutedPercent(stats: Pick<DetailProgressStats, 'total' | 'executed'>) {
  if (!stats.total) return 0
  return Math.round((stats.executed / stats.total) * 100)
}
