import assert from 'node:assert/strict'
import {
  ACTIVE_FILTER_CATEGORY_KEY,
  ACTIVE_FILTER_SEARCH_KEY,
  buildActiveFilterChips,
  buildActiveFilterRemovalOverrides,
  formatColumnFilterValue,
  type ActiveFilterColumnMeta,
} from './activeFilters'

const textModeLabels = { contains: '包含', eq: '等于', starts: '开头是', ends: '结尾是' } as const
const columns: Record<string, ActiveFilterColumnMeta> = {
  itemNumber: { label: '货号', kind: 'text' },
  domesticSupplierCode: {
    label: '国内供应商',
    kind: 'enum',
    options: [
      { value: 'S01', text: 'S01 - 义乌' },
      { value: 'S02', text: 'S02 - 广州' },
    ],
  },
  oemPrice: { label: '标签价', kind: 'comparable' },
  isActive: { label: '状态', kind: 'enum', options: [{ value: 'true', text: '上架' }, { value: 'false', text: '下架' }] },
  productType: { label: '商品类型', kind: 'enum', options: [{ value: '1', text: '套装' }] },
  updatedAt: { label: '更新时间', kind: 'comparable' },
  localSupplierCode: { label: '澳洲供应商', kind: 'enum', options: [{ value: 'AU1', text: 'AU1 - Sydney' }] },
}
const labels = { searchText: '关键词', category: '分类', uncategorized: '未分类商品' }

// 未查询过时不显示任何标签
assert.deepEqual(buildActiveFilterChips({ query: null, labels, columns, textModeLabels }), [])

// 列头筛选值转可读摘要
assert.equal(formatColumnFilterValue(['__filter:contains:ABC'], columns.itemNumber, textModeLabels), '包含 ABC')
assert.equal(formatColumnFilterValue(['ABC'], columns.itemNumber, textModeLabels), '包含 ABC', '旧版裸文本按包含处理')
assert.equal(formatColumnFilterValue(['__filter:starts:HB'], columns.itemNumber, textModeLabels), '开头是 HB')
assert.equal(formatColumnFilterValue(['gte:5'], columns.oemPrice, textModeLabels), '≥ 5')
assert.equal(formatColumnFilterValue(['lte:9.5'], columns.oemPrice, textModeLabels), '≤ 9.5')
assert.equal(formatColumnFilterValue(['gte:1', 'lte:9'], columns.oemPrice, textModeLabels), '1 ~ 9')
assert.equal(formatColumnFilterValue(['__filter:eq:2026-01-01'], columns.updatedAt, textModeLabels), '= 2026-01-01')
assert.equal(formatColumnFilterValue(['AU1', 'AU9'], columns.localSupplierCode, textModeLabels), 'AU1 - Sydney / AU9', '未知枚举值原样显示')
assert.equal(formatColumnFilterValue(['  '], columns.itemNumber, textModeLabels), '', '空值不生成摘要')

// 完整查询：顶部镜像键只出现一次且标记为 toolbar，其余列头键为 column
const chips = buildActiveFilterChips({
  query: {
    searchText: '  帽子 ',
    supplierCode: 'S01',
    productType: undefined,
    isActive: true,
    categoryGuid: 'guid-1',
    uncategorizedOnly: false,
    filters: {
      domesticSupplierCode: ['S01'],
      isActive: ['true'],
      oemPrice: ['gte:5'],
      itemNumber: ['__filter:contains:HB'],
      unknownKey: ['x'],
    },
  },
  labels,
  columns,
  categoryLabel: '节日 / 跑马节帽子',
  textModeLabels,
})
assert.deepEqual(chips, [
  { key: ACTIVE_FILTER_SEARCH_KEY, label: '关键词', value: '帽子', source: 'toolbar' },
  { key: 'domesticSupplierCode', label: '国内供应商', value: 'S01 - 义乌', source: 'toolbar' },
  { key: ACTIVE_FILTER_CATEGORY_KEY, label: '分类', value: '节日 / 跑马节帽子', source: 'toolbar' },
  { key: 'isActive', label: '状态', value: '上架', source: 'toolbar' },
  { key: 'column:itemNumber', label: '货号', value: '包含 HB', source: 'column' },
  { key: 'column:oemPrice', label: '标签价', value: '≥ 5', source: 'column' },
  { key: 'column:unknownKey', label: 'unknownKey', value: 'x', source: 'column' },
])

// 列头多选的镜像键优先显示 filters 中的全部值；顶层单值缺失时也不能丢
const multiChips = buildActiveFilterChips({
  query: { filters: { domesticSupplierCode: ['S01', 'S02'] }, productType: 1 },
  labels,
  columns,
  textModeLabels,
})
assert.deepEqual(multiChips, [
  { key: 'domesticSupplierCode', label: '国内供应商', value: 'S01 - 义乌 / S02 - 广州', source: 'toolbar' },
  { key: 'productType', label: '商品类型', value: '套装', source: 'toolbar' },
])

// 「更多筛选」里的澳洲供应商按顶部筛选身份显示一次，排在商品类型之后、其余列头条件之前
const localSupplierChips = buildActiveFilterChips({
  query: { productType: 1, filters: { localSupplierCode: ['AU1'], itemNumber: ['__filter:eq:HB1'] } },
  labels,
  columns,
  textModeLabels,
})
assert.deepEqual(localSupplierChips, [
  { key: 'productType', label: '商品类型', value: '套装', source: 'toolbar' },
  { key: 'localSupplierCode', label: '澳洲供应商', value: 'AU1 - Sydney', source: 'toolbar' },
  { key: 'column:itemNumber', label: '货号', value: '等于 HB1', source: 'column' },
])
assert.deepEqual(buildActiveFilterRemovalOverrides('localSupplierCode', { localSupplierCode: ['AU1'], oemPrice: ['gte:5'] }), {
  page: 1,
  filters: { oemPrice: ['gte:5'] },
})

// 只看未分类优先于分类 GUID；isActive=false 也要显示
const uncategorizedChips = buildActiveFilterChips({
  query: { uncategorizedOnly: true, categoryGuid: 'guid-1', isActive: false },
  labels,
  columns,
  textModeLabels,
})
assert.deepEqual(uncategorizedChips, [
  { key: ACTIVE_FILTER_CATEGORY_KEY, label: '分类', value: '未分类商品', source: 'toolbar' },
  { key: 'isActive', label: '状态', value: '下架', source: 'toolbar' },
])

// 分类名称未解析时回退为 GUID
assert.equal(
  buildActiveFilterChips({ query: { categoryGuid: 'guid-2' }, labels, columns, textModeLabels })[0].value,
  'guid-2',
)

// 移除标签 → 查询覆盖参数，且总回到第 1 页
const filters = { domesticSupplierCode: ['S01'], oemPrice: ['gte:5'], productType: ['1'], isActive: ['true'] }
assert.deepEqual(buildActiveFilterRemovalOverrides(ACTIVE_FILTER_SEARCH_KEY, filters), { page: 1, searchText: '', filters })
assert.deepEqual(buildActiveFilterRemovalOverrides(ACTIVE_FILTER_CATEGORY_KEY, filters), {
  page: 1,
  categoryGuid: undefined,
  uncategorizedOnly: false,
  filters,
})
assert.deepEqual(buildActiveFilterRemovalOverrides('domesticSupplierCode', filters), {
  page: 1,
  supplierCode: undefined,
  filters: { oemPrice: ['gte:5'], productType: ['1'], isActive: ['true'] },
})
assert.deepEqual(buildActiveFilterRemovalOverrides('productType', filters), {
  page: 1,
  productType: undefined,
  filters: { domesticSupplierCode: ['S01'], oemPrice: ['gte:5'], isActive: ['true'] },
})
assert.deepEqual(buildActiveFilterRemovalOverrides('isActive', filters), {
  page: 1,
  isActive: undefined,
  filters: { domesticSupplierCode: ['S01'], oemPrice: ['gte:5'], productType: ['1'] },
})
assert.deepEqual(buildActiveFilterRemovalOverrides('column:oemPrice', filters), {
  page: 1,
  filters: { domesticSupplierCode: ['S01'], productType: ['1'], isActive: ['true'] },
})
assert.equal(filters.oemPrice[0], 'gte:5', '移除不得改动传入的 filters 对象')

console.log('activeFilters.test: ok')
