import assert from 'node:assert/strict'
import type { WarehouseCategoryNode } from '../../../services/warehouseCategoryService'
import type { LocalSupplierCategoryNode } from '../../../types/localSupplierCategory'
import { SUPPLIER_CATEGORY_UNASSIGNED_VALUE } from './supplierCategoryFilter'
import {
  SUPPLIER_CATEGORY_EMPTY_VALUE,
  SUPPLIER_CATEGORY_RETRY_VALUE,
  buildSupplierCategoryCascaderOptions,
  buildSupplierCategoryIndex,
  findSupplierCategoryGuidPath,
  findSupplierCategoryNamePath,
  findWarehouseCategoryGuidPath,
  mapSupplierTreeToCascaderOptions,
  resolveBatchSupplierCategoryScope,
  searchSupplierCategoryOptions,
  sortSuppliersForCascader,
  splitSupplierCategoryPath,
} from './supplierCategoryOptions'
import type { SupplierCategoryTreeEntry } from './supplierCategoryTreeCache'

function node(categoryGuid: string, name: string, children: LocalSupplierCategoryNode[] = [], extra: Partial<LocalSupplierCategoryNode> = {}): LocalSupplierCategoryNode {
  return { categoryGuid, name, depth: 0, isPromotional: false, isActive: true, productCount: 0, children, ...extra }
}

const supplierTree = [
  node('C-ROOT', 'Office', [node('c-leaf', 'Pens'), node('c-off', 'Old', [], { isActive: false })]),
  node('c-promo', 'Clearance', [], { isPromotional: true }),
]
const warehouseTree: WarehouseCategoryNode[] = [
  { categoryGUID: 'w-root', categoryName: '文具', isActive: true, children: [{ categoryGUID: 'w-leaf', categoryName: '笔', isActive: true }] },
]
const labels = { unassigned: '未归类', empty: '暂无网站分类', retry: '加载失败，点击重试' }

// 200 置顶，其余保持原顺序
assert.deepEqual(
  sortSuppliersForCascader([{ value: '240' }, { value: '201' }, { value: '200' }]).map((item) => item.value),
  ['200', '240', '201'],
)

// 停用节点不进入级联选项；促销分类仍可用于筛选
assert.deepEqual(mapSupplierTreeToCascaderOptions(supplierTree), [
  { value: 'C-ROOT', label: 'Office', kind: 'category', children: [{ value: 'c-leaf', label: 'Pens', kind: 'category', children: undefined }] },
  { value: 'c-promo', label: 'Clearance', kind: 'category', children: undefined },
])

const trees: Record<string, SupplierCategoryTreeEntry> = {
  '240': { status: 'ready', loaded: true, nodes: supplierTree },
  '201': { status: 'ready', loaded: true, nodes: [] },
  '227': { status: 'error', loaded: false, nodes: [], error: new Error('x') },
  '243': { status: 'loading', loaded: false, nodes: [] },
}
const options = buildSupplierCategoryCascaderOptions({
  suppliers: [
    { value: '240', label: 'DATS (240)' },
    { value: '201', label: 'Yatsal (201)' },
    { value: '227', label: 'Malmar (227)' },
    { value: '243', label: 'Brazco (243)' },
    { value: '218', label: 'PJ SAS (218)' },
    { value: '200', label: 'Hot Bargain (200)' },
  ],
  warehouseTree,
  getTree: (code) => trees[code],
  labels,
})
assert.deepEqual(options.map((option) => option.value), ['200', '240', '201', '227', '243', '218'])
const [hot, dats, yatsal, malmar, brazco, pjSas] = options
// 每个供应商首个子节点都是伪叶子「未归类」
for (const option of [hot, dats, yatsal, malmar]) {
  assert.equal(option.children?.[0]?.value, SUPPLIER_CATEGORY_UNASSIGNED_VALUE)
  assert.equal(option.children?.[0]?.label, '未归类')
}
// 200 挂仓库分类树
assert.deepEqual(hot.children?.slice(1).map((option) => option.value), ['w-root'])
assert.equal(hot.children?.[1]?.children?.[0]?.value, 'w-leaf')
// 已加载：挂供应商分类树
assert.deepEqual(dats.children?.slice(1).map((option) => option.value), ['C-ROOT', 'c-promo'])
// 空树：禁用提示节点
assert.deepEqual(yatsal.children?.[1], { value: SUPPLIER_CATEGORY_EMPTY_VALUE, label: '暂无网站分类', kind: 'empty', disabled: true, isLeaf: true })
// 失败：重试节点（必须有 children，否则 antd 一直转圈）
assert.equal(malmar.children?.[1]?.value, SUPPLIER_CATEGORY_RETRY_VALUE)
assert.equal(malmar.children?.[1]?.disabled, undefined)
// 加载中/未加载：不给 children，交给 loadData
assert.equal(brazco.children, undefined)
assert.equal(brazco.isLeaf, false)
assert.equal(pjSas.children, undefined)
assert.equal(pjSas.isLeaf, false)

// 搜索供应商与已加载分类；停用/提示节点不能成为可选结果，结果保留完整路径。
assert.deepEqual(searchSupplierCategoryOptions(options, 'dats').map((result) => result.valuePath), [['240']])
assert.deepEqual(searchSupplierCategoryOptions(options, 'PENS').map((result) => result.valuePath), [['240', 'C-ROOT', 'c-leaf']])
assert.deepEqual(searchSupplierCategoryOptions(options, '笔').map((result) => result.valuePath), [['200', 'w-root', 'w-leaf']])
assert.deepEqual(searchSupplierCategoryOptions(options, 'malmar').map((result) => result.valuePath), [['227']])
assert.deepEqual(searchSupplierCategoryOptions(options, '加载失败'), [])
assert.deepEqual(searchSupplierCategoryOptions(options, 'old'), [])
assert.equal(searchSupplierCategoryOptions(options, '  ').length, 0)
assert.equal(searchSupplierCategoryOptions(options, 'a', 2).length, 2)

// 刷新期间仍展示旧数据
const refreshing = buildSupplierCategoryCascaderOptions({
  suppliers: [{ value: '240', label: 'DATS' }],
  warehouseTree: [],
  getTree: () => ({ status: 'loading', loaded: true, nodes: supplierTree }),
  labels,
})
assert.equal(refreshing[0].children?.length, 3)

// 路径查找不区分 GUID 大小写
assert.deepEqual(findSupplierCategoryGuidPath(supplierTree, 'c-root'), ['C-ROOT'])
assert.deepEqual(findSupplierCategoryGuidPath(supplierTree, 'C-LEAF'), ['C-ROOT', 'c-leaf'])
assert.equal(findSupplierCategoryGuidPath(supplierTree, 'missing'), undefined)
assert.equal(findSupplierCategoryGuidPath(supplierTree, undefined), undefined)
assert.deepEqual(findSupplierCategoryNamePath(supplierTree, 'c-leaf'), ['Office', 'Pens'])
assert.deepEqual(findWarehouseCategoryGuidPath(warehouseTree, 'w-leaf'), ['w-root', 'w-leaf'])
assert.equal(findWarehouseCategoryGuidPath(warehouseTree, 'nope'), undefined)
const index = buildSupplierCategoryIndex(supplierTree)
assert.equal(index.size, 4)
assert.deepEqual(index.get('c-root')?.namePath, ['Office'])

// 服务端完整路径拆分
assert.deepEqual(splitSupplierCategoryPath('Office >  Pens > Gel'), ['Office', 'Pens', 'Gel'])
assert.deepEqual(splitSupplierCategoryPath(undefined), [])

// 批量编辑范围判定
const rows = [
  { productCode: 'P1', localSupplierCode: '240' },
  { productCode: 'P2', localSupplierCode: '240' },
  { productCode: 'P3', localSupplierCode: '201' },
  { productCode: 'P4', localSupplierCode: '200' },
  { productCode: 'P5' },
]
assert.deepEqual(resolveBatchSupplierCategoryScope({ selectedKeys: ['P1', 'P2'], rows }), { status: 'enabled', supplierCode: '240' })
assert.deepEqual(resolveBatchSupplierCategoryScope({ selectedKeys: ['P1', 'P2'], rows, nextSupplierCode: '240' }), { status: 'enabled', supplierCode: '240' })
assert.deepEqual(resolveBatchSupplierCategoryScope({ selectedKeys: ['P1', 'P2'], rows, nextSupplierCode: '201' }), { status: 'supplierChanging', supplierCode: '240' })
assert.deepEqual(resolveBatchSupplierCategoryScope({ selectedKeys: ['P1', 'P3'], rows }), { status: 'mixedSuppliers' })
assert.deepEqual(resolveBatchSupplierCategoryScope({ selectedKeys: ['P4'], rows }), { status: 'hotBargain', supplierCode: '200' })
assert.deepEqual(resolveBatchSupplierCategoryScope({ selectedKeys: ['P5'], rows }), { status: 'noSupplier' })
assert.deepEqual(resolveBatchSupplierCategoryScope({ selectedKeys: ['P1', 'P9'], rows }), { status: 'unknownRows' })
assert.deepEqual(resolveBatchSupplierCategoryScope({ selectedKeys: [], rows }), { status: 'unknownRows' })

console.log('supplierCategoryOptions.test: ok')
