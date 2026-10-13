import type { ComingSoonHomeContainerSummary } from '../../../types/container'

// 即将上新页的货柜日期规则（纯函数，便于单测）。
// 口径与后端 ContainerWarehouseArrival、移动端「HB新品」页一致：
// 到仓库日期 = 实际到货日期，没有时用预计到岸日期；已有实际到货日期 = 已到仓，否则 = 在途。

export type ComingSoonDateTone = 'arrived' | 'soon' | 'future' | 'unknown'

type WarehouseDateFields = Pick<ComingSoonHomeContainerSummary, '实际到货日期' | '预计到岸日期'>

const MS_PER_DAY = 86_400_000
// 距到仓库日期不超过这么多天算「即将」（含已过预计日期但还没到仓的货柜）
const SOON_DAYS = 7

export function parseComingSoonDate(value?: string) {
  if (!value) return null
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? null : date
}

/** 货柜到仓库日期：实际到货日期优先，没有时用预计到岸日期；两者都没有返回 null。 */
export function resolveWarehouseDate(container: WarehouseDateFields) {
  return parseComingSoonDate(container.实际到货日期) ?? parseComingSoonDate(container.预计到岸日期)
}

/**
 * 日期色调：已到仓 = arrived；在途按「到仓库日期」距今天数分档（7 天内 soon，更远 future）。
 * 后端窗口含过去 7 天内预计到仓、但尚未登记实际到货的货柜，它们的天数为负，仍算 soon，
 * 不再像只看预计到岸日期时那样落到 unknown。
 */
export function getComingSoonDateTone(
  container: WarehouseDateFields,
  now: Date = new Date(),
): ComingSoonDateTone {
  if (parseComingSoonDate(container.实际到货日期)) return 'arrived'
  const warehouseDate = resolveWarehouseDate(container)
  if (!warehouseDate) return 'unknown'
  const today = new Date(now)
  today.setHours(0, 0, 0, 0)
  const target = new Date(warehouseDate)
  target.setHours(0, 0, 0, 0)
  const daysUntil = Math.round((target.getTime() - today.getTime()) / MS_PER_DAY)
  if (daysUntil <= SOON_DAYS) return 'soon'
  return 'future'
}
