import assert from 'node:assert/strict'
import { buildPayrollExportRows } from './payrollExport'
import {
  CASH_OVERRIDE_MAX,
  EMPLOYMENT_TYPE_TAG_COLORS,
  MANAGER_TAG_COLOR,
  PERIOD_PRESETS,
  birthdayRateReviews,
  calculatePay,
  cellHoursText,
  findRatesBelowMinimum,
  formatHours,
  formatMoney,
  followWeekdayRateChange,
  formatSignedMoney,
  isoWeekNumber,
  minutesToHours,
  multiplyToCents,
  periodEnd,
  presetPeriodStart,
  resolveCashOverrideCommit,
  resolvePeriodStart,
  shiftPeriod,
  summarizeCashOverrides,
  summarizeEmployeeHours,
  resolvePublicHolidayRate,
} from './payrollLogic'
import { buildSampleHours, buildSamplePay, setSampleCashOverride, setSampleLeaveApproval } from './payrollSample.fixture'

// 十进制小时：7:59 → 7.98，最多 2 位
assert.equal(minutesToHours(479), 7.98)
assert.equal(minutesToHours(450), 7.5)
// 金额按整数分相乘：3.5 × 42.75 = 149.625 → 149.63（不受浮点 149.62499… 影响）
assert.equal(multiplyToCents(3.5, 42.75), 14963)
assert.equal(formatHours(8), '8')
assert.equal(formatHours(7.5), '7.5')
assert.equal(formatHours(0), '—')

// 单元格工时：0 / null / undefined（后端 WhenWritingNull 省略字段）都不显示，避免一片 0
assert.equal(cellHoursText(6), '6')
assert.equal(cellHoursText(7.5), '7.5')
assert.equal(cellHoursText(0), null)
assert.equal(cellHoursText(null), null)
assert.equal(cellHoursText(undefined), null)
assert.equal(cellHoursText(0.004), null, '四舍五入后为 0 的也不显示')
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
// 快捷定位（上周 / 本周 / 下周）：1 周，今天 11-16（周一）所在期 11-16
assert.equal(presetPeriodStart('2026-11-16', 1, '2026-11-16', 'current'), '2026-11-16')
assert.equal(presetPeriodStart('2026-11-16', 1, '2026-11-16', 'previous'), '2026-11-09')
assert.equal(presetPeriodStart('2026-11-16', 1, '2026-11-16', 'next'), '2026-11-23')
// 正在看的期与今天不在同一期：今天落在周日 11-22，已知起点在更早（10-26）或更晚（12-14）都应推出同一个「本周」
assert.equal(presetPeriodStart('2026-10-26', 1, '2026-11-22', 'current'), '2026-11-16')
assert.equal(presetPeriodStart('2026-12-14', 1, '2026-11-22', 'current'), '2026-11-16')
assert.equal(presetPeriodStart('2026-12-14', 1, '2026-11-22', 'previous'), '2026-11-09')
// 跨夏令时（悉尼 2026-10-04 切换）不能差一天：9-28 起每 7 天，10-12 仍是整周边界
assert.equal(presetPeriodStart('2026-09-28', 1, '2026-10-12', 'current'), '2026-10-12')
// 2 周：以后端对齐的 10-05 为基准，每 14 天一期；今天 11-16 / 11-29 都在 11-16 这一期，11-30 进入下一期
assert.equal(presetPeriodStart('2026-10-05', 2, '2026-11-16', 'current'), '2026-11-16')
assert.equal(presetPeriodStart('2026-10-05', 2, '2026-11-29', 'current'), '2026-11-16')
assert.equal(presetPeriodStart('2026-10-05', 2, '2026-11-30', 'current'), '2026-11-30')
assert.equal(presetPeriodStart('2026-10-05', 2, '2026-11-20', 'previous'), '2026-11-02')
assert.equal(presetPeriodStart('2026-10-05', 2, '2026-11-20', 'next'), '2026-11-30')
// 已知起点在今天之后、且今天早于基准的 2 周序列：2026-11-16 为已知起点，今天 10-04 → 本期 09-21
assert.equal(presetPeriodStart('2026-11-16', 2, '2026-10-04', 'current'), '2026-09-21')
assert.deepEqual([...PERIOD_PRESETS], ['previous', 'current', 'next'])
assert.equal(isoWeekNumber('2026-10-05'), 41)
assert.equal(isoWeekNumber('2026-10-18'), 42)

// 样例夹具与设计图 / 验收样例一致（两周 10-05 ~ 10-18）；后端同口径由 PayrollCalculatorTests 锁定
const hours = await buildSampleHours({ storeCode: 'CHERMSIDE', periodStart: '2026-10-05', weeks: 2 })
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

const pay = await buildSamplePay({ storeCode: 'CHERMSIDE', periodStart: '2026-10-05', weeks: 2 })
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
setSampleLeaveApproval('mock-emma', 'PaidAnnual', 24)
const repaid = await buildSamplePay({ storeCode: 'CHERMSIDE', periodStart: '2026-10-05', weeks: 2 })
assert.equal(repaid.rows.find((row) => row.userName === 'Emma Walsh')!.hours.leave, 24)

// 公假价按 MYOB 走：填了就用填的（可与平日价不同）；留空 = 平日价；平日价也空则为空
assert.equal(resolvePublicHolidayRate({ transferWeekdayRate: 30, transferPublicHolidayRate: 67.5 }).transferPublicHolidayRate, 67.5)
assert.equal(resolvePublicHolidayRate({ transferWeekdayRate: 30, transferPublicHolidayRate: null }).transferPublicHolidayRate, 30)
assert.equal(resolvePublicHolidayRate({ transferWeekdayRate: 30 }).transferPublicHolidayRate, 30)
assert.equal(resolvePublicHolidayRate({ transferWeekdayRate: null, transferPublicHolidayRate: null }).transferPublicHolidayRate, undefined)
assert.equal(resolvePublicHolidayRate({ transferWeekdayRate: 30, transferPublicHolidayRate: 0 }).transferPublicHolidayRate, 0, '填 0 是明确填写，不回退到平日价')
// 不改其它档
assert.deepEqual(
  resolvePublicHolidayRate({ transferWeekdayRate: 28.5, transferSaturdayRate: 35.63, transferSundayRate: 42.75, cashRate: 28.5 }),
  { transferWeekdayRate: 28.5, transferSaturdayRate: 35.63, transferSundayRate: 42.75, transferPublicHolidayRate: 28.5, cashRate: 28.5 },
)
// 试算：公假档按填的价（MYOB 的 ×2.25）算；留空按平日价算
const ratesWithHoliday = { transferWeekdayRate: 30, transferSaturdayRate: 37.5, transferSundayRate: 45, transferPublicHolidayRate: 67.5 }
const holidayHours = { sunday: 0, saturday: 0, leave: 0, publicHoliday: 8, weekday: 0 }
assert.equal(calculatePay({ hours: holidayHours, payMode: 'Transfer', weeks: 1, rates: resolvePublicHolidayRate(ratesWithHoliday) }).grossPay, 540)
assert.equal(calculatePay({ hours: holidayHours, payMode: 'Transfer', weeks: 1, rates: resolvePublicHolidayRate({ ...ratesWithHoliday, transferPublicHolidayRate: null }) }).grossPay, 240)

// 改平日价：公假价原本等于平日价（旧规则留下的）就清空跟随；本来就不同（MYOB ×2.25）则不动
assert.equal(followWeekdayRateChange({ transferWeekdayRate: 30, transferPublicHolidayRate: 30 }, { transferWeekdayRate: 31 }).transferPublicHolidayRate, undefined)
assert.equal(followWeekdayRateChange({ transferWeekdayRate: 30, transferPublicHolidayRate: 67.5 }, { transferWeekdayRate: 31 }).transferPublicHolidayRate, 67.5)
assert.equal(followWeekdayRateChange({ transferWeekdayRate: 30, transferPublicHolidayRate: undefined }, { transferWeekdayRate: 31 }).transferPublicHolidayRate, undefined)
assert.equal(followWeekdayRateChange({ transferWeekdayRate: 30, transferPublicHolidayRate: 30 }, { transferSaturdayRate: 40 }).transferPublicHolidayRate, 30, '没改平日价不动公假价')
assert.equal(followWeekdayRateChange({ transferWeekdayRate: 30, transferPublicHolidayRate: 30 }, { transferWeekdayRate: 31, transferPublicHolidayRate: 70 }).transferPublicHolidayRate, 70, '同时显式改公假价以显式值为准')

// 工时页员工类型标签：三种类型与店长颜色两两不同，且避开已有语义色（未成年金、多店青、公假橙、异常红）
const typeColors = [...Object.values(EMPLOYMENT_TYPE_TAG_COLORS), MANAGER_TAG_COLOR]
assert.equal(new Set(typeColors).size, typeColors.length, '员工类型与店长的标签颜色不得重复')
for (const reserved of ['gold', 'cyan', 'orange', 'red']) assert.ok(!typeColors.includes(reserved), `类型颜色不得占用语义色 ${reserved}`)
assert.deepEqual(Object.keys(EMPLOYMENT_TYPE_TAG_COLORS).sort(), ['Casual', 'FullTime', 'PartTime'])

// 生日后时薪待办：只留 hint 为 true 且带生日日期 / 岁数的人；传了期末就只留生日 ≤ 期末的（生日之前的旧期不提示）；按生日先后、再按姓名排
{
  const rate = (userGuid: string, userName: string, extra: Record<string, unknown>) => ({ userGuid, userName, ...extra }) as unknown as Parameters<typeof birthdayRateReviews>[0][number]
  const rates = [
    rate('a', 'Zed', { birthdayReviewHint: true, birthdayReviewDate: '2026-11-12', birthdayReviewAge: 17 }),
    rate('b', 'Amy', { birthdayReviewHint: true, birthdayReviewDate: '2026-11-12', birthdayReviewAge: 21 }), // 刚满 21 岁：AgeYears 已是 null，岁数只靠 birthdayReviewAge
    rate('c', 'Bob', { birthdayReviewHint: true, birthdayReviewDate: '2026-11-03', birthdayReviewAge: 18 }),
    rate('d', 'Cat', { birthdayReviewHint: false, birthdayReviewDate: '2026-11-05', birthdayReviewAge: 19 }), // 已处理
    rate('e', 'Dan', { birthdayReviewHint: true }), // 缺日期 / 岁数：不算
    rate('f', 'Eve', { birthdayReviewHint: true, birthdayReviewDate: '2026-11-20', birthdayReviewAge: 20 }),
  ]
  assert.deepEqual(birthdayRateReviews(rates).map((item) => item.userGuid), ['c', 'b', 'a', 'f'], '不传期末：全部待办，按生日先后再按姓名')
  assert.deepEqual(birthdayRateReviews(rates, '2026-11-15').map((item) => item.userGuid), ['c', 'b', 'a'], '期末 11-15：生日在 11-20 的还没到，不提示')
  assert.deepEqual(birthdayRateReviews(rates, '2026-11-12').map((item) => item.userGuid), ['c', 'b', 'a'], '生日正好是期末当天也要提示')
  assert.deepEqual(birthdayRateReviews(rates, '2026-11-02'), [], '生日之前的旧期一个都不提示')
  assert.equal(birthdayRateReviews(rates)[1].age, 21)
  assert.equal(birthdayRateReviews([rate('x', undefined as unknown as string, { birthdayReviewHint: true, birthdayReviewDate: '2026-11-01', birthdayReviewAge: 16 })])[0].userName, 'x', '没有姓名时退回账号 ID')
}

// 公假 / 带薪假「现金金额」手填：提交判定（失焦 / 回车时该不该发请求、发什么）
{
  const system = { amount: 148.5, isManual: false }
  assert.deepEqual(resolveCashOverrideCommit(system, 200), { kind: 'save', amount: 200 }, '改了值：保存')
  assert.deepEqual(resolveCashOverrideCommit(system, 148.5), { kind: 'none' }, '值没变：不发请求，免得失焦就产生一条手填')
  assert.deepEqual(resolveCashOverrideCommit(system, null), { kind: 'none' }, '没有手填时清空：什么都不做')
  assert.deepEqual(resolveCashOverrideCommit(system, undefined), { kind: 'none' })
  assert.deepEqual(resolveCashOverrideCommit(system, 0), { kind: 'save', amount: 0 }, '填 0 是明确填写：这一档不付现金')
  const manual = { amount: 200, isManual: true }
  assert.deepEqual(resolveCashOverrideCommit(manual, null), { kind: 'clear' }, '已手填时清空：恢复系统值')
  assert.deepEqual(resolveCashOverrideCommit(manual, 200), { kind: 'none' })
  assert.deepEqual(resolveCashOverrideCommit(manual, 148.5), { kind: 'save', amount: 148.5 }, '手填成与系统值相同也算明确手填')
  assert.deepEqual(resolveCashOverrideCommit(system, 10.005), { kind: 'save', amount: 10.01 }, '舍到 2 位小数')
  assert.deepEqual(resolveCashOverrideCommit(system, -5), { kind: 'save', amount: 0 }, '负数按 0')
  assert.deepEqual(resolveCashOverrideCommit(system, 999999), { kind: 'save', amount: CASH_OVERRIDE_MAX }, '超过上限按上限')
}

// 手填后工资重算：Liam（混合）公假 5.5 h 全在现金，系统值 5.5 × 27 = 148.50；手填 200 取代它（不是叠加）
{
  const query = { storeCode: 'CHERMSIDE', periodStart: '2026-10-05', weeks: 2 } as const
  const find = (rows: typeof pay.rows, userGuid: string) => rows.find((row) => row.userGuid === userGuid)!
  const base = find((await buildSamplePay(query)).rows, 'mock-liam')
  assert.equal(base.cash.publicHoliday.systemAmount, 148.5)
  assert.equal(base.cash.publicHoliday.editable, true)
  assert.equal(base.cash.publicHoliday.isManual, false)
  assert.equal(base.cash.leave.editable, false, 'Liam 没有带薪假现金工时')

  setSampleCashOverride('mock-liam', 'PublicHoliday', 200)
  const manualPay = await buildSamplePay(query)
  const manual = find(manualPay.rows, 'mock-liam')
  assert.equal(manual.cash.publicHoliday.isManual, true)
  assert.equal(manual.cash.publicHoliday.amount, 200)
  assert.equal(manual.cash.publicHoliday.systemAmount, 148.5)
  assert.equal(manual.cash.amount, Math.round((base.cash.amount - 148.5 + 200) * 100) / 100, '现金金额：手填取代系统值')
  assert.equal(manual.grossPay, Math.round((base.grossPay + 51.5) * 100) / 100, '应发随之多 51.50')
  assert.equal(manual.transfer.amount, base.transfer.amount, '转账不变')
  assert.deepEqual(summarizeCashOverrides(manualPay.rows), { count: 1, delta: 51.5 })
  assert.deepEqual(summarizeCashOverrides([]), { count: 0, delta: 0 })

  // 少付也算：手填 100 → 比系统值少 48.50
  setSampleCashOverride('mock-liam', 'PublicHoliday', 100)
  assert.deepEqual(summarizeCashOverrides((await buildSamplePay(query)).rows), { count: 1, delta: -48.5 })

  // Jack 的带薪假 15.6 h 全在转账里：没有现金部分，手填不生效（库里的值仍带出，界面据此提示）
  setSampleCashOverride('mock-jack', 'Leave', 100)
  const jack = find((await buildSamplePay(query)).rows, 'mock-jack')
  assert.equal(jack.cash.leave.editable, false)
  assert.equal(jack.cash.leave.isManual, false)
  assert.equal(jack.cash.leave.manualAmount, 100)
  assert.equal(jack.cash.amount, 167.58, '没生效，现金金额照旧')

  // 导出：金额两列 + 末尾「手填说明」（只有手填的格子写明系统值）
  const exportPay = await buildSamplePay(query)
  const labels = (key: string, values?: Record<string, unknown>) => (values ? `${key}|${JSON.stringify(values)}` : key)
  const [header, ...exportRows] = buildPayrollExportRows({
    tab: 'pay', storeName: 'Chermside', periodStart: query.periodStart, weeks: 2, hours: null, pay: exportPay, rates: [], text: labels,
  })
  assert.ok(header.includes('attendancePayroll.export.cashPublicHoliday'))
  assert.ok(header.includes('attendancePayroll.export.cashLeave'))
  assert.equal(header[header.length - 1], 'attendancePayroll.export.cashManualNote', '手填说明放在最后一列，不挪动原有列')
  const liamIndex = exportPay.rows.findIndex((row) => row.userGuid === 'mock-liam')
  const liamRow = exportRows[liamIndex]
  assert.equal(liamRow[header.indexOf('attendancePayroll.export.cashPublicHoliday')], 100)
  assert.match(String(liamRow[liamRow.length - 1]), /\$148\.50/, '手填说明里写着系统值 $148.50')
  const jackRow = exportRows[exportPay.rows.findIndex((row) => row.userGuid === 'mock-jack')]
  assert.equal(jackRow[jackRow.length - 1], '', '没有生效的手填不写说明')

  // 清除后恢复系统值
  setSampleCashOverride('mock-liam', 'PublicHoliday', null)
  setSampleCashOverride('mock-jack', 'Leave', null)
  const restored = await buildSamplePay(query)
  assert.deepEqual(find(restored.rows, 'mock-liam').cash.publicHoliday.isManual, false)
  assert.equal(find(restored.rows, 'mock-liam').grossPay, base.grossPay)
  assert.deepEqual(summarizeCashOverrides(restored.rows), { count: 0, delta: 0 })
}

console.log('payrollLogic.test.ts: ok')
