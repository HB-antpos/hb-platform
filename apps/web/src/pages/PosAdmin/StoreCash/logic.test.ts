import assert from 'node:assert/strict'
import type { CashDailyRow, CashExpenseListItem, CashStoreOption } from '../../../types/storeCash'
import { RequestError } from '../../../utils/request'
import {
  addDays,
  buildCashExportFileName,
  categoryAmount,
  checkRange,
  chooseCashErrorText,
  compareNullableNumber,
  dailyInflow,
  defaultRange,
  formatAud,
  formatSignedAud,
  formatStoreDateTime,
  isDateDisabled,
  isNegativeAmount,
  isReasonValid,
  parseCashSearch,
  rangeDayCount,
  referenceToday,
  resolveCashError,
  resolveCashFilters,
  reviewNoteRequired,
  serializeCashQuery,
  sortDailyRows,
  summarizeDaily,
  summarizeExpenses,
  toCents,
  weekdayKeyOf,
  type CashPageQuery,
} from './logic'

// ---------------------------------------------------------------------------
// 日期区间：含两端最多 93 天（后端判断 to − from < 93），按 UTC 日历计算
// ---------------------------------------------------------------------------

assert.equal(rangeDayCount('2026-10-01', '2026-10-01'), 1)
assert.equal(rangeDayCount('2026-10-01', '2026-10-08'), 8)
assert.equal(rangeDayCount('2026-10-08', '2026-10-01'), 0, '倒序返回 0')
assert.equal(addDays('2026-02-28', 1), '2026-03-01')
assert.equal(addDays('2024-02-28', 1), '2024-02-29', '闰年')
assert.equal(addDays('2026-01-01', -1), '2025-12-31')
// 澳洲夏令时切换日（10 月第一个周日）不影响日期加减。
assert.equal(addDays('2026-10-03', 1), '2026-10-04')
assert.equal(addDays('2026-10-04', 1), '2026-10-05')

assert.deepEqual(defaultRange('2026-10-07'), { from: '2026-10-01', to: '2026-10-07' }, '默认本月 1 日 ~ 今天')
assert.deepEqual(defaultRange('2026-10-01'), { from: '2026-10-01', to: '2026-10-01' }, '月初当天只有一天')

assert.equal(checkRange('2026-07-07', addDays('2026-07-07', 92)), null, '93 天（含两端）允许')
assert.equal(checkRange('2026-07-07', addDays('2026-07-07', 93)), 'tooLong', '94 天超限')
assert.equal(checkRange('2026-10-08', '2026-10-01'), 'order')
assert.equal(checkRange('2026-02-30', '2026-03-01'), 'invalid', '不存在的日期')
assert.equal(checkRange(undefined, '2026-03-01'), 'invalid')

// 日期选择器：不能晚于门店今天；选了一端后，超过 93 天窗口的另一端禁选。
assert.equal(isDateDisabled('2026-10-08', '2026-10-07'), true)
assert.equal(isDateDisabled('2026-10-07', '2026-10-07'), false)
assert.equal(isDateDisabled(addDays('2026-10-07', -92), '2026-10-07', '2026-10-07'), false, '向前 93 天（含两端）可选')
assert.equal(isDateDisabled(addDays('2026-10-07', -93), '2026-10-07', '2026-10-07'), true, '向前 94 天禁选')
assert.equal(isDateDisabled(addDays('2026-06-01', 93), '2026-10-07', '2026-06-01'), true, '向后超出窗口禁选')

// 页面「今天」取各店最晚的门店本地日期；没有分店时用兜底。
const stores: CashStoreOption[] = [
  { storeCode: 'S001', storeName: 'Sydney', timeZoneId: 'Australia/Sydney', storeToday: '2026-10-08' },
  { storeCode: 'S002', storeName: 'Perth', timeZoneId: 'Australia/Perth', storeToday: '2026-10-07' },
]
assert.equal(referenceToday(stores, '2026-01-01'), '2026-10-08')
assert.equal(referenceToday([], '2026-01-01'), '2026-01-01')
assert.equal(weekdayKeyOf('2026-10-07'), 'storeCash.weekday.wed')
assert.equal(weekdayKeyOf('2026-10-04'), 'storeCash.weekday.sun')

// ---------------------------------------------------------------------------
// 地址栏参数：解析、序列化、往返稳定
// ---------------------------------------------------------------------------

assert.deepEqual(parseCashSearch(''), { tab: 'overview', stores: [], voided: false }, '空参数回到默认')
const parsed = parseCashSearch('?tab=expenses&from=2026-10-01&to=2026-10-08&stores=S001&stores=S002&stores=S001&store=S002&category=T2&review=Flagged&voided=1')
assert.deepEqual(parsed, {
  tab: 'expenses',
  from: '2026-10-01',
  to: '2026-10-08',
  stores: ['S001', 'S002'],
  store: 'S002',
  category: 'T2',
  review: 'Flagged',
  voided: true,
})
assert.deepEqual(parseCashSearch(serializeCashQuery(parsed)), parsed, '序列化后再解析应完全一致')
assert.equal(serializeCashQuery(parsed), serializeCashQuery(parseCashSearch(serializeCashQuery(parsed))), '序列化结果稳定')
assert.equal(
  serializeCashQuery({ tab: 'overview', stores: [], voided: false }),
  'tab=overview',
  '默认值省略，只保留页签',
)

// 非法值丢弃：未知页签、不存在的类别 / 核对状态、超长或倒序区间、只有一端的区间。
const sanitized = parseCashSearch('tab=bogus&category=Bonus&review=Maybe&voided=yes&from=2026-01-01&to=2026-10-07')
assert.equal(sanitized.tab, 'overview')
assert.equal(sanitized.category, undefined)
assert.equal(sanitized.review, undefined)
assert.equal(sanitized.voided, false)
assert.equal(sanitized.from, undefined, '超过 93 天的区间整体丢弃')
assert.equal(sanitized.to, undefined)
assert.equal(parseCashSearch('from=2026-10-01').from, undefined, '只有一端的区间丢弃')
assert.equal(parseCashSearch('from=2026-10-08&to=2026-10-01').from, undefined, '倒序区间丢弃')

// 合成实际查询条件：默认区间按门店今天；不可见的分店被过滤；单店缺省取第一家。
const resolvedDefault = resolveCashFilters({ tab: 'overview', stores: ['S002', 'S999'], voided: false }, stores, '2026-01-01')
assert.deepEqual(resolvedDefault, {
  today: '2026-10-08',
  from: '2026-10-01',
  to: '2026-10-08',
  storeCodes: ['S002'],
  storeCode: 'S001',
})
const explicit: CashPageQuery = { tab: 'daily', from: '2026-09-01', to: '2026-09-30', stores: [], store: 'S002', voided: false }
assert.equal(resolveCashFilters(explicit, stores, '2026-01-01').storeCode, 'S002')
assert.equal(resolveCashFilters({ ...explicit, store: 'S999' }, stores, '2026-01-01').storeCode, 'S001', '不可见分店回到第一家')
assert.equal(resolveCashFilters(explicit, [], '2026-01-01').storeCode, null, '没有可见分店时为 null')
assert.equal(resolveCashFilters(explicit, stores, '2026-01-01').from, '2026-09-01')
// 单店页签的默认区间与日期上限按该店自己的今天（珀斯比悉尼晚一天时不出现未来的日子）。
const perthDefault = resolveCashFilters({ tab: 'deposits', stores: [], store: 'S002', voided: false }, stores, '2026-01-01')
assert.deepEqual([perthDefault.today, perthDefault.from, perthDefault.to], ['2026-10-07', '2026-10-01', '2026-10-07'])
const overviewDefault = resolveCashFilters({ tab: 'overview', stores: [], store: 'S002', voided: false }, stores, '2026-01-01')
assert.equal(overviewDefault.to, '2026-10-08', '总览仍取各店最晚的今天')

// ---------------------------------------------------------------------------
// 金额：千分位、澳元符号、负数、空值「—」
// ---------------------------------------------------------------------------

assert.equal(toCents(1.005), 101, '浮点误差不能把 1.005 舍成 1.00')
assert.equal(toCents(-1.005), -101, '正负对称')
assert.equal(toCents(-0.004), 0, '不出现 -0')
assert.equal(toCents(null), null)
assert.equal(toCents(Number.NaN), null)
assert.equal(formatAud(1234567.5), '$1,234,567.50')
assert.equal(formatAud(-12), '-$12.00')
assert.equal(formatAud(0), '$0.00')
assert.equal(formatAud(-0.001), '$0.00', '不显示 -$0.00')
assert.equal(formatAud(null), '—', '空值显示破折号，不显示 0')
assert.equal(formatAud(undefined), '—')
assert.equal(formatSignedAud(12.5), '+$12.50')
assert.equal(formatSignedAud(-5), '-$5.00')
assert.equal(formatSignedAud(0), '$0.00')
assert.equal(formatSignedAud(null), '—')
assert.equal(isNegativeAmount(-0.01), true)
assert.equal(isNegativeAmount(-0.001), false)
assert.equal(isNegativeAmount(null), false)
assert.deepEqual([3, null, -1, undefined, 2].sort(compareNullableNumber), [-1, 2, 3, null, undefined], 'null 排最后')

// ---------------------------------------------------------------------------
// 时间：UTC 按门店时区显示
// ---------------------------------------------------------------------------

assert.equal(formatStoreDateTime('2026-10-06T23:30:00Z', 'Australia/Sydney'), '2026-10-07 10:30', '悉尼夏令时 UTC+11')
assert.equal(formatStoreDateTime('2026-10-06T23:30:00Z', 'Australia/Perth'), '2026-10-07 07:30', '珀斯 UTC+8')
assert.equal(formatStoreDateTime('2026-10-06T23:30:00Z', 'Not/AZone'), '2026-10-07 10:30', '非法时区按悉尼')
assert.equal(formatStoreDateTime(null, 'Australia/Sydney'), '—')
assert.equal(formatStoreDateTime('not a date', 'Australia/Sydney'), '—')

// ---------------------------------------------------------------------------
// 合计
// ---------------------------------------------------------------------------

function expense(overrides: Partial<CashExpenseListItem>): CashExpenseListItem {
  return {
    expenseGuid: Math.random().toString(36),
    storeCode: 'S001',
    expenseDate: '2026-10-01',
    category: 'Other',
    amount: 0,
    payeeName: null,
    note: null,
    reviewStatus: 'None',
    reviewNote: null,
    reviewedByName: null,
    reviewedAtUtc: null,
    canReview: false,
    status: 'Active',
    imageCount: 0,
    createdByName: null,
    createdAtUtc: '2026-10-01T00:00:00Z',
    canVoid: false,
    ...overrides,
  }
}

const expenseSummary = summarizeExpenses([
  expense({ category: 'Salary', amount: 0.1 }),
  expense({ category: 'Salary', amount: 0.2 }),
  expense({ category: 'Purchase', amount: 100.35, reviewStatus: 'Flagged' }),
  expense({ category: 'T2', amount: 50 }),
  expense({ category: 'Other', amount: 9.99 }),
  expense({ category: 'Other', amount: 1000, status: 'Voided' }),
])
assert.deepEqual(expenseSummary.byCategory, { Salary: 0.3, Purchase: 100.35, T2: 50, Other: 9.99 }, '按分累加，没有浮点尾差')
assert.equal(expenseSummary.total, 160.64)
assert.equal(expenseSummary.count, 5, '已作废不计入')
assert.equal(expenseSummary.voidedCount, 1)
assert.equal(expenseSummary.flaggedCount, 1)
assert.deepEqual(summarizeExpenses([]).byCategory, { Salary: 0, Purchase: 0, T2: 0, Other: 0 })

assert.equal(categoryAmount([{ category: 'T2', amount: 12 }], 'T2'), 12)
assert.equal(categoryAmount([{ category: 'T2', amount: 12 }], 'Salary'), null, '缺这一类显示「—」而不是臆造 0')

function dailyRow(overrides: Partial<CashDailyRow>): CashDailyRow {
  return { businessDate: '2026-10-01', inflowCash: 0, hasClose: false, covered: false, coveredByDepositGuid: null, expenseTotal: 0, devices: [], ...overrides }
}
const dailyRows = [
  dailyRow({ businessDate: '2026-10-03', inflowCash: 100.1, hasClose: true, covered: false, expenseTotal: 5 }),
  dailyRow({ businessDate: '2026-10-01', inflowCash: 200.2, hasClose: true, covered: true }),
  dailyRow({ businessDate: '2026-10-02', inflowCash: 0, hasClose: false, expenseTotal: 1.5 }),
]
assert.deepEqual(sortDailyRows(dailyRows).map((row) => row.businessDate), ['2026-10-01', '2026-10-02', '2026-10-03'])
assert.deepEqual(summarizeDaily(dailyRows, true), { inflow: 300.3, closeDays: 2, uncoveredDays: 1, expenseTotal: 6.5 })
// 日结未接入：日结现金不可算（null），天数不统计，支出照常。
assert.deepEqual(summarizeDaily(dailyRows, false), { inflow: null, closeDays: 0, uncoveredDays: 0, expenseTotal: 6.5 })
assert.equal(dailyInflow(dailyRows[0], false), null, '未接入时逐日日结现金显示「—」')
assert.equal(dailyInflow(dailyRows[0], true), 100.1)

// ---------------------------------------------------------------------------
// 原因 / 说明
// ---------------------------------------------------------------------------

assert.equal(isReasonValid('重复'), true, '两个汉字即可')
assert.equal(isReasonValid(' 错 '), false, '去掉首尾空格后只有一个字')
assert.equal(isReasonValid('ab'), true)
assert.equal(isReasonValid(''), false)
assert.equal(isReasonValid(null), false)
assert.equal(isReasonValid('😀'), false, 'emoji 按一个字符计')
assert.equal(reviewNoteRequired('Flagged'), true, '存疑必须写说明')
assert.equal(reviewNoteRequired('Reviewed'), false)
assert.equal(reviewNoteRequired('None'), false)

// ---------------------------------------------------------------------------
// 错误码 → 文案
// ---------------------------------------------------------------------------

const abort = new Error('aborted')
abort.name = 'AbortError'
assert.deepEqual(resolveCashError(abort), { kind: 'abort' }, '取消的请求不提示')

const forbidden = resolveCashError(new RequestError('x', 403, { success: false, errorCode: 'CASH_STORE_FORBIDDEN', message: '无权查看该分店' }))
assert.deepEqual(forbidden, { kind: 'error', key: 'storeCash.errors.storeForbidden', code: 'CASH_STORE_FORBIDDEN', status: 403, serverMessage: '无权查看该分店' })

const conflict = resolveCashError(new RequestError('x', 409, { success: false, errorCode: 'CASH_CONFLICT', message: '' }))
assert.equal(conflict.kind === 'error' && conflict.key, 'storeCash.errors.conflict')
assert.equal(conflict.kind === 'error' && conflict.serverMessage, null, '空 message 当作没有')

// 没有错误码按 HTTP 状态归类；未知错误码同样回退到状态。
const byStatus = (status: number, payload: unknown = {}) => {
  const resolved = resolveCashError(new RequestError('x', status, payload))
  return resolved.kind === 'error' ? resolved.key : 'abort'
}
assert.equal(byStatus(400), 'storeCash.errors.invalidRequest')
assert.equal(byStatus(404), 'storeCash.errors.recordNotFound')
assert.equal(byStatus(409), 'storeCash.errors.conflict')
assert.equal(byStatus(500), 'storeCash.errors.server')
assert.equal(byStatus(403, { errorCode: 'CASH_SOMETHING_NEW' }), 'storeCash.errors.storeForbidden')
assert.equal(byStatus(418), 'storeCash.errors.unknown')
assert.equal(byStatus(200, { errorCode: 'CASH_DATE_OUT_OF_RANGE' }), 'storeCash.errors.dateOutOfRange', 'HTTP 200 + success=false 也按错误码')

const network = resolveCashError(new TypeError('Failed to fetch'))
assert.equal(network.kind === 'error' && network.key, 'storeCash.errors.network')
assert.equal((resolveCashError('boom') as { key: string }).key, 'storeCash.errors.unknown')

// 中文界面优先用服务端的具体中文 message；英文界面用错误码文案。
const translate = (key: string) => `T(${key})`
if (forbidden.kind === 'error') {
  assert.equal(chooseCashErrorText(forbidden, 'zh-CN', translate), '无权查看该分店')
  assert.equal(chooseCashErrorText(forbidden, 'en', translate), 'T(storeCash.errors.storeForbidden)')
}
if (conflict.kind === 'error') {
  assert.equal(chooseCashErrorText(conflict, 'zh', translate), 'T(storeCash.errors.conflict)', '没有服务端 message 时用文案')
}

// ---------------------------------------------------------------------------
// 导出文件名
// ---------------------------------------------------------------------------

assert.equal(buildCashExportFileName('现金总览', '2026-10-01', '2026-10-08'), '现金总览_2026-10-01_2026-10-08.csv')
assert.equal(buildCashExportFileName('现金存款', '2026-10-01', '2026-10-08', 'S001'), '现金存款_S001_2026-10-01_2026-10-08.csv')
assert.equal(buildCashExportFileName('CashDeposits', '2026-10-01', '2026-10-08', 'S/0 1'), 'CashDeposits_S_0_1_2026-10-01_2026-10-08.csv', '文件名非法字符替换')

console.log('storeCash logic.test: ok')
