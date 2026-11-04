import assert from 'node:assert/strict'
import i18next from 'i18next'
import { createRoot, type Root } from 'react-dom/client'
import { initReactI18next } from 'react-i18next'
import type { ProductSpecBatchRequest, ProductSpecBatchResultDto } from '../../../../types/planogram'
import en from '../planogramMessages.en.json'
import { batchTargetFromFilter, batchTargetFromSelection, selectionAdd, EMPTY_BATCH_SELECTION, type BatchTargetSpec } from './batchLogic'
import { useBatchEdit, type BatchEditApi } from './useBatchEdit'

// 批量修改流程控制：预览 → 失效 → 确认执行。用真实 React 调度跑 hook，请求函数换成可控替身。
// 验证：预览后改任何会进请求的输入都使预览失效且不能直接执行；在途预览遇到输入变化被中止、迟到响应丢弃；
// 执行中重复点击只发一次；失败时输入保留并给出按错误码区分的提示、改动输入后提示消失；超过 2000 不发请求；成功后同弹窗不再允许执行。

await i18next.use(initReactI18next).init({ lng: 'en', resources: { en: { translation: en } } })
Reflect.set(globalThis, 'window', { HTMLIFrameElement: class {} })

const nextMacrotask = () => new Promise<void>((resolve) => setImmediate(resolve))
async function settle(turns = 20) {
  for (let turn = 0; turn < turns; turn += 1) await nextMacrotask()
}

interface Deferred<T> { promise: Promise<T>; resolve: (value: T) => void; reject: (error: unknown) => void }
function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void
  let reject!: (error: unknown) => void
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej })
  return { promise, resolve, reject }
}

const previewResult: ProductSpecBatchResultDto = {
  dryRun: true, matched: 2, created: 0, updated: 0, unchanged: 0, willCreate: 0, willUpdate: 2, willStayUnchanged: 0, skipped: 0, skippedItems: [], changedTotal: 2, changedItems: [],
}
const executeResult: ProductSpecBatchResultDto = { ...previewResult, dryRun: false, updated: 2, willUpdate: 0 }

interface Call { request: ProductSpecBatchRequest; signal?: AbortSignal; pending: Deferred<ProductSpecBatchResultDto> }
const calls: Call[] = []
let autoRespond = true
function run(request: ProductSpecBatchRequest, signal?: AbortSignal): Promise<ProductSpecBatchResultDto> {
  const pending = deferred<ProductSpecBatchResultDto>()
  calls.push({ request, signal, pending })
  if (autoRespond) pending.resolve(request.dryRun ? previewResult : executeResult)
  return pending.promise
}

const selectedTarget: BatchTargetSpec = batchTargetFromSelection(
  selectionAdd(EMPTY_BATCH_SELECTION, [{ productCode: 'A', hasSpec: true }, { productCode: 'B', hasSpec: true }]).selection,
)
const hookGuids = new Set(['h1'])
let target: BatchTargetSpec = selectedTarget

let current: BatchEditApi | null = null
function api(): BatchEditApi {
  assert.ok(current, 'hook 已渲染')
  return current
}
function Harness() {
  current = useBatchEdit({ target, hookGuids, run })
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
// 重新挂载一个全新的弹窗（全新的输入 / 预览状态），并等它渲染完再返回——否则 api() 还是上一个实例
async function reset(nextTarget: BatchTargetSpec = selectedTarget) {
  unmountAll()
  current = null
  calls.length = 0
  autoRespond = true
  target = nextTarget
  mount()
  await settle(5)
}

try {
  // ---------- 没有勾选任何修改：不能预览，也不会发请求 ----------
  await reset()
  await settle()
  assert.equal(api().canPreview, false)
  await api().runPreview()
  assert.equal(calls.length, 0, '校验不通过不发请求')
  assert.equal(api().confirmBlock, 'invalid')

  // ---------- 预览 → 执行 ----------
  api().setField('width', { enabled: true, value: 70 })
  api().setField('hangType', { enabled: true, value: 1 })
  await settle(3)
  assert.equal(api().canPreview, true)
  assert.equal(api().confirmBlock, 'noPreview', '还没预览不能执行')
  await api().execute()
  assert.equal(calls.length, 0, '没有预览直接执行：被拦下，不发请求')

  await api().runPreview()
  await settle(3)
  assert.equal(calls.length, 1)
  assert.equal(calls[0].request.dryRun, true, '预览 dryRun=true')
  assert.deepEqual(calls[0].request.target, { productCodes: ['A', 'B'] })
  assert.deepEqual(calls[0].request.patch, { widthMm: 70, hangHoleType: 1 })
  assert.equal(api().preview?.willUpdate, 2)
  assert.equal(api().previewStale, false)
  assert.equal(api().confirmBlock, null, '有当前输入下的有效预览：可以执行')

  // ---------- 预览之后改任何会进请求的输入 → 预览失效 ----------
  api().setField('width', { value: 71 })
  await settle(3)
  assert.equal(api().preview, null, '改了取值：旧预览失效')
  assert.equal(api().previewStale, true)
  assert.equal(api().confirmBlock, 'noPreview')
  await api().execute()
  assert.equal(calls.length, 1, '预览失效后不能直接执行')

  api().setField('width', { value: 70 })
  await settle(3)
  assert.ok(api().preview, '改回原值：请求与预览时完全一致，预览重新有效')
  api().setCreateMissing(true)
  await settle(3)
  assert.equal(api().preview, null, '改了「创建未设参数的商品」：预览失效')
  assert.equal(api().validation.errors.createMissing?.code, 'needDimensions')
  api().setCreateMissing(false)
  await settle(3)

  // ---------- 执行：同步连点只发一次 ----------
  autoRespond = false
  const first = api().execute()
  const second = api().execute()
  await settle(3)
  assert.equal(calls.length, 2, '执行中重复点击只发一次请求')
  assert.equal(calls[1].request.dryRun, false, '执行 dryRun=false')
  assert.deepEqual(calls[1].request.patch, calls[0].request.patch, '执行的补丁与预览一致')
  assert.equal(api().executing, true)
  assert.equal(api().confirmBlock, 'executing')
  calls[1].pending.resolve(executeResult)
  await Promise.all([first, second])
  await settle(3)
  assert.equal(api().executing, false)
  assert.equal(api().result?.updated, 2, '成功后给出执行结果')
  assert.equal(api().confirmBlock, 'done', '成功后同一弹窗不再允许执行')
  await api().execute()
  assert.equal(calls.length, 2)
  assert.equal(api().canPreview, false, '执行成功后也不再预览')

  // ---------- 在途预览遇到输入变化：中止，迟到响应丢弃 ----------
  await reset()
  autoRespond = false
  api().setField('height', { enabled: true, value: 150 })
  await settle(3)
  const inflight = api().runPreview()
  await settle(3)
  assert.equal(api().previewing, true)
  assert.equal(api().canPreview, false, '预览中不能重复发起')
  const stale = calls[0]
  api().setField('height', { value: 151 })
  await settle(3)
  assert.equal(stale.signal?.aborted, true, '输入变了：在途请求被中止')
  assert.equal(api().previewing, false)
  stale.pending.resolve(previewResult)
  await inflight
  await settle(3)
  assert.equal(api().preview, null, '迟到的旧响应不能变成新输入的预览')
  assert.equal(api().canPreview, true, '可以对新输入重新预览')

  // ---------- 预览失败：保留输入，按错误码提示；改动输入后提示消失 ----------
  await reset()
  autoRespond = false
  api().setField('width', { enabled: true, value: 70 })
  await settle(3)
  const failing = api().runPreview()
  await settle(3)
  calls[0].pending.reject({ status: 400, payload: { errorCode: 'PLANOGRAM_BATCH_TOO_MANY', message: '匹配过多', details: 2300 } })
  await failing
  await settle(3)
  assert.equal(api().error?.kind, 'tooMany')
  assert.equal(api().error?.matchedCount, 2300)
  assert.equal(api().error?.message, '2300 products match, over the limit of 2000. Narrow the filters.', '英文界面用错误码对应的英文文案（带匹配数）')
  assert.equal(api().state.width.value, 70, '失败时输入原样保留')
  assert.equal(api().preview, null)
  api().setField('width', { value: 72 })
  await settle(3)
  assert.equal(api().error, null, '改动输入后失败提示消失')

  // 字段校验失败落到字段
  autoRespond = false
  const invalid = api().runPreview()
  await settle(3)
  calls[1].pending.reject({ status: 400, payload: { errorCode: 'PLANOGRAM_VALIDATION', message: '不合法', details: [{ field: 'widthMm', message: '包装宽必须在 1～10000 之间' }] } })
  await invalid
  await settle(3)
  assert.equal(api().error?.kind, 'validation')
  assert.equal(api().error?.fieldErrors.width, '包装宽必须在 1～10000 之间')

  // ---------- 执行失败：保留输入与预览，可再次执行 ----------
  await reset()
  api().setField('depth', { enabled: true, value: 20 })
  await settle(3)
  await api().runPreview()
  await settle(3)
  assert.equal(api().confirmBlock, null)
  autoRespond = false
  const executeFailing = api().execute()
  await settle(3)
  calls[1].pending.reject({ status: 400, payload: { errorCode: 'PLANOGRAM_BATCH_EMPTY', message: '无匹配' } })
  await executeFailing
  await settle(3)
  assert.equal(api().result, null, '失败没有结果')
  assert.equal(api().error?.kind, 'empty')
  assert.equal(api().state.depth.value, 20, '输入保留')
  assert.ok(api().preview, '预览仍然有效')
  assert.equal(api().confirmBlock, null, '可以再次执行')
  assert.equal(api().executing, false)

  // ---------- 执行时并发冲突：整批已回滚，预览作废，必须重新预览 ----------
  await reset()
  api().setField('depth', { enabled: true, value: 20 })
  await settle(3)
  await api().runPreview()
  await settle(3)
  assert.equal(api().confirmBlock, null)
  autoRespond = false
  const conflicting = api().execute()
  await settle(3)
  calls[1].pending.reject({ status: 409, payload: { errorCode: 'PLANOGRAM_BATCH_CONFLICT', message: '有其他人同时修改了这些商品' } })
  await conflicting
  await settle(3)
  assert.equal(api().error?.kind, 'conflict')
  assert.equal(api().result, null, '冲突不算执行成功')
  assert.equal(api().preview, null, '冲突后预览作废')
  assert.equal(api().confirmBlock, 'noPreview', '必须重新预览才能再执行')
  assert.equal(api().canPreview, true, '可以直接重新预览')
  assert.equal(api().state.depth.value, 20, '输入保留')

  // ---------- 超过 2000：不发请求 ----------
  await reset(batchTargetFromFilter({ keyword: '', status: 'all', warehouseCategoryGuids: [], localSupplierCode: null, supplierCategoryGuids: [], supplierCategoryUnassignedOnly: false, grades: [] }, 2001))
  api().setField('width', { enabled: true, value: 70 })
  await settle(3)
  assert.equal(api().tooMany, true)
  assert.equal(api().canPreview, false)
  await api().runPreview()
  assert.equal(calls.length, 0, '目标超过 2000：预览 / 执行都不发请求')
  assert.equal(api().confirmBlock, 'tooMany')

  // ---------- 按筛选的请求体带 filter ----------
  await reset(batchTargetFromFilter({ keyword: '笔', status: 'unset', warehouseCategoryGuids: ['wh-a'], localSupplierCode: 'S1', supplierCategoryGuids: [], supplierCategoryUnassignedOnly: true, grades: ['none'] }, 40))
  api().setField('width', { enabled: true, value: 70 })
  api().setField('height', { enabled: true, value: 150 })
  api().setField('depth', { enabled: true, value: 25 })
  api().setCreateMissing(true)
  await settle(3)
  await api().runPreview()
  await settle(3)
  const filterBody = calls[0].request
  assert.equal(filterBody.createMissing, true)
  assert.ok('filter' in filterBody.target && !('productCodes' in filterBody.target), '按筛选只带 filter')
  assert.deepEqual('filter' in filterBody.target && filterBody.target.filter.grades, ['none'])

  // ---------- 卸载：在途响应不再更新状态、请求被中止 ----------
  await reset()
  autoRespond = false
  api().setField('width', { enabled: true, value: 70 })
  await settle(3)
  const orphan = api().runPreview()
  await settle(3)
  unmountAll()
  assert.equal(calls[0].signal?.aborted, true, '卸载时中止在途预览')
  calls[0].pending.resolve(previewResult)
  await orphan
} finally {
  unmountAll()
}

console.log('useBatchEdit.test: ok')
