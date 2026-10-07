import assert from 'node:assert/strict'

import type { DailyCloseCashCount, DailyCloseTender } from '../../../types/dailyClose'

import {
  DATE_PRESET_KEYS,
  MAX_RANGE_DAYS,
  MINUS_SIGN,
  addDaysIso,
  buildCashCountSections,
  buildListQuery,
  buildTenderSummary,
  buildTotalsView,
  classifyDailyCloseError,
  classifyDifference,
  createAbortableRequestGuard,
  createDefaultFilters,
  diffDaysIso,
  formatCount,
  formatDenomination,
  formatInStoreTime,
  formatMoney,
  formatSignedMoney,
  formatWeekday,
  getDefaultBusinessDateRange,
  getDetailNotice,
  getPageRange,
  getSaveSequenceBadge,
  hasCashFigures,
  isAbortError,
  isBackfilled,
  isBusinessDateSelectable,
  isIsoDate,
  isStatusFilter,
  parseDetailIdParam,
  parseUtcTimestamp,
  resolveDatePreset,
  resolveTimeZone,
  shortGuid,
  shouldShowTotals,
  statusToQueryParam,
  validateBusinessDateRange,
  withDetailIdParam,
} from './logic'

// ───────── 日期与区间 ─────────

assert.equal(isIsoDate('2026-10-07'), true)
assert.equal(isIsoDate('2026-02-30'), false, '不存在的日期不合法')
assert.equal(isIsoDate('2026-1-7'), false)
assert.equal(isIsoDate(null), false)
assert.equal(addDaysIso('2026-10-07', -6), '2026-10-01')
assert.equal(addDaysIso('2026-03-01', -1), '2026-02-28', '跨月')
assert.equal(addDaysIso('2024-03-01', -1), '2024-02-29', '闰年')
assert.equal(addDaysIso('2026-12-31', 1), '2027-01-01', '跨年')
assert.equal(diffDaysIso('2026-10-01', '2026-10-07'), 6)
assert.equal(diffDaysIso('2026-10-07', '2026-10-01'), -6)
// 夏令时切换那一周（悉尼 2026-10-04 切夏令时）按日历日计算，不能出现 23/25 小时造成的偏差
assert.equal(diffDaysIso('2026-10-03', '2026-10-06'), 3)

assert.deepEqual(getDefaultBusinessDateRange('2026-10-07'), ['2026-10-01', '2026-10-07'], '默认最近 7 天含当天')
const defaults = createDefaultFilters('2026-10-07')
assert.equal(defaults.businessDateFrom, '2026-10-01')
assert.equal(defaults.businessDateTo, '2026-10-07')
assert.deepEqual(defaults.storeCodes, [])
assert.equal(defaults.clientKind, undefined)

assert.deepEqual(resolveDatePreset('today', '2026-10-07'), ['2026-10-07', '2026-10-07'])
assert.deepEqual(resolveDatePreset('yesterday', '2026-10-01'), ['2026-09-30', '2026-09-30'])
assert.deepEqual(resolveDatePreset('last7', '2026-10-07'), ['2026-10-01', '2026-10-07'])
assert.deepEqual(resolveDatePreset('last30', '2026-10-07'), ['2026-09-08', '2026-10-07'])
assert.deepEqual(resolveDatePreset('thisMonth', '2026-10-07'), ['2026-10-01', '2026-10-07'])
assert.deepEqual(resolveDatePreset('lastMonth', '2026-10-07'), ['2026-09-01', '2026-09-30'])
assert.deepEqual(resolveDatePreset('lastMonth', '2026-01-15'), ['2025-12-01', '2025-12-31'], '上月跨年')
assert.deepEqual(resolveDatePreset('lastMonth', '2026-03-10'), ['2026-02-01', '2026-02-28'])
for (const key of DATE_PRESET_KEYS) {
  const [from, to] = resolveDatePreset(key, '2026-10-07')
  assert.equal(validateBusinessDateRange(from, to), 'ok', `快捷区间 ${key} 必须在 93 天内`)
}

assert.equal(validateBusinessDateRange('2026-10-01', '2026-10-07'), 'ok')
assert.equal(validateBusinessDateRange('2026-10-07', '2026-10-07'), 'ok', '单日合法')
assert.equal(validateBusinessDateRange('2026-10-08', '2026-10-07'), 'reversed')
assert.equal(validateBusinessDateRange('', '2026-10-07'), 'required')
assert.equal(validateBusinessDateRange(null, null), 'required')
assert.equal(validateBusinessDateRange('bad', '2026-10-07'), 'required')
// 93 天含首尾：from + 92 天是最后一个合法结束日，再多一天就超限
assert.equal(validateBusinessDateRange('2026-07-01', addDaysIso('2026-07-01', MAX_RANGE_DAYS - 1)), 'ok')
assert.equal(validateBusinessDateRange('2026-07-01', addDaysIso('2026-07-01', MAX_RANGE_DAYS)), 'tooLong')

// RangePicker 禁用规则：不能选悉尼「今天」之后；已选一端后另一端最多相差 92 天
assert.equal(isBusinessDateSelectable('2026-10-07', '2026-10-07'), true)
assert.equal(isBusinessDateSelectable('2026-10-08', '2026-10-07'), false, '不能选未来')
assert.equal(isBusinessDateSelectable('2026-10-07', '2026-10-07', '2026-07-07'), true, '正好 93 天')
assert.equal(isBusinessDateSelectable('2026-10-07', '2026-10-07', '2026-07-06'), false, '94 天')
assert.equal(isBusinessDateSelectable('2026-06-01', '2026-10-07', '2026-09-01'), true, '向前正好 93 天')
assert.equal(isBusinessDateSelectable('2026-05-31', '2026-10-07', '2026-09-01'), false, '向前超过 93 天也禁用')
assert.equal(isBusinessDateSelectable('2026-09-02', '2026-10-07', '2026-09-01'), true)

// ───────── 金额与差额 ─────────

assert.equal(formatMoney(1842.3), '$1,842.30')
assert.equal(formatMoney(0), '$0.00')
assert.equal(formatMoney(1234567.891), '$1,234,567.89')
assert.equal(formatMoney(-63.5), `${MINUS_SIGN}$63.50`, '负数用 U+2212')
assert.equal(MINUS_SIGN.codePointAt(0), 0x2212)
assert.equal(formatMoney(-0.001), '$0.00', '四舍五入到分为 0 时不带负号')
assert.equal(formatMoney(0.005), '$0.01', '按分四舍五入')
assert.equal(formatMoney(999.999), '$1,000.00')
assert.equal(formatMoney(null), '—')
assert.equal(formatMoney(undefined, '--'), '--')
assert.equal(formatMoney(Number.NaN), '—')

assert.equal(formatSignedMoney(2), '+$2.00')
assert.equal(formatSignedMoney(-3.5), `${MINUS_SIGN}$3.50`)
assert.equal(formatSignedMoney(0), '$0.00', '已平不带符号')
assert.equal(formatSignedMoney(0.004), '$0.00')
assert.equal(formatSignedMoney(-1842.3), `${MINUS_SIGN}$1,842.30`)
assert.equal(formatSignedMoney(null), '—')

assert.equal(formatCount(142), '142')
assert.equal(formatCount(1234), '1,234')
assert.equal(formatCount(6.5), '6.5')
assert.equal(formatCount(0), '0')
assert.equal(formatCount(null), '—')

assert.equal(classifyDifference(-3.5), 'short')
assert.equal(classifyDifference(2), 'over')
assert.equal(classifyDifference(0), 'even')
assert.equal(classifyDifference(0.003), 'even', '不足一分按已平')
assert.equal(classifyDifference(null), 'none')
assert.equal(classifyDifference(undefined), 'none')

// ───────── 分页、状态与查询参数 ─────────

assert.deepEqual(getPageRange(1, 20, 58), { from: 1, to: 20 })
assert.deepEqual(getPageRange(3, 20, 58), { from: 41, to: 58 })
assert.deepEqual(getPageRange(1, 20, 0), { from: 0, to: 0 })

assert.equal(isStatusFilter('short'), true)
assert.equal(isStatusFilter('bogus'), false)
assert.equal(statusToQueryParam('all'), undefined, '全部不传 status')
assert.equal(statusToQueryParam('over'), 'over')

const query = buildListQuery(
  {
    businessDateFrom: '2026-10-01',
    businessDateTo: '2026-10-07',
    storeCodes: [' 1008 ', '1015', '1008', ''],
    deviceCode: '  POS01 ',
    clientKind: 'Wpf',
    keyword: ' Mei ',
  },
  'short',
  2,
  50,
)
assert.deepEqual(query, {
  businessDateFrom: '2026-10-01',
  businessDateTo: '2026-10-07',
  storeCodes: '1008,1015',
  deviceCode: 'POS01',
  clientKind: 'Wpf',
  keyword: 'Mei',
  status: 'short',
  page: 2,
  pageSize: 50,
})
const bareQuery = buildListQuery(createDefaultFilters('2026-10-07'), 'all', 1, 20)
assert.equal(bareQuery.storeCodes, undefined, '没选分店不传 storeCodes')
assert.equal(bareQuery.deviceCode, undefined)
assert.equal(bareQuery.keyword, undefined)
assert.equal(bareQuery.status, undefined)
assert.equal(bareQuery.clientKind, undefined)

const counts = { all: 58, short: 9, over: 4, even: 43, none: 2 }
assert.equal(shouldShowTotals('all', counts), true)
assert.equal(shouldShowTotals('short', counts), true)
assert.equal(shouldShowTotals('none', counts), false, '无金额页签不显示合计')
assert.equal(shouldShowTotals('all', { ...counts, all: 0 }), false, '没有记录不显示合计')
assert.equal(shouldShowTotals('over', null), false)

const totalsView = buildTotalsView({ expectedCash: 71356.2, countedCash: 71314.7, difference: -41.5 })
assert.deepEqual(totalsView, {
  expected: '$71,356.20',
  counted: '$71,314.70',
  difference: `${MINUS_SIGN}$41.50`,
  differenceKind: 'short',
})

// 最新请求守卫：新请求取消旧请求，旧请求不再是 latest
const guard = createAbortableRequestGuard()
const first = guard.begin()
const second = guard.begin()
assert.equal(first.signal.aborted, true, '开始新请求会取消上一次')
assert.equal(second.signal.aborted, false)
assert.equal(guard.isLatest(first.requestId), false)
assert.equal(guard.isLatest(second.requestId), true)
guard.abort()
assert.equal(second.signal.aborted, true)
assert.equal(guard.isLatest(second.requestId), false)

// ───────── 地址栏中的抽屉状态 ─────────

const guid = 'A3F29C01-7B4E-4D2A-9C3E-5F1D8E60B274'
assert.equal(parseDetailIdParam(guid), guid.toLowerCase(), '统一小写')
assert.equal(parseDetailIdParam(` ${guid.toLowerCase()} `), guid.toLowerCase())
assert.equal(parseDetailIdParam('not-a-guid'), null)
assert.equal(parseDetailIdParam('a3f29c01-7b4e-4d2a-9c3e'), null)
assert.equal(parseDetailIdParam(''), null)
assert.equal(parseDetailIdParam(null), null)
assert.equal(parseDetailIdParam('../../etc/passwd'), null, '非 GUID 一律当没有，不会拼进请求路径')

const withId = withDetailIdParam(new URLSearchParams('foo=bar'), guid.toLowerCase())
assert.equal(withId.get('id'), guid.toLowerCase())
assert.equal(withId.get('foo'), 'bar', '保留其他查询参数')
const withoutId = withDetailIdParam(withId, null)
assert.equal(withoutId.has('id'), false)
assert.equal(withoutId.get('foo'), 'bar')
assert.equal(shortGuid(guid), 'A3F29C01')
assert.equal(shortGuid(guid.toLowerCase()), 'A3F29C01')

// ───────── 门店本地时区 ─────────

assert.equal(resolveTimeZone('Australia/Brisbane'), 'Australia/Brisbane')
assert.equal(resolveTimeZone(null), 'Australia/Sydney', '缺失回退悉尼')
assert.equal(resolveTimeZone(''), 'Australia/Sydney')
assert.equal(resolveTimeZone('Not/AZone'), 'Australia/Sydney', '无法识别回退悉尼')

// 2026-10-06 10:48:12Z：悉尼已进夏令时（UTC+11）→ 21:48:12；布里斯班不用夏令时（UTC+10）→ 20:48:12
const savedAtUtc = '2026-10-06T10:48:12Z'
assert.equal(formatInStoreTime(savedAtUtc, 'Australia/Sydney', 'short'), '10-06 21:48')
assert.equal(formatInStoreTime(savedAtUtc, 'Australia/Sydney', 'minutes'), '2026-10-06 21:48')
assert.equal(formatInStoreTime(savedAtUtc, 'Australia/Sydney', 'seconds'), '2026-10-06 21:48:12')
assert.equal(formatInStoreTime(savedAtUtc, 'Australia/Brisbane', 'seconds'), '2026-10-06 20:48:12')
assert.equal(formatInStoreTime(savedAtUtc, undefined, 'seconds'), '2026-10-06 21:48:12', '缺时区按悉尼')
// 缺少 Z 后缀时按 UTC 解释，而不是浏览器本地时区
assert.equal(formatInStoreTime('2026-10-06T10:48:12', 'Australia/Sydney', 'seconds'), '2026-10-06 21:48:12')
assert.equal(formatInStoreTime('2026-10-06T10:48:12.1234567', 'Australia/Sydney', 'minutes'), '2026-10-06 21:48')
// 跨日：UTC 23:30 在悉尼已是次日 10:30
assert.equal(formatInStoreTime('2026-10-06T23:30:00Z', 'Australia/Sydney', 'short'), '10-07 10:30')
// 午夜不能出现 24:00
assert.equal(formatInStoreTime('2026-10-05T13:00:00Z', 'Australia/Sydney', 'seconds'), '2026-10-06 00:00:00')
assert.equal(formatInStoreTime(null, 'Australia/Sydney'), '—')
assert.equal(formatInStoreTime('garbage', 'Australia/Sydney'), '—')
assert.equal(parseUtcTimestamp('2026-10-06T10:48:12+11:00')?.toISOString(), '2026-10-05T23:48:12.000Z')

assert.equal(formatWeekday('2026-10-06', 'zh-CN'), '周二')
assert.equal(formatWeekday('2026-10-06', 'en-AU'), 'Tue')
assert.equal(formatWeekday('2026-10-04', 'zh-CN'), '周日')
assert.equal(formatWeekday('nope', 'zh-CN'), '')

// ───────── 行与明细的标记判定 ─────────

assert.equal(getSaveSequenceBadge({ saveSequence: 2, saveCountInDay: 2 }), 2)
assert.equal(getSaveSequenceBadge({ saveSequence: 1, saveCountInDay: 2 }), 1, '同日多次保存时第 1 次也要标出')
assert.equal(getSaveSequenceBadge({ saveSequence: 1, saveCountInDay: 1 }), null, '只保存一次不标记')
assert.equal(getSaveSequenceBadge({ saveSequence: 0, saveCountInDay: 3 }), null)

assert.equal(isBackfilled({ dataSource: 'ClientUpload', detailLevel: 'Full' }), false)
assert.equal(isBackfilled({ dataSource: 'AuditBackfill', detailLevel: 'CashOnly' }), true)
assert.equal(isBackfilled({ dataSource: 'AuditBackfill', detailLevel: 'Full' }), true, '数据来源是回填就算补录')
assert.equal(isBackfilled({ dataSource: 'ClientUpload', detailLevel: 'TraceOnly' }), true, '明细不完整也算补录')

assert.equal(getDetailNotice({ detailLevel: 'Full' }), null)
assert.equal(getDetailNotice({ detailLevel: 'CashOnly' }), 'cashOnly')
assert.equal(getDetailNotice({ detailLevel: 'TraceOnly' }), 'traceOnly')

assert.equal(hasCashFigures({ expectedCashAmount: 10, countedCashAmount: 9, cashDifference: -1 }), true)
assert.equal(hasCashFigures({ expectedCashAmount: null, countedCashAmount: null, cashDifference: null }), false)
assert.equal(hasCashFigures({ expectedCashAmount: 10, countedCashAmount: null, cashDifference: null }), false)

// ───────── 支付方式汇总 ─────────

const tenders: DailyCloseTender[] = [
  { method: 'Voucher', salesAmount: 85, refundAmount: 0, netAmount: 85 },
  { method: 'Card', salesAmount: 5320.15, refundAmount: 114, netAmount: 5206.15 },
  { method: 'Cash', salesAmount: 1905.8, refundAmount: 63.5, netAmount: 1842.3 },
]
const tenderSummary = buildTenderSummary(tenders)
assert.deepEqual(
  tenderSummary.rows.map((row) => row.method),
  ['Cash', 'Card', 'Voucher'],
  '固定顺序：现金、刷卡、代金券',
)
assert.deepEqual(tenderSummary.total, { salesAmount: 7310.95, refundAmount: 177.5, netAmount: 7133.45 })
assert.equal(tenders[0].method, 'Voucher', '不能改动入参顺序')
assert.deepEqual(buildTenderSummary([{ method: 'Gift', salesAmount: 1, refundAmount: 0, netAmount: 1 }, tenders[2]]).rows.map((row) => row.method), [
  'Cash',
  'Gift',
])

// ───────── 现金盘点 ─────────

const cashCounts: DailyCloseCashCount[] = [
  { denominationCents: 10000, quantity: 8, subtotalAmount: 800, kind: 'Note' },
  { denominationCents: 5000, quantity: 10, subtotalAmount: 500, kind: 'Note' },
  { denominationCents: 2000, quantity: 18, subtotalAmount: 360, kind: 'Note' },
  { denominationCents: 1000, quantity: 11, subtotalAmount: 110, kind: 'Note' },
  { denominationCents: 500, quantity: 9, subtotalAmount: 45, kind: 'Note' },
  { denominationCents: 200, quantity: 7, subtotalAmount: 14, kind: 'Coin' },
  { denominationCents: 100, quantity: 4, subtotalAmount: 4, kind: 'Coin' },
  { denominationCents: 50, quantity: 8, subtotalAmount: 4, kind: 'Coin' },
  { denominationCents: 20, quantity: 9, subtotalAmount: 1.8, kind: 'Coin' },
]
const sections = buildCashCountSections(cashCounts, 1815, 23.8)
assert.ok(sections)
assert.equal(sections.notes.rows.length, 5, '纸币 5 档')
assert.equal(sections.coins.rows.length, 6, '硬币 6 档：缺的 10c、5c 补 0 行')
assert.deepEqual(
  sections.coins.rows.map((row) => [row.denominationCents, row.quantity]),
  [
    [200, 7],
    [100, 4],
    [50, 8],
    [20, 9],
    [10, 0],
    [5, 0],
  ],
)
assert.equal(sections.notes.subtotal, 1815, '优先用后端给的纸币小计')
assert.equal(sections.coins.subtotal, 23.8)
// 后端没给小计时按行累加（按分求和，不出浮点误差）
const computedSections = buildCashCountSections(cashCounts, null, undefined)
assert.equal(computedSections?.notes.subtotal, 1815)
assert.equal(computedSections?.coins.subtotal, 23.8)
// 没有任何面额数据：返回 null，由界面显示「暂无数据」占位，不能伪造全 0 的盘点
assert.equal(buildCashCountSections([], null, null), null)
// kind 缺失时按面额判断纸币/硬币（≥ $5 为纸币）
const byDenomination = buildCashCountSections(
  [
    { denominationCents: 500, quantity: 1, subtotalAmount: 5, kind: '' },
    { denominationCents: 200, quantity: 1, subtotalAmount: 2, kind: '' },
  ],
  null,
  null,
)
assert.equal(byDenomination?.notes.rows.find((row) => row.denominationCents === 500)?.quantity, 1)
assert.equal(byDenomination?.coins.rows.find((row) => row.denominationCents === 200)?.quantity, 1)

assert.equal(formatDenomination(10000), '$100')
assert.equal(formatDenomination(500), '$5')
assert.equal(formatDenomination(200), '$2')
assert.equal(formatDenomination(100), '$1')
assert.equal(formatDenomination(50), '50c')
assert.equal(formatDenomination(5), '5c')

// ───────── 错误分类 ─────────

assert.equal(classifyDailyCloseError({ status: 403 }), 'forbidden')
assert.equal(classifyDailyCloseError({ status: 404 }), 'notFound')
assert.equal(classifyDailyCloseError({ status: 400, payload: { code: 'INVALID_QUERY', errorCode: 'INVALID_QUERY' } }), 'invalidQuery')
assert.equal(classifyDailyCloseError({ status: 400, payload: { errorCode: 'INVALID_QUERY' } }), 'invalidQuery')
assert.equal(classifyDailyCloseError({ status: 400, payload: { code: 'OTHER' } }), 'failed', '别的 400 不能当成区间问题')
assert.equal(classifyDailyCloseError({ status: 500 }), 'failed')
assert.equal(classifyDailyCloseError(new TypeError('Failed to fetch')), 'failed', '网络错误可重试')
assert.equal(classifyDailyCloseError(null), 'failed')
assert.equal(classifyDailyCloseError('boom'), 'failed')

assert.equal(isAbortError(new DOMException('aborted', 'AbortError')), true)
assert.equal(isAbortError(new Error('x')), false)
assert.equal(isAbortError(null), false)

console.log('DailyCloses logic.test: ok')
