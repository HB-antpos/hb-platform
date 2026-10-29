import assert from 'node:assert/strict'

import type { MyobPayslipCompanyStatus, MyobPayslipRow } from '../../../types/myobPayslip'

import {
  PAYSLIP_DEFAULT_PRESET,
  PAYSLIP_MAX_RANGE_DAYS,
  addDays,
  allCompaniesFailed,
  classifyPayslipError,
  firstFailureKind,
  formatHours,
  formatMoney,
  formatPaperDate,
  formatPaperMoney,
  formatPaperNumber,
  formatPayslipPeriod,
  inclusiveDays,
  isSameEmailAddress,
  isUnassignedPlacement,
  isValidEmailAddress,
  buildPayslipPdfFilename,
  matchPayslipPreset,
  mergePayslipStoreOptions,
  paperLineCells,
  payslipRowKey,
  resolvePayslipPreset,
  sydneyToday,
  validatePayslipRange,
} from './payslipLogic'

// ───────────────────────── 日期预设 ─────────────────────────

// 默认预设与后端默认（最近 90 天）一致。
assert.equal(PAYSLIP_DEFAULT_PRESET, 'last3Months')
assert.deepEqual(resolvePayslipPreset('last3Months', '2026-10-29'), { from: '2026-07-31', to: '2026-10-29' })
assert.deepEqual(resolvePayslipPreset('last2Weeks', '2026-10-29'), { from: '2026-10-15', to: '2026-10-29' })

// 澳洲财年：7 月 1 日起。10 月属于「2026-07-01 起」的本财年，上财年是 2025-07-01 ~ 2026-06-30。
assert.deepEqual(resolvePayslipPreset('thisFinancialYear', '2026-10-29'), { from: '2026-07-01', to: '2026-10-29' })
assert.deepEqual(resolvePayslipPreset('lastFinancialYear', '2026-10-29'), { from: '2025-07-01', to: '2026-06-30' })
// 6 月仍属于上一个 7 月开始的财年；7 月 1 日当天切换。
assert.deepEqual(resolvePayslipPreset('thisFinancialYear', '2026-06-30'), { from: '2025-07-01', to: '2026-06-30' })
assert.deepEqual(resolvePayslipPreset('lastFinancialYear', '2026-06-30'), { from: '2024-07-01', to: '2025-06-30' })
assert.deepEqual(resolvePayslipPreset('thisFinancialYear', '2026-07-01'), { from: '2026-07-01', to: '2026-07-01' })
assert.deepEqual(resolvePayslipPreset('lastFinancialYear', '2026-07-01'), { from: '2025-07-01', to: '2026-06-30' })

// 所有预设都不超过后端单次区间上限。
for (const today of ['2026-01-01', '2026-06-30', '2026-07-01', '2026-10-29', '2026-12-31']) {
  for (const preset of ['last2Weeks', 'last3Months', 'thisFinancialYear', 'lastFinancialYear'] as const) {
    const range = resolvePayslipPreset(preset, today)
    assert.equal(validatePayslipRange(range.from, range.to), 'ok', `${preset} @ ${today}`)
  }
}

// 预设高亮：恰好等于预设才高亮，自定义区间不高亮。
assert.equal(matchPayslipPreset('2026-07-31', '2026-10-29', '2026-10-29'), 'last3Months')
assert.equal(matchPayslipPreset('2026-07-30', '2026-10-29', '2026-10-29'), null)

// 日期运算跨月 / 闰年。
assert.equal(addDays('2026-03-01', -1), '2026-02-28')
assert.equal(addDays('2028-03-01', -1), '2028-02-29')
assert.equal(inclusiveDays('2026-10-29', '2026-10-29'), 1)
assert.equal(inclusiveDays('2024-10-29', '2026-10-29'), 731)

// 悉尼「今天」：UTC 20:00 已经是悉尼次日（夏令时 UTC+11）。
assert.equal(sydneyToday(new Date('2026-10-28T20:00:00Z')), '2026-10-29')
assert.equal(sydneyToday(new Date('2026-06-30T13:00:00Z')), '2026-06-30') // 冬令时 UTC+10：23:00
assert.equal(sydneyToday(new Date('2026-06-30T14:00:00Z')), '2026-07-01')

// 区间校验：与后端一致（含首尾 731 天上限）。
assert.equal(PAYSLIP_MAX_RANGE_DAYS, 731)
assert.equal(validatePayslipRange('2024-10-29', '2026-10-29'), 'ok')
assert.equal(validatePayslipRange('2024-10-28', '2026-10-29'), 'tooLong')
assert.equal(validatePayslipRange('2026-10-30', '2026-10-29'), 'reversed')
assert.equal(validatePayslipRange('', '2026-10-29'), 'incomplete')
assert.equal(validatePayslipRange('2026-10-01', '10/29/2026'), 'incomplete')

// ───────────────────────── 格式化 ─────────────────────────

assert.equal(formatMoney(1812.4), '$ 1,812.40')
assert.equal(formatMoney(-246), '−$ 246.00')
assert.equal(formatMoney(0), '$ 0.00')
assert.equal(formatMoney(58240.5), '$ 58,240.50')
// 缺失显示 —，不是 0：「没有」和「零」不能混。
assert.equal(formatMoney(null), '—')
assert.equal(formatMoney(undefined), '—')
assert.equal(formatMoney(Number.NaN), '—')
assert.equal(formatHours(56.5), '56.50')
assert.equal(formatHours(null), '—')

assert.equal(formatPayslipPeriod('2026-10-06', '2026-10-19'), '10-06 ~ 10-19')
assert.equal(formatPayslipPeriod('2025-12-29', '2026-01-11'), '2025-12-29 ~ 2026-01-11') // 跨年显示完整日期
assert.equal(formatPayslipPeriod(null, null), '—')
assert.equal(formatPayslipPeriod('2026-10-06', null), '2026-10-06 ~ —')

// ───────────────────────── 行与归类 ─────────────────────────

function row(overrides: Partial<MyobPayslipRow> = {}): MyobPayslipRow {
  return {
    connectionGuid: 'c1',
    companyName: 'Investment',
    employeeUid: 'e1',
    employeeName: 'Jane',
    displayId: 'E1',
    userGuid: 'u1',
    paymentDate: '2026-10-23',
    periodStart: '2026-10-06',
    periodEnd: '2026-10-19',
    payFrequency: 'Fortnightly',
    grossPay: 1000,
    taxTotal: -200,
    netPay: 800,
    placement: 'Attendance',
    stores: [{ storeCode: '1002', storeLabel: '1002 Wallsend' }],
    ...overrides,
  }
}

// 同一员工同一支付日的两张不同周期的单必须有不同的键。
assert.notEqual(payslipRowKey(row()), payslipRowKey(row({ periodStart: '2026-09-29' })))
assert.notEqual(payslipRowKey(row()), payslipRowKey(row({ connectionGuid: 'c2' })))
assert.equal(isUnassignedPlacement('Unbound'), true)
assert.equal(isUnassignedPlacement('Unknown'), true)
assert.equal(isUnassignedPlacement('Attendance'), false)
assert.equal(isUnassignedPlacement('Inferred'), false)

// ───────────────────────── 错误归类 ─────────────────────────

assert.equal(classifyPayslipError('MYOB_NEEDS_REAUTHORIZATION', 'x'), 'reauth')
assert.equal(classifyPayslipError('MYOB_API_CALL_FAILED', 'MYOB 工资单报表接口返回 401'), 'reauth')
assert.equal(classifyPayslipError('MYOB_API_CALL_FAILED', 'MYOB 工资单报表接口返回 403'), 'forbidden')
assert.equal(classifyPayslipError('MYOB_API_CALL_FAILED', 'MYOB 工资单报表接口返回 503'), 'unavailable')
assert.equal(classifyPayslipError('MYOB_API_CALL_FAILED', 'MYOB 工资单报表接口返回 429'), 'unavailable')
assert.equal(classifyPayslipError('MYOB_API_CALL_FAILED', '无法连接 MYOB 服务'), 'unavailable')
assert.equal(classifyPayslipError('MYOB_API_CALL_FAILED', '连接 MYOB 服务超时'), 'unavailable')
assert.equal(classifyPayslipError('MYOB_API_CALL_FAILED', 'MYOB 工资单报表接口返回 404'), 'other')
assert.equal(classifyPayslipError(null, null), 'other')

function status(overrides: Partial<MyobPayslipCompanyStatus>): MyobPayslipCompanyStatus {
  return { connectionGuid: 'c', connectionName: 'C', ok: true, count: 1, errorCode: null, errorMessage: null, ...overrides }
}
assert.equal(allCompaniesFailed([]), false) // 没有公司不算「全部失败」
assert.equal(allCompaniesFailed([status({}), status({ ok: false })]), false) // 一家成功就照常显示
assert.equal(allCompaniesFailed([status({ ok: false }), status({ ok: false })]), true)
assert.equal(
  firstFailureKind([status({}), status({ ok: false, errorCode: 'MYOB_API_CALL_FAILED', errorMessage: '返回 503' })]),
  'unavailable',
)
assert.equal(firstFailureKind([status({})]), 'other')

// ───────────────────────── 分店下拉 ─────────────────────────

const opts = mergePayslipStoreOptions(
  [],
  [
    row({ stores: [{ storeCode: '1010', storeLabel: '1010 Ten' }, { storeCode: '1002', storeLabel: '1002 Wallsend' }] }),
    row({ stores: [{ storeCode: '1003', storeLabel: '1003 Bankstown' }] }),
  ],
  null,
)
// 店号按数字序排列（1002 < 1003 < 1010）。
assert.deepEqual(opts.map((item) => item.value), ['1002', '1003', '1010'])
// 累积：筛选后行变少，下拉选项不缩水。
const kept = mergePayslipStoreOptions(opts, [row({ stores: [{ storeCode: '1002', storeLabel: '1002 Wallsend' }] })], null)
assert.deepEqual(kept.map((item) => item.value), ['1002', '1003', '1010'])
// 店长：只保留管辖分店（行里带出的别店标签不能当过滤项）。
assert.deepEqual(
  mergePayslipStoreOptions([], [row({ stores: [{ storeCode: '1002', storeLabel: '1002 Wallsend' }, { storeCode: '1005', storeLabel: '1005 Newcastle' }] })], ['1005']).map(
    (item) => item.value,
  ),
  ['1005'],
)
// 管辖分店为空数组 = 一个都不管，下拉为空（null 才表示不限分店）。
assert.deepEqual(mergePayslipStoreOptions([], [row()], []), [])

// ───────────────────────── MYOB 纸面格式 ─────────────────────────

// 日期 d/m/yyyy 不补零；带时间的 ISO 只取日期；取不到为空串。
assert.equal(formatPaperDate('2026-10-05'), '5/10/2026')
assert.equal(formatPaperDate('2026-10-29T00:00:00'), '29/10/2026')
assert.equal(formatPaperDate(null), '')
assert.equal(formatPaperDate('oops'), '')
// 金额：本期 / YTD 带 $，负数 -$138.00，时薪 4 位小数，缺失留空。
assert.equal(formatPaperMoney(11520), '$11,520.00')
assert.equal(formatPaperMoney(-138), '-$138.00')
assert.equal(formatPaperMoney(-2208), '-$2,208.00')
assert.equal(formatPaperMoney(24, 4), '$24.0000')
assert.equal(formatPaperMoney(0), '$0.00')
assert.equal(formatPaperMoney(null), '')
assert.equal(formatPaperNumber(1.15), '1.15')
assert.equal(formatPaperNumber(30), '30.00')
assert.equal(formatPaperNumber(undefined), '')
// 纸面行：Entitlement 的 YTD 是小时数（不带 $），其余带 $；金额缺失留空。
assert.deepEqual(paperLineCells({ description: 'Base Hourly', type: 'Wage', typeLabel: 'Wages', hours: 30, rate: 24, amount: 720, yearToDate: 11520 }), {
  description: 'Base Hourly',
  hours: '30.00',
  rate: '$24.00',
  amount: '$720.00',
  yearToDate: '$11,520.00',
  type: 'Wages',
})
assert.deepEqual(paperLineCells({ description: 'Annual Leave Accrual', type: 'Entitlement', typeLabel: 'Entitlement', hours: 1.15, rate: null, amount: null, yearToDate: 18.45 }), {
  description: 'Annual Leave Accrual',
  hours: '1.15',
  rate: '',
  amount: '',
  yearToDate: '18.45',
  type: 'Entitlement',
})
assert.equal(paperLineCells({ description: null, type: null, typeLabel: null, hours: null, rate: null, amount: null, yearToDate: null }).type, '')

// ───────────────────────── 下载 / 邮件 ─────────────────────────

assert.equal(buildPayslipPdfFilename('ALEX MORGAN', '2026-10-29'), 'Payslip_ALEX-MORGAN_2026-10-29.pdf')
assert.equal(buildPayslipPdfFilename('  ', '2026-10-29T00:00:00'), 'Payslip_Employee_2026-10-29.pdf')
assert.equal(buildPayslipPdfFilename('A/B:C', '2026-10-29'), 'Payslip_ABC_2026-10-29.pdf')
assert.equal(isValidEmailAddress('alex.morgan@example.com'), true)
assert.equal(isValidEmailAddress(' alex@example.com '), true)
for (const bad of ['', 'alex', 'alex@', '@example.com', 'a@b', 'a b@example.com', 'a@example.com, b@example.com', 'a@example.com;b@example.com']) {
  assert.equal(isValidEmailAddress(bad), false, `${bad} 不应通过`)
}
assert.equal(isSameEmailAddress('Alex@Example.com ', 'alex@example.com'), true)
assert.equal(isSameEmailAddress('a@example.com', 'b@example.com'), false)
assert.equal(isSameEmailAddress(null, ''), true)

console.log('payslipLogic tests passed')
