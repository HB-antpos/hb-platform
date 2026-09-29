import assert from 'node:assert/strict'
import {
  buildColumnFilterChips,
  buildToolbarFilterChips,
  formatNumberRange,
  getPresetStoreRecordCountRange,
  summarizeColumnFilterValue,
  type ColumnFilterMeta,
  type ToolbarFilterChipLabels,
} from './activeFilterChips'

const labels: ToolbarFilterChipLabels = {
  keyword: '关键词',
  supplier: '澳洲供应商',
  supplierCategory: '供应商分类',
  supplierCategoryUnassigned: '未归类',
  category: '商品分类',
  warehouseCategory: '仓库分类',
  status: '状态',
  setType: '套装',
  storeRecord: '分店记录',
  active: '启用',
  inactive: '禁用',
  setProduct: '套装',
  normalProduct: '单品',
  hasRecords: '有记录',
  noRecords: '无记录',
}

const lookups = {
  supplierName: (code: string) => (code === 'S01' ? '悉尼供应商' : undefined),
  categoryPath: (guid: string) => (guid === 'c-leaf' ? ['文具', '笔'] : undefined),
  warehouseCategoryPath: (guid: string) => (guid === 'w-1' ? ['仓库A'] : undefined),
}

// 预设分店记录范围应与 handleSearch 的折算一致
assert.deepEqual(getPresetStoreRecordCountRange('all'), { min: undefined, max: undefined })
assert.deepEqual(getPresetStoreRecordCountRange('hasRecords'), { min: 1, max: undefined })
assert.deepEqual(getPresetStoreRecordCountRange('noRecords'), { min: 0, max: 0 })

// 区间摘要
assert.equal(formatNumberRange(1, 5), '1 – 5')
assert.equal(formatNumberRange(3, 3), '= 3')
assert.equal(formatNumberRange(2, undefined), '≥ 2')
assert.equal(formatNumberRange(undefined, 9), '≤ 9')
assert.equal(formatNumberRange(0, undefined), '≥ 0')
assert.equal(formatNumberRange('', ''), '')

// 没有生效条件时不产生标签
assert.deepEqual(buildToolbarFilterChips({ storeRecordCountMode: 'all', keyword: '  ' }, lookups, labels), [])

// 全部条件都生效：供应商显示名称，分类显示完整路径，未知 GUID 原样显示
const allChips = buildToolbarFilterChips(
  {
    keyword: ' pen ',
    supplierCode: 'S01',
    categoryGuid: 'c-leaf',
    warehouseCategoryGuid: 'w-unknown',
    isActive: false,
    isSet: true,
    storeRecordCountMode: 'hasRecords',
    storeRecordCountMin: 1,
  },
  lookups,
  labels,
)
assert.deepEqual(allChips.map((chip) => [chip.key, chip.label, chip.value]), [
  ['keyword', '关键词', 'pen'],
  ['supplierCode', '澳洲供应商', '悉尼供应商'],
  ['categoryGuid', '商品分类', '文具 / 笔'],
  ['warehouseCategoryGuid', '仓库分类', 'w-unknown'],
  ['isActive', '状态', '禁用'],
  ['isSet', '套装', '套装'],
  ['storeRecordCount', '分店记录', '有记录'],
])

// 未知供应商编码回退为编码本身；isSet=false 显示单品
const fallbackChips = buildToolbarFilterChips(
  { supplierCode: 'S99', isSet: false, isActive: true, storeRecordCountMode: 'noRecords', storeRecordCountMin: 0, storeRecordCountMax: 0 },
  lookups,
  labels,
)
assert.deepEqual(fallbackChips.map((chip) => chip.value), ['S99', '启用', '单品', '无记录'])

// 自定义范围按生效态区间显示；两端都空时不显示
assert.equal(
  buildToolbarFilterChips({ storeRecordCountMode: 'custom', storeRecordCountMin: 2, storeRecordCountMax: 8 }, lookups, labels)[0]?.value,
  '2 – 8',
)
assert.equal(
  buildToolbarFilterChips({ storeRecordCountMode: 'custom', storeRecordCountMax: 4 }, lookups, labels)[0]?.value,
  '≤ 4',
)
assert.deepEqual(buildToolbarFilterChips({ storeRecordCountMode: 'custom' }, lookups, labels), [])

// 供应商分类：排在供应商之后；已加载的树显示名称路径，未加载回退 GUID
const supplierCategoryLookups = {
  ...lookups,
  supplierCategoryPath: (code: string, guid: string) => (code === 'S01' && guid === 'sc-leaf' ? ['文具', '笔'] : undefined),
}
assert.deepEqual(
  buildToolbarFilterChips(
    { supplierCode: 'S01', supplierCategoryGuid: 'sc-leaf', storeRecordCountMode: 'all' },
    supplierCategoryLookups,
    labels,
  ).map((chip) => [chip.key, chip.label, chip.value]),
  [
    ['supplierCode', '澳洲供应商', '悉尼供应商'],
    ['supplierCategory', '供应商分类', '文具 / 笔'],
  ],
)
assert.equal(
  buildToolbarFilterChips({ supplierCode: 'S01', supplierCategoryGuid: 'sc-unknown', storeRecordCountMode: 'all' }, lookups, labels)[1]?.value,
  'sc-unknown',
)
// 仅未归类：显示「未归类」，200 下也显示
assert.deepEqual(
  buildToolbarFilterChips({ supplierCode: '200', supplierCategoryUnassignedOnly: true, storeRecordCountMode: 'all' }, lookups, labels)
    .map((chip) => [chip.key, chip.value]),
  [['supplierCode', '200'], ['supplierCategory', '未归类']],
)
// 200 的供应商分类即仓库分类：只出仓库分类标签，不重复出供应商分类标签
assert.deepEqual(
  buildToolbarFilterChips(
    { supplierCode: '200', supplierCategoryGuid: 'w-1', warehouseCategoryGuid: 'w-1', storeRecordCountMode: 'all' },
    lookups,
    labels,
  ).map((chip) => chip.key),
  ['supplierCode', 'warehouseCategoryGuid'],
)

// 列头筛选摘要
const summaryLabels = {
  textOperators: { contains: '包含', equals: '等于', startsWith: '开头是', endsWith: '结尾是' },
  listSeparator: '、',
}
const columnMeta: Record<string, ColumnFilterMeta> = {
  productName: { label: '商品名称', kind: 'text' },
  retailPrice: { label: '零售价', kind: 'number' },
  createdAt: { label: '创建时间', kind: 'date' },
  isActive: { label: '状态', kind: 'enum', options: [{ text: '启用', value: 'true' }, { text: '禁用', value: 'false' }] },
}
assert.equal(summarizeColumnFilterValue('{"operator":"contains","value":"帽子"}', columnMeta.productName, summaryLabels), '包含 帽子')
assert.equal(summarizeColumnFilterValue('{"operator":"equals","value":"A1"}', columnMeta.productName, summaryLabels), '等于 A1')
assert.equal(summarizeColumnFilterValue('{"operator":"equals","value":"5"}', columnMeta.retailPrice, summaryLabels), '= 5')
assert.equal(summarizeColumnFilterValue('{"operator":"gte","value":"5.5"}', columnMeta.retailPrice, summaryLabels), '≥ 5.5')
assert.equal(summarizeColumnFilterValue('{"operator":"between","min":"1","max":"9"}', columnMeta.retailPrice, summaryLabels), '1 – 9')
assert.equal(summarizeColumnFilterValue('{"operator":"between","min":"","max":"9"}', columnMeta.retailPrice, summaryLabels), '≤ 9')
assert.equal(
  summarizeColumnFilterValue('{"operator":"between","start":"2026-01-01","end":"2026-01-31"}', columnMeta.createdAt, summaryLabels),
  '2026-01-01 – 2026-01-31',
)
assert.equal(summarizeColumnFilterValue('{"operator":"lte","value":"2026-02-01"}', columnMeta.createdAt, summaryLabels), '≤ 2026-02-01')
assert.equal(summarizeColumnFilterValue('false', columnMeta.isActive, summaryLabels), '禁用')
assert.equal(summarizeColumnFilterValue('unknown', columnMeta.isActive, summaryLabels), 'unknown')
// 非 JSON 的普通字符串不应被当成 token
assert.equal(summarizeColumnFilterValue('{broken', undefined, summaryLabels), '{broken')

const columnChips = buildColumnFilterChips(
  {
    isActive: ['true', 'false'],
    retailPrice: ['{"operator":"gte","value":"3"}'],
    empty: [],
    noMeta: ['x'],
  },
  columnMeta,
  summaryLabels,
)
assert.deepEqual(columnChips, [
  { key: 'column:isActive', filterKey: 'isActive', label: '状态', value: '启用、禁用' },
  { key: 'column:retailPrice', filterKey: 'retailPrice', label: '零售价', value: '≥ 3' },
  { key: 'column:noMeta', filterKey: 'noMeta', label: 'noMeta', value: 'x' },
])

console.log('activeFilterChips.test: ok')
