import assert from 'node:assert/strict'
import type { WarehouseProductsTableQuery } from '../../../services/warehouseProductService'
import {
  buildSupplyNoticeSummary,
  buildWarehouseProductMetaParts,
  buildWarehouseProductStatusCountKey,
  buildWarehouseProductStatusCountQuery,
  formatWarehouseProductCount,
  isWarehouseProductRetailPriceMissing,
  planWarehouseProductStatusCounts,
  resolveWarehouseProductStatusTab,
  warehouseProductStatusTabToIsActive,
} from './warehouseProductsListView'

// 状态页签与 isActive 互相转换
assert.equal(resolveWarehouseProductStatusTab(undefined), 'all')
assert.equal(resolveWarehouseProductStatusTab(null), 'all')
assert.equal(resolveWarehouseProductStatusTab(true), 'active')
assert.equal(resolveWarehouseProductStatusTab(false), 'inactive')
assert.equal(warehouseProductStatusTabToIsActive('all'), undefined)
assert.equal(warehouseProductStatusTabToIsActive('active'), true)
assert.equal(warehouseProductStatusTabToIsActive('inactive'), false)

const baseQuery: WarehouseProductsTableQuery = {
  page: 3,
  pageSize: 200,
  searchText: ' 灯 ',
  supplierCode: 'OY0412',
  productType: 1,
  isActive: true,
  categoryGuid: 'cat-1',
  uncategorizedOnly: false,
  sortField: 'importPrice',
  sortOrder: 'ascend',
  filters: { isActive: ['true'], domesticSupplierCode: ['OY0412'], oemPrice: ['gte:5'] },
}

// 计数请求：保留其他筛选，只替换状态，取第 1 页每页 1 条；列头镜像的 isActive 一并去掉
assert.deepEqual(buildWarehouseProductStatusCountQuery(baseQuery, 'all'), {
  searchText: ' 灯 ',
  supplierCode: 'OY0412',
  productType: 1,
  categoryGuid: 'cat-1',
  uncategorizedOnly: false,
  sortField: 'importPrice',
  sortOrder: 'ascend',
  page: 1,
  pageSize: 1,
  filters: { domesticSupplierCode: ['OY0412'], oemPrice: ['gte:5'] },
  isActive: undefined,
})
assert.equal(buildWarehouseProductStatusCountQuery(baseQuery, 'inactive').isActive, false)
assert.equal(
  buildWarehouseProductStatusCountQuery({ page: 1, pageSize: 100, filters: { isActive: ['true', 'false'] } }, 'active').filters,
  undefined,
  '只剩状态筛选时 filters 应为空，避免把空对象发给后端',
)
assert.deepEqual(baseQuery.filters, { isActive: ['true'], domesticSupplierCode: ['OY0412'], oemPrice: ['gte:5'] }, '不得改动传入的查询')

// 计数缓存键：翻页、排序、状态变化不改变键；其他条件变化才改变
const countKey = buildWarehouseProductStatusCountKey(baseQuery)
assert.equal(
  buildWarehouseProductStatusCountKey({ ...baseQuery, page: 1, pageSize: 50, sortField: 'createdAt', sortOrder: 'descend', isActive: false, filters: { oemPrice: ['gte:5'], domesticSupplierCode: ['OY0412'], isActive: ['false'] } }),
  countKey,
  '翻页、排序、切换状态、filters 键顺序不同都不应改变计数键',
)
assert.equal(buildWarehouseProductStatusCountKey({ ...baseQuery, searchText: '灯' }), countKey, '关键词首尾空格不影响计数键')
assert.notEqual(buildWarehouseProductStatusCountKey({ ...baseQuery, categoryGuid: 'cat-2' }), countKey, '换分类应重新计数')
assert.notEqual(buildWarehouseProductStatusCountKey({ ...baseQuery, filters: { ...baseQuery.filters, oemPrice: ['gte:6'] } }), countKey, '列头条件变化应重新计数')

// 计数规划：当前页签用列表 total，另外两个页签发请求
const firstPlan = planWarehouseProductStatusCounts({ query: baseQuery, currentTotal: 371, cached: null, stale: false })
assert.deepEqual(firstPlan, { skip: false, key: countKey, knownCounts: { active: 371 }, tabsToFetch: ['all', 'inactive'] })

const cached = { key: countKey, counts: { all: 412, active: 371, inactive: 41 } }
assert.deepEqual(
  planWarehouseProductStatusCounts({ query: { ...baseQuery, page: 2 }, currentTotal: 371, cached, stale: false }),
  { skip: true },
  '同一组条件翻页、排序且数量未变时不重复计数',
)
assert.deepEqual(
  planWarehouseProductStatusCounts({ query: { ...baseQuery, isActive: false }, currentTotal: 41, cached, stale: false }),
  { skip: true },
  '切到另一个页签且数量与缓存一致时不重复计数',
)
assert.equal(
  planWarehouseProductStatusCounts({ query: baseQuery, currentTotal: 370, cached, stale: false }).skip,
  false,
  '列表 total 与缓存不一致说明数据有变化，应重新计数',
)
assert.equal(
  planWarehouseProductStatusCounts({ query: baseQuery, currentTotal: 371, cached, stale: true }).skip,
  false,
  '写操作后标记过期时即使条件与数量都没变也要重新计数',
)
assert.equal(
  planWarehouseProductStatusCounts({ query: baseQuery, currentTotal: 371, cached: { key: countKey, counts: { active: 371 } }, stale: false }).skip,
  false,
  '缓存里计数不完整（上一轮未完成或失败）时应重新计数',
)
assert.deepEqual(
  planWarehouseProductStatusCounts({ query: baseQuery, cached, stale: true }),
  { skip: false, key: countKey, knownCounts: {}, tabsToFetch: ['all', 'active', 'inactive'] },
  '没有列表 total（行内上下架后单独刷新）时三个页签都要请求',
)

// 缺零售价
assert.equal(isWarehouseProductRetailPriceMissing(undefined), true)
assert.equal(isWarehouseProductRetailPriceMissing(null), true)
assert.equal(isWarehouseProductRetailPriceMissing(0), true)
assert.equal(isWarehouseProductRetailPriceMissing(Number.NaN), true)
assert.equal(isWarehouseProductRetailPriceMissing(7.99), false)

// 商品单元格第二行
const middlePack = (count: number) => `中包 ${count}`
assert.deepEqual(
  buildWarehouseProductMetaParts({ barcode: ' 9330412012076 ', categoryName: 'String Lights', minOrderQuantity: 12 }, middlePack),
  ['9330412012076', 'String Lights', '中包 12'],
)
assert.deepEqual(
  buildWarehouseProductMetaParts({ barcode: '', categoryName: undefined, minOrderQuantity: 0 }, middlePack),
  ['中包 0'],
  '缺失的条码与分类直接省略，中包 0 仍显示',
)
assert.deepEqual(buildWarehouseProductMetaParts({}, middlePack), [])

// 供货计划摘要
const labels = {
  none: '未登记',
  plan: (plan: string) => ({ WillRestock: '会补货', Undecided: '尚未确定', Seasonal: '季节性商品', Discontinued: '不再供应' }[plan] ?? plan),
  expected: () => '11 月',
  overdue: '已逾期',
  watchers: (count: number) => `${count} 家门店关注`,
}
assert.deepEqual(buildSupplyNoticeSummary(null, labels), { text: '未登记', tone: 'none' })
assert.deepEqual(
  buildSupplyNoticeSummary({ supplyPlan: 'WillRestock', isOverdue: false, watchingStoreCount: 0 }, labels),
  { text: '会补货 · 11 月', tone: 'normal' },
)
assert.deepEqual(
  buildSupplyNoticeSummary({ supplyPlan: 'Undecided', isOverdue: true, watchingStoreCount: 4 }, labels),
  { text: '尚未确定 · 已逾期 · 4 家门店关注', tone: 'overdue' },
  '逾期时用「已逾期」替代预计时间并标红',
)
assert.deepEqual(
  buildSupplyNoticeSummary({ supplyPlan: 'Discontinued', isOverdue: false, watchingStoreCount: 2 }, { ...labels, expected: () => { throw new Error('不再供应不应读取预计时间') } }),
  { text: '不再供应 · 2 家门店关注', tone: 'normal' },
  '不再供应没有恢复时间',
)
assert.deepEqual(
  buildSupplyNoticeSummary({ supplyPlan: 'Seasonal', isOverdue: false, watchingStoreCount: 0 }, { ...labels, expected: () => '  ' }),
  { text: '季节性商品', tone: 'normal' },
  '预计时间为空白时不留多余分隔符',
)

assert.equal(formatWarehouseProductCount(7420), '7,420')
assert.equal(formatWarehouseProductCount(0), '0')

console.log('warehouseProductsListView.test: ok')
