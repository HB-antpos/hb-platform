import { formatSydneyDateTime } from '../../SupplierPreorders/Admin/supplierPreorderAdmin.logic'
import type { MyobConnection, MyobStoreMappingChange, MyobStoreMappingItem } from '../../../types/myob'

// MYOB 对接页的纯函数：ABN 规范化 / 校验、回调参数解析、错误码 → 文案键、映射草稿 diff、建议采用。
// 不依赖 React / antd，便于 node 单测。

// ───────────────────────── ABN ─────────────────────────

/** 输入的 ABN 去掉所有空白（含全角 / 不换行空格）；不去掉其他字符，让校验能识别出非法输入。 */
export function normalizeAbnInput(raw: string | null | undefined): string {
  return (raw ?? '').replace(/[\s 　]+/g, '')
}

/** 空串表示「清除 ABN」，合法；否则必须恰好 11 位数字（先去空格）。 */
export function isValidAbnInput(raw: string | null | undefined): boolean {
  const normalized = normalizeAbnInput(raw)
  return normalized === '' || /^\d{11}$/.test(normalized)
}

/** 展示用：11 位数字按 2-3-3-3 分组（12 345 678 901）；不是 11 位纯数字的原样返回。 */
export function formatAbnDisplay(abn: string | null | undefined): string {
  const digits = normalizeAbnInput(abn)
  if (!/^\d{11}$/.test(digits)) {
    return digits
  }
  return `${digits.slice(0, 2)} ${digits.slice(2, 5)} ${digits.slice(5, 8)} ${digits.slice(8)}`
}

// ───────────────────────── 授权回调 ─────────────────────────

/**
 * 后端错误码 → 页面文案键（键写成字面量，页面文案契约测试才能扫描并校验中英文都存在）。
 * 与 MyobDtos.cs 的 MyobErrorCodes 一一对应。
 */
export const MYOB_ERROR_MESSAGE_KEYS: Readonly<Record<string, string>> = {
  MYOB_NOT_CONFIGURED: 'myob.errors.notConfigured',
  MYOB_INVALID_STATE: 'myob.errors.invalidState',
  MYOB_AUTHORIZATION_DENIED: 'myob.errors.authorizationDenied',
  MYOB_TOKEN_EXCHANGE_FAILED: 'myob.errors.tokenExchangeFailed',
  MYOB_BUSINESS_ID_MISSING: 'myob.errors.businessIdMissing',
  MYOB_CONNECTION_NOT_FOUND: 'myob.errors.connectionNotFound',
  MYOB_CONNECTION_NOT_ACTIVE: 'myob.errors.connectionNotActive',
  MYOB_NEEDS_REAUTHORIZATION: 'myob.errors.needsReauthorization',
  MYOB_INVALID_ABN: 'myob.errors.invalidAbn',
  MYOB_STORE_NOT_FOUND: 'myob.errors.storeNotFound',
  MYOB_API_CALL_FAILED: 'myob.errors.apiCallFailed',
}

export const MYOB_UNKNOWN_ERROR_KEY = 'myob.errors.unknown'

/** 未知 / 空错误码用通用文案，不把后端原始码直接展示成句子。 */
export function getMyobErrorMessageKey(code: string | null | undefined): string {
  const normalized = (code ?? '').trim().toUpperCase()
  return MYOB_ERROR_MESSAGE_KEYS[normalized] ?? MYOB_UNKNOWN_ERROR_KEY
}

export type MyobCallbackResult =
  | { kind: 'connected'; connectionGuid: string | null }
  | { kind: 'error'; code: string }

const MYOB_CALLBACK_PARAMS = ['myob', 'connection', 'code'] as const

/**
 * 解析授权完成后后端重定向带回的查询参数：
 * `?myob=connected&connection=<guid>` 或 `?myob=error&code=<MYOB_*>`。
 * 没有 myob 参数（或值不认识）返回 null，页面按普通进入处理。
 */
export function parseMyobCallback(search: string): MyobCallbackResult | null {
  const params = new URLSearchParams(search)
  const status = params.get('myob')
  if (status === 'connected') {
    return { kind: 'connected', connectionGuid: params.get('connection') || null }
  }
  if (status === 'error') {
    return { kind: 'error', code: (params.get('code') ?? '').trim() }
  }
  return null
}

/** 去掉回调参数，保留其他查询参数；返回形如 `?a=1` 或空串，直接拼在 pathname 后。 */
export function stripMyobCallbackParams(search: string): string {
  const params = new URLSearchParams(search)
  for (const key of MYOB_CALLBACK_PARAMS) {
    params.delete(key)
  }
  const rest = params.toString()
  return rest ? `?${rest}` : ''
}

// ───────────────────────── 请求错误文案 ─────────────────────────

const ERROR_CODE_PREFIX = /^(MYOB_[A-Z_]+):\s*/

/**
 * 取后端返回的业务错误（失败时展示后端 message）：
 * - 非 2xx：RequestError.message 已是 payload.message，错误码在 payload.code / errorCode；
 * - HTTP 200 + success=false：unwrapApiData 把 message 拼成 `CODE: message`，这里剥掉前缀。
 */
export function extractMyobError(error: unknown, fallback: string): { code: string | null; message: string } {
  const payload =
    error && typeof error === 'object' && 'payload' in error
      ? ((error as { payload?: unknown }).payload as Record<string, unknown> | undefined)
      : undefined
  const payloadCode =
    payload && typeof payload === 'object'
      ? [payload.code, payload.errorCode].find((value): value is string => typeof value === 'string' && value !== '')
      : undefined
  const rawMessage = error instanceof Error ? error.message.trim() : ''
  const prefixed = ERROR_CODE_PREFIX.exec(rawMessage)
  const message = (prefixed ? rawMessage.slice(prefixed[0].length) : rawMessage).trim()
  return {
    code: payloadCode ?? prefixed?.[1] ?? null,
    message: message || fallback,
  }
}

// ───────────────────────── 时间 ─────────────────────────

const TIMEZONE_SUFFIX = /(?:Z|[+-]\d{2}:?\d{2})$/i

/** 后端时间语义为 UTC，JSON 里可能不带 Z；补上 Z 再按悉尼时区格式化（YYYY-MM-DD HH:mm），空值显示 --。 */
export function formatMyobTime(value: string | null | undefined): string {
  if (!value || !value.trim()) {
    return '--'
  }
  const trimmed = value.trim().replace(' ', 'T')
  return formatSydneyDateTime(TIMEZONE_SUFFIX.test(trimmed) ? trimmed : `${trimmed}Z`)
}

// ───────────────────────── 连接列表 ─────────────────────────

/** 已断开的默认折叠；其余（Active / NeedsReauth）按公司名排序展示。 */
export function splitConnections(connections: MyobConnection[]): {
  visible: MyobConnection[]
  disconnected: MyobConnection[]
} {
  const byName = (a: MyobConnection, b: MyobConnection) =>
    a.businessName.localeCompare(b.businessName) || a.businessId.localeCompare(b.businessId)
  return {
    visible: connections.filter((item) => item.status !== 'Disconnected').sort(byName),
    disconnected: connections.filter((item) => item.status === 'Disconnected').sort(byName),
  }
}

/** 用后端返回的单条连接替换列表里同 guid 的那条；列表里没有则追加。 */
export function upsertConnection(connections: MyobConnection[], next: MyobConnection): MyobConnection[] {
  return connections.some((item) => item.connectionGuid === next.connectionGuid)
    ? connections.map((item) => (item.connectionGuid === next.connectionGuid ? next : item))
    : [...connections, next]
}

// ───────────────────────── 分店映射草稿 ─────────────────────────

/** 草稿只记录用户改过的行：storeCode → 新的 connectionGuid（null = 移除映射）。 */
export type MappingDrafts = Record<string, string | null>

/** 某分店当前生效值：有草稿用草稿，否则用后端当前映射。 */
export function getEffectiveConnectionGuid(store: MyobStoreMappingItem, drafts: MappingDrafts): string | null {
  return Object.prototype.hasOwnProperty.call(drafts, store.storeCode)
    ? drafts[store.storeCode]
    : store.connectionGuid
}

/** 写入一行草稿；改回与后端一致时直接删掉该草稿，保证「只提交变化」。 */
export function setMappingDraft(
  drafts: MappingDrafts,
  store: MyobStoreMappingItem,
  connectionGuid: string | null,
): MappingDrafts {
  const next = { ...drafts }
  if (connectionGuid === store.connectionGuid) {
    delete next[store.storeCode]
  } else {
    next[store.storeCode] = connectionGuid
  }
  return next
}

/** 与后端当前映射有差异的行，按 StoreCode 顺序，作为 PUT items。 */
export function buildMappingChanges(stores: MyobStoreMappingItem[], drafts: MappingDrafts): MyobStoreMappingChange[] {
  return stores
    .filter(
      (store) =>
        Object.prototype.hasOwnProperty.call(drafts, store.storeCode) && drafts[store.storeCode] !== store.connectionGuid,
    )
    .map((store) => ({ storeCode: store.storeCode, connectionGuid: drafts[store.storeCode] }))
}

/**
 * 采用建议：当前生效值为空、且有建议连接的行，把建议写进草稿。
 * 建议的连接必须仍在可选（Active）列表里，避免采用一个已不可选的连接；已有映射 / 草稿的行不动。
 */
export function applySuggestions(
  stores: MyobStoreMappingItem[],
  drafts: MappingDrafts,
  selectableGuids: ReadonlySet<string>,
): MappingDrafts {
  let next = drafts
  for (const store of stores) {
    const suggested = store.suggestedConnectionGuid
    if (suggested && selectableGuids.has(suggested) && getEffectiveConnectionGuid(store, next) === null) {
      next = setMappingDraft(next, store, suggested)
    }
  }
  return next
}

/** 当前（含草稿）可被「一键采用」的行数，用于按钮禁用与计数。 */
export function countApplicableSuggestions(
  stores: MyobStoreMappingItem[],
  drafts: MappingDrafts,
  selectableGuids: ReadonlySet<string>,
): number {
  return stores.filter(
    (store) =>
      store.suggestedConnectionGuid !== null &&
      selectableGuids.has(store.suggestedConnectionGuid) &&
      getEffectiveConnectionGuid(store, drafts) === null,
  ).length
}

/** 未启用分店默认隐藏；但有未保存草稿或已有映射的未启用分店仍显示，避免改动被藏起来。 */
export function filterMappingStores(
  stores: MyobStoreMappingItem[],
  showInactive: boolean,
  drafts: MappingDrafts,
): MyobStoreMappingItem[] {
  if (showInactive) {
    return stores
  }
  return stores.filter(
    (store) =>
      store.isActive ||
      store.connectionGuid !== null ||
      Object.prototype.hasOwnProperty.call(drafts, store.storeCode),
  )
}

/** 分店 → 公司映射下拉选项：Active 连接可选；已映射但不再 Active 的连接作为禁用项保留，避免显示裸 guid。 */
export interface MappingConnectionOption {
  value: string
  label: string
  status: MyobConnection['status']
  disabled: boolean
}

export function buildMappingOptions(
  activeConnections: MyobConnection[],
  allConnections: MyobConnection[],
  stores: MyobStoreMappingItem[],
): MappingConnectionOption[] {
  const options = new Map<string, MappingConnectionOption>()
  for (const connection of activeConnections) {
    options.set(connection.connectionGuid, {
      value: connection.connectionGuid,
      label: connection.businessName || connection.businessId,
      status: connection.status,
      disabled: false,
    })
  }
  const byGuid = new Map(allConnections.map((item) => [item.connectionGuid, item] as const))
  for (const store of stores) {
    const guid = store.connectionGuid
    if (guid && !options.has(guid)) {
      const connection = byGuid.get(guid)
      options.set(guid, {
        value: guid,
        label: connection ? connection.businessName || connection.businessId : guid,
        status: connection?.status ?? 'NeedsReauth',
        disabled: true,
      })
    }
  }
  return [...options.values()].sort((a, b) => a.label.localeCompare(b.label))
}

/** 连接已映射分店的展示名（hover 用）：优先「编码 名称」，查不到名称只显示编码。 */
export function describeMappedStores(storeCodes: string[], stores: MyobStoreMappingItem[]): string[] {
  const names = new Map(stores.map((store) => [store.storeCode, store.storeName] as const))
  return [...storeCodes].sort().map((code) => (names.get(code) ? `${code} ${names.get(code)}` : code))
}
