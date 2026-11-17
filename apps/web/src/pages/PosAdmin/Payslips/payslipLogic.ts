import type {
  MyobPayslipCompanyStatus,
  MyobPayslipFlatLine,
  MyobPayslipRow,
  MyobPayslipSyncResult,
} from '../../../types/myobPayslip'

// 工资单页的纯逻辑：日期预设（澳洲财年）、金额 / 工时 / 周期格式化、错误归类、分店下拉。
// 所有函数都是纯函数（today 由调用方传入），便于单测。

export type PayslipPresetKey = 'lastWeek' | 'last2Weeks' | 'last3Months' | 'thisFinancialYear' | 'lastFinancialYear'

export const PAYSLIP_PRESETS: readonly PayslipPresetKey[] = ['lastWeek', 'last2Weeks', 'last3Months', 'thisFinancialYear', 'lastFinancialYear']

/** 后端单次区间上限：含首尾 731 天（24 个月）。前端用同一个数提前拦截，避免白发一次请求。 */
export const PAYSLIP_MAX_RANGE_DAYS = 731

/**
 * 默认预设：最近一周。周薪员工打开页面就能看到最新一期；双周薪的点「最近两周」即可。
 * 前端每次请求都显式带 from / to，所以这里与后端缺省（最近 90 天）不同也没关系。
 */
export const PAYSLIP_DEFAULT_PRESET: PayslipPresetKey = 'lastWeek'

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
 * - lastWeek：今天往前 7 天（同一个星期几到今天，周薪至少能覆盖最新一期）；
 * - last2Weeks：今天往前 14 天（原设计稿的「最近一个周期」：MYOB 有周薪 / 双周薪，无法统一成一个周期，所以按两周取，足够覆盖一个双周期）；
 * - last3Months：今天往前 90 天；
 * - thisFinancialYear / lastFinancialYear：澳洲财年 7 月 1 日 ~ 次年 6 月 30 日（本财年截止到今天）。
 */
export function resolvePayslipPreset(preset: PayslipPresetKey, today: string): { from: string; to: string } {
  switch (preset) {
    case 'lastWeek':
      return { from: addDays(today, -7), to: today }
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

export type PayslipErrorKind = 'reauth' | 'forbidden' | 'unavailable' | 'syncPending' | 'other'

const HTTP_STATUS = /返回\s*(\d{3})/

/** 后端：这家公司的工资单还没有成功同步到本地（首次同步进行中或失败），本地没有任何数据可读。 */
export const PAYSLIP_SYNC_PENDING_CODE = 'MYOB_PAYSLIP_SYNC_PENDING'

/**
 * 把后端的 MYOB 读取错误归成几类，界面按类给不同文案（不显示后端原文，避免暴露内部细节）：
 * - reauth：授权失效或 scope 不够（NEEDS_REAUTHORIZATION / 401）→ 引导去「MYOB 对接」重新授权；
 * - forbidden：公司文件没有开放工资读取（403）；
 * - unavailable：MYOB 暂时不可用 / 限流 / 网络（5xx、429、连不上、超时）；
 * - syncPending：本地还没有这家公司的数据（首次同步还没完成）——不是 MYOB 出错；
 * - other：其余。
 */
export function classifyPayslipError(code: string | null | undefined, message: string | null | undefined): PayslipErrorKind {
  if (code === PAYSLIP_SYNC_PENDING_CODE) {
    return 'syncPending'
  }
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

// ───────────────────────── 同步状态（本地快照） ─────────────────────────

/** 公司提示的种类：正在同步 / 等待首次同步 / 读取失败 / 同步失败（仍显示旧数据）/ 历史还没回填完。 */
export type PayslipCompanyNoticeKind = 'syncing' | 'pending' | 'failed' | 'staleAfterFailure' | 'historyIncomplete'

export interface PayslipCompanyNotice {
  connectionGuid: string
  name: string
  kind: PayslipCompanyNoticeKind
  /** 失败原因归类（pending / failed / staleAfterFailure 用来选文案）。 */
  errorKind: PayslipErrorKind
  lastSyncedAt: string | null
}

/**
 * 每家公司最多一条提示，优先级：本地没数据（正在同步 > 等待首次同步 > 失败）；有数据时 同步失败 > 历史没回填完 > 正在同步。
 * 一切正常的公司没有提示（不打扰）。
 */
export function buildPayslipCompanyNotices(companies: readonly MyobPayslipCompanyStatus[]): PayslipCompanyNotice[] {
  const notices: PayslipCompanyNotice[] = []
  for (const company of companies) {
    const base = { connectionGuid: company.connectionGuid, name: company.connectionName, lastSyncedAt: company.lastSyncedAt }
    const syncErrorKind = company.syncError ? classifyPayslipError(null, company.syncError) : 'other'
    if (!company.ok) {
      if (company.syncing) {
        notices.push({ ...base, kind: 'syncing', errorKind: 'other' })
      } else if (company.errorCode === PAYSLIP_SYNC_PENDING_CODE) {
        notices.push({ ...base, kind: 'pending', errorKind: syncErrorKind })
      } else {
        notices.push({ ...base, kind: 'failed', errorKind: classifyPayslipError(company.errorCode, company.errorMessage) })
      }
    } else if (company.syncError) {
      notices.push({ ...base, kind: 'staleAfterFailure', errorKind: syncErrorKind })
    } else if (company.historyIncomplete) {
      notices.push({ ...base, kind: 'historyIncomplete', errorKind: 'other' })
    } else if (company.syncing) {
      notices.push({ ...base, kind: 'syncing', errorKind: 'other' })
    }
  }
  return notices
}

/** 手动同步完成后给用户的一句话：按严重程度取一种。 */
export type PayslipSyncSummary =
  | { kind: 'failed'; names: string[] }
  | { kind: 'busy'; names: string[] }
  | { kind: 'recent' }
  | { kind: 'upToDate' }
  | { kind: 'done'; inserted: number; updated: number; removed: number }

/**
 * 汇总手动同步结果：任何一家失败 → failed（本地数据已保留，各家原因在页面提示里）；其次有公司正在别处同步 → busy；
 * 全部在 60 秒冷却内 → recent；成功的几家都没有变化 → upToDate；否则累计新增 / 更新 / 撤销。
 */
export function summarizePayslipSync(result: MyobPayslipSyncResult): PayslipSyncSummary {
  const named = (status: string) => result.companies.filter((company) => company.status === status).map((company) => company.connectionName)
  const failed = named('Failed')
  if (failed.length > 0) {
    return { kind: 'failed', names: failed }
  }
  const busy = named('Busy')
  if (busy.length > 0) {
    return { kind: 'busy', names: busy }
  }
  const synced = result.companies.filter((company) => company.status === 'Synced')
  if (synced.length === 0) {
    return { kind: 'recent' }
  }
  const inserted = synced.reduce((sum, company) => sum + company.inserted, 0)
  const updated = synced.reduce((sum, company) => sum + company.updated, 0)
  const removed = synced.reduce((sum, company) => sum + company.removed, 0)
  return inserted + updated + removed === 0 ? { kind: 'upToDate' } : { kind: 'done', inserted, updated, removed }
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
