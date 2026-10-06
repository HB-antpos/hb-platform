import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { ProductCreationType } from '../../../types/domesticProductCreation'
import type { BatchProductItem } from '../../../types/domesticProductCreation'
import { EXPORT_COLUMN_KEYS, getExportableBatchItems, toExportRows } from './exportBatchDetail'

const items: BatchProductItem[] = [
  {
    itemNumber: 'sub-2',
    hbProductNo: 'HB001-8001-02',
    barcode: '9527800100002',
    productName: '子项2',
    productType: ProductCreationType.SET_SUB_ITEM,
    privateLabelPrice: 8.5,
    parentItemNumber: 'HB001-8001',
  },
  {
    itemNumber: 'normal',
    hbProductNo: 'HB001-9001',
    barcode: '9527900100001',
    productName: '普通商品',
    productType: ProductCreationType.NORMAL,
    privateLabelPrice: 5,
  },
  {
    itemNumber: 'set',
    hbProductNo: 'HB001-8001',
    barcode: '9527800100001',
    productName: '套装商品',
    productType: ProductCreationType.SET,
    privateLabelPrice: 10,
    setQuantity: 2,
    setPrice: 39.99,
  },
  {
    itemNumber: 'sub-1',
    hbProductNo: 'HB001-8001-01',
    barcode: '9527800100003',
    productName: '子项1',
    productType: ProductCreationType.SET_SUB_ITEM,
    privateLabelPrice: 8.5,
    parentItemNumber: 'HB001-8001',
  },
  {
    itemNumber: 'sub-unmatched',
    hbProductNo: 'HB001-0000-01',
    barcode: '9527000000001',
    productName: '父货号异常子项',
    productType: ProductCreationType.SET_SUB_ITEM,
    privateLabelPrice: 9.5,
    parentItemNumber: 'HB001-MISSING',
  },
]

const exportableItems = getExportableBatchItems(items)

assert.deepEqual(
  exportableItems.map((item) => item.hbProductNo),
  ['HB001-8001', 'HB001-8001-01', 'HB001-8001-02', 'HB001-0000-01', 'HB001-9001'],
)
assert.equal(exportableItems[1].parentItemNumber, 'HB001-8001')
assert.equal(exportableItems[2].productType, ProductCreationType.SET_SUB_ITEM)
assert.equal(exportableItems[3].parentItemNumber, 'HB001-MISSING')

// 导出列顺序：前 5 列必须与「批次明细」抽屉的列（货号/条码/名称/类型/零售价）一致，界面与导出文件不错位。
assert.deepEqual(
  EXPORT_COLUMN_KEYS.slice(0, 5),
  ['itemNumber', 'barcode', 'productName', 'type', 'privateLabelPrice'],
)
const exportedRow = toExportRows(items)[0]
for (const key of EXPORT_COLUMN_KEYS) {
  // 条码图片列没有对应的文本值，其余每一列都必须能从导出行里取到值。
  if (key !== 'barcodeImage') assert.ok(key in exportedRow, `导出行缺少列 ${key}`)
}

const productionExcelSources = new Map([
  ['src/services/exportService.ts', 3],
  ['src/pages/DomesticPurchase/ProductCreation/exportBatchDetail.ts', 1],
  ['src/pages/Warehouse/StoreOrders/Invoice.tsx', 1],
  ['src/pages/Warehouse/StoreOrders/PickingList.tsx', 1],
])

for (const [sourcePath, expectedDynamicImports] of productionExcelSources) {
  const source = readFileSync(resolve(process.cwd(), sourcePath), 'utf8')
  assert.doesNotMatch(source, /^\s*import\b[^\n]*['"]exceljs['"]/m, `${sourcePath} 不得静态加载 ExcelJS`)
  assert.equal(
    source.match(/await\s+import\(['"]exceljs['"]\)/g)?.length,
    expectedDynamicImports,
    `${sourcePath} 每个 Excel 导出入口必须在操作内加载 ExcelJS`,
  )
}

console.log('exportBatchDetail.test: ok')
