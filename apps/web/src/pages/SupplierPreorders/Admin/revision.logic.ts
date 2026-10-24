// 供应商节日预订单 PR 3 的纯逻辑：修订标记（商品行上的小标签）、价格涨跌、修订预览计数与筛选、提醒结果归类。
// 只依赖类型，不依赖 React / antd / 其他页面逻辑：下单页（Ordering）也直接复用这里的修订标记推导。
import type {
  SupplierPreorderFormStoreStatus,
  SupplierPreorderRemindResult,
  SupplierPreorderRevisionChange,
  SupplierPreorderRevisionEntry,
  SupplierPreorderRevisionMark,
  SupplierPreorderRevisionPreview,
} from '../../../types/supplierPreorder'

// ---------- 价格涨跌 ----------

/** 进价相差不到半分钱视为没变（避免 1.1 与 1.1000001 这类浮点误差显示成涨价） */
const PRICE_EPSILON = 0.005

export type PriceDirection = 'up' | 'down' | 'same'

/** 旧价 → 新价的涨跌；任一边缺失返回 null（不显示红绿）。 */
export function getPriceDirection(oldPrice: number | null | undefined, newPrice: number | null | undefined): PriceDirection | null {
  if (typeof oldPrice !== 'number' || typeof newPrice !== 'number' || !Number.isFinite(oldPrice) || !Number.isFinite(newPrice)) return null
  const diff = newPrice - oldPrice
  if (Math.abs(diff) < PRICE_EPSILON) return 'same'
  return diff > 0 ? 'up' : 'down'
}

// ---------- 修订标记（商品行小标签） ----------

/** 「其他改动」：不单独出标签，合并成一个，悬停列出明细 */
export const OTHER_REVISION_CHANGES: SupplierPreorderRevisionChange[] = ['Barcode', 'Image', 'Cbm', 'Category']

export type RevisionChip =
  | { kind: 'added' }
  | { kind: 'restored' }
  | { kind: 'price'; from: number | null; to: number; direction: PriceDirection | null }
  | { kind: 'carton'; from: number | null; to: number }
  | { kind: 'name' }
  | { kind: 'other'; changes: SupplierPreorderRevisionChange[] }

/** 紫色 = 这一版新出现（新增 / 重新上架）；橙色 = 这一版改了内容 */
export function getRevisionChipColor(chip: RevisionChip): 'purple' | 'orange' | 'default' {
  if (chip.kind === 'added' || chip.kind === 'restored') return 'purple'
  if (chip.kind === 'other') return 'default'
  return 'orange'
}

/**
 * 由最近一次修订的标记推导行上要显示的小标签（顺序固定：新增/重新上架 → 改价 → 箱规 → 改名 → 其他）。
 * - 改价：旧价取 mark.prevPrice、新价取当前进价；旧价缺失时只显示「改价」不带箭头。
 * - Removed 不出标签：已下架由行状态（isActive = false）单独显示。
 * - 旧值与当前值其实一样（例如改回去了）时不再标改价 / 改箱规，避免「$1.10 → $1.10」。
 */
export function buildRevisionChips(
  mark: SupplierPreorderRevisionMark | null | undefined,
  current: { price: number; cartonQty: number },
): RevisionChip[] {
  if (!mark || !Array.isArray(mark.changes) || mark.changes.length === 0) return []
  const changes = new Set(mark.changes)
  const chips: RevisionChip[] = []
  if (changes.has('Added')) chips.push({ kind: 'added' })
  else if (changes.has('Restored')) chips.push({ kind: 'restored' })
  if (changes.has('Price')) {
    const from = typeof mark.prevPrice === 'number' && Number.isFinite(mark.prevPrice) ? mark.prevPrice : null
    const direction = getPriceDirection(from, current.price)
    if (direction !== 'same') chips.push({ kind: 'price', from, to: current.price, direction })
  }
  if (changes.has('Carton')) {
    const from = typeof mark.prevCartonQty === 'number' && Number.isFinite(mark.prevCartonQty) ? mark.prevCartonQty : null
    if (from !== current.cartonQty) chips.push({ kind: 'carton', from, to: current.cartonQty })
  }
  if (changes.has('Name')) chips.push({ kind: 'name' })
  const others = OTHER_REVISION_CHANGES.filter((change) => changes.has(change))
  if (others.length) chips.push({ kind: 'other', changes: others })
  return chips
}

/** 价格格里要不要显示划掉的旧价：只有标了改价、旧价存在且确实不同才显示。 */
export function getPreviousPrice(mark: SupplierPreorderRevisionMark | null | undefined, currentPrice: number): number | null {
  if (!mark?.changes?.includes('Price')) return null
  const from = mark.prevPrice
  if (typeof from !== 'number' || !Number.isFinite(from)) return null
  return getPriceDirection(from, currentPrice) === 'same' ? null : from
}

/** 箱规格里划掉的旧箱规（同上口径）。 */
export function getPreviousCartonQty(mark: SupplierPreorderRevisionMark | null | undefined, currentCartonQty: number): number | null {
  if (!mark?.changes?.includes('Carton')) return null
  const from = mark.prevCartonQty
  if (typeof from !== 'number' || !Number.isFinite(from) || from === currentCartonQty) return null
  return from
}

// ---------- 修订预览 ----------

export type RevisionEntryFilter = 'all' | 'added' | 'removed' | 'restored' | 'price' | 'carton' | 'other'

/** 「其他改动」：kind = Changed 但既没改价也没改箱规（改名、条码、图片、体积、分类）。 */
export function isOtherChangeEntry(entry: Pick<SupplierPreorderRevisionEntry, 'kind' | 'changes'>): boolean {
  return entry.kind === 'Changed' && !entry.changes.includes('Price') && !entry.changes.includes('Carton')
}

export function filterRevisionEntries(entries: SupplierPreorderRevisionEntry[], filter: RevisionEntryFilter): SupplierPreorderRevisionEntry[] {
  switch (filter) {
    case 'added':
      return entries.filter((entry) => entry.kind === 'Added')
    case 'removed':
      return entries.filter((entry) => entry.kind === 'Removed')
    case 'restored':
      return entries.filter((entry) => entry.kind === 'Restored')
    // 改价 / 改箱规按 changes 判断：重新上架的商品同时改了价也算进来
    case 'price':
      return entries.filter((entry) => entry.changes.includes('Price'))
    case 'carton':
      return entries.filter((entry) => entry.changes.includes('Carton'))
    case 'other':
      return entries.filter(isOtherChangeEntry)
    default:
      return entries
  }
}

export interface RevisionCountItem {
  key: 'added' | 'removed' | 'restored' | 'priceChanged' | 'cartonChanged' | 'otherChanged' | 'unchanged' | 'invalid'
  count: number
  /** 对应的表格筛选；不变 / 不能导入不在表格里列出，为 null */
  filter: RevisionEntryFilter | null
}

/** 预览顶部的计数条（顺序固定）。其他改动由 entries 现算：后端 counts.changed 含改价、改箱规。 */
export function buildRevisionCountItems(preview: Pick<SupplierPreorderRevisionPreview, 'counts' | 'entries'>): RevisionCountItem[] {
  const { counts } = preview
  return [
    { key: 'added', count: counts.added, filter: 'added' },
    { key: 'removed', count: counts.removed, filter: 'removed' },
    { key: 'restored', count: counts.restored, filter: 'restored' },
    { key: 'priceChanged', count: counts.priceChanged, filter: 'price' },
    { key: 'cartonChanged', count: counts.cartonChanged, filter: 'carton' },
    { key: 'otherChanged', count: preview.entries.filter(isOtherChangeEntry).length, filter: 'other' },
    { key: 'unchanged', count: counts.unchanged, filter: null },
    { key: 'invalid', count: counts.invalid, filter: null },
  ]
}

/** 修订版里有没有任何改动（新增 / 下架 / 重新上架 / 改动） */
export function hasRevisionChanges(preview: Pick<SupplierPreorderRevisionPreview, 'counts'>): boolean {
  const { counts } = preview
  return counts.added + counts.removed + counts.restored + counts.changed > 0
}

export type RevisionApplyBlock = 'noRows' | 'noChanges' | null

/**
 * 能否应用修订版：
 * - 一行能导入的都没有（例如列映射错了）→ 不能应用，否则所有商品都会被标成下架；
 * - 和当前订单表完全一致 → 没必要应用。
 */
export function getRevisionApplyBlock(preview: Pick<SupplierPreorderRevisionPreview, 'counts'>): RevisionApplyBlock {
  const { counts } = preview
  if (counts.total - counts.invalid <= 0) return 'noRows'
  if (!hasRevisionChanges(preview)) return 'noChanges'
  return null
}

/** 列表截断：前 limit 个 + 剩余个数（分店名、下架商品等长列表）。 */
export function truncateList<T>(list: readonly T[], limit: number): { shown: T[]; rest: number } {
  const safeLimit = Math.max(0, Math.floor(limit))
  return { shown: list.slice(0, safeLimit), rest: Math.max(0, list.length - safeLimit) }
}

// ---------- 提醒未提交的分店 ----------

/** 还没提交的开启分店（未开始、草稿、提交后又改过）——提醒按钮上的预估数。 */
export function countPendingStores(stores: Pick<SupplierPreorderFormStoreStatus, 'enabled' | 'orderStatus'>[]): number {
  return stores.filter((store) => store.enabled && store.orderStatus !== 'Submitted').length
}

export type RemindOutcome = 'nothingPending' | 'allNotified' | 'partial' | 'nobodyNotified'

/**
 * 提醒结果归类：
 * - nothingPending：没有未提交的分店；
 * - nobodyNotified：有未提交的分店，但一个人都没通知到（全部没人授权）；
 * - partial：通知到了一部分人，另有分店没人授权；
 * - allNotified：都通知到了。
 */
export function getRemindOutcome(result: SupplierPreorderRemindResult): RemindOutcome {
  if (result.pendingStoreCount <= 0) return 'nothingPending'
  const missing = (result.storesWithoutOrderers ?? []).length
  if (result.notifiedUserCount <= 0) return 'nobodyNotified'
  return missing > 0 ? 'partial' : 'allNotified'
}
