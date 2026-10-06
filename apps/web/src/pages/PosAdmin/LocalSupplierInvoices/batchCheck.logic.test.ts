import { readFileSync } from 'node:fs'
import path from 'node:path'

import type {
  BatchCheckProductsItemDto,
  BatchCheckProductsJobDto,
  LocalSupplierInvoiceListDto,
} from '../../../types/localSupplierInvoice'
import {
  BATCH_CHECK_JOB_STORAGE_KEY,
  BATCH_CHECK_MAX_INVOICES,
  applyBatchSelectionLimit,
  buildBatchItemMap,
  getRowBatchDisplay,
  mergeSelectedRecords,
  pickPendingSelection,
  readStoredBatchJobId,
  shouldRefreshListForBatch,
  summarizeBatchSelection,
  writeStoredBatchJobId,
} from './batchCheck'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function buildJob(overrides: Partial<BatchCheckProductsJobDto> = {}): BatchCheckProductsJobDto {
  return {
    jobId: '0123456789abcdef0123456789abcdef',
    operationId: 'batch-check-products|a,b',
    status: 'Running',
    isDuplicateRequest: false,
    isWaiting: false,
    cancelRequested: false,
    storeCodes: ['1005'],
    createdAt: '2026-10-06T10:00:00Z',
    total: 4,
    processed: 0,
    succeeded: 0,
    failed: 0,
    skipped: 0,
    items: [],
    ...overrides,
  }
}

function item(invoiceGuid: string, status: BatchCheckProductsItemDto['status']): BatchCheckProductsItemDto {
  return { invoiceGuid, status, checkedCount: 0 }
}

function record(invoiceGUID: string, isProductChecked: boolean): LocalSupplierInvoiceListDto {
  return { invoiceGUID, isProductChecked } as LocalSupplierInvoiceListDto
}

// ---- 勾选上限 ----
const full = Array.from({ length: BATCH_CHECK_MAX_INVOICES }, (_, index) => `inv-${index}`)
// 复现 10-06 线上问题：已选满 50 张（来自别的筛选视图）后再点一行，不能把已选的挤掉、也不能悄悄丢掉新点的
const overflow = applyBatchSelectionLimit(full, [...full, 'new-row'])
assertEqual(overflow.keys.length, BATCH_CHECK_MAX_INVOICES, '已选满时总数不超过上限')
assert(!overflow.keys.includes('new-row'), '已选满时新勾的不加入')
assert(overflow.truncated, '已选满时要提示')
const partial = applyBatchSelectionLimit(full.slice(0, 48), [...full.slice(0, 48), 'x1', 'x2', 'x3'])
assertEqual(partial.keys.join(','), [...full.slice(0, 48), 'x1', 'x2'].join(','), '全选一页超限时已选保留、新勾的补到上限')
assert(partial.truncated, '补不全要提示')
const removed = applyBatchSelectionLimit(['a', 'b', 'c'], ['a', 'c'])
assertEqual(removed.keys.join(','), 'a,c', '取消勾选总是生效')
assert(!removed.truncated, '取消勾选不提示')
const fresh = applyBatchSelectionLimit([], ['a', 'b'])
assertEqual(fresh.keys.join(','), 'a,b', '未超过上限原样保留')
assert(!fresh.truncated, '未超过上限不提示')

// ---- 源码契约：筛选条件变化清空勾选（翻页、排序不走 requestFirstPage，保留跨页勾选）----
const pageSource = readFileSync(path.resolve(process.cwd(), 'src/pages/PosAdmin/LocalSupplierInvoices/index.tsx'), 'utf8')
const requestFirstPageStart = pageSource.indexOf('const requestFirstPage = ')
assert(requestFirstPageStart >= 0, '找不到 requestFirstPage')
const requestFirstPageBody = pageSource.slice(requestFirstPageStart, pageSource.indexOf('\n  }\n', requestFirstPageStart))
assert(requestFirstPageBody.includes('setSelectedInvoiceKeys([])'), '筛选条件变化必须清空已选进货单')
assert(requestFirstPageBody.includes('setSelectedInvoiceRecords({})'), '筛选条件变化必须清空已选行数据')

// ---- 行内状态 ----
const job = buildJob({
  items: [item('a', 'Queued'), item('b', 'Running'), item('c', 'Succeeded'), item('d', 'Failed'), item('e', 'Skipped')],
})
const map = buildBatchItemMap(job)
assertEqual(getRowBatchDisplay(map.get('a'), true), 'queued', '运行中任务的排队单显示排队中')
assertEqual(getRowBatchDisplay(map.get('b'), true), 'running', '运行中任务的当前单显示检测中')
assertEqual(getRowBatchDisplay(map.get('c'), true), null, '成功的单回到列表数据显示')
assertEqual(getRowBatchDisplay(map.get('d'), false), 'failed', '失败保留到关闭进度条')
assertEqual(getRowBatchDisplay(map.get('e'), false), 'skipped', '跳过保留到关闭进度条')
assertEqual(getRowBatchDisplay(map.get('a'), false), null, '任务结束后不再显示排队中')
assertEqual(getRowBatchDisplay(undefined, true), null, '不在任务里的单正常显示')

// ---- 列表刷新节流 ----
const state = { refreshedProcessed: 1, lastRefreshAt: 10_000 }
assert(!shouldRefreshListForBatch(buildJob({ processed: 1 }), state, 20_000), '没有新处理完的单不刷新')
assert(!shouldRefreshListForBatch(buildJob({ processed: 2 }), state, 11_000), '运行中间隔不足不刷新')
assert(shouldRefreshListForBatch(buildJob({ processed: 2 }), state, 13_000), '运行中间隔够了刷新')
assert(
  shouldRefreshListForBatch(buildJob({ processed: 4, status: 'Completed' }), state, 10_500),
  '任务结束立即刷新，不等间隔',
)
assert(
  !shouldRefreshListForBatch(buildJob({ processed: 1, status: 'Cancelled' }), state, 99_000),
  '结束后已刷新过就不再刷新',
)

// ---- 勾选汇总与仅选待检测 ----
const records = { a: record('a', false), b: record('b', true), c: record('c', false), d: undefined }
const summary = summarizeBatchSelection(['a', 'b', 'c', 'd'], records)
assertEqual(summary.total, 4, '已选总数含看不到数据的单')
assertEqual(summary.pending, 2, '待检测按 isProductChecked === false 计')
assertEqual(pickPendingSelection(['a', 'b', 'c', 'd'], records).join(','), 'a,c', '仅选待检测去掉已检测与未知的单')

// ---- 跨页合并勾选行数据 ----
const merged = mergeSelectedRecords(['a', 'x'], [record('x', false), undefined], { a: record('a', true), z: record('z', true) })
assertEqual(merged.a?.isProductChecked, true, '其它页的行沿用之前记下的数据')
assertEqual(merged.x?.isProductChecked, false, '本页给出的行用最新数据')
assert(!('z' in merged), '取消勾选的单从记录里移除')

// ---- sessionStorage ----
const store = new Map<string, string>()
const storage = {
  getItem: (key: string) => store.get(key) ?? null,
  setItem: (key: string, value: string) => void store.set(key, value),
  removeItem: (key: string) => void store.delete(key),
}
writeStoredBatchJobId(storage, '0123456789abcdef0123456789abcdef')
assertEqual(readStoredBatchJobId(storage), '0123456789abcdef0123456789abcdef', '能读回存下的 jobId')
store.set(BATCH_CHECK_JOB_STORAGE_KEY, 'not-a-job-id')
assertEqual(readStoredBatchJobId(storage), null, '格式不对的值当作没有')
writeStoredBatchJobId(storage, null)
assert(!store.has(BATCH_CHECK_JOB_STORAGE_KEY), '关闭进度条时清掉')
const throwingStorage = {
  getItem: () => {
    throw new Error('blocked')
  },
}
assertEqual(readStoredBatchJobId(throwingStorage), null, 'sessionStorage 不可用时不报错')

console.log('batchCheck logic tests passed')
