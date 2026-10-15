import {
  type PatchWarehouseProductPayload,
  batchUpdateWarehouseProducts,
  createWarehouseProductBatchUpdateJob,
  createWarehouseProductBatchUpdateJobPoller,
  getWarehouseProductBatchUpdateJob,
  patchWarehouseProduct,
  updateWarehouseProductFull,
} from './warehouseProductService'
import { readFileSync } from 'node:fs'
import path from 'node:path'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) {
    throw new Error(message)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)

  if (actualJson !== expectedJson) {
    throw new Error(`${message}。Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

const originalFetch = globalThis.fetch
let capturedUrl = ''
let capturedMethod: string | undefined
let capturedBody: Record<string, unknown> | undefined
const serviceSource = readFileSync(path.resolve(process.cwd(), 'src/services/warehouseProductService.ts'), 'utf8')

assert(
  serviceSource.includes('MinOrderQuantity?: number') &&
    serviceSource.includes('PackingQuantity?: number'),
  '仓库商品批量更新类型应声明 MinOrderQuantity 和 PackingQuantity',
)

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  capturedUrl = String(input)
  capturedMethod = init?.method
  capturedBody = JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown>

  return new Response(JSON.stringify({ success: true, data: { success: true, successCount: 1 } }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })
}) as typeof fetch

try {
  await batchUpdateWarehouseProducts([
    {
      ProductCode: 'P001',
      SupplierCode: 'SUPPLIER-NEW',
      MinOrderQuantity: 0,
      PackingQuantity: 0,
      IsActive: false,
      DomesticPrice: undefined,
    },
  ], { syncStorePurchasePrice: false })

  assert(capturedBody, '应捕获仓库商品批量更新请求体')
  assert(capturedUrl.endsWith('/api/react/v1/product-warehouse/batch-update'), '批量更新应调用仓库商品 batch-update 接口')
  assert(capturedMethod === 'POST', '批量更新应使用 POST 方法')
  assertDeepEqual(
    capturedBody,
    {
      Items: [
        {
          ProductCode: 'P001',
          SupplierCode: 'SUPPLIER-NEW',
          MinOrderQuantity: 0,
          PackingQuantity: 0,
          IsActive: false,
        },
      ],
      SyncStorePurchasePrice: false,
    },
    '批量更新请求体应保留数量零值和 false，并忽略 undefined 字段',
  )
  assert(
    !('SyncStoreRetailPrices' in capturedBody) && !('GenerateTasks' in capturedBody),
    '批量更新未传零售价同步选项时，请求体不应携带 SyncStoreRetailPrices / GenerateTasks',
  )

  await batchUpdateWarehouseProducts(
    [{ ProductCode: 'P002', OEMPrice: 9.9 }],
    { syncStoreRetailPrices: false, generateTasks: true },
  )
  assertDeepEqual(
    capturedBody,
    {
      Items: [{ ProductCode: 'P002', OEMPrice: 9.9 }],
      SyncStoreRetailPrices: false,
      GenerateTasks: true,
    },
    '批量更新应按 PascalCase 键名发送 SyncStoreRetailPrices / GenerateTasks，且保留 false',
  )

  await batchUpdateWarehouseProducts(
    [{ ProductCode: 'P002', OEMPrice: 9.9 }],
    { syncStoreRetailPrices: true, generateTasks: false },
  )
  assertDeepEqual(
    capturedBody,
    {
      Items: [{ ProductCode: 'P002', OEMPrice: 9.9 }],
      SyncStoreRetailPrices: true,
      GenerateTasks: false,
    },
    '批量更新两个开关应相互独立：同步分店价但不生成任务',
  )

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUrl = String(input)
    capturedMethod = init?.method
    capturedBody = JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown>

    return new Response(JSON.stringify({
      success: true,
      successCount: 1,
      failedCount: 0,
      imageUpdatedCount: 1,
      hqImageSync: {
        requested: true,
        success: false,
        updatedCount: 0,
        failedCount: 1,
        errorCode: 'HQ_IMAGE_SYNC_ITEM_ERRORS',
        errors: ['HQ 商品不存在: P001'],
      },
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }) as typeof fetch

  const imageResult = await batchUpdateWarehouseProducts(
    [{ ProductCode: 'P001' }],
    {
      generateImageUrls: true,
      imageBaseUrl: 'https://images.example.com/catalog/',
      syncImageToHq: true,
    },
  )

  assertDeepEqual(
    capturedBody,
    {
      Items: [{ ProductCode: 'P001' }],
      GenerateImageUrls: true,
      ImageBaseUrl: 'https://images.example.com/catalog/',
      SyncImageToHq: true,
    },
    '批量图片更新应发送图片生成和 HQ 同步选项',
  )
  assert(imageResult.imageUpdatedCount === 1, '应归一化本地图片更新数量')
  assert(imageResult.hqImageSync?.success === false, 'HQ 逐项失败不应由服务层抛错')
  assert(imageResult.hqImageSync?.failedCount === 1, '应归一化 HQ 失败数量')
  assertDeepEqual(
    imageResult.hqImageSync?.errors,
    ['HQ 商品不存在: P001'],
    '应保留 HQ 同步错误明细',
  )

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUrl = String(input)
    capturedMethod = init?.method
    capturedBody = JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown>
    const isStatusRequest = capturedMethod === 'GET'
    const data = isStatusRequest
      ? {
          jobId: 'batch-job-1',
          operationId: 'warehouse-product-batch-update:test',
          status: 'PartiallySucceeded',
          result: {
            success: true,
            successCount: 1,
            failedCount: 1,
            imageUpdatedCount: 1,
            hqImageSync: {
              requested: true,
              success: false,
              failedCount: 1,
              errors: ['HQ 商品不存在: P001'],
            },
          },
        }
      : {
          jobId: 'batch-job-1',
          operationId: 'warehouse-product-batch-update:test',
          status: 'Queued',
          createdAt: '2026-08-13T00:00:00Z',
        }

    return new Response(JSON.stringify({ success: true, data }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }) as typeof fetch

  const createdJob = await createWarehouseProductBatchUpdateJob(
    [{ ProductCode: 'P001', SupplierCode: 'SUPPLIER-NEW' }],
    {
      generateImageUrls: true,
      imageBaseUrl: 'https://images.example.com/catalog/',
      syncImageToHq: true,
    },
  )
  assert(capturedUrl.endsWith('/api/react/v1/product-warehouse/batch-update/jobs'), '后台批量修改应调用 jobs 创建接口')
  assert(capturedMethod === 'POST', '后台批量修改任务应使用 POST 创建')
  assert(createdJob.status === 'Queued', '创建任务应保留 Queued 状态')
  assertDeepEqual(
    capturedBody,
    {
      Items: [{ ProductCode: 'P001', SupplierCode: 'SUPPLIER-NEW' }],
      GenerateImageUrls: true,
      ImageBaseUrl: 'https://images.example.com/catalog/',
      SyncImageToHq: true,
    },
    '后台任务请求应完整携带批量修改与图片同步选项',
  )
  assert(
    !('SyncStoreRetailPrices' in (capturedBody ?? {})) && !('GenerateTasks' in (capturedBody ?? {})),
    '后台任务未传零售价同步选项时，请求体不应携带 SyncStoreRetailPrices / GenerateTasks',
  )

  await createWarehouseProductBatchUpdateJob(
    [{ ProductCode: 'P001', OEMPrice: 12.5 }],
    { syncStoreRetailPrices: false, generateTasks: false },
  )
  assert(capturedUrl.endsWith('/api/react/v1/product-warehouse/batch-update/jobs'), '带零售价选项的后台批量修改也应调用 jobs 创建接口')
  assertDeepEqual(
    capturedBody,
    {
      Items: [{ ProductCode: 'P001', OEMPrice: 12.5 }],
      SyncStoreRetailPrices: false,
      GenerateTasks: false,
    },
    '后台任务请求体顶层应携带 SyncStoreRetailPrices / GenerateTasks，且两个都为 false 时也要发送',
  )

  const completedJob = await getWarehouseProductBatchUpdateJob('batch-job-1')
  assert(capturedUrl.endsWith('/api/react/v1/product-warehouse/batch-update/jobs/batch-job-1'), '应按 jobId 查询后台批量修改状态')
  assert(String(capturedMethod) === 'GET', '查询后台批量修改状态应使用 GET')
  assert(completedJob.status === 'PartiallySucceeded', '应保留 PartiallySucceeded 终态')
  assert(completedJob.result?.hqImageSync?.failedCount === 1, '应归一化后台任务中的 HQ 失败明细')

  const scheduledCallbacks: Array<() => void> = []
  const poller = createWarehouseProductBatchUpdateJobPoller({
    jobId: 'batch-job-1',
    getJob: async () => completedJob,
    setTimeoutFn: (callback) => {
      scheduledCallbacks.push(callback)
      return scheduledCallbacks.length
    },
    clearTimeoutFn: () => undefined,
  })
  assert(scheduledCallbacks.length === 2, '后台任务轮询应同时安排超时与首次状态查询')
  scheduledCallbacks[1]?.()
  const polledJob = await poller.promise
  assert(polledJob.status === 'PartiallySucceeded', 'PartiallySucceeded 应作为批量修改轮询终态')

  capturedMethod = undefined
  capturedBody = undefined
  await patchWarehouseProduct('HB 001', { oemPrice: 0 })

  assert(capturedBody, '应捕获仓库商品单字段更新请求体')
  assert(capturedUrl.endsWith('/api/react/v1/product-warehouse/HB%20001'), '单字段更新应编码商品货号并调用仓库商品根 PATCH 接口')
  assert(capturedMethod === 'PATCH', '单字段更新应使用 PATCH 方法')
  assertDeepEqual(
    capturedBody,
    { OEMPrice: 0 },
    '单字段更新应只发送一个 PascalCase 字段并保留零值',
  )

  await patchWarehouseProduct('HB 001', { oemPrice: 9.9, syncStoreRetailPrices: false, generateTasks: true })
  assertDeepEqual(
    capturedBody,
    { OEMPrice: 9.9, SyncStoreRetailPrices: false, GenerateTasks: true },
    '改零售价的 PATCH 应携带 SyncStoreRetailPrices / GenerateTasks',
  )

  await patchWarehouseProduct('HB 001', { oemPrice: 9.9, syncStoreRetailPrices: true })
  assertDeepEqual(
    capturedBody,
    { OEMPrice: 9.9, SyncStoreRetailPrices: true },
    'PATCH 只传了一个开关时，另一个不应出现在请求体里（交给后端默认值）',
  )

  // 类型上已禁止，运行时再兜底一次：不含零售价的补丁绝不携带这两个开关。
  await patchWarehouseProduct('HB 001', {
    importPrice: 3.5,
    syncStoreRetailPrices: false,
    generateTasks: false,
  } as unknown as PatchWarehouseProductPayload)
  assertDeepEqual(
    capturedBody,
    { ImportPrice: 3.5 },
    '不含零售价的 PATCH 不应携带 SyncStoreRetailPrices / GenerateTasks',
  )

  capturedMethod = undefined
  capturedBody = undefined
  await updateWarehouseProductFull('P001', {
    minOrderQuantity: 0,
    isActive: true,
  })

  const fullUpdateBody = capturedBody as Record<string, unknown> | undefined
  assert(fullUpdateBody, '应捕获仓库商品完整更新请求体')
  assert(capturedMethod === 'PUT', '完整更新应继续使用 PUT 方法')
  assert(fullUpdateBody.MinOrderQuantity === 0, '编辑弹窗完整更新应发送 MinOrderQuantity 并保留零值')
  assert(!('MiddlePackQuantity' in fullUpdateBody), '编辑弹窗不应再把中包数发送为 Product.MiddlePackageQuantity 对应字段')
  assert(
    !('SyncStoreRetailPrices' in fullUpdateBody) && !('GenerateTasks' in fullUpdateBody),
    '没有改零售价的完整更新不应携带 SyncStoreRetailPrices / GenerateTasks',
  )

  await updateWarehouseProductFull('P001', {
    oemPrice: 8.8,
    isActive: true,
    syncStoreRetailPrices: false,
    generateTasks: false,
  })
  const retailFullUpdateBody = capturedBody as Record<string, unknown> | undefined
  assert(retailFullUpdateBody, '应捕获改零售价的完整更新请求体')
  assert(capturedUrl.endsWith('/api/react/v1/product-warehouse/P001/full-update'), '完整更新应调用 full-update 接口')
  assert(retailFullUpdateBody.OEMPrice === 8.8, '完整更新应发送新的 OEMPrice')
  assert(retailFullUpdateBody.SyncStoreRetailPrices === false, '完整更新应携带 SyncStoreRetailPrices 且保留 false')
  assert(retailFullUpdateBody.GenerateTasks === false, '完整更新应携带 GenerateTasks 且保留 false')

  await updateWarehouseProductFull('P001', {
    oemPrice: 8.8,
    isActive: true,
    syncStoreRetailPrices: true,
    generateTasks: true,
  })
  const defaultRetailFullUpdateBody = capturedBody as Record<string, unknown> | undefined
  assert(
    defaultRetailFullUpdateBody?.SyncStoreRetailPrices === true && defaultRetailFullUpdateBody?.GenerateTasks === true,
    '完整更新默认勾选时两个开关都应显式发送 true',
  )
} finally {
  globalThis.fetch = originalFetch
}

console.log('warehouseProductService.batchUpdate.test: ok')
