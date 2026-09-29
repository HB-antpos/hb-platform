import assert from 'node:assert/strict'
import i18next from 'i18next'
import { createRoot, type Root } from 'react-dom/client'
import { initReactI18next } from 'react-i18next'
import { useShopStore } from '../store/shop'
import { useShopUserStores } from './useShopUserStores'

// 回归：拉取分店的 effect 曾依赖 selectedStore 对象，而 setUserStores 每次都换成新对象，
// 选中分店后便按网络往返节奏无限重拉（生产 /shop 每个标签页约 0.28 秒一次）。
// 这里用真实 React 调度执行 effect，直接统计分店接口的实际请求次数。

const alpha = { storeGUID: 'g-alpha', storeCode: 'A01', storeName: 'Alpha', isActive: true, isManageable: true, assignedAt: '2026-01-01T00:00:00' }
const beta = { storeGUID: 'g-beta', storeCode: 'B01', storeName: 'Beta', isActive: true, isManageable: false, assignedAt: '2026-01-01T00:00:00' }

let responseStores = [alpha, beta]
const requestedUsers: string[] = []

function nextMacrotask() {
  return new Promise<void>((resolve) => setImmediate(resolve))
}

// 固定等待若干轮宏任务：修复后早已稳定，若出现循环则请求数会随轮数持续增长。
async function settle(turns = 50) {
  for (let turn = 0; turn < turns; turn += 1) {
    await nextMacrotask()
  }
}

const originalFetch = globalThis.fetch
globalThis.fetch = (async (input: RequestInfo | URL) => {
  const url = String(input)
  const match = /^\/api\/Users\/guid\/([^/]+)\/stores$/.exec(url)
  if (!match) {
    throw new Error(`意外的请求: ${url}`)
  }

  requestedUsers.push(match[1])
  // 下一个宏任务才返回，模拟网络往返。
  await nextMacrotask()
  return new Response(JSON.stringify({ success: true, data: responseStores }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })
}) as typeof fetch

// 与生产一样由真实 i18next 实例提供 t，保证 t 的引用稳定性与线上一致。
await i18next.use(initReactI18next).init({ lng: 'en', resources: { en: { translation: {} } } })

// React DOM 在 Node 中调度更新会读 window.event，提交阶段会读 window.HTMLIFrameElement；
// 被测组件只返回 null、不创建任何 DOM 节点，所以最小桩即可让 effect 按真实调度执行。
Reflect.set(globalThis, 'window', { HTMLIFrameElement: class {} })

function Harness({ userGuid }: { userGuid?: string }) {
  useShopUserStores(userGuid)
  return null
}

const roots: Root[] = []

function mount(userGuid: string) {
  const noop = () => {}
  const eventTarget = { addEventListener: noop, removeEventListener: noop }
  // 根宿主上下文需要 tagName/namespaceURI，事件委托只调用 addEventListener，均给最小实现。
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
  root.render(<Harness userGuid={userGuid} />)
  return root
}

function unmountAll() {
  for (const root of roots.splice(0)) {
    root.unmount()
  }
}

try {
  // 场景一：多分店用户进入商城后，在下拉框中手动选中分店。
  useShopStore.getState().reset()
  requestedUsers.length = 0
  responseStores = [beta, alpha]
  mount('user-multi')
  await settle()
  assert.deepEqual(requestedUsers, ['user-multi'], '进入商城应只请求一次分店列表')
  assert.deepEqual(
    useShopStore.getState().userStores.map((store) => store.storeCode),
    ['A01', 'B01'],
    '分店列表仍按名称排序',
  )
  assert.equal(useShopStore.getState().selectedStore, null, '多分店用户不应被自动选中')

  // 与 ShopLayout.handleStoreChange 相同，通过 setSelectedStore 选中分店。
  useShopStore.getState().setSelectedStore(useShopStore.getState().userStores[0])
  await settle()
  assert.equal(useShopStore.getState().selectedStore?.storeCode, 'A01')
  assert.equal(requestedUsers.length, 1, `选中分店后不应重复请求分店列表，实际请求 ${requestedUsers.length} 次`)
  unmountAll()

  // 场景二：单分店用户自动选中唯一分店，同样不能因此进入循环；切换用户仍要重新拉取且只拉一次。
  useShopStore.getState().reset()
  requestedUsers.length = 0
  responseStores = [alpha]
  const root = mount('user-single')
  await settle()
  assert.equal(useShopStore.getState().selectedStore?.storeCode, 'A01', '单分店用户应自动选中唯一分店')
  assert.equal(requestedUsers.length, 1, `自动选中分店后不应重复请求分店列表，实际请求 ${requestedUsers.length} 次`)

  root.render(<Harness userGuid="user-other" />)
  await settle()
  assert.deepEqual(requestedUsers, ['user-single', 'user-other'], '切换用户后应重新请求且只请求一次分店列表')
} finally {
  // 断言失败时也要卸载，effect 清理后在途请求的结果会被丢弃，进程才能正常退出。
  unmountAll()
  globalThis.fetch = originalFetch
  Reflect.deleteProperty(globalThis, 'window')
}

console.log('useShopUserStores tests passed')
