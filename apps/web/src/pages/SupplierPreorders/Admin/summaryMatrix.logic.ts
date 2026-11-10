// 「订货汇总」页签的纯逻辑：单元格输入解析与键盘移动、保存中的乐观合计、保存结果合并、
// 折合箱与偏差、装柜进度、分店明细行（排序 / 偏差标记）与「按去年销量填入空白店」建议。
// 不依赖 React / antd，便于 node 直接跑单测。
import type {
  SupplierPreorderAdminSaveLineResult,
  SupplierPreorderMatrix,
  SupplierPreorderMatrixRow,
  SupplierPreorderMatrixTotals,
  SupplierPreorderStoreHistory,
  SupplierPreorderStoreOrderStatus,
  SupplierPreorderStoreRef,
} from '../../../types/supplierPreorder'
import { parseLineKey } from '../Ordering/lineSaveQueue.logic'

import { roundMoney, type MatrixUnit } from './supplierPreorderAdmin.logic'

/** 订单表没有填柜容量时的兜底：40GP ≈ 67 m³ */
export const DEFAULT_CONTAINER_CAPACITY_CBM = 67
/** 分店箱数输入上限（与后端 0–9999 一致） */
export const MAX_CARTONS = 9999
/** 去年卖过、今年没订的提示门槛：该店去年销量折合 ≥ 1.5 箱 */
export const MISSED_THRESHOLD_CARTONS = 1.5
/** 今年订货 ÷ 去年销量 ≥ 160% 标琥珀色（订多了） */
export const HIGH_RATIO_THRESHOLD = 1.6

// ---------- 单元格输入 ----------

export type CartonInputResult = { ok: true; value: number } | { ok: false; reason: 'empty' | 'invalid' }

/** 解析输入框文字：只接受 0–9999 的整数；空串单独标出（调用方按「没改」处理） */
export function parseCartonInput(text: string): CartonInputResult {
  const trimmed = text.trim()
  if (!trimmed) return { ok: false, reason: 'empty' }
  if (!/^\d+$/.test(trimmed)) return { ok: false, reason: 'invalid' }
  const value = Number(trimmed)
  if (!Number.isSafeInteger(value) || value > MAX_CARTONS) return { ok: false, reason: 'invalid' }
  return { ok: true, value }
}

/** 输入过程中只留数字，最多 4 位 */
export function sanitizeCartonDraft(text: string): string {
  return text.replace(/\D/g, '').slice(0, String(MAX_CARTONS).length)
}

// ---------- 键盘移动 ----------

export interface CellPos {
  row: number
  col: number
}

export type CellMove = 'up' | 'down' | 'left' | 'right' | 'next' | 'prev'

/** 编辑中的按键 → 移动方向：Enter 下一行同店；Tab / Shift+Tab 同行下一家 / 上一家店；方向键对应四个方向 */
export function keyToMove(key: string, shiftKey = false): CellMove | null {
  switch (key) {
    case 'Enter':
      return 'down'
    case 'Tab':
      return shiftKey ? 'prev' : 'next'
    case 'ArrowUp':
      return 'up'
    case 'ArrowDown':
      return 'down'
    case 'ArrowLeft':
      return 'left'
    case 'ArrowRight':
      return 'right'
    default:
      return null
  }
}

/**
 * 计算移动后的格子；到边界返回 null（停在原地）。
 * Tab / Shift+Tab 到行尾 / 行首时换行（到下一行第一家店 / 上一行最后一家店），方便整行连续录入；方向键不换行。
 */
export function moveCell(pos: CellPos, move: CellMove, rowCount: number, colCount: number): CellPos | null {
  if (rowCount <= 0 || colCount <= 0) return null
  let { row, col } = pos
  switch (move) {
    case 'up':
      row -= 1
      break
    case 'down':
      row += 1
      break
    case 'left':
      col -= 1
      break
    case 'right':
      col += 1
      break
    case 'next':
      col += 1
      if (col >= colCount) {
        col = 0
        row += 1
      }
      break
    case 'prev':
      col -= 1
      if (col < 0) {
        col = colCount - 1
        row -= 1
      }
      break
  }
  if (row < 0 || row >= rowCount || col < 0 || col >= colCount) return null
  return { row, col }
}

// ---------- 折合箱与偏差 ----------

/** 去年销量（件）折合箱数；箱规无效按 0 */
export function toLastYearCartons(salesPieces: number | null | undefined, cartonQty: number): number {
  if (!salesPieces || !(cartonQty > 0)) return 0
  return salesPieces / cartonQty
}

/** 格子小字「去年 X.X」的数字；该店去年没有记录返回 null（显示「去年 —」） */
export function formatLastYearCartons(salesPieces: number | null | undefined, cartonQty: number): string | null {
  if (salesPieces === null || salesPieces === undefined || !(salesPieces > 0) || !(cartonQty > 0)) return null
  return (salesPieces / cartonQty).toFixed(1)
}

/** 去年卖过、今年没订：今年 0 箱且去年折合 ≥ 1.5 箱 */
export function isMissedCell(thisYearCartons: number, lastYearCartons: number): boolean {
  return thisYearCartons === 0 && lastYearCartons >= MISSED_THRESHOLD_CARTONS
}

/** 今年订货（件）÷ 去年销量；去年没销量返回 null */
export function thisYearRatio(thisYearCartons: number, cartonQty: number, salesPieces: number | null | undefined): number | null {
  if (!salesPieces || salesPieces <= 0) return null
  return (thisYearCartons * cartonQty) / salesPieces
}

/** 非「箱」单位下格子小字里的去年数：件 = 销量；金额 = 销量 × 进价 */
export function getLastYearCellValue(row: Pick<SupplierPreorderMatrixRow, 'storeLastYearSales' | 'cartonQty' | 'price'>, storeGuid: string, unit: MatrixUnit): number | null {
  const sales = row.storeLastYearSales[storeGuid]
  if (sales === undefined || !(sales > 0)) return null
  if (unit === 'pieces') return sales
  if (unit === 'amount') return roundMoney(sales * row.price)
  return toLastYearCartons(sales, row.cartonQty)
}

// ---------- 保存中的乐观合计 ----------

const roundCbm = (value: number) => Math.round(value * 1_000_000) / 1_000_000

function cloneTotals(totals: SupplierPreorderMatrixTotals): SupplierPreorderMatrixTotals {
  return {
    ...totals,
    storeCartons: { ...totals.storeCartons },
    storePieces: { ...totals.storePieces },
    storeAmount: { ...totals.storeAmount },
    storeCbm: { ...totals.storeCbm },
  }
}

/**
 * 把「还没保存完成的格子」叠加到矩阵上：格子、该行合计、表尾分店合计、全表合计、已下单款数即时变化，
 * 保存成功后再由服务端返回的 totals 覆盖（applySaveResult）。pending 的键是 makeLineKey(storeGuid, itemGuid)。
 * 不改入参；没有可叠加的内容时原样返回同一个对象（便于 useMemo 保持引用稳定）。
 */
export function applyPendingEdits(matrix: SupplierPreorderMatrix, pending: Readonly<Record<string, number>>): SupplierPreorderMatrix {
  const keys = Object.keys(pending)
  if (!keys.length) return matrix
  const rowIndex = new Map(matrix.rows.items.map((row, index) => [row.itemGuid, index]))
  let rows: SupplierPreorderMatrixRow[] | null = null
  let totals: SupplierPreorderMatrixTotals | null = null

  for (const key of keys) {
    const { storeGuid, itemGuid } = parseLineKey(key)
    const index = rowIndex.get(itemGuid)
    if (index === undefined) continue
    const row = (rows ?? matrix.rows.items)[index]
    const next = pending[key]
    const delta = next - (row.storeCartons[storeGuid] ?? 0)
    if (delta === 0) continue

    rows ??= [...matrix.rows.items]
    totals ??= cloneTotals(matrix.totals)

    const storeCartons = { ...row.storeCartons }
    if (next > 0) storeCartons[storeGuid] = next
    else delete storeCartons[storeGuid]
    const cartonCbm = row.cartonCbm ?? 0
    const pieces = delta * row.cartonQty
    const updated: SupplierPreorderMatrixRow = {
      ...row,
      storeCartons,
      totalCartons: row.totalCartons + delta,
      totalPieces: row.totalPieces + pieces,
      totalAmount: roundMoney(row.totalAmount + pieces * row.price),
      totalCbm: roundCbm(row.totalCbm + delta * cartonCbm),
    }
    rows[index] = updated

    totals.storeCartons[storeGuid] = (totals.storeCartons[storeGuid] ?? 0) + delta
    totals.storePieces[storeGuid] = (totals.storePieces[storeGuid] ?? 0) + pieces
    totals.storeAmount[storeGuid] = roundMoney((totals.storeAmount[storeGuid] ?? 0) + pieces * row.price)
    totals.storeCbm[storeGuid] = roundCbm((totals.storeCbm[storeGuid] ?? 0) + delta * cartonCbm)
    totals.totalCartons += delta
    totals.totalPieces += pieces
    totals.totalAmount = roundMoney(totals.totalAmount + pieces * row.price)
    totals.totalCbm = roundCbm(totals.totalCbm + delta * cartonCbm)
    // 这一款在全部分店的合计穿过 0：已下单款数 ±1
    if (row.totalCartons <= 0 && updated.totalCartons > 0) totals.orderedItemCount += 1
    else if (row.totalCartons > 0 && updated.totalCartons <= 0) totals.orderedItemCount -= 1
  }

  if (!rows || !totals) return matrix
  return { ...matrix, rows: { ...matrix.rows, items: rows }, totals }
}

/** 合并一次保存结果：格子、总部改过标记、该行合计、全表合计（服务端权威值）和「总部改过」计数 */
export function applySaveResult(matrix: SupplierPreorderMatrix, result: SupplierPreorderAdminSaveLineResult): SupplierPreorderMatrix {
  const items = matrix.rows.items.map((row) => {
    if (row.itemGuid !== result.itemGuid) return row
    const storeCartons = { ...row.storeCartons }
    if (result.cartons > 0) storeCartons[result.storeGuid] = result.cartons
    else delete storeCartons[result.storeGuid]
    const storeMarks = { ...row.storeMarks }
    if (result.mark) storeMarks[result.storeGuid] = result.mark
    else delete storeMarks[result.storeGuid]
    return {
      ...row,
      storeCartons,
      storeMarks,
      totalCartons: result.rowTotalCartons,
      totalPieces: result.rowTotalPieces,
      totalAmount: result.rowTotalAmount,
      totalCbm: result.rowTotalCbm,
    }
  })
  return {
    ...matrix,
    rows: { ...matrix.rows, items },
    totals: result.totals,
    counts: { ...matrix.counts, adjusted: result.adjustedCount, ordered: result.totals.orderedItemCount },
  }
}

// ---------- 指标条 ----------

export interface ContainerProgress {
  /** 实际使用的柜容量（m³） */
  capacity: number
  /** 是否用了兜底容量（订单表没填柜容量） */
  fallback: boolean
  /** 体积合计折合多少柜（小数） */
  containers: number
  /** 已装满的整柜数 */
  fullCount: number
  /** 当前正在装的这一柜的装载比例 0~1 */
  partialRatio: number
  /** 当前这一柜还差多少 m³ 装满 */
  remainingCbm: number
}

/** 装柜进度：容量取订单表详情里的柜容量，没有（≤0）就按 67 m³ 兜底 */
export function computeContainerProgress(totalCbm: number, capacityCbm?: number | null): ContainerProgress {
  const fallback = !(capacityCbm && capacityCbm > 0)
  const capacity = fallback ? DEFAULT_CONTAINER_CAPACITY_CBM : (capacityCbm as number)
  const safeTotal = Math.max(0, Number.isFinite(totalCbm) ? totalCbm : 0)
  // 体积正好是整柜时把它算成「刚好满 N 柜」，避免出现 0% 的下一柜
  const fullCount = Math.floor(safeTotal / capacity + 1e-9)
  const rest = Math.max(0, safeTotal - fullCount * capacity)
  const partialRatio = Math.min(1, rest / capacity)
  return { capacity, fallback, containers: safeTotal / capacity, fullCount, partialRatio, remainingCbm: Math.max(0, capacity - rest) }
}

export interface StoreProgress {
  submitted: number
  draft: number
  none: number
  /** 按分店顺序的状态，用于分段条 */
  segments: SupplierPreorderStoreOrderStatus[]
}

export function countStoreProgress(stores: readonly Pick<SupplierPreorderStoreRef, 'storeGuid'>[], statuses: Readonly<Record<string, SupplierPreorderStoreOrderStatus>>): StoreProgress {
  const segments = stores.map((store) => statuses[store.storeGuid] ?? 'None')
  return {
    submitted: segments.filter((status) => status === 'Submitted').length,
    draft: segments.filter((status) => status === 'Draft').length,
    none: segments.filter((status) => status === 'None').length,
    segments,
  }
}

// ---------- 抽屉：分店明细 ----------

export type StoreDetailFlag = 'missed' | 'high' | null

export interface StoreDetailRow {
  key: string
  /** 订单表开启的分店才有；去年有数据但没开启的分店为 null（只看不改） */
  storeGuid: string | null
  storeName: string
  enabled: boolean
  history: SupplierPreorderStoreHistory | null
  hasData: boolean
  purchaseQty: number
  salesQty: number
  sellThrough: number | null
  /** 去年销量折合箱数 */
  foldedCartons: number
  /** 今年已订箱数 */
  thisYear: number
  /** 今年 ÷ 去年销量；去年没销量为 null */
  ratio: number | null
  flag: StoreDetailFlag
}

/**
 * 分店明细行：先放订单表开启的分店（带今年已订箱数），再放去年有数据但没开启的分店；
 * 整体按「有去年记录的在前、去年销量从高到低、其余保持原顺序」排序。
 */
export function buildStoreDetailRows(
  stores: readonly SupplierPreorderStoreRef[],
  histories: readonly SupplierPreorderStoreHistory[] | null | undefined,
  cartonQty: number,
  cartonsOf: (storeGuid: string) => number,
): StoreDetailRow[] {
  const byGuid = new Map<string, SupplierPreorderStoreHistory>()
  const extras: SupplierPreorderStoreHistory[] = []
  for (const history of histories ?? []) {
    if (history.storeGuid) byGuid.set(history.storeGuid, history)
    else if (history.hasData) extras.push(history)
  }
  const enabledGuids = new Set(stores.map((store) => store.storeGuid))

  const toRow = (history: SupplierPreorderStoreHistory | null, storeGuid: string | null, storeName: string, enabled: boolean, key: string): StoreDetailRow => {
    const hasData = !!history?.hasData
    const purchaseQty = hasData ? (history?.purchaseQty ?? 0) : 0
    const salesQty = hasData ? (history?.salesQty ?? 0) : 0
    const thisYear = storeGuid ? cartonsOf(storeGuid) : 0
    const foldedCartons = toLastYearCartons(salesQty, cartonQty)
    const ratio = thisYearRatio(thisYear, cartonQty, salesQty)
    const flag: StoreDetailFlag = !enabled ? null : isMissedCell(thisYear, foldedCartons) ? 'missed' : ratio !== null && ratio >= HIGH_RATIO_THRESHOLD ? 'high' : null
    return { key, storeGuid, storeName, enabled, history, hasData, purchaseQty, salesQty, sellThrough: hasData ? (history?.sellThrough ?? null) : null, foldedCartons, thisYear, ratio, flag }
  }

  const rows: StoreDetailRow[] = stores.map((store) => toRow(byGuid.get(store.storeGuid) ?? null, store.storeGuid, store.storeName, true, store.storeGuid))
  for (const history of [...byGuid.values()].filter((item) => !enabledGuids.has(item.storeGuid as string))) {
    if (history.hasData) rows.push(toRow(history, null, history.storeName, false, `h-${history.storeGuid}`))
  }
  for (const history of extras) rows.push(toRow(history, null, history.storeName, false, `h-${history.storeCode || history.storeName}`))

  return rows
    .map((row, index) => ({ row, index }))
    .sort((a, b) => {
      if (a.row.hasData !== b.row.hasData) return a.row.hasData ? -1 : 1
      if (a.row.hasData && a.row.salesQty !== b.row.salesQty) return b.row.salesQty - a.row.salesQty
      return a.index - b.index
    })
    .map((entry) => entry.row)
}

export interface StoreDetailTotals {
  purchaseQty: number
  salesQty: number
  sellThrough: number | null
  foldedCartons: number
  thisYear: number
  ratio: number | null
}

/** 分店明细合计行：进货、销量求和；售罄率 = 销量 ÷ 进货；今年 ÷ 去年按全部分店件数算 */
export function sumStoreDetailRows(rows: readonly StoreDetailRow[], cartonQty: number): StoreDetailTotals {
  let purchaseQty = 0
  let salesQty = 0
  let thisYear = 0
  for (const row of rows) {
    purchaseQty += row.purchaseQty
    salesQty += row.salesQty
    thisYear += row.thisYear
  }
  return {
    purchaseQty,
    salesQty,
    sellThrough: purchaseQty > 0 ? salesQty / purchaseQty : null,
    foldedCartons: toLastYearCartons(salesQty, cartonQty),
    thisYear,
    ratio: thisYearRatio(thisYear, cartonQty, salesQty),
  }
}

export interface FillSuggestion {
  storeGuid: string
  storeName: string
  cartons: number
}

/**
 * 「按去年销量填入空白店」：对订单表开启、今年为 0、去年有销量的分店，
 * 建议箱数 = max(1, round(去年销量 ÷ 箱规))。箱规无效时没有建议。
 */
export function suggestFillBlanks(rows: readonly StoreDetailRow[], cartonQty: number): FillSuggestion[] {
  if (!(cartonQty > 0)) return []
  return rows
    .filter((row) => row.enabled && row.storeGuid && row.thisYear === 0 && row.salesQty > 0)
    .map((row) => ({ storeGuid: row.storeGuid as string, storeName: row.storeName, cartons: Math.min(MAX_CARTONS, Math.max(1, Math.round(row.salesQty / cartonQty))) }))
}

/** 上一款 / 下一款：只在当前页范围内切换，越界返回 null */
export function stepIndex(index: number, delta: number, length: number): number | null {
  const next = index + delta
  return index < 0 || next < 0 || next >= length ? null : next
}

/** 总部改过的悬停文案里的时间：悉尼时间 "MM-DD HH:mm"（入参是 formatSydneyDateTime 的结果 "YYYY-MM-DD HH:mm"） */
export function toMonthDayTime(sydneyDateTime: string): string {
  return /^\d{4}-/.test(sydneyDateTime) ? sydneyDateTime.slice(5) : sydneyDateTime
}
