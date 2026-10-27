// 员工操作日志（新收银）的时间口径：一律按门店时区（悉尼）显示与查询，
// 与销售明细里的墙钟时间对齐，不再受操作者浏览器所在时区影响。
// 日期选择器里的 Dayjs 只当作「悉尼墙钟」的数值容器：字段（年月日时分秒）就是悉尼时间，
// 提交给后端前再换算成 UTC。只用 Intl，不引入 dayjs 时区插件（首屏包体预算紧）。

import dayjs, { type Dayjs } from 'dayjs'

import { EMPTY_VALUE, FALLBACK_TIME_ZONE, formatInStoreTime, type TimeFormat } from '../DailyCloses/logic'

/** 门店时区：仓库其他页面缺时区时同样回退悉尼。 */
export const STORE_TIME_ZONE = FALLBACK_TIME_ZONE

export interface StoreWallClock {
  year: number
  month: number
  day: number
  hour: number
  minute: number
  second: number
  millisecond: number
}

const storeClockFormatter = new Intl.DateTimeFormat('en-CA', {
  timeZone: STORE_TIME_ZONE,
  hourCycle: 'h23',
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
})

/** 某个 UTC 时刻在门店时区的墙钟。 */
export function utcToStoreWallClock(date: Date): StoreWallClock {
  const parts: Record<string, number> = {}
  for (const part of storeClockFormatter.formatToParts(date)) {
    if (part.type !== 'literal') parts[part.type] = Number(part.value)
  }
  return {
    year: parts.year,
    month: parts.month,
    day: parts.day,
    hour: parts.hour,
    minute: parts.minute,
    second: parts.second,
    millisecond: date.getUTCMilliseconds(),
  }
}

function wallClockAsUtcMs(wall: StoreWallClock): number {
  return Date.UTC(wall.year, wall.month - 1, wall.day, wall.hour, wall.minute, wall.second, wall.millisecond)
}

/**
 * 门店墙钟 → UTC 时刻。
 * 先把墙钟当作 UTC 估一次偏移，再用修正后的时刻复算一次，夏令时切换当天也能收敛。
 */
export function storeWallClockToUtc(wall: StoreWallClock): Date {
  const asUtc = wallClockAsUtcMs(wall)
  const offsetAt = (utcMs: number) => wallClockAsUtcMs(utcToStoreWallClock(new Date(utcMs))) - utcMs
  const utcMs = asUtc - offsetAt(asUtc)
  return new Date(asUtc - offsetAt(utcMs))
}

/** 把门店墙钟装进 Dayjs（字段即悉尼时间，仅供日期选择器展示与取值）。 */
export function storeWallClockToDayjs(wall: StoreWallClock): Dayjs {
  return dayjs(new Date(wall.year, wall.month - 1, wall.day, wall.hour, wall.minute, wall.second, wall.millisecond))
}

/** 门店此刻的墙钟（Dayjs 容器）。 */
export function getStoreNow(now: Date = new Date()): Dayjs {
  return storeWallClockToDayjs(utcToStoreWallClock(now))
}

/** UTC 时间 → 门店墙钟的 Dayjs 容器，用于把一条日志的时间回填到日期选择器。 */
export function utcToStorePickerValue(utc: string | Date): Dayjs {
  return storeWallClockToDayjs(utcToStoreWallClock(typeof utc === 'string' ? new Date(utc) : utc))
}

/** 日期选择器里的值（悉尼墙钟）→ 提交给后端的 UTC ISO 字符串。 */
export function storePickerValueToUtcIso(value: Dayjs): string {
  return storeWallClockToUtc({
    year: value.year(),
    month: value.month() + 1,
    day: value.date(),
    hour: value.hour(),
    minute: value.minute(),
    second: value.second(),
    millisecond: value.millisecond(),
  }).toISOString()
}

/** 按门店时区显示 UTC 时间，默认 `YYYY-MM-DD HH:mm:ss`；空值或非法值显示 —。 */
export function formatStoreTime(value: string | null | undefined, format: TimeFormat = 'seconds'): string {
  return formatInStoreTime(value, STORE_TIME_ZONE, format)
}

/** 只要 `HH:mm:ss`（详情里的时间线、同一天内的时刻）。 */
export function formatStoreClock(value: string | null | undefined): string {
  const text = formatStoreTime(value, 'seconds')
  return text === EMPTY_VALUE ? text : text.slice(-8)
}
