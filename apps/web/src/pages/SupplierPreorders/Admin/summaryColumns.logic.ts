// 「订货汇总」分店列的纯逻辑：哪些分店显示、列顺序、拖拽落点与重排、本机记忆（localStorage）、分店搜索。
// 列的显示 / 顺序只是客户端视图：合计、指标、装柜进度始终按全部开启分店计算，不受这里影响。
// 不依赖 React / antd，便于 node 直接跑单测。
import type { SupplierPreorderStoreOrderStatus, SupplierPreorderStoreRef } from '../../../types/supplierPreorder'

import { DEFAULT_SORT, isSortValue, normalizeSort, type SortState } from './summarySort.logic'

/**
 * 用户对分店列的手动设置：
 * - order：手动顺序（可以不完整；不在里面的分店按矩阵原顺序排在最右，所以新开启的分店天然在最右）
 * - shown：手动加过的列（没有订单也显示）
 * - hidden：手动隐藏的列（即使有订单也不显示，优先级高于 shown 与默认规则）
 * 三者都为空 = 默认视图：只显示「已有订单」的开启分店，按矩阵顺序（店号）。
 */
export interface ColumnPrefs {
  order: string[]
  shown: string[]
  hidden: string[]
}

export const EMPTY_COLUMN_PREFS: ColumnPrefs = { order: [], shown: [], hidden: [] }

export interface ResolvedColumns {
  /** 全部开启分店，按手动顺序（含当前没显示的） */
  all: SupplierPreorderStoreRef[]
  /** 当前显示的列，顺序即列顺序 */
  visible: SupplierPreorderStoreRef[]
  /** 已开启但当前没显示的分店 */
  hiddenStores: SupplierPreorderStoreRef[]
}

/** 默认显示规则用到的「有订单」：订单状态为草稿 / 已提交，或该店在全表有箱数 */
export function storeHasOrder(
  storeGuid: string,
  statuses: Record<string, SupplierPreorderStoreOrderStatus>,
  storeCartons: Record<string, number>,
): boolean {
  const status = statuses[storeGuid]
  return status === 'Draft' || status === 'Submitted' || (storeCartons[storeGuid] ?? 0) > 0
}

/** 由矩阵算出「有订单的分店」集合（只在矩阵加载时算一次，避免用户把某店数量改成 0 后整列消失） */
export function collectOrderedStoreGuids(
  stores: SupplierPreorderStoreRef[],
  statuses: Record<string, SupplierPreorderStoreOrderStatus>,
  storeCartons: Record<string, number>,
): Set<string> {
  return new Set(stores.filter((store) => storeHasOrder(store.storeGuid, statuses, storeCartons)).map((store) => store.storeGuid))
}

function unique(list: readonly string[]): string[] {
  return [...new Set(list)]
}

/** 按手动设置算出全部列 / 显示列 / 未显示列；设置里已不存在的 guid 直接忽略 */
export function resolveColumns(stores: SupplierPreorderStoreRef[], orderedGuids: ReadonlySet<string>, prefs: ColumnPrefs): ResolvedColumns {
  const byGuid = new Map(stores.map((store) => [store.storeGuid, store]))
  const manual = unique(prefs.order).filter((guid) => byGuid.has(guid))
  const manualSet = new Set(manual)
  const all = [...manual.map((guid) => byGuid.get(guid)!), ...stores.filter((store) => !manualSet.has(store.storeGuid))]
  const hidden = new Set(prefs.hidden)
  const shown = new Set(prefs.shown)
  const isVisible = (guid: string) => (hidden.has(guid) ? false : shown.has(guid) ? true : orderedGuids.has(guid))
  return {
    all,
    visible: all.filter((store) => isVisible(store.storeGuid)),
    hiddenStores: all.filter((store) => !isVisible(store.storeGuid)),
  }
}

// ---------- 设置的修改（都返回新对象；没有变化返回 null，调用方据此跳过保存与播报） ----------

/** 落点 insertIndex（0..n，在第 insertIndex 列之前插入）换算成拖完后的列位置 */
export function finalIndexAfterDrop(sourceIndex: number, insertIndex: number): number {
  return insertIndex > sourceIndex ? insertIndex - 1 : insertIndex
}

/** 把显示列里的 guid 移到 toIndex（0 起，指移动后的位置）；未显示的列保持相对顺序排在后面 */
export function reorderVisible(prefs: ColumnPrefs, resolved: ResolvedColumns, guid: string, toIndex: number): ColumnPrefs | null {
  const current = resolved.visible.map((store) => store.storeGuid)
  if (!current.includes(guid)) return null
  const next = current.filter((item) => item !== guid)
  next.splice(Math.max(0, Math.min(toIndex, next.length)), 0, guid)
  if (next.every((item, index) => item === current[index])) return null
  return { ...prefs, order: [...next, ...resolved.hiddenStores.map((store) => store.storeGuid)] }
}

/** 拖拽松手：insertIndex 是「在第几列之前」，没有实际移动返回 null */
export function reorderByDrop(prefs: ColumnPrefs, resolved: ResolvedColumns, guid: string, insertIndex: number): ColumnPrefs | null {
  const sourceIndex = resolved.visible.findIndex((store) => store.storeGuid === guid)
  if (sourceIndex < 0) return null
  return reorderVisible(prefs, resolved, guid, finalIndexAfterDrop(sourceIndex, insertIndex))
}

/** 键盘 Alt+←/→：左右移一列；到头了返回 null。position 是移动后的第几位（1 起），给 aria-live 播报 */
export function moveVisibleBy(prefs: ColumnPrefs, resolved: ResolvedColumns, guid: string, delta: number): { prefs: ColumnPrefs; position: number } | null {
  const from = resolved.visible.findIndex((store) => store.storeGuid === guid)
  const to = from + delta
  if (from < 0 || to < 0 || to >= resolved.visible.length) return null
  const next = reorderVisible(prefs, resolved, guid, to)
  return next ? { prefs: next, position: to + 1 } : null
}

/** 显示一列（加列 / 开启后加列）：放到最右，并记为手动显示（没订单也显示） */
export function showColumn(prefs: ColumnPrefs, resolved: ResolvedColumns, guid: string): ColumnPrefs {
  return {
    order: [...resolved.all.map((store) => store.storeGuid).filter((item) => item !== guid), guid],
    shown: unique([...prefs.shown, guid]),
    hidden: prefs.hidden.filter((item) => item !== guid),
  }
}

/** 隐藏一列：记为手动隐藏，位置不动（以后再显示会回到最右） */
export function hideColumn(prefs: ColumnPrefs, guid: string): ColumnPrefs {
  return { ...prefs, shown: prefs.shown.filter((item) => item !== guid), hidden: unique([...prefs.hidden, guid]) }
}

/** 「只显示有订单的」：清掉手动显示 / 隐藏，保留手动顺序 */
export function showOnlyOrdered(prefs: ColumnPrefs): ColumnPrefs {
  return { order: prefs.order, shown: [], hidden: [] }
}

/** 「恢复默认顺序」：顺序回到矩阵原顺序（店号），显示规则也回默认 */
export function resetColumns(): ColumnPrefs {
  return { order: [], shown: [], hidden: [] }
}

/** 订单表里的分店增减后保持自洽：丢掉已不存在的 guid；没有变化时返回原对象（便于 setState 判断相等） */
export function prunePrefs(prefs: ColumnPrefs, validGuids: ReadonlySet<string>): ColumnPrefs {
  const keep = (list: string[]) => unique(list).filter((guid) => validGuids.has(guid))
  const order = keep(prefs.order)
  const shown = keep(prefs.shown)
  const hidden = keep(prefs.hidden)
  const same = (a: string[], b: string[]) => a.length === b.length && a.every((item, index) => item === b[index])
  return same(order, prefs.order) && same(shown, prefs.shown) && same(hidden, prefs.hidden) ? prefs : { order, shown, hidden }
}

/**
 * 提示条用：未显示的开启分店数、名称，以及措辞开关 allWithoutOrder。
 * allWithoutOrder 为 true 时提示条写「显示有订单的 N 家；M 家已开启但还没下单（…）未显示」，
 * 只有「显示的列全是有订单的、未显示的全是没下单的」才成立；手动隐藏了有订单的列、或手动加了没订单的列，就改用中性措辞。
 */
export function summarizeHiddenColumns(resolved: ResolvedColumns, orderedGuids: ReadonlySet<string>): { count: number; names: string[]; allWithoutOrder: boolean } {
  const hiddenStores = resolved.hiddenStores
  return {
    count: hiddenStores.length,
    names: hiddenStores.map((store) => store.storeName),
    allWithoutOrder: hiddenStores.every((store) => !orderedGuids.has(store.storeGuid)) && resolved.visible.every((store) => orderedGuids.has(store.storeGuid)),
  }
}

// ---------- 拖拽落点 ----------

/** 鼠标 x 落在哪两列之间：返回「在第几列之前插入」（0..列数）。以每列的中线为界：过了中线才算越过这一列 */
export function insertIndexAtX(rects: { left: number; right: number }[], x: number): number {
  const index = rects.findIndex((rect) => x < (rect.left + rect.right) / 2)
  return index < 0 ? rects.length : index
}

/** 竖直列表（下拉里的「已显示」）同理：鼠标 y 在行的上半 → 插到这一行之前，下半 → 之后 */
export function insertIndexAtRow(rowIndex: number, y: number, rect: { top: number; bottom: number }): number {
  return y < (rect.top + rect.bottom) / 2 ? rowIndex : rowIndex + 1
}

export interface DropPreview {
  /** 拖完后是否真的换了位置（没换不画落点线） */
  changed: boolean
  /** 拖完后的第几位（1 起） */
  position: number
  /** 落点右边的那一列（提示条写「放到 X 之前」）；落在最后为 null */
  beforeGuid: string | null
}

export function previewDrop(visibleGuids: string[], guid: string, insertIndex: number): DropPreview | null {
  const sourceIndex = visibleGuids.indexOf(guid)
  if (sourceIndex < 0) return null
  const finalIndex = finalIndexAfterDrop(sourceIndex, insertIndex)
  return { changed: finalIndex !== sourceIndex, position: finalIndex + 1, beforeGuid: visibleGuids[insertIndex] ?? null }
}

// ---------- 分店搜索 ----------

/** 按名称 / 店号搜索，不区分大小写；关键字为空时全部匹配 */
export function matchesStoreKeyword(store: { storeName: string; storeCode: string }, keyword: string): boolean {
  const text = keyword.trim().toLowerCase()
  if (!text) return true
  return store.storeName.toLowerCase().includes(text) || store.storeCode.toLowerCase().includes(text)
}

// ---------- 本机记忆 ----------

export interface StoredSummaryView {
  columns: ColumnPrefs
  sort: SortState
}

export const DEFAULT_SUMMARY_VIEW: StoredSummaryView = { columns: EMPTY_COLUMN_PREFS, sort: DEFAULT_SORT }

type StorageLike = Pick<Storage, 'getItem' | 'setItem'>

const STORAGE_VERSION = 1

export function summaryViewStorageKey(formGuid: string): string {
  return `sp-admin-summary-view:${formGuid}`
}

function toGuidList(value: unknown): string[] {
  return Array.isArray(value) ? unique(value.filter((item): item is string => typeof item === 'string' && item.length > 0)) : []
}

/** 解析本机保存的视图；任何异常（空、坏 JSON、结构不对）都按默认视图处理，绝不抛出 */
export function parseSummaryView(raw: string | null | undefined): StoredSummaryView {
  if (!raw) return DEFAULT_SUMMARY_VIEW
  try {
    const data: unknown = JSON.parse(raw)
    if (!data || typeof data !== 'object' || Array.isArray(data)) return DEFAULT_SUMMARY_VIEW
    const record = data as Record<string, unknown>
    if (record.v !== STORAGE_VERSION) return DEFAULT_SUMMARY_VIEW
    const hidden = toGuidList(record.hidden)
    const hiddenSet = new Set(hidden)
    const sortRecord = record.sort && typeof record.sort === 'object' ? (record.sort as Record<string, unknown>) : {}
    const sort = normalizeSort({
      sort: isSortValue(sortRecord.sort) ? sortRecord.sort : 'seq',
      storeGuid: typeof sortRecord.storeGuid === 'string' ? sortRecord.storeGuid : undefined,
    })
    return {
      // 同时出现在 shown 与 hidden 的以 hidden 为准（与 resolveColumns 一致）
      columns: { order: toGuidList(record.order), shown: toGuidList(record.shown).filter((guid) => !hiddenSet.has(guid)), hidden },
      sort,
    }
  } catch {
    return DEFAULT_SUMMARY_VIEW
  }
}

export function serializeSummaryView(view: StoredSummaryView): string {
  return JSON.stringify({ v: STORAGE_VERSION, ...view.columns, sort: view.sort })
}

function defaultStorage(): StorageLike | null {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage
  } catch {
    // 隐私模式 / 禁用站点数据时访问 localStorage 本身会抛
    return null
  }
}

export function loadSummaryView(formGuid: string, storage: StorageLike | null = defaultStorage()): StoredSummaryView {
  try {
    return parseSummaryView(storage?.getItem(summaryViewStorageKey(formGuid)))
  } catch {
    return DEFAULT_SUMMARY_VIEW
  }
}

/** 保存失败（空间满 / 被禁用）静默忽略：视图仍在内存里生效，只是下次打开不记得 */
export function saveSummaryView(formGuid: string, view: StoredSummaryView, storage: StorageLike | null = defaultStorage()): void {
  try {
    storage?.setItem(summaryViewStorageKey(formGuid), serializeSummaryView(view))
  } catch {
    // 忽略
  }
}
