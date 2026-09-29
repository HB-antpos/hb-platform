import assert from 'node:assert/strict'
import ExcelJS from 'exceljs'
import { getImageDownloadCandidates } from '../../../services/exportService'
import type { CompactSalesBoardProduct } from '../../../services/salesDashboardService'
import {
  buildCompactBoardWorkbook,
  collectAllCompactBoardProducts,
  COMPACT_BOARD_IMAGE_PROXY,
  type CompactBoardExportProgress,
} from './export'

const product = (index: number, image = ''): CompactSalesBoardProduct => ({
  productCode: `P${index}`,
  itemNumber: `00${index}`,
  productName: `商品 ${index}`,
  productImage: image,
  chinaSupplierCode: 'HB215',
  chinaSupplierName: '玛索文具',
  totalQuantity: 10 * index,
  unitPrice: 2.5,
  totalAmount: 25 * index,
})
const controller = new AbortController()

// 看板用户未必有销售明细权限：图片走按看板权限放行的代理路由。
const proxied = getImageDownloadCandidates('https://hotbargain-yw-2023-1300114625.cos.ap-shanghai.myqcloud.com/a.jpg', COMPACT_BOARD_IMAGE_PROXY)
assert.match(proxied[0], /^\/api\/react\/v1\/image-proxy\/compact-sales-board\?url=/)

// ---------- 导出全部结果：按 500 行一批读取，去重，数据变化时不会无限翻页 ----------
const universe = Array.from({ length: 1203 }, (_, index) => product(index + 1))
const requested: Array<[number, number]> = []
const progress: CompactBoardExportProgress[] = []
const all = await collectAllCompactBoardProducts(async (pageIndex, pageSize) => {
  requested.push([pageIndex, pageSize])
  return { data: universe.slice((pageIndex - 1) * pageSize, pageIndex * pageSize), total: universe.length }
}, controller.signal, (item) => progress.push(item))
assert.deepEqual(requested, [[1, 500], [2, 500], [3, 500]], '1203 行应分 3 批、每批 500 行读取')
assert.equal(all.length, 1203)
assert.deepEqual(all.slice(0, 2).map((row) => row.productCode), ['P1', 'P2'], '保持服务端排序')
assert.equal(progress[progress.length - 1]?.text, '正在读取商品 1203/1203')

// 分批期间整点重算导致行位次挪动：重复的商品只保留一次，最多多读一批就停止。
const shifted: number[] = []
const deduped = await collectAllCompactBoardProducts(async (pageIndex) => {
  shifted.push(pageIndex)
  // 每批都与上一批重叠 1 行，并且总是返回满批，模拟排序漂移
  const start = (pageIndex - 1) * 2 - (pageIndex > 1 ? 1 : 0)
  return { data: universe.slice(start, start + 2), total: 5 }
}, controller.signal, undefined, 2)
assert.deepEqual(deduped.map((row) => row.productCode), ['P1', 'P2', 'P3', 'P4', 'P5'], '重叠的行必须去重')
assert.ok(shifted.length <= 4, '最多只多读一批（ceil(5/2)+1 = 4）')

const emptyResult = await collectAllCompactBoardProducts(async () => ({ data: [], total: 0 }), controller.signal)
assert.deepEqual(emptyResult, [], '没有结果时直接返回空')

const cancelled = new AbortController()
cancelled.abort()
await assert.rejects(collectAllCompactBoardProducts(async () => ({ data: [], total: 0 }), cancelled.signal), { name: 'AbortError' })

// ---------- 工作簿：列、数值格式、占比、嵌图上限 ----------
const png = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO/aAooAAAAASUVORK5CYII='
const loaded: string[] = []
const rows = [
  product(1, 'https://example.com/one.png'),
  product(2, 'https://example.com/missing.png'),
  product(3),
  product(4, 'https://example.com/four.png'),
]
const { workbook, failedImages, imageRows } = await buildCompactBoardWorkbook(rows, {
  startDate: '2025-09-25',
  endDate: '2026-09-24',
  filterLabel: '国内供应商 玛索文具（HB215）',
  sortLabel: '按金额降序',
  scopeLabel: '全部结果',
  scopeAmount: 1000,
  firstRank: 51,
  imageRowLimit: 3,
  signal: controller.signal,
  loadImage: async (url) => { loaded.push(url); return url.endsWith('one.png') ? png : null },
})
assert.equal(failedImages, 1, '有图片地址但读取失败的行计入失败数')
assert.equal(imageRows, 3)
assert.deepEqual(loaded, ['https://example.com/one.png', 'https://example.com/missing.png'], '只给前 3 行取图，第 4 行超出上限不取')

const roundTrip = new ExcelJS.Workbook()
await roundTrip.xlsx.load(await workbook.xlsx.writeBuffer() as ArrayBuffer)
const sheet = roundTrip.worksheets[0]!
const column = (header: string) => {
  const index = Array.from({ length: sheet.columnCount }, (_, offset) => sheet.getRow(3).getCell(offset + 1).value).indexOf(header)
  assert.ok(index >= 0, `缺少列：${header}`)
  return index + 1
}
assert.equal(sheet.getCell('A1').value, '销售看板 · 国内商品明细')
assert.match(String(sheet.getCell('A2').value), /2025-09-25 ~ 2026-09-24 {2}· {2}4 件商品 {2}· {2}国内供应商 玛索文具（HB215） {2}· {2}按金额降序 {2}· {2}全部结果 {2}· {2}前 3 行含图片/)
assert.equal(sheet.getRow(4).getCell(column('排名')).value, 51, '导出本页时排名从页偏移开始')
assert.equal(sheet.getRow(4).getCell(column('货号')).value, '001', '货号前导零必须保留')
assert.equal(sheet.getRow(4).getCell(column('国内供应商代码')).value, 'HB215')
assert.equal(sheet.getRow(4).getCell(column('金额')).value, 25)
assert.equal(sheet.getRow(4).getCell(column('金额')).numFmt, '$#,##0.00;[Red]($#,##0.00)')
assert.equal(sheet.getRow(4).getCell(column('占比')).value, 0.025, '占比分母与页面一致（商品栏范围合计）')
assert.equal(sheet.getRow(5).getCell(column('商品图片')).value, '图片读取失败')
assert.equal(sheet.getRow(6).getCell(column('商品图片')).value, '无图片')
assert.equal(sheet.getRow(7).getCell(column('商品图片')).value, null, '超出嵌图上限的行不写图片说明')
assert.equal(sheet.getRow(6).height, 66, '嵌图行留出缩略图高度')
assert.equal(sheet.getRow(7).height, 20, '超出上限的行保持普通行高')
assert.equal(sheet.getImages().length, 1, 'Excel 文件须真实嵌入图片')

const noScope = await buildCompactBoardWorkbook([product(1)], {
  startDate: '2026-09-24', endDate: '2026-09-24', filterLabel: '全部国内供应商 · 全部分店', sortLabel: '按金额降序',
  scopeLabel: '第 1 页（每页 50 行）', scopeAmount: 0, firstRank: 1, signal: controller.signal, loadImage: async () => null,
})
assert.equal(noScope.workbook.worksheets[0]!.getRow(4).getCell(10).value, null, '分母为 0 时占比留空，不写成 0')

await assert.rejects(buildCompactBoardWorkbook(rows, {
  startDate: '2026-09-24', endDate: '2026-09-24', filterLabel: '', sortLabel: '', scopeLabel: '',
  scopeAmount: 1, firstRank: 1, signal: cancelled.signal,
}), { name: 'AbortError' })

console.log('独立销售看板导出：分批读取、去重、Excel 数值与嵌图上限通过')
