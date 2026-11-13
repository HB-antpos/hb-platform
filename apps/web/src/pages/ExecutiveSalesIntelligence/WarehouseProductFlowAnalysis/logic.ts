import type { WarehouseCategoryNode } from '../../../services/warehouseCategoryService'
import type { WarehouseProductFlowBranch, WarehouseProductFlowFilter, WarehouseProductFlowItemNumberMatchMode, WarehouseProductFlowPeriods, WarehouseProductFlowSupplierOption } from '../../../types/warehouseProductFlowAnalysis'

export type WarehouseProductFlowPosFilter = 'all' | 'enabled' | 'disabled'

export function filterWarehouseProductFlowShipments<T extends { posEnabled: boolean | null }>(rows: readonly T[], posFilter: WarehouseProductFlowPosFilter): T[] {
  // 未关联分店的状态未知，只在“全部”中显示，不能归为未启用 POS。
  return rows.filter((row) => posFilter === 'all' || row.posEnabled === (posFilter === 'enabled'))
}

export function sortWarehouseProductFlowBranches(branches: readonly WarehouseProductFlowBranch[]): WarehouseProductFlowBranch[] {
  return [...branches].sort((left, right) => right.netSalesQuantity - left.netSalesQuantity || left.branchCode.localeCompare(right.branchCode))
}

export interface WarehouseProductFlowDateRange {
  startDate: string
  endDate: string
}

export interface WarehouseProductFlowSelectOption {
  label: string
  value: string
  searchTerms: string[]
}

function normalizeSearchText(value: string | undefined): string {
  return value?.trim().replace(/\s+/g, ' ').toLocaleLowerCase() ?? ''
}

function matchesSearch(searchTerms: readonly string[], input: string): boolean {
  const query = normalizeSearchText(input)
  return !query || searchTerms.some((term) => normalizeSearchText(term).includes(query))
}

const brisbaneDateFormatter = new Intl.DateTimeFormat('en-CA', {
  timeZone: 'Australia/Brisbane', year: 'numeric', month: '2-digit', day: '2-digit',
})

function formatBrisbaneDate(now: Date): string {
  const parts = brisbaneDateFormatter.formatToParts(now).reduce<Record<string, string>>((result, part) => {
    if (part.type !== 'literal') result[part.type] = part.value
    return result
  }, {})
  return `${parts.year}-${parts.month}-${parts.day}`
}

function shiftDate(date: string, days: number): string {
  const result = new Date(`${date}T00:00:00Z`)
  result.setUTCDate(result.getUTCDate() + days)
  return result.toISOString().slice(0, 10)
}

function subtractCalendarMonths(date: string, months: number): string {
  const source = new Date(`${date}T00:00:00Z`)
  const targetMonth = source.getUTCMonth() - months
  const targetYear = source.getUTCFullYear() + Math.floor(targetMonth / 12)
  const normalizedMonth = ((targetMonth % 12) + 12) % 12
  const lastDay = new Date(Date.UTC(targetYear, normalizedMonth + 1, 0)).getUTCDate()
  const day = Math.min(source.getUTCDate(), lastDay)
  return new Date(Date.UTC(targetYear, normalizedMonth, day)).toISOString().slice(0, 10)
}

function naturalMonthRange(months: number, now: Date): WarehouseProductFlowDateRange {
  const endDate = shiftDate(formatBrisbaneDate(now), -1)
  // 完整自然月滚动口径：结束日次日向前减 N 个月，首尾均包含。
  const startDate = subtractCalendarMonths(shiftDate(endDate, 1), months)
  return { startDate, endDate }
}

export function buildWarehouseProductFlowDefaultPeriods(now = new Date()): WarehouseProductFlowPeriods {
  return {
    containerPeriod: naturalMonthRange(12, now),
    orderShipmentPeriod: naturalMonthRange(6, now),
    salesPeriod: naturalMonthRange(6, now),
  }
}

export function isValidWarehouseProductFlowRange(range: readonly Date[], now = new Date()): boolean {
  const [start, end] = range
  const yesterday = shiftDate(formatBrisbaneDate(now), -1)
  if (!start || !end) return false
  const startDate = formatBrisbaneDate(start)
  const endDate = formatBrisbaneDate(end)
  if (startDate > endDate || endDate > yesterday) return false
  const dayCount = Math.round((Date.parse(`${endDate}T00:00:00Z`) - Date.parse(`${startDate}T00:00:00Z`)) / 86_400_000) + 1
  return dayCount <= 366
}

export function createWarehouseProductFlowFilter(
  keyword: string,
  warehouseCategoryGuids: string[],
  supplierCodes: string[],
  documentKeyword: string,
  itemNumbers: readonly string[] = [],
  itemNumberMatchMode: WarehouseProductFlowItemNumberMatchMode = 'itemNumber',
): WarehouseProductFlowFilter {
  return {
    keyword: keyword.trim() || undefined,
    warehouseCategoryGuids,
    supplierCodes,
    documentKeyword: documentKeyword.trim() || undefined,
    // 没有批量货号时不带这两个字段，保持原有请求与缓存键不变。
    ...(itemNumbers.length ? { itemNumbers: [...itemNumbers], itemNumberMatchMode } : {}),
  }
}

/** 批量货号上限，与后端 MaxItemNumberFilterCount 一致。 */
export const WAREHOUSE_PRODUCT_FLOW_MAX_ITEM_NUMBERS = 2000

export interface ParsedPastedItemNumbers {
  /** 去重后的货号，保留粘贴顺序与原始大小写。 */
  itemNumbers: string[]
  /** 归一化货号（大写）→ 首次出现的粘贴行号（1 起，含表头行；从 A1 复制时等于 Excel 行号）。 */
  lineByItemNumber: Record<string, number>
  /** 粘贴的行数（去掉末尾空行，Excel 复制通常多带一个换行）。 */
  lineCount: number
  /** 被丢弃的条数：空行、表头、重复货号。 */
  skippedCount: number
}

const ITEM_NUMBER_SEPARATOR = /[\t,，;；\s]+/
const ITEM_NUMBER_HEADER_WORDS = new Set(['item', 'items', 'no', 'no.', 'number', '#', 'sku', 'code', 'itemno', 'item#', 'barcode', 'product', 'productcode'])

export function normalizeItemNumberKey(value: string): string {
  return value.trim().toUpperCase()
}

function isItemNumberHeaderToken(token: string): boolean {
  // 中文表头（货号、商品编码…）或常见英文表头才算表头；真实货号不会含中文。
  return /[\u3400-\u9fff]/.test(token) || ITEM_NUMBER_HEADER_WORDS.has(token.toLowerCase())
}

/**
 * 解析从 Excel 粘贴的货号：按换行 / Tab / 逗号 / 分号 / 空格拆分，
 * 去掉 Excel 文本单元格两侧引号、空值和重复（不区分大小写），首行全是表头词时整行跳过。
 */
export function parsePastedItemNumbers(text: string): ParsedPastedItemNumbers {
  const lines = text.replace(/\r\n?/g, '\n').split('\n')
  while (lines.length && !lines[lines.length - 1].trim()) lines.pop()
  const itemNumbers: string[] = []
  const lineByItemNumber: Record<string, number> = {}
  let skippedCount = 0
  lines.forEach((line, index) => {
    const tokens = line.split(ITEM_NUMBER_SEPARATOR).map((token) => token.replace(/^"+|"+$/g, '').trim()).filter(Boolean)
    if (!tokens.length) { skippedCount += 1; return }
    if (index === 0 && tokens.every(isItemNumberHeaderToken)) { skippedCount += 1; return }
    for (const token of tokens) {
      const key = normalizeItemNumberKey(token)
      if (key in lineByItemNumber) { skippedCount += 1; continue }
      lineByItemNumber[key] = index + 1
      itemNumbers.push(token)
    }
  })
  return { itemNumbers, lineByItemNumber, lineCount: lines.length, skippedCount }
}

/** 候选商品命中的粘贴行号（取最靠前的），用于卡片上的「粘贴第 N 行」。 */
export function findItemNumberPasteLine(
  product: { productCode: string; itemNumber?: string; barcode?: string },
  lineByItemNumber: Readonly<Record<string, number>>,
  matchMode: WarehouseProductFlowItemNumberMatchMode,
): number | undefined {
  const values = matchMode === 'any' ? [product.itemNumber, product.barcode, product.productCode] : [product.itemNumber]
  const lines = values.filter((value): value is string => !!value?.trim()).map((value) => lineByItemNumber[normalizeItemNumberKey(value)]).filter((line): line is number => typeof line === 'number')
  return lines.length ? Math.min(...lines) : undefined
}

export function buildWarehouseProductFlowCategoryOptions(nodes: readonly WarehouseCategoryNode[]): WarehouseProductFlowSelectOption[] {
  const visit = (items: readonly WarehouseCategoryNode[], ancestors: readonly WarehouseCategoryNode[], depth: number): WarehouseProductFlowSelectOption[] => items.flatMap((node) => {
    const displayName = node.chineseName || node.categoryName
    const chinesePath = [...ancestors, node].map((item) => item.chineseName || item.categoryName).join(' ')
    const englishPath = [...ancestors, node].map((item) => item.categoryName).join(' ')
    const option: WarehouseProductFlowSelectOption = {
      label: `${'— '.repeat(depth)}${displayName}`,
      value: node.categoryGUID,
      // 搜索保留中英文、完整祖先路径和 GUID，使匹配父分类时子分类也可见。
      searchTerms: [node.categoryGUID, node.chineseName || '', node.categoryName, chinesePath, englishPath],
    }
    return [option, ...visit(node.children || [], [...ancestors, node], depth + 1)]
  })
  return visit(nodes, [], 0)
}

export function filterWarehouseProductFlowCategoryOptions(options: readonly WarehouseProductFlowSelectOption[], input: string): WarehouseProductFlowSelectOption[] {
  return options.filter((option) => matchesSearch(option.searchTerms, input))
}

function supplierMatchRank(supplier: WarehouseProductFlowSupplierOption, input: string): number | null {
  const query = normalizeSearchText(input)
  if (!query) return 0
  const terms = [supplier.code, supplier.name].map(normalizeSearchText).filter(Boolean)
  if (terms.some((term) => term === query)) return 0
  if (terms.some((term) => term.startsWith(query))) return 1
  return terms.some((term) => term.includes(query)) ? 2 : null
}

export function filterWarehouseProductFlowSupplierOptions(suppliers: readonly WarehouseProductFlowSupplierOption[], input: string): WarehouseProductFlowSelectOption[] {
  return suppliers.flatMap((supplier) => {
    const rank = supplierMatchRank(supplier, input)
    return rank === null ? [] : [{ supplier, rank }]
  }).sort((left, right) => left.rank - right.rank || left.supplier.code.localeCompare(right.supplier.code)).map(({ supplier }) => ({
    label: supplier.name ? `${supplier.code} · ${supplier.name}` : supplier.code,
    value: supplier.code,
    searchTerms: [supplier.code, supplier.name || ''],
  }))
}
