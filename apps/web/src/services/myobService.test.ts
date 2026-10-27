import assert from 'node:assert/strict'
import {
  getMyobCompanies,
  getMyobPayCategories,
  getMyobTimesheetPreview,
  normalizeMyobCompanies,
  normalizeMyobConnection,
  normalizeMyobConnections,
  normalizeMyobPayCategories,
  normalizeMyobPreviewRow,
  normalizeMyobPreviewStatus,
  normalizeMyobStatus,
  normalizeMyobStoreMappings,
  normalizeMyobTimesheetPreview,
  saveMyobPayCategoryMapping,
} from './myobService'

// 契约：MyobDtos.cs，camelCase；后端字段缺失 / 类型异常时前端不崩。

// 完整连接
const full = normalizeMyobConnection({
  connectionGuid: 'c1',
  businessId: 'b-1',
  businessName: 'Hot Bargain Pty Ltd',
  abn: '12345678901',
  status: 'Active',
  authorizedByUserGuid: 'u1',
  authorizedByName: 'Sean',
  authorizedAt: '2026-10-27T01:02:03',
  lastRefreshedAt: '2026-10-27T02:00:00Z',
  lastError: null,
  storeCodes: ['1001', '1002'],
})
assert.deepEqual(full, {
  connectionGuid: 'c1',
  businessId: 'b-1',
  businessName: 'Hot Bargain Pty Ltd',
  abn: '12345678901',
  status: 'Active',
  authorizedByUserGuid: 'u1',
  authorizedByName: 'Sean',
  authorizedAt: '2026-10-27T01:02:03',
  lastRefreshedAt: '2026-10-27T02:00:00Z',
  lastError: null,
  storeCodes: ['1001', '1002'],
})

// 缺字段 / 空串 → null 或空数组；没有 connectionGuid 的行被丢弃
const sparse = normalizeMyobConnection({ connectionGuid: 'c2', abn: '', lastError: '  ', storeCodes: [1, 'a', ''] })
assert.equal(sparse?.abn, null)
assert.equal(sparse?.lastError, null)
assert.deepEqual(sparse?.storeCodes, ['a'])
assert.equal(sparse?.businessName, '')
assert.equal(normalizeMyobConnection({ businessName: 'x' }), null)
assert.equal(normalizeMyobConnection(null), null)
assert.deepEqual(normalizeMyobConnections('nope'), [])
assert.equal(normalizeMyobConnections([{ connectionGuid: 'a' }, {}, null]).length, 1)

// 状态：未知值按需重新授权，不当作可用连接
assert.equal(normalizeMyobStatus('Active'), 'Active')
assert.equal(normalizeMyobStatus('NeedsReauth'), 'NeedsReauth')
assert.equal(normalizeMyobStatus('Disconnected'), 'Disconnected')
assert.equal(normalizeMyobStatus('Weird'), 'NeedsReauth')
assert.equal(normalizeMyobStatus(undefined), 'NeedsReauth')

// 分店映射
const mappings = normalizeMyobStoreMappings({
  stores: [
    { storeCode: '1001', storeName: 'A', storeAbn: '12345678901', isActive: true, connectionGuid: 'c1', suggestedConnectionGuid: null, mappedAt: '2026-10-27T00:00:00' },
    { storeCode: '1002', storeName: 'B', isActive: false, connectionGuid: '', suggestedConnectionGuid: 'c1' },
    { storeCode: '1003', storeName: 'C' },
    { storeName: 'no code' },
  ],
  connections: [{ connectionGuid: 'c1', businessName: 'X', status: 'Active' }, {}],
})
assert.deepEqual(mappings.stores.map((item) => item.storeCode), ['1001', '1002', '1003'])
assert.equal(mappings.stores[0].connectionGuid, 'c1')
assert.equal(mappings.stores[1].connectionGuid, null)
assert.equal(mappings.stores[1].suggestedConnectionGuid, 'c1')
assert.equal(mappings.stores[1].isActive, false)
assert.equal(mappings.stores[2].isActive, true, '后端没给 isActive 时按启用处理')
assert.equal(mappings.stores[2].storeAbn, null)
assert.equal(mappings.connections.length, 1)
assert.deepEqual(normalizeMyobStoreMappings(undefined), { stores: [], connections: [] })

// 公司选项（GET /myob/companies，只读、只含 Active）：缺字段 / null / 类型异常给安全默认，没有 connectionGuid 的行丢弃
assert.deepEqual(
  normalizeMyobCompanies([
    { connectionGuid: 'c1', name: 'Investment', abn: '00 000 000 000' },
    { connectionGuid: 'c2', name: 'Australian', abn: null },
    { connectionGuid: 'c3' },
    { connectionGuid: 'c4', name: 5, abn: 123 },
    { name: 'no guid' },
    null,
    'x',
  ]),
  [
    { connectionGuid: 'c1', name: 'Investment', abn: '00000000000' },
    { connectionGuid: 'c2', name: 'Australian', abn: null },
    { connectionGuid: 'c3', name: '', abn: null },
    { connectionGuid: 'c4', name: '', abn: null },
  ],
)
assert.deepEqual(normalizeMyobCompanies(null), [])
assert.deepEqual(normalizeMyobCompanies(undefined), [])
assert.deepEqual(normalizeMyobCompanies({ not: 'an array' }), [])
assert.deepEqual(normalizeMyobCompanies([]), [])

// getMyobCompanies 走 GET /api/react/v1/myob/companies 并解包 ApiResponse
const originalFetch = globalThis.fetch
const fetchCalls: Array<{ url: string; method: string }> = []
let fetchBody: unknown = null
globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  fetchCalls.push({ url: String(input), method: init?.method ?? 'GET' })
  return new Response(JSON.stringify(fetchBody), { status: 200, headers: { 'content-type': 'application/json' } })
}) as typeof fetch
try {
  fetchBody = { success: true, data: [{ connectionGuid: 'c1', name: 'Investment', abn: '00000000000' }, {}] }
  assert.deepEqual(await getMyobCompanies(), [{ connectionGuid: 'c1', name: 'Investment', abn: '00000000000' }])
  assert.equal(fetchCalls.length, 1)
  assert.equal(fetchCalls[0].method, 'GET')
  assert.ok(fetchCalls[0].url.endsWith('/api/react/v1/myob/companies'), fetchCalls[0].url)
  assert.ok(!fetchCalls[0].url.includes('/connections'), '公司选项不能再走需要连接管理权限的 /connections')

  // data 为 null / 缺失时给空列表，不崩
  fetchBody = { success: true, data: null }
  assert.deepEqual(await getMyobCompanies(), [])
  fetchBody = { success: true }
  assert.deepEqual(await getMyobCompanies(), [])

  // HTTP 200 + success=false：抛业务错误，由页面提示
  fetchBody = { success: false, message: 'nope' }
  await assert.rejects(getMyobCompanies(), /nope/)
} finally {
  globalThis.fetch = originalFetch
}

// ───────────── 阶段 2：工资类别映射（契约 MyobTimesheetDtos.cs） ─────────────

const payCategories = normalizeMyobPayCategories({
  connectionGuid: 'c1',
  connectionName: 'Hot Bargain Investment Pty Ltd',
  fetchedAt: '2026-10-27T05:00:00',
  categories: [
    { uid: 'u-base', name: 'Base Hourly', wageType: 'Hourly' },
    { uid: 'u-sat', name: '', wageType: null },
    { name: '没有 uid 的丢弃' },
    null,
  ],
  mapping: { weekdayUid: 'u-base', saturdayUid: '', sundayUid: null },
  suggestions: { saturdayUid: 'u-sat' },
  mappedCount: 1,
  complete: false,
  missingKeys: ['weekday', 'bogus', 5],
  updatedAt: null,
})
assert.equal(payCategories.connectionName, 'Hot Bargain Investment Pty Ltd')
assert.equal(payCategories.categories.length, 2, '没有 uid 的类别丢弃')
assert.equal(payCategories.categories[1].name, 'u-sat', '名称为空时退回 uid，下拉里不出现空白项')
assert.equal(payCategories.categories[0].wageType, 'Hourly')
assert.equal(payCategories.categories[1].wageType, null)
assert.deepEqual(payCategories.mapping, {
  weekdayUid: 'u-base',
  saturdayUid: null,
  sundayUid: null,
  personalLeaveUid: null,
  holidayLeaveUid: null,
})
assert.equal(payCategories.suggestions.saturdayUid, 'u-sat')
assert.deepEqual(payCategories.missingKeys, ['weekday'], '未知的类别键丢弃')
assert.equal(payCategories.updatedAt, null)

// 空 / 异常响应不崩，给安全默认
const emptyCategories = normalizeMyobPayCategories(null)
assert.deepEqual(emptyCategories.categories, [])
assert.equal(emptyCategories.mappedCount, 0)
assert.equal(emptyCategories.complete, false)
assert.deepEqual(emptyCategories.missingKeys, [])
assert.equal(emptyCategories.mapping.weekdayUid, null)
assert.equal(normalizeMyobPayCategories({ categories: 'x', mapping: 5, mappedCount: 99 }).mappedCount, 5, 'mappedCount 限制在 0–5')
// 后端没给 mappedCount 时由映射推出
assert.equal(normalizeMyobPayCategories({ mapping: { weekdayUid: 'a', sundayUid: 'b' } }).mappedCount, 2)

// ───────────── 阶段 2：推送预览 ─────────────

assert.equal(normalizeMyobPreviewStatus('Ready'), 'Ready')
assert.equal(normalizeMyobPreviewStatus('NoTransfer'), 'NoTransfer')
assert.equal(normalizeMyobPreviewStatus('Blocked'), 'Blocked')
assert.equal(normalizeMyobPreviewStatus('Weird'), 'Blocked', '未知状态按被拦截处理，不会显示成可推送')
assert.equal(normalizeMyobPreviewStatus(undefined), 'Blocked')

const previewRow = normalizeMyobPreviewRow({
  userGuid: 'u1',
  userName: 'Jason Nguyen',
  payMode: 'Mixed',
  fixedTransferHoursPerWeek: 20,
  isCrossCompany: true,
  allocatedTransferHours: 15,
  actualHours: 30,
  storeLabels: ['1013 Orion', '', 7],
  status: 'Ready',
  pushed: { weekday: 7, saturday: 8, sunday: 'x' },
  pushedTotal: 15,
  cashOutHours: 0,
  blockers: [],
  hintCodes: ['A'],
  myobName: 'Jason Nguyen',
  myobState: 'Clear',
  detail: {
    days: ['2026-10-26', '2026-10-27', '2026-10-28'],
    actual: [{ key: 'weekday', hours: [8, 8], total: 16 }, { hours: [1] }],
    pushed: [{ key: 'weekday', label: 'Base Hourly', hours: [7, 0, 0, 9, 9], total: 7 }],
    cash: [],
    shares: [
      { connectionGuid: 'c1', connectionName: 'Investment', actualHours: 30, allocatedTransferHours: 15 },
      { connectionName: '没有 guid 的丢弃' },
    ],
    annualCashOutTransferHours: 0,
    checkOk: true,
    checkMessage: '推送合计 15.00 h',
  },
})
assert.ok(previewRow)
assert.equal(previewRow.status, 'Ready')
assert.equal(previewRow.payMode, 'Mixed')
assert.deepEqual(previewRow.storeLabels, ['1013 Orion'])
assert.equal(previewRow.pushed.sunday, 0, '非数字工时按 0，不出现 NaN')
assert.equal(previewRow.pushed.personalLeave, 0)
assert.deepEqual(previewRow.detail.actual.map((item) => item.key), ['weekday'], '没有 key 的网格行丢弃')
assert.deepEqual(previewRow.detail.actual[0].hours, [8, 8, 0], '日网格与 days 对齐：缺的补 0')
assert.deepEqual(previewRow.detail.pushed[0].hours, [7, 0, 0], '日网格与 days 对齐：多的截断')
assert.equal(previewRow.detail.pushed[0].label, 'Base Hourly')
assert.equal(previewRow.detail.actual[0].label, null)
assert.equal(previewRow.detail.shares.length, 1)
assert.equal(previewRow.detail.checkOk, true)

// 没有 userGuid 的行丢弃；有拦截项时一律按被拦截（即使 status 写了 Ready）
assert.equal(normalizeMyobPreviewRow({ userName: 'x' }), null)
assert.equal(normalizeMyobPreviewRow(null), null)
const blockedRow = normalizeMyobPreviewRow({
  userGuid: 'u2',
  status: 'Ready',
  blockers: [{ code: 'NOT_BOUND', message: '未绑定', ref: null }, {}, null],
})
assert.equal(blockedRow?.status, 'Blocked')
assert.equal(blockedRow?.blockers.length, 1)
assert.equal(blockedRow?.blockers[0].ref, null)
// 缺字段整行给默认值
const bareRow = normalizeMyobPreviewRow({ userGuid: 'u3' })
assert.equal(bareRow?.payMode, null)
assert.equal(bareRow?.myobState, 'Unknown')
assert.equal(bareRow?.myobName, null)
assert.deepEqual(bareRow?.detail.days, [])
assert.equal(bareRow?.detail.checkOk, false, '没给 checkOk 时不当作校验通过')
assert.equal(normalizeMyobPreviewRow({ userGuid: 'u3', payMode: 'Weird', myobState: 'Weird' })?.payMode, null)

const preview = normalizeMyobTimesheetPreview({
  connectionGuid: 'c1',
  connectionName: 'Investment',
  periodStart: '2026-10-26',
  periodEnd: '2026-11-01',
  weeks: 0,
  periods: [{ start: '2026-10-26', end: '2026-11-01' }, { end: 'x' }, null],
  readiness: {
    lockedStores: ['1014 Kawana', 3],
    unlockedStores: [{ storeCode: '1042', storeName: 'TestStore', employeeCount: 1 }, { storeName: 'no code' }],
    categoryMappedCount: 9,
    categoryComplete: true,
    myobChecked: true,
    myobProcessedCount: 2,
    myobCheckFailedCount: 1,
  },
  hints: [
    { code: 'BIRTHDAY_REVIEW', userGuid: 'u1', userName: 'Tom', message: '已满 18 岁' },
    { code: 'X', message: '   ' },
  ],
  rows: [
    { userGuid: 'a', status: 'Ready', pushedTotal: 10 },
    { userGuid: 'b', status: 'NoTransfer' },
    { userGuid: 'c', status: 'Blocked', blockers: [{ code: 'NOT_BOUND' }] },
    { noGuid: true },
  ],
  totals: { readyCount: 1, readyHours: 10, blockedCount: 1, noTransferCount: 1 },
  pushEnabled: false,
})
assert.equal(preview.weeks, 1, 'weeks 至少为 1')
assert.deepEqual(preview.periods, [{ start: '2026-10-26', end: '2026-11-01' }])
assert.deepEqual(preview.readiness.lockedStores, ['1014 Kawana'])
assert.deepEqual(preview.readiness.unlockedStores, [{ storeCode: '1042', storeName: 'TestStore', employeeCount: 1 }])
assert.equal(preview.readiness.categoryMappedCount, 5, '映射项数限制在 0–5')
assert.equal(preview.readiness.myobChecked, true)
assert.equal(preview.readiness.myobProcessedCount, 2)
assert.equal(preview.hints.length, 1, '没有说明文字的提示丢弃')
assert.equal(preview.rows.length, 3)
assert.deepEqual(preview.totals, { readyCount: 1, readyHours: 10, blockedCount: 1, noTransferCount: 1 })
assert.equal(preview.pushEnabled, false)

// 缺 totals 时由行推出；整个响应为空也不崩
const derived = normalizeMyobTimesheetPreview({ rows: [{ userGuid: 'a', status: 'Ready', pushedTotal: 8.5 }, { userGuid: 'b', status: 'Ready', pushedTotal: 1.5 }, { userGuid: 'c', status: 'NoTransfer' }] })
assert.deepEqual(derived.totals, { readyCount: 2, readyHours: 10, blockedCount: 0, noTransferCount: 1 })
const emptyPreview = normalizeMyobTimesheetPreview(undefined)
assert.deepEqual(emptyPreview.rows, [])
assert.deepEqual(emptyPreview.periods, [])
assert.equal(emptyPreview.readiness.categoryComplete, false)
assert.equal(emptyPreview.pushEnabled, false)
assert.equal(emptyPreview.weeks, 1)
assert.deepEqual(emptyPreview.totals, { readyCount: 0, readyHours: 0, blockedCount: 0, noTransferCount: 0 })

// 请求：路由、查询参数、请求体
const originalFetch2 = globalThis.fetch
const stage2Calls: Array<{ url: string; method: string; body: unknown }> = []
let stage2Body: unknown = null
globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  stage2Calls.push({ url: String(input), method: init?.method ?? 'GET', body: typeof init?.body === 'string' ? JSON.parse(init.body) : null })
  return new Response(JSON.stringify(stage2Body), { status: 200, headers: { 'content-type': 'application/json' } })
}) as typeof fetch
try {
  stage2Body = { success: true, data: { connectionGuid: 'c1', categories: [{ uid: 'u1', name: 'Base' }], mappedCount: 0 } }
  const read = await getMyobPayCategories('c1')
  assert.equal(read.categories.length, 1)
  assert.equal(stage2Calls[0].method, 'GET')
  assert.ok(stage2Calls[0].url.includes('/api/react/v1/myob/pay-categories'), stage2Calls[0].url)
  assert.ok(stage2Calls[0].url.includes('connectionGuid=c1'), stage2Calls[0].url)
  assert.ok(!stage2Calls[0].url.includes('refresh'), '不强制刷新时不带 refresh 参数')
  await getMyobPayCategories('c1', true)
  assert.ok(stage2Calls[1].url.includes('refresh=true'), stage2Calls[1].url)

  const mapping = { weekdayUid: 'a', saturdayUid: 'b', sundayUid: 'c', personalLeaveUid: 'd', holidayLeaveUid: 'e' }
  await saveMyobPayCategoryMapping('c 1/x', mapping)
  assert.equal(stage2Calls[2].method, 'PUT')
  assert.ok(stage2Calls[2].url.endsWith('/api/react/v1/myob/pay-categories/c%201%2Fx'), stage2Calls[2].url)
  assert.deepEqual(stage2Calls[2].body, mapping)

  stage2Body = { success: true, data: { connectionGuid: 'c1', rows: [{ userGuid: 'u1', status: 'Ready' }] } }
  const previewResult = await getMyobTimesheetPreview('c1')
  assert.equal(previewResult.rows.length, 1)
  assert.equal(stage2Calls[3].method, 'GET')
  assert.ok(stage2Calls[3].url.includes('/api/react/v1/myob/timesheet/preview'), stage2Calls[3].url)
  assert.ok(!stage2Calls[3].url.includes('periodStart') && !stage2Calls[3].url.includes('checkMyob'), '省略周期与检查时不带参数')
  await getMyobTimesheetPreview('c1', { periodStart: '2026-10-26', checkMyob: true })
  assert.ok(stage2Calls[4].url.includes('periodStart=2026-10-26'), stage2Calls[4].url)
  assert.ok(stage2Calls[4].url.includes('checkMyob=true'), stage2Calls[4].url)
  await getMyobTimesheetPreview('c1', { periodStart: null, checkMyob: false })
  assert.ok(!stage2Calls[5].url.includes('periodStart') && !stage2Calls[5].url.includes('checkMyob'), stage2Calls[5].url)

  // data 缺失 / 业务失败
  stage2Body = { success: true }
  assert.deepEqual((await getMyobTimesheetPreview('c1')).rows, [])
  stage2Body = { success: false, message: 'MYOB_TIMESHEET_FORBIDDEN: no access' }
  await assert.rejects(getMyobTimesheetPreview('c1'), /no access/)
} finally {
  globalThis.fetch = originalFetch2
}

console.log('myobService.test: ok')
