import assert from 'node:assert/strict'
import {
  SUPPLIER_CATEGORY_RESTORE_AUTO_VALUE,
  SUPPLIER_CATEGORY_UNASSIGNED_VALUE,
  applySupplierCategoryCascaderChange,
  applySupplierSelectChange,
  applyWarehouseCategoryFilterChange,
  clearSupplierCategoryFilter,
  formatCascaderDisplayLabels,
  resolveBatchSupplierCategoryUpdate,
  resolveSupplierCategoryUpdate,
  toSupplierCategoryCascaderValue,
  toSupplierCategoryQueryParams,
  type SupplierCategoryFilterState,
} from './supplierCategoryFilter'

const empty: SupplierCategoryFilterState = { supplierCategoryUnassignedOnly: false }

// —— 级联框变更 ——
// 非 200：选到叶子写供应商分类，仓库分类（独立筛选）保持不变
assert.deepEqual(
  applySupplierCategoryCascaderChange({ ...empty, warehouseCategoryGuid: 'w-1' }, ['240', 'c-root', 'c-leaf']),
  { supplierCode: '240', warehouseCategoryGuid: 'w-1', supplierCategoryGuid: 'c-leaf', supplierCategoryUnassignedOnly: false },
)
// 非 200：只点到供应商层
assert.deepEqual(
  applySupplierCategoryCascaderChange({ ...empty, supplierCode: '240', supplierCategoryGuid: 'c-leaf' }, ['240']),
  { supplierCode: '240', warehouseCategoryGuid: undefined, supplierCategoryGuid: undefined, supplierCategoryUnassignedOnly: false },
)
// 非 200 选「未归类」：不动仓库分类
assert.deepEqual(
  applySupplierCategoryCascaderChange({ ...empty, warehouseCategoryGuid: 'w-1' }, ['240', SUPPLIER_CATEGORY_UNASSIGNED_VALUE]),
  { supplierCode: '240', warehouseCategoryGuid: 'w-1', supplierCategoryGuid: undefined, supplierCategoryUnassignedOnly: true },
)
// 200：第二层即仓库分类，不写供应商分类
assert.deepEqual(
  applySupplierCategoryCascaderChange({ ...empty, supplierCode: '240', supplierCategoryGuid: 'c-leaf' }, ['200', 'w-root', 'w-leaf']),
  { supplierCode: '200', warehouseCategoryGuid: 'w-leaf', supplierCategoryGuid: undefined, supplierCategoryUnassignedOnly: false },
)
// 200 下选「未归类」是唯一主动清理的矛盾组合：清掉仓库分类
assert.deepEqual(
  applySupplierCategoryCascaderChange({ ...empty, supplierCode: '200', warehouseCategoryGuid: 'w-leaf' }, ['200', SUPPLIER_CATEGORY_UNASSIGNED_VALUE]),
  { supplierCode: '200', warehouseCategoryGuid: undefined, supplierCategoryGuid: undefined, supplierCategoryUnassignedOnly: true },
)
// 从别的供应商切到 200 本层：保留独立设置的仓库分类
assert.equal(
  applySupplierCategoryCascaderChange({ ...empty, supplierCode: '240', warehouseCategoryGuid: 'w-1' }, ['200']).warehouseCategoryGuid,
  'w-1',
)
// 已在 200 下再点 200 本层：视为回到上层，清掉仓库分类
assert.equal(
  applySupplierCategoryCascaderChange({ ...empty, supplierCode: '200', warehouseCategoryGuid: 'w-1' }, ['200']).warehouseCategoryGuid,
  undefined,
)
// 清空：200 下连同仓库分类一起清；非 200 只清级联框展示的条件
assert.deepEqual(
  applySupplierCategoryCascaderChange({ supplierCode: '200', warehouseCategoryGuid: 'w-1', supplierCategoryUnassignedOnly: false }, undefined),
  { supplierCode: undefined, warehouseCategoryGuid: undefined, supplierCategoryGuid: undefined, supplierCategoryUnassignedOnly: false },
)
assert.deepEqual(
  applySupplierCategoryCascaderChange({ supplierCode: '240', warehouseCategoryGuid: 'w-1', supplierCategoryGuid: 'c', supplierCategoryUnassignedOnly: false }, []),
  { supplierCode: undefined, warehouseCategoryGuid: 'w-1', supplierCategoryGuid: undefined, supplierCategoryUnassignedOnly: false },
)
// 伪节点（空树提示等）不会写进筛选
assert.equal(applySupplierCategoryCascaderChange(empty, ['240', '__empty__']).supplierCategoryGuid, undefined)

// —— 供应商 Select ——
const withCategory: SupplierCategoryFilterState = { supplierCode: '240', warehouseCategoryGuid: 'w-1', supplierCategoryGuid: 'c', supplierCategoryUnassignedOnly: false }
assert.equal(applySupplierSelectChange(withCategory, '240'), withCategory, '供应商未变时原样返回')
assert.deepEqual(applySupplierSelectChange(withCategory, '201'), {
  supplierCode: '201',
  warehouseCategoryGuid: 'w-1',
  supplierCategoryGuid: undefined,
  supplierCategoryUnassignedOnly: false,
})
assert.deepEqual(applySupplierSelectChange({ ...empty, supplierCode: '240', supplierCategoryUnassignedOnly: true }, undefined), {
  supplierCode: undefined,
  warehouseCategoryGuid: undefined,
  supplierCategoryGuid: undefined,
  supplierCategoryUnassignedOnly: false,
})

// —— 更多筛选里的仓库分类 ——
assert.equal(
  applyWarehouseCategoryFilterChange({ supplierCode: '200', supplierCategoryUnassignedOnly: true }, 'w-2').supplierCategoryUnassignedOnly,
  false,
  '200 下选具体仓库分类应取消「未归类」',
)
assert.equal(
  applyWarehouseCategoryFilterChange({ supplierCode: '240', supplierCategoryUnassignedOnly: true }, 'w-2').supplierCategoryUnassignedOnly,
  true,
  '非 200 的未归类与仓库分类不冲突',
)
assert.equal(
  applyWarehouseCategoryFilterChange({ supplierCode: '200', supplierCategoryUnassignedOnly: true }, undefined).supplierCategoryUnassignedOnly,
  true,
  '清空仓库分类不影响未归类',
)

// —— 移除供应商分类标签 ——
assert.deepEqual(
  clearSupplierCategoryFilter({ supplierCode: '240', warehouseCategoryGuid: 'w', supplierCategoryGuid: 'c', supplierCategoryUnassignedOnly: true }),
  { supplierCode: '240', warehouseCategoryGuid: 'w', supplierCategoryGuid: undefined, supplierCategoryUnassignedOnly: false },
)

// —— 显示值派生 ——
const lookups = {
  findWarehouseGuidPath: (guid: string) => (guid === 'w-leaf' ? ['w-root', 'w-leaf'] : undefined),
  findSupplierGuidPath: (code: string, guid: string) => (code === '240' && guid === 'c-leaf' ? ['c-root', 'c-leaf'] : undefined),
}
assert.equal(toSupplierCategoryCascaderValue(empty, lookups), undefined)
assert.deepEqual(toSupplierCategoryCascaderValue({ ...empty, supplierCode: '240', supplierCategoryGuid: 'c-leaf' }, lookups), ['240', 'c-root', 'c-leaf'])
assert.deepEqual(toSupplierCategoryCascaderValue({ ...empty, supplierCode: '240', supplierCategoryGuid: 'c-x' }, lookups), ['240'], '树未加载时只显示到供应商层')
assert.deepEqual(toSupplierCategoryCascaderValue({ ...empty, supplierCode: '200', warehouseCategoryGuid: 'w-leaf' }, lookups), ['200', 'w-root', 'w-leaf'])
assert.deepEqual(toSupplierCategoryCascaderValue({ supplierCode: '200', supplierCategoryUnassignedOnly: true }, lookups), ['200', SUPPLIER_CATEGORY_UNASSIGNED_VALUE])
assert.deepEqual(
  toSupplierCategoryCascaderValue({ ...empty, supplierCode: '240', warehouseCategoryGuid: 'w-leaf' }, lookups),
  ['240'],
  '非 200 时仓库分类不出现在供应商分类级联框里',
)

// —— 查询参数 ——
assert.deepEqual(toSupplierCategoryQueryParams(empty), {})
assert.deepEqual(toSupplierCategoryQueryParams({ ...empty, supplierCode: '240', supplierCategoryGuid: 'c' }), { supplierCategoryGuid: 'c' })
assert.deepEqual(toSupplierCategoryQueryParams({ supplierCode: '200', supplierCategoryUnassignedOnly: true }), { supplierCategoryUnassignedOnly: true })

// —— 显示文本折叠 ——
assert.equal(formatCascaderDisplayLabels(['DATS', '文具']), 'DATS / 文具')
assert.equal(formatCascaderDisplayLabels(['DATS', 'A', 'B']), 'DATS / A / B')
assert.equal(formatCascaderDisplayLabels(['DATS', 'A', 'B', 'C']), 'DATS / … / C')

// —— 单个保存三态 ——
assert.deepEqual(resolveSupplierCategoryUpdate({ originalSupplierCode: '200', nextSupplierCode: '200', originalGuid: 'w', nextGuid: 'w2' }), {}, '200 一律不带')
assert.deepEqual(resolveSupplierCategoryUpdate({ originalSupplierCode: '240', nextSupplierCode: '200', originalGuid: 'c' }), {}, '改成 200 由服务端随仓库分类')
assert.deepEqual(resolveSupplierCategoryUpdate({ originalSupplierCode: '240', nextSupplierCode: '240', originalGuid: 'c', nextGuid: 'c' }), {}, '未改动')
assert.deepEqual(resolveSupplierCategoryUpdate({ originalSupplierCode: '240', nextSupplierCode: '240', originalGuid: 'ABC', nextGuid: 'abc' }), {}, 'GUID 大小写不同视为未改动')
assert.deepEqual(resolveSupplierCategoryUpdate({ originalSupplierCode: '240', nextSupplierCode: '240' }), {}, '原来没有、现在也没有')
assert.deepEqual(resolveSupplierCategoryUpdate({ originalSupplierCode: '240', nextSupplierCode: '240', originalGuid: 'c', nextGuid: 'd' }), { supplierCategoryGUID: 'd' })
assert.deepEqual(resolveSupplierCategoryUpdate({ originalSupplierCode: '240', nextSupplierCode: '240', nextGuid: 'd' }), { supplierCategoryGUID: 'd' })
assert.deepEqual(resolveSupplierCategoryUpdate({ originalSupplierCode: '240', nextSupplierCode: '240', originalGuid: 'c' }), { clearSupplierCategory: true }, '清空')
assert.deepEqual(resolveSupplierCategoryUpdate({ originalSupplierCode: '240', nextSupplierCode: '201', originalGuid: 'c' }), { clearSupplierCategory: true }, '换供应商未选新分类')
assert.deepEqual(resolveSupplierCategoryUpdate({ originalSupplierCode: '200', nextSupplierCode: '201', originalGuid: 'w' }), { clearSupplierCategory: true }, '从 200 换出去按采集重新归类')
assert.deepEqual(resolveSupplierCategoryUpdate({ originalSupplierCode: '240', nextSupplierCode: '201', originalGuid: 'c', nextGuid: 'x' }), { supplierCategoryGUID: 'x' })
assert.deepEqual(resolveSupplierCategoryUpdate({ originalSupplierCode: ' 240 ', nextSupplierCode: '240', originalGuid: 'c', nextGuid: 'c' }), {}, '供应商编码两端空白不算换供应商')
assert.deepEqual(resolveSupplierCategoryUpdate({ originalSupplierCode: '240', nextSupplierCode: '240', originalGuid: 'c', nextGuid: SUPPLIER_CATEGORY_UNASSIGNED_VALUE }), { clearSupplierCategory: true }, '伪值不能当 GUID 提交')

// —— 批量保存 ——
assert.deepEqual(resolveBatchSupplierCategoryUpdate(undefined), {})
assert.deepEqual(resolveBatchSupplierCategoryUpdate(''), {})
assert.deepEqual(resolveBatchSupplierCategoryUpdate(SUPPLIER_CATEGORY_RESTORE_AUTO_VALUE), { clearSupplierCategory: true })
assert.deepEqual(resolveBatchSupplierCategoryUpdate('c-leaf'), { supplierCategoryGUID: 'c-leaf' })
assert.deepEqual(resolveBatchSupplierCategoryUpdate('__empty__'), {})

console.log('supplierCategoryFilter.test: ok')
