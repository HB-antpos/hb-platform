import type ExcelJS from 'exceljs'

import type { SeasonalCardStatsSummary } from '../../../types/seasonalCardStats'

import {
  PRICE_OPTIONS,
  buildExportFileName,
  buildFooterTotals,
  formatLocalTime,
  formatSupplierList,
  getPriceLabel,
  getPriceQuantity,
} from './logic'

/**
 * 分店填报统计导出 xlsx：两个工作表——「分店明细」（含合计行）与「未填报分店」。
 * 所有文案经 text() 取得（页面传 i18n 翻译函数，单测传替身）；exceljs 只在点导出时动态加载，不进首屏。
 */

export type TextFn = (key: string, params?: Record<string, string | number>) => string

export interface ExportContext {
  seasonYear: number
  /** 已翻译的节日名称 */
  holiday: string
  /** 已翻译的供应商筛选文字（「全部供应商」或供应商名称） */
  supplier: string
  /** 已翻译的价格筛选文字（「全部价格」或 $1 …） */
  price: string
}

export interface ExportInput {
  summary: SeasonalCardStatsSummary
  context: ExportContext
  text: TextFn
}

export interface ExportDeps {
  /** 取得 ExcelJS；默认动态 import，首屏与未导出的页面都不加载它。 */
  loadExcel: () => Promise<typeof ExcelJS>
  /** 把文件交给浏览器下载 */
  download: (blob: Blob, fileName: string) => void
  /** 本地时间格式化（单测固定时区用） */
  formatTime: (value: string | null) => string
}

const XLSX_MIME = 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'
const MONEY_FORMAT = '"$"#,##0.00'
const COUNT_FORMAT = '#,##0'

const COLORS = {
  ink: 'FF172033',
  muted: 'FF667085',
  headerFill: 'FFEEF3FB',
  accent: 'FF1677FF',
  totalFill: 'FFF8FAFC',
  totalBorder: 'FFAEB8C8',
  rowBorder: 'FFE3E8EF',
  unfilledFill: 'FFFFFBEB',
  warning: 'FFAD4E00',
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
  download: defaultDownload,
  formatTime: (value) => (value ? formatLocalTime(value, 'full') : ''),
}

function writeTitle(sheet: ExcelJS.Worksheet, rowNumber: number, value: string, lastColumn: number, muted: boolean) {
  const cell = sheet.getCell(rowNumber, 1)
  cell.value = value
  cell.font = muted ? { color: { argb: COLORS.muted } } : { bold: true, size: 13, color: { argb: COLORS.ink } }
  cell.alignment = { vertical: 'middle', horizontal: 'left' }
  sheet.mergeCells(rowNumber, 1, rowNumber, lastColumn)
  sheet.getRow(rowNumber).height = muted ? 18 : 24
}

/** 表头：淡蓝底、蓝色粗下边线；numericColumns 里的列右对齐。 */
function styleHeaderRow(row: ExcelJS.Row, columnCount: number, numericColumns: ReadonlySet<number>) {
  for (let column = 1; column <= columnCount; column += 1) {
    const cell = row.getCell(column)
    cell.font = { bold: true, color: { argb: COLORS.ink } }
    cell.fill = { type: 'pattern', pattern: 'solid', fgColor: { argb: COLORS.headerFill } }
    cell.border = { bottom: { style: 'medium', color: { argb: COLORS.accent } } }
    cell.alignment = { vertical: 'middle', horizontal: numericColumns.has(column) ? 'right' : 'left' }
  }
  row.height = 22
}

function priceHeader(option: (typeof PRICE_OPTIONS)[number], text: TextFn): string {
  return getPriceLabel(option, text('seasonalCardStats.price.other'))
}

/** 「分店明细」：每店一行（未填报行淡黄底、数量留空），末行合计。 */
export function fillStoreSheet(sheet: ExcelJS.Worksheet, input: ExportInput, formatTime: ExportDeps['formatTime']) {
  const { summary, context, text } = input
  const headers = [
    text('seasonalCardStats.export.columns.storeCode'),
    text('seasonalCardStats.export.columns.storeName'),
    text('seasonalCardStats.columns.status'),
    ...PRICE_OPTIONS.map((option) => priceHeader(option, text)),
    text('seasonalCardStats.columns.totalQuantity'),
    text('seasonalCardStats.columns.totalAmount'),
    text('seasonalCardStats.columns.suppliers'),
    text('seasonalCardStats.export.columns.lastSubmittedAt'),
    text('seasonalCardStats.export.columns.lastSubmittedBy'),
  ]
  const columnCount = headers.length
  const numericColumns = new Set([4, 5, 6, 7, 8, 9])
  ;[10, 26, 10, 9, 9, 9, 10, 12, 14, 32, 18, 14].forEach((width, index) => {
    sheet.getColumn(index + 1).width = width
  })

  writeTitle(sheet, 1, text('seasonalCardStats.export.title', { year: context.seasonYear, holiday: context.holiday }), columnCount, false)
  writeTitle(
    sheet,
    2,
    text('seasonalCardStats.export.subtitle', {
      supplier: context.supplier,
      price: context.price,
      filled: summary.filledStoreCount,
      total: summary.storeCount,
    }),
    columnCount,
    true,
  )

  const header = sheet.getRow(3)
  headers.forEach((value, index) => {
    header.getCell(index + 1).value = value
  })
  styleHeaderRow(header, columnCount, numericColumns)

  const separator = text('seasonalCardStats.listSeparator')
  let rowNumber = 4
  for (const store of summary.stores) {
    const row = sheet.getRow(rowNumber)
    const values: (string | number | null)[] = [
      store.storeCode,
      store.storeName,
      store.isFilled ? text('seasonalCardStats.status.filled') : text('seasonalCardStats.status.unfilled'),
      ...PRICE_OPTIONS.map((option) => (store.isFilled ? getPriceQuantity(store.prices, option) : null)),
      store.isFilled ? store.totalQuantity : null,
      store.isFilled ? store.totalAmount : null,
      store.isFilled ? formatSupplierList(store.suppliers, separator) : null,
      store.lastSubmittedAt ? formatTime(store.lastSubmittedAt) : null,
      store.lastSubmittedByName,
    ]
    values.forEach((value, index) => {
      const cell = row.getCell(index + 1)
      cell.value = value
      const column = index + 1
      if (numericColumns.has(column)) {
        cell.numFmt = column === 9 ? MONEY_FORMAT : COUNT_FORMAT
        cell.alignment = { horizontal: 'right' }
      }
      cell.border = { bottom: { style: 'thin', color: { argb: COLORS.rowBorder } } }
      if (!store.isFilled) {
        cell.fill = { type: 'pattern', pattern: 'solid', fgColor: { argb: COLORS.unfilledFill } }
      }
    })
    if (!store.isFilled) row.getCell(3).font = { color: { argb: COLORS.warning } }
    row.height = 20
    rowNumber += 1
  }

  // 合计行：与页面同口径，按全部分店累加（未填报分店为 0）。
  const footer = buildFooterTotals(summary.stores)
  const totalRow = sheet.getRow(rowNumber)
  totalRow.getCell(1).value = text('seasonalCardStats.table.total')
  totalRow.getCell(3).value = text('seasonalCardStats.table.totalFilled', { count: footer.filledCount })
  PRICE_OPTIONS.forEach((option, index) => {
    const cell = totalRow.getCell(4 + index)
    cell.value = footer.quantities[option]
    cell.numFmt = COUNT_FORMAT
  })
  totalRow.getCell(8).value = footer.totalQuantity
  totalRow.getCell(8).numFmt = COUNT_FORMAT
  totalRow.getCell(9).value = footer.totalAmount
  totalRow.getCell(9).numFmt = MONEY_FORMAT
  for (let column = 1; column <= columnCount; column += 1) {
    const cell = totalRow.getCell(column)
    cell.font = { bold: true, color: { argb: COLORS.ink } }
    cell.fill = { type: 'pattern', pattern: 'solid', fgColor: { argb: COLORS.totalFill } }
    cell.border = { top: { style: 'thin', color: { argb: COLORS.totalBorder } } }
    if (numericColumns.has(column)) cell.alignment = { horizontal: 'right' }
  }
  totalRow.height = 22

  // 口径说明：最新一次提交为准、被排除的分店、筛选只影响数量。
  const notes = [
    text('seasonalCardStats.notes.latest'),
    summary.excludedStores.length
      ? text('seasonalCardStats.notes.excluded', {
          stores: summary.excludedStores.map((store) => [store.storeCode, store.storeName].filter(Boolean).join(' ')).join(separator),
        })
      : text('seasonalCardStats.notes.excludedNone'),
    text('seasonalCardStats.notes.filter'),
    text('seasonalCardStats.notes.amount'),
    text('seasonalCardStats.notes.time'),
  ]
  notes.forEach((note, index) => {
    const cell = sheet.getCell(rowNumber + 2 + index, 1)
    // 不合并单元格：文字向右溢出到相邻空单元格。
    cell.value = note
    cell.font = { color: { argb: COLORS.muted } }
  })
  sheet.views = [{ state: 'frozen', ySplit: 3 }]
}

/** 「未填报分店」：编码 + 名称，方便直接复制给店长群。 */
export function fillUnfilledSheet(sheet: ExcelJS.Worksheet, input: ExportInput) {
  const { summary, context, text } = input
  sheet.getColumn(1).width = 12
  sheet.getColumn(2).width = 32
  writeTitle(
    sheet,
    1,
    text('seasonalCardStats.export.unfilledTitle', {
      year: context.seasonYear,
      holiday: context.holiday,
      count: summary.unfilledStores.length,
    }),
    2,
    false,
  )
  const header = sheet.getRow(2)
  header.getCell(1).value = text('seasonalCardStats.export.columns.storeCode')
  header.getCell(2).value = text('seasonalCardStats.export.columns.storeName')
  styleHeaderRow(header, 2, new Set())
  summary.unfilledStores.forEach((store, index) => {
    const row = sheet.getRow(3 + index)
    row.getCell(1).value = store.storeCode
    row.getCell(2).value = store.storeName
  })
  if (!summary.unfilledStores.length) {
    sheet.getCell(3, 1).value = text('seasonalCardStats.unfilled.none')
    sheet.getCell(3, 1).font = { color: { argb: COLORS.muted } }
  }
  sheet.views = [{ state: 'frozen', ySplit: 2 }]
}

export function buildWorkbookFileName(input: ExportInput): string {
  return buildExportFileName(input.text('seasonalCardStats.export.fileNamePrefix'), input.context.seasonYear, input.context.holiday)
}

/** 组装工作簿（不下载），单测直接读回内容核对。 */
export function buildWorkbook(excel: typeof ExcelJS, input: ExportInput, formatTime: ExportDeps['formatTime']): ExcelJS.Workbook {
  const workbook = new excel.Workbook()
  fillStoreSheet(workbook.addWorksheet(input.text('seasonalCardStats.export.storeSheet')), input, formatTime)
  fillUnfilledSheet(workbook.addWorksheet(input.text('seasonalCardStats.export.unfilledSheet')), input)
  return workbook
}

export async function exportSeasonalCardStatsWorkbook(input: ExportInput, deps?: Partial<ExportDeps>): Promise<{ fileName: string }> {
  const resolved: ExportDeps = { ...defaultDeps, ...deps }
  const excel = await resolved.loadExcel()
  const workbook = buildWorkbook(excel, input, resolved.formatTime)
  const buffer = await workbook.xlsx.writeBuffer()
  const fileName = buildWorkbookFileName(input)
  resolved.download(new Blob([buffer], { type: XLSX_MIME }), fileName)
  return { fileName }
}
