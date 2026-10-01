import ExcelJS from 'exceljs'
import { readFileSync } from 'node:fs'
import {
  addContainerDetailWorksheetImages,
  buildDatedExportFileName,
  calculateContainerExportImageSize,
  mapContainerExportProgress,
  populateContainerDetailsWorksheet,
  resolveContainerDetailPdfLayout,
  type ContainerDetailExportColumn,
} from './exportService'

function assertEqual<T>(actual: T, expected: T, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

const workbook = new ExcelJS.Workbook()
const worksheet = workbook.addWorksheet('货柜明细')

const columns: ContainerDetailExportColumn[] = [
  { header: '序号', key: 'index', width: 8, valueType: 'integer' },
  { header: '件数', key: 'containerPieces', width: 12, valueType: 'integer' },
  { header: '单件体积', key: 'unitVolume', width: 12, valueType: 'volume' },
  { header: '国内价格', key: 'domesticPrice', width: 12, valueType: 'money' },
]

const result = populateContainerDetailsWorksheet(
  worksheet,
  [
    {
      index: 1,
      containerPieces: 12,
      unitVolume: 0.123456,
      domesticPrice: 4.5,
    },
  ],
  {
    columns,
    summary: {
      title: '货柜主表信息',
      rows: [
        [
          { label: '货柜编号', value: 'CSNU6137731' },
          { label: '运费', value: 12000, valueType: 'money' },
        ],
        [
          { label: '总体积', value: 69.053, valueType: 'volume' },
        ],
      ],
    },
  },
)

assertEqual(result.headerRowNumber, 5, '主表信息和空行之后应写入明细表头')
assertEqual(worksheet.getCell('A1').value, '货柜主表信息', 'summary 标题应写在首行')
assertEqual(worksheet.getCell('A2').value, '货柜编号', 'summary 应写在明细表头上方')
assertEqual(worksheet.getCell('D2').numFmt, '$#,##0.00', 'summary 金额应保留 2 位小数')
assertEqual(worksheet.getCell('B3').numFmt, '#,##0.0000', 'summary 体积应保留 4 位小数')
assertEqual(worksheet.getRow(result.headerRowNumber).getCell(1).value, '序号', '明细表头应在返回的表头行')
assertEqual(worksheet.getRow(result.dataStartRowNumber).getCell(1).value, 1, '明细数据应写在表头下一行')
assertEqual(worksheet.getColumn('index').numFmt, undefined, '列级格式不应污染整列元数据')
assertEqual(worksheet.getRow(result.dataStartRowNumber).getCell(1).numFmt, '#,##0', '数量应使用整数格式')
assertEqual(worksheet.getRow(result.dataStartRowNumber).getCell(3).numFmt, '#,##0.0000', '体积应使用 4 位小数格式')
assertEqual(worksheet.getRow(result.dataStartRowNumber).getCell(4).numFmt, '¥#,##0.00', '国内价格应使用人民币 2 位小数格式')
assertEqual((worksheet.views[0] as { ySplit?: number } | undefined)?.ySplit, result.headerRowNumber, '冻结行应落在明细表头行')

const imageWorkbook = new ExcelJS.Workbook()
const imageWorksheet = imageWorkbook.addWorksheet('货柜明细图片列')
const imageResult = populateContainerDetailsWorksheet(
  imageWorksheet,
  [
    {
      index: 1,
      itemNumber: 'HB202-181',
      barcode: '9527902200518',
      barcodeImage: '9527902200518',
      productImage: 'https://cdn.example.com/HB202-181.jpg',
    },
  ],
  {
    columns: [
      { header: '序号', key: 'index', width: 8, valueType: 'integer' },
      { header: '条码图片', key: 'barcodeImage', width: 24, valueType: 'text' },
      { header: '商品图片', key: 'productImage', width: 18, valueType: 'text' },
      { header: '货号', key: 'itemNumber', width: 18, valueType: 'text' },
    ],
  },
)
assertEqual(imageResult.barcodeImageColIndex, 1, '条码图片列应返回零基列号')
assertEqual(imageResult.productImageColIndex, 2, '商品图片列应返回零基列号')
assertEqual(imageWorksheet.getRow(imageResult.dataStartRowNumber).height, 58, '选择图片列时应增加 Excel 行高')

const embeddedImageWorkbook = new ExcelJS.Workbook()
const embeddedImageWorksheet = embeddedImageWorkbook.addWorksheet('去重商品图片')
const embeddedImageItems = [
  { index: 1, productImage: 'https://cdn.example.com/shared.png' },
  { index: 2, productImage: 'https://cdn.example.com/shared.png' },
  { index: 3, productImage: 'https://cdn.example.com/failed.png' },
  { index: 4, productImage: '' },
]
const embeddedImageWorksheetInfo = populateContainerDetailsWorksheet(
  embeddedImageWorksheet,
  embeddedImageItems,
  {
    columns: [
      { header: '序号', key: 'index', width: 8, valueType: 'integer' },
      { header: '商品图片', key: 'productImage', width: 18, valueType: 'text' },
    ],
  },
)
const onePixelPng = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII='
const embeddedImageResult = addContainerDetailWorksheetImages(
  embeddedImageWorkbook,
  embeddedImageWorksheet,
  embeddedImageItems,
  embeddedImageWorksheetInfo,
  {
    barcodeImageMap: new Map(),
    productImageMap: new Map([['https://cdn.example.com/shared.png', onePixelPng]]),
    productImageSizeMap: new Map([[
      'https://cdn.example.com/shared.png',
      { width: 160, height: 80 },
    ]]),
    productImageKeyByRow: new Map([
      [0, 'https://cdn.example.com/shared.png'],
      [1, 'https://cdn.example.com/shared.png'],
    ]),
    failedProductImageRows: new Set([2]),
    missingProductImageRows: new Set([3]),
  },
  {
    productImageDownloadFailedText: 'Image download failed',
    productImageMissingText: 'No image',
  },
)
assertEqual(embeddedImageWorksheet.getImages().length, 2, '同一商品图片应嵌入到每一条明细行')
const firstEmbeddedImageRange = embeddedImageWorksheet.getImages()[0]?.range as unknown as {
  ext?: { width?: number; height?: number }
}
assertEqual(
  JSON.stringify(firstEmbeddedImageRange.ext),
  JSON.stringify({ width: 72, height: 36 }),
  'Excel 商品图片应在单元格显示范围内保持原始宽高比，不得拉伸铺满单元格',
)
assertEqual(
  embeddedImageWorksheet.getRow(embeddedImageWorksheetInfo.dataStartRowNumber).getCell(2).value,
  null,
  '已嵌入图片的单元格不应泄露原始或签名 URL',
)
assertEqual(
  ((embeddedImageWorkbook as unknown as { model: { media?: unknown[] } }).model.media?.length ?? 0),
  1,
  '相同图片 URL 应只创建一个工作簿媒体资源，避免重复放大文件',
)
assertEqual(embeddedImageResult.embeddedProductImageCount, 2, '图片嵌入结果应统计成功行数')
assertEqual(embeddedImageResult.failedProductImageCount, 1, '图片嵌入结果应统计下载失败行数')
assertEqual(embeddedImageResult.missingProductImageCount, 1, '图片嵌入结果应统计无图行数')
assertEqual(
  embeddedImageWorksheet.getRow(embeddedImageWorksheetInfo.dataStartRowNumber + 2).getCell(2).value,
  'Image download failed',
  '下载失败的商品图片单元格应使用调用方提供的本地化提示',
)
assertEqual(
  embeddedImageWorksheet.getRow(embeddedImageWorksheetInfo.dataStartRowNumber + 3).getCell(2).value,
  'No image',
  '没有图片地址的商品图片单元格应使用调用方提供的本地化提示',
)
assertEqual(
  JSON.stringify(calculateContainerExportImageSize(320, 160)),
  JSON.stringify({ width: 160, height: 80 }),
  '商品图片缩略图应按比例限制在 160×160 内',
)
const embeddedImageWorkbookBuffer = await embeddedImageWorkbook.xlsx.writeBuffer()
assertEqual(embeddedImageWorkbookBuffer.byteLength > 0, true, '复用同一图片资源后工作簿仍应能成功序列化')
const reloadedImageWorkbook = new ExcelJS.Workbook()
await reloadedImageWorkbook.xlsx.load(embeddedImageWorkbookBuffer)
assertEqual(
  reloadedImageWorkbook.getWorksheet('去重商品图片')?.getImages().length,
  2,
  '工作簿重新读取后应保留相同媒体资源在两行中的图片定位',
)

const plainWorkbook = new ExcelJS.Workbook()
const plainWorksheet = plainWorkbook.addWorksheet('货柜明细普通列')
const plainResult = populateContainerDetailsWorksheet(
  plainWorksheet,
  [{ index: 1, itemNumber: 'HB202-181' }],
  {
    columns: [
      { header: '序号', key: 'index', width: 8, valueType: 'integer' },
      { header: '货号', key: 'itemNumber', width: 18, valueType: 'text' },
    ],
  },
)
assertEqual(plainResult.barcodeImageColIndex, -1, '未选择条码图片列时不应生成图片列')
assertEqual(plainResult.productImageColIndex, -1, '未选择商品图片列时不应生成图片列')
assertEqual(plainWorksheet.getRow(plainResult.dataStartRowNumber).height, undefined, '未选择图片列时应保持普通行高')
assertEqual(mapContainerExportProgress(-10, 10, 55), 10, '导出进度映射应截断负数')
assertEqual(mapContainerExportProgress(100, 55, 90), 90, '导出进度映射应到达区间终点')
assertEqual(
  mapContainerExportProgress(20, 55, 90) > mapContainerExportProgress(65, 10, 55),
  true,
  '图片准备后的写表进度应继续向前，不能回退',
)

const exportServiceSource = readFileSync('src/services/exportService.ts', 'utf8')
const sixPdfColumns: ContainerDetailExportColumn[] = [
  { header: '序号', key: 'index', width: 8, valueType: 'integer' },
  { header: '货号', key: 'itemNumber', width: 18, valueType: 'text' },
  { header: '条码图片', key: 'barcodeImage', width: 24, valueType: 'text' },
  { header: '英文名称', key: 'englishName', width: 36, valueType: 'text' },
  { header: '零售价', key: 'oemPrice', width: 12, valueType: 'money' },
  { header: '备注', key: 'remark', width: 18, valueType: 'text' },
]
const portraitLayout = resolveContainerDetailPdfLayout(sixPdfColumns)
assertEqual(portraitLayout.orientation, 'p', '6 列及以下货柜明细 PDF 应使用竖向 A4')
assertEqual(portraitLayout.pageWidthPx, 793, '竖向 PDF 页面 DOM 宽度应匹配 A4 竖向比例')
assertEqual(portraitLayout.pageHeightPx, 1122, '竖向 PDF 页面 DOM 高度应匹配 A4 竖向比例')
assertEqual(portraitLayout.pdfWidthMm, 210, '竖向 PDF 写入宽度应为 210mm')
assertEqual(portraitLayout.pdfHeightMm, 297, '竖向 PDF 写入高度应为 297mm')
assertEqual(portraitLayout.rowsPerPageWithImages, 15, '竖向带图片 PDF 应按可扫码条码高度分页')
assertEqual(portraitLayout.rowsPerPageWithoutImages, 36, '竖向无图片 PDF 应使用更多每页行数')

const defaultPdfLayout = resolveContainerDetailPdfLayout([
  { header: '序号', key: 'index', width: 8, valueType: 'integer' },
  { header: '商品图片', key: 'productImage', width: 18, valueType: 'text' },
  { header: '货号', key: 'itemNumber', width: 18, valueType: 'text' },
  { header: '条码图片', key: 'barcodeImage', width: 24, valueType: 'text' },
  { header: '英文名称', key: 'englishName', width: 36, valueType: 'text' },
  { header: '零售价', key: 'oemPrice', width: 12, valueType: 'money' },
])
assertEqual(defaultPdfLayout.orientation, 'p', '货柜明细 PDF 默认 6 列应导出为竖向 A4')

const landscapeLayout = resolveContainerDetailPdfLayout([
  ...sixPdfColumns,
  { header: '总装柜数', key: 'containerQuantity', width: 12, valueType: 'integer' },
])
assertEqual(landscapeLayout.orientation, 'l', '7 列及以上货柜明细 PDF 应使用横向 A4')
assertEqual(landscapeLayout.pageWidthPx, 1122, '横向 PDF 页面 DOM 宽度应匹配 A4 横向比例')
assertEqual(landscapeLayout.pageHeightPx, 793, '横向 PDF 页面 DOM 高度应匹配 A4 横向比例')
assertEqual(landscapeLayout.pdfWidthMm, 297, '横向 PDF 写入宽度应为 297mm')
assertEqual(landscapeLayout.pdfHeightMm, 210, '横向 PDF 写入高度应为 210mm')
assertEqual(landscapeLayout.rowsPerPageWithImages, 11, '横向带图片 PDF 应保留原每页行数')
assertEqual(landscapeLayout.rowsPerPageWithoutImages, 18, '横向无图片 PDF 应保留原每页行数')
assertEqual(exportServiceSource.includes("import('jspdf')"), true, '货柜明细 PDF 导出应动态加载 jspdf，避免增加首屏包')
assertEqual(exportServiceSource.includes('new jsPDF(layout.orientation'), true, '货柜明细 PDF 应按列数自动传入横竖向')
assertEqual(exportServiceSource.includes('layout.pdfWidthMm, layout.pdfHeightMm'), true, '货柜明细 PDF 写入尺寸应来自自动布局')
assertEqual(
  exportServiceSource.includes('scale: 3'),
  true,
  'PDF 页面截图应提高倍率，减少条码边缘模糊',
)
assertEqual(exportServiceSource.includes("canvas.toDataURL('image/png')"), true, 'PDF 页面应使用 PNG 写入，避免条码被 JPEG 压缩')
assertEqual(exportServiceSource.includes("pdf.addImage(imageData, 'PNG'"), true, 'PDF 应以无损 PNG 图片写入页面')
assertEqual(
  exportServiceSource.includes('width: 3, height: 74, fontSize: 13, margin: 8'),
  true,
  'PDF 条码源图应生成得足够清晰，保证可扫码',
)
assertEqual(exportServiceSource.includes('width: 138px'), true, 'PDF 条码图片应放大显示')
assertEqual(exportServiceSource.includes('height: 58px'), true, 'PDF 条码图片应保持足够显示高度')
assertEqual(
  exportServiceSource.includes("pdf.save(buildDatedExportFileName(options.fileName || '货柜明细', 'pdf'))"),
  true,
  '货柜明细 PDF 导出文件名应使用 .pdf 后缀并带悉尼日期',
)
assertEqual(
  exportServiceSource.includes("toISOString().split('T')[0]"),
  false,
  '导出文件名不得用 toISOString 截取 UTC 日期',
)

function formatMinuteOfDay(minute: number) {
  return `${String(Math.floor(minute / 60)).padStart(2, '0')}:${String(minute % 60).padStart(2, '0')}`
}

// 悉尼 00:00–09:59（AEST，UTC+10）逐分钟导出：UTC 还停在前一天，文件名仍应带悉尼当天日期。
const aestMidnightUtc = Date.parse('2026-09-28T14:00:00Z') // 悉尼 2026-09-29 00:00
for (let minute = 0; minute < 10 * 60; minute += 1) {
  assertEqual(
    buildDatedExportFileName('0OLU9935964_商品明细', 'xlsx', new Date(aestMidnightUtc + minute * 60_000)),
    '0OLU9935964_商品明细_2026-09-29.xlsx',
    `悉尼 ${formatMinuteOfDay(minute)} 导出的货柜明细 Excel 文件名应带悉尼当天日期`,
  )
}

// 夏令时（AEDT，UTC+11）受影响时段延长到 00:00–10:59。
const aedtMidnightUtc = Date.parse('2026-12-14T13:00:00Z') // 悉尼 2026-12-15 00:00
for (let minute = 0; minute < 11 * 60; minute += 1) {
  assertEqual(
    buildDatedExportFileName('货柜明细', 'pdf', new Date(aedtMidnightUtc + minute * 60_000)),
    '货柜明细_2026-12-15.pdf',
    `夏令时悉尼 ${formatMinuteOfDay(minute)} 导出的货柜明细 PDF 文件名应带悉尼当天日期`,
  )
}

assertEqual(
  buildDatedExportFileName('仓库商品', 'xlsx', new Date('2026-09-28T13:59:00Z')),
  '仓库商品_2026-09-28.xlsx',
  '悉尼前一天 23:59 导出仍应带前一天日期，不能一律加一天',
)
