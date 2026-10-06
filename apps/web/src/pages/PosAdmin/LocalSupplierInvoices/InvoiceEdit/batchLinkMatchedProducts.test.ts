import { readFileSync } from 'node:fs'
import path from 'node:path'

import type {
  BarcodeAbnormalMatchedProductDto,
  LocalSupplierInvoiceItemDto,
} from '../../../../types/localSupplierInvoice'

import {
  mapWithConcurrency,
  pickBatchLinkCandidates,
  resolveSingleMatchedProductCode,
} from './batchLinkMatchedProducts'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function detail(overrides: Partial<LocalSupplierInvoiceItemDto>): LocalSupplierInvoiceItemDto {
  return { detailGUID: 'd', barcode: '931', existingProductCount: 0, barcodeMatchCount: 1, ...overrides } as LocalSupplierInvoiceItemDto
}

function product(productCode: string, isMultiCode = false): BarcodeAbnormalMatchedProductDto {
  return { productCode, productName: productCode, supplierCode: 'S', barcode: '931', isMultiCode, isBundle: false }
}

async function main() {
  // ---- 初筛：主档不存在 + 有条码 + 条码匹配到过商品，且只看勾选行 ----
  const details = [
    detail({ detailGUID: 'ok' }),
    detail({ detailGUID: 'exists', existingProductCount: 1, productCode: 'P1' }),
    detail({ detailGUID: 'unchecked', existingProductCount: undefined }),
    detail({ detailGUID: 'no-match', barcodeMatchCount: 0 }),
    detail({ detailGUID: 'no-barcode', barcode: '  ' }),
    detail({ detailGUID: 'not-selected' }),
  ]
  const picked = pickBatchLinkCandidates(['ok', 'exists', 'unchecked', 'no-match', 'no-barcode'], details)
  assertEqual(picked.candidates.map((item) => item.detailGUID).join(','), 'ok', '只有主档不存在且条码有匹配的行进入候选')
  assertEqual(picked.ineligibleCount, 4, '其余勾选行计入不符合条件')

  // ---- 唯一匹配判定以去重商品编码为准 ----
  assertEqual(resolveSingleMatchedProductCode([product('P1')]), 'P1', '只匹配到一个商品时返回其编码')
  assertEqual(resolveSingleMatchedProductCode([product('P1'), product('p1 ', true)]), 'P1', '同一商品主条码与多码各一条仍算一个商品')
  assertEqual(resolveSingleMatchedProductCode([product('P1'), product('P2')]), null, '匹配到多个商品交给人工')
  assertEqual(resolveSingleMatchedProductCode([]), null, '没有匹配返回 null')
  assertEqual(resolveSingleMatchedProductCode(undefined), null, '接口没返回列表时返回 null')

  // ---- 限并发执行：结果顺序与输入一致，单个失败不影响其它 ----
  let running = 0
  let peak = 0
  const results = await mapWithConcurrency([1, 2, 3, 4, 5, 6], 2, async (value) => {
    running += 1
    peak = Math.max(peak, running)
    await new Promise((resolve) => setTimeout(resolve, 5))
    running -= 1
    if (value === 3) throw new Error('boom')
    return value * 10
  })
  assertEqual(peak, 2, '同时执行的任务不超过并发上限')
  assertEqual(results.map((item) => (item.status === 'fulfilled' ? item.value : 'x')).join(','), '10,20,x,40,50,60', '结果按输入顺序，失败项单独标记')
  assertEqual((await mapWithConcurrency([], 4, async () => 1)).length, 0, '空列表直接返回')

  // ---- 源码契约：入口挂在「设置操作」菜单，且先走未保存守卫 ----
  const pageSource = readFileSync(path.resolve(process.cwd(), 'src/pages/PosAdmin/LocalSupplierInvoices/InvoiceEdit/index.tsx'), 'utf8')
  assert(pageSource.includes('key: BATCH_LINK_MATCHED_MENU_KEY'), '「设置操作」菜单应包含批量选用入口')
  assert(pageSource.includes('runAfterUnsavedGuard(handleBatchLinkMatchedProducts)'), '批量选用前应先处理未保存修改')
  assert(pageSource.includes('isProductLinkConfirmed(checkByGuid.get(link.detailGUID), link.productCode)'), '批量选用后应以重新检测结果确认关联')

  console.log('batchLinkMatchedProducts tests passed')
}

void main().catch((error) => {
  console.error(error)
  process.exit(1)
})
