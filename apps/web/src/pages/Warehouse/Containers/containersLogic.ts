import dayjs, { type Dayjs } from 'dayjs'
import isoWeek from 'dayjs/plugin/isoWeek'
import type { ContainerMain } from '../../../types/container'

dayjs.extend(isoWeek)

/** 货柜状态码：与后端 Container.Status 一致。 */
export const CONTAINER_STATUS = {
  loaded: 0,
  inTransit: 1,
  completed: 2,
  cancelled: 7,
} as const

/** 装载率按 68 m³ 标准柜（40 尺高柜常用装载上限）计算。 */
export const STANDARD_CONTAINER_VOLUME_CBM = 68

/** 到岸周历：已逾期一列 + 本周 + 之后 5 周。 */
export const ARRIVAL_CALENDAR_WEEK_COUNT = 6

/** 周历额外请求的页长：未完成货柜通常十几柜，取足够大一页即可覆盖。 */
export const ARRIVAL_CALENDAR_PAGE_SIZE = 200

export type ContainerStatusTabKey = 'open' | 'loaded' | 'inTransit' | 'completed' | 'cancelled' | 'all'

export const DEFAULT_CONTAINER_STATUS_TAB: ContainerStatusTabKey = 'open'

/**
 * 状态页签对应的接口 Statuses 过滤值。「未完成」= 已装柜 + 运输中（接口支持多状态过滤），
 * 「全部」不传 Statuses，与原页面默认不过滤一致。
 */
export const CONTAINER_STATUS_TAB_STATUSES: Record<ContainerStatusTabKey, number[] | undefined> = {
  open: [CONTAINER_STATUS.loaded, CONTAINER_STATUS.inTransit],
  loaded: [CONTAINER_STATUS.loaded],
  inTransit: [CONTAINER_STATUS.inTransit],
  completed: [CONTAINER_STATUS.completed],
  cancelled: [CONTAINER_STATUS.cancelled],
  all: undefined,
}

export const CONTAINER_STATUS_TAB_KEYS: ContainerStatusTabKey[] = ['open', 'loaded', 'inTransit', 'completed', 'cancelled', 'all']

/** 计数请求只需要这几个互不重叠的口径，「未完成」由已装柜 + 运输中相加得到，少发一次请求。 */
export type ContainerCountBucket = 'loaded' | 'inTransit' | 'completed' | 'cancelled' | 'all'

export const CONTAINER_COUNT_BUCKETS: ContainerCountBucket[] = ['loaded', 'inTransit', 'completed', 'cancelled', 'all']

export function buildStatusTabCounts(
  counts: Record<ContainerCountBucket, number>,
): Record<ContainerStatusTabKey, number> {
  return {
    open: counts.loaded + counts.inTransit,
    loaded: counts.loaded,
    inTransit: counts.inTransit,
    completed: counts.completed,
    cancelled: counts.cancelled,
    all: counts.all,
  }
}

export function isOpenContainerStatus(status: number | null | undefined) {
  return status === CONTAINER_STATUS.loaded || status === CONTAINER_STATUS.inTransit
}

/**
 * 解析接口日期为本地自然日。后端 DateTime 无时区（如 2026-10-27T00:00:00），
 * 优先截取日期部分，避免带 Z 的值被换算到前一天。
 */
export function parseContainerDate(value?: string | null): Dayjs | undefined {
  if (!value) return undefined
  const datePart = /^\d{4}-\d{2}-\d{2}/.exec(value)?.[0]
  const parsed = datePart ? dayjs(datePart) : dayjs(value)
  return parsed.isValid() ? parsed.startOf('day') : undefined
}

export function formatContainerDate(value?: string | null) {
  return parseContainerDate(value)?.format('YYYY-MM-DD')
}

/** ISO 周号（周一为一周第一天），跨年时按 ISO week-year 计算。 */
export function getIsoWeekNumber(date: Dayjs) {
  return date.isoWeek()
}

function startOfIsoWeek(date: Dayjs) {
  return date.startOf('isoWeek')
}

function wholeDaysBetween(from: Dayjs, to: Dayjs) {
  // 两端都取本地零点后按天差计算；Math.round 吸收夏令时切换带来的 ±1 小时。
  return Math.round(to.startOf('day').diff(from.startOf('day'), 'hour') / 24)
}

export type EtaHint =
  | { kind: 'none' }
  | { kind: 'overdue'; days: number }
  | { kind: 'today' }
  | { kind: 'soon'; days: number }
  | { kind: 'upcoming'; days: number; week: number }
  | { kind: 'week'; week: number }

/**
 * 预计到岸列第二行提示：只有「未完成且未到货」的货柜才提示逾期/今天/还有几天，
 * 已到货、已完成或已取消的只显示 ISO 周号，避免把历史货柜标成逾期。
 */
export function describeEtaHint(
  container: Pick<ContainerMain, '状态' | '预计到岸日期' | '实际到货日期'>,
  today: Dayjs,
): EtaHint {
  const eta = parseContainerDate(container.预计到岸日期)
  if (!eta) return { kind: 'none' }
  const week = getIsoWeekNumber(eta)

  const awaitingArrival = isOpenContainerStatus(container.状态) && !parseContainerDate(container.实际到货日期)
  if (!awaitingArrival) return { kind: 'week', week }

  const days = wholeDaysBetween(today, eta)
  if (days < 0) return { kind: 'overdue', days: -days }
  if (days === 0) return { kind: 'today' }
  if (days <= 7) return { kind: 'soon', days }
  if (days <= 14) return { kind: 'upcoming', days, week }
  return { kind: 'week', week }
}

/** 装载率（%）：体积缺失时返回 undefined，超过 100% 照实显示（进度条在界面上封顶）。 */
export function getLoadRatePercent(volume?: number | null) {
  if (volume == null || !Number.isFinite(volume) || volume < 0) return undefined
  return Math.round((volume / STANDARD_CONTAINER_VOLUME_CBM) * 100)
}

export interface ContainerTotals {
  count: number
  pieces: number
  amount: number
  volume: number
}

/** 当前页或已勾选货柜的件数/金额/体积合计；只用于「本页合计」与勾选条，不能当成筛选结果合计。 */
export function summarizeContainers(containers: readonly ContainerMain[]): ContainerTotals {
  return containers.reduce<ContainerTotals>(
    (acc, item) => ({
      count: acc.count + 1,
      pieces: acc.pieces + (item.合计件数 || 0),
      amount: acc.amount + (item.合计金额 || 0),
      volume: acc.volume + (item.总体积 || 0),
    }),
    { count: 0, pieces: 0, amount: 0, volume: 0 },
  )
}

export interface ArrivalCalendarItem {
  container: ContainerMain
  eta: Dayjs
  volume: number
}

export interface ArrivalCalendarColumn {
  key: string
  kind: 'overdue' | 'week'
  /** 本周列：用于标题「W41 本周」与高亮。 */
  isCurrentWeek: boolean
  week?: number
  start?: Dayjs
  end?: Dayjs
  items: ArrivalCalendarItem[]
  totalVolume: number
}

export interface ArrivalCalendar {
  columns: ArrivalCalendarColumn[]
  /** 预计到岸晚于最后一列或未填预计日期的未完成货柜数，界面上单独提示，避免静默丢失。 */
  outsideCount: number
  /** 最后一列的最后一天，用于「晚于 MM-DD」提示。 */
  lastDay: Dayjs
}

/**
 * 到岸周历：把「已装柜/运输中且没有实际到货日期」的货柜按预计到岸日期分到
 * 已逾期（早于今天）、本周（今天起到本周日）与之后 5 周，共 7 列。
 */
export function buildArrivalCalendar(
  containers: readonly ContainerMain[],
  today: Dayjs,
  weekCount = ARRIVAL_CALENDAR_WEEK_COUNT,
): ArrivalCalendar {
  const todayStart = today.startOf('day')
  const thisWeekStart = startOfIsoWeek(todayStart)

  const overdue: ArrivalCalendarColumn = {
    key: 'overdue',
    kind: 'overdue',
    isCurrentWeek: false,
    items: [],
    totalVolume: 0,
  }
  const weeks: ArrivalCalendarColumn[] = Array.from({ length: weekCount }, (_, index) => {
    const start = thisWeekStart.add(index, 'week')
    return {
      key: `week-${index}`,
      kind: 'week',
      isCurrentWeek: index === 0,
      week: getIsoWeekNumber(start),
      start,
      end: start.add(6, 'day'),
      items: [],
      totalVolume: 0,
    }
  })

  let outsideCount = 0
  for (const container of containers) {
    if (!isOpenContainerStatus(container.状态) || parseContainerDate(container.实际到货日期)) continue

    const eta = parseContainerDate(container.预计到岸日期)
    if (!eta) {
      outsideCount += 1
      continue
    }

    const item: ArrivalCalendarItem = { container, eta, volume: container.总体积 || 0 }
    if (eta.isBefore(todayStart)) {
      overdue.items.push(item)
      continue
    }

    const weekIndex = Math.round(wholeDaysBetween(thisWeekStart, startOfIsoWeek(eta)) / 7)
    const column = weeks[weekIndex]
    if (column) {
      column.items.push(item)
    } else {
      outsideCount += 1
    }
  }

  const columns = [overdue, ...weeks]
  for (const column of columns) {
    column.items.sort(
      (left, right) =>
        left.eta.valueOf() - right.eta.valueOf() ||
        (left.container.货柜编号 ?? '').localeCompare(right.container.货柜编号 ?? ''),
    )
    column.totalVolume = column.items.reduce((sum, item) => sum + item.volume, 0)
  }

  return {
    columns,
    outsideCount,
    lastDay: weeks[weeks.length - 1]?.end ?? todayStart,
  }
}
