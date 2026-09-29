import {
  buildPanelKeys,
  buildRequestKey,
  defaultProductSort,
  describeExportFilters,
  describeProductSort,
  formatShare,
  matchesSupplierSearch,
  pageRange,
  readStoredFlag,
  resolveProductSort,
  resolveSupplierToggle,
  shouldHandleEscape,
  toAntdSortOrder,
  toggleSelection,
  writeStoredFlag,
  type BoardFilterState,
} from './logic'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) {
    throw new Error(message)
  }
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  const actualText = JSON.stringify(actual)
  const expectedText = JSON.stringify(expected)
  if (actualText !== expectedText) {
    throw new Error(`${message}: expected ${expectedText}, got ${actualText}`)
  }
}

const base: BoardFilterState = {
  dateRange: { startDate: '2026-09-19', endDate: '2026-09-19' },
  scopeKey: 'ALL',
  branch: null,
  supplier: null,
  product: null,
  keyword: '',
  productSort: defaultProductSort,
  pageIndex: 1,
  pageSize: 80,
}
const branch = { code: '1012', label: 'Glendale', detail: '1012' }
const supplier = { code: 'HB215', label: '玛索文具', detail: 'HB215' }
const product = { code: 'P-1', label: 'MP165-24', supplierCode: 'HB215' }

// 各栏只依赖其他栏：选中分店后，分店栏的键不变（不显示加载），供应商栏、商品栏和 KPI 的键变化。
const initialKeys = buildPanelKeys(base)
const branchKeys = buildPanelKeys({ ...base, branch })
assertEqual(branchKeys.stores, initialKeys.stores, '选中分店不应让分店栏过期')
assert(branchKeys.suppliers !== initialKeys.suppliers, '选中分店应让供应商栏过期')
assert(branchKeys.products !== initialKeys.products, '选中分店应让商品栏过期')
assert(branchKeys.summary !== initialKeys.summary, '选中分店应让 KPI 过期')

const supplierKeys = buildPanelKeys({ ...base, supplier })
assertEqual(supplierKeys.suppliers, initialKeys.suppliers, '选中供应商不应让供应商栏过期')
assert(supplierKeys.stores !== initialKeys.stores, '选中供应商应让分店栏过期')

const productKeys = buildPanelKeys({ ...base, product })
assertEqual(productKeys.products, initialKeys.products, '选中商品不应让商品栏过期')
assert(productKeys.stores !== initialKeys.stores && productKeys.suppliers !== initialKeys.suppliers, '选中商品应让分店栏和供应商栏过期')

const sortKeys = buildPanelKeys({ ...base, productSort: { field: 'quantity', order: 'desc' }, pageIndex: 2, keyword: ' canvas ' })
assertEqual([sortKeys.stores, sortKeys.suppliers, sortKeys.summary], [initialKeys.stores, initialKeys.suppliers, initialKeys.summary], '排序、翻页、搜索只影响商品栏')
assert(sortKeys.products !== initialKeys.products, '排序、翻页、搜索应让商品栏过期')
assertEqual(buildPanelKeys({ ...base, keyword: ' canvas ' }).products, buildPanelKeys({ ...base, keyword: 'canvas' }).products, '关键词首尾空白不应产生新查询')
assert(buildPanelKeys({ ...base, scopeKey: '1012|1005' }).stores !== initialKeys.stores, '授权范围变化必须让所有栏过期')
assert(buildRequestKey({ ...base, product }) !== buildRequestKey(base), '客户端缓存键必须包含全部请求参数')

// 选中与取消
assertEqual(toggleSelection(null, branch), branch, '首次点击应选中')
assertEqual(toggleSelection(branch, { ...branch }), null, '再次点击同一行应取消')

// 改选供应商：商品不属于新供应商时解除，属于时保留；取消供应商时保留商品。
assertEqual(resolveSupplierToggle({ supplier: null, product }, { code: 'HB102', label: '华瑞文具' }), { supplier: { code: 'HB102', label: '华瑞文具' }, product: null }, '改选其他供应商应解除商品')
assertEqual(resolveSupplierToggle({ supplier: null, product }, supplier), { supplier, product }, '选中商品所属供应商应保留商品')
assertEqual(resolveSupplierToggle({ supplier, product }, supplier), { supplier: null, product }, '取消供应商不应影响已选商品')

// antd 排序回调 → 服务端排序参数
assertEqual(resolveProductSort('quantity', 'descend'), { field: 'quantity', order: 'desc' }, '数量降序')
assertEqual(resolveProductSort('itemNumber', 'ascend'), { field: 'itemNumber', order: 'asc' }, '货号升序')
assertEqual(resolveProductSort('unitPrice', null), defaultProductSort, '第三次点击恢复默认金额降序')
assertEqual(resolveProductSort('productImage', 'ascend'), { field: 'amount', order: 'asc' }, '未知列回退到金额')
assertEqual(toAntdSortOrder({ field: 'unitPrice', order: 'asc' }, 'unitPrice'), 'ascend', '受控排序应标出当前列')
assertEqual(toAntdSortOrder({ field: 'unitPrice', order: 'asc' }, 'amount'), null, '其他列不显示排序状态')
assertEqual(describeProductSort({ field: 'itemNumber', order: 'asc' }), '按货号升序', '分页说明应描述当前排序')

// 占比、搜索、分页区间
assertEqual(formatShare(25, 200), '12.5%', '占比保留一位小数')
assertEqual(formatShare(25, 0), '-', '分母为 0 时不显示占比')
assert(matchesSupplierSearch({ supplierCode: 'HB215', supplierName: '玛索文具' }, ' hb2 '), '供应商搜索应匹配代码且不区分大小写')
assert(matchesSupplierSearch({ supplierCode: 'HB215', supplierName: '玛索文具' }, '玛索'), '供应商搜索应匹配名称')
assert(!matchesSupplierSearch({ supplierCode: 'HB215', supplierName: '玛索文具' }, 'HB102'), '供应商搜索不应误匹配')
assertEqual(pageRange(2, 80, 150), [81, 150], '最后一页区间应截断到总数')
assertEqual(pageRange(1, 80, 0), [0, 0], '无数据时区间为 0')
assert(shouldHandleEscape(null), '无目标元素时可处理 Esc')

// 导出说明：顺序与三栏一致，选中商品不写入（商品栏不被自身收窄）；名称与代码相同时不重复。
assertEqual(describeExportFilters(base), '全部国内供应商 · 全部分店', '无筛选时说明全部范围')
assertEqual(describeExportFilters({ ...base, branch, supplier, keyword: ' 笔 ' }), '国内供应商 玛索文具（HB215） · 分店 Glendale（1012） · 搜索「笔」', '供应商在前、分店在后，并附搜索词')
assertEqual(describeExportFilters({ ...base, branch: { code: '1012', label: '1012' } }), '分店 1012', '名称缺失时只写代码')

// 偏好存储：默认折叠；存储不可用或抛错时退回默认值且不影响使用。
const memory = new Map<string, string>()
const memoryStorage = () => ({ getItem: (key: string) => memory.get(key) ?? null, setItem: (key: string, value: string) => { memory.set(key, value) } })
assertEqual(readStoredFlag('stats', memoryStorage), false, '未记录时默认折叠')
writeStoredFlag('stats', true, memoryStorage)
assertEqual(readStoredFlag('stats', memoryStorage), true, '记住展开')
writeStoredFlag('stats', false, memoryStorage)
assertEqual(readStoredFlag('stats', memoryStorage), false, '记住折叠')
const throwingStorage = () => ({ getItem: () => { throw new Error('denied') }, setItem: () => { throw new Error('denied') } })
assertEqual(readStoredFlag('stats', throwingStorage), false, '读取抛错时按默认折叠')
writeStoredFlag('stats', true, throwingStorage)
assertEqual(readStoredFlag('stats', () => null), false, '无存储时按默认折叠')

console.log('compactSalesBoard logic: ok')
