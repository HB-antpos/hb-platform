// 「从 MYOB 同步资料到我方」抽屉的纯逻辑：字段勾选、范围、预览分组 / 筛选 / 统计、apply 请求、结果与失败文案。
// 不依赖 React / i18n 实例：需要文案的地方只返回 key，由组件翻译；这样可以直接在 node 下做单元测试。

import type {
  MyobSyncApplyEmployeeItem,
  MyobSyncApplyRequest,
  MyobSyncApplyResult,
  MyobSyncBatch,
  MyobSyncChange,
  MyobSyncEmployee,
  MyobSyncFailure,
  MyobSyncField,
  MyobSyncFieldStat,
  MyobSyncWritten,
} from '../../../types/myob'

// ───────────────────────── 字段 ─────────────────────────

/** 设计稿里的两个字段组；组内顺序与后端 MyobSyncFields.All 一致。 */
export const SYNC_FIELD_GROUPS: readonly { key: 'basic' | 'employment'; fields: readonly MyobSyncField[] }[] = [
  { key: 'basic', fields: ['name', 'phone', 'email', 'birthday', 'gender', 'address'] },
  { key: 'employment', fields: ['employmentType', 'bank', 'super'] },
]

/** 敏感字段（走「敏感资料直改」）：与后端 MyobSyncFields.IsSensitive 一致。 */
const SENSITIVE_FIELDS: ReadonlySet<string> = new Set(['birthday', 'bank', 'super'])

export function isSensitiveField(field: string): boolean {
  return SENSITIVE_FIELDS.has(field)
}

export function syncFieldLabelKey(field: string): string {
  return `myob.sync.fields.${field}`
}

export function syncFieldSourceKey(field: string): string {
  return `myob.sync.sources.${field}`
}

export interface SyncStatGroup {
  key: 'basic' | 'employment'
  stats: MyobSyncFieldStat[]
}

/** 把 field-stats 按设计稿分成两组；后端新增、前端不认识的字段并入最后一组，不丢。 */
export function groupFieldStats(stats: readonly MyobSyncFieldStat[]): SyncStatGroup[] {
  const known = new Set(SYNC_FIELD_GROUPS.flatMap((group) => group.fields as readonly string[]))
  const groups = SYNC_FIELD_GROUPS.map((group) => ({
    key: group.key,
    stats: group.fields.map((field) => stats.find((item) => item.field === field)).filter((item): item is MyobSyncFieldStat => !!item),
  }))
  groups[groups.length - 1].stats.push(...stats.filter((item) => !known.has(item.field)))
  return groups.filter((group) => group.stats.length > 0)
}

/** 字段在 MYOB 里读不到（如付款详情没权限）：灰显、默认不勾、不可选。 */
export function isFieldUnavailable(field: string, unavailable: readonly string[]): boolean {
  return unavailable.includes(field)
}

/** 默认勾选 = 后端 defaultSelected（敏感字段与邮箱默认不勾）；读不到的字段一律不勾。 */
export function getDefaultSelectedFields(stats: readonly MyobSyncFieldStat[], unavailable: readonly string[] = []): MyobSyncField[] {
  return stats.filter((item) => item.defaultSelected && !isFieldUnavailable(item.field, unavailable)).map((item) => item.field)
}

export function selectAllFields(stats: readonly MyobSyncFieldStat[], unavailable: readonly string[] = []): MyobSyncField[] {
  return stats.filter((item) => !isFieldUnavailable(item.field, unavailable)).map((item) => item.field)
}

/** 「只选有差异的」：补空或不同至少一人。 */
export function selectDiffFields(stats: readonly MyobSyncFieldStat[], unavailable: readonly string[] = []): MyobSyncField[] {
  return stats
    .filter((item) => item.fillable + item.differs > 0 && !isFieldUnavailable(item.field, unavailable))
    .map((item) => item.field)
}

/** 已选字段数与其中敏感字段数（底栏「已选 X 个字段（含 Y 个敏感）」）；读不到的字段即使在集合里也不计。 */
export function countSelectedFields(
  stats: readonly MyobSyncFieldStat[],
  selected: ReadonlySet<MyobSyncField>,
  unavailable: readonly string[] = [],
): { fieldCount: number; sensitiveCount: number } {
  const picked = stats.filter((item) => selected.has(item.field) && !isFieldUnavailable(item.field, unavailable))
  return { fieldCount: picked.length, sensitiveCount: picked.filter((item) => item.sensitive || isSensitiveField(item.field)).length }
}

/** 按 stats 的顺序输出已选字段（请求里的顺序稳定，与后端展示顺序一致）；读不到的字段不会进请求。 */
export function listSelectedFields(
  stats: readonly MyobSyncFieldStat[],
  selected: ReadonlySet<MyobSyncField>,
  unavailable: readonly string[] = [],
): MyobSyncField[] {
  return stats.filter((item) => selected.has(item.field) && !isFieldUnavailable(item.field, unavailable)).map((item) => item.field)
}

// ───────────────────────── 范围 ─────────────────────────

export type SyncScopeKind = 'selected' | 'all'

/** 范围选项：列表勾选了已绑定员工才有「只同步勾选的」；单人入口范围固定、不让选。 */
export function getScopeOptions(selectedCount: number, locked: boolean): SyncScopeKind[] {
  if (locked) {
    return ['selected']
  }
  return selectedCount > 0 ? ['selected', 'all'] : ['all']
}

/** 默认范围：有勾选 / 单人入口时用勾选，否则全部。 */
export function getDefaultScope(selectedCount: number): SyncScopeKind {
  return selectedCount > 0 ? 'selected' : 'all'
}

/** 请求里的 userGuids：全部 = 省略（后端按「该公司文件下全部已绑定员工」处理）。 */
export function resolveScopeGuids(kind: SyncScopeKind, selectedGuids: readonly string[]): string[] | undefined {
  return kind === 'selected' && selectedGuids.length > 0 ? [...selectedGuids] : undefined
}

/** 「列表里勾了 Aidan、Ella、Kathryn」：最多列 3 个名字，其余折成「等」。 */
export function summarizeNames(names: readonly string[], limit = 3): { shown: string[]; more: number } {
  return { shown: names.slice(0, limit), more: Math.max(0, names.length - limit) }
}

// ───────────────────────── 时间 ─────────────────────────

const TIMEZONE_SUFFIX = /(?:Z|[+-]\d{2}:?\d{2})$/i

/** 后端时间是 UTC，JSON 里可能不带 Z；补上后转毫秒，解析失败返回 null。 */
export function toUtcMillis(value: string | null | undefined): number | null {
  if (!value || !value.trim()) {
    return null
  }
  const trimmed = value.trim().replace(' ', 'T')
  const millis = Date.parse(TIMEZONE_SUFFIX.test(trimmed) ? trimmed : `${trimmed}Z`)
  return Number.isNaN(millis) ? null : millis
}

/** 「MYOB 数据拉取于 N 分钟前」：返回 i18n key 与数量。 */
export function describeFetchAge(fetchedAt: string | null | undefined, now: number): { key: string; count: number } | null {
  const millis = toUtcMillis(fetchedAt)
  if (millis === null) {
    return null
  }
  const minutes = Math.max(0, Math.floor((now - millis) / 60000))
  if (minutes < 1) {
    return { key: 'myob.sync.age.justNow', count: 0 }
  }
  if (minutes < 60) {
    return { key: 'myob.sync.age.minutes', count: minutes }
  }
  const hours = Math.floor(minutes / 60)
  return hours < 24 ? { key: 'myob.sync.age.hours', count: hours } : { key: 'myob.sync.age.days', count: Math.floor(hours / 24) }
}

// ───────────────────────── 第 2 步：预览 ─────────────────────────

export type SyncPreviewFilter = 'all' | 'overwrite' | 'sensitive'

export const SYNC_PREVIEW_FILTERS: readonly SyncPreviewFilter[] = ['all', 'overwrite', 'sensitive']

/** 勾选状态的键：员工 + 字段。 */
export function changeKey(userGuid: string, field: string): string {
  return `${userGuid}|${field}`
}

export type SyncCheckedMap = Readonly<Record<string, boolean>>

/** 勾选初值 = 后端 defaultChecked（补空恒勾，覆盖项仅「以 MYOB 为准」才勾）。 */
export function buildInitialChecked(employees: readonly MyobSyncEmployee[]): Record<string, boolean> {
  const checked: Record<string, boolean> = {}
  for (const employee of employees) {
    for (const change of employee.changes) {
      checked[changeKey(employee.userGuid, change.field)] = change.defaultChecked
    }
  }
  return checked
}

function matchesFilter(change: MyobSyncChange, filter: SyncPreviewFilter): boolean {
  if (filter === 'overwrite') {
    return change.kind === 'Overwrite'
  }
  if (filter === 'sensitive') {
    return change.sensitive || isSensitiveField(change.field)
  }
  return true
}

/** 三个分段的条数：全部变更 / 仅有差异（覆盖）/ 仅敏感。 */
export function countByFilter(employees: readonly MyobSyncEmployee[]): Record<SyncPreviewFilter, number> {
  const counts: Record<SyncPreviewFilter, number> = { all: 0, overwrite: 0, sensitive: 0 }
  for (const employee of employees) {
    for (const change of employee.changes) {
      counts.all += 1
      if (matchesFilter(change, 'overwrite')) counts.overwrite += 1
      if (matchesFilter(change, 'sensitive')) counts.sensitive += 1
    }
  }
  return counts
}

export interface SyncPreviewRow {
  key: string
  employee: MyobSyncEmployee
  change: MyobSyncChange
  /** 员工列合并行：该员工第一行 = 本员工可见行数，其余行 = 0（antd Table 的 rowSpan 语义）。 */
  rowSpan: number
}

/** 按筛选展开成表格行：没有可见变更的员工整个不出现；员工顺序沿用后端（按姓名升序）。 */
export function buildPreviewRows(employees: readonly MyobSyncEmployee[], filter: SyncPreviewFilter): SyncPreviewRow[] {
  const rows: SyncPreviewRow[] = []
  for (const employee of employees) {
    const visible = employee.changes.filter((change) => matchesFilter(change, filter))
    visible.forEach((change, index) => {
      rows.push({ key: changeKey(employee.userGuid, change.field), employee, change, rowSpan: index === 0 ? visible.length : 0 })
    })
  }
  return rows
}

export interface SyncCheckedSummary {
  /** 已勾选的变更项数（不受当前筛选影响：筛选只影响显示，不影响将要写入什么）。 */
  changeCount: number
  /** 至少勾了一项的员工数。 */
  employeeCount: number
  sensitiveCount: number
  overwriteCount: number
}

export function summarizeChecked(employees: readonly MyobSyncEmployee[], checked: SyncCheckedMap): SyncCheckedSummary {
  const summary: SyncCheckedSummary = { changeCount: 0, employeeCount: 0, sensitiveCount: 0, overwriteCount: 0 }
  for (const employee of employees) {
    const picked = employee.changes.filter((change) => checked[changeKey(employee.userGuid, change.field)])
    if (picked.length === 0) {
      continue
    }
    summary.employeeCount += 1
    summary.changeCount += picked.length
    summary.sensitiveCount += picked.filter((change) => matchesFilter(change, 'sensitive')).length
    summary.overwriteCount += picked.filter((change) => change.kind === 'Overwrite').length
  }
  return summary
}

/** 「全部勾选补空项」：只动补空项（含敏感补空），覆盖项保持原勾选状态。 */
export function checkAllFill(employees: readonly MyobSyncEmployee[], checked: SyncCheckedMap): Record<string, boolean> {
  const next = { ...checked }
  for (const employee of employees) {
    for (const change of employee.changes) {
      if (change.kind === 'Fill') {
        next[changeKey(employee.userGuid, change.field)] = true
      }
    }
  }
  return next
}

/** 「取消全部」。 */
export function uncheckAll(checked: SyncCheckedMap): Record<string, boolean> {
  return Object.fromEntries(Object.keys(checked).map((key) => [key, false]))
}

/** 本次预览里「有待审敏感申请、且含敏感字段变更」的员工（敏感提示条点名；写入敏感字段会让其申请作废）。 */
export function listPendingSensitiveEmployees(employees: readonly MyobSyncEmployee[]): MyobSyncEmployee[] {
  return employees.filter(
    (employee) => employee.hasPendingSensitiveRequest && employee.changes.some((change) => matchesFilter(change, 'sensitive')),
  )
}

/** 已勾选的敏感项里，哪些员工会作废待审申请（决定 confirmSupersedePendingSensitiveRequests）。 */
export function listSupersededEmployees(employees: readonly MyobSyncEmployee[], checked: SyncCheckedMap): MyobSyncEmployee[] {
  return employees.filter(
    (employee) =>
      employee.hasPendingSensitiveRequest &&
      employee.changes.some((change) => matchesFilter(change, 'sensitive') && checked[changeKey(employee.userGuid, change.field)]),
  )
}

/** 能不能提交：至少勾一项；勾了敏感项必须先勾「我已核对敏感字段」。 */
export function canSubmitSync(summary: SyncCheckedSummary, acknowledged: boolean): boolean {
  return summary.changeCount > 0 && (summary.sensitiveCount === 0 || acknowledged)
}

/**
 * 构造 apply 请求：只带至少勾了一项的员工与被勾选的变更（字段 + 两个哈希，值由服务端重取）；
 * 员工的 expectedSensitiveRevision 取预览时的 sensitiveRevision；勾了敏感项且员工有待审申请 → confirmSupersede = true。
 * 没有任何勾选时返回 null。
 */
export function buildApplyRequest(
  connectionGuid: string,
  employees: readonly MyobSyncEmployee[],
  checked: SyncCheckedMap,
): MyobSyncApplyRequest | null {
  const items: MyobSyncApplyEmployeeItem[] = []
  for (const employee of employees) {
    const changes = employee.changes
      .filter((change) => checked[changeKey(employee.userGuid, change.field)])
      .map((change) => ({ field: change.field, ourHash: change.ourHash, myobHash: change.myobHash }))
    if (changes.length > 0) {
      items.push({ userGuid: employee.userGuid, expectedSensitiveRevision: employee.sensitiveRevision, changes })
    }
  }
  if (items.length === 0) {
    return null
  }
  return {
    connectionGuid,
    items,
    confirmSupersedePendingSensitiveRequests: listSupersededEmployees(employees, checked).length > 0,
  }
}

// ───────────────────────── 第 3 步：结果 ─────────────────────────

/** 错误码 → i18n key（逐员工失败原因 + 整次请求失败原因）；未知码返回 null，由调用方退回后端 message。 */
const ERROR_KEYS: Readonly<Record<string, string>> = {
  MYOB_SYNC_NO_PERMISSION: 'myob.sync.errors.noPermission',
  MYOB_SYNC_NO_FIELDS: 'myob.sync.errors.noFields',
  MYOB_SYNC_BATCH_NOT_FOUND: 'myob.sync.errors.batchNotFound',
  MYOB_SYNC_BATCH_ALREADY_UNDONE: 'myob.sync.errors.batchAlreadyUndone',
  MYOB_SYNC_BATCH_EXPIRED: 'myob.sync.errors.batchExpired',
  MYOB_SYNC_NOT_BOUND: 'myob.sync.errors.notBound',
  MYOB_SYNC_CARD_MISSING: 'myob.sync.errors.cardMissing',
  MYOB_SYNC_VALUE_CHANGED: 'myob.sync.errors.valueChanged',
  MYOB_SYNC_VERSION_CONFLICT: 'myob.sync.errors.versionConflict',
  MYOB_SYNC_PENDING_NEEDS_CONFIRM: 'myob.sync.errors.pendingNeedsConfirm',
  MYOB_SYNC_WRITE_FAILED: 'myob.sync.errors.writeFailed',
}

export function getSyncErrorMessageKey(code: string | null | undefined): string | null {
  return (code && ERROR_KEYS[code]) || null
}

/** 失败行文案：优先后端 message（带具体原因），没有则按错误码取本地文案，再没有返回 null。 */
export function describeSyncFailure(failure: MyobSyncFailure): { text: string | null; messageKey: string | null } {
  const text = failure.message.trim()
  return { text: text || null, messageKey: text ? null : getSyncErrorMessageKey(failure.code) }
}

export interface SplitWritten {
  head: MyobSyncWritten[]
  rest: MyobSyncWritten[]
  restChangeCount: number
}

/** 已写入员工表默认只显示前 N 人，其余折叠（「其余 5 人 · 11 项已写入 展开全部」）。 */
export function splitWritten(written: readonly MyobSyncWritten[], limit = 5): SplitWritten {
  const rest = written.slice(limit)
  return {
    head: written.slice(0, limit),
    rest,
    restChangeCount: rest.reduce((sum, item) => sum + item.fields.length, 0),
  }
}

export function countFailedChanges(failed: readonly MyobSyncFailure[]): number {
  return failed.reduce((sum, item) => sum + item.fields.length, 0)
}

/** 「重新预览这 1 人」：范围限定为该员工，字段沿用该员工没写入的字段。 */
export function buildRescope(failure: MyobSyncFailure): { userGuids: string[]; fields: MyobSyncField[] } {
  return { userGuids: [failure.userGuid], fields: [...failure.fields] }
}

/** 撤销卡片：有批次、有截止时间且还没过期才显示（全部失败没有 batchGuid → 不显示）。 */
export function isUndoAvailable(result: Pick<MyobSyncApplyResult, 'batchGuid' | 'undoDeadline'>, now: number): boolean {
  if (!result.batchGuid) {
    return false
  }
  const deadline = toUtcMillis(result.undoDeadline)
  return deadline !== null && deadline > now
}

/** 同步记录里一行的状态：可撤销 / 已撤销 / 已过期（以服务端 canUndo + 截止时间为准，本地时间只做兜底）。 */
export function getBatchStatusKey(batch: MyobSyncBatch, now: number): 'Active' | 'Undone' | 'Expired' {
  if (batch.status === 'Undone') {
    return 'Undone'
  }
  if (batch.status === 'Expired') {
    return 'Expired'
  }
  const deadline = toUtcMillis(batch.undoDeadline)
  return deadline !== null && deadline <= now ? 'Expired' : 'Active'
}
