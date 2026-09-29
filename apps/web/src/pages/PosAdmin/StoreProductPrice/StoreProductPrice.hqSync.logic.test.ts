import { readFileSync } from 'node:fs'
import path from 'node:path'
import { formatPaginationTotalText, getPaginationTotalPages } from './pagination'
import {
  getStorePriceTransferJob,
  getStoreProductPriceGrid,
  startStorePriceTransferJob,
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

  const transferTypeFailure = await runTest('StorePriceTransferRequest 应声明方向、表和字段选择', () => {
    assert(
      typeSource.includes("export type StorePriceTransferDirection = 'HqToLocal' | 'LocalToHq'"),
      '双向价格同步类型应限制方向枚举',
    )
    assert(
      typeSource.includes('syncRetailPrices: boolean') && typeSource.includes('syncMultiCodePrices: boolean'),
      '双向价格同步请求应包含价格表和多码表选择',
    )
    assert(
      typeSource.includes('syncPurchasePrice: boolean') && typeSource.includes('syncRetailPrice: boolean'),
      '双向价格同步请求应包含价格字段选择',
    )
  })
  if (transferTypeFailure) failures.push(transferTypeFailure)

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

  const priceTransferPageFailure = await runTest('HQ/本地价格同步 job 弹窗只保留本地 -> HQ 方向', () => {
    assert(
      pageSource.includes("t('posAdmin.productPrice.priceTransfer', 'HQ/本地价格同步')"),
      '页面应显示独立的 HQ/本地价格同步入口',
    )
    // HQ -> 本地方向属于 HQ → HBweb，已于 2026-09-29 停用；后端也只放行 LocalToHq。
    assert(
      pageSource.includes("direction: 'LocalToHq'") &&
        !pageSource.includes("direction: 'HqToLocal'") &&
        !pageSource.includes("value: 'HqToLocal'"),
      '弹窗默认方向应为本地 -> HQ，且不再提供 HQ -> 本地选项',
    )
    assert(
      pageSource.includes('startStorePriceTransferJob(dto)'),
      '页面提交应走后台 job 创建接口',
    )
    assert(
      pageSource.includes('createHqSyncJobPoller<StorePriceTransferJobDto>'),
      '页面应复用 2 秒轮询 job 工具',
    )
    assert(
      pageSource.includes('const PRICE_TRANSFER_POLL_TIMEOUT_MS = 45 * 60 * 1000') &&
        pageSource.includes('timeoutMs: PRICE_TRANSFER_POLL_TIMEOUT_MS'),
      '20w 行价格同步应使用 45 分钟专属轮询超时',
    )
    assert(
      pageSource.includes("目标分店同步任务正在执行，已切换到已有任务"),
      '重复任务提示应说明是目标分店同步任务正在执行',
    )
    assert(
      pageSource.includes('任务可能仍在后台执行，请稍后刷新或重新查询'),
      '轮询超时提示应说明后台任务可能仍在执行',
    )
    assert(
      pageSource.includes('syncRetailPrices: !!values.syncRetailPrices') &&
        pageSource.includes('syncMultiCodePrices: !!values.syncMultiCodePrices'),
      '页面 payload 应包含同步表选择',
    )
  })
  if (priceTransferPageFailure) failures.push(priceTransferPageFailure)

  const priceTransferSameStoreFailure = await runTest('HQ/本地价格同步应允许源目标分店同名', () => {
    assert(
      !pageSource.includes('sourceTargetDifferent') && !pageSource.includes('源分店和目标分店不能相同'),
      'HQ/本地跨数据域同步不应拦截同名源/目标分店',
    )
  })
  if (priceTransferSameStoreFailure) failures.push(priceTransferSameStoreFailure)

  const priceTransferProgressFailure = await runTest('HQ/本地价格同步进度应使用后端真实 totalCount', () => {
    assert(
      typeSource.includes('totalCount: number') &&
        typeSource.includes('retailPriceTotal: number') &&
        typeSource.includes('multiCodeTotal: number'),
      'StorePriceTransferResult 类型应声明后端进度总数字段',
    )
    assert(
      pageSource.includes('function getPriceTransferProgressPercent(job: StorePriceTransferJobDto)'),
      '页面应使用独立函数计算价格同步进度',
    )
    assert(
      pageSource.includes('result.totalProcessed + result.skippedCount') &&
        pageSource.includes('job.result?.totalCount ?? 0'),
      'Running 进度应按已处理/总量计算',
    )
    assert(
      !pageSource.includes("? 100 : 50"),
      'Running 进度不应继续硬编码为 50%',
    )
    assert(
      pageSource.includes("t('posAdmin.productPrice.processedProgress', '已处理')"),
      '弹窗应显示已处理 X / Y',
    )
    assert(
      pageSource.includes('const totalProcessed = getPriceTransferHandledCount(completedJob.result)'),
      '完成提示应复用已处理数量，避免漏算 skippedCount',
    )
    assert(
      pageSource.includes('const nextJob = await getStorePriceTransferJob(jobId)') &&
        pageSource.includes('setPriceTransferJob(nextJob)'),
      '轮询到 Running 快照时应立即刷新弹窗进度',
    )
  })
  if (priceTransferProgressFailure) failures.push(priceTransferProgressFailure)

  const validationErrorFailure = await runTest('表单校验失败不应弹出同步失败全局提示', () => {
    assert(
      pageSource.includes('function isFormValidationError(error: unknown): error is { errorFields: unknown[] }'),
      '页面应声明 AntD 表单校验错误识别函数',
    )
    assert(
      pageSource.includes('if (isFormValidationError(error)) return'),
      '分店价格同步提交应让字段级校验错误留在表单内展示，不弹出同步失败提示',
    )
  })
  if (validationErrorFailure) failures.push(validationErrorFailure)

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

  const priceTransferStartFailure = await runTest('startStorePriceTransferJob 应 POST 到后台任务接口并归一化返回', async () => {
    globalThis.fetch = (async (input, init) => {
      assertEqual(String(input), '/api/react/v1/store-product-prices/store-price-transfer-jobs', '创建任务接口路径应正确')
      const body = JSON.parse(String(init?.body || '{}'))
      assertEqual(body.direction, 'HqToLocal', '创建任务 payload 应包含方向')
      assertEqual(body.syncMultiCodePrices, true, '创建任务 payload 应包含多码表选择')
      return new Response(JSON.stringify({
        success: true,
        data: {
          jobId: 'job-1',
          status: 'Running',
          isDuplicateRequest: true,
        },
      }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    }) as typeof fetch

    const job = await startStorePriceTransferJob({
      direction: 'HqToLocal',
      sourceStoreCode: 'S01',
      targetStoreCode: 'T01',
      syncRetailPrices: true,
      syncMultiCodePrices: true,
      syncPurchasePrice: true,
      syncRetailPrice: true,
      syncDiscountRate: false,
      syncIsAutoPricing: false,
      syncIsSpecialProduct: false,
    })

    assertEqual(job.jobId, 'job-1', '创建任务应返回 jobId')
    assertEqual(job.status, 'Running', '创建任务应归一化运行中状态')
    assertEqual(job.isDuplicateRequest, true, '创建任务应保留重复提交标记')
  })
  if (priceTransferStartFailure) failures.push(priceTransferStartFailure)

  const priceTransferGetFailure = await runTest('getStorePriceTransferJob 应 GET 任务状态并归一化统计', async () => {
    globalThis.fetch = (async (input) => {
      assertEqual(String(input), '/api/react/v1/store-product-prices/store-price-transfer-jobs/job-1', '查询任务接口路径应正确')
      return new Response(JSON.stringify({
        success: true,
        data: {
          jobId: 'job-1',
          status: 'Succeeded',
          result: {
            totalProcessed: 4,
            insertedCount: 2,
            updatedCount: 2,
            retailPriceInserted: 1,
            multiCodeInserted: 1,
          },
        },
      }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    }) as typeof fetch

    const job = await getStorePriceTransferJob('job-1')
    assertEqual(job.status, 'Succeeded', '查询任务应归一化成功状态')
    assertEqual(job.result?.totalProcessed, 4, '查询任务应归一化总处理数')
    assertEqual(job.result?.retailPriceInserted, 1, '查询任务应归一化价格表新增数')
    assertEqual(job.result?.multiCodeInserted, 1, '查询任务应归一化多码表新增数')
  })
  if (priceTransferGetFailure) failures.push(priceTransferGetFailure)

  const priceTransferMissingStatusFailure = await runTest('getStorePriceTransferJob 缺少状态时应暴露契约错误', async () => {
    globalThis.fetch = (async () => new Response(JSON.stringify({
      success: true,
      data: {
        jobId: 'job-missing-status',
      },
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })) as typeof fetch

    await assertRejects(
      () => getStorePriceTransferJob('job-missing-status'),
      '分店价格同步任务缺少状态',
      '缺少 status 时不应被默认为 Running',
    )
  })
  if (priceTransferMissingStatusFailure) failures.push(priceTransferMissingStatusFailure)

  globalThis.fetch = originalFetch

  if (failures.length > 0) {
    throw new Error(`共有 ${failures.length} 个测试失败\n- ${failures.join('\n- ')}`)
  }

  console.log('StoreProductPrice.hqSync.logic.test: ok')
}

await main()
