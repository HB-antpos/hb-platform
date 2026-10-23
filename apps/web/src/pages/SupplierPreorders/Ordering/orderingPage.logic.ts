// 供应商节日预订单「下单」页的纯逻辑：截止时间与服务器时钟校正、件数/金额、输入校验、
// 本机记忆（localStorage）容错读写、分页大小校验、初始分店选择、错误文案提取。
// 不依赖 React / antd，便于 node 直接跑单测。

import { SUPPLIER_PREORDER_PAGE_SIZES, type SupplierPreorderMyStore } from '../../../types/supplierPreorder'

export const DAY_MS = 24 * 60 * 60 * 1000
const HOUR_MS = 60 * 60 * 1000

/** 剩余不超过 3 天时提醒（琥珀色） */
export const URGENT_THRESHOLD_MS = 3 * DAY_MS

/** 本店下单箱数上限：防止误输入超大数字；正常一款不会超过这个箱数 */
export const MAX_CARTONS_PER_LINE = 9999

/** 分页大小：只允许 50 / 100 / 200 / 500，默认 50 */
export const ORDERING_PAGE_SIZES: readonly number[] = SUPPLIER_PREORDER_PAGE_SIZES
export const DEFAULT_ORDERING_PAGE_SIZE = 50

const SYDNEY_TIME_ZONE = 'Australia/Sydney'

// ---------- 时间 ----------

/** ISO 字符串转毫秒；空值或无法解析返回 null */
export function parseUtcMs(value?: string | null): number | null {
  if (!value) return null
  const trimmed = value.trim()
  if (!trimmed) return null
  // 后端 DateTime 有时不带时区后缀（按 UTC 存的），补 Z 避免被浏览器当成本地时间
  const normalized = /[zZ]|[+-]\d{2}:?\d{2}$/.test(trimmed) || !/T\d{2}:/.test(trimmed) ? trimmed : `${trimmed}Z`
  const ms = Date.parse(normalized)
  return Number.isNaN(ms) ? null : ms
}

/**
 * 服务器时钟偏差（毫秒）= 服务器当前时间 − 本机当前时间。
 * 之后用「本机 Date.now() + 偏差」作为校正后的当前时间，避免店里电脑时间不准导致截止判断错误。
 */
export function computeClockOffsetMs(serverNowUtc: string | null | undefined, localNowMs: number): number {
  const serverMs = parseUtcMs(serverNowUtc)
  return serverMs === null ? 0 : serverMs - localNowMs
}

export type DeadlineKind = 'none' | 'open' | 'urgent' | 'closed'

export interface DeadlineState {
  /** none 无截止；open 正常；urgent 剩 ≤3 天；closed 已截止（含订单表已锁定） */
  kind: DeadlineKind
  /** 距截止剩余毫秒（已截止为 0；无截止为 null） */
  remainingMs: number | null
  /** 剩余天数（向上取整），用于「剩 N 天」 */
  daysLeft: number | null
  /** 剩余不足 1 天时的小时数（向上取整），用于「剩 N 小时」 */
  hoursLeft: number | null
}

/**
 * 计算截止状态。nowMs 必须是已经过服务器时钟校正的时间。
 * isLocked 为后端判定的「对普通下单人已锁定」，优先于本地时间。
 */
export function getDeadlineState(deadlineAtUtc: string | null | undefined, nowMs: number, isLocked = false): DeadlineState {
  const deadlineMs = parseUtcMs(deadlineAtUtc)
  if (isLocked) return { kind: 'closed', remainingMs: deadlineMs === null ? null : 0, daysLeft: 0, hoursLeft: 0 }
  if (deadlineMs === null) return { kind: 'none', remainingMs: null, daysLeft: null, hoursLeft: null }
  const remainingMs = deadlineMs - nowMs
  if (remainingMs <= 0) return { kind: 'closed', remainingMs: 0, daysLeft: 0, hoursLeft: 0 }
  return {
    kind: remainingMs <= URGENT_THRESHOLD_MS ? 'urgent' : 'open',
    remainingMs,
    daysLeft: Math.ceil(remainingMs / DAY_MS),
    hoursLeft: remainingMs < DAY_MS ? Math.ceil(remainingMs / HOUR_MS) : null,
  }
}

/** 截止时间是否已到（按校正后的当前时间） */
export function isDeadlinePassed(deadlineAtUtc: string | null | undefined, nowMs: number): boolean {
  const deadlineMs = parseUtcMs(deadlineAtUtc)
  return deadlineMs !== null && nowMs >= deadlineMs
}

const sydneyDateTimeFormatter = new Intl.DateTimeFormat('en-CA', {
  timeZone: SYDNEY_TIME_ZONE,
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
})

/** 按悉尼时间显示 YYYY-MM-DD HH:mm；空值返回 '--' */
export function formatSydneyDateTime(value?: string | null): string {
  const ms = parseUtcMs(value)
  if (ms === null) return value?.trim() ? value : '--'
  const parts = sydneyDateTimeFormatter.formatToParts(new Date(ms)).reduce<Record<string, string>>((acc, part) => {
    if (part.type !== 'literal') acc[part.type] = part.value
    return acc
  }, {})
  return `${parts.year}-${parts.month}-${parts.day} ${parts.hour}:${parts.minute}`
}

/** 业务日期（报价日期等只有日期的字段）只取 YYYY-MM-DD，不做时区换算 */
export function formatBusinessDate(value?: string | null): string {
  if (!value) return '--'
  const match = /^(\d{4}-\d{2}-\d{2})/.exec(value.trim())
  return match ? match[1] : value
}

// ---------- 数量与金额 ----------

/** 金额保留两位小数（避免 0.1 + 0.2 之类的浮点尾巴） */
export function roundMoney(value: number): number {
  return Math.round((value + Number.EPSILON) * 100) / 100
}

/** 某行的件数 = 箱数 × 箱规；金额 = 件数 × 单价（不含 GST） */
export function computeLineTotals(cartons: number, cartonQty: number, price: number): { pieces: number; amount: number } {
  const safeCartons = Number.isFinite(cartons) && cartons > 0 ? Math.floor(cartons) : 0
  const safeQty = Number.isFinite(cartonQty) && cartonQty > 0 ? cartonQty : 0
  const safePrice = Number.isFinite(price) && price > 0 ? price : 0
  const pieces = safeCartons * safeQty
  return { pieces, amount: roundMoney(pieces * safePrice) }
}

const audFormatter = new Intl.NumberFormat('en-AU', { style: 'currency', currency: 'AUD', minimumFractionDigits: 2, maximumFractionDigits: 2 })
const integerFormatter = new Intl.NumberFormat('en-AU', { maximumFractionDigits: 0 })
const cbmFormatter = new Intl.NumberFormat('en-AU', { minimumFractionDigits: 1, maximumFractionDigits: 1 })

/** AUD 两位小数：$4,912.36 */
export function formatAud(amount: number | null | undefined): string {
  return audFormatter.format(Number.isFinite(amount) ? (amount as number) : 0)
}

/** 整数千分位 */
export function formatInteger(value: number | null | undefined): string {
  return integerFormatter.format(Number.isFinite(value) ? (value as number) : 0)
}

/** 体积一位小数 */
export function formatCbm(value: number | null | undefined): string {
  return cbmFormatter.format(Number.isFinite(value) ? (value as number) : 0)
}

/**
 * 解析箱数输入：只接受 0 以上整数（允许前后空格）；空串视为 0（清空 = 不订）。
 * 非法输入返回 null，调用方应回退到原值。
 */
export function parseCartonsInput(raw: string): number | null {
  const trimmed = raw.trim()
  if (trimmed === '') return 0
  if (!/^\d+$/.test(trimmed)) return null
  const value = Number(trimmed)
  if (!Number.isSafeInteger(value) || value > MAX_CARTONS_PER_LINE) return null
  return value
}

/** 步进器加减后的箱数：不小于 0、不超过上限 */
export function stepCartons(current: number, delta: number): number {
  const next = Math.floor(current) + delta
  return Math.min(MAX_CARTONS_PER_LINE, Math.max(0, next))
}

/** 体积占柜容量比例（0~1+）；没有容量返回 null */
export function computeContainerRatio(totalCbm: number, capacityCbm: number | null | undefined): number | null {
  if (!capacityCbm || !Number.isFinite(capacityCbm) || capacityCbm <= 0) return null
  return Math.max(0, totalCbm) / capacityCbm
}

// ---------- 分页大小 ----------

/** 只允许 50 / 100 / 200 / 500，其他值（含字符串、NaN）一律回到默认 50 */
export function normalizePageSize(value: unknown): number {
  const numeric = typeof value === 'string' ? Number(value.trim()) : value
  return typeof numeric === 'number' && ORDERING_PAGE_SIZES.includes(numeric) ? numeric : DEFAULT_ORDERING_PAGE_SIZE
}

// ---------- 本机记忆（localStorage） ----------

/** 最小存储接口，便于测试注入；页面里传 window.localStorage */
export interface KeyValueStorage {
  getItem(key: string): string | null
  setItem(key: string, value: string): void
}

export const PAGE_SIZE_STORAGE_KEY = 'sp-order:page-size'

/** 分店选择按订单表分别记忆 */
export function storeStorageKey(formGuid: string): string {
  return `sp-order:store:${formGuid}`
}

/** 取浏览器 localStorage；隐私模式、禁用存储或非浏览器环境返回 null */
export function getBrowserStorage(): KeyValueStorage | null {
  try {
    return typeof window !== 'undefined' && window.localStorage ? window.localStorage : null
  } catch {
    return null
  }
}

/** 容错读取：存储不可用或抛异常时返回 null */
export function safeStorageGet(storage: KeyValueStorage | null | undefined, key: string): string | null {
  if (!storage) return null
  try {
    return storage.getItem(key)
  } catch {
    return null
  }
}

/** 容错写入：失败静默忽略（记忆只是便利功能，不影响下单） */
export function safeStorageSet(storage: KeyValueStorage | null | undefined, key: string, value: string): void {
  if (!storage) return
  try {
    storage.setItem(key, value)
  } catch {
    // 配额满或被禁用：忽略
  }
}

export function readStoredPageSize(storage: KeyValueStorage | null | undefined): number {
  return normalizePageSize(safeStorageGet(storage, PAGE_SIZE_STORAGE_KEY))
}

/**
 * 初始分店：优先上次记住且仍在可选列表里的；否则第一家可编辑的；再否则第一家。
 */
export function pickInitialStoreGuid(stores: SupplierPreorderMyStore[], rememberedGuid: string | null | undefined): string | null {
  if (!stores.length) return null
  if (rememberedGuid && stores.some((store) => store.storeGuid === rememberedGuid)) return rememberedGuid
  return (stores.find((store) => store.canEdit) ?? stores[0]).storeGuid
}

// ---------- 错误文案 ----------

/**
 * 提取后端返回的中文原因：优先 ApiResponse.message，其次 Error.message（去掉「错误码: 」前缀）。
 */
export function getErrorMessage(error: unknown, fallback: string): string {
  if (error && typeof error === 'object') {
    const payload = (error as { payload?: unknown }).payload
    if (payload && typeof payload === 'object') {
      const payloadMessage = (payload as { message?: unknown }).message
      if (typeof payloadMessage === 'string' && payloadMessage.trim()) return payloadMessage.trim()
    }
    const message = (error as { message?: unknown }).message
    if (typeof message === 'string' && message.trim()) {
      // RequestError 会拼成「CODE: 原因」，错误码对店员没有意义
      return message.trim().replace(/^[A-Za-z][\w.]*:\s+/, '')
    }
  }
  return fallback
}

/** 请求被主动取消（切换分店/筛选时） */
export function isAbortError(error: unknown): boolean {
  return Boolean(error && typeof error === 'object' && (error as { name?: unknown }).name === 'AbortError')
}
