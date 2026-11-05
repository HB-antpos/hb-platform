import assert from 'node:assert/strict'
import type { WarehouseCategoryNode } from '../../../../services/warehouseCategoryService'
import type { LocalSupplierCategoryNode } from '../../../../types/localSupplierCategory'
import {
  batchFilterToQuery,
  buildFilterChips,
  buildSupplierCascaderOptions,
  buildSupplierFilterOptions,
  buildWarehouseTreeData,
  countActiveFilters,
  createEmptyFilterState,
  hasActiveFilter,
  scopeFilterKey,
  toBatchFilter,
  toScopeFilter,
  toSupplierCascaderValue,
  withGrades,
  withSupplierCascader,
  withWarehouseCategories,
} from './categoryFilterLogic'

// 商品范围过滤的纯逻辑（附录 D.1 + 商品等级）：状态 → 查询参数、隐藏供应商 200、供应商级联（复用商品管理页语义）、等级多选、树数据。

const UNASSIGNED = '__unassigned__'

// ---------- 空状态 ----------
const empty = createEmptyFilterState()
assert.deepEqual(toScopeFilter(empty), {}, '没选任何条件 = 空过滤，不出现任何键')
assert.equal(countActiveFilters(empty), 0)
assert.equal(hasActiveFilter(empty), false)

// ---------- 仓库分类（多选） ----------
const w1 = withWarehouseCategories(empty, ['wh-a', ' wh-b ', 'wh-a', ''])
assert.deepEqual(w1.warehouseCategoryGuids, ['wh-a', 'wh-b'], '去空白、去空串、去重，保持首次出现的顺序')
assert.deepEqual(toScopeFilter(w1), { warehouseCategoryGuids: ['wh-a', 'wh-b'] })
assert.equal(withWarehouseCategories(w1, ['wh-a', 'wh-b']), w1, '内容没变返回原对象（不触发重新请求）')
assert.deepEqual(toScopeFilter(withWarehouseCategories(w1, [])), {}, '清空后不再带该条件')

// ---------- 商品等级（A/B/C/D/none，多选取并集） ----------
const g1 = withGrades(empty, ['none', 'B', 'A', 'X', 'B', ''])
assert.deepEqual(g1.grades, ['A', 'B', 'none'], '只认 A/B/C/D/none，去重，按固定顺序')
assert.deepEqual(toScopeFilter(g1), { grades: ['A', 'B', 'none'] })
assert.equal(withGrades(g1, ['A', 'none', 'B']), g1, '同一组等级无论点选顺序都得到同一状态')
assert.deepEqual(withGrades(g1, ['d']).grades, [], '小写 d 不是合法取值（等级字母大写）')

// ---------- 供应商级联：隐藏 200，复用商品管理页的级联语义 ----------
const s1 = withSupplierCascader(empty, ['S1'])
assert.equal(s1.supplierCode, 'S1')
assert.deepEqual(toScopeFilter(s1), { localSupplierCode: 'S1' }, '只选供应商 = 只带 localSupplierCode')

const s2 = withSupplierCascader(s1, ['S1', 'root-1', 'child-2'])
assert.equal(s2.supplierCategoryGuid, 'child-2', '取路径末端分类（含子分类由服务端展开）')
assert.deepEqual(toScopeFilter(s2), { localSupplierCode: 'S1', supplierCategoryGuids: ['child-2'] })

const s3 = withSupplierCascader(s2, ['S1', UNASSIGNED])
assert.equal(s3.supplierCategoryUnassignedOnly, true)
assert.equal(s3.supplierCategoryGuid, undefined, '「未归类」与具体分类互斥')
assert.deepEqual(toScopeFilter(s3), { localSupplierCode: 'S1', supplierCategoryUnassignedOnly: true })

const s4 = withSupplierCascader(s2, ['S2'])
assert.equal(s4.supplierCode, 'S2')
assert.equal(s4.supplierCategoryGuid, undefined, '换供应商清掉只属于旧供应商的分类')
assert.deepEqual(toScopeFilter(s4), { localSupplierCode: 'S2' })

assert.deepEqual(toScopeFilter(withSupplierCascader(s2, undefined)), {}, '清空级联 = 去掉供应商与分类条件')
assert.deepEqual(toScopeFilter(withSupplierCascader(s2, [])), {}, '空路径同样清空')
assert.equal(withSupplierCascader(s2, ['S1', 'root-1', 'child-2']), s2, '选同一个值返回原对象')

// 200 被隐藏：路径第一项是 200 时按清空处理，不能带一个看不见的条件
const hidden = withSupplierCascader(s2, ['200', 'wh-x'])
assert.equal(hidden.supplierCode, undefined, '200 不会成为供应商条件')
assert.deepEqual(toScopeFilter(hidden), {})

// 三类条件互不影响
const combined = withGrades(withWarehouseCategories(s2, ['wh-a']), ['C'])
assert.deepEqual(toScopeFilter(combined), {
  warehouseCategoryGuids: ['wh-a'],
  localSupplierCode: 'S1',
  supplierCategoryGuids: ['child-2'],
  grades: ['C'],
})
assert.equal(countActiveFilters(combined), 3, '仓库分类 / 供应商分类 / 等级各算 1 组')
const changedSupplier = withSupplierCascader(combined, ['S2'])
assert.deepEqual(changedSupplier.warehouseCategoryGuids, ['wh-a'], '换供应商不动仓库分类')
assert.deepEqual(changedSupplier.grades, ['C'], '换供应商不动等级')
assert.equal(countActiveFilters(withSupplierCascader(combined, undefined)), 2)

// ---------- 请求键：选择顺序无关 ----------
assert.equal(
  scopeFilterKey({ warehouseCategoryGuids: ['b', 'a'], grades: ['B', 'A'] }),
  scopeFilterKey({ warehouseCategoryGuids: ['a', 'b'], grades: ['A', 'B'] }),
  '数组顺序不同的同一组条件键相同',
)
assert.notEqual(scopeFilterKey({ localSupplierCode: 'S1' }), scopeFilterKey({ localSupplierCode: 'S2' }))
assert.notEqual(
  scopeFilterKey({ localSupplierCode: 'S1', supplierCategoryUnassignedOnly: true }),
  scopeFilterKey({ localSupplierCode: 'S1' }),
  '「未归类」参与请求键',
)
assert.equal(scopeFilterKey({}), scopeFilterKey(toScopeFilter(empty)))

// ---------- 级联显示值：由状态派生，分类树没加载到时只到供应商层 ----------
const tree: LocalSupplierCategoryNode[] = [
  {
    categoryGuid: 'root-1', name: '文具', depth: 0, isPromotional: false, isActive: true, productCount: 3,
    children: [{ categoryGuid: 'child-2', name: '笔', depth: 1, isPromotional: false, isActive: true, productCount: 1, children: [] }],
  },
]
assert.deepEqual(toSupplierCascaderValue(s2, (code) => (code === 'S1' ? tree : undefined)), ['S1', 'root-1', 'child-2'])
assert.deepEqual(toSupplierCascaderValue(s2, () => undefined), ['S1'], '树没加载到：只显示供应商，不把 GUID 当标签')
assert.deepEqual(toSupplierCascaderValue(s3, () => undefined), ['S1', UNASSIGNED])
assert.equal(toSupplierCascaderValue(empty, () => undefined), undefined)

// ---------- 批量修改「按当前筛选」的目标形状 ----------
assert.deepEqual(toBatchFilter({ keyword: '  笔 ', status: 'all' }, toScopeFilter(empty)), {
  keyword: '笔',
  status: 'all',
  warehouseCategoryGuids: [],
  localSupplierCode: null,
  supplierCategoryGuids: [],
  supplierCategoryUnassignedOnly: false,
  grades: [],
}, '空条件给固定形状：空数组 / null / false')
assert.deepEqual(toBatchFilter({ status: 'unset' }, toScopeFilter(combined)), {
  keyword: '',
  status: 'unset',
  warehouseCategoryGuids: ['wh-a'],
  localSupplierCode: 'S1',
  supplierCategoryGuids: ['child-2'],
  supplierCategoryUnassignedOnly: false,
  grades: ['C'],
})

// ---------- 仓库分类树 → TreeSelect 数据 ----------
const warehouse: WarehouseCategoryNode[] = [
  {
    categoryGUID: 'wh-a', categoryName: 'Stationery', isActive: true,
    children: [
      { categoryGUID: 'wh-a1', categoryName: 'Pens', isActive: true, children: [] },
      { categoryGUID: 'wh-a2', categoryName: 'Other', isActive: false, children: [] },
    ],
  },
  { categoryGUID: 'wh-b', categoryName: 'Other', isActive: true, children: [] },
]
const treeData = buildWarehouseTreeData(warehouse)
assert.equal(treeData.length, 2)
assert.equal(treeData[0].value, 'wh-a')
assert.equal(treeData[0].children?.length, 2)
assert.equal(treeData[0].children?.[0].pathLabel, 'Stationery / Pens', '路径标签带上级，区分重名分类')
assert.equal(treeData[0].children?.[1].pathLabel, 'Stationery / Other', '停用分类也保留')
assert.equal(treeData[1].pathLabel, 'Other')
assert.equal(treeData[1].children, undefined, '叶子不带 children')
assert.deepEqual(buildWarehouseTreeData(undefined), [])

// ---------- 供应商选项：隐藏 200、去重 ----------
const supplierOptions = buildSupplierFilterOptions([
  { localSupplierCode: '200', name: 'Hot Bargain' },
  { localSupplierCode: 'S1', name: 'Alpha' },
  { localSupplierCode: 'S1', name: 'Alpha dup' },
  { localSupplierCode: ' S2 ', name: null },
  { localSupplierCode: '', name: 'bad' },
])
assert.deepEqual(supplierOptions, [
  { value: 'S1', label: 'Alpha (S1)', name: 'Alpha' },
  { value: 'S2', label: 'S2', name: 'S2' },
], '隐藏 200、去重、缺名称时只显示编码')

// 级联选项：没有 200；每个供应商下首个是「未归类」伪叶子；未加载的供应商等待懒加载
const labels = { unassigned: '未归类', empty: '暂无', retry: '重试' }
const options = buildSupplierCascaderOptions({
  suppliers: [{ value: '200', label: 'Hot Bargain (200)', name: 'Hot Bargain' }, ...supplierOptions],
  getTree: (code) => (code === 'S1' ? { status: 'ready', loaded: true, nodes: tree } : undefined),
  labels,
})
assert.deepEqual(options.map((option) => option.value), ['S1', 'S2'], '级联第一层没有 200（即使传入了）')
assert.equal(options[0].children?.[0].label, '未归类', '「未归类」伪叶子排第一')
assert.equal(options[0].children?.[1].value, 'root-1')
assert.equal(options[1].isLeaf, false, '未加载的供应商交给 loadData 懒加载')
const failed = buildSupplierCascaderOptions({
  suppliers: supplierOptions,
  getTree: (code) => (code === 'S1' ? { status: 'error', loaded: false, nodes: [] } : undefined),
  labels,
})
assert.equal(failed[0].children?.[1].kind, 'retry', '加载失败给可点的重试节点')

// ---------- 条件标签（设计器商品库「可逐个删除」） ----------
{
  const lookups = {
    warehouseLabel: (guid: string) => ({ 'wh-a': '手工', 'wh-b': '圣诞', 'wh-c': '文具' } as Record<string, string>)[guid],
    supplierLabel: (code: string) => (code === 'S1' ? 'Kmart' : undefined),
    supplierCategoryLabel: (code: string, guid: string) => (code === 'S1' && guid === 'child-2' ? '文具 / 笔' : undefined),
    unassigned: '未归类',
    gradeLabel: (grade: string) => (grade === 'none' ? '未分级' : grade),
    more: (count: number) => `等 ${count} 项`,
  }
  assert.deepEqual(buildFilterChips(empty, lookups), [], '没有条件没有标签')
  assert.deepEqual(buildFilterChips(withWarehouseCategories(empty, ['wh-a']), lookups), [{ id: 'warehouse', label: '手工' }])
  assert.deepEqual(buildFilterChips(withWarehouseCategories(empty, ['wh-a', 'wh-b']), lookups), [{ id: 'warehouse', label: '手工、圣诞' }])
  assert.deepEqual(buildFilterChips(withWarehouseCategories(empty, ['wh-a', 'wh-b', 'wh-c']), lookups), [{ id: 'warehouse', label: '手工、圣诞 等 3 项' }], '多于两个时省略')
  assert.deepEqual(buildFilterChips(withSupplierCascader(empty, ['S1']), lookups), [{ id: 'supplier', label: 'Kmart' }])
  assert.deepEqual(buildFilterChips(withSupplierCascader(empty, ['S1', 'root-1', 'child-2']), lookups), [{ id: 'supplier', label: 'Kmart / 文具 / 笔' }])
  assert.deepEqual(buildFilterChips(withSupplierCascader(empty, ['S1', UNASSIGNED]), lookups), [{ id: 'supplier', label: 'Kmart / 未归类' }])
  assert.deepEqual(buildFilterChips(withSupplierCascader(empty, ['S9']), lookups), [{ id: 'supplier', label: 'S9' }], '查不到名称时退回编码')
  assert.deepEqual(buildFilterChips(withGrades(empty, ['none', 'B', 'A']), lookups), [{ id: 'grades', label: 'A、B、未分级' }])
  assert.deepEqual(buildFilterChips(combined, lookups).map((chip) => chip.id), ['warehouse', 'supplier', 'grades'], '三组条件各一个标签')

  // 「按当前筛选」的目标 → 列表查询：只带有值的条件
  assert.deepEqual(batchFilterToQuery(toBatchFilter({ keyword: '笔', status: 'all' }, toScopeFilter(combined)), { status: 'set', page: 1, pageSize: 1 }), {
    keyword: '笔', status: 'set', warehouseCategoryGuids: ['wh-a'], localSupplierCode: 'S1', supplierCategoryGuids: ['child-2'], grades: ['C'], page: 1, pageSize: 1,
  })
  assert.deepEqual(batchFilterToQuery(toBatchFilter({ status: 'all' }, {}), { status: 'set', page: 1, pageSize: 1 }), { keyword: undefined, status: 'set', page: 1, pageSize: 1 }, '空条件')
}

console.log('categoryFilterLogic.test: ok')
