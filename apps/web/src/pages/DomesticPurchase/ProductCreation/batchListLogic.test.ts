import assert from 'node:assert/strict'
import { buildBatchListParams, formatBatchTime, resolveCreatedRange } from './batchListLogic'

// 固定「现在」为本地时间 2026-10-06 15:30，避免测试依赖运行日期。
const now = new Date(2026, 9, 6, 15, 30, 0)

assert.deepEqual(resolveCreatedRange('all', now), {})
assert.deepEqual(resolveCreatedRange('today', now), { startDate: '2026-10-06' })
assert.deepEqual(resolveCreatedRange('last7', now), { startDate: '2026-09-30' }, '近 7 天含今天：10-06 往前数 7 个自然日')
assert.deepEqual(resolveCreatedRange('last30', now), { startDate: '2026-09-07' })
assert.equal('endDate' in resolveCreatedRange('last7', now), false, '不传 endDate，避免 endDate 当天 0 点排除结束日创建的批次')

// 跨月、跨年
assert.deepEqual(resolveCreatedRange('last7', new Date(2026, 0, 3, 9, 0, 0)), { startDate: '2025-12-28' })

// 请求参数：筛选条件是对象合并的产物，空供应商不下发
assert.deepEqual(buildBatchListParams({ page: 1, pageSize: 20, range: 'all' }, now), { page: 1, pageSize: 20 })
assert.deepEqual(
  buildBatchListParams({ page: 3, pageSize: 50, supplierCode: 'S001', range: 'last7' }, now),
  { page: 3, pageSize: 50, supplierCode: 'S001', startDate: '2026-09-30' },
)
assert.deepEqual(
  buildBatchListParams({ page: 1, pageSize: 20, supplierCode: undefined, range: 'today' }, now),
  { page: 1, pageSize: 20, startDate: '2026-10-06' },
  '清除供应商筛选（显式 undefined）后不能残留 supplierCode',
)
assert.equal(
  'supplierCode' in buildBatchListParams({ page: 1, pageSize: 20, supplierCode: '', range: 'all' }, now),
  false,
)

// 时间展示
assert.equal(formatBatchTime('2026-10-05T10:22:00'), '2026-10-05 10:22')
assert.equal(formatBatchTime(''), '-')
assert.equal(formatBatchTime(undefined), '-')
assert.equal(formatBatchTime('not a date'), '-')

console.log('batchListLogic.test: ok')
