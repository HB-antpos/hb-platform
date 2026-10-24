/**
 * containerDetailColumnPaste — 货柜明细「粘贴 Excel 列数据」纯逻辑
 *
 * 职责边界：
 * - 把剪贴板文本解析成单列值（多列数据直接拒绝，避免错位写入）
 * - 按当前显示顺序，从起始行向下把每个值解析为目标列的行补丁
 * - 单元格拖拽填充也复用同一套计划：把源单元格的值重复成一列值，再按目标范围生成补丁
 * - 不触碰 React 状态和保存队列；页面层负责把结果送入自动保存或“保存明细”草稿通道
 */

import type { ContainerDetail } from '../../../types/container'
import { parseProductImportPasteText } from '../../DomesticPurchase/ProductImport/utils'
import {
  containsChineseText,
  getContainerDetailEnglishName,
  getContainerDetailRealtimeImportPrice,
  getContainerDetailRealtimeRetailPrice,
  getContainerDetailVisibleOemPrice,
  isContainerDetailExistingPriceEditable,
  isContainerDetailThisPriceEditable,
} from './containerDetailLogic'

export const CONTAINER_DETAIL_COLUMN_PASTE_KEYS = [
  'englishName',
  'packingQuantity',
  'unitVolume',
  'middlePackQuantity',
  'floatRate',
  'warehouseImportPrice',
  'lastOEMPrice',
  'importPrice',
  'oemPrice',
  'remark',
] as const

export type ContainerDetailColumnPasteKey = typeof CONTAINER_DETAIL_COLUMN_PASTE_KEYS[number]

export type ContainerDetailColumnPasteField =
  | '英文名称'
  | '单件装箱数'
  | '单件体积'
  | '中包数'
  | '调整浮率'
  | '仓库进口价'
  | '仓库零售价'
  | '进口价格'
  | '贴牌价格'
  | '备注'

/**
 * 保存通道：
 * - pendingDraft：进口价格 / 零售价 / 英文名称，进入本地草稿，由“保存明细”统一落库
 * - autoSave：其余字段，进入自动保存队列
 * requiresCostParameters 为 true 的列会联动成本重算，缺少汇率/运费/总体积时页面层必须先拦截。
 */
export type ContainerDetailColumnPasteTarget = {
  field: ContainerDetailColumnPasteField
  channel: 'pendingDraft' | 'autoSave'
  requiresCostParameters: boolean
} & (
  | { kind: 'text' }
  | { kind: 'integer'; min?: number }
  | { kind: 'decimal'; precision: number; min?: number }
)

export const CONTAINER_DETAIL_COLUMN_PASTE_TARGETS: Record<ContainerDetailColumnPasteKey, ContainerDetailColumnPasteTarget> = {
  englishName: { field: '英文名称', kind: 'text', channel: 'pendingDraft', requiresCostParameters: false },
  packingQuantity: { field: '单件装箱数', kind: 'integer', min: 0, channel: 'autoSave', requiresCostParameters: true },
  unitVolume: { field: '单件体积', kind: 'decimal', precision: 3, min: 0, channel: 'autoSave', requiresCostParameters: true },
  middlePackQuantity: { field: '中包数', kind: 'integer', min: 0, channel: 'autoSave', requiresCostParameters: false },
  floatRate: { field: '调整浮率', kind: 'decimal', precision: 2, channel: 'autoSave', requiresCostParameters: true },
  // 已有价：保存明细时回写仓库表 / 商品主档 / 分店表（零售价还会联动分店价与价格任务）。
  warehouseImportPrice: { field: '仓库进口价', kind: 'decimal', precision: 2, min: 0, channel: 'pendingDraft', requiresCostParameters: false },
  lastOEMPrice: { field: '仓库零售价', kind: 'decimal', precision: 2, min: 0, channel: 'pendingDraft', requiresCostParameters: false },
  importPrice: { field: '进口价格', kind: 'decimal', precision: 2, min: 0, channel: 'pendingDraft', requiresCostParameters: false },
  oemPrice: { field: '贴牌价格', kind: 'decimal', precision: 2, min: 0, channel: 'pendingDraft', requiresCostParameters: false },
  remark: { field: '备注', kind: 'text', channel: 'autoSave', requiresCostParameters: false },
}

export function isContainerDetailColumnPasteKey(value: unknown): value is ContainerDetailColumnPasteKey {
  return typeof value === 'string' && (CONTAINER_DETAIL_COLUMN_PASTE_KEYS as readonly string[]).includes(value)
}

export type ContainerDetailColumnPasteParseResult =
  | { ok: true; values: string[] }
  | { ok: false; reason: 'empty' | 'multiple_columns' }

/**
 * 把剪贴板文本解析为单列值。
 * - 复用 Excel 粘贴解析器，保留中间空单元格（对应 Excel 空格子），去掉末尾空行
 * - 除第一列外还有非空单元格时视为多列，拒绝粘贴
 */
export function parseContainerDetailColumnPasteText(text: string): ContainerDetailColumnPasteParseResult {
  const rows = parseProductImportPasteText(text ?? '')
  const hasExtraColumnData = rows.some((row) => row.slice(1).some((cell) => cell.trim() !== ''))
  if (hasExtraColumnData) return { ok: false, reason: 'multiple_columns' }

  const values = rows.map((row) => (row[0] ?? '').trim())
  while (values.length > 0 && values[values.length - 1] === '') {
    values.pop()
  }
  if (!values.length) return { ok: false, reason: 'empty' }
  return { ok: true, values }
}

export type ContainerDetailColumnPasteValueResult =
  | { ok: true; value: string | number }
  | { ok: false }

/** 单个单元格文本按目标列类型解析：数字列剥离货币符号和千分位，文本列做业务校验。 */
export function parseContainerDetailColumnPasteValue(
  columnKey: ContainerDetailColumnPasteKey,
  rawValue: string,
): ContainerDetailColumnPasteValueResult {
  const target = CONTAINER_DETAIL_COLUMN_PASTE_TARGETS[columnKey]
  const trimmed = rawValue.trim()
  if (!trimmed) return { ok: false }

  if (target.kind === 'text') {
    // 英文名称列沿用单元格编辑的校验：含中文的值不进入草稿。
    if (columnKey === 'englishName' && containsChineseText(trimmed)) return { ok: false }
    return { ok: true, value: trimmed }
  }

  const numericText = trimmed.replace(/[$¥￥€£₩₹,，\s]/g, '')
  if (!numericText) return { ok: false }
  const parsed = Number(numericText)
  if (!Number.isFinite(parsed)) return { ok: false }
  if (target.min != null && parsed < target.min) return { ok: false }

  if (target.kind === 'integer') {
    if (!Number.isInteger(parsed)) return { ok: false }
    return { ok: true, value: parsed }
  }
  return { ok: true, value: Number(parsed.toFixed(target.precision)) }
}

/**
 * 目标列在该行是否可编辑：
 * - 已有进口价 / 已有零售价：只有已有商品（新商品还没有仓库记录）
 * - 本次进口价 / 本次零售价：只有新商品（已有商品的价格要改「已有」两列）
 * 粘贴与拖拽填充遇到不可编辑的行直接跳过，不算无效值。
 */
export function isContainerDetailColumnEditableForRow(
  columnKey: ContainerDetailColumnPasteKey,
  row: ContainerDetail,
): boolean {
  switch (columnKey) {
    case 'warehouseImportPrice':
    case 'lastOEMPrice':
      return isContainerDetailExistingPriceEditable(row)
    case 'importPrice':
    case 'oemPrice':
      return isContainerDetailThisPriceEditable(row)
    default:
      return true
  }
}

/** 取目标列当前显示值，用于跳过与现值相同的粘贴项，避免无意义的保存请求。 */
export function getContainerDetailColumnPasteCurrentValue(
  columnKey: ContainerDetailColumnPasteKey,
  row: ContainerDetail,
): string | number | undefined {
  switch (columnKey) {
    case 'englishName':
      return getContainerDetailEnglishName(row)?.trim() || undefined
    case 'warehouseImportPrice':
      return getContainerDetailRealtimeImportPrice(row)
    case 'lastOEMPrice':
      return getContainerDetailRealtimeRetailPrice(row)
    case 'oemPrice':
      return getContainerDetailVisibleOemPrice(row)
    case 'remark':
      return row.备注?.trim() || undefined
    default:
      return (row as unknown as Record<string, unknown>)[CONTAINER_DETAIL_COLUMN_PASTE_TARGETS[columnKey].field] as number | undefined
  }
}

export type ContainerDetailColumnPasteEntry = {
  row: ContainerDetail
  field: ContainerDetailColumnPasteField
  value: string | number
}

export type ContainerDetailColumnPastePlan = {
  entries: ContainerDetailColumnPasteEntry[]
  appliedCount: number
  unchangedCount: number
  /** 目标列在该行不可编辑（如已有商品的「本次价」、新商品的「已有价」）而跳过的行数。 */
  notEditableCount: number
  skippedBlankCount: number
  invalidCount: number
  /** 无效值在粘贴数据中的行号（从 1 开始），用于提示用户回查 Excel。 */
  invalidRowNumbers: number[]
  /** 粘贴数据多于起始行之后的可见行数时，超出的行数。 */
  overflowCount: number
  error?: 'missing_target'
}

/**
 * 按当前显示顺序，从 startRowKey 所在行向下为每个粘贴值生成行补丁。
 * - 空单元格保持原值（不清空），与 Excel 行号一一对应
 * - 无效值、无 hguid（尚未落库）的行跳过并计数
 * - 与当前显示值相同的项不生成补丁
 */
export function buildContainerDetailColumnPastePlan(params: {
  columnKey: ContainerDetailColumnPasteKey
  values: readonly string[]
  rows: readonly ContainerDetail[]
  startRowKey: string
  getRowKey: (row: ContainerDetail) => string
}): ContainerDetailColumnPastePlan {
  const { columnKey, values, rows, startRowKey, getRowKey } = params
  const plan: ContainerDetailColumnPastePlan = {
    entries: [],
    appliedCount: 0,
    unchangedCount: 0,
    notEditableCount: 0,
    skippedBlankCount: 0,
    invalidCount: 0,
    invalidRowNumbers: [],
    overflowCount: 0,
  }
  const startIndex = rows.findIndex((row) => getRowKey(row) === startRowKey)
  if (startIndex < 0) {
    return { ...plan, error: 'missing_target' }
  }
  const field = CONTAINER_DETAIL_COLUMN_PASTE_TARGETS[columnKey].field

  values.forEach((rawValue, offset) => {
    const row = rows[startIndex + offset]
    if (!row) {
      plan.overflowCount += 1
      return
    }
    if (!rawValue.trim()) {
      plan.skippedBlankCount += 1
      return
    }
    if (!isContainerDetailColumnEditableForRow(columnKey, row)) {
      plan.notEditableCount += 1
      return
    }
    const parsed = parseContainerDetailColumnPasteValue(columnKey, rawValue)
    if (!parsed.ok || !row.hguid) {
      plan.invalidCount += 1
      plan.invalidRowNumbers.push(offset + 1)
      return
    }
    if (getContainerDetailColumnPasteCurrentValue(columnKey, row) === parsed.value) {
      plan.unchangedCount += 1
      return
    }
    plan.entries.push({ row, field, value: parsed.value })
    plan.appliedCount += 1
  })

  return plan
}

/**
 * 拖拽填充（Excel 式填充柄）的目标范围：从源行出发向上或向下拖到 endRowKey，返回被覆盖的行，
 * 按「离源行由近到远」排序，不含源行本身；源行或终点行不在当前列表里返回空数组。
 */
export function resolveContainerDetailFillTargetRows(params: {
  rows: readonly ContainerDetail[]
  sourceRowKey: string
  endRowKey: string
  getRowKey: (row: ContainerDetail) => string
}): ContainerDetail[] {
  const { rows, sourceRowKey, endRowKey, getRowKey } = params
  const sourceIndex = rows.findIndex((row) => getRowKey(row) === sourceRowKey)
  const endIndex = rows.findIndex((row) => getRowKey(row) === endRowKey)
  if (sourceIndex < 0 || endIndex < 0 || sourceIndex === endIndex) return []
  return endIndex > sourceIndex
    ? rows.slice(sourceIndex + 1, endIndex + 1)
    : rows.slice(endIndex, sourceIndex).reverse()
}

/**
 * 把源单元格的值复制到目标行，复用列粘贴的解析 / 校验 / 同值跳过 / 不可编辑跳过规则。
 * 源值取源行目标列的当前显示值；源值为空时不生成任何补丁（空值不会清空目标格）。
 */
export function buildContainerDetailFillPlan(params: {
  columnKey: ContainerDetailColumnPasteKey
  sourceRow: ContainerDetail
  targetRows: readonly ContainerDetail[]
  getRowKey: (row: ContainerDetail) => string
}): ContainerDetailColumnPastePlan {
  const { columnKey, sourceRow, targetRows, getRowKey } = params
  const sourceValue = getContainerDetailColumnPasteCurrentValue(columnKey, sourceRow)
  const sourceText = sourceValue == null ? '' : String(sourceValue)
  if (!targetRows.length) {
    return {
      entries: [],
      appliedCount: 0,
      unchangedCount: 0,
      notEditableCount: 0,
      skippedBlankCount: 0,
      invalidCount: 0,
      invalidRowNumbers: [],
      overflowCount: 0,
    }
  }
  // 复用粘贴计划：目标行集合就是「按显示顺序的一段连续行」，起点取第一行，值重复到每一行。
  // 目标行可能是向上拖得到的倒序，这里按目标集合自身顺序构造，不依赖全表顺序。
  return buildContainerDetailColumnPastePlan({
    columnKey,
    values: targetRows.map(() => sourceText),
    rows: targetRows,
    startRowKey: getRowKey(targetRows[0]),
    getRowKey,
  })
}
