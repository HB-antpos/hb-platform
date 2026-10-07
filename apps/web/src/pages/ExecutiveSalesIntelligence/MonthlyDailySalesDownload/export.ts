import type ExcelJS from 'exceljs'
import {
  addTotals,
  buildCsv,
  buildFileName,
  buildSheetNames,
  compareBranchCode,
  displayTotals,
  isWeekend,
  listDates,
  ZERO_TOTALS,
  type Coverage,
  type DayRow,
  type StoreSummary,
} from './logic'

/**
 * 月度日销售导出：xlsx（汇总 + 每店一页）与 CSV 长表。
 *
 * - 所有文案经 text() 取得，页面传入 i18n 翻译函数，单测传入简易替身，本文件不依赖 i18n 与 DOM（下载动作可注入）。
 * - 缺数日、无支付方式拆分日在文件里一律留空，不写 0；备注列与说明行明确标注。
 * - xlsx 按分店逐个组装并回调进度；某一家失败时返回「中断」结果与检查点，已完成的工作表不会丢，
 *   可「重试未完成的」或「只下载已完成的」。
 */

export type TextFn = (key: string, params?: Record<string, string | number>) => string

export interface ExportContext extends Coverage {
  /** 已本地化的月份文字，如「2026 年 9 月」 */
  monthLabel: string
}

export interface ExportRequest {
  /** 已勾选的分店 */
  stores: readonly StoreSummary[]
  context: ExportContext
  text: TextFn
  signal?: AbortSignal
}

export interface ExportProgress {
  stage: 'store' | 'finalize'
  /** 正在生成第几家（从 1 开始） */
  index: number
  total: number
  storeName: string
  /** 0–1 */
  percent: number
}

export interface ExportStoreRef {
  branchCode: string
  branchName: string
}

/** 中断后保留的现场：已完成分店的工作表都还在 workbook 里。 */
export interface ExportCheckpoint {
  workbook: ExcelJS.Workbook
  /** 全部已勾选分店，按编码排序 */
  stores: StoreSummary[]
  sheetNames: Map<string, string>
  /** 已完成的分店编码，按完成顺序 */
  completed: string[]
  context: ExportContext
  text: TextFn
  fileName: string
}

export type ExportOutcome =
  | { status: 'completed'; fileName: string; storeCount: number; sheetCount: number }
  | { status: 'cancelled' }
  | {
    status: 'interrupted'
    checkpoint: ExportCheckpoint
    /** store：某家分店组装失败；finalize：全部分店完成，但生成 / 下载文件失败 */
    stage: 'store' | 'finalize'
    failedStore: ExportStoreRef | null
    message: string
    done: number
    total: number
    pending: number
  }

export interface ExportDeps {
  /** 取得 ExcelJS；默认动态 import，首屏与未导出的页面都不加载它。 */
  loadExcel: () => Promise<typeof ExcelJS>
  /** 组装一家分店的工作表；失败请抛错，默认实现保证失败时不留下半张表。 */
  addStoreSheet: (workbook: ExcelJS.Workbook, store: StoreSummary, sheetName: string, context: ExportContext, text: TextFn) => Promise<void> | void
  /** 把文件交给浏览器下载 */
  download: (blob: Blob, fileName: string) => void
  /** 让出事件循环，进度条才能绘制、取消才能生效 */
  nextTick: () => Promise<void>
}

const XLSX_MIME = 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'
const CSV_MIME = 'text/csv;charset=utf-8'

const COLORS = {
  ink: 'FF172033',
  muted: 'FF667085',
  headerFill: 'FFEEF3FB',
  accent: 'FF1677FF',
  totalFill: 'FFF8FAFC',
  totalBorder: 'FFAEB8C8',
  rowBorder: 'FFE3E8EF',
  // 设计稿的网页端周末底色 #fafbfd 在 Excel 里几乎看不出，这里取稍深一点的淡灰。
  weekendFill: 'FFF3F5F8',
  warning: 'FFAD4E00',
}
const MONEY_FORMAT = '#,##0.00'
const DATE_FORMAT = 'yyyy-mm-dd'

const WEEKDAY_KEYS = [
  'monthlyDailySalesDownload.weekday.sun',
  'monthlyDailySalesDownload.weekday.mon',
  'monthlyDailySalesDownload.weekday.tue',
  'monthlyDailySalesDownload.weekday.wed',
  'monthlyDailySalesDownload.weekday.thu',
  'monthlyDailySalesDownload.weekday.fri',
  'monthlyDailySalesDownload.weekday.sat',
] as const

export function weekdayKey(weekday: number): string {
  return WEEKDAY_KEYS[weekday] ?? WEEKDAY_KEYS[0]
}

function defaultDownload(blob: Blob, fileName: string) {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  document.body.appendChild(link)
  link.click()
  link.remove()
  setTimeout(() => URL.revokeObjectURL(url), 60_000)
}

const defaultDeps: ExportDeps = {
  loadExcel: async () => (await import('exceljs')).default,
  addStoreSheet: addStoreSheetToWorkbook,
  download: defaultDownload,
  nextTick: () => new Promise<void>(resolve => { setTimeout(resolve, 0) }),
}

function resolveDeps(deps?: Partial<ExportDeps>): ExportDeps {
  return { ...defaultDeps, ...deps }
}

function errorMessage(error: unknown): string {
  if (error instanceof Error && error.message) return error.message
  return typeof error === 'string' && error ? error : 'Unknown error'
}

function isAbortError(error: unknown): boolean {
  return typeof error === 'object' && error !== null && (error as { name?: string }).name === 'AbortError'
}

// ---------------------------------------------------------------------------
// 工作表内容
// ---------------------------------------------------------------------------

function moneyValue(cents: number | null): number | null {
  return cents === null ? null : cents / 100
}

/** 备注里的日期用 MM-DD，最多列 6 个，其余用省略号。 */
function formatShortDates(dates: readonly string[], text: TextFn): string {
  const { shown, hidden } = listDates(dates)
  return `${shown.map(date => date.slice(5)).join(text('monthlyDailySalesDownload.listSeparator'))}${hidden > 0 ? '…' : ''}`
}

/** 汇总页「备注」：缺数与无支付方式拆分的日期。 */
function summaryRemark(store: StoreSummary, text: TextFn): string {
  const parts: string[] = []
  if (store.missingDates.length > 0) {
    parts.push(text('monthlyDailySalesDownload.export.noteMissing', {
      count: store.missingDates.length,
      dates: formatShortDates(store.missingDates, text),
    }))
  }
  if (store.noSplitDates.length > 0) {
    parts.push(text('monthlyDailySalesDownload.export.noteNoSplit', {
      count: store.noSplitDates.length,
      dates: formatShortDates(store.noSplitDates, text),
    }))
  }
  return parts.join(text('monthlyDailySalesDownload.export.noteSeparator'))
}

function dayRemark(row: DayRow, text: TextFn): string {
  if (row.status === 'missing') return text('monthlyDailySalesDownload.export.dayMissing')
  if (row.status === 'noSplit') return text('monthlyDailySalesDownload.export.dayNoSplit')
  return ''
}

/** 文件里的说明行：缺数、无拆分、本月进行中各一行，只写实际出现的情况。 */
function footnotes(stores: readonly StoreSummary[], context: ExportContext, text: TextFn): string[] {
  const notes: string[] = []
  if (stores.some(store => store.missingDates.length > 0)) notes.push(text('monthlyDailySalesDownload.export.footnoteMissing'))
  if (stores.some(store => store.noSplitDates.length > 0)) notes.push(text('monthlyDailySalesDownload.export.footnoteNoSplit'))
  if (context.isPartial) {
    notes.push(text('monthlyDailySalesDownload.export.footnotePartial', {
      through: context.throughDate ?? '—',
    }))
  }
  return notes
}

/** 表头：淡蓝底、蓝色粗下边线；金额列（第 3–6 列）右对齐，其余左对齐。 */
function styleHeaderRow(row: ExcelJS.Row, columnCount: number) {
  for (let column = 1; column <= columnCount; column += 1) {
    const cell = row.getCell(column)
    cell.font = { bold: true, color: { argb: COLORS.ink } }
    cell.fill = { type: 'pattern', pattern: 'solid', fgColor: { argb: COLORS.headerFill } }
    cell.border = { bottom: { style: 'medium', color: { argb: COLORS.accent } } }
    cell.alignment = { vertical: 'middle', horizontal: column >= 3 && column <= 6 ? 'right' : 'left' }
  }
  row.height = 22
}

function styleBodyCell(cell: ExcelJS.Cell) {
  cell.border = { bottom: { style: 'thin', color: { argb: COLORS.rowBorder } } }
  cell.alignment = { vertical: 'middle', horizontal: cell.alignment?.horizontal }
}

function styleTotalRow(row: ExcelJS.Row, columnCount: number) {
  for (let column = 1; column <= columnCount; column += 1) {
    const cell = row.getCell(column)
    cell.font = { bold: true, color: { argb: cell.font?.color?.argb ?? COLORS.ink } }
    cell.fill = { type: 'pattern', pattern: 'solid', fgColor: { argb: COLORS.totalFill } }
    cell.border = { top: { style: 'thin', color: { argb: COLORS.totalBorder } }, bottom: { style: 'thin', color: { argb: COLORS.rowBorder } } }
  }
  row.height = 22
}

function writeTitle(sheet: ExcelJS.Worksheet, rowNumber: number, value: string, lastColumn: number, muted: boolean) {
  const cell = sheet.getCell(rowNumber, 1)
  cell.value = value
  cell.font = muted ? { color: { argb: COLORS.muted } } : { bold: true, size: 13, color: { argb: COLORS.ink } }
  cell.alignment = { vertical: 'middle', horizontal: 'left' }
  sheet.mergeCells(rowNumber, 1, rowNumber, lastColumn)
  sheet.getRow(rowNumber).height = muted ? 18 : 24
}

function writeFootnotes(sheet: ExcelJS.Worksheet, firstRow: number, notes: readonly string[]) {
  notes.forEach((note, index) => {
    const cell = sheet.getCell(firstRow + index, 1)
    // 不合并单元格：文字向右溢出到相邻空单元格，不需要估算换行高度。
    cell.value = note
    cell.font = { color: { argb: COLORS.warning } }
    cell.alignment = { vertical: 'middle', horizontal: 'left' }
  })
}

/**
 * 汇总页：每店一行 + 末行合计。只在有备注内容时才出现「备注」列。
 * stores 是真正写进文件的分店（可能少于已勾选的，见 selectedCount）。
 */
export function fillSummarySheet(
  sheet: ExcelJS.Worksheet,
  stores: readonly StoreSummary[],
  context: ExportContext,
  text: TextFn,
  selectedCount: number,
) {
  const hasRemarks = stores.some(store => store.missingDates.length > 0 || store.noSplitDates.length > 0)
  const columnCount = hasRemarks ? 7 : 6
  const widths = [12, 28, 16, 16, 16, 16, 64]
  widths.slice(0, columnCount).forEach((width, index) => { sheet.getColumn(index + 1).width = width })

  writeTitle(sheet, 1, text('monthlyDailySalesDownload.export.summaryTitle', { monthLabel: context.monthLabel }), columnCount, false)
  const through = context.throughDate ?? '—'
  const subtitleParts = [
    selectedCount === stores.length
      ? text('monthlyDailySalesDownload.export.summarySubtitle', { through, count: stores.length })
      : text('monthlyDailySalesDownload.export.summarySubtitleCompletedOnly', { through, selected: selectedCount, count: stores.length }),
  ]
  if (context.isPartial) {
    subtitleParts.push(text('monthlyDailySalesDownload.export.partialMonth', { counted: context.countedDays, total: context.daysInMonth }))
  }
  writeTitle(sheet, 2, subtitleParts.join(' · '), columnCount, true)

  const header = sheet.getRow(3)
  const headers = [
    text('monthlyDailySalesDownload.columns.code'),
    text('monthlyDailySalesDownload.columns.name'),
    text('monthlyDailySalesDownload.columns.revenue'),
    text('monthlyDailySalesDownload.columns.card'),
    text('monthlyDailySalesDownload.columns.cash'),
    text('monthlyDailySalesDownload.columns.other'),
    ...(hasRemarks ? [text('monthlyDailySalesDownload.columns.remark')] : []),
  ]
  headers.forEach((value, index) => { header.getCell(index + 1).value = value })
  styleHeaderRow(header, columnCount)

  const ordered = [...stores].sort((left, right) => compareBranchCode(left.branchCode, right.branchCode))
  let rowNumber = 4
  let totals = ZERO_TOTALS
  for (const store of ordered) {
    totals = addTotals(totals, store.totals)
    const display = displayTotals(store.totals)
    const row = sheet.getRow(rowNumber)
    row.getCell(1).value = store.branchCode
    row.getCell(2).value = store.branchName
    ;([display.revenue, display.card, display.cash, display.other] as const).forEach((cents, index) => {
      const cell = row.getCell(3 + index)
      cell.value = moneyValue(cents)
      cell.numFmt = MONEY_FORMAT
      cell.alignment = { horizontal: 'right' }
    })
    if (hasRemarks) row.getCell(7).value = summaryRemark(store, text) || null
    row.getCell(1).font = { color: { argb: COLORS.muted } }
    row.getCell(6).font = { color: { argb: COLORS.muted } }
    row.getCell(1).alignment = { horizontal: 'left' }
    row.getCell(2).alignment = { horizontal: 'left' }
    if (hasRemarks) row.getCell(7).font = { color: { argb: COLORS.warning } }
    for (let column = 1; column <= columnCount; column += 1) styleBodyCell(row.getCell(column))
    row.height = 20
    rowNumber += 1
  }

  const totalRow = sheet.getRow(rowNumber)
  totalRow.getCell(1).value = text('monthlyDailySalesDownload.export.total')
  sheet.mergeCells(rowNumber, 1, rowNumber, 2)
  const display = displayTotals(totals)
  ;([display.revenue, display.card, display.cash, display.other] as const).forEach((cents, index) => {
    const cell = totalRow.getCell(3 + index)
    cell.value = moneyValue(cents)
    cell.numFmt = MONEY_FORMAT
    cell.alignment = { horizontal: 'right' }
  })
  totalRow.getCell(6).font = { color: { argb: COLORS.muted } }
  styleTotalRow(totalRow, columnCount)
  writeFootnotes(sheet, rowNumber + 2, footnotes(ordered, context, text))
  sheet.views = [{ state: 'frozen', ySplit: 3 }]
}

/** 单店页：每天一行，周末淡灰底，末行合计；有缺数 / 无拆分的日子才出现「备注」列。 */
export function fillStoreSheet(sheet: ExcelJS.Worksheet, store: StoreSummary, context: ExportContext, text: TextFn) {
  const hasRemarks = store.missingDates.length > 0 || store.noSplitDates.length > 0
  const columnCount = hasRemarks ? 7 : 6
  const widths = [13, 9, 16, 16, 16, 16, 38]
  widths.slice(0, columnCount).forEach((width, index) => { sheet.getColumn(index + 1).width = width })

  writeTitle(sheet, 1, text('monthlyDailySalesDownload.export.storeTitle', {
    name: store.branchName, code: store.branchCode, monthLabel: context.monthLabel,
  }), columnCount, false)

  const header = sheet.getRow(2)
  const headers = [
    text('monthlyDailySalesDownload.columns.date'),
    text('monthlyDailySalesDownload.columns.weekday'),
    text('monthlyDailySalesDownload.columns.revenue'),
    text('monthlyDailySalesDownload.columns.card'),
    text('monthlyDailySalesDownload.columns.cash'),
    text('monthlyDailySalesDownload.columns.other'),
    ...(hasRemarks ? [text('monthlyDailySalesDownload.columns.remark')] : []),
  ]
  headers.forEach((value, index) => { header.getCell(index + 1).value = value })
  styleHeaderRow(header, columnCount)

  let rowNumber = 3
  for (const day of store.rows) {
    const row = sheet.getRow(rowNumber)
    // 真正的日期单元格：按 UTC 构造，Excel 序列号由 UTC 毫秒换算，不会因本机时区差一天。
    const [year, month, date] = day.date.split('-').map(Number)
    const dateCell = row.getCell(1)
    dateCell.value = new Date(Date.UTC(year, month - 1, date))
    dateCell.numFmt = DATE_FORMAT
    dateCell.alignment = { horizontal: 'left' }
    const weekdayCell = row.getCell(2)
    weekdayCell.value = text(weekdayKey(day.weekday))
    weekdayCell.font = { color: { argb: COLORS.muted } }
    weekdayCell.alignment = { horizontal: 'left' }
    ;([day.revenue, day.card, day.cash, day.other] as const).forEach((cents, index) => {
      const cell = row.getCell(3 + index)
      // 缺数 / 无拆分：留空，不写 0。
      cell.value = moneyValue(cents)
      cell.numFmt = MONEY_FORMAT
      cell.alignment = { horizontal: 'right' }
    })
    row.getCell(6).font = { color: { argb: COLORS.muted } }
    if (hasRemarks) {
      row.getCell(7).value = dayRemark(day, text) || null
      row.getCell(7).font = { color: { argb: COLORS.warning } }
    }
    for (let column = 1; column <= columnCount; column += 1) {
      const cell = row.getCell(column)
      if (isWeekend(day.weekday)) cell.fill = { type: 'pattern', pattern: 'solid', fgColor: { argb: COLORS.weekendFill } }
      styleBodyCell(cell)
    }
    row.height = 20
    rowNumber += 1
  }

  const totalRow = sheet.getRow(rowNumber)
  totalRow.getCell(1).value = text('monthlyDailySalesDownload.export.total')
  sheet.mergeCells(rowNumber, 1, rowNumber, 2)
  const display = displayTotals(store.totals)
  ;([display.revenue, display.card, display.cash, display.other] as const).forEach((cents, index) => {
    const cell = totalRow.getCell(3 + index)
    cell.value = moneyValue(cents)
    cell.numFmt = MONEY_FORMAT
    cell.alignment = { horizontal: 'right' }
  })
  totalRow.getCell(6).font = { color: { argb: COLORS.muted } }
  styleTotalRow(totalRow, columnCount)
  writeFootnotes(sheet, rowNumber + 2, footnotes([store], context, text))
  sheet.views = [{ state: 'frozen', ySplit: 2 }]
}

/** 默认的单店组装：失败时把已创建的半张表移除，保证工作簿里只有完整的工作表。 */
function addStoreSheetToWorkbook(workbook: ExcelJS.Workbook, store: StoreSummary, sheetName: string, context: ExportContext, text: TextFn) {
  const sheet = workbook.addWorksheet(sheetName)
  try {
    fillStoreSheet(sheet, store, context, text)
  } catch (error) {
    workbook.removeWorksheet(sheet.id)
    throw error
  }
}

// ---------------------------------------------------------------------------
// xlsx 导出流程
// ---------------------------------------------------------------------------

function sortStores(stores: readonly StoreSummary[]): StoreSummary[] {
  return [...stores].sort((left, right) => compareBranchCode(left.branchCode, right.branchCode))
}

/** 汇总页最后生成并放在第一页：只列入已完成的分店。 */
async function finalizeWorkbook(checkpoint: ExportCheckpoint, deps: ExportDeps): Promise<{ fileName: string; storeCount: number; sheetCount: number }> {
  const { workbook, context, text } = checkpoint
  const name = text('monthlyDailySalesDownload.export.summarySheet')
  // 重试时旧的汇总页可能是半成品：删掉重建，保证每次都是完整内容。
  const stale = workbook.getWorksheet(name)
  if (stale) workbook.removeWorksheet(stale.id)
  const doneCodes = new Set(checkpoint.completed)
  const doneStores = checkpoint.stores.filter(store => doneCodes.has(store.branchCode))
  const summary = workbook.addWorksheet(name)
  // ExcelJS 按 orderNo 排列工作表；汇总页在最后才创建，需要显式排到第一位。
  ;(summary as unknown as { orderNo: number }).orderNo = 0
  fillSummarySheet(summary, doneStores, context, text, checkpoint.stores.length)
  const buffer = await workbook.xlsx.writeBuffer()
  deps.download(new Blob([buffer], { type: XLSX_MIME }), checkpoint.fileName)
  return { fileName: checkpoint.fileName, storeCount: doneStores.length, sheetCount: doneStores.length + 1 }
}

async function runPending(
  checkpoint: ExportCheckpoint,
  signal: AbortSignal | undefined,
  onProgress: ((progress: ExportProgress) => void) | undefined,
  deps: ExportDeps,
): Promise<ExportOutcome> {
  const total = checkpoint.stores.length
  const completed = new Set(checkpoint.completed)
  const pending = checkpoint.stores.filter(store => !completed.has(store.branchCode))
  for (const store of pending) {
    if (signal?.aborted) return { status: 'cancelled' }
    const index = checkpoint.completed.length + 1
    onProgress?.({ stage: 'store', index, total, storeName: store.branchName, percent: index / total })
    try {
      await deps.addStoreSheet(checkpoint.workbook, store, checkpoint.sheetNames.get(store.branchCode) ?? store.branchCode, checkpoint.context, checkpoint.text)
    } catch (error) {
      if (isAbortError(error) || signal?.aborted) return { status: 'cancelled' }
      return {
        status: 'interrupted',
        checkpoint,
        stage: 'store',
        failedStore: { branchCode: store.branchCode, branchName: store.branchName },
        message: errorMessage(error),
        done: checkpoint.completed.length,
        total,
        pending: total - checkpoint.completed.length,
      }
    }
    checkpoint.completed.push(store.branchCode)
    await deps.nextTick()
  }
  if (signal?.aborted) return { status: 'cancelled' }

  onProgress?.({ stage: 'finalize', index: total, total, storeName: '', percent: 1 })
  try {
    const result = await finalizeWorkbook(checkpoint, deps)
    return { status: 'completed', ...result }
  } catch (error) {
    if (isAbortError(error)) return { status: 'cancelled' }
    return {
      status: 'interrupted',
      checkpoint,
      stage: 'finalize',
      failedStore: null,
      message: errorMessage(error),
      done: checkpoint.completed.length,
      total,
      pending: total - checkpoint.completed.length,
    }
  }
}

export function buildXlsxFileName(text: TextFn, month: string): string {
  return buildFileName(text('monthlyDailySalesDownload.export.fileNamePrefix'), month, 'xlsx')
}

export function buildCsvFileName(text: TextFn, month: string): string {
  return buildFileName(text('monthlyDailySalesDownload.export.fileNamePrefix'), month, 'csv')
}

/** 开始导出 xlsx：按编码顺序逐店组装，回调进度，最后生成汇总页并下载。 */
export async function startWorkbookExport(
  request: ExportRequest,
  onProgress?: (progress: ExportProgress) => void,
  deps?: Partial<ExportDeps>,
): Promise<ExportOutcome> {
  const resolved = resolveDeps(deps)
  const { text, context } = request
  const stores = sortStores(request.stores)
  const excel = await resolved.loadExcel()
  if (request.signal?.aborted) return { status: 'cancelled' }
  const checkpoint: ExportCheckpoint = {
    workbook: new excel.Workbook(),
    stores,
    sheetNames: buildSheetNames(stores, [text('monthlyDailySalesDownload.export.summarySheet')]),
    completed: [],
    context,
    text,
    fileName: buildXlsxFileName(text, context.month),
  }
  return runPending(checkpoint, request.signal, onProgress, resolved)
}

/** 「重试未完成的 N 家」：沿用中断时的工作簿，只组装尚未完成的分店（含失败的那家）。 */
export function resumeWorkbookExport(
  checkpoint: ExportCheckpoint,
  signal?: AbortSignal,
  onProgress?: (progress: ExportProgress) => void,
  deps?: Partial<ExportDeps>,
): Promise<ExportOutcome> {
  return runPending(checkpoint, signal, onProgress, resolveDeps(deps))
}

/** 「只下载已完成的 N 家」：汇总页只列已完成的分店，并注明已选分店数。 */
export function downloadCompletedStores(checkpoint: ExportCheckpoint, deps?: Partial<ExportDeps>) {
  return finalizeWorkbook(checkpoint, resolveDeps(deps))
}

/** CSV 长表：纯文本，不需要分店逐个组装；返回行数供提示。 */
export function exportCsvFile(
  stores: readonly StoreSummary[],
  context: ExportContext,
  text: TextFn,
  deps?: Partial<Pick<ExportDeps, 'download'>>,
): { fileName: string; rowCount: number } {
  const header = [
    text('monthlyDailySalesDownload.columns.date'),
    text('monthlyDailySalesDownload.columns.code'),
    text('monthlyDailySalesDownload.columns.name'),
    text('monthlyDailySalesDownload.columns.revenue'),
    text('monthlyDailySalesDownload.columns.card'),
    text('monthlyDailySalesDownload.columns.cash'),
    text('monthlyDailySalesDownload.columns.other'),
  ]
  const fileName = buildCsvFileName(text, context.month)
  const download = deps?.download ?? defaultDownload
  download(new Blob([buildCsv(stores, header)], { type: CSV_MIME }), fileName)
  return { fileName, rowCount: stores.reduce((count, store) => count + store.rows.length, 0) }
}
