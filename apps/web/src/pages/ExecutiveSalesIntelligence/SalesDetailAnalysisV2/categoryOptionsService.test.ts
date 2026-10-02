import assert from 'node:assert/strict'
import { buildSalesDetailSupplierCategoryTree, fetchSalesDetailCategoryOptions, normalizeSalesDetailCategoryOptions } from './categoryOptionsService'

const warehouse = normalizeSalesDetailCategoryOptions({ success: true, data: { warehouseCategories: [
  { categoryGUID: 'ROOT', categoryName: '家居', children: [
    { categoryGUID: 'CHILD', categoryName: '收纳', children: [] },
  ] },
] } })
assert.deepEqual(warehouse[0]?.options.map(option => [option.guid, option.name]), [
  ['ROOT', '家居'], ['CHILD', '家居 / 收纳'],
], '仓库分类的子节点必须可选')
assert.deepEqual(warehouse[0]?.options.map(option => [option.guid, option.label, option.parentGuid]), [
  ['ROOT', '家居', undefined], ['CHILD', '收纳', 'ROOT'],
], '分类树应保留独立节点名称和父子关系')

const inactiveParent = normalizeSalesDetailCategoryOptions({ data: { warehouseCategories: [
  { categoryGuid: 'OLD', name: '旧分类', isActive: false, children: [
    { categoryGuid: 'ACTIVE', name: '新分类', isActive: true, children: [] },
  ] },
] } })
assert.deepEqual(inactiveParent[0]?.options.map(option => [option.guid, option.name]), [
  ['ACTIVE', '新分类'],
], '停用的父分类不应隐藏启用的子分类')
assert.equal(inactiveParent[0]?.options[0]?.parentGuid, undefined, '停用父类下的启用子类应成为可见根节点')

const supplier = normalizeSalesDetailCategoryOptions({ success: true, data: { supplierCategories: [
  { supplierCode: '200', categories: [{ categoryGuid: 'HB', name: '文具', children: [] }] },
  { supplierCode: '240', categories: [{ categoryGuid: 'AU', name: 'Cards', children: [
    { categoryGuid: 'AU-CHILD', name: 'Birthday', children: [] },
  ] }] },
] } })
assert.deepEqual(supplier.map(group => [group.supplierCode, ...group.options.map(option => option.guid)]), [
  ['200', 'HB'], ['240', 'AU', 'AU-CHILD'],
], '多个供应商的分类树必须保留各自子分类')
const tree = buildSalesDetailSupplierCategoryTree(supplier, code => code === '240' ? 'Dats · 240' : 'Hot Bargain · 200')
assert.deepEqual(tree.map(group => [group.title, group.disableCheckbox, group.children?.map(node => node.title)]), [
  ['Hot Bargain · 200', true, ['文具']], ['Dats · 240', true, ['Cards']],
], '供应商仅用于树分组，不能作为可勾选分类')
assert.equal(tree[1]?.children?.[0]?.children?.[0]?.title, 'Birthday', '子分类应显示自己的名称而非重复完整路径')
assert.equal(tree[1]?.children?.[0]?.children?.[0]?.searchText, 'Dats · 240 Cards / Birthday', '树搜索仍可匹配完整路径')

const warehouseTree = buildSalesDetailSupplierCategoryTree(warehouse, () => '').flatMap(group => group.children ?? [])
assert.deepEqual(warehouseTree.map(node => [node.value, node.title, node.children?.map(child => [child.value, child.title])]), [
  ['ROOT', '家居', [['CHILD', '收纳']]],
], '仓库树直接展示真实分类层级，每个节点保留分类 GUID')
assert.ok(warehouseTree[0]?.children?.[0]?.searchText.includes('家居 / 收纳'), '仓库树搜索应匹配完整分类路径')
const activeWarehouseTree = buildSalesDetailSupplierCategoryTree(inactiveParent, () => '').flatMap(group => group.children ?? [])
assert.equal(activeWarehouseTree[0]?.value, 'ACTIVE', '启用的孤立子分类仍可作为树根选择')

const originalFetch = globalThis.fetch
globalThis.fetch = (async (input: RequestInfo | URL) => {
  const url = new URL(String(input), 'http://localhost')
  assert.equal(url.pathname, '/api/react/v1/dashboard/sales-detail-view/category-options')
  assert.deepEqual(url.searchParams.getAll('supplierCodes'), ['200', '240'])
  return new Response(JSON.stringify({ success: true, data: { supplierCategories: [] } }),
    { headers: { 'content-type': 'application/json' } })
}) as typeof fetch
try {
  await fetchSalesDetailCategoryOptions('australia', ['200', '240'], new AbortController().signal)
} finally {
  globalThis.fetch = originalFetch
}
console.log('销售明细分类选项树及多供应商请求：通过')
