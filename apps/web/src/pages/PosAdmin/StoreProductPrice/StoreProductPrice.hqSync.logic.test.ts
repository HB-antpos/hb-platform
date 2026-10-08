import { readFileSync } from 'node:fs'
import path from 'node:path'
import { formatPaginationTotalText, getPaginationTotalPages } from './pagination'
import {
  getStoreProductPriceGrid,
  syncFromHq,
} from '../../../services/storeProductPriceService'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) {
    throw new Error(message)
  }
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

async function assertRejects(execute: () => Promise<unknown>, expectedMessage: string, message: string) {
  try {
    await execute()
  } catch (error) {
    const actualMessage = error instanceof Error ? error.message : String(error)
    assertEqual(actualMessage, expectedMessage, message)
    return
  }

  throw new Error(`${message}。Expected promise to reject`)
}

async function runTest(name: string, execute: () => void | Promise<void>): Promise<string | null> {
  try {
    await execute()
    console.log(`ok - ${name}`)
    return null
  } catch (error) {
    const reason = error instanceof Error ? error.message : String(error)
    console.error(`not ok - ${name}`)
    console.error(reason)
    return `${name}: ${reason}`
  }
}

const pageFile = path.resolve(process.cwd(), 'src/pages/PosAdmin/StoreProductPrice/index.tsx')
const typeFile = path.resolve(process.cwd(), 'src/types/storeProductPrice.ts')
const pageSource = readFileSync(pageFile, 'utf8')
const typeSource = readFileSync(typeFile, 'utf8')

async function main() {
  const failures: string[] = []

  const typeFailure = await runTest('SyncFromHqRequest 应声明 endDate', () => {
    assert(
      typeSource.includes('endDate?: string'),
      'SyncFromHqRequest 类型中应新增 endDate 可选字段',
    )
  })
  if (typeFailure) failures.push(typeFailure)

  // HQ 零售价 → HBweb 的「从HQ更新零售价」已于 2026-09-29 停用（后端返回 410），页面不得再保留入口与弹窗。
  const hqSyncRemovedFailure = await runTest('页面不再提供从HQ更新零售价的按钮与弹窗', () => {
    for (const removed of [
      'syncFromHq',
      'SyncFromHqRequest',
      'hqSyncForm',
      'hqSyncModalOpen',
      'openHqSyncModal',
      'selectAllHqSyncStores',
      'handleSyncFromHq',
      "t('posAdmin.productPrice.updateFromHQ'",
      "t('posAdmin.productPrice.hqSyncTitle'",
      "t('posAdmin.productPrice.hqSyncFailed'",
    ]) {
      assert(!pageSource.includes(removed), `页面不应再包含 HQ → HBweb 零售价同步代码：${removed}`)
    }
  })
  if (hqSyncRemovedFailure) failures.push(hqSyncRemovedFailure)

  const priceTransferRemovedFailure = await runTest('HQ/本地价格同步入口已取消，页面与服务层不得再出现', () => {
    for (const marker of ['startStorePriceTransferJob', 'getStorePriceTransferJob', 'priceTransferModalOpen', 'HQ/本地价格同步']) {
      assert(!pageSource.includes(marker), `商品价格页不应再包含 ${marker}`)
    }
    assert(
      !readFileSync(path.resolve(process.cwd(), 'src/services/storeProductPriceService.ts'), 'utf8').includes('store-price-transfer-jobs'),
      '服务层不应再调用 store-price-transfer-jobs 接口',
    )
  })
  if (priceTransferRemovedFailure) failures.push(priceTransferRemovedFailure)

  const paginationTotalPagesFailure = await runTest('分页文案应同时显示总数和总页数', () => {
    assertEqual(getPaginationTotalPages(128, 50), 3, '128 条且每页 50 条时应显示 3 页')
    assertEqual(getPaginationTotalPages(0, 50), 0, '0 条数据时应显示 0 页')
    assertEqual(getPaginationTotalPages(10, 0), 10, 'pageSize 异常时应使用 1 作为兜底页大小')

    const text = formatPaginationTotalText(128, 50, (_key, _fallback, values) => (
      `共 ${values?.count} 条 / ${values?.pages} 页`
    ))

    assertEqual(text, '共 128 条 / 3 页', '分页文案应把总数和总页数一起展示')
  })
  if (paginationTotalPagesFailure) failures.push(paginationTotalPagesFailure)

  const originalFetch = globalThis.fetch

  const pagedFieldFailure = await runTest('商品价格分页接口应兼容 totalCount 和 pageIndex 字段', async () => {
    globalThis.fetch = (async () => new Response(JSON.stringify({
      success: true,
      data: {
        items: [{ productCode: 'P001', isActive: true, isStoreAutoPricing: false, isStoreSpecialProduct: false }],
        totalCount: 128,
        pageIndex: 3,
        pageSize: 50,
      },
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })) as typeof fetch

    const result = await getStoreProductPriceGrid({
      storeCode: 'S01',
      pageNumber: 3,
      pageSize: 50,
    })

    assertEqual(result.total, 128, '分页结果应把 totalCount 归一为 total')
    assertEqual(result.page, 3, '分页结果应把 pageIndex 归一为 page')
    assertEqual(result.pageSize, 50, '分页结果应保留后端 pageSize')
    assertEqual(result.items.length, 1, '分页结果应保留商品列表')
  })
  if (pagedFieldFailure) failures.push(pagedFieldFailure)

  const businessFailure = await runTest('syncFromHq 遇到 success false 时应抛出后端消息', async () => {
    globalThis.fetch = (async () => new Response(JSON.stringify({
      success: false,
      message: 'HQ 同步失败：测试业务错误',
      data: {
        addedCount: 99,
        updatedCount: 88,
        totalProcessed: 187,
        durationMs: 2000,
        errors: [],
      },
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })) as typeof fetch

    await assertRejects(
      () => syncFromHq({ selectedStoreCodes: ['S01'], startDate: '2026-05-01', endDate: '2026-05-31' }),
      'HQ 同步失败：测试业务错误',
      'syncFromHq 不应把 success false 的响应当成成功结果',
    )
  })
  if (businessFailure) failures.push(businessFailure)

  const httpFailure = await runTest('syncFromHq 遇到非 2xx 时应抛出后端消息', async () => {
    globalThis.fetch = (async () => new Response(JSON.stringify({
      success: false,
      message: 'HQ 同步失败：测试 HTTP 错误',
    }), {
      status: 500,
      headers: { 'Content-Type': 'application/json' },
    })) as typeof fetch

    await assertRejects(
      () => syncFromHq({ selectedStoreCodes: ['S01'], startDate: '2026-05-01', endDate: '2026-05-31' }),
      'HQ 同步失败：测试 HTTP 错误',
      'syncFromHq 应把非 2xx 的后端消息透传出来',
    )
  })
  if (httpFailure) failures.push(httpFailure)

  globalThis.fetch = originalFetch

  if (failures.length > 0) {
    throw new Error(`共有 ${failures.length} 个测试失败\n- ${failures.join('\n- ')}`)
  }

  console.log('StoreProductPrice.hqSync.logic.test: ok')
}

await main()
