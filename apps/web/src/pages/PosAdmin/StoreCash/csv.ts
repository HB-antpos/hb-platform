import type {
  CashDaily,
  CashDepositListItem,
  CashExpenseListItem,
  CashOverview,
} from '../../../types/storeCash'
import {
  categoryAmount,
  categoryLabelKey,
  dailyInflow,
  formatStoreDateTime,
  recordStatusKey,
  reviewLabelKey,
  sortDailyRows,
  toCents,
} from './logic'

/**
 * 现金管理 CSV：纯前端生成，不加依赖。
 * - UTF-8 带 BOM（Excel 直接打开不乱码），CRLF 换行；
 * - 字段按 RFC 4180 转义：含逗号、双引号、回车或换行时整体加双引号，内部双引号翻倍；
 * - 防公式注入只作用于文本列：以 = + - @ 制表符 回车 开头的文本前补一个单引号；
 *   金额、计数等数值列直接输出数值字符串（负数 -12.50 不会被补单引号），数值列先校验格式，
 *   不是纯数值的内容退回文本处理，保证不会有未防护的公式混进来；
 * - 空值输出空单元格，不写 0；存单 / 收据图片不进 CSV，只导出张数。
 */

export const CSV_BOM = '\uFEFF'

export type CsvCell =
  | { readonly kind: 'text'; readonly value: string }
  | { readonly kind: 'number'; readonly value: string }

export type CsvText = (key: string, params?: Record<string, string | number>) => string

const numericPattern = /^-?\d+(?:\.\d+)?$/

export function csvText(value: string | null | undefined): CsvCell {
  return { kind: 'text', value: value ?? '' }
}

/** 金额：两位小数、不带千分位与货币符号；null 留空。 */
export function csvMoney(value: number | null | undefined): CsvCell {
  const cents = toCents(value)
  if (cents === null) return { kind: 'number', value: '' }
  const abs = Math.abs(cents)
  return { kind: 'number', value: `${cents < 0 ? '-' : ''}${Math.floor(abs / 100)}.${String(abs % 100).padStart(2, '0')}` }
}

/** 整数计数；null / 非有限数留空。 */
export function csvInt(value: number | null | undefined): CsvCell {
  return { kind: 'number', value: typeof value === 'number' && Number.isFinite(value) ? String(Math.trunc(value)) : '' }
}

/** 文本以 = + - @ 制表符 回车 开头时前补单引号，防止 Excel 当公式执行。 */
export function guardCsvFormula(value: string): string {
  return /^[=+\-@\t\r]/.test(value) ? `'${value}` : value
}

/** RFC 4180：含逗号、双引号、回车或换行时加双引号并把内部双引号翻倍。 */
export function escapeCsvField(value: string): string {
  return /[",\r\n]/.test(value) ? `"${value.replace(/"/g, '""')}"` : value
}

export function encodeCsvCell(cell: CsvCell): string {
  if (cell.kind === 'number' && (cell.value === '' || numericPattern.test(cell.value))) return cell.value
  return escapeCsvField(guardCsvFormula(cell.value))
}

/** 表头也是文本列，同样做防注入与转义。 */
export function buildCsv(header: readonly string[], rows: readonly (readonly CsvCell[])[]): string {
  const lines = [header.map((title) => encodeCsvCell(csvText(title))).join(',')]
  for (const row of rows) lines.push(row.map(encodeCsvCell).join(','))
  return `${CSV_BOM}${lines.join('\r\n')}\r\n`
}

function yesNo(value: boolean, tx: CsvText): CsvCell {
  return csvText(value ? tx('storeCash.csv.yes') : tx('storeCash.csv.no'))
}

function categoryText(category: string, tx: CsvText): string {
  const key = categoryLabelKey(category)
  return key ? tx(key) : category
}

// ---------------------------------------------------------------------------
// 总览：每店一行 + 合计行
// ---------------------------------------------------------------------------

export function buildOverviewCsv(overview: CashOverview, tx: CsvText, t2VisibleDays: number): { content: string; rowCount: number } {
  // 日结未接入时，未存天数、逾期、日结天数都无从判断（后端给 0），与界面一致留空，不写 0 / 否。
  const connected = overview.dailyCloseConnected
  const ifConnected = (cell: CsvCell): CsvCell => (connected ? cell : csvText(''))
  const header = [
    tx('storeCash.csv.storeCode'),
    tx('storeCash.csv.storeName'),
    tx('storeCash.csv.poolBalance'),
    tx('storeCash.csv.openingMissing'),
    tx('storeCash.csv.uncoveredDays'),
    tx('storeCash.csv.oldestUncoveredDate'),
    tx('storeCash.csv.depositOverdue'),
    tx('storeCash.csv.inflowCash'),
    tx('storeCash.csv.closeVariance'),
    tx('storeCash.csv.closeDays'),
    tx('storeCash.csv.missingCloseDays'),
    tx('storeCash.csv.depositCount'),
    tx('storeCash.csv.depositTotal'),
    tx('storeCash.csv.expenseTotal'),
    tx('storeCash.category.Salary'),
    tx('storeCash.category.Purchase'),
    // T2 受可见窗口限制时直接写在表头，文件离开页面后口径仍然清楚。
    overview.t2Restricted ? tx('storeCash.csv.t2Restricted', { days: t2VisibleDays }) : tx('storeCash.category.T2'),
    tx('storeCash.category.Other'),
    tx('storeCash.csv.flaggedCount'),
    tx('storeCash.csv.lastDepositDate'),
  ]
  const rows: CsvCell[][] = overview.rows.map((row) => [
    csvText(row.storeCode),
    csvText(row.storeName),
    csvMoney(row.poolBalance),
    yesNo(row.openingMissing, tx),
    ifConnected(csvInt(row.uncoveredDayCount)),
    csvText(row.oldestUncoveredDate),
    ifConnected(yesNo(row.depositOverdue, tx)),
    csvMoney(row.inflowCash),
    csvMoney(row.closeVariance),
    ifConnected(csvInt(row.closeDayCount)),
    ifConnected(csvInt(row.missingCloseDayCount)),
    csvInt(row.depositCount),
    csvMoney(row.depositTotal),
    csvMoney(row.expenseTotal),
    csvMoney(categoryAmount(row.expenseByCategory, 'Salary')),
    csvMoney(categoryAmount(row.expenseByCategory, 'Purchase')),
    csvMoney(categoryAmount(row.expenseByCategory, 'T2')),
    csvMoney(categoryAmount(row.expenseByCategory, 'Other')),
    csvInt(row.flaggedExpenseCount),
    csvText(row.lastDepositDate),
  ])
  const totals = overview.totals
  if (overview.rows.length > 0) {
    // 合计行只写后端给出的合计；任一分店不可算时合计也是 null，留空。逾期列写逾期分店数。
    rows.push([
      csvText(tx('storeCash.csv.totalRow')),
      csvText(''),
      csvMoney(totals.poolBalance),
      csvText(''),
      ifConnected(csvInt(totals.uncoveredDayCount)),
      csvText(''),
      ifConnected(csvInt(totals.overdueStoreCount)),
      csvMoney(totals.inflowCash),
      csvMoney(totals.closeVariance),
      csvText(''),
      csvText(''),
      csvInt(totals.depositCount),
      csvMoney(totals.depositTotal),
      csvMoney(totals.expenseTotal),
      csvMoney(categoryAmount(totals.expenseByCategory, 'Salary')),
      csvMoney(categoryAmount(totals.expenseByCategory, 'Purchase')),
      csvMoney(categoryAmount(totals.expenseByCategory, 'T2')),
      csvMoney(categoryAmount(totals.expenseByCategory, 'Other')),
      csvInt(totals.flaggedExpenseCount),
      csvText(''),
    ])
  }
  return { content: buildCsv(header, rows), rowCount: overview.rows.length }
}

// ---------------------------------------------------------------------------
// 按日明细：每天一行
// ---------------------------------------------------------------------------

export function buildDailyCsv(daily: CashDaily, tx: CsvText): { content: string; rowCount: number } {
  const connected = daily.dailyCloseConnected
  const header = [
    tx('storeCash.csv.storeCode'),
    tx('storeCash.csv.businessDate'),
    tx('storeCash.csv.inflowCash'),
    tx('storeCash.csv.hasClose'),
    tx('storeCash.csv.covered'),
    tx('storeCash.csv.dayExpense'),
    tx('storeCash.csv.deviceCount'),
    tx('storeCash.csv.manualDevices'),
    tx('storeCash.csv.staleDevices'),
  ]
  const rows: CsvCell[][] = sortDailyRows(daily.rows).map((row) => [
    csvText(daily.storeCode),
    csvText(row.businessDate),
    csvMoney(dailyInflow(row, connected)),
    // 日结未接入时「有没有日结」「是否已存」都无从判断，留空而不是写「否」。
    connected ? yesNo(row.hasClose, tx) : csvText(''),
    connected && row.hasClose ? yesNo(row.covered, tx) : csvText(''),
    csvMoney(row.expenseTotal),
    csvInt(row.devices.length),
    csvInt(row.devices.filter((device) => device.selectionMode === 'Manual').length),
    csvInt(row.devices.filter((device) => device.selectionStale).length),
  ])
  return { content: buildCsv(header, rows), rowCount: rows.length }
}

// ---------------------------------------------------------------------------
// 存款：每张存单一行（银行流水是一张存单对一笔入账，按存单粒度才能对账）；
// 同一次存款的公共信息在每行重复，存单图片只导出张数。
// ---------------------------------------------------------------------------

export function buildDepositsCsv(
  items: readonly CashDepositListItem[],
  tx: CsvText,
  timeZone: string,
): { content: string; rowCount: number } {
  const header = [
    tx('storeCash.csv.storeCode'),
    tx('storeCash.csv.depositDate'),
    tx('storeCash.csv.coveredFrom'),
    tx('storeCash.csv.coveredTo'),
    tx('storeCash.csv.depositAmount'),
    tx('storeCash.csv.slipCount'),
    tx('storeCash.csv.slipIndex'),
    tx('storeCash.csv.slipAmount'),
    tx('storeCash.csv.slipNo'),
    tx('storeCash.csv.slipImageCount'),
    tx('storeCash.csv.createdBy'),
    tx('storeCash.csv.createdAt'),
    tx('storeCash.csv.status'),
    tx('storeCash.csv.note'),
  ]
  const rows: CsvCell[][] = items.flatMap((item) => {
    const common = {
      head: [
        csvText(item.storeCode),
        csvText(item.depositDate),
        csvText(item.coveredFromDate),
        csvText(item.coveredToDate),
        csvMoney(item.totalAmount),
        csvInt(item.slipCount),
      ],
      tail: [
        csvText(item.createdByName),
        csvText(item.createdAtUtc ? formatStoreDateTime(item.createdAtUtc, timeZone) : ''),
        csvText(tx(recordStatusKey(item))),
        csvText(item.note),
      ],
    }
    const slips = item.slipSummaries ?? []
    if (slips.length === 0) {
      // 旧版后端没有存单摘要：仍输出一行，存单列留空，不编造金额。
      return [[...common.head, csvText(''), csvText(''), csvText(''), csvText(''), ...common.tail]]
    }
    return slips.map((slip, index) => [
      ...common.head,
      csvInt(index + 1),
      csvMoney(slip.amount),
      csvText(slip.slipNo),
      csvInt(slip.imageCount),
      ...common.tail,
    ])
  })
  return { content: buildCsv(header, rows), rowCount: rows.length }
}

// ---------------------------------------------------------------------------
// 支出：每笔一行
// ---------------------------------------------------------------------------

export function buildExpensesCsv(
  items: readonly CashExpenseListItem[],
  tx: CsvText,
  timeZone: string,
): { content: string; rowCount: number } {
  const header = [
    tx('storeCash.csv.storeCode'),
    tx('storeCash.csv.expenseDate'),
    tx('storeCash.csv.category'),
    tx('storeCash.csv.amount'),
    tx('storeCash.csv.payee'),
    tx('storeCash.csv.note'),
    tx('storeCash.csv.reviewStatus'),
    tx('storeCash.csv.reviewNote'),
    tx('storeCash.csv.reviewedBy'),
    tx('storeCash.csv.reviewedAt'),
    tx('storeCash.csv.createdBy'),
    tx('storeCash.csv.createdAt'),
    tx('storeCash.csv.status'),
    tx('storeCash.csv.imageCount'),
  ]
  const rows: CsvCell[][] = items.map((item) => [
    csvText(item.storeCode),
    csvText(item.expenseDate),
    csvText(categoryText(item.category, tx)),
    csvMoney(item.amount),
    csvText(item.payeeName),
    csvText(item.note),
    csvText(tx(reviewLabelKey(item.reviewStatus))),
    csvText(item.reviewNote),
    csvText(item.reviewedByName),
    csvText(item.reviewedAtUtc ? formatStoreDateTime(item.reviewedAtUtc, timeZone) : ''),
    csvText(item.createdByName),
    csvText(item.createdAtUtc ? formatStoreDateTime(item.createdAtUtc, timeZone) : ''),
    csvText(tx(recordStatusKey(item))),
    csvInt(item.imageCount),
  ])
  return { content: buildCsv(header, rows), rowCount: rows.length }
}

// ---------------------------------------------------------------------------
// 下载（浏览器副作用，测试不调用）
// ---------------------------------------------------------------------------

export function downloadCsvFile(content: string, fileName: string): void {
  const blob = new Blob([content], { type: 'text/csv;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  document.body.appendChild(link)
  link.click()
  link.remove()
  setTimeout(() => URL.revokeObjectURL(url), 60_000)
}
