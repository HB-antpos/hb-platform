import type ExcelJS from 'exceljs'
import type { CompactSalesBoardProduct } from '../../../services/salesDashboardService'
import { loadProductExportImage } from '../SalesDetailAnalysisV2/export'
import { MAX_PRODUCT_IMAGE_EXPORT_ROWS } from '../SalesDetailAnalysisV2/logic'

/** 看板用户未必有销售明细权限，图片走按看板权限放行的代理路由。 */
export const COMPACT_BOARD_IMAGE_PROXY = '/api/react/v1/image-proxy/compact-sales-board'
/** 导出全部结果时每批读取的行数，与服务端商品明细每页上限一致。 */
export const COMPACT_BOARD_EXPORT_BATCH_SIZE = 500

const IMAGE_BATCH_SIZE = 12

export interface CompactBoardExportProgress {
  text: string
  /** 当前阶段进度 0–1；未知时不显示进度条 */
  percent?: number
}

type ProgressHandler = (progress: CompactBoardExportProgress) => void
type ImageLoader = (url: string, signal: AbortSignal) => Promise<string | null>

export interface CompactBoardProductPage {
  data: CompactSalesBoardProduct[]
  total: number
}

export interface CompactBoardWorkbookOptions {
  startDate: string
  endDate: string
  /** 联动筛选与搜索词说明，写入表头说明行 */
  filterLabel: string
  sortLabel: string
  /** 「第 2 页（每页 50 行）」或「全部结果」 */
  scopeLabel: string
  /** 商品占比的分母：当前分店、供应商约束下全部商品合计（与页面占比列同一口径） */
  scopeAmount: number
  /** 第一行的排名：导出本页时为页偏移 + 1 */
  firstRank: number
  signal: AbortSignal
  onProgress?: ProgressHandler
  loadImage?: ImageLoader
  /** 只给前 N 行嵌图（默认 500，与销售明细带图导出上限一致），其余行只导出数据 */
  imageRowLimit?: number
}

export interface CompactBoardExportOptions extends CompactBoardWorkbookOptions {
  /** 文件名后缀，如「第2页」「全部」 */
  fileSuffix: string
  onFinalize?: () => void
}

function checkCancelled(signal: AbortSignal) {
  if (signal.aborted) throw new DOMException('导出已取消', 'AbortError')
}

function defaultImageLoader(url: string, signal: AbortSignal) {
  return loadProductExportImage(url, signal, COMPACT_BOARD_IMAGE_PROXY)
}

/**
 * 按服务端同一排序分批读取全部商品。分批期间统计可能整点重算、行位次挪动：
 * 按商品编码去重，并以首批的总数为准最多多读一批，避免重复行或无限翻页。
 */
export async function collectAllCompactBoardProducts(
  fetchPage: (pageIndex: number, pageSize: number, signal: AbortSignal) => Promise<CompactBoardProductPage>,
  signal: AbortSignal,
  onProgress?: ProgressHandler,
  batchSize = COMPACT_BOARD_EXPORT_BATCH_SIZE,
): Promise<CompactSalesBoardProduct[]> {
  const rows: CompactSalesBoardProduct[] = []
  const seen = new Set<string>()
  let total = 0
  let maxPages = 1
  for (let pageIndex = 1; pageIndex <= maxPages; pageIndex++) {
    checkCancelled(signal)
    const page = await fetchPage(pageIndex, batchSize, signal)
    checkCancelled(signal)
    if (pageIndex === 1) {
      total = Math.max(0, page.total)
      maxPages = Math.ceil(total / batchSize) + 1
    }
    for (const row of page.data) {
      if (seen.has(row.productCode)) continue
      seen.add(row.productCode)
      rows.push(row)
    }
    onProgress?.({ text: `正在读取商品 ${Math.min(rows.length, total)}/${total}`, percent: total > 0 ? Math.min(1, rows.length / total) : 1 })
    if (page.data.length < batchSize || rows.length >= total) break
  }
  return rows
}

const COLUMNS: Partial<ExcelJS.Column>[] = [
  { header: '排名', key: 'rank', width: 7 },
  { header: '商品图片', key: 'image', width: 14 },
  { header: '货号', key: 'itemNumber', width: 18 },
  { header: '商品名称', key: 'name', width: 38 },
  { header: '国内供应商代码', key: 'supplierCode', width: 15 },
  { header: '国内供应商', key: 'supplierName', width: 22 },
  { header: '数量', key: 'quantity', width: 12 },
  { header: '单价', key: 'unitPrice', width: 12 },
  { header: '金额', key: 'amount', width: 15 },
  { header: '占比', key: 'share', width: 10 },
]

/** 返回可检查的工作簿；图片缺失时保留销售数据并在图片列注明，超出嵌图上限的行只导出数据。 */
export async function buildCompactBoardWorkbook(rows: readonly CompactSalesBoardProduct[], options: CompactBoardWorkbookOptions) {
  checkCancelled(options.signal)
  const { default: ExcelJS } = await import('exceljs')
  const workbook = new ExcelJS.Workbook()
  const worksheet = workbook.addWorksheet('国内商品明细')
  const imageRowLimit = Math.max(0, Math.min(options.imageRowLimit ?? MAX_PRODUCT_IMAGE_EXPORT_ROWS, rows.length))
  worksheet.columns = COLUMNS
  worksheet.getRow(1).values = ['销售看板 · 国内商品明细']
  worksheet.mergeCells(1, 1, 1, COLUMNS.length)
  worksheet.getCell(1, 1).font = { bold: true, size: 15, color: { argb: 'FF143452' } }
  const notes = [
    `${options.startDate} ~ ${options.endDate}`,
    `${rows.length} 件商品`,
    options.filterLabel,
    options.sortLabel,
    options.scopeLabel,
    ...(rows.length > imageRowLimit ? [`前 ${imageRowLimit} 行含图片`] : []),
  ]
  worksheet.getRow(2).values = [notes.join('  ·  ')]
  const header = worksheet.getRow(3)
  header.values = COLUMNS.map(column => String(column.header ?? ''))
  header.height = 28
  header.eachCell(cell => {
    cell.font = { bold: true, color: { argb: 'FFFFFFFF' } }
    cell.fill = { type: 'pattern', pattern: 'solid', fgColor: { argb: 'FF23476C' } }
    cell.alignment = { vertical: 'middle' }
  })
  worksheet.views = [{ state: 'frozen', ySplit: 3 }]
  worksheet.autoFilter = { from: { row: 3, column: 1 }, to: { row: 3, column: COLUMNS.length } }

  const columnNumber = (key: string) => worksheet.getColumn(key).number
  rows.forEach((product, index) => {
    const row = worksheet.addRow({
      rank: options.firstRank + index,
      itemNumber: product.itemNumber || product.productCode,
      name: product.productName || product.productCode,
      supplierCode: product.chinaSupplierCode ?? '',
      supplierName: product.chinaSupplierName ?? '',
      quantity: product.totalQuantity,
      unitPrice: product.unitPrice,
      amount: product.totalAmount,
      share: options.scopeAmount > 0 ? product.totalAmount / options.scopeAmount : null,
    })
    // 嵌图行给足高度放 74px 缩略图，其余行保持普通行高，避免上千行空白。
    row.height = index < imageRowLimit ? 66 : 20
    row.alignment = { vertical: 'middle' }
    row.getCell(columnNumber('quantity')).numFmt = '#,##0'
    row.getCell(columnNumber('unitPrice')).numFmt = '$#,##0.00'
    row.getCell(columnNumber('amount')).numFmt = '$#,##0.00;[Red]($#,##0.00)'
    row.getCell(columnNumber('share')).numFmt = '0.0%'
    if (index % 2 === 1) row.fill = { type: 'pattern', pattern: 'solid', fgColor: { argb: 'FFF7FAFD' } }
  })

  const loadImage = options.loadImage ?? defaultImageLoader
  const imageColumn = columnNumber('image')
  const imageIds = new Map<string, number | null>()
  let failedImages = 0
  for (let start = 0; start < imageRowLimit; start += IMAGE_BATCH_SIZE) {
    checkCancelled(options.signal)
    const batch = rows.slice(start, Math.min(start + IMAGE_BATCH_SIZE, imageRowLimit))
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
        worksheet.getRow(rowIndex).getCell(imageColumn).value = product.productImage ? '图片读取失败' : '无图片'
      }
    })
    const done = Math.min(start + IMAGE_BATCH_SIZE, imageRowLimit)
    options.onProgress?.({ text: `正在处理图片 ${done}/${imageRowLimit}`, percent: done / imageRowLimit })
  }
  return { workbook, failedImages, imageRows: imageRowLimit }
}

export async function exportCompactBoardProducts(rows: readonly CompactSalesBoardProduct[], options: CompactBoardExportOptions) {
  const result = await buildCompactBoardWorkbook(rows, options)
  checkCancelled(options.signal)
  options.onProgress?.({ text: '正在生成 Excel 文件…' })
  options.onFinalize?.()
  const buffer = await result.workbook.xlsx.writeBuffer()
  checkCancelled(options.signal)
  const blob = new Blob([buffer], { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' })
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = `销售看板_国内商品明细_${options.startDate}_${options.endDate}_${options.fileSuffix}.xlsx`
  document.body.appendChild(link)
  link.click()
  link.remove()
  setTimeout(() => URL.revokeObjectURL(url), 60_000)
  return { count: rows.length, failedImages: result.failedImages, imageRows: result.imageRows }
}
