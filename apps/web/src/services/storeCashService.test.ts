import assert from 'node:assert/strict'
import { resolveCashError } from '../pages/PosAdmin/StoreCash/logic'
import {
  getCashContext,
  getCashDaily,
  getCashDeposit,
  getCashOverview,
  listCashDeposits,
  listCashEntries,
  listCashExpenses,
  reviewCashExpense,
  voidCashDeposit,
  voidCashExpense,
} from './storeCashService'

// 现金管理接口封装：路径、查询参数（storeCodes 重复参数、includeVoided 显式传 false）、
// 信封解包、数组字段兜底、可空金额保持 null、失败时带 errorCode 抛错。

interface Captured { url: URL; init?: RequestInit }
const originalFetch = globalThis.fetch
const requests: Captured[] = []
let nextStatus = 200
let nextBody: unknown = {}

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  const url = new URL(String(input), 'http://localhost')
  // 失败请求会异步上报中心日志，这些请求不计入断言。
  if (!url.pathname.startsWith('/api/react/v1/cash')) {
    return new Response('{}', { status: 200, headers: { 'content-type': 'application/json' } })
  }
  requests.push({ url, init })
  return new Response(JSON.stringify(nextBody), { status: nextStatus, headers: { 'content-type': 'application/json' } })
}) as typeof fetch

function last(): Captured {
  const request = requests[requests.length - 1]
  assert.ok(request, '应发出请求')
  return request
}

function ok(data: unknown) {
  nextStatus = 200
  nextBody = { success: true, data }
}

try {
  // 1) context：信封解包、缺字段兜底。
  ok({
    stores: [
      { storeCode: 'S001', storeName: 'A', timeZoneId: 'Australia/Sydney', storeToday: '2026-10-07' },
      { storeCode: 'S002', storeName: 'B', timeZoneId: 'Australia/Sydney', storeToday: '2026-10-07', cashRegisterEnabled: false },
    ],
    capabilities: { canVoid: true },
    dailyCloseConnected: false,
    t2VisibleDays: 14,
  })
  const context = await getCashContext()
  assert.equal(last().url.pathname, '/api/react/v1/cash/context')
  // 旧后端没有 cashRegisterEnabled 时按启用处理；明确 false 的保留。
  assert.deepEqual(context.stores.map((store) => store.cashRegisterEnabled), [true, false])
  assert.deepEqual(context.capabilities, { canCreateDeposit: false, canCreateExpense: false, canViewAllStores: false, canVoid: true })
  assert.equal(context.dailyCloseConnected, false)

  // 2) overview：storeCodes 按重复参数传；可空金额保持 null。
  ok({
    from: '2026-10-01',
    to: '2026-10-07',
    dailyCloseConnected: false,
    t2Restricted: true,
    rows: [{ storeCode: 'S001', storeName: 'A', poolBalance: null, inflowCash: null, closeVariance: null, expenseByCategory: [] }],
    totals: { poolBalance: null, inflowCash: null, closeVariance: null, depositTotal: 10, depositCount: 1, expenseTotal: 5, expenseByCategory: [], uncoveredDayCount: 0, overdueStoreCount: 0, flaggedExpenseCount: 0 },
  })
  const controller = new AbortController()
  const overview = await getCashOverview({ from: '2026-10-01', to: '2026-10-07', storeCodes: ['S001', 'S002'] }, controller.signal)
  assert.equal(last().url.pathname, '/api/react/v1/cash/overview')
  assert.equal(last().url.searchParams.get('from'), '2026-10-01')
  assert.equal(last().url.searchParams.get('to'), '2026-10-07')
  assert.deepEqual(last().url.searchParams.getAll('storeCodes'), ['S001', 'S002'], 'storeCodes 应按重复参数传递')
  assert.equal(last().init?.signal, controller.signal, 'AbortSignal 应透传')
  assert.equal(overview.rows[0].poolBalance, null, '日结未接入时现金池余额保持 null，不补 0')
  assert.equal(overview.totals.inflowCash, null)
  assert.equal(overview.t2Restricted, true)

  // 不传或空数组 = 全部可见分店：不带 storeCodes 参数。
  ok({ from: '2026-10-01', to: '2026-10-07', rows: [], totals: {} })
  const emptyOverview = await getCashOverview({ from: '2026-10-01', to: '2026-10-07', storeCodes: [] })
  assert.equal(last().url.searchParams.has('storeCodes'), false, '空数组不应传 storeCodes')
  assert.deepEqual(emptyOverview.rows, [])
  assert.equal(emptyOverview.totals.poolBalance, null, '合计缺失时可空字段为 null')
  assert.equal(emptyOverview.dailyCloseConnected, false)

  // 3) daily：设备与存档数组兜底。
  ok({ storeCode: 'S001', dailyCloseConnected: true, rows: [{ businessDate: '2026-10-01', inflowCash: 1, hasClose: true, covered: false, expenseTotal: 0, devices: [{ deviceCode: 'POS1' }] }] })
  const daily = await getCashDaily({ storeCode: 'S001', from: '2026-10-01', to: '2026-10-07' })
  assert.equal(last().url.pathname, '/api/react/v1/cash/daily')
  assert.equal(last().url.searchParams.get('storeCode'), 'S001')
  assert.deepEqual(daily.rows[0].devices[0].archives, [])

  // 4) deposits：includeVoided 显式传 false；limit / offset。
  ok({ items: [{ depositGuid: 'd1' }], total: 1 })
  const deposits = await listCashDeposits({ storeCode: 'S001', from: '2026-10-01', to: '2026-10-07', limit: 200, offset: 400 })
  assert.equal(last().url.pathname, '/api/react/v1/cash/deposits')
  assert.equal(last().url.searchParams.get('includeVoided'), 'false')
  assert.equal(last().url.searchParams.get('limit'), '200')
  assert.equal(last().url.searchParams.get('offset'), '400')
  assert.equal(deposits.total, 1)

  // 详情：路径里的编号要编码；存单附件兜底为空数组。
  ok({ depositGuid: 'a/b', slips: [{ slipGuid: 's1' }] })
  const deposit = await getCashDeposit('a/b')
  assert.equal(last().url.pathname, '/api/react/v1/cash/deposits/a%2Fb')
  assert.deepEqual(deposit.slips[0].attachments, [])

  // 作废：POST { reason }。
  ok({ depositGuid: 'd1', status: 'Voided', slips: [] })
  await voidCashDeposit('d1', '重复录入')
  assert.equal(last().init?.method, 'POST')
  assert.equal(last().url.pathname, '/api/react/v1/cash/deposits/d1/void')
  assert.deepEqual(JSON.parse(String(last().init?.body)), { reason: '重复录入' })

  // 5) expenses：类别与核对状态参数；未选时不带。
  ok({ items: [], total: 0 })
  await listCashExpenses({ storeCode: 'S001', category: 'T2', reviewStatus: 'Flagged', includeVoided: true, limit: 200, offset: 0 })
  assert.equal(last().url.pathname, '/api/react/v1/cash/expenses')
  assert.equal(last().url.searchParams.get('category'), 'T2')
  assert.equal(last().url.searchParams.get('reviewStatus'), 'Flagged')
  assert.equal(last().url.searchParams.get('includeVoided'), 'true')
  ok({ items: [], total: 0 })
  await listCashExpenses({ storeCode: 'S001' })
  assert.equal(last().url.searchParams.has('category'), false)
  assert.equal(last().url.searchParams.has('reviewStatus'), false)

  // 核对标记：POST { reviewStatus, note }。
  ok({ expenseGuid: 'e1', reviewStatus: 'Flagged', attachments: null })
  const reviewed = await reviewCashExpense('e1', { reviewStatus: 'Flagged', note: '收据不清楚' })
  assert.equal(last().url.pathname, '/api/react/v1/cash/expenses/e1/review')
  assert.deepEqual(JSON.parse(String(last().init?.body)), { reviewStatus: 'Flagged', note: '收据不清楚' })
  assert.deepEqual(reviewed.attachments, [], '附件缺失时兜底为空数组')

  ok({ expenseGuid: 'e1', status: 'Voided', attachments: [] })
  await voidCashExpense('e1', '金额录错')
  assert.equal(last().url.pathname, '/api/react/v1/cash/expenses/e1/void')

  ok([{ entryGuid: 'x' }])
  const entries = await listCashEntries('S001', true)
  assert.equal(last().url.searchParams.get('includeVoided'), 'true')
  assert.equal(entries.length, 1)

  // 6) 失败：非 2xx 的 ApiResponse 抛 RequestError，带状态码与 errorCode，可映射成文案。
  nextStatus = 403
  nextBody = { success: false, message: '无权作废这条记录', errorCode: 'CASH_VOID_NOT_ALLOWED' }
  let caught: unknown
  try {
    await voidCashExpense('e1', '金额录错')
  } catch (error) {
    caught = error
  }
  const resolved = resolveCashError(caught)
  assert.equal(resolved.kind, 'error')
  if (resolved.kind === 'error') {
    assert.equal(resolved.status, 403)
    assert.equal(resolved.code, 'CASH_VOID_NOT_ALLOWED')
    assert.equal(resolved.key, 'storeCash.errors.voidNotAllowed')
    assert.equal(resolved.serverMessage, '无权作废这条记录')
  }

  // HTTP 200 但 success=false 同样抛错，不能当成空数据。
  nextStatus = 200
  nextBody = { success: false, message: '日期范围无效，最多查询 93 天', errorCode: 'CASH_INVALID_REQUEST' }
  await assert.rejects(() => getCashOverview({ from: '2026-01-01', to: '2026-10-07' }), /93/)
} finally {
  globalThis.fetch = originalFetch
}

console.log('storeCashService.test: ok')
