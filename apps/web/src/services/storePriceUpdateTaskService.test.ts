import { createPriceNotificationCapture } from '../utils/priceNotification'
import { getWarehouseProductBatchUpdateJob, patchWarehouseProduct, updateWarehouseProductFull } from './warehouseProductService'
import {
  getStorePriceUpdateTaskSettings,
  lookupSuggestedDiscounts,
  previewPriceNotification,
  setSuggestedDiscounts,
  updateStorePriceUpdateTaskSettings,
} from './storePriceUpdateTaskService'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

const originalFetch = globalThis.fetch
const HEADER = '{"productCount":1,"needsPriceUpdateStores":6,"labelOnlyStores":0,"cancelledStores":0,"skippedSpecialStores":1,"hasAny":true}'
let capturedUrl = ''
let capturedMethod: string | undefined
let capturedBody: unknown
let nextData: unknown = {}
let nextHeader: string | null = null
let nextFailure: { status: number; message: string } | null = null

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  capturedUrl = String(input)
  capturedMethod = init?.method
  capturedBody = init?.body ? JSON.parse(String(init.body)) : undefined
  if (nextFailure) {
    return new Response(JSON.stringify({ success: false, message: nextFailure.message }), {
      status: nextFailure.status,
      headers: { 'Content-Type': 'application/json' },
    })
  }
  return new Response(JSON.stringify({ success: true, data: nextData }), {
    status: 200,
    headers: { 'Content-Type': 'application/json', ...(nextHeader ? { 'X-Price-Notification': nextHeader } : {}) },
  })
}) as typeof fetch

try {
  // 一次保存连发两个请求：以最后一个带头的响应为准，不相加。
  const capture = createPriceNotificationCapture()
  nextHeader = HEADER
  await updateWarehouseProductFull('P001', { isActive: true, oemPrice: 9.9 }, { onResponse: capture.onResponse })
  assert(capture.getSummary()?.needsPriceUpdateStores === 6, 'full-update 应通过 onResponse 读到 X-Price-Notification 响应头')

  nextHeader = '{"needsPriceUpdateStores":2,"labelOnlyStores":0,"cancelledStores":0,"skippedSpecialStores":0}'
  nextData = { changedCount: 1 }
  const result = await setSuggestedDiscounts(
    { productCodes: ['P001'], suggestedDiscountRate: null, source: 'WarehouseProducts' },
    { onResponse: capture.onResponse },
  )
  assert(capturedUrl.endsWith('/api/react/v1/store-price-update-tasks/suggested-discounts'), '建议折扣应调用 suggested-discounts')
  assert(String(capturedMethod) === 'PUT', '建议折扣应使用 PUT')
  assert(
    JSON.stringify(capturedBody) === JSON.stringify({ productCodes: ['P001'], suggestedDiscountRate: null, source: 'WarehouseProducts' }),
    '清除建议折扣必须显式传 null（不能被序列化丢弃）',
  )
  assert(result.changedCount === 1, '应返回 changedCount')
  assert(capture.getSummary()?.needsPriceUpdateStores === 2, '第二个响应头应覆盖第一个，而不是相加')

  nextHeader = null
  await patchWarehouseProduct('P001', { oemPrice: 5 }, { onResponse: capture.onResponse })
  assert(capture.getSummary()?.needsPriceUpdateStores === 2, '不带头的响应不应清掉已有汇总')

  // onResponse 抛错不能把成功的保存变成失败。
  await patchWarehouseProduct('P001', { oemPrice: 5 }, { onResponse: () => { throw new Error('boom') } })

  nextData = [{ productCode: 'P001', suggestedDiscountRate: 0 }]
  const lookup = await lookupSuggestedDiscounts(['P001'])
  assert(String(capturedMethod) === 'POST' && capturedUrl.endsWith('/suggested-discounts/lookup'), 'lookup 应为 POST')
  assert(lookup[0]?.suggestedDiscountRate === 0, '0 = 明确无折扣，必须原样返回')

  nextData = { affectedStores: 4, skippedSpecialStores: 1 }
  const preview = await previewPriceNotification({ productCode: 'P 1', retailPrice: undefined, suggestedDiscountSpecified: true, suggestedDiscountRate: null })
  assert(preview.affectedStores === 4 && preview.skippedSpecialStores === 1, '应返回预告分店数')
  const previewUrl = new URL(capturedUrl, 'http://localhost')
  assert(previewUrl.searchParams.get('productCode') === 'P 1', '预告应带商品编码')
  assert(!previewUrl.searchParams.has('retailPrice'), '零售价未变时应省略 retailPrice')
  assert(previewUrl.searchParams.get('suggestedDiscountSpecified') === 'true', '应标记建议折扣已指定')
  assert(!previewUrl.searchParams.has('suggestedDiscountRate'), '建议折扣改为未设置时不传 rate（后端按 null 处理）')

  await previewPriceNotification({ productCode: 'P1', retailPrice: 9.5, suggestedDiscountSpecified: false, suggestedDiscountRate: 0.2 })
  const retailOnlyUrl = new URL(capturedUrl, 'http://localhost')
  assert(retailOnlyUrl.searchParams.get('retailPrice') === '9.5', '改零售价时应带 retailPrice')
  assert(!retailOnlyUrl.searchParams.has('suggestedDiscountRate'), '未指定建议折扣时不应传 rate')

  // 批量后台任务拿不到响应头，汇总来自任务快照。
  nextData = { jobId: 'job-1', status: 'Succeeded', priceNotification: JSON.parse(HEADER) }
  const job = await getWarehouseProductBatchUpdateJob('job-1')
  assert(job.priceNotification?.needsPriceUpdateStores === 6, '任务快照应归一化 priceNotification')
  nextData = { jobId: 'job-1', status: 'Succeeded' }
  assert((await getWarehouseProductBatchUpdateJob('job-1')).priceNotification === null, '任务快照缺失 priceNotification = 未涉及通知')

  // 任务超期天数设置：GET / PUT 同一路径 /settings，PUT 请求体为 { overdueDays }。
  const settingsData = {
    overdueDays: 14, defaultOverdueDays: 7, minOverdueDays: 1, maxOverdueDays: 90,
    isCustomized: true, updatedBy: 'amy', updatedAtUtc: '2026-10-16T01:02:03Z',
  }
  nextData = settingsData
  const settings = await getStorePriceUpdateTaskSettings()
  assert(capturedUrl.endsWith('/api/react/v1/store-price-update-tasks/settings'), '读取设置应调用 /settings')
  assert(String(capturedMethod) === 'GET', '读取设置应使用 GET')
  assert(JSON.stringify(settings) === JSON.stringify(settingsData), '应原样返回设置的各字段')

  nextData = { ...settingsData, overdueDays: 10, updatedBy: 'bob' }
  const saved = await updateStorePriceUpdateTaskSettings(10)
  assert(capturedUrl.endsWith('/api/react/v1/store-price-update-tasks/settings'), '保存设置应调用 /settings')
  assert(String(capturedMethod) === 'PUT', '保存设置应使用 PUT')
  assert(JSON.stringify(capturedBody) === JSON.stringify({ overdueDays: 10 }), '保存设置的请求体应为 { overdueDays }')
  assert(saved.overdueDays === 10 && saved.updatedBy === 'bob', '保存后应返回后端给出的最新设置')

  nextData = { overdueDays: 7, defaultOverdueDays: 7, minOverdueDays: 1, maxOverdueDays: 90, isCustomized: false }
  const defaults = await getStorePriceUpdateTaskSettings()
  assert(!defaults.isCustomized && defaults.updatedBy === null && defaults.updatedAtUtc === null, '未自定义时修改人与修改时间归一化为 null')

  // 超出范围时后端返回 400 与中文提示：错误信息必须原样抛给调用方展示。
  nextFailure = { status: 400, message: '超期天数必须在 1 到 90 之间' }
  let failureMessage = ''
  try {
    await updateStorePriceUpdateTaskSettings(120)
  } catch (error) {
    failureMessage = error instanceof Error ? error.message : ''
  }
  nextFailure = null
  assert(failureMessage === '超期天数必须在 1 到 90 之间', '保存失败应抛出后端的错误信息')

  console.log('storePriceUpdateTaskService.test: ok')
} finally {
  globalThis.fetch = originalFetch
}
