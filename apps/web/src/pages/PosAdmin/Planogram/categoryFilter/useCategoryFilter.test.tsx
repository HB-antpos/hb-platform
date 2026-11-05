import assert from 'node:assert/strict'
import i18next from 'i18next'
import { createRoot, type Root } from 'react-dom/client'
import { initReactI18next } from 'react-i18next'
import type { WarehouseCategoryNode } from '../../../../services/warehouseCategoryService'
import type { LocalSupplierDto } from '../../../../types/localSupplier'
import type { LocalSupplierCategoryNode } from '../../../../types/localSupplierCategory'
import { SUPPLIER_CATEGORY_UNASSIGNED_VALUE } from '../../ProductManagement/supplierCategoryFilter'
import en from '../planogramMessages.en.json'
import { useCategoryFilter, type CategoryFilterApi, type CategoryFilterLoadKind } from './useCategoryFilter'

// 状态 hook：用真实 React 调度执行 effect。验证——
// 1) 分类数据懒加载：页面挂载时一个请求都不发，ensureLoaded 才发，且只发一次；
// 2) 加载失败：状态变 error、回调通知、不自动重试，显式 reload 才重试，失败不影响过滤状态本身；
// 3) 过期响应（重试后旧请求才回来）被丢弃；
// 4) 供应商分类树按供应商懒加载并去重，失败后出现重试节点、重试成功后选项更新；
// 5) 状态变更 → scope / scopeKey 的引用稳定性（条件没变就不触发重新请求）。

await i18next.use(initReactI18next).init({ lng: 'en', resources: { en: { translation: en } } })
Reflect.set(globalThis, 'window', { HTMLIFrameElement: class {} })

const nextMacrotask = () => new Promise<void>((resolve) => setImmediate(resolve))
async function settle(turns = 30) {
  for (let turn = 0; turn < turns; turn += 1) await nextMacrotask()
}

// ---------- 可控的取数替身 ----------
interface Deferred<T> { promise: Promise<T>; resolve: (value: T) => void; reject: (error: unknown) => void }
function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void
  let reject!: (error: unknown) => void
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej })
  return { promise, resolve, reject }
}

const warehouseTree: WarehouseCategoryNode[] = [
  { categoryGUID: 'wh-a', categoryName: 'Stationery', isActive: true, children: [{ categoryGUID: 'wh-a1', categoryName: 'Pens', isActive: true, children: [] }] },
]
const suppliers = [
  { localSupplierCode: '200', name: 'Hot Bargain' },
  { localSupplierCode: 'S1', name: 'Alpha' },
] as unknown as LocalSupplierDto[]
const supplierTree: LocalSupplierCategoryNode[] = [
  { categoryGuid: 'root-1', name: '文具', depth: 0, isPromotional: false, isActive: true, productCount: 1, children: [] },
]

let warehouseCalls = 0
let supplierCalls = 0
const treeCalls: string[] = []
let warehouseImpl: () => Promise<WarehouseCategoryNode[]> = async () => warehouseTree
let supplierImpl: () => Promise<LocalSupplierDto[]> = async () => suppliers
let treeImpl: (code: string) => Promise<LocalSupplierCategoryNode[]> = async () => supplierTree
const errors: CategoryFilterLoadKind[] = []

let current: CategoryFilterApi | null = null
function api(): CategoryFilterApi {
  assert.ok(current, 'hook 已渲染')
  return current
}

function Harness() {
  current = useCategoryFilter({
    loadWarehouseTree: () => { warehouseCalls += 1; return warehouseImpl() },
    loadSuppliers: () => { supplierCalls += 1; return supplierImpl() },
    loadSupplierTree: (code) => { treeCalls.push(code); return treeImpl(code) },
    onLoadError: (kind) => { errors.push(kind) },
  })
  return null
}

const roots: Root[] = []
function mount() {
  const noop = () => {}
  const eventTarget = { addEventListener: noop, removeEventListener: noop }
  const container = { ...eventTarget, nodeType: 1, tagName: 'DIV', namespaceURI: 'http://www.w3.org/1999/xhtml', textContent: '', ownerDocument: eventTarget }
  const root = createRoot(container as unknown as HTMLElement)
  roots.push(root)
  root.render(<Harness />)
  return root
}
function unmountAll() {
  for (const root of roots.splice(0)) root.unmount()
}

try {
  // ---------- 1) 懒加载 ----------
  mount()
  await settle()
  assert.equal(warehouseCalls + supplierCalls + treeCalls.length, 0, '页面挂载时不发任何分类请求')
  assert.equal(api().warehouse.status, 'idle')
  assert.equal(api().suppliers.status, 'idle')
  assert.deepEqual(api().scope, {})
  assert.equal(api().hasActive, false)

  api().ensureLoaded()
  api().ensureLoaded()
  await settle()
  assert.equal(warehouseCalls, 1, '仓库分类树只请求一次')
  assert.equal(supplierCalls, 1, '供应商列表只请求一次')
  assert.equal(api().warehouse.status, 'ready')
  assert.equal(api().warehouse.treeData[0].children?.[0].pathLabel, 'Stationery / Pens')
  assert.deepEqual(api().suppliers.options.map((option) => option.value), ['S1'], '供应商选项隐藏 200')
  assert.deepEqual(api().cascader.options.map((option) => option.value), ['S1'], '级联第一层没有 200')
  assert.equal(api().cascader.options[0].isLeaf, false, '供应商的分类树还没加载：等待展开时懒加载')

  api().ensureLoaded()
  await settle()
  assert.equal(warehouseCalls, 1, '已加载后再调用 ensureLoaded 不重复请求')

  // ---------- 5) 状态 → scope 引用稳定性 ----------
  const scope0 = api().scope
  api().setWarehouseCategories([])
  await settle(3)
  assert.equal(api().scope, scope0, '条件没变：scope 引用不变（不会触发重新请求）')
  api().setWarehouseCategories(['wh-a1', 'wh-a'])
  await settle(3)
  assert.deepEqual(api().scope, { warehouseCategoryGuids: ['wh-a1', 'wh-a'] })
  assert.equal(api().activeCount, 1)
  const key1 = api().scopeKey
  api().setWarehouseCategories(['wh-a1', 'wh-a'])
  await settle(3)
  assert.equal(api().scopeKey, key1)
  api().setGrades(['none', 'A'])
  api().setSupplierPath(['S1', SUPPLIER_CATEGORY_UNASSIGNED_VALUE])
  await settle(3)
  assert.deepEqual(api().scope, {
    warehouseCategoryGuids: ['wh-a1', 'wh-a'],
    localSupplierCode: 'S1',
    supplierCategoryUnassignedOnly: true,
    grades: ['A', 'none'],
  })
  assert.equal(api().activeCount, 3)
  assert.deepEqual(api().cascader.value, ['S1', SUPPLIER_CATEGORY_UNASSIGNED_VALUE], '级联显示值由状态派生')
  api().clear()
  await settle(3)
  assert.deepEqual(api().scope, {}, '清除后没有任何条件')
  assert.equal(api().cascader.value, undefined)
  assert.equal(api().warehouse.status, 'ready', '清除条件不丢已加载的分类树')
  const clearedScope = api().scope
  api().clear()
  await settle(3)
  assert.equal(api().scope, clearedScope, '本来就没有条件时清除不换引用')

  // ---------- 4) 供应商分类树：按供应商懒加载 ----------
  api().cascader.loadSupplier('S1')
  api().cascader.loadSupplier('S1')
  await settle()
  assert.deepEqual(treeCalls, ['S1'], '同一供应商的分类树只请求一次')
  assert.equal(api().cascader.options[0].children?.[0].label, 'Unassigned', '「未归类」文案来自 planogram.categoryFilter.unassigned')
  assert.equal(api().cascader.options[0].children?.[1]?.value, 'root-1', '分类树加载后进入级联选项')
  api().setSupplierPath(['S1', 'root-1'])
  await settle(3)
  assert.deepEqual(api().cascader.value, ['S1', 'root-1'])
  assert.deepEqual(api().scope, { localSupplierCode: 'S1', supplierCategoryGuids: ['root-1'] })
  unmountAll()

  // ---------- 2)+3) 失败 / 重试 / 过期响应 ----------
  warehouseCalls = 0
  supplierCalls = 0
  treeCalls.length = 0
  errors.length = 0
  const firstWarehouse = deferred<WarehouseCategoryNode[]>()
  warehouseImpl = () => firstWarehouse.promise
  supplierImpl = async () => { throw new Error('suppliers down') }
  mount()
  await settle()
  api().ensureLoaded()
  await settle()
  assert.equal(api().warehouse.status, 'loading')
  assert.equal(api().suppliers.status, 'error', '供应商列表失败：状态 error')
  assert.deepEqual(errors, ['suppliers'], '失败回调带上种类')
  assert.deepEqual(api().cascader.options, [], '失败时级联没有选项，但不影响其它')

  api().setGrades(['B'])
  await settle(3)
  assert.deepEqual(api().scope, { grades: ['B'] }, '加载失败不影响过滤状态本身（列表不被阻塞）')

  api().ensureLoaded()
  await settle()
  assert.equal(supplierCalls, 1, '失败后 ensureLoaded 不自动重试')
  assert.equal(warehouseCalls, 1)

  // 重试仓库分类：第二次请求先返回，第一次请求（过期）后返回——应被丢弃
  const secondWarehouse = deferred<WarehouseCategoryNode[]>()
  warehouseImpl = () => secondWarehouse.promise
  api().warehouse.reload()
  await settle(3)
  assert.equal(warehouseCalls, 2)
  secondWarehouse.resolve([{ categoryGUID: 'new', categoryName: 'New', isActive: true, children: [] }])
  await settle()
  assert.deepEqual(api().warehouse.treeData.map((node) => node.value), ['new'])
  firstWarehouse.resolve(warehouseTree)
  await settle()
  assert.deepEqual(api().warehouse.treeData.map((node) => node.value), ['new'], '过期响应不覆盖新结果')

  // 供应商列表重试成功
  supplierImpl = async () => suppliers
  api().suppliers.reload()
  await settle()
  assert.equal(api().suppliers.status, 'ready')
  assert.deepEqual(api().suppliers.options.map((option) => option.value), ['S1'])

  // 供应商分类树失败 → 级联里出现重试节点 → 重试成功
  treeImpl = async () => { throw new Error('tree down') }
  errors.length = 0
  api().cascader.loadSupplier('S1')
  await settle()
  assert.deepEqual(errors, ['supplierTree'])
  assert.equal(api().cascader.options[0].children?.[1]?.kind, 'retry', '失败后出现「加载失败，点击重试」节点')
  treeImpl = async () => supplierTree
  api().cascader.retrySupplier('S1')
  await settle()
  assert.equal(api().cascader.options[0].children?.[1]?.value, 'root-1', '重试成功后显示分类')
  unmountAll()

  // ---------- 卸载后的响应不再更新状态（也不触发失败回调） ----------
  warehouseCalls = 0
  errors.length = 0
  const late = deferred<WarehouseCategoryNode[]>()
  warehouseImpl = () => late.promise
  supplierImpl = async () => suppliers
  const root = mount()
  await settle()
  api().ensureLoaded()
  await settle(3)
  root.unmount()
  roots.length = 0
  late.reject(new Error('too late'))
  await settle()
  assert.deepEqual(errors, [], '卸载后到达的失败不再回调')
} finally {
  unmountAll()
}

console.log('useCategoryFilter.test: ok')
