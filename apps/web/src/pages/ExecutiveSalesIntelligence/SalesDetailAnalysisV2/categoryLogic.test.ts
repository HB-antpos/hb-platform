import assert from 'node:assert/strict'
import { ancestorKeys, flattenCategoryTree, nodeKey, resolveCategoryFocus, supplierOpenKeys } from './categoryLogic'
import { normalizeCategoryReport, UNASSIGNED_CATEGORY_KEY, type CategoryNode, type CategoryReport } from './categoryReportService'

const last = <T,>(items: readonly T[], offset = 1): T => items[items.length - offset]

const metrics = (revenue: number, compareRevenue: number | null = null) => ({ revenue, compareRevenue, quantity: 1, compareQuantity: null,
  grossProfit: null, compareGrossProfit: null, grossMarginRate: null, compareGrossMarginRate: null, productCount: 1, compareProductCount: null })
const node = (categoryGuid: string, name: string, revenue: number, children: CategoryNode[] = []): CategoryNode =>
  ({ ...metrics(revenue), categoryGuid, name, depth: 0, isActive: true, children })

// 一级分类 10 个（超过 8 + 1），第一项有两个子分类；另有未归类。
const kitchen = node('k', 'Kitchen And Household', 100, [node('k1', 'Food Storage', 60), node('k2', 'Bakeware', 40)])
const others = Array.from({ length: 9 }, (_, index) => node(`o${index}`, `Other ${index}`, 9 - index))
const report: CategoryReport = {
  summary: metrics(400), unassigned: metrics(50),
  suppliers: [
    { ...metrics(300), supplierCode: '240', supplierName: 'Dats', categorySource: 'supplier', categories: [kitchen, ...others],
      unassigned: { ...node(UNASSIGNED_CATEGORY_KEY, '未归类', 50) } },
    { ...metrics(100), supplierCode: '200', supplierName: 'Hot Bargain', categorySource: 'warehouse', categories: [node('W', '10.TOYS', 100)] },
  ],
}

const collapsed = flattenCategoryTree(report, { expanded: new Set(), showAll: new Set() })
assert.deepEqual(collapsed.map(row => row.key), ['240', '200'], '未展开时只显示供应商行')
assert.equal(collapsed[0].share, 0.75, '供应商占所选供应商合计')
assert.equal(collapsed[0].unassignedShare, 50 / 300, '供应商行带出未归类占比')
assert.equal(collapsed[1].source, 'warehouse')

const opened = flattenCategoryTree(report, { expanded: new Set(['240', nodeKey('240', 'k')]), showAll: new Set() })
assert.deepEqual(opened.slice(0, 4).map(row => [row.name, row.depth]), [['Dats', 0], ['Kitchen And Household', 1], ['Food Storage', 2], ['Bakeware', 2]])
assert.equal(opened[1].share, 100 / 300, '一级分类占供应商')
assert.equal(opened[2].share, 0.6, '子分类占父分类')
const more = opened.find(row => row.kind === 'more')!
assert.equal(more.hiddenCount, 2, '10 个一级分类显示前 8 个，其余收起')
assert.equal(more.hiddenRevenue, 2 + 1, '收起行带出被收起项的合计营业额')
assert.equal(more.parentKey, '240')
assert.equal(last(opened, 2).kind, 'unassigned', '未归类排在该供应商最后')
assert.equal(last(opened, 2).share, 50 / 300)
assert.equal(last(opened).key, '200')

const all = flattenCategoryTree(report, { expanded: new Set(['240']), showAll: new Set(['240']) })
assert.equal(all.filter(row => row.kind === 'category').length, 10, '展开全部后不再收起')
const nine = { ...report, suppliers: [{ ...report.suppliers[0], categories: [kitchen, ...others.slice(0, 8)] }] }
assert.equal(flattenCategoryTree(nine, { expanded: new Set(['240']), showAll: new Set() }).filter(row => row.kind === 'more').length, 0,
  '只多出 1 项时直接显示，不出现「另外 1 个」')

const byQuantity = { ...report, suppliers: [{ ...report.suppliers[0], categories: [
  { ...node('a', 'Low qty high revenue', 90), quantity: 1 }, { ...node('b', 'High qty low revenue', 10), quantity: 50 }] }] }
assert.deepEqual(flattenCategoryTree(byQuantity, { expanded: new Set(['240']), showAll: new Set(), sort: { key: 'quantity', ascending: false } })
  .filter(row => row.kind === 'category').map(row => row.name), ['High qty low revenue', 'Low qty high revenue'], '按数量降序')
assert.deepEqual(flattenCategoryTree(byQuantity, { expanded: new Set(['240']), showAll: new Set(), sort: { key: 'revenue', ascending: true } })
  .filter(row => row.kind === 'category').map(row => row.name), ['High qty low revenue', 'Low qty high revenue'], '按营业额升序')
assert.equal(last(flattenCategoryTree(byQuantity, { expanded: new Set(['240']), showAll: new Set(), sort: { key: 'quantity', ascending: true } })).kind,
  'unassigned', '任何排序下未归类都排在供应商最后')
assert.deepEqual(flattenCategoryTree(report, { expanded: new Set(), showAll: new Set(), sort: { key: 'revenue', ascending: true } }).map(row => row.key),
  ['200', '240'], '供应商行同样参与排序')

const filtered = flattenCategoryTree(report, { expanded: new Set(), showAll: new Set(), filter: 'food' })
assert.deepEqual(filtered.map(row => row.name), ['Dats', 'Kitchen And Household', 'Food Storage'], '筛选只保留命中节点及其祖先，自动展开')
assert.deepEqual(flattenCategoryTree(report, { expanded: new Set(), showAll: new Set(), filter: 'hot bargain' }).map(row => row.key), ['200'],
  '命中供应商名时按正常展开状态显示')

const labels = { all: '全部', unassigned: '未归类' }
const focus = resolveCategoryFocus(report, { supplierCode: '240', categoryGuid: 'K1' }, labels)!
assert.deepEqual(focus.path.map(item => item.label), ['全部', 'Dats', 'Kitchen And Household', 'Food Storage'], '分类 GUID 不区分大小写')
assert.equal(focus.share, 0.6)
assert.deepEqual(focus.path[2].selection, { supplierCode: '240', categoryGuid: 'k' })
assert.equal(resolveCategoryFocus(report, { supplierCode: '240' }, labels)!.share, 0.75)
assert.equal(resolveCategoryFocus(report, { supplierCode: '240', categoryGuid: UNASSIGNED_CATEGORY_KEY }, labels)!.path.slice(-1)[0].label, '未归类')
assert.equal(resolveCategoryFocus(report, { supplierCode: '999' }, labels), undefined, '节点不在结果里时返回空，由页面回到默认节点')
assert.deepEqual(ancestorKeys(report, { supplierCode: '240', categoryGuid: 'k1' }), ['240', nodeKey('240', 'k')])

assert.deepEqual(supplierOpenKeys(report, '240'), ['240'], '多个根时只展开供应商')
const singleRoot = { ...report, suppliers: [{ ...report.suppliers[1], categories: [node('ALL', 'ALL', 100, [node('W', '10.TOYS', 100)])] }] }
assert.deepEqual(supplierOpenKeys(singleRoot, '200'), ['200', nodeKey('200', 'ALL')], '只有一个根时连根一起展开')

const normalized = normalizeCategoryReport({
  Summary: { Revenue: '12.5', CompareRevenue: null, ProductCount: 2 },
  suppliers: [{ supplierCode: '200', supplierName: 'Hot Bargain', categorySource: 'warehouse',
    categories: [{ categoryGuid: 'W', name: 'Toys', isActive: false, revenue: 3, children: [{ categoryGuid: 'W1', name: 'Dolls', revenue: 1 }] }] }],
  products: { rows: [{ code: 'P1', name: 'Item', revenue: 3 }], total: 9 },
})
assert.equal(normalized.summary.revenue, 12.5, '兼容大写字段与字符串数字')
assert.equal(normalized.summary.compareRevenue, null)
assert.equal(normalized.suppliers[0].categories[0].isActive, false)
assert.equal(normalized.suppliers[0].categories[0].children[0].name, 'Dolls')
assert.equal(normalized.products?.total, 9)
assert.equal(normalizeCategoryReport({}).products, undefined, '只请求分类树时没有商品分页')

console.log('销售明细澳洲供应商分类：树展开、收起、占上级、筛选与面包屑：通过')
