import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import path from 'node:path'

import type {
  LocalSupplierInvoiceItemDto,
  ProductCheckResult,
  ProductCheckSuggestedProduct,
} from '../../../../types/localSupplierInvoice'
import { BarcodeStatus, ProductStatus } from '../../../../types/localSupplierInvoice'

import {
  describeSuggestedProduct,
  getRowSuggestedProducts,
  mergeSuggestedExistingProducts,
  type SuggestedExistingByDetail,
} from './suggestedExistingProducts'

function suggested(productCode: string, overrides: Partial<ProductCheckSuggestedProduct> = {}): ProductCheckSuggestedProduct {
  return { productCode, itemNumber: `I-${productCode}`, productName: `Name ${productCode}`, localSupplierCode: '250', ...overrides }
}

function checkResult(detailGuid: string, suggestions?: ProductCheckSuggestedProduct[]): ProductCheckResult {
  return {
    detailGuid,
    productStatus: ProductStatus.NotExists,
    barcodeStatus: BarcodeStatus.Normal,
    existingProductCount: 0,
    suggestedExistingProducts: suggestions,
  }
}

function detail(overrides: Partial<LocalSupplierInvoiceItemDto>): LocalSupplierInvoiceItemDto {
  return { detailGUID: 'd1', barcode: '931', existingProductCount: 0, ...overrides } as LocalSupplierInvoiceItemDto
}

// ---- 合并：以最新检测结果为准 ----
const first = mergeSuggestedExistingProducts({}, [
  checkResult('d1', [suggested('p1'), suggested('p2')]),
  checkResult('d2', []),
  checkResult('d3'),
])
assert.deepEqual(Object.keys(first), ['d1'], '没有建议的行不进表')
assert.equal(first.d1.length, 2)

// 没出现在本次结果里的行保持不动；出现且建议为空的行（已选用 / 条码改了）清掉旧提示。
const second = mergeSuggestedExistingProducts(first, [checkResult('d9', [suggested('p9')]), checkResult('d1', [])])
assert.deepEqual(Object.keys(second), ['d9'], '重新检测后旧提示被撤掉，新行加入')
assert.equal(first.d1.length, 2, '不修改传入的旧表')

// 没有商品编码的建议没法选用，直接丢弃，避免出现空提示。
const noCode = mergeSuggestedExistingProducts({}, [checkResult('d1', [{ productName: 'x' }, suggested(' ')])])
assert.deepEqual(noCode, {})

// ---- 行内展示：只给主档不存在的行 ----
const table: SuggestedExistingByDetail = { d1: [suggested('p1')] }
assert.equal(getRowSuggestedProducts(detail({ detailGUID: 'd1' }), table).length, 1)
assert.equal(
  getRowSuggestedProducts(detail({ detailGUID: 'd1', existingProductCount: 1, productCode: 'p1' }), table).length,
  0,
  '已选用商品（主档已存在）的行不再提示',
)
assert.equal(
  getRowSuggestedProducts(detail({ detailGUID: 'd1', existingProductCount: undefined }), table).length,
  0,
  '未检测的行不提示',
)
assert.equal(
  getRowSuggestedProducts(detail({ detailGUID: 'd1', isCreatedByThisInvoice: true }), table).length,
  0,
  '本单新品不提示',
)
assert.equal(getRowSuggestedProducts(detail({ detailGUID: 'other' }), table).length, 0)

// ---- 文案参数：名称缺失退回货号 / 商品编码，供应商缺失用 -- ----
assert.deepEqual(describeSuggestedProduct(suggested('p1')), { name: 'Name p1', supplier: '250' })
assert.deepEqual(
  describeSuggestedProduct({ productCode: 'p2', itemNumber: '62800' }),
  { name: '62800', supplier: '--' },
)
assert.deepEqual(describeSuggestedProduct({ productCode: 'p3' }), { name: 'p3', supplier: '--' })

// ---- 页面契约：每次检测结果都要刷新提示；提示点开的是现有的匹配商品弹窗（含「选用」），不自动关联 ----
const pageSource = readFileSync(
  path.resolve(process.cwd(), 'src/pages/PosAdmin/LocalSupplierInvoices/InvoiceEdit/index.tsx'),
  'utf8',
)
const applySource = pageSource.match(/const applyCheckProductsResponse = [\s\S]*?\n  \}\n\n  const handleUpdateLastPurchasePrices/)?.[0] ?? ''
assert.match(applySource, /mergeSuggestedExistingProducts\(prev, result\.results\)/, '检测结果必须刷新建议选用提示')
const hintSource = pageSource.match(/const suggestedHint = [\s\S]*?\}\)\(\) : null/)?.[0] ?? ''
assert.ok(hintSource, '必须有行内建议选用提示')
assert.match(hintSource, /onClick=\{openMatchedProducts\}/, '提示复用现有的匹配商品弹窗（里面有「选用」）')
assert.doesNotMatch(hintSource, /batchUpsertDetails|checkProducts\(/, '提示本身不自动关联商品')

console.log('suggestedExistingProducts tests passed')
