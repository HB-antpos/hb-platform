import type { StatusPillTone } from '../../../components/listToolbar/StatusPill'
import type {
  PreorderActivationStatus,
  PreorderActivationSummary,
  PreorderTemplateSummary,
} from '../../../types/preorder'

// Preorder 管理页的派生显示逻辑：只用现有接口字段在前端计算，不依赖新接口。

/** 只为最近 60 天内激活过的模板拉批次，避免模板一多首屏请求跟着线性增长。 */
export const PREORDER_LIVE_LOOKBACK_DAYS = 60
/** 首屏最多为这么多个模板拉批次；超出时批次概览视为不完整，页头不显示期数。 */
export const PREORDER_LIVE_MAX_TEMPLATES = 40
/** 并行拉批次的并发上限。 */
export const PREORDER_LIVE_CONCURRENCY = 4
/** 进行中批次剩余天数不超过它时用琥珀色提醒。 */
export const PREORDER_URGENT_DAYS = 2

const DAY_MS = 86_400_000

export type PreorderTemplateStatusFilter = 'all' | 'enabled' | 'disabled'

// 批次状态配色与批次详情口径一致：待开始橙、进行中蓝、已关闭绿、已取消灰。
const ACTIVATION_STATUS_TONES: Record<PreorderActivationStatus, StatusPillTone> = {
  Scheduled: 'orange',
  Active: 'blue',
  Closed: 'green',
  Cancelled: 'gray',
}

export function getActivationStatusTone(status: PreorderActivationStatus): StatusPillTone {
  return ACTIVATION_STATUS_TONES[status] ?? 'gray'
}

export function isLiveActivation(activation: Pick<PreorderActivationSummary, 'status'>) {
  return activation.status === 'Active' || activation.status === 'Scheduled'
}

export interface LiveBatchCandidates {
  templateGuids: string[]
  /** 候选模板超过上限被截断：批次概览不完整。 */
  truncated: boolean
}

/**
 * 选出需要拉批次的模板。
 * 摘要带最近激活时间时只取近 60 天内激活过的；后端摘要目前没有这个字段，
 * 退回到「有过批次」的模板（没有批次的模板不可能有进行中或待开始的批次）。
 */
export function selectLiveBatchCandidates(
  templates: readonly PreorderTemplateSummary[],
  nowMs: number,
): LiveBatchCandidates {
  const cutoff = nowMs - PREORDER_LIVE_LOOKBACK_DAYS * DAY_MS
  const matched = templates.filter((template) => {
    if (template.activationCount <= 0) return false
    const latest = template.latestActivationAt ? Date.parse(template.latestActivationAt) : Number.NaN
    return Number.isFinite(latest) ? latest >= cutoff : true
  })
  return {
    templateGuids: matched.slice(0, PREORDER_LIVE_MAX_TEMPLATES).map((template) => template.templateGuid),
    truncated: matched.length > PREORDER_LIVE_MAX_TEMPLATES,
  }
}

/** 限制并发地逐个执行任务，单个失败不影响其他任务，结果顺序与输入一致。 */
export async function runWithConcurrency<T, R>(
  items: readonly T[],
  limit: number,
  worker: (item: T, index: number) => Promise<R>,
): Promise<PromiseSettledResult<R>[]> {
  const results: PromiseSettledResult<R>[] = new Array(items.length)
  let nextIndex = 0
  const runnerCount = Math.max(1, Math.min(Math.floor(limit) || 1, items.length))
  const runners = Array.from({ length: runnerCount }, async () => {
    while (nextIndex < items.length) {
      const index = nextIndex
      nextIndex += 1
      try {
        results[index] = { status: 'fulfilled', value: await worker(items[index], index) }
      } catch (reason) {
        results[index] = { status: 'rejected', reason }
      }
    }
  })
  await Promise.all(runners)
  return results
}

function startOfLocalDay(ms: number) {
  const date = new Date(ms)
  date.setHours(0, 0, 0, 0)
  return date.getTime()
}

/** 两个时间点之间相差的本地日历天数（按日期算，不按 24 小时算；夏令时日也按一天计）。 */
export function getCalendarDayDiff(fromMs: number, toMs: number) {
  return Math.round((startOfLocalDay(toMs) - startOfLocalDay(fromMs)) / DAY_MS)
}

export type ActivationTiming =
  | { kind: 'closesToday'; urgent: true }
  | { kind: 'closesIn'; days: number; urgent: boolean }
  | { kind: 'starts'; durationDays: number; urgent: false }
  | { kind: 'none'; urgent: false }

/**
 * 卡片与「当前批次」列的时间提示：
 * - 进行中：距截止还剩几个日历天，当天截止或剩余不超过 2 天标为紧急；
 * - 待开始：开始时间 + 为期几天（按日历天，至少 1 天）；
 * - 其他状态或时间无效：不提示。
 */
export function getActivationTiming(
  activation: Pick<PreorderActivationSummary, 'status' | 'startAtUtc' | 'endAtUtc'>,
  nowMs: number,
): ActivationTiming {
  const startMs = Date.parse(activation.startAtUtc)
  const endMs = Date.parse(activation.endAtUtc)
  if (activation.status === 'Active' && Number.isFinite(endMs)) {
    const days = getCalendarDayDiff(nowMs, endMs)
    if (days <= 0) return { kind: 'closesToday', urgent: true }
    return { kind: 'closesIn', days, urgent: days <= PREORDER_URGENT_DAYS }
  }
  if (activation.status === 'Scheduled' && Number.isFinite(startMs) && Number.isFinite(endMs)) {
    return { kind: 'starts', durationDays: Math.max(1, getCalendarDayDiff(startMs, endMs)), urgent: false }
  }
  return { kind: 'none', urgent: false }
}

export interface ActivationProgress {
  target: number
  /** 已处理（已提交订单或已确认无需求）的分店数。 */
  responded: number
  pending: number
  respondedPercent: number
}

/**
 * 分店处理进度。后台批次列表只返回「已处理」总数，不区分已订货与无需求，
 * 所以这里只拆成已处理 / 未提交两段。
 */
export function getActivationProgress(
  activation: Pick<PreorderActivationSummary, 'targetStoreCount' | 'pendingCount'>,
): ActivationProgress {
  const target = Math.max(0, activation.targetStoreCount || 0)
  const pending = Math.min(target, Math.max(0, activation.pendingCount || 0))
  const responded = target - pending
  const respondedPercent = target > 0 ? Math.round((responded / target) * 1000) / 10 : 0
  return { target, responded, pending, respondedPercent }
}

function compareByTime(left: string, right: string) {
  return (Date.parse(left) || 0) - (Date.parse(right) || 0)
}

/** 模板当前批次：优先最早截止的进行中批次，其次最早开始的待开始批次。 */
export function pickCurrentActivation(activations: readonly PreorderActivationSummary[]) {
  const active = activations
    .filter((activation) => activation.status === 'Active')
    .sort((left, right) => compareByTime(left.endAtUtc, right.endAtUtc))
  if (active.length) return active[0]
  return activations
    .filter((activation) => activation.status === 'Scheduled')
    .sort((left, right) => compareByTime(left.startAtUtc, right.startAtUtc))[0]
}

/** 汇总所有已加载模板里进行中与待开始的批次：进行中按截止时间、待开始按开始时间排在后面。 */
export function collectLiveActivations(activationsByTemplate: Readonly<Record<string, readonly PreorderActivationSummary[]>>) {
  const seen = new Set<string>()
  const live: PreorderActivationSummary[] = []
  for (const activations of Object.values(activationsByTemplate)) {
    for (const activation of activations) {
      if (!isLiveActivation(activation) || seen.has(activation.activationGuid)) continue
      seen.add(activation.activationGuid)
      live.push(activation)
    }
  }
  return live.sort((left, right) => {
    if (left.status !== right.status) return left.status === 'Active' ? -1 : 1
    return left.status === 'Active'
      ? compareByTime(left.endAtUtc, right.endAtUtc)
      : compareByTime(left.startAtUtc, right.startAtUtc)
  })
}

/**
 * 最近激活时间：摘要有字段就用摘要；否则取已加载批次里期号最大那一期的开始时间。
 * 都没有时返回 undefined（界面显示 --）。
 */
export function resolveLatestActivationMs(
  template: Pick<PreorderTemplateSummary, 'latestActivationAt'>,
  activations?: readonly PreorderActivationSummary[],
) {
  const fromSummary = template.latestActivationAt ? Date.parse(template.latestActivationAt) : Number.NaN
  if (Number.isFinite(fromSummary)) return fromSummary
  if (!activations?.length) return undefined
  const latest = activations.reduce((best, activation) =>
    activation.sequenceNumber > best.sequenceNumber ? activation : best,
  )
  const startMs = Date.parse(latest.startAtUtc)
  return Number.isFinite(startMs) ? startMs : undefined
}

export function countTemplatesByStatus(templates: readonly PreorderTemplateSummary[]) {
  const enabled = templates.filter((template) => template.isEnabled).length
  return { all: templates.length, enabled, disabled: templates.length - enabled }
}

/** 模板表的前端过滤：名称包含关键字（忽略大小写）+ 启用状态。 */
export function filterTemplates(
  templates: readonly PreorderTemplateSummary[],
  keyword: string,
  status: PreorderTemplateStatusFilter,
) {
  const needle = keyword.trim().toLocaleLowerCase()
  return templates.filter((template) => {
    if (status === 'enabled' && !template.isEnabled) return false
    if (status === 'disabled' && template.isEnabled) return false
    return !needle || template.name.toLocaleLowerCase().includes(needle)
  })
}

function pad2(value: number) {
  return String(value).padStart(2, '0')
}

/** 「10-08 周四 18:00」：卡片上的截止 / 开始时间，星期按界面语言显示。 */
export function formatMonthDayWeekdayTime(ms: number, language: string) {
  const date = new Date(ms)
  const weekday = new Intl.DateTimeFormat(language, { weekday: 'short' }).format(date)
  return `${pad2(date.getMonth() + 1)}-${pad2(date.getDate())} ${weekday} ${pad2(date.getHours())}:${pad2(date.getMinutes())}`
}

/** 同年显示「MM-DD」，跨年显示「YYYY-MM-DD」。 */
export function formatCompactDate(ms: number, nowMs: number) {
  const date = new Date(ms)
  const monthDay = `${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}`
  return date.getFullYear() === new Date(nowMs).getFullYear() ? monthDay : `${date.getFullYear()}-${monthDay}`
}

/** 同年显示「MM-DD HH:mm」，跨年只显示「YYYY-MM-DD」。 */
export function formatCompactDateTime(ms: number, nowMs: number) {
  const date = new Date(ms)
  if (date.getFullYear() !== new Date(nowMs).getFullYear()) return formatCompactDate(ms, nowMs)
  return `${formatCompactDate(ms, nowMs)} ${pad2(date.getHours())}:${pad2(date.getMinutes())}`
}
