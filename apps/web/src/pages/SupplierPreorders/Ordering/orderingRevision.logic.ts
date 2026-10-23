// 下单页与修订版相关的纯逻辑：本店订过、但已被供应商下架的商品摘要（页面顶部提示）。
// 商品行上的修订标记推导复用管理端的 revision.logic（纯函数，不含 React 组件）。
import type { SupplierPreorderRemovedLine } from '../../../types/supplierPreorder'

/** 收起时最多列几款 */
export const REMOVED_LINES_PREVIEW_LIMIT = 5

export interface RemovedLinesSummary {
  /** 本店下过箱数的下架商品（箱数 ≤ 0 的不提示） */
  count: number
  totalCartons: number
  shown: SupplierPreorderRemovedLine[]
  /** 收起时没列出的款数 */
  hiddenCount: number
}

/** 下架商品摘要：只算箱数 > 0 的；expanded = true 时全部列出。没有时返回 null（不显示提示）。 */
export function summarizeRemovedLines(
  lines: readonly SupplierPreorderRemovedLine[] | null | undefined,
  expanded: boolean,
  limit = REMOVED_LINES_PREVIEW_LIMIT,
): RemovedLinesSummary | null {
  const ordered = (lines ?? []).filter((line) => typeof line.cartons === 'number' && line.cartons > 0)
  if (!ordered.length) return null
  const shown = expanded ? ordered : ordered.slice(0, Math.max(1, limit))
  return {
    count: ordered.length,
    totalCartons: ordered.reduce((sum, line) => sum + line.cartons, 0),
    shown,
    hiddenCount: ordered.length - shown.length,
  }
}
