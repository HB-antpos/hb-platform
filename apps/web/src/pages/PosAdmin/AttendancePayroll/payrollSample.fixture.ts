import dayjs from 'dayjs'
import {
  calculatePay,
  minutesToHours,
  round2,
} from './payrollLogic'
import type {
  AttendanceAnnualLeaveType,
  AttendanceEmploymentType,
  AttendanceHoursAlertDto,
  AttendanceHoursDayDto,
  AttendanceHoursEmployeeDto,
  AttendanceHoursResultDto,
  AttendanceHoursTotalsDto,
  AttendancePayRateDto,
  AttendancePayResultDto,
  AttendancePayRowDto,
  AttendancePeriodQuery,
  AttendanceTierHours,
} from '../../../types/attendancePayroll'

// 测试夹具（不进任何页面代码）：样例数据移植自设计源 docs/design/attendance-payroll-20261021/src/period-data.js，
// 用于锁定前端展示与规格第 3 节验收样例一致；也供免登录预览造数。
// 两周 2026-10-05（周一，QLD 公共假期 King's Birthday）~ 10-18，分店 Chermside，店长 Sarah Kim。
// 计算口径同规格第 3 节，结果须与验收样例一致（Jack $1,964.50、Emma $1,702.96、Liam $1,612.71、Mia $1,119.89）。

const SAMPLE_START = '2026-10-05'
const SAMPLE_DAYS = 14
const HOLIDAY_INDEX = 0
const HOLIDAY_NAME = "King's Birthday"
const OTHER_STORE = { storeCode: 'SB', storeName: 'Sunnybank' }

interface SampleDay {
  sch?: string
  seg?: string[]
  off?: boolean
  unavail?: boolean
  leave?: { type: 'PaidAnnual' | 'PaidSick'; hours: number; cert?: boolean }
  phPaid?: { hours: number; state: 'Confirmed' | 'Pending'; count: number }
  other?: number
  pend?: string
  ok?: string
  /** 年假发放（照常上班）当天另计的工时。 */
  cashOut?: number
  auto?: string
  ot?: number
  addBack?: number
}

interface SampleEmployee {
  userGuid: string
  userName: string
  employmentType: AttendanceEmploymentType
  isManager?: boolean
  ageYears?: number
  birthday?: string
  birthdayIndex?: number
  segmentLimit: number
  days: (SampleDay | null)[]
}

const SAMPLE: SampleEmployee[] = [
  {
    userGuid: 'mock-sarah', userName: 'Sarah Kim', employmentType: 'FullTime', isManager: true, segmentLimit: 6,
    days: [
      { sch: '08:30-17:00', seg: ['08:29-12:30', '13:00-17:01'] },
      { sch: '08:30-17:00', seg: ['08:30-12:30', '13:00-17:00'] },
      null,
      { sch: '08:30-12:00', seg: ['08:28-12:00'], other: 4 },
      { sch: '08:30-17:00', seg: ['08:31-12:30', '13:02-17:00'] },
      null,
      { sch: '08:30-17:00', seg: ['08:30-12:30', '13:00-17:03'] },
      { sch: '08:30-17:00', seg: ['08:28-12:30', '13:01-17:05'] },
      { sch: '08:30-17:00', seg: ['08:31-12:00', '12:30-14:40', '14:55-17:02'] },
      null,
      { sch: '08:30-17:00', seg: ['08:25-13:00', '13:30-17:00'] },
      { sch: '08:30-17:00', seg: ['08:30-17:01'] },
      null,
      { sch: '08:30-17:00', seg: ['09:07-12:30', '13:00-17:00'], pend: 'PunchAdjustment' },
    ],
  },
  {
    userGuid: 'mock-emma', userName: 'Emma Walsh', employmentType: 'PartTime', segmentLimit: 4,
    days: [
      { off: true },
      { leave: { type: 'PaidAnnual', hours: 8 } },
      { leave: { type: 'PaidAnnual', hours: 8 } },
      { leave: { type: 'PaidAnnual', hours: 8 } },
      { sch: '09:00-13:00', seg: ['08:58-13:00'] },
      { sch: '09:00-17:00', seg: ['09:00-12:30', '13:00-17:01'] },
      null,
      { sch: '09:00-15:00', seg: ['09:00-15:02'], ok: 'PunchAdjustment' },
      { unavail: true },
      { sch: '12:00-18:00', seg: ['11:58-15:00', '15:30-18:03'] },
      null,
      { sch: '09:00-13:00', seg: ['08:59-13:00'] },
      { sch: '09:00-17:00', seg: ['08:58-13:02', '13:33-17:47'], pend: 'Overtime' },
      null,
    ],
  },
  {
    userGuid: 'mock-jack', userName: 'Jack Nguyen', employmentType: 'FullTime', segmentLimit: 4,
    days: [
      { off: true, phPaid: { hours: 7.6, state: 'Confirmed', count: 8 } },
      { leave: { type: 'PaidSick', hours: 8, cert: true } },
      { sch: '09:00-17:30', seg: ['08:56-13:00', '13:31-17:30'] },
      null,
      { sch: '13:00-21:00', seg: ['12:59-17:00', '17:30-21:01'] },
      { sch: '09:00-17:30', seg: ['09:00-13:00', '13:30-17:32'] },
      null,
      null,
      { sch: '09:00-17:30', seg: ['08:55-13:00', '13:30-17:31'] },
      null,
      { sch: '09:00-17:30', seg: ['09:12-13:00', '13:30-17:30'], pend: 'Punch' },
      { sch: '13:00-21:00', seg: ['12:58-21:02'] },
      { unavail: true },
      { sch: '09:00-17:30', seg: ['08:57-12:30'], auto: '13:02-00:00', pend: 'PunchAdjustment' },
    ],
  },
  {
    userGuid: 'mock-olivia', userName: 'Olivia Chen', employmentType: 'PartTime', segmentLimit: 4,
    days: [
      { off: true, phPaid: { hours: 6, state: 'Pending', count: 7 } },
      { sch: '10:00-16:00', seg: ['10:00-16:00'] },
      null,
      { sch: '09:00-15:00', seg: ['08:59-15:00'] },
      null,
      { sch: '10:00-16:00', seg: ['10:00-16:02'] },
      null,
      { sch: '10:00-16:00', seg: ['09:58-16:00'], cashOut: 6 },
      null,
      { leave: { type: 'PaidSick', hours: 6, cert: true } },
      null,
      { sch: '09:00-18:30', seg: ['08:59-13:30', '14:00-18:31'], pend: 'MealBreak', cashOut: 6 },
      { sch: '10:00-16:00', seg: ['10:00-16:01'], cashOut: 6 },
      null,
    ],
  },
  {
    userGuid: 'mock-mia', userName: 'Mia Robinson', employmentType: 'Casual', ageYears: 17, birthday: '2009-10-16', birthdayIndex: 11, segmentLimit: 4,
    days: [
      { off: true },
      { sch: '09:00-15:00', seg: ['09:00-15:01'] },
      null,
      { sch: '15:30-18:00', seg: ['15:30-18:00'] },
      null,
      { sch: '09:00-17:00', seg: ['08:59-12:30', '13:00-17:00'] },
      null,
      null,
      { sch: '09:00-18:00', seg: ['09:00-18:02'], addBack: 30, ok: 'MealBreak' },
      { sch: '09:00-15:00', seg: ['09:01-15:00'] },
      null,
      { sch: '12:00-18:00', seg: ['11:59-18:00'] },
      { sch: '09:00-17:00', seg: ['08:59-12:30', '13:00-17:02'] },
      { sch: '10:00-16:00', seg: ['10:00-16:00'] },
    ],
  },
  {
    userGuid: 'mock-liam', userName: 'Liam Patel', employmentType: 'Casual', segmentLimit: 4,
    days: [
      { sch: '12:00-18:00', seg: ['12:00-18:01'] },
      null,
      { off: true, other: 5.5 },
      { sch: '16:00-21:30', seg: ['16:00-21:30'] },
      null,
      { sch: '12:00-20:00', seg: ['11:59-16:00', '16:30-20:00'] },
      { sch: '12:00-18:00', seg: ['12:00-18:00'] },
      { sch: '16:00-21:30', seg: ['16:00-21:30'] },
      null,
      { sch: '16:00-21:30', seg: ['15:58-22:02'], ot: 30, ok: 'Overtime' },
      { sch: '16:00-21:30', seg: ['16:01-21:30'] },
      null,
      { sch: '12:00-20:00', seg: ['11:58-16:00', '16:30-20:00'] },
      { sch: '12:00-18:00', seg: ['12:00-18:00'] },
    ],
  },
]

const toMinutes = (text: string) => {
  const [hour, minute] = text.split(':').map(Number)
  return hour * 60 + minute
}

/** 当天计薪分钟（规格第 3 节）：排班窗口内工时 + 已批加班 − 扣餐 + 已批加回；自动下班段不计。 */
function paidMinutes(day: SampleDay): number {
  if (!day.sch || !day.seg) return 0
  const [scheduleStart, scheduleEnd] = day.sch.split('-').map(toMinutes)
  const segments = day.seg.map((segment) => segment.split('-').map(toMinutes))
  const worked = segments.reduce((sum, [start, end]) => sum + (end - start), 0)
  const inSchedule = segments.reduce((sum, [start, end]) => sum + Math.max(0, Math.min(end, scheduleEnd) - Math.max(start, scheduleStart)), 0)
  const duration = scheduleEnd - scheduleStart
  const effectiveMeals = duration > 540 ? 2 : duration > 270 ? 1 : 0
  const required = Math.min(effectiveMeals, Math.floor(worked / 240))
  let gaps = 0
  for (let index = 1; index < segments.length; index++) {
    if (segments[index][0] - segments[index - 1][1] >= 30) gaps++
  }
  const deduction = Math.max(0, required - gaps) * 30
  return inSchedule + (day.ot ?? 0) - deduction + (day.addBack ?? 0)
}

function tierOf(index: number): keyof AttendanceTierHours {
  if (index === HOLIDAY_INDEX) return 'publicHoliday'
  if (index % 7 === 5) return 'saturday'
  if (index % 7 === 6) return 'sunday'
  return 'weekday'
}

function sampleIndexOf(date: string): number {
  return dayjs(date).diff(dayjs(SAMPLE_START), 'day')
}

function periodDates(periodStart: string, weeks: number): string[] {
  return Array.from({ length: weeks * 7 }, (_, offset) => dayjs(periodStart).add(offset, 'day').format('YYYY-MM-DD'))
}

function zeroTiers(): AttendanceTierHours {
  return { weekday: 0, saturday: 0, sunday: 0, publicHoliday: 0 }
}

// ===== 可变状态（mock 内存）=====

const leaveApprovals = new Map<string, number>([
  // Emma 年假申请 24 h，财务按累计带薪小时只核定 20 h（设计样例）
  ['mock-emma|PaidAnnual', 20],
])
const notes = new Map<string, string>([
  ['mock-sarah', '补贴：开关店 $50'],
  ['mock-emma', '年假申请 24 h，累计只够 20 h'],
  ['mock-jack', '扣回 10-09 借支 $20'],
  ['mock-olivia', '年假发放 10-12~10-18，照常上班'],
  ['mock-mia', '10-16 满 17 岁，核对新时薪'],
])


const rateHistory: AttendancePayRateDto[] = [
  { rateGuid: 'r-sarah-1', userGuid: 'mock-sarah', payMode: 'Mixed', fixedTransferHoursPerWeek: 38, transferWeekdayRate: 32, transferSaturdayRate: 40, transferSundayRate: 48, transferPublicHolidayRate: 72, cashRate: 32, cashAllowancePerPeriod: 50, minimumRate: 30, effectiveFrom: '2026-07-01', createdBy: 'Admin' },
  { rateGuid: 'r-emma-1', userGuid: 'mock-emma', payMode: 'Transfer', transferWeekdayRate: 29.5, transferSaturdayRate: 36.88, transferSundayRate: 44.25, transferPublicHolidayRate: 66.38, cashAllowancePerPeriod: 0, minimumRate: 28, effectiveFrom: '2026-07-01', createdBy: 'Admin' },
  { rateGuid: 'r-jack-1', userGuid: 'mock-jack', payMode: 'Mixed', fixedTransferHoursPerWeek: 30, transferWeekdayRate: 27.8, transferSaturdayRate: 34.75, transferSundayRate: 41.7, transferPublicHolidayRate: 62.55, cashRate: 27.8, cashAllowancePerPeriod: 0, minimumRate: 27, effectiveFrom: '2026-07-01', createdBy: 'Admin' },
  { rateGuid: 'r-jack-2', userGuid: 'mock-jack', payMode: 'Mixed', fixedTransferHoursPerWeek: 30, transferWeekdayRate: 28.5, transferSaturdayRate: 35.63, transferSundayRate: 42.75, transferPublicHolidayRate: 64.13, cashRate: 28.5, cashAllowancePerPeriod: -20, minimumRate: 28, effectiveFrom: '2026-10-05', remark: '扣回 10-09 借支 $20', createdBy: 'Sarah Kim' },
  { rateGuid: 'r-olivia-1', userGuid: 'mock-olivia', payMode: 'Transfer', transferWeekdayRate: 29, transferSaturdayRate: 36.25, transferSundayRate: 43.5, transferPublicHolidayRate: 65.25, cashAllowancePerPeriod: 0, minimumRate: 28, effectiveFrom: '2026-07-01', createdBy: 'Admin' },
  { rateGuid: 'r-mia-1', userGuid: 'mock-mia', payMode: 'Cash', cashRate: 23.1, cashAllowancePerPeriod: 0, effectiveFrom: '2026-01-10', createdBy: 'Admin' },
  { rateGuid: 'r-liam-1', userGuid: 'mock-liam', payMode: 'Mixed', fixedTransferHoursPerWeek: 10, transferWeekdayRate: 27, transferSaturdayRate: 33.75, transferSundayRate: 40.5, transferPublicHolidayRate: 60.75, cashRate: 27, cashAllowancePerPeriod: 0, minimumRate: 26.5, effectiveFrom: '2026-07-01', createdBy: 'Admin' },
]

/** 某天有效的时薪：生效日期不晚于该天的最新一条。 */
function rateOn(userGuid: string, date: string): AttendancePayRateDto | undefined {
  return rateHistory
    .filter((rate) => rate.userGuid === userGuid && (rate.effectiveFrom ?? '') <= date)
    .sort((left, right) => (right.effectiveFrom ?? '').localeCompare(left.effectiveFrom ?? ''))[0]
}

function delay<T>(value: T): Promise<T> {
  return new Promise((resolve) => setTimeout(() => resolve(value), 120))
}

// ===== 工时统计 =====

function buildDay(employee: SampleEmployee, date: string, mergeStores: boolean): AttendanceHoursDayDto {
  const index = sampleIndexOf(date)
  const sample = index >= 0 && index < SAMPLE_DAYS ? employee.days[index] : null
  const tier = tierOf(index)
  const byTier = zeroTiers()
  let paid: number | null = null
  if (sample?.sch) {
    paid = minutesToHours(paidMinutes(sample))
    byTier[tier] = paid
  }
  // 合并其他分店是查看参数：只把另一店当天工时加进本表，不改数据
  if (mergeStores && sample?.other) {
    paid = round2((paid ?? 0) + sample.other)
    byTier[tier] = paid
  }
  const pending = sample?.pend ? [sample.pend] : []
  if (sample?.phPaid?.state === 'Pending') pending.push('PublicHolidayPay')
  return {
    date,
    paidHours: paid,
    byTier,
    hasSchedule: Boolean(sample?.sch),
    isUnavailable: sample?.unavail,
    leave: sample?.leave
      ? { type: sample.leave.type, hours: sample.leave.hours, hasCertificate: sample.leave.cert }
      : sample?.cashOut
        ? { type: 'AnnualCashOut', hours: sample.cashOut }
        : undefined,
    publicHolidayPay: sample?.phPaid
      ? {
          state: sample.phPaid.state,
          hours: sample.phPaid.hours,
          count: sample.phPaid.count,
          approvalGuid: sample.phPaid.state === 'Pending' ? 'mock-ap-ph-olivia' : undefined,
        }
      : undefined,
    pending,
    approvedMarks: sample?.ok ? [sample.ok] : [],
    otherStores: sample?.other && !mergeStores ? [{ ...OTHER_STORE, hours: sample.other }] : [],
    isBirthday: employee.birthdayIndex === index,
  }
}

function buildEmployee(employee: SampleEmployee, dates: string[], mergeStores: boolean): AttendanceHoursEmployeeDto {
  const days = dates.map((date) => buildDay(employee, date, mergeStores))
  // 后端口径：各档按分钟合计后再舍到 2 位
  const minutes = zeroTiers()
  const totals: AttendanceHoursTotalsDto = { ...zeroTiers(), leave: 0, total: 0, sick: 0, annual: 0, cashOut: 0, publicHolidayPay: 0 }
  for (const date of dates) {
    const index = sampleIndexOf(date)
    const sample = index >= 0 && index < SAMPLE_DAYS ? employee.days[index] : null
    if (!sample) continue
    if (sample.sch) minutes[tierOf(index)] += paidMinutes(sample)
    if (mergeStores && sample.other) minutes[tierOf(index)] += sample.other * 60
    if (sample.leave?.type === 'PaidSick') totals.sick += sample.leave.hours
    if (sample.leave?.type === 'PaidAnnual') totals.annual += sample.leave.hours
    if (sample.cashOut) totals.cashOut += sample.cashOut
    if (sample.phPaid?.state === 'Confirmed') totals.publicHolidayPay += sample.phPaid.hours
  }
  totals.weekday = minutesToHours(minutes.weekday)
  totals.saturday = minutesToHours(minutes.saturday)
  totals.sunday = minutesToHours(minutes.sunday)
  totals.publicHoliday = minutesToHours(minutes.publicHoliday)
  totals.leave = round2(totals.sick + totals.annual + totals.cashOut + totals.publicHolidayPay)
  totals.total = round2(totals.weekday + totals.saturday + totals.sunday + totals.publicHoliday + totals.leave)
  return {
    userGuid: employee.userGuid,
    userName: employee.userName,
    employmentType: employee.employmentType,
    isManager: employee.isManager,
    ageYears: employee.ageYears,
    birthday: employee.birthday,
    segmentLimit: employee.segmentLimit,
    hasOtherStores: employee.days.some((day) => day?.other),
    days,
    totals,
  }
}

function buildAlerts(dates: string[], mergeStores: boolean): AttendanceHoursAlertDto[] {
  const inPeriod = (index: number) => dates.includes(dayjs(SAMPLE_START).add(index, 'day').format('YYYY-MM-DD'))
  const alerts: AttendanceHoursAlertDto[] = []
  const otherStoreItems = SAMPLE.flatMap((employee) => employee.days.flatMap((day, index) => (
    day?.other && inPeriod(index)
      ? [{ userGuid: employee.userGuid, userName: employee.userName, date: dayjs(SAMPLE_START).add(index, 'day').format('YYYY-MM-DD'), ...OTHER_STORE, hours: day.other }]
      : []
  )))
  if (otherStoreItems.length && !mergeStores) alerts.push({ type: 'MultiStore', otherStoreItems })
  for (const employee of SAMPLE) {
    employee.days.forEach((day, index) => {
      if (day?.phPaid?.state === 'Pending' && inPeriod(index)) {
        alerts.push({
          type: 'PublicHolidayPay',
          userGuid: employee.userGuid,
          userName: employee.userName,
          employmentType: employee.employmentType,
          date: dayjs(SAMPLE_START).add(index, 'day').format('YYYY-MM-DD'),
          holidayName: HOLIDAY_NAME,
          ordinaryCount: day.phPaid.count,
          lookbackWeeks: 10,
          minCount: 7,
          hours: day.phPaid.hours,
          approvalGuid: 'mock-ap-ph-olivia',
        })
      }
    })
    if (employee.birthdayIndex !== undefined && inPeriod(employee.birthdayIndex)) {
      alerts.push({
        type: 'Birthday',
        userGuid: employee.userGuid,
        userName: employee.userName,
        date: dayjs(SAMPLE_START).add(employee.birthdayIndex, 'day').format('YYYY-MM-DD'),
        ageYears: employee.ageYears,
        isMinor: (employee.ageYears ?? 18) < 18,
      })
    }
  }
  return alerts
}

/** 样例期：默认两周 10-05 ~ 10-18（与后端一样，缺省期起点 / 周数时给出样例所在期）。 */
type SampleQuery = AttendancePeriodQuery & { periodStart: string; weeks: 1 | 2 }

export function buildSampleHours(input: AttendancePeriodQuery): Promise<AttendanceHoursResultDto> {
  const query: SampleQuery = { ...input, periodStart: input.periodStart ?? SAMPLE_START, weeks: input.weeks ?? 2 }
  const dates = periodDates(query.periodStart, query.weeks)
  const employees = SAMPLE.map((employee) => buildEmployee(employee, dates, Boolean(query.mergeStores)))
  return delay({
    storeCode: query.storeCode,
    periodStart: query.periodStart,
    weeks: query.weeks,
    periodStatus: 'Open',
    pendingCount: employees.reduce((sum, employee) => sum + employee.days.reduce((count, day) => count + day.pending.length, 0), 0),
    days: dates.map((date) => {
      const index = sampleIndexOf(date)
      return {
        date,
        isoWeekday: ((dayjs(date).day() + 6) % 7) + 1,
        isPublicHoliday: index === HOLIDAY_INDEX,
        holidayName: index === HOLIDAY_INDEX ? HOLIDAY_NAME : undefined,
      }
    }),
    employees,
    alerts: buildAlerts(dates, Boolean(query.mergeStores)),
  })
}

// ===== 工资表 =====

function leaveApproval(userGuid: string, leaveType: AttendanceAnnualLeaveType, requested: number) {
  if (requested <= 0) return undefined
  const approved = leaveApprovals.get(`${userGuid}|${leaveType}`)
  return { leaveType, requestedHours: requested, approvedHours: approved ?? requested }
}

export async function buildSamplePay(input: AttendancePeriodQuery): Promise<AttendancePayResultDto> {
  const query: SampleQuery = { ...input, periodStart: input.periodStart ?? SAMPLE_START, weeks: input.weeks ?? 2 }
  const hours = await buildSampleHours(query)
  const rows: AttendancePayRowDto[] = hours.employees.map((employee) => {
    const totals = employee.totals!
    const rate = rateOn(employee.userGuid, query.periodStart)
    const annual = leaveApproval(employee.userGuid, 'PaidAnnual', totals.annual)
    const cashOut = leaveApproval(employee.userGuid, 'AnnualCashOut', totals.cashOut)
    // 工资里的年假工时 = 财务核定工时（无记录时 = 申请工时）
    const leave = round2(totals.sick + totals.publicHolidayPay + (annual?.approvedHours ?? 0) + (cashOut?.approvedHours ?? 0))
    const result = calculatePay({
      hours: { sunday: totals.sunday, saturday: totals.saturday, leave, publicHoliday: totals.publicHoliday, weekday: totals.weekday },
      payMode: rate?.payMode ?? 'Transfer',
      fixedTransferHoursPerWeek: rate?.fixedTransferHoursPerWeek,
      weeks: query.weeks,
      rates: rate ?? {},
      allowance: rate?.cashAllowancePerPeriod ?? 0,
    })
    return {
      userGuid: employee.userGuid,
      userName: employee.userName,
      employmentType: employee.employmentType,
      ageYears: employee.ageYears,
      payMode: rate?.payMode ?? 'Transfer',
      fixedTransferHoursPerWeek: rate?.fixedTransferHoursPerWeek,
      hours: {
        weekday: totals.weekday,
        saturday: totals.saturday,
        sunday: totals.sunday,
        publicHoliday: totals.publicHoliday,
        leave,
        total: round2(totals.weekday + totals.saturday + totals.sunday + totals.publicHoliday + leave),
      },
      leaveDetail: { sick: totals.sick, publicHolidayPay: totals.publicHolidayPay, annual, cashOut },
      transfer: result.transfer,
      cash: { ...result.cash, rate: rate?.cashRate },
      allowance: result.allowance,
      grossPay: result.grossPay,
      note: notes.get(employee.userGuid),
      warnings: result.missingRates.length ? ['RATE_MISSING'] : undefined,
    }
  })
  return {
    storeCode: query.storeCode,
    periodStart: query.periodStart,
    weeks: query.weeks,
    periodStatus: hours.periodStatus,
    pendingCount: hours.pendingCount,
    rows,
  }
}

// ===== 财务核定（测试用：改核定工时后重算）=====

export function setSampleLeaveApproval(userGuid: string, leaveType: 'PaidAnnual' | 'AnnualCashOut', approvedHours: number) {
  leaveApprovals.set(`${userGuid}|${leaveType}`, approvedHours)
}
