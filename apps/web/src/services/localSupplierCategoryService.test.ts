import assert from 'node:assert/strict'
import {
  getLocalSupplierCategorySummary,
  getLocalSupplierCategoryTree,
  normalizeLocalSupplierCategorySummary,
  normalizeLocalSupplierCategoryTree,
  readRequestErrorCode,
  resolveLocalSupplierCategories,
  setLocalSupplierCategoryPromotional,
} from './localSupplierCategoryService'
import { getProducts, normalizePosProductDto, updateProduct } from './posProductService'
import { RequestError } from '../utils/request'

// —— summary 归一化 ——
assert.deepEqual(
  normalizeLocalSupplierCategorySummary([
    {
      supplierCode: '240',
      supplierName: 'DATS',
      sourceKind: 'website',
      categoryCount: 12,
      promotionalCount: 2,
      productCount: 300,
      assignedCount: 250,
      manualCount: 3,
      unassignedCount: 50,
      lastCapturedAt: '2026-09-23T01:02:03Z',
    },
    { SupplierCode: '200', CategoryCount: '8' },
    { supplierName: '缺编码的行应丢弃' },
    null,
  ]),
  [
    {
      supplierCode: '240',
      supplierName: 'DATS',
      sourceKind: 'website',
      categoryCount: 12,
      promotionalCount: 2,
      productCount: 300,
      assignedCount: 250,
      manualCount: 3,
      unassignedCount: 50,
      lastCapturedAt: '2026-09-23T01:02:03Z',
      lastSnapshotAt: undefined,
    },
    {
      supplierCode: '200',
      supplierName: undefined,
      sourceKind: 'warehouse',
      categoryCount: 8,
      promotionalCount: 0,
      productCount: 0,
      assignedCount: 0,
      manualCount: 0,
      unassignedCount: 0,
      lastCapturedAt: undefined,
      lastSnapshotAt: undefined,
    },
  ],
)
assert.deepEqual(normalizeLocalSupplierCategorySummary({ not: 'array' }), [])

// —— 树归一化：嵌套形状 ——
const nested = normalizeLocalSupplierCategoryTree([
  {
    categoryGuid: 'root',
    name: 'Office',
    externalKey: '/office',
    fullPath: 'Office',
    depth: 0,
    isPromotional: false,
    promotionalSource: 'pattern',
    isActive: true,
    sortOrder: 1,
    productCount: 10,
    children: [
      { categoryGuid: 'leaf', name: 'Pens', depth: 1, isPromotional: true, promotionalSource: 'MANUAL', productCount: 4, children: [] },
    ],
  },
  { categoryGuid: 'root', name: '重复 GUID 只保留第一次' },
])
assert.equal(nested.length, 1)
assert.equal(nested[0].categoryGuid, 'root')
assert.equal(nested[0].sortOrder, 1)
assert.equal(nested[0].promotionalSource, 'pattern')
assert.equal(nested[0].children.length, 1)
assert.equal(nested[0].children[0].parentGuid, 'root', '嵌套子节点的 parentGuid 取所在层级')
assert.equal(nested[0].children[0].isPromotional, true)
assert.equal(nested[0].children[0].promotionalSource, 'manual', '促销来源按小写归一')
assert.equal(nested[0].children[0].isActive, true, 'isActive 缺省视为启用')
assert.equal(nested[0].children[0].depth, 1)

// —— 树归一化：扁平形状（只带 parentGuid，父节点缺失的当根） ——
const flat = normalizeLocalSupplierCategoryTree([
  { categoryGUID: 'b', parentGUID: 'a', categoryName: 'Child' },
  { categoryGUID: 'a', categoryName: 'Parent' },
  { categoryGUID: 'orphan', parentGUID: 'missing', categoryName: 'Orphan' },
  { categoryGUID: 'x', parentGUID: 'y', categoryName: 'X' },
  { categoryGUID: 'y', parentGUID: 'x', categoryName: 'Y' },
])
assert.deepEqual(flat.map((item) => item.categoryGuid), ['a', 'orphan', 'x'], '父节点缺失的当根；成环的节点补成根，不丢节点')
assert.deepEqual(flat[0].children.map((item) => item.categoryGuid), ['b'])
assert.deepEqual(flat[2].children.map((item) => item.categoryGuid), ['y'])
assert.deepEqual(normalizeLocalSupplierCategoryTree(null), [])

// —— 商品 DTO 归一化：只新增四个字段，旧字段行为不变 ——
const normalizedProduct = normalizePosProductDto({
  productCode: 'P1',
  warehouseCategoryGUID: 'w-1',
  supplierCategoryGUID: 'sc-1',
  supplierCategoryName: 'Pens',
  supplierCategoryPath: 'Office > Pens',
  supplierCategorySource: 'Manual',
})
assert.deepEqual(normalizedProduct, {
  productCode: 'P1',
  warehouseCategoryGuid: 'w-1',
  supplierCategoryGuid: 'sc-1',
  supplierCategoryName: 'Pens',
  supplierCategoryPath: 'Office > Pens',
  supplierCategorySource: 'manual',
})
assert.deepEqual(
  normalizePosProductDto({ productCode: 'P2', supplierCategorySource: 'unknown-source', supplierCategoryGUID: null }),
  { productCode: 'P2' },
  '来源不在白名单、空 GUID 时不写字段',
)
assert.deepEqual(normalizePosProductDto({ productCode: 'P3', categoryGuid: 'c' }), { productCode: 'P3', categoryGuid: 'c' }, '无新字段时输出与原来一致')

// —— 请求形状（stub fetch） ——
const originalFetch = globalThis.fetch
const requests: Array<{ url: string; method?: string; body?: unknown }> = []
let nextResponse: { status: number; body: unknown } = { status: 200, body: { success: true, data: [] } }
const lastRequest = () => requests[requests.length - 1]
globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  requests.push({ url: String(input), method: init?.method, body: init?.body ? JSON.parse(String(init.body)) : undefined })
  return new Response(JSON.stringify(nextResponse.body), {
    status: nextResponse.status,
    headers: { 'Content-Type': 'application/json' },
  })
}) as typeof fetch

try {
  nextResponse = { status: 200, body: { success: true, data: [{ supplierCode: '240', categoryCount: 1 }] } }
  const summary = await getLocalSupplierCategorySummary()
  assert.equal(summary[0].supplierCode, '240')
  assert.equal(lastRequest()?.url, '/api/react/v1/local-supplier-categories/summary')
  assert.equal(lastRequest()?.method, 'GET')

  nextResponse = { status: 200, body: { success: true, data: [{ categoryGuid: 'a', name: 'A', children: [] }] } }
  const tree = await getLocalSupplierCategoryTree('240')
  assert.equal(tree[0].categoryGuid, 'a')
  assert.equal(lastRequest()?.url, '/api/react/v1/local-supplier-categories/tree?supplierCode=240')

  nextResponse = { status: 200, body: { success: true, data: { reassigned: 3, cleared: 1 } } }
  assert.deepEqual(await setLocalSupplierCategoryPromotional('g/1', true), { reassigned: 3, cleared: 1 })
  assert.equal(lastRequest()?.url, '/api/react/v1/local-supplier-categories/g%2F1/promotional')
  assert.equal(lastRequest()?.method, 'PATCH')
  assert.deepEqual(lastRequest()?.body, { isPromotional: true })

  nextResponse = {
    status: 200,
    body: { success: true, data: { productsScanned: 10, assigned: 2, updated: 1, cleared: 0, unchanged: 7, manualSkipped: 0, staleRemoved: 1 } },
  }
  assert.deepEqual(await resolveLocalSupplierCategories('240'), {
    productsScanned: 10, assigned: 2, updated: 1, cleared: 0, unchanged: 7, manualSkipped: 0, staleRemoved: 1,
  })
  assert.equal(lastRequest()?.url, '/api/react/v1/local-supplier-categories/240/resolve')
  assert.equal(lastRequest()?.method, 'POST')

  // 列表筛选：只有设置时才写新键，旧请求体保持不变
  nextResponse = { status: 200, body: { success: true, data: { items: [], total: 0 } } }
  await getProducts({ pageIndex: 1, pageSize: 20, supplierCode: '240', supplierCategoryGuid: 'sc-1', supplierCategoryUnassignedOnly: true })
  const listBody = lastRequest()?.body as Record<string, unknown>
  assert.deepEqual(listBody.supplierCategoryGUIDs, ['sc-1'])
  assert.equal(listBody.supplierCategoryUnassignedOnly, true)
  await getProducts({ pageIndex: 1, pageSize: 20 })
  const plainBody = lastRequest()?.body as Record<string, unknown>
  assert.equal('supplierCategoryGUIDs' in plainBody, false)
  assert.equal('supplierCategoryUnassignedOnly' in plainBody, false)

  // 单个更新：三态字段原样透传
  nextResponse = { status: 200, body: { success: true, data: { productCode: 'P1' } } }
  await updateProduct('P1', { productName: 'x', ...{ supplierCategoryGUID: 'sc-2' } } as never)
  assert.equal((lastRequest()?.body as Record<string, unknown>).supplierCategoryGUID, 'sc-2')
  await updateProduct('P1', { productName: 'x', ...{ clearSupplierCategory: true } } as never)
  assert.equal((lastRequest()?.body as Record<string, unknown>).clearSupplierCategory, true)

  // 400 业务错误码可读取
  nextResponse = { status: 400, body: { success: false, message: '分类不属于该供应商', errorCode: 'CATEGORY_SUPPLIER_MISMATCH' } }
  let caught: unknown
  try {
    await updateProduct('P1', { productName: 'x' })
  } catch (error) {
    caught = error
  }
  assert.ok(caught instanceof RequestError)
  assert.equal(readRequestErrorCode(caught), 'CATEGORY_SUPPLIER_MISMATCH')
  assert.equal(readRequestErrorCode(new Error('plain')), undefined)
  assert.equal(readRequestErrorCode(new RequestError('x', 500, 'text body')), undefined)
} finally {
  globalThis.fetch = originalFetch
}

console.log('localSupplierCategoryService.test: ok')
