// 分店「小票下发」的纯逻辑：新旧对比、下发预览分类、状态请求竞态、勾选上限、设备应用汇总、错误解析、时间格式。
// 组件交互在 Node 下没有 jsdom 可测，所以把能抽出来的判断都放在这里并由 receiptProfileLogic.test.ts 覆盖。
import type {
  StoreReceiptProfileDevice,
  StoreReceiptProfileFields,
  StoreReceiptProfilePublishErrorDetail,
  StoreReceiptProfileStatusItem,
} from '../../../types/storeReceiptProfile'
import { RequestError } from '../../../utils/request'

/** 参与下发的 8 个字段，顺序即确认框里的展示顺序（与契约 StoreReceiptProfileFieldsDto 一致）。 */
export const RECEIPT_PROFILE_FIELD_KEYS = [
  'brandName',
  'storeName',
  'address',
  'phone',
  'abn',
  'returnPolicy',
  'voucherTerms',
  'installmentTerms',
] as const
export type ReceiptProfileFieldKey = (typeof RECEIPT_PROFILE_FIELD_KEYS)[number]

/** 契约：单批下发 1–100 家。 */
export const RECEIPT_PROFILE_MAX_STORES = 100

/** 并发下发冲突的错误码（HTTP 409）。 */
export const RECEIPT_PROFILE_PUBLISH_CONFLICT = 'RECEIPT_PROFILE_PUBLISH_CONFLICT'

/** 与后端「是否一致」口径相同：null 与空串/纯空白视为相同（trim 后比较），其余区分大小写。 */
export function normalizeReceiptField(value: string | null | undefined): string {
  return value == null ? '' : value.trim()
}

export interface ReceiptFieldDiff {
  key: ReceiptProfileFieldKey
  /** 最新下发快照里的值；从未下发时为 null。 */
  oldValue: string | null
  /** Store 当前值（将被下发的值）。 */
  newValue: string | null
  /** 是否有差异；从未下发时表示「该字段有内容将被下发」。 */
  changed: boolean
}

/** 逐字段对比 Store 当前值与最新下发快照；latest 为 null 表示从未下发。 */
export function diffReceiptProfile(
  current: StoreReceiptProfileFields,
  latest: StoreReceiptProfileFields | null,
): ReceiptFieldDiff[] {
  return RECEIPT_PROFILE_FIELD_KEYS.map((key) => {
    const newValue = current[key] ?? null
    const oldValue = latest ? (latest[key] ?? null) : null
    const changed = latest
      ? normalizeReceiptField(newValue) !== normalizeReceiptField(oldValue)
      : normalizeReceiptField(newValue) !== ''
    return { key, oldValue, newValue, changed }
  })
}

/** first=首次下发；changed=有未下发的修改；unchanged=与最新快照一致（下发时会被跳过）。 */
export type PublishPreviewKind = 'first' | 'changed' | 'unchanged'

export interface PublishPreviewRow {
  item: StoreReceiptProfileStatusItem
  kind: PublishPreviewKind
  diffs: ReceiptFieldDiff[]
  changedKeys: ReceiptProfileFieldKey[]
}

export interface PublishPreview {
  /** 全部分店（保持请求顺序）。 */
  rows: PublishPreviewRow[]
  /** 将被下发的分店（首次 + 有修改）。 */
  publishRows: PublishPreviewRow[]
  unchangedRows: PublishPreviewRow[]
  firstCount: number
  changedCount: number
  unchangedCount: number
  publishCount: number
}

/**
 * 下发预览分类：是否「有变化」以服务端 status 为准（避免前端口径与后端判定出现分歧），
 * 前端只用字段对比来决定「高亮哪些字段」。
 */
export function classifyPublishItem(item: StoreReceiptProfileStatusItem): PublishPreviewKind {
  if (item.status === 'never' || item.latestVersion <= 0 || !item.latest) {
    return 'first'
  }
  return item.status === 'synced' ? 'unchanged' : 'changed'
}

export function buildPublishPreview(items: readonly StoreReceiptProfileStatusItem[]): PublishPreview {
  const rows = items.map<PublishPreviewRow>((item) => {
    const diffs = diffReceiptProfile(item.current, item.latest)
    return {
      item,
      kind: classifyPublishItem(item),
      diffs,
      changedKeys: diffs.filter((diff) => diff.changed).map((diff) => diff.key),
    }
  })
  const publishRows = rows.filter((row) => row.kind !== 'unchanged')
  const unchangedRows = rows.filter((row) => row.kind === 'unchanged')
  return {
    rows,
    publishRows,
    unchangedRows,
    firstCount: rows.filter((row) => row.kind === 'first').length,
    changedCount: rows.filter((row) => row.kind === 'changed').length,
    unchangedCount: unchangedRows.length,
    publishCount: publishRows.length,
  }
}

export class ReceiptProfilePublishRequestError extends Error {
  constructor(public readonly code: 'INVALID_TARGETS') {
    super(code)
    this.name = 'ReceiptProfilePublishRequestError'
  }
}

/**
 * 构造下发请求：1–100 家、不重复、非空白（与后端校验一致，提前拦截）。
 * 下发是原子的（任一店不可下发则整批不写入），所以不能在前端拆成多批，超限直接拒绝。
 */
export function buildPublishRequest(storeGuids: readonly string[]): { storeGuids: string[] } {
  if (
    storeGuids.length < 1
    || storeGuids.length > RECEIPT_PROFILE_MAX_STORES
    || new Set(storeGuids).size !== storeGuids.length
    || storeGuids.some((guid) => !guid.trim())
  ) {
    throw new ReceiptProfilePublishRequestError('INVALID_TARGETS')
  }
  return { storeGuids: [...storeGuids] }
}

// ───────────────────────── 状态请求：按分店「最后发起者胜出」 ─────────────────────────

export interface StatusIssueTracker {
  /** 为这批分店发起一次新请求，返回令牌。 */
  begin: (guids: readonly string[]) => number
  /** 该令牌是否仍是此分店最近一次发起的请求。 */
  isCurrent: (token: number, guid: string) => boolean
}

/**
 * 页面里会同时有「换页/刷新触发的整页状态请求」和「下发后触发的指定分店请求」，响应先后不确定。
 * 按分店记录最近一次发起的令牌：较旧请求的响应不能覆盖较新请求已写入的结果。
 */
export function createStatusIssueTracker(): StatusIssueTracker {
  let sequence = 0
  const latestByGuid = new Map<string, number>()
  return {
    begin(guids) {
      sequence += 1
      for (const guid of guids) {
        latestByGuid.set(guid, sequence)
      }
      return sequence
    },
    isCurrent(token, guid) {
      return latestByGuid.get(guid) === token
    },
  }
}

/**
 * 把一次状态响应写入已有状态表：只处理「仍是该分店最新请求」的分店；
 * 请求过但响应里没有的分店（已软删/不存在）从表里移除。没有任何变化时返回原对象，避免无谓重渲染。
 */
export function applyStatusResponse(
  previous: Readonly<Record<string, StoreReceiptProfileStatusItem>>,
  requestedGuids: readonly string[],
  items: readonly StoreReceiptProfileStatusItem[],
  isCurrent: (guid: string) => boolean,
): Record<string, StoreReceiptProfileStatusItem> {
  const returned = new Map(items.map((item) => [item.storeGuid, item]))
  let next: Record<string, StoreReceiptProfileStatusItem> | null = null
  for (const guid of requestedGuids) {
    if (!isCurrent(guid)) {
      continue
    }
    const item = returned.get(guid)
    if (item) {
      next ??= { ...previous }
      next[guid] = item
    } else if (guid in previous) {
      next ??= { ...previous }
      delete next[guid]
    }
  }
  return next ?? (previous as Record<string, StoreReceiptProfileStatusItem>)
}

/** 当前页里状态为 pending（有未下发修改）的分店，保持列表顺序。 */
export function collectPendingGuids(
  rows: ReadonlyArray<{ storeGUID: string }>,
  statusByGuid: Readonly<Record<string, StoreReceiptProfileStatusItem | undefined>>,
): string[] {
  return rows
    .filter((row) => statusByGuid[row.storeGUID]?.status === 'pending')
    .map((row) => row.storeGUID)
}

export interface SelectionMergeResult {
  next: string[]
  added: number
  /** 因为 100 家上限，有分店没能加入。 */
  truncated: boolean
}

/** 在已有勾选上追加分店：保持已有顺序、去重，总数不超过 limit（下发单批上限）。 */
export function mergeSelectionWithLimit(
  selected: readonly string[],
  additions: readonly string[],
  limit = RECEIPT_PROFILE_MAX_STORES,
): SelectionMergeResult {
  const next = [...selected]
  const seen = new Set(selected)
  let truncated = false
  let added = 0
  for (const guid of additions) {
    if (seen.has(guid)) {
      continue
    }
    if (next.length >= limit) {
      truncated = true
      break
    }
    next.push(guid)
    seen.add(guid)
    added += 1
  }
  return { next, added, truncated }
}

// ───────────────────────── 设备应用情况 ─────────────────────────

/**
 * none=从未下发（不展示台数）；noDevices=已下发但该店没有启用的设备；
 * allApplied=全部已应用；partial=有设备尚未应用到最新版本。
 */
export type DeviceApplyTone = 'none' | 'noDevices' | 'allApplied' | 'partial'

export function summarizeDeviceApply(
  item: Pick<StoreReceiptProfileStatusItem, 'latestVersion' | 'deviceApplied' | 'deviceTotal'>,
): { tone: DeviceApplyTone; applied: number; total: number } {
  const applied = Math.max(0, item.deviceApplied)
  const total = Math.max(0, item.deviceTotal)
  if (item.latestVersion <= 0) {
    return { tone: 'none', applied: 0, total }
  }
  if (total === 0) {
    return { tone: 'noDevices', applied: 0, total: 0 }
  }
  return { tone: applied >= total ? 'allApplied' : 'partial', applied, total }
}

/** 设备明细排序：未更新到最新的排前面（最需要关注），同组内按设备编号。 */
export function sortReceiptProfileDevices(devices: readonly StoreReceiptProfileDevice[]): StoreReceiptProfileDevice[] {
  return [...devices].sort((a, b) => {
    if (a.upToDate !== b.upToDate) {
      return a.upToDate ? 1 : -1
    }
    return a.deviceCode.localeCompare(b.deviceCode)
  })
}

// ───────────────────────── 错误解析 ─────────────────────────

export type PublishFailureKind = 'conflict' | 'rejected' | 'other'

export interface PublishFailureItem {
  storeGuid?: string
  storeCode?: string
  errorCode?: string
  message: string
}

export interface PublishFailure {
  kind: PublishFailureKind
  errorCode?: string
  /** 服务端给出的整体提示（仅 other / rejected 使用；conflict 用本地化固定文案）。 */
  message?: string
  items: PublishFailureItem[]
}

function readPayload(error: unknown): Record<string, unknown> | undefined {
  if (!(error instanceof RequestError)) {
    return undefined
  }
  const payload = error.payload
  return typeof payload === 'object' && payload !== null && !Array.isArray(payload)
    ? (payload as Record<string, unknown>)
    : undefined
}

function readString(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() ? value : undefined
}

/**
 * 解析下发失败：409 并发冲突、400 整批被拒（details 里带逐店原因）、其它（网络/403/500）。
 * 逐店原因以 payload.details 为准；整体 message 只作兜底。
 */
export function parsePublishFailure(error: unknown): PublishFailure {
  const payload = readPayload(error)
  const errorCode = readString(payload?.errorCode) ?? readString(payload?.code)
  const rawDetails = Array.isArray(payload?.details) ? (payload.details as unknown[]) : []
  const items: PublishFailureItem[] = rawDetails
    .filter((detail): detail is StoreReceiptProfilePublishErrorDetail => typeof detail === 'object' && detail !== null)
    .map((detail) => ({
      storeGuid: readString(detail.storeGuid),
      storeCode: readString(detail.storeCode),
      errorCode: readString(detail.errorCode),
      message: readString(detail.message) ?? readString(detail.errorCode) ?? '',
    }))

  const status = error instanceof RequestError ? error.status : undefined
  const message = readString(payload?.message) ?? (error instanceof Error ? error.message : undefined)

  if (errorCode === RECEIPT_PROFILE_PUBLISH_CONFLICT || (status === 409 && !errorCode)) {
    return { kind: 'conflict', errorCode, message, items: [] }
  }
  if (items.length > 0 || status === 400) {
    return { kind: 'rejected', errorCode, message, items }
  }
  return { kind: 'other', errorCode, message, items: [] }
}

/** 状态接口 404：通常是后端还没部署该接口（部署先后顺序出错），与普通失败区分提示。 */
export function isReceiptProfileEndpointMissing(error: unknown): boolean {
  return error instanceof RequestError && error.status === 404
}

// ───────────────────────── 时间格式 ─────────────────────────

const ZONE_DESIGNATOR = /(?:Z|[+-]\d{2}(?::?\d{2})?)$/i

/**
 * 解析 ISO 时间为毫秒时间戳。assumeUtc=true 时，没有时区标记的文本按 UTC 解析：
 * 后端 datetime2 读出来的 DateTime 常是 Unspecified，序列化后没有 Z，浏览器会误当本地时间。
 */
export function parseInstant(value: string | null | undefined, assumeUtc: boolean): number | null {
  if (!value) {
    return null
  }
  const timePart = value.split('T')[1] ?? ''
  const text = assumeUtc && timePart && !ZONE_DESIGNATOR.test(timePart) ? `${value}Z` : value
  const timestamp = Date.parse(text)
  return Number.isNaN(timestamp) ? null : timestamp
}

const pad2 = (value: number) => String(value).padStart(2, '0')

/** 浏览器本地时区的 YYYY-MM-DD HH:mm。 */
export function formatLocalMinute(timestamp: number): string {
  const date = new Date(timestamp)
  return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())} ${pad2(date.getHours())}:${pad2(date.getMinutes())}`
}

/** UTC 的 YYYY-MM-DD HH:mm（用于悬浮提示，便于与服务端/日志对照）。 */
export function formatUtcMinute(timestamp: number): string {
  return new Date(timestamp).toISOString().slice(0, 16).replace('T', ' ')
}

/** 下发时间：字段名是 PublishedAtUtc，无时区标记时按 UTC 解析，展示为浏览器本地时间；无法解析返回 null。 */
export function formatPublishedAt(value: string | null | undefined): { local: string; utc: string } | null {
  const timestamp = parseInstant(value, true)
  return timestamp === null ? null : { local: formatLocalMinute(timestamp), utc: formatUtcMinute(timestamp) }
}

/** 最后心跳：与「设备管理」页一致，无时区标记的文本按本地时间解析；无法解析返回 null。 */
export function formatHeartbeatAt(value: string | null | undefined): string | null {
  const timestamp = parseInstant(value, false)
  return timestamp === null ? null : formatLocalMinute(timestamp)
}
