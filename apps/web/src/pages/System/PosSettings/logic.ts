import type {
  PosSettingChange,
  PosSettingDefinition,
  PosSettingEffectiveValue,
  PosSettingStoreSummary,
} from '../../../types/posSettings'

// 收银端设置页的纯逻辑：三态合并显示、变更差异、设备应用状态、错误归类、时间格式。
// 不依赖 React / antd，便于单测。

export const POS_SETTINGS_PATH = '/system/pos-settings'
export const EMPTY_CELL = '—'

// ---------------------------------------------------------------------------
// 三态：每一层的每一项都是「未设置 / 跟随上一层」「开」「关」三选一
// ---------------------------------------------------------------------------

/** inherit：这一层没设置（全局 = 未设置、门店 = 跟随全局、设备 = 跟随门店）。 */
export type TriState = 'inherit' | 'on' | 'off'
export type SettingLayer = 'global' | 'store' | 'device'
export type SettingDraft = Record<string, TriState>

export const TRI_STATES: readonly TriState[] = ['inherit', 'on', 'off']

/** 值字典里的值 → 三态：没出现（undefined / null）= inherit。 */
export function triOf(value: boolean | null | undefined): TriState {
  if (value === true) return 'on'
  if (value === false) return 'off'
  return 'inherit'
}

/** 三态 → 保存请求里的值：inherit = null（清除这一项）。 */
export function triToValue(tri: TriState): boolean | null {
  if (tri === 'on') return true
  if (tri === 'off') return false
  return null
}

/** 按目录里的每一项生成草稿；目录之外的键（旧数据、已下线的项）不展示也不修改。 */
export function buildDraft(
  definitions: readonly PosSettingDefinition[],
  values: Readonly<Record<string, boolean>> | null | undefined,
): SettingDraft {
  const draft: SettingDraft = {}
  for (const definition of definitions) draft[definition.key] = triOf(values?.[definition.key])
  return draft
}

export interface SettingChange {
  key: string
  from: TriState
  to: TriState
}

/** 草稿相对原值的变更列表，顺序与目录一致；没改的项不出现。 */
export function diffDraft(
  definitions: readonly PosSettingDefinition[],
  original: Readonly<SettingDraft>,
  draft: Readonly<SettingDraft>,
): SettingChange[] {
  const changes: SettingChange[] = []
  for (const definition of definitions) {
    const from = original[definition.key] ?? 'inherit'
    const to = draft[definition.key] ?? 'inherit'
    if (from !== to) changes.push({ key: definition.key, from, to })
  }
  return changes
}

/** 保存请求体：只带改动的项（true / false 设置，null 清除），没出现的项服务端保持不变。 */
export function buildSaveValues(changes: readonly SettingChange[]): Record<string, boolean | null> {
  const values: Record<string, boolean | null> = {}
  for (const change of changes) values[change.key] = triToValue(change.to)
  return values
}

// ---------------------------------------------------------------------------
// 生效值：设备 → 门店 → 全局 → 缺省 → 本机，越具体越优先
// ---------------------------------------------------------------------------

export interface EffectiveValue {
  value: boolean | null
  /** global / store / device / default / none。 */
  source: string
}

/** 全局层这一项的生效值：全局设置过用全局；否则有服务端缺省用缺省；否则沿用本机（value = null）。 */
export function globalEffective(
  definition: PosSettingDefinition,
  globalValues: Readonly<Record<string, boolean>> | null | undefined,
): EffectiveValue {
  const own = globalValues?.[definition.key]
  if (typeof own === 'boolean') return { value: own, source: 'global' }
  if (typeof definition.defaultWhenUnset === 'boolean') return { value: definition.defaultWhenUnset, source: 'default' }
  return { value: null, source: 'none' }
}

/** 本层设置过就用本层；没设置（inherit）沿用上一层的结果。 */
export function layerEffective(own: TriState, ownSource: 'store' | 'device', inherited: EffectiveValue): EffectiveValue {
  if (own === 'on') return { value: true, source: ownSource }
  if (own === 'off') return { value: false, source: ownSource }
  return inherited
}

/** 未改动的项以服务端算好的值为准（权威）；改了的项才按草稿本地推算，让用户在保存前就能看到结果。 */
export function chooseEffective(
  draftTri: TriState,
  originalTri: TriState,
  server: PosSettingEffectiveValue | undefined,
  computed: EffectiveValue,
): EffectiveValue {
  if (draftTri === originalTri && server) {
    return { value: typeof server.value === 'boolean' ? server.value : null, source: server.source }
  }
  return computed
}

// ---------------------------------------------------------------------------
// 设备回报与「待同步」
// ---------------------------------------------------------------------------

/**
 * synced：回报与应生效一致；pending：回报过但与应生效不同（或没回报这一项）→ 标「待同步」；
 * unreported：设备从没回报过任何值（旧版本或没联过网），只显示「—」，不逐项标待同步；
 * native：这一项没有下发值（沿用本机），没有「应生效」可比对，只显示设备回报的值（有的话）。
 */
export type ReportedState = 'synced' | 'pending' | 'unreported' | 'native'

export function reportedState(
  effective: EffectiveValue | undefined,
  reported: Readonly<Record<string, boolean>>,
  key: string,
): { state: ReportedState; value: boolean | undefined } {
  const value = typeof reported[key] === 'boolean' ? reported[key] : undefined
  if (!effective || effective.value === null) return { state: 'native', value }
  if (Object.keys(reported).length === 0) return { state: 'unreported', value: undefined }
  return { state: value === effective.value ? 'synced' : 'pending', value }
}

export type StatusTone = 'green' | 'orange' | 'gray'

/** applied = 已应用最新（绿）、pending = 待同步（橙）、never / 其他 = 尚未同步（灰）。 */
export function deviceStatusTone(status: string): StatusTone {
  if (status === 'applied') return 'green'
  if (status === 'pending') return 'orange'
  return 'gray'
}

/** 设备抽屉里要列出的设置：该设备端适用的项，加上服务端已给出生效值的项。 */
export function deviceVisibleDefinitions(
  definitions: readonly PosSettingDefinition[],
  clientKind: string,
  effective: Readonly<Record<string, PosSettingEffectiveValue>>,
): PosSettingDefinition[] {
  return definitions.filter((definition) => definition.clientKinds.includes(clientKind) || definition.key in effective)
}

// ---------------------------------------------------------------------------
// 门店列表
// ---------------------------------------------------------------------------

export function filterStores(stores: readonly PosSettingStoreSummary[], keyword: string): PosSettingStoreSummary[] {
  const text = keyword.trim().toLowerCase()
  if (!text) return [...stores]
  return stores.filter((store) => `${store.storeName} ${store.storeCode}`.toLowerCase().includes(text))
}

/** 有本店覆盖（与全局不同或显式设置的项）的门店数。 */
export function countOverriddenStores(stores: readonly PosSettingStoreSummary[]): number {
  return stores.filter((store) => Object.keys(store.overrides).length > 0).length
}

/** 门店覆盖项：按目录顺序，目录里没有的键（已下线的项）排在后面，界面回退显示键名。 */
export function storeOverrideEntries(
  definitions: readonly PosSettingDefinition[],
  overrides: Readonly<Record<string, boolean>>,
): Array<{ key: string; value: boolean }> {
  const known = definitions.filter((definition) => definition.key in overrides).map((definition) => definition.key)
  const extra = Object.keys(overrides).filter((key) => !known.includes(key)).sort()
  return [...known, ...extra].map((key) => ({ key, value: overrides[key] }))
}

/** 设备应用进度：没有设备时不显示进度。 */
export function applyProgress(store: Pick<PosSettingStoreSummary, 'deviceApplied' | 'deviceTotal'>):
  | { kind: 'none' }
  | { kind: 'all' | 'partial'; applied: number; total: number } {
  if (store.deviceTotal <= 0) return { kind: 'none' }
  const applied = Math.min(store.deviceApplied, store.deviceTotal)
  return { kind: applied >= store.deviceTotal ? 'all' : 'partial', applied, total: store.deviceTotal }
}

// ---------------------------------------------------------------------------
// 修改记录
// ---------------------------------------------------------------------------

/** 记录里的范围显示：全局 / 门店名（编号）/ 设备编号。 */
export function describeChangeScope(
  change: Pick<PosSettingChange, 'scope' | 'scopeKey'>,
  storeNames: Readonly<Record<string, string>>,
): { scope: 'global' | 'store' | 'device' | 'unknown'; label: string } {
  if (change.scope === 'global') return { scope: 'global', label: EMPTY_CELL }
  if (change.scope === 'store') {
    const name = storeNames[change.scopeKey]
    return { scope: 'store', label: name ? `${name} (${change.scopeKey})` : change.scopeKey }
  }
  if (change.scope === 'device') return { scope: 'device', label: change.scopeKey }
  return { scope: 'unknown', label: change.scopeKey }
}

// ---------------------------------------------------------------------------
// 时间：接口给 UTC，统一按悉尼时间显示
// ---------------------------------------------------------------------------

const DISPLAY_TIME_ZONE = 'Australia/Sydney'
let dateTimeFormatter: Intl.DateTimeFormat | null = null

/** 后端 DateTime 可能不带时区后缀（Unspecified），按 UTC 解释，避免被当成本地时间。 */
function parseUtc(value: string): Date {
  const hasZone = /(?:Z|[+-]\d{2}:?\d{2})$/i.test(value)
  return new Date(hasZone ? value : `${value}Z`)
}

/** UTC 时间 → 悉尼 yyyy-MM-dd HH:mm；空值或非法值显示「—」。 */
export function formatDateTime(utc: string | null | undefined): string {
  if (!utc) return EMPTY_CELL
  const date = parseUtc(utc)
  if (Number.isNaN(date.getTime())) return EMPTY_CELL
  dateTimeFormatter ??= new Intl.DateTimeFormat('en-CA', {
    timeZone: DISPLAY_TIME_ZONE,
    hourCycle: 'h23',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  })
  const parts = Object.fromEntries(dateTimeFormatter.formatToParts(date).map((part) => [part.type, part.value]))
  return `${parts.year}-${parts.month}-${parts.day} ${parts.hour}:${parts.minute}`
}

// ---------------------------------------------------------------------------
// 错误
// ---------------------------------------------------------------------------

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

function statusKey(status: number | null): string {
  if (status === null) return 'posSettings.errors.network'
  if (status === 401 || status === 403) return 'posSettings.errors.forbidden'
  if (status === 404) return 'posSettings.errors.notFound'
  if (status >= 500) return 'posSettings.errors.server'
  return 'posSettings.errors.unknown'
}

export type ResolvedPosSettingsError =
  | { kind: 'abort' }
  | { kind: 'error'; key: string; status: number | null; serverMessage: string | null }

/** 取消的请求不提示；有 HTTP 状态按状态归类，并带回后端 message（可直接展示）。 */
export function resolvePosSettingsError(error: unknown): ResolvedPosSettingsError {
  if (isRecord(error) && error.name === 'AbortError') return { kind: 'abort' }
  if (isRecord(error) && typeof error.status === 'number') {
    const payload = isRecord(error.payload) ? error.payload : {}
    const message = typeof payload.message === 'string' && payload.message.trim() ? payload.message.trim() : null
    return { kind: 'error', key: statusKey(error.status), status: error.status, serverMessage: message }
  }
  if (error instanceof TypeError) return { kind: 'error', key: 'posSettings.errors.network', status: null, serverMessage: null }
  return { kind: 'error', key: 'posSettings.errors.unknown', status: null, serverMessage: null }
}

/**
 * 403 一律显示后端 message（例如「全局设置只有管理员可以修改」）；
 * 其余错误中文界面也优先用后端的具体中文，英文界面用状态对应的英文文案。
 */
export function choosePosSettingsErrorText(
  resolved: Extract<ResolvedPosSettingsError, { kind: 'error' }>,
  language: string,
  translate: (key: string) => string,
): string {
  if (resolved.serverMessage && (resolved.status === 403 || language.toLowerCase().startsWith('zh'))) {
    return resolved.serverMessage
  }
  return translate(resolved.key)
}
