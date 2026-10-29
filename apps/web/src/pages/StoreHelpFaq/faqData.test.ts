import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

import type { StoreHelpFaqGroup, StoreHelpFaqTab } from './faqContent'
import {
  STORE_HELP_FAQ_API_PATH,
  fetchStoreHelpFaqPayload,
  loadStoreHelpFaqGroups,
  loadStoreHelpFaqSnapshot,
  parseStoreHelpFaqPayload,
  type StoreHelpFaqGroupsByTab,
} from './faqData'
import { RequestError } from '../../utils/request'

// 测试在 apps/web 目录下运行（与同目录其他测试一致，用相对路径读文件）
const snapshotText = readFileSync('src/pages/StoreHelpFaq/faqSnapshot.json', 'utf8')
const snapshot = JSON.parse(snapshotText) as { version: number; tabs: Record<StoreHelpFaqTab, StoreHelpFaqGroup[]> }

// 前端兜底快照与后端种子必须逐字节一致：两边都从同一份内容生成，任何一边单独改动都会让线上与兜底内容分叉
const seedBytes = readFileSync('../../services/backend/BlazorApp.Api/Data/SeedData/help-faq-seed.json')
assert.ok(readFileSync('src/pages/StoreHelpFaq/faqSnapshot.json').equals(seedBytes), 'faqSnapshot.json 与 help-faq-seed.json 不一致')

// 每次返回一份全新的接口 data 副本（any：各用例要按需改坏其中的字段）
const cloneSnapshotPayload = () => JSON.parse(snapshotText)
const snapshotGroups = (): StoreHelpFaqGroupsByTab => ({ pos: snapshot.tabs.pos, mobile: snapshot.tabs.mobile })

// —— 结构校验 ——
// 合法数据原样通过（同时说明快照本身满足接口校验规则）
assert.deepEqual(parseStoreHelpFaqPayload(cloneSnapshotPayload()), snapshotGroups())
// 后端把缺省的可选字段序列化成 null，也应当视为没有，且不会被带进页面数据
{
  const payload = cloneSnapshotPayload()
  const question = payload.tabs.pos[0].questions[0]
  Object.assign(question, { tips: null, rules: null, permissions: null, related: null, unknownField: 1 })
  const parsed = parseStoreHelpFaqPayload(payload)
  const parsedQuestion = parsed.pos[0].questions[0]
  assert.equal(parsedQuestion.tips, undefined)
  assert.equal(parsedQuestion.permissions, undefined)
  assert.ok(!('unknownField' in parsedQuestion))
}
// 空分组被丢弃，其他分组保留
{
  const payload = cloneSnapshotPayload()
  payload.tabs.pos.push({ id: 'empty', title: { en: 'Empty', zh: '空' }, questions: [] })
  assert.deepEqual(parseStoreHelpFaqPayload(payload), snapshotGroups())
}

function expectInvalid(label: string, mutate: (payload: any) => unknown) {
  const payload = cloneSnapshotPayload()
  // mutate 返回 undefined 表示就地修改 payload；返回其他值（含 null）表示整体替换
  const replaced = mutate(payload)
  const next = replaced === undefined ? payload : replaced
  assert.throws(() => parseStoreHelpFaqPayload(next), /FAQ 数据结构不合法/, `${label} 应判为不合法`)
}
expectInvalid('payload 不是对象', () => 'not-json')
expectInvalid('payload 为 null', () => null)
expectInvalid('缺 tabs', (payload) => { delete payload.tabs })
expectInvalid('tabs.pos 不是数组', (payload) => { payload.tabs.pos = {} })
expectInvalid('缺 tabs.mobile', (payload) => { delete payload.tabs.mobile })
expectInvalid('问题缺 id', (payload) => { delete payload.tabs.pos[0].questions[0].id })
expectInvalid('问题 id 为空白', (payload) => { payload.tabs.pos[0].questions[0].id = '  ' })
expectInvalid('缺中文标题', (payload) => { delete payload.tabs.pos[0].questions[0].title.zh })
expectInvalid('英文标题为空', (payload) => { payload.tabs.pos[0].questions[0].title.en = '' })
expectInvalid('英文步骤不是数组', (payload) => { payload.tabs.pos[0].questions[0].steps.en = 'step' })
expectInvalid('中文步骤缺失', (payload) => { delete payload.tabs.pos[0].questions[0].steps.zh })
expectInvalid('步骤里有非字符串', (payload) => { payload.tabs.pos[0].questions[0].steps.en.push(1) })
expectInvalid('tips 只给了一种语言', (payload) => { payload.tabs.pos[0].questions[0].tips = { en: [] } })
expectInvalid('分组缺 questions', (payload) => { delete payload.tabs.pos[0].questions })
expectInvalid('问题 id 重复', (payload) => { payload.tabs.mobile[0].questions[0].id = payload.tabs.pos[0].questions[0].id })
expectInvalid('一道题都没有', (payload) => { payload.tabs.pos = []; payload.tabs.mobile = [] })

// 快照加载器返回的数据与快照文件一致（动态 import 能被解析）
assert.deepEqual(await loadStoreHelpFaqSnapshot(), snapshotGroups())

// —— 加载流程：三种回退分支 + 成功分支 ——
const remoteGroups: StoreHelpFaqGroupsByTab = {
  pos: [{
    id: 'remote-group',
    title: { en: 'Remote', zh: '远程' },
    questions: [{
      id: 'remote-question',
      title: { en: 'From API', zh: '来自接口' },
      where: { en: 'Where', zh: '入口' },
      steps: { en: ['One'], zh: ['一'] },
    }],
  }],
  mobile: [],
}
const remotePayload = { version: '7', tabs: remoteGroups }

function createSnapshotLoader() {
  const loader = async () => {
    loader.calls += 1
    return snapshotGroups()
  }
  loader.calls = 0
  return loader
}

// 接口成功：使用接口数据，不触碰快照
{
  const loadSnapshot = createSnapshotLoader()
  const result = await loadStoreHelpFaqGroups({ fetcher: async () => remotePayload, loadSnapshot, timeoutMs: 1000 })
  assert.equal(result.source, 'api')
  assert.deepEqual(result.groups, remoteGroups)
  assert.equal(loadSnapshot.calls, 0, '接口成功时不应加载快照')
}

// 接口抛错：回退快照
{
  const loadSnapshot = createSnapshotLoader()
  const result = await loadStoreHelpFaqGroups({
    fetcher: async () => { throw new RequestError('请求失败 (500)', 500) },
    loadSnapshot,
    timeoutMs: 1000,
  })
  assert.equal(result.source, 'snapshot')
  assert.deepEqual(result.groups, snapshotGroups())
  assert.equal(loadSnapshot.calls, 1)
}

// 结构不合法（缺 tabs、返回 HTML 文本、题全空）：回退快照
for (const invalidPayload of [{ version: '1' }, '<!doctype html><html></html>', undefined, { version: '1', tabs: { pos: [], mobile: [] } }]) {
  const loadSnapshot = createSnapshotLoader()
  const result = await loadStoreHelpFaqGroups({ fetcher: async () => invalidPayload, loadSnapshot, timeoutMs: 1000 })
  assert.equal(result.source, 'snapshot')
  assert.equal(loadSnapshot.calls, 1)
}

// 超时：fetcher 永不返回（且不响应 signal）也能在 timeoutMs 内回退快照
{
  const loadSnapshot = createSnapshotLoader()
  const startedAt = Date.now()
  const result = await loadStoreHelpFaqGroups({ fetcher: () => new Promise<unknown>(() => {}), loadSnapshot, timeoutMs: 30 })
  assert.equal(result.source, 'snapshot')
  assert.ok(Date.now() - startedAt < 1000, '超时应很快回退，不能一直等')
}

// 超时会取消请求（signal 被 abort），且接口先返回时不会残留超时计时器去取消请求
{
  let abortedSignal: AbortSignal | undefined
  await loadStoreHelpFaqGroups({
    fetcher: (signal) => {
      abortedSignal = signal
      return new Promise<unknown>((_resolve, reject) => signal.addEventListener('abort', () => reject(new Error('aborted'))))
    },
    loadSnapshot: createSnapshotLoader(),
    timeoutMs: 20,
  })
  assert.equal(abortedSignal?.aborted, true, '超时后应取消请求')

  let fastSignal: AbortSignal | undefined
  await loadStoreHelpFaqGroups({
    fetcher: async (signal) => { fastSignal = signal; return remotePayload },
    loadSnapshot: createSnapshotLoader(),
    timeoutMs: 20,
  })
  await new Promise((resolve) => setTimeout(resolve, 60))
  assert.equal(fastSignal?.aborted, false, '请求已完成，超时计时器应被清除')
}

// 快照也加载失败：把错误交给调用方（页面显示重试）
await assert.rejects(
  loadStoreHelpFaqGroups({
    fetcher: async () => { throw new Error('offline') },
    loadSnapshot: async () => { throw new Error('chunk load failed') },
    timeoutMs: 1000,
  }),
  /chunk load failed/,
)

// —— 默认 fetcher：匿名请求不能触发登录跳转 ——
// 页面免登录，接口若返回 401（例如网关策略变更），request 的默认行为是刷新令牌并跳转 /login；
// 这里确认带 skipAuthRedirect 后不会刷新令牌、不会派发 auth-expired、不会跳转。
{
  const originalFetch = globalThis.fetch
  const originalWindow = globalThis.window
  const requested: string[] = []
  let redirectedTo = ''
  let authExpiredEvents = 0
  Object.defineProperty(globalThis, 'window', {
    configurable: true,
    value: {
      location: { pathname: '/support/faq', search: '?lang=zh', replace: (url: string) => { redirectedTo = url } },
      dispatchEvent: () => { authExpiredEvents += 1; return true },
      sessionStorage: { getItem: () => null, setItem: () => {} },
      setTimeout,
      clearTimeout,
    },
  })
  try {
    // 先按成功响应：验证请求地址、方法与 ApiResponse 解包
    globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
      requested.push(`${init?.method ?? 'GET'} ${String(input)}`)
      return new Response(JSON.stringify({ success: true, data: remotePayload }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    }) as typeof fetch
    assert.deepEqual(await fetchStoreHelpFaqPayload(new AbortController().signal), remotePayload)
    assert.deepEqual(requested, [`GET ${STORE_HELP_FAQ_API_PATH}`])

    // 再按 401 响应
    requested.length = 0
    globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
      requested.push(`${init?.method ?? 'GET'} ${String(input)}`)
      return new Response(JSON.stringify({ success: false, message: 'unauthorized' }), {
        status: 401,
        headers: { 'Content-Type': 'application/json' },
      })
    }) as typeof fetch
    await assert.rejects(
      fetchStoreHelpFaqPayload(new AbortController().signal),
      (error: unknown) => error instanceof RequestError && error.status === 401,
    )
    assert.equal(requested.filter((entry) => entry.includes('/api/Auth/session/refresh')).length, 0, '匿名页面不应尝试刷新令牌')
    assert.equal(redirectedTo, '', '匿名页面不应跳转登录页')
    assert.equal(authExpiredEvents, 0, '匿名页面不应派发 auth-expired')

    // 业务失败（HTTP 200 + success:false）也要抛错，让加载流程回退到快照
    globalThis.fetch = (async () => new Response(JSON.stringify({ success: false, message: 'boom' }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })) as typeof fetch
    await assert.rejects(fetchStoreHelpFaqPayload(new AbortController().signal), /boom/)
  } finally {
    globalThis.fetch = originalFetch
    Object.defineProperty(globalThis, 'window', { configurable: true, value: originalWindow })
  }
}

console.log('store help FAQ data tests passed')
