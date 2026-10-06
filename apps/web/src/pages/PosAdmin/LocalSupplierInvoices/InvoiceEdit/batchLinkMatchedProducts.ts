import type { Key } from 'react'

import type {
  BarcodeAbnormalMatchedProductDto,
  LocalSupplierInvoiceItemDto,
} from '../../../../types/localSupplierInvoice'

import { canLinkMatchedProduct } from './matchedProductLink'

/** 批量「选用条码匹配商品」时按条码查询的并发上限，避免一次勾选上百行把接口打满。 */
export const BATCH_LINK_QUERY_CONCURRENCY = 4

/**
 * 批量选用的初筛：主档不存在（与单行「选用」同口径）、有条码、检测时条码匹配到过商品。
 * barcodeMatchCount 统计的是匹配记录条数（同一商品主条码与多码各算一次），只用于初筛；
 * 是否「只匹配到一个商品」以按条码查询后的去重商品编码为准，见 resolveSingleMatchedProductCode。
 */
export function pickBatchLinkCandidates(
  selectedRowKeys: Key[],
  details: LocalSupplierInvoiceItemDto[],
) {
  const selected = new Set(selectedRowKeys.map(String))
  const candidates: LocalSupplierInvoiceItemDto[] = []
  let ineligibleCount = 0
  for (const detail of details) {
    if (!selected.has(detail.detailGUID)) continue
    if (canLinkMatchedProduct(detail) && detail.barcode?.trim() && (detail.barcodeMatchCount ?? 0) > 0) {
      candidates.push(detail)
    } else {
      ineligibleCount += 1
    }
  }
  return { candidates, ineligibleCount }
}

/** 条码匹配到的商品去重后恰好一个时返回其商品编码；没有或多个都返回 null，交给人工逐行处理。 */
export function resolveSingleMatchedProductCode(matchedProducts: BarcodeAbnormalMatchedProductDto[] | undefined) {
  const codes = new Map<string, string>()
  for (const product of matchedProducts ?? []) {
    const code = product.productCode?.trim()
    // 编码比较不分大小写，回填用第一次出现的写法（通常是主条码那条）。
    if (code && !codes.has(code.toLowerCase())) codes.set(code.toLowerCase(), code)
  }
  return codes.size === 1 ? [...codes.values()][0] : null
}

/** 限制并发地逐个执行异步任务，结果顺序与输入一致；单个失败不影响其它任务。 */
export async function mapWithConcurrency<T, R>(
  items: T[],
  limit: number,
  task: (item: T) => Promise<R>,
): Promise<PromiseSettledResult<R>[]> {
  const results: PromiseSettledResult<R>[] = new Array(items.length)
  let next = 0
  const worker = async () => {
    while (next < items.length) {
      const index = next
      next += 1
      try {
        results[index] = { status: 'fulfilled', value: await task(items[index]) }
      } catch (reason) {
        results[index] = { status: 'rejected', reason }
      }
    }
  }
  await Promise.all(Array.from({ length: Math.max(1, Math.min(limit, items.length)) }, worker))
  return results
}
