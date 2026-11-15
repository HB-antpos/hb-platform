import type { PackBlankLimitData, PackCartonSummary, PackVoidBlankResult } from '../../types/packing'
import { PACK_CARTON_STATUS, PACK_ERROR_CODE } from '../../types/packing'

// 批量箱标（预打印空箱标签）的纯逻辑：不依赖 React / DOM，便于单测。

/** 一次最多创建的张数（后端同样限制 1～100）。 */
export const BLANK_BATCH_MAX = 100
export const BLANK_BATCH_DEFAULT = 20
/** 快捷张数。 */
export const BLANK_QUICK_COUNTS = [10, 20, 50, 100] as const
/** 本地点未启用的空箱标上限（后端 BLANK_LIMIT_EXCEEDED 没带 limit 时的兜底）。 */
export const BLANK_UNACTIVATED_LIMIT = 300
/** 每个箱号打印份数上限（一份 = 一页）。 */
export const BLANK_COPIES_MAX = 5
/** 一次浏览器打印最多多少页：再多就分成几次打印，每次打印后单独上报「已打印」。 */
export const PRINT_CHUNK_MAX_PAGES = 100
/** 后端批量接口（labels-printed / void-blank）每次最多 100 个箱。 */
export const BLANK_GUID_BATCH_SIZE = 100

function toInteger(value: number | string | null | undefined): number | null {
  if (value === null || value === undefined || value === '') {
    return null
  }
  const parsed = typeof value === 'number' ? value : Number(value)
  return Number.isFinite(parsed) ? Math.floor(parsed) : null
}

/** 张数夹到 1～100；空值 / 非数字回到默认值。 */
export function clampBlankCount(value: number | string | null | undefined): number {
  const parsed = toInteger(value)
  if (parsed === null) {
    return BLANK_BATCH_DEFAULT
  }
  return Math.min(BLANK_BATCH_MAX, Math.max(1, parsed))
}

/** 打印份数夹到 1～5；空值 / 非数字为 1。 */
export function clampCopies(value: number | string | null | undefined): number {
  const parsed = toInteger(value)
  if (parsed === null) {
    return 1
  }
  return Math.min(BLANK_COPIES_MAX, Math.max(1, parsed))
}

// ---- 箱的空箱标判断（旧后端缺字段按「不是空箱标、没打印过」） ----

export function isVoidedCarton(carton: Pick<PackCartonSummary, 'status'>): boolean {
  return carton.status === PACK_CARTON_STATUS.Voided
}

/** 未启用的空箱标：标了 isBlank 且还没启用、没作废。启用后箱就是普通箱（印内容标）。 */
export function isUnactivatedBlank(carton: Pick<PackCartonSummary, 'isBlank' | 'activatedAtUtc' | 'status'>): boolean {
  return carton.isBlank === true && !carton.activatedAtUtc && !isVoidedCarton(carton)
}

export function labelPrintCountOf(carton: Pick<PackCartonSummary, 'labelPrintCount'>): number {
  return carton.labelPrintCount && carton.labelPrintCount > 0 ? carton.labelPrintCount : 0
}

export type BlankPrintState = { kind: 'unprinted' } | { kind: 'printed'; count: number }

/** 「还没打印 / 已打印 N 次」。 */
export function blankPrintState(carton: Pick<PackCartonSummary, 'labelPrintCount'>): BlankPrintState {
  const count = labelPrintCountOf(carton)
  return count > 0 ? { kind: 'printed', count } : { kind: 'unprinted' }
}

/** 批内位置：序号和总数都有才算（旧数据 / 非批量创建的箱没有）。 */
export function blankBatchPosition(
  carton: Pick<PackCartonSummary, 'batchGuid' | 'batchSeq' | 'batchCount'>,
): { batch: string; seq: number; count: number } | null {
  if (!carton.batchGuid || !carton.batchSeq || !carton.batchCount) {
    return null
  }
  return { batch: shortBatchCode(carton.batchGuid), seq: carton.batchSeq, count: carton.batchCount }
}

/** 批次短码：批次 GUID 前 4 位大写（后端没有「今天第几批」的编号，这里只用来区分不同批）。 */
export function shortBatchCode(batchGuid: string): string {
  return batchGuid.replace(/-/g, '').slice(0, 4).toUpperCase()
}

/** 标签右下角的 `LABEL 01 / 20`：序号补到至少两位。 */
export function formatLabelSeq(seq: number, total: number): string {
  return `LABEL ${String(seq).padStart(2, '0')} / ${total}`
}

// ---- 打印队列 ----

export function chunkArray<T>(items: readonly T[], size: number): T[][] {
  const chunkSize = Math.max(1, Math.floor(size))
  const chunks: T[][] = []
  for (let index = 0; index < items.length; index += chunkSize) {
    chunks.push(items.slice(index, index + chunkSize))
  }
  return chunks
}

/** 总页数：箱数 × 每个箱号份数。 */
export function countPrintPages(cartonCount: number, copies: number): number {
  return cartonCount * clampCopies(copies)
}

/**
 * 打印队列分页：每次打印的页数不超过 maxPages，同一只箱的几份标签留在同一次打印里
 * （一次至少一只箱）。每次打印结束后单独上报「已打印」，浏览器卡住 / 关掉页面时进度不会全丢。
 */
export function chunkPrintQueue<T>(cartons: readonly T[], copies: number, maxPages: number = PRINT_CHUNK_MAX_PAGES): T[][] {
  const perChunk = Math.max(1, Math.floor(maxPages / clampCopies(copies)))
  return chunkArray(cartons, perChunk)
}

/** 去重（按 cartonGuid，保留第一次出现的顺序）。 */
export function uniqueCartons<T extends { cartonGuid: string }>(cartons: readonly T[]): T[] {
  const seen = new Set<string>()
  return cartons.filter((carton) => {
    if (seen.has(carton.cartonGuid)) {
      return false
    }
    seen.add(carton.cartonGuid)
    return true
  })
}

/** 批次里「补打未打印的」：未启用的空箱标里一次都没打印过的。 */
export function filterUnprinted<T extends Pick<PackCartonSummary, 'isBlank' | 'activatedAtUtc' | 'status' | 'labelPrintCount'>>(
  cartons: readonly T[],
): T[] {
  return cartons.filter((carton) => isUnactivatedBlank(carton) && labelPrintCountOf(carton) === 0)
}

/** 批次里「全部重打」：未启用的空箱标（启用后的箱贴的是内容标，作废的不打）。 */
export function filterReprintable<T extends Pick<PackCartonSummary, 'isBlank' | 'activatedAtUtc' | 'status'>>(cartons: readonly T[]): T[] {
  return cartons.filter(isUnactivatedBlank)
}

/** 打印后要上报「已打印」的箱：空箱标（isBlank）。普通内容标走封箱时的打印记录，不在这里上报。 */
export function cartonsToReportPrinted<T extends Pick<PackCartonSummary, 'isBlank' | 'cartonGuid'>>(cartons: readonly T[]): string[] {
  return cartons.filter((carton) => carton.isBlank === true).map((carton) => carton.cartonGuid)
}

// ---- 多选 ----

export interface BlankSelectionSummary {
  /** 能作废的：未启用的空箱标。 */
  blankGuids: string[]
  /** 不是（未启用的）空箱标的数量。 */
  otherCount: number
}

export function summarizeSelection(selected: readonly PackCartonSummary[]): BlankSelectionSummary {
  const blankGuids = selected.filter(isUnactivatedBlank).map((carton) => carton.cartonGuid)
  return { blankGuids, otherCount: selected.length - blankGuids.length }
}

export interface VoidBlankSummary {
  voided: number
  /** 不是空箱标而被跳过：界面预先排除的 + 后端回的 NOT_BLANK。 */
  notBlank: number
  /** 后端回的 NOT_FOUND（已被别处删除 / 范围外）。 */
  notFound: number
}

/** 汇总多次 void-blank 的结果（每次 ≤100 张）。 */
export function summarizeVoidResult(clientSkipped: number, responses: readonly PackVoidBlankResult[]): VoidBlankSummary {
  const summary: VoidBlankSummary = { voided: 0, notBlank: Math.max(0, clientSkipped), notFound: 0 }
  for (const response of responses) {
    summary.voided += response.voided?.length ?? 0
    for (const skipped of response.skipped ?? []) {
      if (skipped.reason === 'NOT_FOUND') {
        summary.notFound += 1
      } else {
        summary.notBlank += 1
      }
    }
  }
  return summary
}

export type BlankMessagePart = { key: 'voided' | 'skippedNotBlank' | 'skippedNotFound'; count: number }

/** 作废结果提示的组成：只列数量大于 0 的项，界面按顺序拼成一句。 */
export function voidResultParts(summary: VoidBlankSummary): BlankMessagePart[] {
  const parts: BlankMessagePart[] = []
  if (summary.voided > 0) parts.push({ key: 'voided', count: summary.voided })
  if (summary.notBlank > 0) parts.push({ key: 'skippedNotBlank', count: summary.notBlank })
  if (summary.notFound > 0) parts.push({ key: 'skippedNotFound', count: summary.notFound })
  return parts
}

// ---- 创建 ----

/** 创建失败是「空箱标已满」：返回已有张数和上限（后端没带时上限兜底 300），否则 null。 */
export function resolveBlankLimitError(
  info: { status?: number; errorCode?: string; data?: PackBlankLimitData | null } | null | undefined,
): { existing: number; limit: number } | null {
  if (!info || info.errorCode !== PACK_ERROR_CODE.BlankLimitExceeded) {
    return null
  }
  const existing = typeof info.data?.existing === 'number' ? info.data.existing : 0
  const limit = typeof info.data?.limit === 'number' && info.data.limit > 0 ? info.data.limit : BLANK_UNACTIVATED_LIMIT
  return { existing, limit }
}

/** 本地点还能再建多少张（只用于提示；真正的上限由后端判断）。 */
export function remainingBlankQuota(existing: number | null | undefined, limit: number = BLANK_UNACTIVATED_LIMIT): number {
  return Math.max(0, limit - Math.max(0, existing ?? 0))
}

export interface ClientRequestMemo {
  id: string
  /** 生成该 id 时的创建参数；参数变了（张数、去向、地点）就不能复用，否则会拿到上一批。 */
  signature: string
}

/** 创建参数的签名：同一次点击的失败重试签名不变。 */
export function blankRequestSignature(input: {
  ownerCode: string
  count: number
  destinationType?: number
  destinationCode?: string | null
  carrierGuid?: string | null
}): string {
  return [input.ownerCode, input.count, input.destinationType ?? '', input.destinationCode ?? '', input.carrierGuid ?? ''].join('|')
}

/** clientRequestId：失败重试（参数没变）复用上一次的，参数变了或上次成功后重新生成。 */
export function resolveClientRequestId(
  memo: ClientRequestMemo | null,
  signature: string,
  generate: () => string,
): ClientRequestMemo {
  if (memo && memo.signature === signature) {
    return memo
  }
  return { id: generate(), signature }
}

/** 生成 GUID：优先 crypto.randomUUID；不可用（非安全上下文）时用 getRandomValues 拼 v4。 */
export function generateRequestGuid(): string {
  const cryptoApi = typeof globalThis !== 'undefined' ? globalThis.crypto : undefined
  if (cryptoApi?.randomUUID) {
    return cryptoApi.randomUUID()
  }
  const bytes = new Uint8Array(16)
  if (cryptoApi?.getRandomValues) {
    cryptoApi.getRandomValues(bytes)
  } else {
    for (let index = 0; index < bytes.length; index += 1) {
      bytes[index] = Math.floor(Math.random() * 256)
    }
  }
  bytes[6] = (bytes[6] & 0x0f) | 0x40
  bytes[8] = (bytes[8] & 0x3f) | 0x80
  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}

/** 预览用的示例箱号：BX + 本地日期 yymmdd + 0001（真实箱号以创建结果为准）。 */
export function sampleBlankCartonNo(now: Date = new Date()): string {
  const pad = (part: number) => String(part).padStart(2, '0')
  return `BX${pad(now.getFullYear() % 100)}${pad(now.getMonth() + 1)}${pad(now.getDate())}0001`
}
