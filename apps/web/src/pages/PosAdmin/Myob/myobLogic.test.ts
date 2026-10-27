import assert from 'node:assert/strict'
import type { MyobConnection, MyobStoreMappingItem } from '../../../types/myob'
import {
  MYOB_ERROR_MESSAGE_KEYS,
  MYOB_UNKNOWN_ERROR_KEY,
  applySuggestions,
  buildMappingChanges,
  buildMappingOptions,
  countApplicableSuggestions,
  describeMappedStores,
  extractMyobError,
  filterMappingStores,
  formatAbnDisplay,
  formatMyobTime,
  getEffectiveConnectionGuid,
  getMyobErrorMessageKey,
  isValidAbnInput,
  normalizeAbnInput,
  parseMyobCallback,
  setMappingDraft,
  splitConnections,
  stripMyobCallbackParams,
  upsertConnection,
} from './myobLogic'

function connection(guid: string, name: string, status: MyobConnection['status'] = 'Active'): MyobConnection {
  return {
    connectionGuid: guid,
    businessId: `biz-${guid}`,
    businessName: name,
    abn: null,
    status,
    authorizedByUserGuid: null,
    authorizedByName: null,
    authorizedAt: null,
    lastRefreshedAt: null,
    lastError: null,
    storeCodes: [],
  }
}

function store(code: string, patch: Partial<MyobStoreMappingItem> = {}): MyobStoreMappingItem {
  return {
    storeCode: code,
    storeName: `Store ${code}`,
    storeAbn: null,
    isActive: true,
    connectionGuid: null,
    suggestedConnectionGuid: null,
    mappedAt: null,
    ...patch,
  }
}

// ── ABN：允许空格输入，提交前去空格；11 位数字；空串 = 清除 ──
assert.equal(normalizeAbnInput(' 12 345 678 901 '), '12345678901')
assert.equal(normalizeAbnInput('12 345　678 901'), '12345678901')
assert.equal(normalizeAbnInput(null), '')
assert.equal(isValidAbnInput('12 345 678 901'), true)
assert.equal(isValidAbnInput('12345678901'), true)
assert.equal(isValidAbnInput(''), true, '空串用于清除 ABN')
assert.equal(isValidAbnInput('   '), true)
assert.equal(isValidAbnInput('1234567890'), false, '10 位')
assert.equal(isValidAbnInput('123456789012'), false, '12 位')
assert.equal(isValidAbnInput('1234567890a'), false, '含字母')
assert.equal(isValidAbnInput('12-345-678-901'), false, '连字符不接受')
assert.equal(formatAbnDisplay('12345678901'), '12 345 678 901')
assert.equal(formatAbnDisplay('123'), '123')
assert.equal(formatAbnDisplay(null), '')

// ── 错误码 → 文案键：11 个已知码都有映射，未知 / 空走通用 ──
const knownCodes = [
  'MYOB_NOT_CONFIGURED',
  'MYOB_INVALID_STATE',
  'MYOB_AUTHORIZATION_DENIED',
  'MYOB_TOKEN_EXCHANGE_FAILED',
  'MYOB_BUSINESS_ID_MISSING',
  'MYOB_CONNECTION_NOT_FOUND',
  'MYOB_CONNECTION_NOT_ACTIVE',
  'MYOB_NEEDS_REAUTHORIZATION',
  'MYOB_INVALID_ABN',
  'MYOB_STORE_NOT_FOUND',
  'MYOB_API_CALL_FAILED',
]
assert.deepEqual(Object.keys(MYOB_ERROR_MESSAGE_KEYS).sort(), [...knownCodes].sort(), '与 MyobErrorCodes 一一对应')
for (const code of knownCodes) {
  assert.notEqual(getMyobErrorMessageKey(code), MYOB_UNKNOWN_ERROR_KEY, code)
}
assert.equal(getMyobErrorMessageKey('myob_invalid_abn'), 'myob.errors.invalidAbn', '大小写不敏感')
assert.equal(getMyobErrorMessageKey('MYOB_SOMETHING_NEW'), MYOB_UNKNOWN_ERROR_KEY)
assert.equal(getMyobErrorMessageKey(''), MYOB_UNKNOWN_ERROR_KEY)
assert.equal(getMyobErrorMessageKey(null), MYOB_UNKNOWN_ERROR_KEY)

// ── 授权回调参数 ──
assert.deepEqual(parseMyobCallback('?myob=connected&connection=abc-123'), { kind: 'connected', connectionGuid: 'abc-123' })
assert.deepEqual(parseMyobCallback('?myob=connected'), { kind: 'connected', connectionGuid: null })
assert.deepEqual(parseMyobCallback('?myob=error&code=MYOB_AUTHORIZATION_DENIED'), {
  kind: 'error',
  code: 'MYOB_AUTHORIZATION_DENIED',
})
assert.deepEqual(parseMyobCallback('?myob=error'), { kind: 'error', code: '' })
assert.equal(parseMyobCallback(''), null)
assert.equal(parseMyobCallback('?myob=weird'), null)
assert.equal(parseMyobCallback('?other=1'), null)
assert.equal(stripMyobCallbackParams('?myob=connected&connection=abc'), '')
assert.equal(stripMyobCallbackParams('?myob=error&code=MYOB_X&keep=1'), '?keep=1', '保留无关参数')
assert.equal(stripMyobCallbackParams(''), '')

// ── 请求错误：非 2xx 取 payload.message / code；HTTP 200 失败剥掉 `CODE: ` 前缀 ──
class FakeRequestError extends Error {
  payload?: unknown
  constructor(message: string, payload?: unknown) {
    super(message)
    this.payload = payload
  }
}
assert.deepEqual(extractMyobError(new FakeRequestError('ABN 不合法', { code: 'MYOB_INVALID_ABN', message: 'ABN 不合法' }), 'x'), {
  code: 'MYOB_INVALID_ABN',
  message: 'ABN 不合法',
})
assert.deepEqual(extractMyobError(new FakeRequestError('MYOB_INVALID_ABN: ABN must be 11 digits', { success: false }), 'x'), {
  code: 'MYOB_INVALID_ABN',
  message: 'ABN must be 11 digits',
})
assert.deepEqual(extractMyobError('boom', 'fallback'), { code: null, message: 'fallback' })
assert.deepEqual(extractMyobError(new Error('   '), 'fallback'), { code: null, message: 'fallback' })

// ── 时间：不带 Z 的按 UTC，转悉尼；空值 -- ──
assert.equal(formatMyobTime(null), '--')
assert.equal(formatMyobTime('  '), '--')
// 2026-10-27 00:30 UTC = 悉尼夏令时（UTC+11）11:30
assert.equal(formatMyobTime('2026-10-27T00:30:00'), '2026-10-27 11:30')
assert.equal(formatMyobTime('2026-10-27T00:30:00Z'), '2026-10-27 11:30')
assert.equal(formatMyobTime('2026-10-27T11:30:00+11:00'), '2026-10-27 11:30')
// 冬令时（UTC+10）
assert.equal(formatMyobTime('2026-07-01T00:30:00.123'), '2026-07-01 10:30')

// ── 连接列表 ──
const split = splitConnections([
  connection('c', 'Zeta', 'Active'),
  connection('a', 'Alpha', 'NeedsReauth'),
  connection('d', 'Delta', 'Disconnected'),
])
assert.deepEqual(split.visible.map((item) => item.businessName), ['Alpha', 'Zeta'])
assert.deepEqual(split.disconnected.map((item) => item.businessName), ['Delta'])
const replaced = upsertConnection([connection('a', 'Old')], { ...connection('a', 'New') })
assert.equal(replaced.length, 1)
assert.equal(replaced[0].businessName, 'New')
assert.equal(upsertConnection(replaced, connection('b', 'B')).length, 2)

// ── 映射草稿 / diff：只提交变化 ──
const c1 = 'guid-1'
const c2 = 'guid-2'
const stores = [
  store('1001', { connectionGuid: c1 }),
  store('1002'),
  store('1003', { suggestedConnectionGuid: c2 }),
  store('1004', { isActive: false }),
]
let drafts = {}
assert.deepEqual(buildMappingChanges(stores, drafts), [])
drafts = setMappingDraft(drafts, stores[0], c2)
drafts = setMappingDraft(drafts, stores[1], c1)
assert.deepEqual(buildMappingChanges(stores, drafts), [
  { storeCode: '1001', connectionGuid: c2 },
  { storeCode: '1002', connectionGuid: c1 },
])
// 清空已有映射 = 移除（null），且要出现在提交里
drafts = setMappingDraft({}, stores[0], null)
assert.deepEqual(buildMappingChanges(stores, drafts), [{ storeCode: '1001', connectionGuid: null }])
assert.equal(getEffectiveConnectionGuid(stores[0], drafts), null, '草稿 null 覆盖后端值')
assert.equal(getEffectiveConnectionGuid(stores[1], drafts), null)
// 改回原值 → 草稿消失，不提交
drafts = setMappingDraft(drafts, stores[0], c1)
assert.deepEqual(drafts, {})
assert.deepEqual(buildMappingChanges(stores, drafts), [])
// 未映射的行选了又清空 → 与后端一致，无草稿
drafts = setMappingDraft(setMappingDraft({}, stores[1], c1), stores[1], null)
assert.deepEqual(drafts, {})

// ── 采用建议 ──
const selectable = new Set([c1, c2])
assert.equal(countApplicableSuggestions(stores, {}, selectable), 1)
const adopted = applySuggestions(stores, {}, selectable)
assert.deepEqual(adopted, { '1003': c2 })
assert.deepEqual(buildMappingChanges(stores, adopted), [{ storeCode: '1003', connectionGuid: c2 }])
assert.equal(countApplicableSuggestions(stores, adopted, selectable), 0, '已采用后不再计入')
// 用户已手动给该行选了别的连接 → 不覆盖
const manual = setMappingDraft({}, stores[2], c1)
assert.deepEqual(applySuggestions(stores, manual, selectable), manual)
// 建议的连接不在可选列表 → 不采用
assert.deepEqual(applySuggestions(stores, {}, new Set([c1])), {})
// 用户手动清空（null）已映射行不会被建议覆盖（该行建议恒为 null，仅验证不影响其他行）
assert.deepEqual(applySuggestions(stores, setMappingDraft({}, stores[0], null), selectable), { '1001': null, '1003': c2 })

// ── 未启用分店过滤 ──
assert.deepEqual(filterMappingStores(stores, false, {}).map((item) => item.storeCode), ['1001', '1002', '1003'])
assert.deepEqual(filterMappingStores(stores, true, {}).map((item) => item.storeCode), ['1001', '1002', '1003', '1004'])
assert.deepEqual(
  filterMappingStores(stores, false, { '1004': c1 }).map((item) => item.storeCode),
  ['1001', '1002', '1003', '1004'],
  '有未保存草稿的未启用分店不能被藏起来',
)
const inactiveMapped = [store('1005', { isActive: false, connectionGuid: c1 })]
assert.equal(filterMappingStores(inactiveMapped, false, {}).length, 1, '未启用但已有映射的仍显示')

// ── 下拉选项：Active 可选，已映射的非 Active 连接保留为禁用项 ──
const active = [connection(c1, 'Beta Pty'), connection(c2, 'Alpha Pty')]
const all = [...active, connection('guid-3', 'Gamma', 'NeedsReauth')]
const options = buildMappingOptions(active, all, [store('1', { connectionGuid: 'guid-3' }), store('2', { connectionGuid: 'guid-unknown' })])
assert.deepEqual(options.map((item) => [item.label, item.disabled]), [
  ['Alpha Pty', false],
  ['Beta Pty', false],
  ['Gamma', true],
  ['guid-unknown', true],
].sort((a, b) => String(a[0]).localeCompare(String(b[0]))))
assert.equal(options.find((item) => item.value === 'guid-3')?.status, 'NeedsReauth')

// ── 已映射分店 hover 文案 ──
assert.deepEqual(describeMappedStores(['1002', '1001', '9999'], stores), ['1001 Store 1001', '1002 Store 1002', '9999'])

console.log('myobLogic.test: ok')
