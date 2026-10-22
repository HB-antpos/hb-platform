// 供应商节日预订单管理端的纯逻辑：粘贴拆分、列映射、悉尼日期换算、截止剩余天数、矩阵件数/金额计算等。
// 不依赖 React / antd，便于用 node 直接跑单测。
import type {
  SupplierPreorderColumnMapping,
  SupplierPreorderFormStoreStatus,
  SupplierPreorderFormSummary,
  SupplierPreorderHolidayType,
  SupplierPreorderMatrixRow,
  SupplierPreorderMatrixTotals,
  SupplierPreorderPreviewItem,
} from '../../../types/supplierPreorder'
import { parsePasteCells } from '../../PosAdmin/LocalSupplierInvoices/InvoiceEdit/pasteDetails'

export const SYDNEY_TIME_ZONE = 'Australia/Sydney'
const DAY_MS = 24 * 60 * 60 * 1000

export const HOLIDAY_TYPES: SupplierPreorderHolidayType[] = ['Christmas', 'Halloween', 'Easter', 'Diaries', 'Custom']

// ---------- 粘贴与文件 ----------

/**
 * 把从 Excel 复制的整块内容（含表头行）拆成二维数组。
 * 复用发票粘贴的 parsePasteCells：它能正确处理 Excel 给多行单元格加的引号（"第一行\n第二行"）与 "" 转义。
 * 这里再把每行补齐到同一列数，保证列下标在各行一致（后端按下标映射列）。
 */
export function splitPastedSheet(text: string): string[][] {
  const rows = parsePasteCells(text).map((row) => row.map((cell) => cell.trim()))
  if (!rows.length) return []
  const width = Math.max(...rows.map((row) => row.length))
  return rows.map((row) => (row.length < width ? [...row, ...Array<string>(width - row.length).fill('')] : row))
}

export type ImportFileKind = 'excel' | 'xls' | 'unsupported'

/** .xls（老格式）后端 ClosedXML 解析不了，前端直接提示另存为 .xlsx。 */
export function getImportFileKind(fileName: string): ImportFileKind {
  const lower = fileName.trim().toLowerCase()
  if (lower.endsWith('.xlsx') || lower.endsWith('.xlsm')) return 'excel'
  if (lower.endsWith('.xls')) return 'xls'
  return 'unsupported'
}

// ---------- 列映射 ----------

export type MappingKey = keyof SupplierPreorderColumnMapping

export const REQUIRED_MAPPING_KEYS: MappingKey[] = ['itemCode', 'name', 'price', 'cartonQty']
export const OPTIONAL_MAPPING_KEYS: MappingKey[] = [
  'priceFallback',
  'cartonQtyFallback',
  'barcode',
  'unit',
  'cartonCbm',
  'image',
  'category',
  'prevYearItemCode',
  'notes',
  'seq',
  'orderQty',
]

export const EMPTY_MAPPING: SupplierPreorderColumnMapping = {
  itemCode: null,
  name: null,
  price: null,
  cartonQty: null,
  priceFallback: null,
  cartonQtyFallback: null,
  barcode: null,
  unit: null,
  cartonCbm: null,
  image: null,
  category: null,
  prevYearItemCode: null,
  notes: null,
  seq: null,
  orderQty: null,
}

/** 0 → A，25 → Z，26 → AA（粘贴导入没有列字母时按位置推算）。 */
export function getColumnLetter(index: number): string {
  if (!Number.isInteger(index) || index < 0) return ''
  let value = index + 1
  let letter = ''
  while (value > 0) {
    const remainder = (value - 1) % 26
    letter = String.fromCharCode(65 + remainder) + letter
    value = Math.floor((value - 1) / 26)
  }
  return letter
}

export function getMissingRequiredMappings(mapping: SupplierPreorderColumnMapping): MappingKey[] {
  return REQUIRED_MAPPING_KEYS.filter((key) => mapping[key] === null || mapping[key] === undefined)
}

/** 同一列被映射到多个字段时提示（不阻止，例如货号与去年货号偶尔确实是同一列）。 */
export function findDuplicateMappedColumns(mapping: SupplierPreorderColumnMapping): { column: number; keys: MappingKey[] }[] {
  const byColumn = new Map<number, MappingKey[]>()
  for (const key of [...REQUIRED_MAPPING_KEYS, ...OPTIONAL_MAPPING_KEYS]) {
    const column = mapping[key]
    if (column === null || column === undefined) continue
    byColumn.set(column, [...(byColumn.get(column) ?? []), key])
  }
  return [...byColumn.entries()]
    .filter(([, keys]) => keys.length > 1)
    .map(([column, keys]) => ({ column, keys }))
    .sort((a, b) => a.column - b.column)
}

export function setMappingColumn(
  mapping: SupplierPreorderColumnMapping,
  key: MappingKey,
  column: number | null | undefined,
): SupplierPreorderColumnMapping {
  return { ...mapping, [key]: column ?? null }
}

/** 把后端建议的映射补全成完整对象（缺的键按 null），避免后端少返回某个键时下拉显示异常。 */
export function normalizeMapping(mapping?: Partial<SupplierPreorderColumnMapping> | null): SupplierPreorderColumnMapping {
  const result = { ...EMPTY_MAPPING }
  if (!mapping) return result
  for (const key of Object.keys(EMPTY_MAPPING) as MappingKey[]) {
    const value = mapping[key]
    result[key] = typeof value === 'number' && Number.isInteger(value) && value >= 0 ? value : null
  }
  return result
}

// ---------- 校对预览 ----------

export type PreviewFilter = 'all' | 'matched' | 'new' | 'warning' | 'invalid' | 'priceChanged'

/** 进价变动：与商品表进价相差 0.5% 及以上，与后端 counts.priceChanged 口径一致。 */
export function isPriceChanged(item: Pick<SupplierPreorderPreviewItem, 'priceChangeRate'>): boolean {
  const rate = item.priceChangeRate
  return typeof rate === 'number' && Number.isFinite(rate) && Math.abs(rate) >= 0.005
}

export function filterPreviewItems(items: SupplierPreorderPreviewItem[], filter: PreviewFilter): SupplierPreorderPreviewItem[] {
  switch (filter) {
    case 'matched':
      return items.filter((item) => item.status === 'Matched')
    case 'new':
      return items.filter((item) => item.status === 'New')
    case 'warning':
      return items.filter((item) => item.status === 'Warning')
    case 'invalid':
      return items.filter((item) => item.status === 'Invalid')
    case 'priceChanged':
      return items.filter(isPriceChanged)
    default:
      return items
  }
}

/** 0.059 → "+5.9%"，-0.049 → "-4.9%" */
export function formatPriceChangeRate(rate: number): string {
  const percent = Math.round(rate * 1000) / 10
  return `${percent > 0 ? '+' : ''}${percent.toFixed(1)}%`
}

// ---------- 悉尼日期 ----------

const sydneyPartsFormatter = new Intl.DateTimeFormat('en-US', {
  timeZone: SYDNEY_TIME_ZONE,
  hourCycle: 'h23',
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
})

interface SydneyParts {
  year: number
  month: number
  day: number
  hour: number
  minute: number
  second: number
}

function getSydneyParts(ms: number): SydneyParts {
  const parts: Record<string, string> = {}
  for (const part of sydneyPartsFormatter.formatToParts(new Date(ms))) {
    if (part.type !== 'literal') parts[part.type] = part.value
  }
  return {
    year: Number(parts.year),
    month: Number(parts.month),
    day: Number(parts.day),
    // 个别运行时在 h23 下仍可能给出 "24"，按 0 处理
    hour: Number(parts.hour) % 24,
    minute: Number(parts.minute),
    second: Number(parts.second),
  }
}

/** 某个瞬间悉尼相对 UTC 的偏移（毫秒）：夏令时 +11h，标准时 +10h。 */
function getSydneyOffsetMs(ms: number): number {
  const p = getSydneyParts(ms)
  const wallAsUtc = Date.UTC(p.year, p.month - 1, p.day, p.hour, p.minute, p.second)
  return wallAsUtc - (ms - (((ms % 1000) + 1000) % 1000))
}

const dateOnlyPattern = /^(\d{4})-(\d{2})-(\d{2})/

/** 后端 DateTime（如 "2026-02-05T00:00:00"）或 "2026-02-05" → "2026-02-05"；无法识别返回 null。 */
export function normalizeDateOnly(value?: string | null): string | null {
  if (!value) return null
  const match = dateOnlyPattern.exec(value.trim())
  return match ? `${match[1]}-${match[2]}-${match[3]}` : null
}

/**
 * 悉尼某天 23:59:59 → UTC ISO。
 * 关键：先按「墙钟时间当 UTC」得到近似值，再用该瞬间的悉尼偏移修正两次，夏令时切换当天也准确。
 */
export function sydneyDateEndToUtcIso(date: string): string | null {
  const match = dateOnlyPattern.exec(date.trim())
  if (!match) return null
  const wallMs = Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3]), 23, 59, 59)
  let guess = wallMs - getSydneyOffsetMs(wallMs)
  guess = wallMs - getSydneyOffsetMs(guess)
  return new Date(guess).toISOString()
}

function pad2(value: number) {
  return String(value).padStart(2, '0')
}

/** UTC ISO → 悉尼日期 "YYYY-MM-DD"（编辑截止日期时回填 DatePicker）。 */
export function utcIsoToSydneyDate(iso?: string | null): string | null {
  if (!iso) return null
  const ms = Date.parse(iso)
  if (Number.isNaN(ms)) return null
  const p = getSydneyParts(ms)
  return `${p.year}-${pad2(p.month)}-${pad2(p.day)}`
}

/** UTC ISO → 悉尼 "YYYY-MM-DD HH:mm"；空值返回 "--"。 */
export function formatSydneyDateTime(iso?: string | null): string {
  if (!iso) return '--'
  const ms = Date.parse(iso)
  if (Number.isNaN(ms)) return iso
  const p = getSydneyParts(ms)
  return `${p.year}-${pad2(p.month)}-${pad2(p.day)} ${pad2(p.hour)}:${pad2(p.minute)}`
}

export type DeadlineState = 'none' | 'passed' | 'today' | 'days'

export interface DeadlineInfo {
  state: DeadlineState
  /** 按悉尼日历日计的剩余天数（今天截止 = 0） */
  daysLeft: number | null
  /** 剩 ≤3 天（含今天）琥珀色提醒 */
  urgent: boolean
}

/** 截止剩余天数：按悉尼日历日相减，避免「还剩 26 小时」显示成 2 天或 1 天的歧义。 */
export function getDeadlineInfo(deadlineAtUtc: string | null | undefined, nowMs: number): DeadlineInfo {
  if (!deadlineAtUtc) return { state: 'none', daysLeft: null, urgent: false }
  const deadlineMs = Date.parse(deadlineAtUtc)
  if (Number.isNaN(deadlineMs)) return { state: 'none', daysLeft: null, urgent: false }
  if (deadlineMs <= nowMs) return { state: 'passed', daysLeft: null, urgent: false }
  const d = getSydneyParts(deadlineMs)
  const n = getSydneyParts(nowMs)
  const daysLeft = Math.round((Date.UTC(d.year, d.month - 1, d.day) - Date.UTC(n.year, n.month - 1, n.day)) / DAY_MS)
  return { state: daysLeft <= 0 ? 'today' : 'days', daysLeft: Math.max(0, daysLeft), urgent: daysLeft <= 3 }
}

// ---------- 订单表信息默认值 ----------

const HOLIDAY_TITLE_NAMES: Record<SupplierPreorderHolidayType, string> = {
  Christmas: 'Christmas',
  Halloween: 'Halloween',
  Easter: 'Easter',
  Diaries: 'Diaries',
  Custom: '',
}

/** 默认标题：「供应商名 · 节日 年份」，与设计图一致用英文节日名（标题是数据，不随界面语言变）。 */
export function buildDefaultFormTitle(
  supplierName: string | null | undefined,
  holidayType: SupplierPreorderHolidayType,
  seasonYear: number | null | undefined,
): string {
  const season = [HOLIDAY_TITLE_NAMES[holidayType], seasonYear ? String(seasonYear) : ''].filter(Boolean).join(' ')
  return [supplierName?.trim(), season].filter(Boolean).join(' · ')
}

/**
 * 历史对比默认取去年同季：
 * - 圣诞：去年 9/1 ~ 今年 1/18（含节后清货）
 * - 万圣节：去年 8/1 ~ 去年 11/7
 * - 其他节日留空
 */
export function getDefaultHistoryRange(
  holidayType: SupplierPreorderHolidayType,
  seasonYear: number | null | undefined,
): { from: string; to: string } | null {
  if (!seasonYear || !Number.isInteger(seasonYear)) return null
  const lastYear = seasonYear - 1
  if (holidayType === 'Christmas') return { from: `${lastYear}-09-01`, to: `${seasonYear}-01-18` }
  if (holidayType === 'Halloween') return { from: `${lastYear}-08-01`, to: `${lastYear}-11-07` }
  return null
}

export type ContainerPreset = 'none' | '40GP' | '20GP' | 'custom'

export const CONTAINER_PRESET_CBM: Record<'40GP' | '20GP', number> = { '40GP': 67, '20GP': 33 }

/** 由柜型文本推断下拉预设（表头可能写成「40GP」「40 GP」「1 x 40GP」）。 */
export function resolveContainerPreset(containerType?: string | null): ContainerPreset {
  const normalized = (containerType ?? '').toUpperCase().replace(/\s+/g, '')
  if (!normalized) return 'none'
  if (normalized.includes('40GP')) return '40GP'
  if (normalized.includes('20GP')) return '20GP'
  return 'custom'
}

// ---------- 分店开启 ----------

/** 分店已有下单数据（草稿或已提交、或有箱数）：关闭前要二次确认。 */
export function storeHasOrderData(store: Pick<SupplierPreorderFormStoreStatus, 'orderStatus' | 'cartons' | 'itemCount'>): boolean {
  return store.orderStatus !== 'None' || store.cartons > 0 || store.itemCount > 0
}

/** 切换某家店后，整体提交给后端的开启分店列表（保持原顺序、去重）。 */
export function buildEnabledStoreGuids(
  stores: Pick<SupplierPreorderFormStoreStatus, 'storeGuid' | 'enabled'>[],
  storeGuid: string,
  enabled: boolean,
): string[] {
  const result: string[] = []
  for (const store of stores) {
    const on = store.storeGuid === storeGuid ? enabled : store.enabled
    if (on && !result.includes(store.storeGuid)) result.push(store.storeGuid)
  }
  if (enabled && !result.includes(storeGuid)) result.push(storeGuid)
  return result
}

export function getNotStartedStoreCount(summary: Pick<SupplierPreorderFormSummary, 'enabledStoreCount' | 'submittedStoreCount' | 'draftStoreCount'>): number {
  return Math.max(0, summary.enabledStoreCount - summary.submittedStoreCount - summary.draftStoreCount)
}

/** 前端预判能否删除（后端仍会校验）：没有任何分店下过单。 */
export function canDeleteForm(summary: Pick<SupplierPreorderFormSummary, 'submittedStoreCount' | 'draftStoreCount' | 'totalCartons'>): boolean {
  return summary.submittedStoreCount === 0 && summary.draftStoreCount === 0 && summary.totalCartons === 0
}

// ---------- 下单矩阵 ----------

export type MatrixUnit = 'cartons' | 'pieces' | 'amount'

export function roundMoney(value: number): number {
  return Math.round((value + Number.EPSILON) * 100) / 100
}

/** 单元格数值：件 = 箱 × 箱规；金额 = 件 × 进价（按行实时计算）。 */
export function getMatrixCellValue(
  row: Pick<SupplierPreorderMatrixRow, 'storeCartons' | 'cartonQty' | 'price'>,
  storeGuid: string,
  unit: MatrixUnit,
): number {
  const cartons = row.storeCartons[storeGuid] ?? 0
  if (unit === 'pieces') return cartons * row.cartonQty
  if (unit === 'amount') return roundMoney(cartons * row.cartonQty * row.price)
  return cartons
}

/** 合计行的分店值直接取后端 totals（覆盖全部页，不是当前页之和）。 */
export function getMatrixStoreTotal(totals: SupplierPreorderMatrixTotals, storeGuid: string, unit: MatrixUnit): number {
  if (unit === 'pieces') return totals.storePieces[storeGuid] ?? 0
  if (unit === 'amount') return totals.storeAmount[storeGuid] ?? 0
  return totals.storeCartons[storeGuid] ?? 0
}

// ---------- 格式化 ----------

const moneyFormatter = new Intl.NumberFormat('en-AU', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
const integerFormatter = new Intl.NumberFormat('en-AU', { maximumFractionDigits: 0 })

export function formatMoney(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '--'
  const text = moneyFormatter.format(Math.abs(value))
  return value < 0 ? `-$${text}` : `$${text}`
}

export function formatInteger(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '--'
  return integerFormatter.format(value)
}

/** m³：最多 3 位小数，去掉末尾 0（0.052 / 9.4 / 41.8） */
export function formatCbm(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '--'
  return String(Number(value.toFixed(3)))
}

export function formatMatrixValue(value: number, unit: MatrixUnit): string {
  if (unit === 'amount') return formatMoney(value)
  return formatInteger(value)
}

// ---------- 请求 ----------

export function isAbortError(error: unknown): boolean {
  return typeof error === 'object' && error !== null && (error as { name?: string }).name === 'AbortError'
}

/** 后端业务错误的中文原因（RequestError.message），没有时用兜底文案。 */
export function getErrorText(error: unknown, fallback: string): string {
  if (error instanceof Error && error.message.trim()) return error.message
  return fallback
}

export const PAGE_SIZE_OPTIONS = ['50', '100', '200', '500']
export const DEFAULT_PAGE_SIZE = 50
