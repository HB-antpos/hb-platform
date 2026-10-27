// MYOB 阶段 2（工资类别映射 + 只读推送预览）的纯逻辑：映射草稿与保存校验、行状态 / 阻断码到标签、提示分组、
// 日网格格式化、跨公司份额说明、CSV 生成、汇总文案等。
// 不依赖 React / antd，便于单元测试；文案键一律写成字面量，供页面文案契约测试扫描。

import type {
  MyobPayCategories,
  MyobPayCategoryKey,
  MyobPayCategoryMapping,
  MyobPreviewHint,
  MyobPreviewPeriodOption,
  MyobPreviewRow,
  MyobPushHours,
  MyobTimesheetPreview,
} from '../../../types/myob'

// ───────────────────────── 工资类别映射 ─────────────────────────

/** 5 个类别键，顺序即页面行的顺序（与后端 MyobPayCategoryKeys.All 一致）。 */
export const PAY_CATEGORY_KEYS: readonly MyobPayCategoryKey[] = [
  'weekday',
  'saturday',
  'sunday',
  'personalLeave',
  'holidayLeave',
]

export const PAY_CATEGORY_TOTAL = PAY_CATEGORY_KEYS.length

const PAY_CATEGORY_FIELDS: Readonly<Record<MyobPayCategoryKey, keyof MyobPayCategoryMapping>> = {
  weekday: 'weekdayUid',
  saturday: 'saturdayUid',
  sunday: 'sundayUid',
  personalLeave: 'personalLeaveUid',
  holidayLeave: 'holidayLeaveUid',
}

export interface PayCategoryRowMeta {
  key: MyobPayCategoryKey
  /** 「我方工时」列主标题。 */
  labelKey: string
  /** 主标题下的灰色小字（含哪些工时 / 推送成哪个 MYOB 类别）。 */
  subKey: string | null
  /** 「说明」列的固定说明（行没有特殊状态时显示）。 */
  noteKey: string | null
}

export const PAY_CATEGORY_ROWS: readonly PayCategoryRowMeta[] = [
  { key: 'weekday', labelKey: 'myob.payCategories.rows.weekday.label', subKey: 'myob.payCategories.rows.weekday.sub', noteKey: 'myob.payCategories.rows.weekday.note' },
  { key: 'saturday', labelKey: 'myob.payCategories.rows.saturday.label', subKey: null, noteKey: 'myob.payCategories.rows.saturday.note' },
  { key: 'sunday', labelKey: 'myob.payCategories.rows.sunday.label', subKey: null, noteKey: 'myob.payCategories.rows.sunday.note' },
  { key: 'personalLeave', labelKey: 'myob.payCategories.rows.personalLeave.label', subKey: 'myob.payCategories.rows.personalLeave.sub', noteKey: 'myob.payCategories.rows.personalLeave.note' },
  { key: 'holidayLeave', labelKey: 'myob.payCategories.rows.holidayLeave.label', subKey: 'myob.payCategories.rows.holidayLeave.sub', noteKey: 'myob.payCategories.rows.holidayLeave.note' },
]

/** 草稿只记录用户改过的行：类别键 → 新选的 MYOB 类别 uid。改回与基线一致时删掉该键，保证「只记变化」。 */
export type PayCategoryEdits = Partial<Record<MyobPayCategoryKey, string | null>>

export function getMappingUid(mapping: MyobPayCategoryMapping, key: MyobPayCategoryKey): string | null {
  return mapping[PAY_CATEGORY_FIELDS[key]]
}

function categoryUidSet(data: MyobPayCategories): ReadonlySet<string> {
  return new Set(data.categories.map((item) => item.uid))
}

/** 已保存的映射指向的类别在 MYOB 里已找不到：后端 missingKeys 说了算，同时自己再核对一次（后端漏报时不显示裸 uid）。 */
export function isPayCategoryMissing(data: MyobPayCategories, key: MyobPayCategoryKey): boolean {
  const saved = getMappingUid(data.mapping, key)
  if (data.missingKeys.includes(key)) {
    return true
  }
  return saved !== null && !categoryUidSet(data).has(saved)
}

/** 某行的「基线值」：能用的已保存映射；没有则取（仍在 MYOB 类别里的）名称相近建议；都没有为 null。 */
export function getBasePayCategoryUid(data: MyobPayCategories, key: MyobPayCategoryKey): string | null {
  const uids = categoryUidSet(data)
  const saved = getMappingUid(data.mapping, key)
  if (saved !== null && !isPayCategoryMissing(data, key)) {
    return saved
  }
  const suggested = getMappingUid(data.suggestions, key)
  return suggested !== null && saved === null && uids.has(suggested) ? suggested : null
}

/** 当前（含草稿）每一行实际选中的类别 uid。 */
export function resolvePayCategoryValues(
  data: MyobPayCategories,
  edits: PayCategoryEdits,
): Record<MyobPayCategoryKey, string | null> {
  const values = {} as Record<MyobPayCategoryKey, string | null>
  for (const key of PAY_CATEGORY_KEYS) {
    values[key] = Object.prototype.hasOwnProperty.call(edits, key) ? (edits[key] ?? null) : getBasePayCategoryUid(data, key)
  }
  return values
}

/** 写入一行草稿；改回与基线一致时直接删掉该键。 */
export function setPayCategoryEdit(
  data: MyobPayCategories,
  edits: PayCategoryEdits,
  key: MyobPayCategoryKey,
  uid: string | null,
): PayCategoryEdits {
  const next = { ...edits }
  if (uid === getBasePayCategoryUid(data, key)) {
    delete next[key]
  } else {
    next[key] = uid
  }
  return next
}

/**
 * 重新读取 MYOB 类别后对草稿「对账」：草稿选的类别已不在新列表里的丢弃；与新基线一致的丢弃。
 * 避免保存时把 MYOB 里已删掉的类别 uid 提交上去。
 */
export function reconcilePayCategoryEdits(data: MyobPayCategories, edits: PayCategoryEdits): PayCategoryEdits {
  const uids = categoryUidSet(data)
  const next: PayCategoryEdits = {}
  for (const key of PAY_CATEGORY_KEYS) {
    if (!Object.prototype.hasOwnProperty.call(edits, key)) {
      continue
    }
    const uid = edits[key] ?? null
    if (uid !== null && !uids.has(uid)) {
      continue
    }
    if (uid !== getBasePayCategoryUid(data, key)) {
      next[key] = uid
    }
  }
  return next
}

/** 用户是否动过：用于「离开 / 切公司前提示」。只靠名称推荐预填、没人改过的不算「未保存改动」。 */
export function hasPayCategoryEdits(edits: PayCategoryEdits): boolean {
  return Object.keys(edits).length > 0
}

export type PayCategoryRowState =
  /** 已保存且仍有效。 */
  | 'set'
  /** 没保存过，按名称相近预填，保存后才生效。 */
  | 'suggested'
  /** 没保存过，也没有建议：需手动选。 */
  | 'unset'
  /** 已保存的类别在 MYOB 里找不到了：需重新选。 */
  | 'missing'
  /** 用户这次改过（含从「未设置 / 失效」改选了新值），尚未保存。 */
  | 'edited'

export function getPayCategoryRowState(
  data: MyobPayCategories,
  edits: PayCategoryEdits,
  key: MyobPayCategoryKey,
): PayCategoryRowState {
  if (Object.prototype.hasOwnProperty.call(edits, key)) {
    return edits[key] ? 'edited' : isPayCategoryMissing(data, key) ? 'missing' : 'unset'
  }
  if (isPayCategoryMissing(data, key)) {
    return 'missing'
  }
  if (getMappingUid(data.mapping, key) !== null) {
    return 'set'
  }
  return getBasePayCategoryUid(data, key) !== null ? 'suggested' : 'unset'
}

export function countMappedValues(values: Record<MyobPayCategoryKey, string | null>): number {
  return PAY_CATEGORY_KEYS.filter((key) => values[key] !== null).length
}

export interface PayCategorySaveState {
  /** 还没选的项数（0–5）。 */
  remaining: number
  /** 与已保存映射相比有没有差异（含按名称推荐预填、失效项改选）。 */
  changed: boolean
  /** 5 项都选了且有差异才允许保存。 */
  canSave: boolean
}

export function getPayCategorySaveState(data: MyobPayCategories, edits: PayCategoryEdits): PayCategorySaveState {
  const values = resolvePayCategoryValues(data, edits)
  const remaining = PAY_CATEGORY_TOTAL - countMappedValues(values)
  const changed = PAY_CATEGORY_KEYS.some((key) => values[key] !== getMappingUid(data.mapping, key))
  return { remaining, changed, canSave: remaining === 0 && changed }
}

/** 保存请求体；5 项没选齐返回 null（调用方不应发请求）。 */
export function buildPayCategoryMappingRequest(
  data: MyobPayCategories,
  edits: PayCategoryEdits,
): MyobPayCategoryMapping | null {
  const values = resolvePayCategoryValues(data, edits)
  if (countMappedValues(values) !== PAY_CATEGORY_TOTAL) {
    return null
  }
  return {
    weekdayUid: values.weekday,
    saturdayUid: values.saturday,
    sundayUid: values.sunday,
    personalLeaveUid: values.personalLeave,
    holidayLeaveUid: values.holidayLeave,
  }
}

export interface CompanyMappingBadge {
  mapped: number
  total: number
  complete: boolean
}

/** 公司卡片上的「已映射 N / 5」与完整性：用已保存的数据（推送用的就是已保存映射）；没读到数据按 0 / 5 未完整。 */
export function getCompanyMappingBadge(data: MyobPayCategories | null | undefined): CompanyMappingBadge {
  if (!data) {
    return { mapped: 0, total: PAY_CATEGORY_TOTAL, complete: false }
  }
  return { mapped: Math.min(PAY_CATEGORY_TOTAL, Math.max(0, data.mappedCount)), total: PAY_CATEGORY_TOTAL, complete: data.complete }
}

// ───────────────────────── 日期（纯日期字符串，不经 Date 本地时区） ─────────────────────────

const ISO_DATE = /^(\d{4})-(\d{2})-(\d{2})/

/** 解析 yyyy-MM-dd（容忍后面带时间）；非法返回 null。 */
export function parseIsoDate(value: string | null | undefined): { year: number; month: number; day: number } | null {
  const match = ISO_DATE.exec((value ?? '').trim())
  if (!match) {
    return null
  }
  const year = Number(match[1])
  const month = Number(match[2])
  const day = Number(match[3])
  // 用 UTC 构造再回读，挡掉 2026-02-31 这类不存在的日期
  const probe = new Date(Date.UTC(year, month - 1, day))
  if (probe.getUTCFullYear() !== year || probe.getUTCMonth() !== month - 1 || probe.getUTCDate() !== day) {
    return null
  }
  return { year, month, day }
}

/** 一周里的第几天（0 = 周日）。全程 UTC：日期是纯日期，用本地时区解析会差一天。 */
export function getWeekdayIndex(value: string): number | null {
  const parsed = parseIsoDate(value)
  return parsed ? new Date(Date.UTC(parsed.year, parsed.month - 1, parsed.day)).getUTCDay() : null
}

/** 星期的文案键（字面量，下标 0 = 周日）。 */
export const WEEKDAY_LABEL_KEYS: readonly string[] = [
  'myob.push.weekdays.sun',
  'myob.push.weekdays.mon',
  'myob.push.weekdays.tue',
  'myob.push.weekdays.wed',
  'myob.push.weekdays.thu',
  'myob.push.weekdays.fri',
  'myob.push.weekdays.sat',
]

export interface DayHeader {
  day: string
  /** 星期文案键；日期不合法时为 null（此时只显示原始字符串）。 */
  weekdayKey: string | null
  /** 几号；日期不合法时为 null。 */
  dayOfMonth: number | null
}

export function buildDayHeader(day: string): DayHeader {
  const parsed = parseIsoDate(day)
  const weekday = getWeekdayIndex(day)
  return {
    day,
    weekdayKey: weekday === null ? null : WEEKDAY_LABEL_KEYS[weekday],
    dayOfMonth: parsed?.day ?? null,
  }
}

/** 两个纯日期相差的天数（end - start）；任一非法返回 null。 */
export function diffDays(start: string, end: string): number | null {
  const a = parseIsoDate(start)
  const b = parseIsoDate(end)
  if (!a || !b) {
    return null
  }
  return Math.round((Date.UTC(b.year, b.month - 1, b.day) - Date.UTC(a.year, a.month - 1, a.day)) / 86_400_000)
}

/** 工资周期的周数（含首尾共几天 / 7，至少 1）；日期非法按 1 周。 */
export function getPeriodWeeks(period: MyobPreviewPeriodOption): number {
  const span = diffDays(period.start, period.end)
  return span === null ? 1 : Math.max(1, Math.round((span + 1) / 7))
}

/** 「2026-10-26 ~ 2026-11-01」；short 时同一年的结束日省略年份（「2026-10-26 ~ 11-01」）。 */
export function formatPeriodRange(start: string, end: string, short = false): string {
  if (!end) {
    return start
  }
  const sameYear = short && start.slice(0, 4) === end.slice(0, 4)
  return `${start} ~ ${sameYear ? end.slice(5) : end}`
}

// ───────────────────────── 小时格式化 ─────────────────────────

/** 两位小数；用于合计、汇总、CSV。 */
export function formatHours(value: number): string {
  return (Number.isFinite(value) ? value : 0).toFixed(2)
}

/** 表格单元格：0 显示「—」，其余两位小数。 */
export function formatHoursCell(value: number): string {
  return Math.abs(value) < 0.005 ? '—' : formatHours(value)
}

// ───────────────────────── 推送预览：周期 ─────────────────────────

/** 周期下拉选项：保证当前后端返回的周期一定在选项里（后端没把它列进 periods 时补在最前面）。 */
export function buildPeriodOptions(
  preview: Pick<MyobTimesheetPreview, 'periods' | 'periodStart' | 'periodEnd'> | null,
): MyobPreviewPeriodOption[] {
  if (!preview) {
    return []
  }
  const options = [...preview.periods]
  if (preview.periodStart && !options.some((item) => item.start === preview.periodStart)) {
    options.unshift({ start: preview.periodStart, end: preview.periodEnd })
  }
  return options
}

// ───────────────────────── 推送预览：行状态 / 拦截原因 ─────────────────────────

/** 拦截码 → 状态列短标签（字面量键）。 */
export const BLOCKER_LABEL_KEYS: Readonly<Record<string, string>> = {
  NOT_BOUND: 'myob.push.blocker.NOT_BOUND',
  STORE_NOT_LOCKED: 'myob.push.blocker.STORE_NOT_LOCKED',
  STORE_NOT_MAPPED: 'myob.push.blocker.STORE_NOT_MAPPED',
  CATEGORY_NOT_MAPPED: 'myob.push.blocker.CATEGORY_NOT_MAPPED',
  NO_RATE: 'myob.push.blocker.NO_RATE',
  NO_STORE_ACCESS: 'myob.push.blocker.NO_STORE_ACCESS',
  MYOB_PROCESSED: 'myob.push.blocker.MYOB_PROCESSED',
}

export const BLOCKER_UNKNOWN_LABEL_KEY = 'myob.push.blocker.unknown'

/** 未知拦截码用通用「被拦截」，不把后端原始码当文案展示。 */
export function getBlockerLabelKey(code: string | null | undefined): string {
  return BLOCKER_LABEL_KEYS[(code ?? '').trim().toUpperCase()] ?? BLOCKER_UNKNOWN_LABEL_KEY
}

export type RowStatusKind = 'ready' | 'readyCashOut' | 'noTransfer' | 'blocked'

export interface RowStatusView {
  kind: RowStatusKind
  /** antd Tag 的 color。 */
  tone: 'success' | 'warning' | 'default' | 'error'
  labelKey: string
  /** 被拦截时第一条拦截项的码（去绑定等跳转依据）；其他状态为 null。 */
  blockerCode: string | null
  /** 除第一条外还有几条拦截项。 */
  extraBlockers: number
  /** 悬停说明：全部拦截原因（后端 message）或折现提示；没有为 null。 */
  detail: string | null
}

export function getRowStatusView(row: MyobPreviewRow): RowStatusView {
  if (row.status === 'Blocked') {
    const first = row.blockers[0]
    return {
      kind: 'blocked',
      tone: 'error',
      labelKey: getBlockerLabelKey(first?.code),
      blockerCode: first?.code ?? null,
      extraBlockers: Math.max(0, row.blockers.length - 1),
      detail: row.blockers.map((item) => item.message).filter(Boolean).join('\n') || null,
    }
  }
  if (row.status === 'NoTransfer') {
    return { kind: 'noTransfer', tone: 'default', labelKey: 'myob.push.status.noTransfer', blockerCode: null, extraBlockers: 0, detail: null }
  }
  if (row.cashOutHours > 0) {
    return { kind: 'readyCashOut', tone: 'warning', labelKey: 'myob.push.status.readyCashOut', blockerCode: null, extraBlockers: 0, detail: null }
  }
  return { kind: 'ready', tone: 'success', labelKey: 'myob.push.status.ready', blockerCode: null, extraBlockers: 0, detail: null }
}

/** 行底色类名（颜色之外状态列有文字，不只靠颜色）。 */
export function getRowClassName(row: MyobPreviewRow): string {
  if (row.status === 'Blocked') return 'myob-push-row-blocked'
  if (row.status === 'NoTransfer') return 'myob-push-row-muted'
  return ''
}

// ───────────────────────── 推送预览：员工单元格 ─────────────────────────

/** 发放方式标签：Mixed 带固定转账小时（每周）；没有时薪记录（payMode 为空）不显示标签。 */
export function getPayModeTag(row: MyobPreviewRow): { labelKey: string; hours?: string } | null {
  switch (row.payMode) {
    case 'Transfer':
      return { labelKey: 'myob.push.payMode.Transfer' }
    case 'Cash':
      return { labelKey: 'myob.push.payMode.Cash' }
    case 'Mixed':
      return { labelKey: 'myob.push.payMode.Mixed', hours: formatFixedHours(row.fixedTransferHoursPerWeek) }
    default:
      return null
  }
}

/** 固定转账小时：整数不带小数（20），否则最多两位（7.5）。 */
export function formatFixedHours(value: number): string {
  const rounded = Math.round((Number.isFinite(value) ? value : 0) * 100) / 100
  return String(rounded)
}

function gcd(a: number, b: number): number {
  return b === 0 ? a : gcd(b, a % b)
}

/** 约分后的比例（「3 : 1」）；有 ≤0 的值、或约分后仍有大于 99 的数（没有易读的比例）时返回 null。 */
export function formatRatio(values: number[]): string | null {
  const scaled = values.map((value) => Math.round(value * 100))
  if (scaled.length < 2 || scaled.some((value) => value <= 0)) {
    return null
  }
  const divisor = scaled.reduce((acc, value) => gcd(acc, value))
  const reduced = scaled.map((value) => value / divisor)
  return reduced.every((value) => value <= 99) ? reduced.join(' : ') : null
}

export interface CrossShareInfo {
  /** 本公司分到的转账额度。 */
  allocated: string
  /** 各公司分到的转账额度合计（= 固定转账总额）。 */
  total: string
  /** 工时比例；没有易读比例为 null。 */
  ratio: string | null
}

/** 跨公司员工的份额摘要；不是跨公司或份额不足两家返回 null。 */
export function describeCrossShare(row: MyobPreviewRow): CrossShareInfo | null {
  const shares = row.detail.shares
  if (!row.isCrossCompany || shares.length < 2) {
    return null
  }
  return {
    allocated: formatHours(row.allocatedTransferHours),
    total: formatHours(shares.reduce((sum, share) => sum + share.allocatedTransferHours, 0)),
    ratio: formatRatio(shares.map((share) => share.actualHours)),
  }
}

export interface RowNote {
  key: string
  values?: Record<string, string | number>
  /** 需要引起注意的备注（琥珀色）。 */
  warn?: boolean
}

/** 员工名下的说明行：跨公司份额 / 已绑定的 MYOB 员工卡 / 装入顺序提醒 / MYOB 状态（检查后）。 */
export function getRowNotes(row: MyobPreviewRow): RowNote[] {
  const notes: RowNote[] = []
  const cross = describeCrossShare(row)
  if (cross) {
    notes.push(
      cross.ratio
        ? { key: 'myob.push.notes.crossShare', values: { allocated: cross.allocated, total: cross.total, ratio: cross.ratio } }
        : { key: 'myob.push.notes.crossShareNoRatio', values: { allocated: cross.allocated, total: cross.total } },
    )
  } else if (row.myobName) {
    notes.push({ key: 'myob.push.notes.bound', values: { name: row.myobName } })
  }
  // 转账 + 现金的员工，病假 / 年假先于平日装入转账额度（这是员工容易问「为什么没推平日」的地方）
  if (!cross && row.payMode === 'Mixed' && (row.pushed.personalLeave > 0 || row.pushed.holidayLeave > 0)) {
    notes.push({ key: 'myob.push.notes.leaveFirst' })
  }
  if (row.myobState === 'HasEntries') {
    notes.push({ key: 'myob.push.notes.myobHasEntries', warn: true })
  } else if (row.myobState === 'CheckFailed') {
    notes.push({ key: 'myob.push.notes.myobCheckFailed', warn: true })
  }
  return notes
}

/** 推送合计列：0 显示「—」。 */
export function sumPushHours(hours: MyobPushHours): number {
  return hours.weekday + hours.saturday + hours.sunday + hours.personalLeave + hours.holidayLeave
}

// ───────────────────────── 推送预览：就绪卡 ─────────────────────────

export type ReadinessTone = 'ok' | 'bad' | 'warn' | 'muted'

export interface ReadinessCard {
  id: 'locked' | 'unlocked' | 'category' | 'processed'
  tone: ReadinessTone
  /** 状态符号：状态不能只靠颜色。 */
  symbol: '✓' | '✕' | '!' | '–'
  titleKey: string
  titleValues?: Record<string, string | number>
  /** 副标题：文案键（含插值）或直接文本（分店清单）。 */
  subKey?: string
  subValues?: Record<string, string | number>
  subText?: string
  /** 未锁定分店清单：页面按「分店 · 涉及 N 人」渲染。 */
  subItems?: Array<{ label: string; count: number }>
  /** 卡片上的动作：去映射。 */
  action?: 'goMapping'
}

function joinStoreNames(items: string[]): string {
  return items.join('、')
}

/** 四张就绪卡。myobChecked 为 false 时第四张显示「未检查」，不假装 MYOB 里没有已发薪记录。 */
export function buildReadinessCards(preview: MyobTimesheetPreview): ReadinessCard[] {
  const { readiness } = preview
  const cards: ReadinessCard[] = []

  cards.push(
    readiness.lockedStores.length > 0
      ? { id: 'locked', tone: 'ok', symbol: '✓', titleKey: 'myob.push.readiness.locked.title', subText: joinStoreNames(readiness.lockedStores) }
      : { id: 'locked', tone: 'muted', symbol: '–', titleKey: 'myob.push.readiness.locked.title', subKey: 'myob.push.readiness.locked.none' },
  )

  cards.push(
    readiness.unlockedStores.length > 0
      ? {
          id: 'unlocked',
          tone: 'bad',
          symbol: '✕',
          titleKey: 'myob.push.readiness.unlocked.title',
          titleValues: { count: readiness.unlockedStores.length },
          subItems: readiness.unlockedStores.map((store) => ({
            label: [store.storeCode, store.storeName].filter(Boolean).join(' '),
            count: store.employeeCount,
          })),
        }
      : { id: 'unlocked', tone: 'ok', symbol: '✓', titleKey: 'myob.push.readiness.unlocked.noneTitle', subKey: 'myob.push.readiness.unlocked.noneSub' },
  )

  cards.push(
    readiness.categoryComplete
      ? {
          id: 'category',
          tone: 'ok',
          symbol: '✓',
          titleKey: 'myob.push.readiness.category.title',
          titleValues: { mapped: readiness.categoryMappedCount, total: PAY_CATEGORY_TOTAL },
          subText: preview.connectionName,
        }
      : {
          id: 'category',
          tone: 'bad',
          symbol: '✕',
          titleKey: 'myob.push.readiness.category.title',
          titleValues: { mapped: readiness.categoryMappedCount, total: PAY_CATEGORY_TOTAL },
          subKey: 'myob.push.readiness.category.incomplete',
          action: 'goMapping',
        },
  )

  if (!readiness.myobChecked) {
    cards.push({
      id: 'processed',
      tone: 'muted',
      symbol: '–',
      titleKey: 'myob.push.readiness.processed.title',
      subKey: 'myob.push.readiness.processed.unchecked',
    })
  } else if (readiness.myobProcessedCount > 0) {
    cards.push({
      id: 'processed',
      tone: 'bad',
      symbol: '✕',
      titleKey: 'myob.push.readiness.processed.title',
      subKey: 'myob.push.readiness.processed.some',
      subValues: { count: readiness.myobProcessedCount },
    })
  } else if (readiness.myobCheckFailedCount > 0) {
    cards.push({
      id: 'processed',
      tone: 'warn',
      symbol: '!',
      titleKey: 'myob.push.readiness.processed.title',
      subKey: 'myob.push.readiness.processed.failed',
      subValues: { count: readiness.myobCheckFailedCount },
    })
  } else {
    cards.push({
      id: 'processed',
      tone: 'ok',
      symbol: '✓',
      titleKey: 'myob.push.readiness.processed.title',
      subKey: 'myob.push.readiness.processed.none',
    })
  }
  return cards
}

// ───────────────────────── 推送预览：提示（hints） ─────────────────────────

/** 提示码 → 分组标题（字面量键）。 */
export const HINT_CODE_LABEL_KEYS: Readonly<Record<string, string>> = {
  BIRTHDAY_REVIEW: 'myob.push.hints.codes.BIRTHDAY_REVIEW',
  BELOW_MINIMUM_RATE: 'myob.push.hints.codes.BELOW_MINIMUM_RATE',
  PUBLIC_HOLIDAY_RATE_MISMATCH: 'myob.push.hints.codes.PUBLIC_HOLIDAY_RATE_MISMATCH',
  ANNUAL_CASH_OUT_NOT_PUSHED: 'myob.push.hints.codes.ANNUAL_CASH_OUT_NOT_PUSHED',
}

export const HINT_UNKNOWN_LABEL_KEY = 'myob.push.hints.codes.unknown'

export function getHintLabelKey(code: string | null | undefined): string {
  return HINT_CODE_LABEL_KEYS[(code ?? '').trim().toUpperCase()] ?? HINT_UNKNOWN_LABEL_KEY
}

export interface HintGroup {
  code: string
  labelKey: string
  items: MyobPreviewHint[]
}

/** 按提示码分组：已知码按固定顺序在前，未知码合并成一组放最后；组内保持后端顺序。 */
export function groupHints(hints: MyobPreviewHint[]): HintGroup[] {
  const order = Object.keys(HINT_CODE_LABEL_KEYS)
  const known = new Map<string, MyobPreviewHint[]>()
  const unknown: MyobPreviewHint[] = []
  for (const hint of hints) {
    const code = hint.code.trim().toUpperCase()
    if (HINT_CODE_LABEL_KEYS[code]) {
      known.set(code, [...(known.get(code) ?? []), hint])
    } else {
      unknown.push(hint)
    }
  }
  const groups: HintGroup[] = order
    .filter((code) => known.has(code))
    .map((code) => ({ code, labelKey: HINT_CODE_LABEL_KEYS[code], items: known.get(code) ?? [] }))
  if (unknown.length > 0) {
    groups.push({ code: 'UNKNOWN', labelKey: HINT_UNKNOWN_LABEL_KEY, items: unknown })
  }
  return groups
}

/**
 * 把提示说明拆成「前 / 员工名 / 后」，方便把员工名加粗：说明里本来就含员工名就原位拆分；
 * 不含则 before 为空、separated=true，页面在员工名后补一个（随语言的）冒号再接说明。
 */
export function splitHintMessage(hint: MyobPreviewHint): { before: string; name: string; after: string; separated: boolean } {
  const name = hint.userName.trim()
  if (!name) {
    return { before: hint.message, name: '', after: '', separated: false }
  }
  const index = hint.message.indexOf(name)
  if (index >= 0) {
    return { before: hint.message.slice(0, index), name, after: hint.message.slice(index + name.length), separated: false }
  }
  return { before: '', name, after: hint.message, separated: true }
}

// ───────────────────────── 推送预览：汇总 / 推送按钮 ─────────────────────────

/** 「推送到 MYOB」按钮是否可用：后端已开放 + 有可推送的人 + 有提示时已勾选「已知晓」。本批后端恒不开放。 */
export function canPushPreview(preview: MyobTimesheetPreview | null, acknowledged: boolean): boolean {
  if (!preview || !preview.pushEnabled || preview.totals.readyCount <= 0) {
    return false
  }
  return preview.hints.length === 0 || acknowledged
}

// ───────────────────────── 按日明细抽屉 ─────────────────────────

/** 「本公司实际工时」行名（工时种类键 → 文案键，字面量）。 */
export const KIND_LABEL_KEYS: Readonly<Record<string, string>> = {
  weekday: 'myob.push.drawer.kinds.weekday',
  saturday: 'myob.push.drawer.kinds.saturday',
  sunday: 'myob.push.drawer.kinds.sunday',
  publicHolidayWork: 'myob.push.drawer.kinds.publicHolidayWork',
  publicHolidayPay: 'myob.push.drawer.kinds.publicHolidayPay',
  sick: 'myob.push.drawer.kinds.sick',
  annual: 'myob.push.drawer.kinds.annual',
  annualCashOut: 'myob.push.drawer.kinds.annualCashOut',
}

/** 「将推送」行名（MYOB 类别键 → 文案键，字面量）。 */
export const PUSHED_LABEL_KEYS: Readonly<Record<string, string>> = {
  weekday: 'myob.push.drawer.pushedRows.weekday',
  saturday: 'myob.push.drawer.pushedRows.saturday',
  sunday: 'myob.push.drawer.pushedRows.sunday',
  personalLeave: 'myob.push.drawer.pushedRows.personalLeave',
  holidayLeave: 'myob.push.drawer.pushedRows.holidayLeave',
}

/** 找不到文案键的行返回 null（页面显示原始键，也比显示空白强）。 */
export function getKindLabelKey(kind: string): string | null {
  return KIND_LABEL_KEYS[kind] ?? null
}

export function getPushedLabelKey(key: string): string | null {
  return PUSHED_LABEL_KEYS[key] ?? null
}

const LEGAL_SUFFIX = /\s+(?:pty\.?\s*ltd\.?|pty\.?|ltd\.?|limited)\s*$/i

/**
 * 公司名缩写：去掉 Pty Ltd 之类的后缀；多家公司时再去掉大家共有的前导单词
 * （「Hot Bargain Investment Pty Ltd」「Hot Bargain Australian Pty Ltd」→「Investment」「Australian」）。
 * 缩写后会出现空串或重名时放弃去前缀，保证两家公司仍可区分。
 */
export function shortCompanyNames(names: string[]): string[] {
  const stripped = names.map((name) => name.replace(LEGAL_SUFFIX, '').trim() || name.trim())
  if (stripped.length < 2) {
    return stripped
  }
  const words = stripped.map((name) => name.split(/\s+/))
  // 一家的名字是另一家名字的开头（「Hot Bargain」与「Hot Bargain Two」）：去前缀只会得到残缺的词，保持原样
  const lower = words.map((list) => list.map((word) => word.toLowerCase()))
  const isWordPrefix = (a: string[], b: string[]) => a.length <= b.length && a.every((word, index) => word === b[index])
  if (lower.some((a, i) => lower.some((b, j) => i !== j && isWordPrefix(a, b)))) {
    return stripped
  }
  let common = 0
  while (
    words.every((list) => list.length > common + 1) &&
    words.every((list) => list[common].toLowerCase() === words[0][common].toLowerCase())
  ) {
    common += 1
  }
  if (common === 0) {
    return stripped
  }
  const trimmed = words.map((list) => list.slice(common).join(' '))
  return new Set(trimmed.map((name) => name.toLowerCase())).size === trimmed.length ? trimmed : stripped
}

/** 校验说明去掉后端可能自带的「校验通过：/ 校验未通过：」前缀（页面已用粗体标题表达结论，避免重复）。 */
export function stripCheckPrefix(message: string): string {
  return message.replace(/^\s*校验(?:未)?通过\s*[：:]\s*/, '').trim()
}

export type ExplanationKind = 'cross' | 'transfer' | 'cash' | 'order'

export interface Explanation {
  kind: ExplanationKind
  /** 仅 cross：「Investment 30.00 h : Australian 10.00 h」。 */
  pairs?: string
  /** 仅 cross：「 = 3 : 1」（没有易读比例时为空串）。 */
  ratioPart?: string
  /** 仅 cross：「15.00 / 5.00」。 */
  allocated?: string
}

/** 抽屉顶部蓝色「怎么分的」说明的数据：跨公司按工时比例分摊；其余按装入顺序；全转账 / 全现金单独说明。 */
export function buildExplanation(row: MyobPreviewRow): Explanation {
  const shares = row.detail.shares
  if (row.isCrossCompany && shares.length >= 2) {
    const names = shortCompanyNames(shares.map((share) => share.connectionName))
    return {
      kind: 'cross',
      pairs: shares.map((share, index) => `${names[index] || share.connectionName} ${formatHours(share.actualHours)} h`).join(' : '),
      ratioPart: (() => {
        const ratio = formatRatio(shares.map((share) => share.actualHours))
        return ratio ? ` = ${ratio}` : ''
      })(),
      allocated: shares.map((share) => formatHours(share.allocatedTransferHours)).join(' / '),
    }
  }
  if (row.payMode === 'Transfer') return { kind: 'transfer' }
  if (row.payMode === 'Cash') return { kind: 'cash' }
  return { kind: 'order' }
}

export interface DrawerSubtitle {
  /** 公司缩写 + 周期 + 发放方式，各段用 · 连接（页面拼接）。 */
  company: string
  period: string
  payModeKey: string | null
  payModeValues: Record<string, string>
}

/** 抽屉副标题：「Investment · 2026-10-26 ~ 11-01 · 转账 + 现金（固定转账 20h/周，本公司分到 15.00 h）」。 */
export function buildDrawerSubtitle(row: MyobPreviewRow, preview: Pick<MyobTimesheetPreview, 'connectionName' | 'periodStart' | 'periodEnd'>): DrawerSubtitle {
  const names = row.detail.shares.length >= 2 ? row.detail.shares.map((share) => share.connectionName) : [preview.connectionName]
  const shortNames = shortCompanyNames(names)
  const own = row.detail.shares.length >= 2 ? row.detail.shares.findIndex((share) => share.connectionName === preview.connectionName) : 0
  let payModeKey: string | null = null
  if (row.payMode === 'Mixed') payModeKey = 'myob.push.drawer.subtitle.mixed'
  else if (row.payMode === 'Transfer') payModeKey = 'myob.push.drawer.subtitle.transfer'
  else if (row.payMode === 'Cash') payModeKey = 'myob.push.drawer.subtitle.cash'
  return {
    company: shortNames[own >= 0 ? own : 0] || preview.connectionName,
    period: formatPeriodRange(preview.periodStart, preview.periodEnd, true),
    payModeKey,
    payModeValues: {
      fixed: formatFixedHours(row.fixedTransferHoursPerWeek),
      allocated: formatHours(row.allocatedTransferHours),
    },
  }
}

// ───────────────────────── 导出预览 CSV ─────────────────────────

export type CsvText = (key: string, values?: Record<string, string | number>) => string

/** 文本以 = + - @ 制表符 回车 开头时前补单引号，防止 Excel 当公式执行。 */
export function guardCsvFormula(value: string): string {
  return /^[=+\-@\t\r]/.test(value) ? `'${value}` : value
}

/** RFC 4180：含逗号、双引号、回车或换行时加双引号并把内部双引号翻倍。 */
export function escapeCsvField(value: string): string {
  return /[",\r\n]/.test(value) ? `"${value.replace(/"/g, '""')}"` : value
}

function csvTextCell(value: string): string {
  return escapeCsvField(guardCsvFormula(value))
}

export const CSV_BOM = '﻿'

/** 预览 CSV：UTF-8 带 BOM（Excel 直接打开不乱码），CRLF 换行；文本列防公式注入，工时列直接输出两位小数。 */
export function buildPreviewCsv(preview: MyobTimesheetPreview, tx: CsvText): string {
  const header = [
    tx('myob.push.csv.employee'),
    tx('myob.push.csv.stores'),
    tx('myob.push.csv.payMode'),
    tx('myob.push.csv.weekday'),
    tx('myob.push.csv.saturday'),
    tx('myob.push.csv.sunday'),
    tx('myob.push.csv.personalLeave'),
    tx('myob.push.csv.holidayLeave'),
    tx('myob.push.csv.pushedTotal'),
    tx('myob.push.csv.cashOut'),
    tx('myob.push.csv.status'),
    tx('myob.push.csv.reason'),
    tx('myob.push.csv.myobCard'),
  ].map(csvTextCell)

  const lines = [header.join(',')]
  for (const row of preview.rows) {
    const view = getRowStatusView(row)
    const tag = getPayModeTag(row)
    const reason =
      row.status === 'Blocked'
        ? row.blockers.map((item) => item.message || tx(getBlockerLabelKey(item.code))).join('；')
        : ''
    const cells = [
      csvTextCell(row.userName),
      csvTextCell(row.storeLabels.join(' / ')),
      csvTextCell(tag ? tx(tag.labelKey, { hours: tag.hours ?? '' }) : ''),
      formatHours(row.pushed.weekday),
      formatHours(row.pushed.saturday),
      formatHours(row.pushed.sunday),
      formatHours(row.pushed.personalLeave),
      formatHours(row.pushed.holidayLeave),
      formatHours(row.pushedTotal),
      formatHours(row.cashOutHours),
      csvTextCell(tx(view.labelKey)),
      csvTextCell(reason),
      csvTextCell(row.myobName ?? ''),
    ]
    lines.push(cells.join(','))
  }
  return `${CSV_BOM}${lines.join('\r\n')}\r\n`
}

/** 导出文件名：公司名只保留字母数字，避免文件系统不认的字符；日期用周期起始日。 */
export function buildPreviewCsvFileName(connectionName: string, periodStart: string): string {
  const company = connectionName.replace(/[^A-Za-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'company'
  return `myob-timesheet-preview-${company}-${periodStart || 'period'}.csv`
}
