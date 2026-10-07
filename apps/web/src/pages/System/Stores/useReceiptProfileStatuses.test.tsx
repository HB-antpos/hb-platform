import assert from 'node:assert/strict'
import { createRoot, type Root } from 'react-dom/client'
import { useReceiptProfileStatuses } from './useReceiptProfileStatuses'

// 用真实 React 调度执行 hook 的 effect 与 setState（Web 没有 jsdom，做法见 useShopUserStores.test.tsx）：
// 1) 状态异步补充：请求在途时 loading=true，返回后写入状态表；
// 2) 同一分店被先后请求两次，较旧响应后到时不能覆盖较新的结果；
// 3) 失败不抛出、只在第一次失败回调一次（一次性轻提示），成功后 failed 复位。

function nextMacrotask() {
  return new Promise<void>((resolve) => setImmediate(resolve))
}

async function settle(turns = 10) {
  for (let turn = 0; turn < turns; turn += 1) {
    await nextMacrotask()
  }
}

function item(storeGuid: string, latestVersion: number) {
  return {
    storeGuid,
    storeCode: storeGuid.toUpperCase(),
    storeName: `示例分店 ${storeGuid}`,
    status: latestVersion > 0 ? 'synced' : 'never',
    latestVersion,
    publishedAtUtc: null,
    publishedBy: null,
    current: { brandName: null, storeName: `示例分店 ${storeGuid}`, address: null, phone: null, abn: null, returnPolicy: null },
    latest: null,
    deviceTotal: 0,
    deviceApplied: 0,
  }
}

interface PendingRequest {
  guids: string[]
  respond: (response: { status?: number; body: unknown }) => void
}

// 每个请求挂起，由测试决定先后与内容，模拟乱序到达。
const pending: PendingRequest[] = []
const originalFetch = globalThis.fetch
globalThis.fetch = (async (_input: RequestInfo | URL, init?: RequestInit) => {
  const guids = (JSON.parse(String(init?.body)) as { storeGuids: string[] }).storeGuids
  const response = await new Promise<{ status?: number; body: unknown }>((resolve) => {
    pending.push({ guids, respond: resolve })
  })
  await nextMacrotask()
  return new Response(JSON.stringify(response.body), {
    status: response.status ?? 200,
    headers: { 'Content-Type': 'application/json' },
  })
}) as typeof fetch

Reflect.set(globalThis, 'window', { HTMLIFrameElement: class {} })

let latest: ReturnType<typeof useReceiptProfileStatuses> | undefined
const failures: unknown[] = []

function Harness() {
  latest = useReceiptProfileStatuses({ onFirstFailure: (error) => failures.push(error) })
  return null
}

const roots: Root[] = []
function mount() {
  const noop = () => {}
  const eventTarget = { addEventListener: noop, removeEventListener: noop }
  const container = {
    ...eventTarget,
    nodeType: 1,
    tagName: 'DIV',
    namespaceURI: 'http://www.w3.org/1999/xhtml',
    textContent: '',
    ownerDocument: eventTarget,
  }
  const root = createRoot(container as unknown as HTMLElement)
  roots.push(root)
  root.render(<Harness />)
}

function current() {
  assert.ok(latest, 'hook 应已渲染')
  return latest
}

const originalConsoleError = console.error
const loggedErrors: unknown[][] = []

try {
  // 失败时 hook 会 console.error；这里接管以免污染测试输出，并断言确实记录了。
  console.error = (...args: unknown[]) => { loggedErrors.push(args) }
  mount()
  await settle()
  assert.equal(current().loading, false)
  assert.deepEqual(current().statusByGuid, {})

  // 场景一：正常加载。请求在途时 loading=true，返回后写入。
  void current().refresh(['g1', 'g2'])
  await settle()
  assert.equal(pending.length, 1, '一次 refresh 只发一个请求')
  assert.deepEqual(pending[0].guids, ['g1', 'g2'])
  assert.equal(current().loading, true, '请求在途时处于加载中（状态列显示骨架）')
  pending.shift()?.respond({ body: { success: true, data: [item('g1', 1), item('g2', 0)] } })
  await settle()
  assert.equal(current().loading, false)
  assert.equal(current().failed, false)
  assert.equal(current().statusByGuid.g1.latestVersion, 1)
  assert.equal(current().statusByGuid.g2.status, 'never')

  // 空列表不发请求。
  void current().refresh([])
  await settle()
  assert.equal(pending.length, 0)

  // 场景二：乱序。整页刷新（g1,g2）先发，随后下发后只刷新 g1；较新的先回、较旧的后回。
  void current().refresh(['g1', 'g2'])
  await settle()
  void current().refresh(['g1'])
  await settle()
  assert.equal(pending.length, 2)
  const [pageRequest, publishRequest] = pending.splice(0, 2)
  publishRequest.respond({ body: { success: true, data: [item('g1', 4)] } })
  await settle()
  assert.equal(current().statusByGuid.g1.latestVersion, 4)
  pageRequest.respond({ body: { success: true, data: [item('g1', 3), item('g2', 7)] } })
  await settle()
  assert.equal(current().statusByGuid.g1.latestVersion, 4, '迟到的旧响应不能把 g1 覆盖回旧版本')
  assert.equal(current().statusByGuid.g2.latestVersion, 7, '旧响应里仍然是最新请求的分店照常写入')
  assert.equal(current().loading, false, '两个请求都结束后不再加载中')

  // 场景三：失败（后端未部署 404）。不抛出、保留旧数据、failed=true，只回调一次。
  void current().refresh(['g1'])
  await settle()
  pending.shift()?.respond({ status: 404, body: { success: false, message: 'Not Found' } })
  await settle()
  assert.equal(current().failed, true)
  assert.equal(current().loading, false)
  assert.equal(failures.length, 1, '第一次失败回调一次（一次性轻提示）')
  assert.equal(current().statusByGuid.g1.latestVersion, 4, '失败不清空已有状态')
  assert.ok(loggedErrors.length >= 1, '失败应记录到控制台')

  void current().refresh(['g1'])
  await settle()
  pending.shift()?.respond({ status: 500, body: { success: false, message: 'boom' } })
  await settle()
  assert.equal(failures.length, 1, '之后的失败静默，不重复提示')

  // 恢复后 failed 复位。
  void current().refresh(['g1'])
  await settle()
  pending.shift()?.respond({ body: { success: true, data: [item('g1', 5)] } })
  await settle()
  assert.equal(current().failed, false)
  assert.equal(current().statusByGuid.g1.latestVersion, 5)

  // 门店已被删除：请求过但响应没有 → 从状态表移除（列表行随后也会消失）。
  void current().refresh(['g1', 'g2'])
  await settle()
  pending.shift()?.respond({ body: { success: true, data: [item('g2', 7)] } })
  await settle()
  assert.ok(!('g1' in current().statusByGuid))
} finally {
  console.error = originalConsoleError
  for (const root of roots.splice(0)) {
    root.unmount()
  }
  // 卸载后不再有人读取在途请求，放行以便进程退出。
  for (const request of pending.splice(0)) {
    request.respond({ body: { success: true, data: [] } })
  }
  globalThis.fetch = originalFetch
  Reflect.deleteProperty(globalThis, 'window')
}

console.log('useReceiptProfileStatuses tests passed')
