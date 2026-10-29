import dayjs from 'dayjs'
import type {
  AttendanceEmploymentType,
  AttendanceHoursEmployeeDto,
  AttendanceHoursTotalsDto,
  AttendancePayBuckets,
  AttendancePayMode,
  AttendancePayRateDto,
} from '../../../types/attendancePayroll'

// 工资口径（规格第 0.2、3 节）：后端为准，前端只用于时薪抽屉的实时试算与 mock。
// - 转账 + 现金：转账容量 = 固定转账工时/周 × 周数，按 周日 → 周六 → 带薪假 → 公假 → 平日 依次装入；
// - 各档用该档转账时薪，带薪假用平日转账时薪；剩余工时合计 × 现金时薪（现金只有一个价）；
// - 先把各档工时舍到 2 位小数再乘时薪，金额舍到分，逐档相加；再 ± 每期现金补贴。
export const TRANSFER_FILL_ORDER = ['sunday', 'saturday', 'leave', 'publicHoliday', 'weekday'] as const
export type PayBucketKey = (typeof TRANSFER_FILL_ORDER)[number]

/**
 * 工时页员工姓名下的类型标签颜色：全职蓝、兼职绿、临时紫、店长洋红，一眼分得清。
 * 避开已有语义色：未成年（金）、多店（青）、公共假期（橙）、异常（红）。
 */
export const EMPLOYMENT_TYPE_TAG_COLORS: Record<AttendanceEmploymentType, string> = {
  FullTime: 'blue',
  PartTime: 'green',
  Casual: 'purple',
}
export const MANAGER_TAG_COLOR = 'magenta'

/** 舍到 2 位小数（先放大成整数分再四舍五入，避免 0.005 这类浮点误差）。 */
export function round2(value: number): number {
  return Math.round(Number((value * 100).toFixed(6))) / 100
}

/** 分钟 → 十进制小时，最多 2 位（7:59 → 7.98）。 */
export function minutesToHours(minutes: number): number {
  return round2(minutes / 60)
}

/** 工时 × 时薪 → 金额（分）：两者都按 2 位小数取整后用整数相乘，再四舍五入到分。 */
export function multiplyToCents(hours: number, rate: number): number {
  const hundredthHours = Math.round(hours * 100)
  const rateCents = Math.round(rate * 100)
  return Math.round((hundredthHours * rateCents) / 100)
}

export function emptyBuckets(): AttendancePayBuckets {
  return { sunday: 0, saturday: 0, leave: 0, publicHoliday: 0, weekday: 0 }
}

export function sumBuckets(buckets: AttendancePayBuckets): number {
  return round2(TRANSFER_FILL_ORDER.reduce((total, key) => total + buckets[key], 0))
}

export interface PayRates {
  transferWeekdayRate?: number | null
  transferSaturdayRate?: number | null
  transferSundayRate?: number | null
  transferPublicHolidayRate?: number | null
  cashRate?: number | null
}

export interface PayCalculationInput {
  hours: AttendancePayBuckets
  payMode: AttendancePayMode
  fixedTransferHoursPerWeek?: number
  weeks: number
  rates: PayRates
  allowance?: number
}

export interface PayCalculationResult {
  transfer: { buckets: AttendancePayBuckets; hours: number; amount: number }
  cash: { buckets: AttendancePayBuckets; hours: number; amount: number }
  allowance: number
  grossPay: number
  /** 有工时却缺时薪的档（transfer.weekday / cash 等），界面提示补填。 */
  missingRates: string[]
}

function transferRateFor(key: PayBucketKey, rates: PayRates): number | null | undefined {
  switch (key) {
    case 'sunday':
      return rates.transferSundayRate
    case 'saturday':
      return rates.transferSaturdayRate
    case 'publicHoliday':
      return rates.transferPublicHolidayRate
    // 带薪假按平日转账时薪
    case 'leave':
    case 'weekday':
      return rates.transferWeekdayRate
  }
}

/** 按发放方式把应付工时拆进转账桶 / 现金桶并算钱。 */
export function calculatePay(input: PayCalculationInput): PayCalculationResult {
  const { hours, payMode, rates } = input
  const capacity = payMode === 'Transfer'
    ? Number.POSITIVE_INFINITY
    : payMode === 'Cash'
      ? 0
      : Math.max(0, (input.fixedTransferHoursPerWeek ?? 0) * input.weeks)
  const transferBuckets = emptyBuckets()
  const cashBuckets = emptyBuckets()
  let left = capacity
  for (const key of TRANSFER_FILL_ORDER) {
    const bucketHours = round2(hours[key])
    const take = round2(Math.min(bucketHours, Math.max(0, left)))
    transferBuckets[key] = take
    cashBuckets[key] = round2(bucketHours - take)
    left -= take
  }

  const missingRates: string[] = []
  let transferCents = 0
  for (const key of TRANSFER_FILL_ORDER) {
    const bucketHours = transferBuckets[key]
    if (bucketHours <= 0) continue
    const rate = transferRateFor(key, rates)
    if (rate === undefined || rate === null) {
      missingRates.push(`transfer.${key}`)
      continue
    }
    transferCents += multiplyToCents(bucketHours, rate)
  }

  const cashHours = sumBuckets(cashBuckets)
  let cashCents = 0
  if (cashHours > 0) {
    if (rates.cashRate === undefined || rates.cashRate === null) {
      missingRates.push('cash')
    } else {
      // 现金只有一个价：剩余工时合计 × 现金时薪
      cashCents = multiplyToCents(cashHours, rates.cashRate)
    }
  }

  const allowanceCents = Math.round((input.allowance ?? 0) * 100)
  return {
    transfer: { buckets: transferBuckets, hours: sumBuckets(transferBuckets), amount: transferCents / 100 },
    cash: { buckets: cashBuckets, hours: cashHours, amount: cashCents / 100 },
    allowance: allowanceCents / 100,
    grossPay: (transferCents + cashCents + allowanceCents) / 100,
    missingRates,
  }
}

/** 时薪记录里低于自填最低时薪的档（任一档低于即标红）。 */
export function findRatesBelowMinimum(rate: PayRates & Pick<AttendancePayRateDto, 'minimumRate' | 'payMode'>): (keyof PayRates)[] {
  const minimum = rate.minimumRate
  if (minimum === undefined || minimum === null || minimum <= 0) return []
  const keys: (keyof PayRates)[] = rate.payMode === 'Cash'
    ? ['cashRate']
    : rate.payMode === 'Transfer'
      ? ['transferWeekdayRate', 'transferSaturdayRate', 'transferSundayRate', 'transferPublicHolidayRate']
      : ['transferWeekdayRate', 'transferSaturdayRate', 'transferSundayRate', 'transferPublicHolidayRate', 'cashRate']
  return keys.filter((key) => {
    const value = rate[key]
    return typeof value === 'number' && value < minimum
  })
}

/**
 * 公假转账时薪永远等于平日转账时薪（公司规定，后端保存时也会强制）：
 * 保存前 / 平日价变动时，把公假价同步成平日价；平日价为空则公假价也为空。
 */
export function syncPublicHolidayRate<T extends PayRates>(rates: T): T {
  return { ...rates, transferPublicHolidayRate: rates.transferWeekdayRate ?? undefined }
}

/** 当前生效记录里公假价与平日价不一致（以后端 publicHolidayRateMismatch 为准；全现金恒为 false）。 */
export function hasPublicHolidayRateMismatch(rate: Pick<AttendancePayRateDto, 'publicHolidayRateMismatch'>): boolean {
  return rate.publicHolidayRateMismatch === true
}

/** 列表里公假价与平日价不一致的员工数（只反映当前已加载的分店 / 筛选结果）。 */
export function countPublicHolidayRateMismatch(rates: readonly Pick<AttendancePayRateDto, 'publicHolidayRateMismatch'>[]): number {
  return rates.filter(hasPublicHolidayRateMismatch).length
}

// ===== 周期 =====

/**
 * 统计周期起点：1 周 = 所在 ISO 周的周一；
 * 2 周 = 以「双周起始日」为锚，每 14 天一期，保证与发薪周期对齐（锚点之前的日期同样按 14 天倒推）。
 */
export function resolvePeriodStart(date: string, weeks: 1 | 2, fortnightAnchorDate?: string): string {
  const target = dayjs(date)
  const monday = target.subtract((target.day() + 6) % 7, 'day').startOf('day')
  if (weeks === 1 || !fortnightAnchorDate) return monday.format('YYYY-MM-DD')
  const anchor = dayjs(fortnightAnchorDate).startOf('day')
  const diffDays = target.startOf('day').diff(anchor, 'day')
  const offset = Math.floor(diffDays / 14) * 14
  return anchor.add(offset, 'day').format('YYYY-MM-DD')
}

export function shiftPeriod(periodStart: string, weeks: 1 | 2, direction: -1 | 1): string {
  return dayjs(periodStart).add(direction * weeks * 7, 'day').format('YYYY-MM-DD')
}

export function periodEnd(periodStart: string, weeks: 1 | 2): string {
  return dayjs(periodStart).add(weeks * 7 - 1, 'day').format('YYYY-MM-DD')
}

/** ISO 周号（第 41–42 周）。 */
export function isoWeekNumber(date: string): number {
  const target = dayjs(date)
  const thursday = target.add(3 - ((target.day() + 6) % 7), 'day')
  const firstThursday = dayjs(`${thursday.year()}-01-04`)
  const firstMonday = firstThursday.subtract((firstThursday.day() + 6) % 7, 'day')
  return Math.floor(thursday.diff(firstMonday, 'day') / 7) + 1
}

// ===== 展示 =====

/** 十进制小时：去掉多余的 0（8 → "8"，7.50 → "7.5"）。 */
export function formatHours(value: number | null | undefined, empty = '—'): string {
  if (value === null || value === undefined || value === 0) return empty
  return String(round2(value))
}

/** 金额：$1,964.50；负数用减号「−$20.00」。 */
export function formatMoney(value: number | null | undefined, empty = '—'): string {
  if (value === null || value === undefined) return empty
  const absText = Math.abs(value).toFixed(2).replace(/\B(?=(\d{3})+(?!\d))/g, ',')
  return `${value < 0 ? '−' : ''}$${absText}`
}

/** 补贴带符号：+$50.00 / −$20.00。 */
export function formatSignedMoney(value: number | null | undefined, empty = '—'): string {
  if (!value) return empty
  return `${value > 0 ? '+' : ''}${formatMoney(value)}`
}

/** 后端没给合计时，按各天相加兜底（各天已是 2 位小数）。 */
export function summarizeEmployeeHours(employee: AttendanceHoursEmployeeDto): AttendanceHoursTotalsDto {
  if (employee.totals) return employee.totals
  const totals: AttendanceHoursTotalsDto = {
    weekday: 0, saturday: 0, sunday: 0, publicHoliday: 0,
    leave: 0, total: 0, sick: 0, annual: 0, cashOut: 0, publicHolidayPay: 0,
  }
  for (const day of employee.days) {
    totals.weekday += day.byTier.weekday
    totals.saturday += day.byTier.saturday
    totals.sunday += day.byTier.sunday
    totals.publicHoliday += day.byTier.publicHoliday
    if (day.leave?.type === 'PaidSick') totals.sick += day.leave.hours
    if (day.leave?.type === 'PaidAnnual') totals.annual += day.leave.hours
    if (day.leave?.type === 'AnnualCashOut') totals.cashOut += day.leave.hours
    if (day.publicHolidayPay?.state === 'Confirmed') totals.publicHolidayPay += day.publicHolidayPay.hours ?? 0
  }
  const result = Object.fromEntries(
    Object.entries(totals).map(([key, value]) => [key, round2(value)]),
  ) as unknown as AttendanceHoursTotalsDto
  result.leave = round2(result.sick + result.annual + result.cashOut + result.publicHolidayPay)
  result.total = round2(result.weekday + result.saturday + result.sunday + result.publicHoliday + result.leave)
  return result
}
