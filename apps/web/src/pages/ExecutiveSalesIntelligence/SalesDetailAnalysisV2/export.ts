import type ExcelJS from 'exceljs'
import { getImageDownloadCandidates } from '../../../services/exportService'
import { growth } from '../ReportWorkbench/logic'
import { MAX_PRODUCT_IMAGE_EXPORT_ROWS } from './logic'
import type { SalesDetailPage, SalesDetailQuery, SalesDetailRow } from './reportService'

export type ExportProgress = (message: string) => void
type ImageLoader = (url: string, signal: AbortSignal) => Promise<string | null>

const IMAGE_BATCH_SIZE = 12
const SALES_DETAIL_IMAGE_PROXY = '/api/react/v1/image-proxy/sales-detail'
const IMAGE_TIMEOUT_MS = 4_000

function checkCancelled(signal: AbortSignal) {
  if (signal.aborted) throw new DOMException('导出已取消', 'AbortError')
}

/** 严格使用当前页已显示的数据；分页切换后的新页不会拼接旧快照。 */
export function selectCurrentPageExportRows(currentPage: SalesDetailPage, signal: AbortSignal): SalesDetailRow[] {
  checkCancelled(signal)
  if (currentPage.rows.length > MAX_PRODUCT_IMAGE_EXPORT_ROWS) throw new Error('带图导出最多 500 件商品')
  return currentPage.rows
}

async function fetchImageBlob(url: string, signal: AbortSignal, proxyPath: string): Promise<Blob | null> {
  for (const candidate of getImageDownloadCandidates(url, proxyPath)) {
    checkCancelled(signal)
    const controller = new AbortController()
    const abort = () => controller.abort()
    signal.addEventListener('abort', abort, { once: true })
    const timeout = setTimeout(abort, IMAGE_TIMEOUT_MS)
    try {
      const response = await fetch(candidate, { signal: controller.signal })
      if (response.ok) {
        const blob = await response.blob()
        if (blob.type.startsWith('image/')) return blob
      }
    } catch (error) {
      checkCancelled(signal)
      // 图片无法读取时尝试下一个受信任来源，最终在工作簿中标出失败。
      void error
    } finally {
      clearTimeout(timeout)
      signal.removeEventListener('abort', abort)
    }
  }
  return null
}

/**
 * 将原图缩成 Excel 单元格大小，避免大批商品图片撑爆浏览器内存和工作簿。
 * proxyPath 按页面权限选择图片代理路由（销售明细、独立销售看板各有一条），白名单与校验相同。
 */
export async function loadProductExportImage(url: string, signal: AbortSignal, proxyPath: string): Promise<string | null> {
  const blob = await fetchImageBlob(url, signal, proxyPath)
  if (!blob) return null
  checkCancelled(signal)
  let bitmap: ImageBitmap | undefined
  try {
    bitmap = await createImageBitmap(blob)
    checkCancelled(signal)
    const canvas = document.createElement('canvas')
    canvas.width = 96; canvas.height = 96
    const context = canvas.getContext('2d')
    if (!context) return null
    context.fillStyle = '#ffffff'
    context.fillRect(0, 0, 96, 96)
    const scale = Math.min(90 / bitmap.width, 90 / bitmap.height)
    const width = bitmap.width * scale; const height = bitmap.height * scale
    context.drawImage(bitmap, (96 - width) / 2, (96 - height) / 2, width, height)
    return canvas.toDataURL('image/jpeg', 0.78)
  } catch (error) {
    checkCancelled(signal)
    void error
    return null
  } finally {
    bitmap?.close()
  }
}

export function loadSalesDetailImage(url: string, signal: AbortSignal): Promise<string | null> {
  return loadProductExportImage(url, signal, SALES_DETAIL_IMAGE_PROXY)
}

function columns(compare: boolean, english: boolean): Partial<ExcelJS.Column>[] {
  const label = (zh: string, en: string) => english ? en : zh
  return [
    { header: label('货号', 'Item number'), key: 'itemNumber', width: 19 },
    { header: label('商品名称', 'Product'), key: 'name', width: 38 },
    { header: label('商品图片', 'Image'), key: 'image', width: 14 },
    { header: label('营业额', 'Revenue'), key: 'revenue', width: 16 },
    ...(compare ? [{ header: label('同期营业额', 'Previous revenue'), key: 'compareRevenue', width: 17 }] : []),
    { header: label('数量', 'Quantity'), key: 'quantity', width: 13 },
    ...(compare ? [{ header: label('同期数量', 'Previous quantity'), key: 'compareQuantity', width: 15 }] : []),
    { header: label('均价', 'Unit price'), key: 'averageUnitPrice', width: 15 },
    ...(compare ? [{ header: label('同期均价', 'Previous unit price'), key: 'compareAverageUnitPrice', width: 17 },
      { header: label('营业额增长率', 'Revenue growth'), key: 'growth', width: 17 }] : []),
    { header: label('毛利额', 'Gross profit'), key: 'grossProfit', width: 16 },
    ...(compare ? [{ header: label('同期毛利额', 'Previous gross profit'), key: 'compareGrossProfit', width: 18 }] : []),
    { header: label('毛利率', 'Margin'), key: 'grossMarginRate', width: 14 },
    ...(compare ? [{ header: label('同期毛利率', 'Previous margin'), key: 'compareGrossMarginRate', width: 16 }] : []),
  ]
}

export interface SalesDetailWorkbookOptions {
  compare: boolean
  english: boolean
  startDate: string
  endDate: string
  signal: AbortSignal
  onProgress?: ExportProgress
  onFinalize?: () => void
  loadImage?: ImageLoader
}

/** 返回可检查的工作簿；图片缺失时保留销售数据，并在图片列明确注明。 */
export async function buildSalesDetailWorkbook(rows: readonly SalesDetailRow[], options: SalesDetailWorkbookOptions) {
  checkCancelled(options.signal)
  const { default: ExcelJS } = await import('exceljs')
  const workbook = new ExcelJS.Workbook()
  const worksheet = workbook.addWorksheet(options.english ? 'Product detail' : '商品明细')
  const label = (zh: string, en: string) => options.english ? en : zh
  const activeColumns = columns(options.compare, options.english)
  const activeKeys = new Set(activeColumns.map(column => column.key))
  worksheet.columns = activeColumns
  worksheet.getRow(1).values = [label('销售商品明细', 'Sales product detail')]
  worksheet.mergeCells(1, 1, 1, activeColumns.length)
  worksheet.getCell(1, 1).font = { bold: true, size: 15, color: { argb: 'FF143452' } }
  worksheet.getRow(2).values = [`${options.startDate} ~ ${options.endDate}  ·  ${rows.length} ${label('件商品', 'products')}`]
  const header = worksheet.getRow(3)
  header.values = activeColumns.map(column => String(column.header ?? ''))
  header.height = 28
  header.eachCell(cell => {
    cell.font = { bold: true, color: { argb: 'FFFFFFFF' } }
    cell.fill = { type: 'pattern', pattern: 'solid', fgColor: { argb: 'FF23476C' } }
    cell.alignment = { vertical: 'middle' }
  })
  worksheet.views = [{ state: 'frozen', ySplit: 3 }]
  worksheet.autoFilter = { from: { row: 3, column: 1 }, to: { row: 3, column: worksheet.columnCount } }

  const imageColumn = worksheet.getColumn('image').number
  rows.forEach((product, index) => {
    const change = options.compare ? growth(product.revenue, product.compareRevenue) : null
    const row = worksheet.addRow({
      itemNumber: product.itemNumber || product.code, name: product.name || product.code,
      revenue: product.revenue, compareRevenue: product.compareRevenue,
      quantity: product.quantity, compareQuantity: product.compareQuantity,
      averageUnitPrice: product.averageUnitPrice, compareAverageUnitPrice: product.compareAverageUnitPrice,
      growth: change === 'new' ? label('新增', 'New') : change,
      grossProfit: product.grossProfit, compareGrossProfit: product.compareGrossProfit,
      grossMarginRate: product.grossMarginRate, compareGrossMarginRate: product.compareGrossMarginRate,
    })
    row.height = 66
    row.alignment = { vertical: 'middle' }
    for (const key of ['revenue', 'compareRevenue', 'averageUnitPrice', 'compareAverageUnitPrice', 'grossProfit', 'compareGrossProfit']) {
      if (activeKeys.has(key)) row.getCell(worksheet.getColumn(key).number).numFmt = '$#,##0.00;[Red]($#,##0.00)'
    }
    for (const key of ['growth', 'grossMarginRate', 'compareGrossMarginRate']) {
      if (activeKeys.has(key)) row.getCell(worksheet.getColumn(key).number).numFmt = '0.0%'
    }
    if (index % 2 === 1) row.fill = { type: 'pattern', pattern: 'solid', fgColor: { argb: 'FFF7FAFD' } }
  })

  const loadImage = options.loadImage ?? loadSalesDetailImage
  const imageIds = new Map<string, number | null>()
  let failedImages = 0
  for (let start = 0; start < rows.length; start += IMAGE_BATCH_SIZE) {
    checkCancelled(options.signal)
    const batch = rows.slice(start, start + IMAGE_BATCH_SIZE)
    const images = await Promise.all(batch.map(async product => {
      if (!product.productImage) return null
      if (imageIds.has(product.productImage)) return imageIds.get(product.productImage) ?? null
      const image = await loadImage(product.productImage, options.signal)
      checkCancelled(options.signal)
      const id = image ? workbook.addImage({ base64: image.split(',')[1], extension: image.startsWith('data:image/png') ? 'png' : 'jpeg' }) : null
      imageIds.set(product.productImage, id)
      return id
    }))
    batch.forEach((product, offset) => {
      const rowIndex = start + offset + 4
      const imageId = images[offset]
      if (imageId != null) worksheet.addImage(imageId, {
        tl: { col: imageColumn - 0.9, row: rowIndex - 0.95 },
        ext: { width: 74, height: 74 }, editAs: 'oneCell',
      })
      else {
        if (product.productImage) failedImages++
        worksheet.getRow(rowIndex).getCell(imageColumn).value = product.productImage
          ? label('图片读取失败', 'Image unavailable') : label('无图片', 'No image')
      }
    })
    options.onProgress?.(label(`正在处理图片 ${Math.min(start + IMAGE_BATCH_SIZE, rows.length)}/${rows.length}`,
      `Processing images ${Math.min(start + IMAGE_BATCH_SIZE, rows.length)}/${rows.length}`))
  }
  return { workbook, failedImages }
}

export async function exportSalesDetailProducts(query: SalesDetailQuery, currentPage: SalesDetailPage,
  options: SalesDetailWorkbookOptions) {
  const rows = selectCurrentPageExportRows(currentPage, options.signal)
  const result = await buildSalesDetailWorkbook(rows, options)
  checkCancelled(options.signal)
  options.onProgress?.(options.english ? 'Generating Excel file…' : '正在生成 Excel 文件…')
  options.onFinalize?.()
  const buffer = await result.workbook.xlsx.writeBuffer()
  checkCancelled(options.signal)
  const blob = new Blob([buffer], { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' })
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = `${options.english ? 'Sales_Product_Detail' : '销售商品明细'}_${options.startDate}_${options.endDate}_page-${query.pageIndex}.xlsx`
  document.body.appendChild(link)
  link.click()
  link.remove()
  setTimeout(() => URL.revokeObjectURL(url), 60_000)
  return { count: rows.length, failedImages: result.failedImages }
}
