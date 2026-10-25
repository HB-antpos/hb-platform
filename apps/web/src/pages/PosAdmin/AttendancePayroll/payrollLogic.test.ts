import assert from 'node:assert/strict'
import {
  calculatePay,
  findRatesBelowMinimum,
  formatHours,
  formatMoney,
  formatSignedMoney,
  isoWeekNumber,
  minutesToHours,
  multiplyToCents,
  periodEnd,
  resolvePeriodStart,
  shiftPeriod,
  summarizeEmployeeHours,
} from './payrollLogic'
import { mockGetHours, mockGetPay, mockSaveLeaveApproval } from '../../../services/attendancePayrollMock'

// 十进制小时：7:59 → 7.98，最多 2 位
assert.equal(minutesToHours(479), 7.98)
assert.equal(minutesToHours(450), 7.5)
// 金额按整数分相乘：3.5 × 42.75 = 149.625 → 149.63（不受浮点 149.62499… 影响）
assert.equal(multiplyToCents(3.5, 42.75), 14963)
assert.equal(formatHours(8), '8')
assert.equal(formatHours(7.5), '7.5')
assert.equal(formatHours(0), '—')
assert.equal(formatMoney(1964.5), '$1,964.50')
assert.equal(formatMoney(-20), '−$20.00')
assert.equal(formatSignedMoney(50), '+$50.00')
assert.equal(formatSignedMoney(0), '—')

// 规格第 3 节验收样例：Jack Nguyen 转账+现金，固定 30 h/周 × 2 周 = 60 h
const jack = calculatePay({
  hours: { sunday: 3.5, saturday: 8, leave: 15.6, publicHoliday: 0, weekday: 38.78 },
  payMode: 'Mixed',
  fixedTransferHoursPerWeek: 30,
  weeks: 2,
  rates: { transferWeekdayRate: 28.5, transferSaturdayRate: 35.63, transferSundayRate: 42.75, transferPublicHolidayRate: 64.13, cashRate: 28.5 },
  allowance: -20,
})
// 装填顺序：周日 3.5 → 周六 8 → 带薪假 15.6 → 公假 0 → 平日 32.9
assert.deepEqual(jack.transfer.buckets, { sunday: 3.5, saturday: 8, leave: 15.6, publicHoliday: 0, weekday: 32.9 })
assert.equal(jack.transfer.hours, 60)
assert.equal(jack.transfer.amount, 1816.92)
assert.equal(jack.cash.hours, 5.88)
assert.equal(jack.cash.amount, 167.58)
assert.equal(jack.grossPay, 1964.5)

// 全现金：所有工时（含带薪假）× 现金单一价
const mia = calculatePay({
  hours: { sunday: 5.5, saturday: 15, leave: 0, publicHoliday: 0, weekday: 27.98 },
  payMode: 'Cash',
  weeks: 2,
  rates: { cashRate: 23.1 },
})
assert.equal(mia.transfer.hours, 0)
assert.equal(mia.cash.hours, 48.48)
assert.equal(mia.grossPay, 1119.89)

// 缺时薪：只提示，不当 0 元静默
const missing = calculatePay({
  hours: { sunday: 0, saturday: 0, leave: 0, publicHoliday: 0, weekday: 10 },
  payMode: 'Mixed',
  fixedTransferHoursPerWeek: 5,
  weeks: 1,
  rates: { transferWeekdayRate: 30 },
})
assert.deepEqual(missing.missingRates, ['cash'])

// 低于自填最低时薪：任一档低于就标出
assert.deepEqual(
  findRatesBelowMinimum({ payMode: 'Mixed', minimumRate: 28, transferWeekdayRate: 27.5, transferSaturdayRate: 35, transferSundayRate: 42, transferPublicHolidayRate: 60, cashRate: 28 }),
  ['transferWeekdayRate'],
)
assert.deepEqual(findRatesBelowMinimum({ payMode: 'Cash', cashRate: 23.1 }), [])

// 周期：1 周 = ISO 周一；2 周按双周起始日对齐
assert.equal(resolvePeriodStart('2026-10-18', 1), '2026-10-12')
assert.equal(resolvePeriodStart('2026-10-18', 2, '2026-10-05'), '2026-10-05')
assert.equal(resolvePeriodStart('2026-10-19', 2, '2026-10-05'), '2026-10-19')
assert.equal(resolvePeriodStart('2026-10-04', 2, '2026-10-05'), '2026-09-21')
assert.equal(shiftPeriod('2026-10-05', 2, 1), '2026-10-19')
assert.equal(periodEnd('2026-10-05', 2), '2026-10-18')
assert.equal(isoWeekNumber('2026-10-05'), 41)
assert.equal(isoWeekNumber('2026-10-18'), 42)

// mock 样例数据与设计图 / 验收样例一致（两周 10-05 ~ 10-18）
const hours = await mockGetHours({ storeCode: 'CHERMSIDE', periodStart: '2026-10-05', weeks: 2 })
const totals = Object.fromEntries(hours.employees.map((employee) => [employee.userName, summarizeEmployeeHours(employee)]))
assert.equal(totals['Sarah Kim'].total, 74.55)
assert.equal(totals['Jack Nguyen'].total, 65.88)
assert.equal(totals['Emma Walsh'].leave, 24)
assert.equal(totals['Olivia Chen'].leave, 24)
assert.equal(hours.pendingCount, 6)
const sum = (key: 'weekday' | 'saturday' | 'sunday' | 'publicHoliday' | 'leave' | 'total') => (
  Math.round(Object.values(totals).reduce((acc, item) => acc + item[key], 0) * 100) / 100
)
assert.deepEqual(
  [sum('weekday'), sum('saturday'), sum('sunday'), sum('publicHoliday'), sum('leave'), sum('total')],
  [182.41, 63.98, 35.38, 13.5, 63.6, 358.87],
)

const pay = await mockGetPay({ storeCode: 'CHERMSIDE', periodStart: '2026-10-05', weeks: 2 })
const gross = Object.fromEntries(pay.rows.map((row) => [row.userName, row.grossPay]))
assert.equal(gross['Jack Nguyen'], 1964.5)
assert.equal(gross['Emma Walsh'], 1702.96)
assert.equal(gross['Liam Patel'], 1612.71)
assert.equal(gross['Mia Robinson'], 1119.89)
assert.equal(gross['Sarah Kim'], 3001.68)
assert.equal(gross['Olivia Chen'], 1819.75)
const liam = pay.rows.find((row) => row.userName === 'Liam Patel')!
assert.equal(liam.cash.hours, 31.98)
assert.equal(liam.cash.amount, 863.46)
const emma = pay.rows.find((row) => row.userName === 'Emma Walsh')!
assert.deepEqual([emma.leaveDetail.annual?.requestedHours, emma.leaveDetail.annual?.approvedHours], [24, 20])
const payTotal = Math.round(pay.rows.reduce((acc, row) => acc + row.grossPay, 0) * 100) / 100
assert.equal(payTotal, 11221.49)

// 财务改核定工时后，工资按核定工时重算
await mockSaveLeaveApproval({ storeCode: 'CHERMSIDE', periodStart: '2026-10-05', weeks: 2, userGuid: 'mock-emma', leaveType: 'PaidAnnual', approvedHours: 24 })
const repaid = await mockGetPay({ storeCode: 'CHERMSIDE', periodStart: '2026-10-05', weeks: 2 })
assert.equal(repaid.rows.find((row) => row.userName === 'Emma Walsh')!.hours.leave, 24)

console.log('payrollLogic.test.ts: ok')
