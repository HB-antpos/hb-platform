import assert from 'node:assert/strict'
import {
  formatPrefixProductPrice,
  formatPrefixTimestamp,
  hasActivePrefixFilters,
  mergePrefixListQuery,
  pageAfterRemoval,
  resolvePrefixSort,
  statusFilterToIsActive,
  toTableSortOrder,
} from './prefixListLogic'
import type { PrefixListQuery } from './prefixListLogic'

// ---- 状态分段 ----
assert.equal(statusFilterToIsActive('all'), undefined, '全部 = 不带 isActive')
assert.equal(statusFilterToIsActive('active'), true)
assert.equal(statusFilterToIsActive('inactive'), false, '停用必须是 false 而不是 undefined')

// ---- 排序键映射：只放行后端支持且页面开了排序的列 ----
assert.deepEqual(resolvePrefixSort('prefixName', 'ascend'), { sortField: 'prefixName', sortDirection: 'asc' })
assert.deepEqual(resolvePrefixSort('supplierName', 'descend'), { sortField: 'supplierName', sortDirection: 'desc' })
assert.deepEqual(resolvePrefixSort('sortOrder', 'ascend'), { sortField: 'sortOrder', sortDirection: 'asc' })
assert.deepEqual(resolvePrefixSort('prefixName', null), { sortField: undefined, sortDirection: undefined }, '取消排序 = 不带排序参数')
assert.deepEqual(resolvePrefixSort('updatedAt', 'ascend'), { sortField: undefined, sortDirection: undefined }, '后端不支持的列不排序')
assert.deepEqual(resolvePrefixSort(['prefixName'], 'ascend'), { sortField: undefined, sortDirection: undefined }, '非字符串 field 忽略')
assert.deepEqual(resolvePrefixSort(undefined, undefined), { sortField: undefined, sortDirection: undefined })

assert.equal(toTableSortOrder({ sortField: 'prefixName', sortDirection: 'asc' }, 'prefixName'), 'ascend')
assert.equal(toTableSortOrder({ sortField: 'prefixName', sortDirection: 'desc' }, 'prefixName'), 'descend')
assert.equal(toTableSortOrder({ sortField: 'prefixName', sortDirection: 'asc' }, 'sortOrder'), null, '其它列不显示箭头')
assert.equal(toTableSortOrder({}, 'prefixName'), null)

// ---- 查询参数合并：显式 undefined 能清除筛选 ----
const base: PrefixListQuery = { page: 3, pageSize: 20, search: 'bx', supplierCode: 'S1', isActive: true, sortField: 'prefixName', sortDirection: 'asc' }
const cleared = mergePrefixListQuery(base, { page: 1, supplierCode: undefined, isActive: undefined })
assert.equal(cleared.page, 1)
assert.equal(cleared.supplierCode, undefined, '显式 undefined 必须清除供应商筛选（默认参数写法会把它吞掉）')
assert.equal('supplierCode' in cleared, true, '键仍存在但值为 undefined')
assert.equal(cleared.isActive, undefined, '显式 undefined 必须清除状态筛选')
assert.equal(cleared.search, 'bx', '未提及的筛选保持不变')
assert.equal(cleared.sortField, 'prefixName', '未提及的排序保持不变')
assert.deepEqual(mergePrefixListQuery(base), base, '没有 overrides 时等同于原查询')
assert.equal(mergePrefixListQuery(base, { isActive: false }).isActive, false, 'false 是有效的状态筛选值')
assert.equal(base.supplierCode, 'S1', '合并不能修改原对象')

assert.equal(hasActivePrefixFilters({}), false)
assert.equal(hasActivePrefixFilters({ search: 'bx' }), true)
assert.equal(hasActivePrefixFilters({ supplierCode: 'S1' }), true)
assert.equal(hasActivePrefixFilters({ isActive: false }), true, '只筛「停用」也算有筛选')

// ---- 删除后的页码 ----
assert.equal(pageAfterRemoval(3, 1), 2, '删掉最后一页的唯一一行，回退一页')
assert.equal(pageAfterRemoval(1, 1), 1, '第一页不回退')
assert.equal(pageAfterRemoval(3, 5), 3, '当前页仍有数据则不回退')

// ---- 更新时间文本（不做时区换算） ----
assert.equal(formatPrefixTimestamp('2026-10-02T14:20:33', 2026), '10-02 14:20', '当年显示 MM-DD HH:mm')
assert.equal(formatPrefixTimestamp('2025-12-31T23:59:00', 2026), '2025-12-31 23:59', '跨年显示完整日期')
assert.equal(formatPrefixTimestamp('2026-10-02 14:20:33', 2026), '10-02 14:20', '空格分隔同样可解析')
assert.equal(formatPrefixTimestamp(undefined, 2026), '--')
assert.equal(formatPrefixTimestamp('', 2026), '--')
assert.equal(formatPrefixTimestamp('not-a-date', 2026), 'not-a-date', '无法解析时原样展示')

// ---- 展开行的国内价 ----
assert.equal(formatPrefixProductPrice(18.5), '¥ 18.50')
assert.equal(formatPrefixProductPrice(0), '¥ 0.00', '0 元也要显示')
assert.equal(formatPrefixProductPrice(undefined), '--')
assert.equal(formatPrefixProductPrice(Number.NaN), '--')

console.log('prefixListLogic.test.ts: ok')
