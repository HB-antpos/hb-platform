import assert from 'node:assert/strict'

import ExcelJS from 'exceljs'

import type { SeasonalCardStatsSummary } from '../../../types/seasonalCardStats'

import { exportSeasonalCardStatsWorkbook, type TextFn } from './export'
import zhMessages from './messages.zh.json'

// 用真实的中文消息文件做文案替身：键不存在或占位符没传都直接报错，顺便核对文案与代码一致。
const text: TextFn = (key, params = {}) => {
  let node: unknown = zhMessages
  for (const segment of key.split('.')) node = (node as Record<string, unknown> | undefined)?.[segment]
  assert.equal(typeof node, 'string', `缺少文案键 ${key}`)
  return (node as string).replace(/\{\{(\w+)\}\}/g, (_, name: string) => {
    assert.ok(name in params, `${key} 缺少参数 ${name}`)
    return String(params[name])
  })
}

const prices = (q: [number, number, number, number], amount4: number) => [
  { priceOption: 1 as const, priceLabel: '$1', quantity: q[0], amount: q[0] },
  { priceOption: 2 as const, priceLabel: '$2', quantity: q[1], amount: q[1] * 2 },
  { priceOption: 3 as const, priceLabel: '$3', quantity: q[2], amount: q[2] * 3 },
  { priceOption: 4 as const, priceLabel: '其他', quantity: q[3], amount: amount4 },
]

const summary: SeasonalCardStatsSummary = {
  seasonYear: 2026,
  cardType: 1,
  cardTypeName: '圣诞节',
  storeCount: 3,
  filledStoreCount: 2,
  unfilledStoreCount: 1,
  totalQuantity: 300,
  totalAmount: 0,
  stores: [
    {
      storeCode: '1011',
      storeName: 'Alpha',
      isFilled: true,
      prices: prices([120, 80, 30, 0], 0),
      totalQuantity: 230,
      totalAmount: 370,
      suppliers: [
        { localSupplierCode: 'A', supplierName: '供应商 A' },
        { localSupplierCode: null, supplierName: '未指定供应商' },
      ],
      lastSubmittedAt: '2026-10-08T03:32:00',
      lastSubmittedByName: 'Wang',
    },
    {
      storeCode: '1012',
      storeName: 'Beta',
      isFilled: false,
      prices: prices([0, 0, 0, 0], 0),
      totalQuantity: 0,
      totalAmount: 0,
      suppliers: [],
      lastSubmittedAt: null,
      lastSubmittedByName: null,
    },
    {
      storeCode: '1013',
      storeName: 'Gamma',
      isFilled: true,
      prices: prices([58, 0, 0, 12], 54),
      totalQuantity: 70,
      totalAmount: 112.1,
      suppliers: [{ localSupplierCode: 'A', supplierName: '供应商 A' }],
      lastSubmittedAt: '2026-10-07T23:05:00Z',
      lastSubmittedByName: 'Li',
    },
  ],
  priceTotals: prices([178, 80, 30, 12], 54),
  supplierTotals: [],
  unfilledStores: [{ storeCode: '1012', storeName: 'Beta' }],
  excludedStores: [
    { storeCode: '1006', storeName: 'HB Warehouse' },
    { storeCode: '1042', storeName: 'TestStore' },
  ],
}

let downloaded: { blob: Blob; fileName: string } | null = null
const result = await exportSeasonalCardStatsWorkbook(
  { summary, context: { seasonYear: 2026, holiday: '圣诞节', supplier: '全部供应商', price: '全部价格' }, text },
  {
    loadExcel: async () => ExcelJS,
    download: (blob, fileName) => {
      downloaded = { blob, fileName }
    },
    // 固定时区，避免受运行环境时区影响
    formatTime: (value) => (value ? `T:${value}` : ''),
  },
)

assert.equal(result.fileName, '贺卡分店填报统计_2026_圣诞节.xlsx', '文件名含年份和节日')
assert.ok(downloaded, '应触发下载')
const file = downloaded as unknown as { blob: Blob; fileName: string }
assert.equal(file.fileName, result.fileName)

const workbook = new ExcelJS.Workbook()
await workbook.xlsx.load(await file.blob.arrayBuffer())
assert.deepEqual(workbook.worksheets.map((sheet) => sheet.name), ['分店明细', '未填报分店'], '两个工作表')

const stores = workbook.getWorksheet('分店明细')!
assert.equal(stores.getCell('A1').value, '2026 圣诞节 · 分店贺卡填报统计')
assert.equal(stores.getCell('A2').value, '供应商：全部供应商 · 价格：全部价格 · 已填报 2 / 3 家')
const header = (stores.getRow(3).values as unknown[]).slice(1)
assert.deepEqual(header, [
  '分店编码',
  '分店名称',
  '状态',
  '$1',
  '$2',
  '$3',
  '其他价格',
  '合计数量',
  '合计金额',
  '供应商',
  '最后提交时间',
  '提交人',
])

const row4 = stores.getRow(4)
assert.equal(row4.getCell(1).value, '1011')
assert.equal(row4.getCell(3).value, '已填报')
assert.equal(row4.getCell(4).value, 120)
assert.equal(row4.getCell(9).value, 370)
assert.equal(row4.getCell(9).numFmt, '"$"#,##0.00', '金额列用 $ 格式')
assert.equal(row4.getCell(10).value, '供应商 A、未指定供应商')
assert.equal(row4.getCell(11).value, 'T:2026-10-08T03:32:00')
assert.equal(row4.getCell(12).value, 'Wang')

const row5 = stores.getRow(5)
assert.equal(row5.getCell(3).value, '未填报')
assert.equal(row5.getCell(4).value, null, '未填报分店数量留空，不写 0')
assert.equal(row5.getCell(9).value, null)
assert.equal((row5.getCell(1).fill as ExcelJS.FillPattern).fgColor?.argb, 'FFFFFBEB', '未填报行淡黄底')

const total = stores.getRow(7)
assert.equal(total.getCell(1).value, '合计')
assert.equal(total.getCell(3).value, '已填报 2 家')
assert.deepEqual([4, 5, 6, 7].map((column) => total.getCell(column).value), [178, 80, 30, 12])
assert.equal(total.getCell(8).value, 300)
assert.equal(total.getCell(9).value, 482.1, '合计金额按分求和')

assert.equal(stores.getCell('A9').value, text('seasonalCardStats.notes.latest'))
assert.equal(stores.getCell('A10').value, '应填报分店为启用中的分店，不含：1006 HB Warehouse、1042 TestStore。', '口径里注明排除的分店')

const unfilled = workbook.getWorksheet('未填报分店')!
assert.equal(unfilled.getCell('A1').value, '2026 圣诞节 · 未填报分店（1 家）')
assert.deepEqual((unfilled.getRow(2).values as unknown[]).slice(1), ['分店编码', '分店名称'])
assert.equal(unfilled.getCell('A3').value, '1012')
assert.equal(unfilled.getCell('B3').value, 'Beta')

// 全部已填报：未填报页给出说明而不是空表
let second: Blob | null = null
await exportSeasonalCardStatsWorkbook(
  {
    summary: { ...summary, unfilledStores: [], excludedStores: [] },
    context: { seasonYear: 2025, holiday: '复活节', supplier: '供应商 A', price: '$1' },
    text,
  },
  {
    loadExcel: async () => ExcelJS,
    download: (blob) => {
      second = blob
    },
  },
)
const workbook2 = new ExcelJS.Workbook()
await workbook2.xlsx.load(await (second as unknown as Blob).arrayBuffer())
assert.equal(workbook2.getWorksheet('未填报分店')!.getCell('A3').value, '全部分店都已填报')
assert.equal(workbook2.getWorksheet('分店明细')!.getCell('A10').value, '应填报分店为启用中的分店。')

console.log('SeasonalCardStats export.test: ok')
