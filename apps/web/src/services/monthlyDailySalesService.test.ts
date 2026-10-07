import assert from 'node:assert/strict'
import {
  getMonthlyStoreDailySales,
  normalizeMonthlyStoreDailySales,
} from './monthlyDailySalesService'

const originalFetch = globalThis.fetch
const requests: { url: URL; init?: RequestInit }[] = []
let nextBody: unknown = {}

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  requests.push({ url: new URL(String(input), 'http://localhost'), init })
  return new Response(JSON.stringify(nextBody), { status: 200, headers: { 'content-type': 'application/json' } })
}) as typeof fetch

try {
  // 1) 请求参数：月份、可重复的授权分店、强制刷新标记、AbortSignal 透传。
  nextBody = {
    success: true,
    data: {
      month: '2026-09',
      daysInMonth: 30,
      countedThroughDate: '2026-09-30',
      countedDays: 30,
      stores: [{ branchCode: '1001', branchName: 'Store A', days: [{ date: '2026-09-01', revenue: 100, card: 60, cash: 30, other: 10 }] }],
    },
  }
  const controller = new AbortController()
  const result = await getMonthlyStoreDailySales({ month: '2026-09', branchCodes: ['1001', '1002'], forceRefresh: true }, controller.signal)
  const first = requests[0]
  assert.equal(first.url.pathname, '/api/react/v1/dashboard/monthly-store-daily-sales')
  assert.equal(first.url.searchParams.get('month'), '2026-09')
  assert.deepEqual(first.url.searchParams.getAll('branchCodes'), ['1001', '1002'], '授权分店应按重复参数传递')
  assert.equal(first.url.searchParams.get('forceRefresh'), 'true')
  assert.equal(first.init?.method, 'GET')
  assert.equal(first.init?.signal, controller.signal)
  assert.equal(result.stores[0]?.days[0]?.card, 60, '信封应被解包并规范化')

  // 管理员 / 全局范围不传 branchCodes；forceRefresh 默认明确传 false。
  await getMonthlyStoreDailySales({ month: '2026-09' })
  const second = requests[1]
  assert.equal(second.url.searchParams.has('branchCodes'), false, '全局范围不应传 branchCodes')
  assert.equal(second.url.searchParams.get('forceRefresh'), 'false')

  // 2) 授权范围为空数组：不发请求（空数组会被丢弃，服务端会误当成不限分店），直接返回空月份。
  const before = requests.length
  const empty = await getMonthlyStoreDailySales({ month: '2026-02', branchCodes: [] })
  assert.equal(requests.length, before, '授权范围为空时不应请求接口')
  assert.deepEqual(empty, { month: '2026-02', daysInMonth: 28, countedThroughDate: null, countedDays: 0, stores: [] })

  // 3) 业务失败（success=false）必须抛错，不能当成空数据静默显示。
  nextBody = { success: false, message: 'boom' }
  await assert.rejects(() => getMonthlyStoreDailySales({ month: '2026-09' }), /boom/)
} finally {
  globalThis.fetch = originalFetch
}

// 4) 规范化：空值语义。
const normalized = normalizeMonthlyStoreDailySales({
  success: true,
  data: {
    Month: '2026-09',
    DaysInMonth: 30,
    CountedThroughDate: '2026-09-06',
    CountedDays: 6,
    Stores: [
      {
        BranchCode: ' 2001 ',
        BranchName: '  ',
        Days: [
          // 乱序输入应按日期升序，重复日期保留第一条。
          { Date: '2026-09-03', Revenue: 0, Card: 0, Cash: 0, Other: 0 },
          { Date: '2026-09-01', Revenue: 1200.5, Card: 800, Cash: 300.25, Other: 100.25 },
          { Date: '2026-09-01', Revenue: 999, Card: 1, Cash: 1, Other: 1 },
          // 缺数：revenue 为 null。
          { date: '2026-09-02', revenue: null, card: null, cash: null, other: null },
          // 无支付方式拆分：revenue 有值，三项为 null / 缺字段 / 非数值。
          { date: '2026-09-04', revenue: 500, card: null, cash: '12', other: Number.NaN },
          { date: '2026-09-05', revenue: 700 },
          // 非法日期、其他月份的日期、非对象都应被丢弃。
          { date: '2026-09-31', revenue: 1 },
          { date: '2026-10-01', revenue: 1 },
          { date: 'not-a-date', revenue: 1 },
          null,
        ],
      },
      { BranchCode: '2001', BranchName: 'dup', Days: [] },
      { BranchName: 'no code', Days: [] },
      { branchCode: '2002', branchName: 'Store B' },
    ],
  },
}, '2026-09')

assert.equal(normalized.month, '2026-09')
assert.equal(normalized.daysInMonth, 30)
assert.equal(normalized.countedThroughDate, '2026-09-06')
assert.equal(normalized.countedDays, 6)
assert.deepEqual(normalized.stores.map(store => store.branchCode), ['2001', '2002'], '重复与无编码的分店应被丢弃')
const [storeA, storeB] = normalized.stores
assert.equal(storeA.branchName, '2001', '分店名缺失时回退为编码')
assert.deepEqual(storeA.days.map(day => day.date), ['2026-09-01', '2026-09-02', '2026-09-03', '2026-09-04', '2026-09-05'])
assert.deepEqual(storeA.days[0], { date: '2026-09-01', revenue: 1200.5, card: 800, cash: 300.25, other: 100.25 }, '重复日期保留第一条')
assert.deepEqual(storeA.days[1], { date: '2026-09-02', revenue: null, card: null, cash: null, other: null }, '缺数日保持 null')
assert.deepEqual(storeA.days[2], { date: '2026-09-03', revenue: 0, card: 0, cash: 0, other: 0 }, '休业日是 0 不是 null')
assert.deepEqual(storeA.days[3], { date: '2026-09-04', revenue: 500, card: null, cash: null, other: null }, '字符串 / NaN 一律按 null')
assert.deepEqual(storeA.days[4], { date: '2026-09-05', revenue: 700, card: null, cash: null, other: null }, '缺字段按 null')
assert.deepEqual(storeB.days, [], '缺 days 时按空数组')

// 5) 规范化：截止日 / 已计入天数的兜底。
const noDays = normalizeMonthlyStoreDailySales({ month: '2026-10', countedThroughDate: null, countedDays: 0, stores: [] }, '2026-10')
assert.equal(noDays.countedThroughDate, null)
assert.equal(noDays.countedDays, 0)
assert.equal(noDays.daysInMonth, 31)

const outOfMonth = normalizeMonthlyStoreDailySales({ month: '2026-09', countedThroughDate: '2026-10-01', countedDays: 99, stores: [] }, '2026-09')
assert.equal(outOfMonth.countedThroughDate, null, '截止日不在本月内应视为没有已出数日子')
assert.equal(outOfMonth.countedDays, 30, 'countedDays 不应超过当月天数')

const derived = normalizeMonthlyStoreDailySales({ month: '2026-09', countedThroughDate: '2026-09-12', stores: [] }, '2026-09')
assert.equal(derived.countedDays, 12, '缺 countedDays 时按截止日推算')

const fallbackMonth = normalizeMonthlyStoreDailySales({ stores: [] }, '2026-02')
assert.equal(fallbackMonth.month, '2026-02', '响应缺 month 时使用请求月份')
assert.equal(fallbackMonth.daysInMonth, 28)

const bare = normalizeMonthlyStoreDailySales(
  { month: '2026-09', daysInMonth: 30, countedThroughDate: '2026-09-01', countedDays: 1, stores: [{ branchCode: 'A', branchName: 'A', days: [{ date: '2026-09-01', revenue: 1, card: 1, cash: 0, other: 0 }] }] },
  '2026-09',
)
assert.equal(bare.stores[0].days[0].revenue, 1, '没有信封的裸对象也应被接受')
assert.deepEqual(normalizeMonthlyStoreDailySales(null, '2026-09').stores, [])

console.log('monthlyDailySalesService.test: ok')
