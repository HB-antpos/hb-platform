import type { MyobPayslipCompanyStatus, MyobPayslipFlatLine, MyobPayslipRow } from '../../../types/myobPayslip'

// 工资单页的纯逻辑：日期预设（澳洲财年）、金额 / 工时 / 周期格式化、错误归类、分店下拉。
// 所有函数都是纯函数（today 由调用方传入），便于单测。

export type PayslipPresetKey = 'last2Weeks' | 'last3Months' | 'thisFinancialYear' | 'lastFinancialYear'

export const PAYSLIP_PRESETS: readonly PayslipPresetKey[] = ['last2Weeks', 'last3Months', 'thisFinancialYear', 'lastFinancialYear']

/** 后端单次区间上限：含首尾 731 天（24 个月）。前端用同一个数提前拦截，避免白发一次请求。 */
export const PAYSLIP_MAX_RANGE_DAYS = 731

/** 默认预设：与后端默认（最近 90 天）一致。 */
export const PAYSLIP_DEFAULT_PRESET: PayslipPresetKey = 'last3Months'

/** 悉尼「今天」（yyyy-MM-dd）：门店与 MYOB 工资单都按澳洲日期算，不用浏览器本地时区。 */
export function sydneyToday(now: Date = new Date()): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: 'Australia/Sydney', year: 'numeric', month: '2-digit', day: '2-digit' }).format(now)
}

function parseDate(value: string): Date {
  const [year, month, day] = value.split('-').map(Number)
  return new Date(Date.UTC(year, month - 1, day))
}

function formatDate(date: Date): string {
  return date.toISOString().slice(0, 10)
}

export function addDays(value: string, days: number): string {
  const date = parseDate(value)
  date.setUTCDate(date.getUTCDate() + days)
  return formatDate(date)
}

/** 含首尾的天数。 */
export function inclusiveDays(from: string, to: string): number {
  return Math.round((parseDate(to).getTime() - parseDate(from).getTime()) / 86_400_000) + 1
}

/**
 * 预设对应的支付日期区间（含首尾）。
 * - last2Weeks：今天往前 14 天（原设计稿的「最近一个周期」：MYOB 有周薪 / 双周薪，无法统一成一个周期，所以按两周取，足够覆盖一个双周期）；
 * - last3Months：今天往前 90 天；
 * - thisFinancialYear / lastFinancialYear：澳洲财年 7 月 1 日 ~ 次年 6 月 30 日（本财年截止到今天）。
 */
export function resolvePayslipPreset(preset: PayslipPresetKey, today: string): { from: string; to: string } {
  switch (preset) {
    case 'last2Weeks':
      return { from: addDays(today, -14), to: today }
    case 'last3Months':
      return { from: addDays(today, -90), to: today }
    case 'thisFinancialYear': {
      const [year, month] = today.split('-').map(Number)
      const startYear = month >= 7 ? year : year - 1
      return { from: `${startYear}-07-01`, to: today }
    }
    case 'lastFinancialYear': {
      const [year, month] = today.split('-').map(Number)
      const startYear = (month >= 7 ? year : year - 1) - 1
      return { from: `${startYear}-07-01`, to: `${startYear + 1}-06-30` }
    }
  }
}

/** 当前区间恰好等于某个预设时返回它（用来高亮预设标签）；自定义区间返回 null。 */
export function matchPayslipPreset(from: string, to: string, today: string): PayslipPresetKey | null {
  return PAYSLIP_PRESETS.find((preset) => {
    const range = resolvePayslipPreset(preset, today)
    return range.from === from && range.to === to
  }) ?? null
}

/** 区间是否可以发给后端：两端都有、顺序正确、不超过 24 个月。 */
export function validatePayslipRange(from: string, to: string): 'ok' | 'incomplete' | 'reversed' | 'tooLong' {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(from) || !/^\d{4}-\d{2}-\d{2}$/.test(to)) {
    return 'incomplete'
  }
  if (from > to) {
    return 'reversed'
  }
  return inclusiveDays(from, to) > PAYSLIP_MAX_RANGE_DAYS ? 'tooLong' : 'ok'
}

// ───────────────────────── 格式化 ─────────────────────────

const MONEY = new Intl.NumberFormat('en-AU', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
const HOURS = new Intl.NumberFormat('en-AU', { minimumFractionDigits: 2, maximumFractionDigits: 2 })

/** 金额：$ 1,812.40；负数 −$ 246.00（MYOB 税行为负）；缺失 —（不显示成 0，避免把「没有」误读成「零」）。 */
export function formatMoney(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) {
    return '—'
  }
  const absolute = MONEY.format(Math.abs(value))
  return value < 0 ? `−$ ${absolute}` : `$ ${absolute}`
}

export function formatHours(value: number | null | undefined): string {
  return value === null || value === undefined || !Number.isFinite(value) ? '—' : HOURS.format(value)
}

/** 工资周期：同一年显示 MM-dd ~ MM-dd，跨年显示完整日期；缺失显示 —。 */
export function formatPayslipPeriod(start: string | null, end: string | null): string {
  if (!start && !end) {
    return '—'
  }
  if (start && end && start.slice(0, 4) === end.slice(0, 4)) {
    return `${start.slice(5)} ~ ${end.slice(5)}`
  }
  return `${start ?? '—'} ~ ${end ?? '—'}`
}

/** 列表行的唯一键：同一员工同一支付日可能有多张（不同周期），所以带上周期起始日。 */
export function payslipRowKey(row: MyobPayslipRow): string {
  return `${row.connectionGuid}|${row.employeeUid}|${row.paymentDate}|${row.periodStart ?? ''}`
}

/** 该行是否「无法归到分店」（未绑定或无法确定分店）。 */
export function isUnassignedPlacement(placement: MyobPayslipRow['placement']): boolean {
  return placement === 'Unbound' || placement === 'Unknown'
}

// ───────────────────────── 错误归类 ─────────────────────────

export type PayslipErrorKind = 'reauth' | 'forbidden' | 'unavailable' | 'other'

const HTTP_STATUS = /返回\s*(\d{3})/

/**
 * 把后端的 MYOB 读取错误归成几类，界面按类给不同文案（不显示后端原文，避免暴露内部细节）：
 * - reauth：授权失效或 scope 不够（NEEDS_REAUTHORIZATION / 401）→ 引导去「MYOB 对接」重新授权；
 * - forbidden：公司文件没有开放工资读取（403）；
 * - unavailable：MYOB 暂时不可用 / 限流 / 网络（5xx、429、连不上、超时）；
 * - other：其余。
 */
export function classifyPayslipError(code: string | null | undefined, message: string | null | undefined): PayslipErrorKind {
  if (code === 'MYOB_NEEDS_REAUTHORIZATION') {
    return 'reauth'
  }
  const status = Number(HTTP_STATUS.exec(message ?? '')?.[1])
  if (status === 401) {
    return 'reauth'
  }
  if (status === 403) {
    return 'forbidden'
  }
  if (status === 429 || status >= 500 || /无法连接|超时/.test(message ?? '')) {
    return 'unavailable'
  }
  return 'other'
}

/** 所有已读取的公司都失败（且至少有一家）：整页显示读取失败状态。 */
export function allCompaniesFailed(companies: readonly MyobPayslipCompanyStatus[]): boolean {
  return companies.length > 0 && companies.every((company) => !company.ok)
}

/** 取第一家失败公司的错误类型，用于整页失败状态的文案。 */
export function firstFailureKind(companies: readonly MyobPayslipCompanyStatus[]): PayslipErrorKind {
  const failed = companies.find((company) => !company.ok)
  return failed ? classifyPayslipError(failed.errorCode, failed.errorMessage) : 'other'
}

// ───────────────────────── 分店下拉 ─────────────────────────

export interface PayslipStoreOption {
  value: string
  label: string
}

/**
 * 分店下拉：累积每次返回里出现过的分店（筛选后选项不会缩水），按店号数字序排列。
 * allowedStoreCodes 非空时只保留管辖分店（店长）：同一员工在别店的标签也会出现在行里，但店长不能按别店过滤（后端会 403）。
 */
export function mergePayslipStoreOptions(
  current: readonly PayslipStoreOption[],
  rows: readonly MyobPayslipRow[],
  allowedStoreCodes: readonly string[] | null,
): PayslipStoreOption[] {
  const map = new Map(current.map((item) => [item.value, item.label]))
  for (const row of rows) {
    for (const store of row.stores) {
      map.set(store.storeCode, store.storeLabel || store.storeCode)
    }
  }
  const allowed = allowedStoreCodes ? new Set(allowedStoreCodes.map((code) => code.toLowerCase())) : null
  return [...map.entries()]
    .filter(([code]) => !allowed || allowed.has(code.toLowerCase()))
    .sort(([a], [b]) => a.localeCompare(b, undefined, { numeric: true }))
    .map(([value, label]) => ({ value, label }))
}

// ───────────────────────── MYOB 纸面格式（Pay Slip 原样） ─────────────────────────

const PAPER_NUMBER = new Intl.NumberFormat('en-AU', { minimumFractionDigits: 2, maximumFractionDigits: 2 })

/** 纸面日期：MYOB 原样 d/m/yyyy，不补零（5/10/2026）；取不到返回空串，不显示「—」。 */
export function formatPaperDate(value: string | null | undefined): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value ?? '')
  return match ? `${Number(match[3])}/${Number(match[2])}/${match[1]}` : ''
}

/** 纸面金额：$1,234.00；负数 -$138.00；时薪用 4 位小数（$24.0000）；缺失留空（纸面上没有就是空格）。 */
export function formatPaperMoney(value: number | null | undefined, digits = 2): string {
  if (value === null || value === undefined || !Number.isFinite(value)) {
    return ''
  }
  const absolute =
    digits === 2
      ? PAPER_NUMBER.format(Math.abs(value))
      : new Intl.NumberFormat('en-AU', { minimumFractionDigits: digits, maximumFractionDigits: digits }).format(Math.abs(value))
  return `${value < 0 ? '-' : ''}$${absolute}`
}

/** 纸面小时数 / 假期累计：1,234.00，不带 $；缺失留空。 */
export function formatPaperNumber(value: number | null | undefined): string {
  return value === null || value === undefined || !Number.isFinite(value) ? '' : PAPER_NUMBER.format(value)
}

/** Entitlement 行（假期累计）的 YTD 是小时数，不是金额，纸面不带 $。 */
export function isEntitlementLine(line: Pick<MyobPayslipFlatLine, 'type' | 'typeLabel'>): boolean {
  return (line.type ?? line.typeLabel ?? '').trim().toLowerCase() === 'entitlement'
}

/** 纸面上一行的各列文本（与 MYOB 下载的 PDF 一致）。 */
export function paperLineCells(line: MyobPayslipFlatLine) {
  return {
    description: line.description ?? '',
    hours: formatPaperNumber(line.hours),
    rate: formatPaperMoney(line.rate),
    amount: formatPaperMoney(line.amount),
    yearToDate: isEntitlementLine(line) ? formatPaperNumber(line.yearToDate) : formatPaperMoney(line.yearToDate),
    type: line.typeLabel ?? line.type ?? '',
  }
}

// ───────────────────────── 下载 / 邮件 ─────────────────────────

/** 取不到响应头文件名时的兜底：Payslip_<姓名>_<支付日>.pdf（姓名空格改连字符，去掉路径与非法字符）。 */
export function buildPayslipPdfFilename(employeeName: string, paymentDate: string): string {
  const safeName = employeeName.trim().replace(/\s+/g, '-').replace(/[\\/:*?"<>|]/g, '') || 'Employee'
  const safeDate = paymentDate.slice(0, 10).replace(/[^0-9-]/g, '') || 'date'
  return `Payslip_${safeName}_${safeDate}.pdf`
}

// 前端先做一道常见格式校验；最终以后端 MYOB_PAYSLIP_EMAIL_INVALID 为准。
const EMAIL_PATTERN = /^[^\s@,;]+@[^\s@,;]+\.[^\s@,;]+$/

export function isValidEmailAddress(value: string): boolean {
  return value.length <= 254 && EMAIL_PATTERN.test(value.trim())
}

/** 邮箱比较不分大小写、忽略首尾空白（用来判断是否「改了收件人」）。 */
export function isSameEmailAddress(a: string | null | undefined, b: string | null | undefined): boolean {
  return (a ?? '').trim().toLowerCase() === (b ?? '').trim().toLowerCase()
}
