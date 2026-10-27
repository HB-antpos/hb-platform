// MYOB 阶段 1（员工绑定 + 发薪公司）的纯逻辑：筛选、搜索、徽章映射、批量条数、对比结果、生效日期处理等。
// 不依赖 React / antd，便于单元测试；文案键一律写成字面量，供页面文案契约测试扫描。

import type {
  MyobBindingCounts,
  MyobBindingRow,
  MyobBindingStatus,
  MyobConfirmBindingItem,
  MyobConfirmFailure,
  MyobEmployeeCard,
  MyobMatchBasis,
  MyobMatchCandidate,
  MyobPayrollBindingState,
  MyobPayrollEmployerDetail,
  MyobPayrollEmployerRow,
  MyobSavePayrollEmployerItem,
  MyobSignalResult,
} from '../../../types/myob'
import { formatMyobTime } from './myobLogic'

// ───────────────────────── 页签 / 权限 ─────────────────────────

export type MyobTabKey = 'connections' | 'employees' | 'payroll'

export interface MyobTabAccess {
  canManageConnections: boolean
  canBindEmployees: boolean
  canEditPayroll: boolean
}

/** 有权限的页签（按固定顺序）；没有权限的页签不渲染。 */
export function getVisibleMyobTabs(access: MyobTabAccess): MyobTabKey[] {
  const tabs: MyobTabKey[] = []
  if (access.canManageConnections) tabs.push('connections')
  if (access.canBindEmployees) tabs.push('employees')
  if (access.canEditPayroll) tabs.push('payroll')
  return tabs
}

/** 当前选中的页签不可见（权限变化 / 参数非法）时回落到第一个可见页签；全都没有返回 null。 */
export function resolveActiveMyobTab(requested: string | null | undefined, visible: MyobTabKey[]): MyobTabKey | null {
  const found = visible.find((tab) => tab === requested)
  return found ?? visible[0] ?? null
}

// ───────────────────────── 绑定列表：筛选 / 搜索 / 计数 ─────────────────────────

export type BindingFilter = 'all' | 'bound' | 'suggested' | 'review' | 'unmatched'

export const BINDING_FILTERS: readonly BindingFilter[] = ['all', 'bound', 'suggested', 'review', 'unmatched']

/** 筛选 chip 的文案键（字面量）。 */
export const BINDING_FILTER_LABEL_KEYS: Readonly<Record<BindingFilter, string>> = {
  all: 'myob.employees.filters.all',
  bound: 'myob.employees.filters.bound',
  suggested: 'myob.employees.filters.suggested',
  review: 'myob.employees.filters.review',
  unmatched: 'myob.employees.filters.unmatched',
}

/** chip 上的计数取自后端 counts（review = 需人工确认 + 冲突）。 */
export function getFilterCount(counts: MyobBindingCounts, filter: BindingFilter): number {
  switch (filter) {
    case 'all':
      return counts.all
    case 'bound':
      return counts.bound
    case 'suggested':
      return counts.suggested
    case 'review':
      return counts.needsReview
    case 'unmatched':
      return counts.unmatched
  }
}

function matchesFilter(status: MyobBindingStatus, filter: BindingFilter): boolean {
  switch (filter) {
    case 'all':
      return true
    case 'bound':
      return status === 'Bound'
    case 'suggested':
      return status === 'Suggested'
    case 'review':
      return status === 'NeedsReview' || status === 'Conflict'
    case 'unmatched':
      return status === 'Unmatched'
  }
}

/** 纯前端搜索：姓名 / 邮箱 / 分店（以及已绑定或候选的 MYOB 姓名、邮箱，方便从对方名字反查）；大小写不敏感。 */
export function matchesBindingKeyword(row: MyobBindingRow, keyword: string): boolean {
  const needle = keyword.trim().toLowerCase()
  if (!needle) {
    return true
  }
  const haystack = [
    row.userName,
    row.email ?? '',
    ...row.storeLabels,
    row.candidate?.card.name ?? '',
    row.candidate?.card.email ?? '',
    row.link?.myobName ?? '',
  ]
  return haystack.some((value) => value.toLowerCase().includes(needle))
}

/** 先按状态筛选再按关键字搜索；保持后端给出的排序，不重排。 */
export function filterBindingRows(rows: MyobBindingRow[], filter: BindingFilter, keyword: string): MyobBindingRow[] {
  return rows.filter((row) => matchesFilter(row.status, filter) && matchesBindingKeyword(row, keyword))
}

/**
 * 「员工绑定」页签右上角的待处理条数 = 高分建议 + 需人工确认（含冲突）+ 未匹配。
 * needsReview 已包含冲突，不能再单独加。
 */
export function getPendingBindingCount(counts: MyobBindingCounts | null | undefined): number {
  if (!counts) {
    return 0
  }
  return Math.max(0, counts.suggested + counts.needsReview + counts.unmatched)
}

/** 批量确认：只取状态为 Suggested 且带候选卡的行（manual=false，服务端会重新打分）。 */
export function buildBatchConfirmItems(rows: MyobBindingRow[]): MyobConfirmBindingItem[] {
  return rows
    .filter((row) => row.status === 'Suggested' && row.candidate !== null)
    .map((row) => ({ userGuid: row.userGuid, myobEmployeeUid: row.candidate!.card.uid, manual: false }))
}

/** 单行「确认绑定」。 */
export function buildSingleConfirmItem(row: MyobBindingRow): MyobConfirmBindingItem | null {
  return row.status === 'Suggested' && row.candidate
    ? { userGuid: row.userGuid, myobEmployeeUid: row.candidate.card.uid, manual: false }
    : null
}

/** 抽屉里手动选卡后的确认项（manual=true）。 */
export function buildManualConfirmItem(userGuid: string, card: Pick<MyobEmployeeCard, 'uid'>): MyobConfirmBindingItem {
  return { userGuid, myobEmployeeUid: card.uid, manual: true }
}

// ───────────────────────── 行操作 ─────────────────────────

export type BindingRowAction = 'confirm' | 'review' | 'change' | 'searchOther' | 'searchMyob' | 'unbind'

/** 各状态可用的操作，按展示顺序（第一项为主操作）。 */
export function getBindingRowActions(status: MyobBindingStatus): BindingRowAction[] {
  switch (status) {
    case 'Suggested':
      return ['confirm', 'change']
    case 'NeedsReview':
      return ['review', 'change']
    case 'Conflict':
      return ['searchOther']
    case 'Unmatched':
      return ['searchMyob']
    case 'Bound':
      return ['unbind']
  }
}

/** 抽屉打开时预选的候选卡 uid：只有「核对并绑定」才预选最佳候选，且这张卡必须没被别人绑定。 */
export function getPreselectedCardUid(action: BindingRowAction, row: MyobBindingRow): string | null {
  if (action !== 'review' || !row.candidate) {
    return null
  }
  return isCandidateSelectable(row.candidate) ? row.candidate.card.uid : null
}

// ───────────────────────── 状态 / 徽章 ─────────────────────────

export interface StatusBadge {
  /** antd Tag 的 color。 */
  color: 'processing' | 'warning' | 'error' | 'success' | 'default'
  labelKey: string
}

export const BINDING_STATUS_BADGES: Readonly<Record<MyobBindingStatus, StatusBadge>> = {
  Suggested: { color: 'processing', labelKey: 'myob.employees.status.Suggested' },
  NeedsReview: { color: 'warning', labelKey: 'myob.employees.status.NeedsReview' },
  Conflict: { color: 'error', labelKey: 'myob.employees.status.Conflict' },
  Unmatched: { color: 'error', labelKey: 'myob.employees.status.Unmatched' },
  Bound: { color: 'success', labelKey: 'myob.employees.status.Bound' },
}

export type BasisField = 'email' | 'birthday' | 'name' | 'phone'

/** 匹配依据四个徽章的顺序与文案键。 */
export const BASIS_FIELDS: ReadonlyArray<{ field: BasisField; labelKey: string }> = [
  { field: 'email', labelKey: 'myob.employees.basis.email' },
  { field: 'birthday', labelKey: 'myob.employees.basis.birthday' },
  { field: 'name', labelKey: 'myob.employees.basis.name' },
  { field: 'phone', labelKey: 'myob.employees.basis.phone' },
]

export interface SignalBadge {
  /** 徽章里的符号：状态不能只靠颜色，符号本身要能读。 */
  symbol: '✓' | '✕' | '–'
  tone: 'ok' | 'bad' | 'muted'
  /** 屏幕阅读器读的状态词文案键。 */
  stateKey: string
}

export function getSignalBadge(result: MyobSignalResult): SignalBadge {
  if (result === 'match') {
    return { symbol: '✓', tone: 'ok', stateKey: 'myob.employees.signal.match' }
  }
  if (result === 'mismatch') {
    return { symbol: '✕', tone: 'bad', stateKey: 'myob.employees.signal.mismatch' }
  }
  return { symbol: '–', tone: 'muted', stateKey: 'myob.employees.signal.unknown' }
}

export function getBasisResult(basis: MyobMatchBasis, field: BasisField): MyobSignalResult {
  return basis[field]
}

export type ScoreTone = 'high' | 'mid' | 'low'

/** 得分档位：≥85 高分（与后端「高分建议」阈值一致）、≥40 中分、其余低分。 */
export function getScoreTone(score: number): ScoreTone {
  if (score >= 85) return 'high'
  if (score >= 40) return 'mid'
  return 'low'
}

/** 候选卡能否被选择：已绑定给别人的不可选。 */
export function isCandidateSelectable(candidate: MyobMatchCandidate): boolean {
  return !candidate.boundToUserGuid
}

// ───────────────────────── 用工类型 ─────────────────────────

const EMPLOYEE_TYPE_KEYS: Readonly<Record<string, string>> = {
  FullTime: 'myob.employees.type.FullTime',
  PartTime: 'myob.employees.type.PartTime',
  Temporary: 'myob.employees.type.Temporary',
}

/** 用工类型文案键；资料没填 / 未知取值返回 null（调用方显示原值或不显示）。 */
export function getEmployeeTypeKey(type: string | null | undefined): string | null {
  return type ? (EMPLOYEE_TYPE_KEYS[type] ?? null) : null
}

/** 我方是否应当有年假 / 病假累计：全职、兼职应有；临时工不应有；其余未知。 */
export function expectsLeaveEntitlement(type: string | null | undefined): boolean | null {
  if (type === 'FullTime' || type === 'PartTime') return true
  if (type === 'Temporary') return false
  return null
}

/** MYOB 卡上年假 / 病假累计的描述文案键。 */
export function describeEntitlementKey(hasAnnual: boolean, hasSick: boolean): string {
  if (hasAnnual && hasSick) return 'myob.employees.entitlement.both'
  if (hasAnnual) return 'myob.employees.entitlement.annualOnly'
  if (hasSick) return 'myob.employees.entitlement.sickOnly'
  return 'myob.employees.entitlement.none'
}

const ISSUE_CODE_KEYS: Readonly<Record<string, string>> = {
  MISSING_LEAVE_ENTITLEMENT: 'myob.employees.typeIssues.codes.missingLeave',
  TEMPORARY_HAS_LEAVE_ENTITLEMENT: 'myob.employees.typeIssues.codes.temporaryHasLeave',
}

/** 问题码 → 中文问题文案键；未知码用通用文案，不把后端原始码当句子展示。 */
export function getTypeIssueKey(code: string | null | undefined): string {
  return ISSUE_CODE_KEYS[(code ?? '').trim().toUpperCase()] ?? 'myob.employees.typeIssues.codes.unknown'
}

const ISSUE_ACTION_KEYS: Readonly<Record<string, string>> = {
  MISSING_LEAVE_ENTITLEMENT: 'myob.employees.typeIssues.actions.fix.missingLeave',
  TEMPORARY_HAS_LEAVE_ENTITLEMENT: 'myob.employees.typeIssues.actions.fix.temporaryHasLeave',
}

/** 「告知财务」按钮文案键（补设 / 修改）。 */
export function getTypeIssueActionKey(code: string | null | undefined): string {
  return ISSUE_ACTION_KEYS[(code ?? '').trim().toUpperCase()] ?? 'myob.employees.typeIssues.actions.fix.generic'
}

/** 「告知财务」复制的说明文案键（带插值：name / type / stores）。 */
export function getTypeIssueNoteKey(code: string | null | undefined): string {
  const normalized = (code ?? '').trim().toUpperCase()
  if (normalized === 'MISSING_LEAVE_ENTITLEMENT') return 'myob.employees.typeIssues.notes.missingLeave'
  if (normalized === 'TEMPORARY_HAS_LEAVE_ENTITLEMENT') return 'myob.employees.typeIssues.notes.temporaryHasLeave'
  return 'myob.employees.typeIssues.notes.generic'
}

// ───────────────────────── 逐项对比（手动选卡抽屉） ─────────────────────────

export type CompareField = 'name' | 'email' | 'phone' | 'birthday' | 'type'
export type CompareResult = 'same' | 'diff' | 'unknown'

export interface CompareRowModel {
  field: CompareField
  labelKey: string
  /** 结果；用工类型用 same=相符 / diff=不符 / unknown=无法比对（文案在 resultKey 里区分）。 */
  result: CompareResult
  resultKey: string
  /** 不同时 MYOB 一侧用琥珀色高亮。 */
  highlightTheirs: boolean
}

const SIGNAL_TO_COMPARE: Readonly<Record<MyobSignalResult, CompareResult>> = {
  match: 'same',
  mismatch: 'diff',
  unknown: 'unknown',
}

const COMPARE_RESULT_KEYS: Readonly<Record<CompareResult, string>> = {
  same: 'myob.drawer.compare.same',
  diff: 'myob.drawer.compare.diff',
  unknown: 'myob.drawer.compare.unknown',
}

const COMPARE_LABEL_KEYS: Readonly<Record<CompareField, string>> = {
  name: 'myob.drawer.compare.fields.name',
  email: 'myob.drawer.compare.fields.email',
  phone: 'myob.drawer.compare.fields.phone',
  birthday: 'myob.drawer.compare.fields.birthday',
  type: 'myob.drawer.compare.fields.type',
}

/** 用工类型是否与 MYOB 假期累计相符：临时工不该有、全职兼职应该有；类型未知无法比对。 */
export function compareEmployeeType(ourType: string | null | undefined, card: MyobEmployeeCard): CompareResult {
  const expected = expectsLeaveEntitlement(ourType)
  if (expected === null) return 'unknown'
  const hasAny = card.hasAnnualEntitlement || card.hasSickEntitlement
  if (expected) {
    return card.hasAnnualEntitlement && card.hasSickEntitlement ? 'same' : 'diff'
  }
  return hasAny ? 'diff' : 'same'
}

/** 构造逐项对比的 5 行（姓名 / 邮箱 / 手机 / 生日 / 用工类型）；前四项结果直接用后端的匹配信号。 */
export function buildCompareRows(candidate: MyobMatchCandidate, ourType: string | null | undefined): CompareRowModel[] {
  const signal = (field: 'name' | 'email' | 'phone' | 'birthday'): CompareRowModel => {
    const result = SIGNAL_TO_COMPARE[candidate.basis[field]]
    return {
      field,
      labelKey: COMPARE_LABEL_KEYS[field],
      result,
      resultKey: COMPARE_RESULT_KEYS[result],
      highlightTheirs: result === 'diff',
    }
  }
  const typeResult = compareEmployeeType(ourType, candidate.card)
  const typeRow: CompareRowModel = {
    field: 'type',
    labelKey: COMPARE_LABEL_KEYS.type,
    result: typeResult,
    resultKey:
      typeResult === 'same'
        ? 'myob.drawer.compare.typeSame'
        : typeResult === 'diff'
          ? 'myob.drawer.compare.typeDiff'
          : 'myob.drawer.compare.unknown',
    highlightTheirs: typeResult === 'diff',
  }
  return [signal('name'), signal('email'), signal('phone'), signal('birthday'), typeRow]
}

export interface EvidenceWarning {
  show: boolean
  emailMismatch: boolean
  birthdayUnknown: boolean
  /** 提示文案键（字面量）；show=false 时为 null。 */
  messageKey: string | null
}

/** 证据不足（邮箱不一致或生日无法比对）时给黄色提示条。 */
export function getEvidenceWarning(basis: MyobMatchBasis): EvidenceWarning {
  const emailMismatch = basis.email === 'mismatch'
  const birthdayUnknown = basis.birthday === 'unknown'
  let messageKey: string | null = null
  if (emailMismatch && birthdayUnknown) messageKey = 'myob.drawer.warn.both'
  else if (emailMismatch) messageKey = 'myob.drawer.warn.email'
  else if (birthdayUnknown) messageKey = 'myob.drawer.warn.birthday'
  return { show: messageKey !== null, emailMismatch, birthdayUnknown, messageKey }
}

// ───────────────────────── 文本格式化 ─────────────────────────

const MONTH_ABBR = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/** yyyy-MM-dd → 「12 Mar 1996」（与 MYOB 界面一致的写法）；空 / 格式不对返回 null。 */
export function formatBirthday(value: string | null | undefined): string | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec((value ?? '').trim())
  if (!match) {
    return null
  }
  const month = Number(match[2])
  if (month < 1 || month > 12) {
    return null
  }
  return `${Number(match[3])} ${MONTH_ABBR[month - 1]} ${match[1]}`.replace(/^(\d) /, '0$1 ')
}

/** UTC 时间 → 悉尼时间「MM-dd HH:mm」（已绑定的状态格用）。 */
export function formatShortDateTime(value: string | null | undefined): string {
  const full = formatMyobTime(value)
  return full === '--' ? full : full.slice(5)
}

/** UTC 时间 → 悉尼日期「MM-dd」（入职日期用）。 */
export function formatShortDate(value: string | null | undefined): string {
  const full = formatMyobTime(value)
  return full === '--' ? full : full.slice(5, 10)
}

/** 头像里的缩写：取前两个单词的首字母（大写）；空串给「?」。 */
export function getInitials(name: string | null | undefined): string {
  const parts = (name ?? '').trim().split(/\s+/).filter(Boolean)
  if (parts.length === 0) return '?'
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase()
  return (parts[0][0] + parts[1][0]).toUpperCase()
}

/** 「邮箱 · 分店」副标题；缺哪项就略过哪项，都没有给「—」。 */
export function joinSubtitle(parts: Array<string | null | undefined>): string {
  const filled = parts.map((part) => (part ?? '').trim()).filter(Boolean)
  return filled.length > 0 ? filled.join(' · ') : '—'
}

// ───────────────────────── 确认结果（部分失败） ─────────────────────────

const BIND_FAILURE_KEYS: Readonly<Record<string, string>> = {
  MYOB_BIND_USER_NOT_FOUND: 'myob.employees.failures.userNotFound',
  MYOB_BIND_CARD_NOT_FOUND: 'myob.employees.failures.cardNotFound',
  MYOB_BIND_CARD_ALREADY_BOUND: 'myob.employees.failures.cardAlreadyBound',
  MYOB_BIND_USER_ALREADY_BOUND: 'myob.employees.failures.userAlreadyBound',
  MYOB_BIND_NOT_HIGH_SCORE: 'myob.employees.failures.notHighScore',
  MYOB_LINK_NOT_FOUND: 'myob.employees.failures.linkNotFound',
}

/** 绑定失败码 → 文案键；未知码返回 null，调用方退回展示后端 message。 */
export function getBindFailureKey(code: string | null | undefined): string | null {
  return BIND_FAILURE_KEYS[(code ?? '').trim().toUpperCase()] ?? null
}

export interface BindFailureLine {
  userGuid: string
  userName: string
  /** 文案键；null 时用 fallbackMessage。 */
  messageKey: string | null
  fallbackMessage: string
}

/** 把部分失败的结果逐条整理成「员工姓名 + 原因」，找不到姓名时用 userGuid。 */
export function describeBindFailures(
  failed: MyobConfirmFailure[],
  rows: Array<Pick<MyobBindingRow, 'userGuid' | 'userName'>>,
): BindFailureLine[] {
  const nameByGuid = new Map(rows.map((row) => [row.userGuid, row.userName]))
  return failed.map((failure) => ({
    userGuid: failure.userGuid,
    userName: nameByGuid.get(failure.userGuid) || failure.userGuid,
    messageKey: getBindFailureKey(failure.code),
    fallbackMessage: failure.message,
  }))
}

// ───────────────────────── 发薪公司 ─────────────────────────

const ISO_DATE = /^\d{4}-\d{2}-\d{2}$/

/** 可选生效日期：只保留 yyyy-MM-dd、去重、升序；第一项为当前期。 */
export function normalizeAllowedEffectiveFrom(values: string[] | null | undefined): string[] {
  const unique = new Set((values ?? []).filter((value) => ISO_DATE.test(value)))
  return [...unique].sort()
}

/** 默认生效日期 = 可选列表第一项（当前期）；没有可选项返回 null（此时不能保存）。 */
export function pickDefaultEffectiveFrom(allowed: string[]): string | null {
  return normalizeAllowedEffectiveFrom(allowed)[0] ?? null
}

/** 当前选择是否仍在可选范围内（详情刷新后可选期会滚动）。 */
export function resolveEffectiveFrom(current: string | null, allowed: string[]): string | null {
  const normalized = normalizeAllowedEffectiveFrom(allowed)
  return current && normalized.includes(current) ? current : (normalized[0] ?? null)
}

/** 发薪公司草稿：分店编号 → 覆盖到的公司 guid；null = 按分店所属公司。 */
export type PayrollDraft = Record<string, string | null>

/** 从详情构造初始草稿：当前有覆盖的用覆盖公司，否则 null。 */
export function buildPayrollDraft(detail: MyobPayrollEmployerDetail): PayrollDraft {
  const draft: PayrollDraft = {}
  for (const row of detail.rows) {
    draft[row.storeCode] = row.isOverride ? row.effectiveConnectionGuid : null
  }
  return draft
}

/**
 * 草稿是否有改动：对比每个分店当前的覆盖状态；
 * 另外若所选生效日期不是当前期，也要允许保存（把已有覆盖挪到下一期）——这里只看公司选择是否变化，生效日期由调用方单独判断。
 */
export function hasPayrollDraftChanges(detail: MyobPayrollEmployerDetail, draft: PayrollDraft): boolean {
  const initial = buildPayrollDraft(detail)
  return detail.rows.some((row) => (draft[row.storeCode] ?? null) !== (initial[row.storeCode] ?? null))
}

/**
 * 保存请求的 items：只带「相对当前状态有改动」的分店（null = 改回按分店）。
 * 不能把未改动的分店也带上：它们会被当成「从所选生效日起」的新设置，
 * 若所选生效日晚于它们现有覆盖的起始日，现有覆盖会被误挪到后面（当前期反而回到默认公司）。
 */
export function buildPayrollSaveItems(detail: MyobPayrollEmployerDetail, draft: PayrollDraft): MyobSavePayrollEmployerItem[] {
  const initial = buildPayrollDraft(detail)
  return detail.rows
    .filter((row) => (draft[row.storeCode] ?? null) !== (initial[row.storeCode] ?? null))
    .map((row) => ({ storeCode: row.storeCode, connectionGuid: draft[row.storeCode] ?? null }))
}

/** 草稿里指定了发薪公司、但该公司文件里还没有绑定这个员工的公司 guid（去重，保持出现顺序）。 */
export function findUnboundPayrollCompanies(draft: PayrollDraft, states: MyobPayrollBindingState[]): string[] {
  const boundByGuid = new Map(states.map((state) => [state.connectionGuid, state.bound]))
  const result: string[] = []
  for (const guid of Object.values(draft)) {
    if (guid && boundByGuid.get(guid) === false && !result.includes(guid)) {
      result.push(guid)
    }
  }
  return result
}

export interface PayrollEmployerCell {
  name: string
  /** override = 例外（黄色）；default = 按分店（绿色）。 */
  kind: 'override' | 'default'
}

/** 列表「本期实际发薪公司」单元格：没有生效公司（分店未映射）用空名，由页面显示「未设置」。 */
export function describePayrollEmployer(row: MyobPayrollEmployerRow): PayrollEmployerCell {
  return {
    name: row.effectiveConnectionName ?? row.defaultConnectionName ?? '',
    kind: row.isOverride ? 'override' : 'default',
  }
}

/** 上班分店展示：「1013 Orion」；没有名称只显示编号。 */
export function formatStoreLabel(storeCode: string, storeName: string): string {
  const name = storeName.trim()
  return name && name !== storeCode ? `${storeCode} ${name}` : storeCode
}

/** 列表中同一员工的其他上班分店（用于「另有 1025（Australian）」副标题）。 */
export function getOtherStoresOfUser(
  rows: MyobPayrollEmployerRow[],
  current: MyobPayrollEmployerRow,
): Array<{ label: string; companyName: string }> {
  return rows
    .filter((row) => row.userGuid === current.userGuid && row.storeCode !== current.storeCode)
    .map((row) => ({
      label: row.storeCode,
      companyName: row.effectiveConnectionName ?? row.defaultConnectionName ?? '',
    }))
}
