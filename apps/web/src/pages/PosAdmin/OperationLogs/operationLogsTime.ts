// 员工操作日志（新收银）的时间口径：按「分店时区」显示与查询，与销售明细里的门店墙钟时间对齐，
// 不再受操作者浏览器所在时区影响。分店时区由后端按分店管理里的配置（缺省按地址推断，再回退悉尼）给出。
// 日期选择器里的 Dayjs 只当作「门店墙钟」的数值容器：字段（年月日时分秒）就是门店本地时间；
// 查询时直接把墙钟字符串交给后端，由后端按每家分店各自的时区换算。
// 只用 Intl，不引入 dayjs 时区插件（首屏包体预算紧）。

import dayjs, { type Dayjs } from 'dayjs'

import { EMPTY_VALUE, FALLBACK_TIME_ZONE, formatInStoreTime, resolveTimeZone, type TimeFormat } from '../DailyCloses/logic'

/** 默认时区：分店没有时区数据（旧后端、未知分店）、以及生成「现在」「今天」预设时使用，全仓同样回退悉尼。 */
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

const clockFormatterCache = new Map<string, Intl.DateTimeFormat>()

function getClockFormatter(timeZone: string): Intl.DateTimeFormat {
  let formatter = clockFormatterCache.get(timeZone)
  if (!formatter) {
    formatter = new Intl.DateTimeFormat('en-CA', {
      timeZone,
      hourCycle: 'h23',
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
    })
    clockFormatterCache.set(timeZone, formatter)
  }
  return formatter
}

/** 某个 UTC 时刻在指定分店时区的墙钟；时区缺失或无法识别时按悉尼。 */
export function utcToStoreWallClock(date: Date, timeZoneId?: string | null): StoreWallClock {
  const parts: Record<string, number> = {}
  for (const part of getClockFormatter(resolveTimeZone(timeZoneId)).formatToParts(date)) {
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
 * 分店墙钟 → UTC 时刻（默认悉尼）。
 * 先把墙钟当作 UTC 估一次偏移，再用修正后的时刻复算一次，夏令时切换当天也能收敛。
 */
export function storeWallClockToUtc(wall: StoreWallClock, timeZoneId?: string | null): Date {
  const asUtc = wallClockAsUtcMs(wall)
  const offsetAt = (utcMs: number) => wallClockAsUtcMs(utcToStoreWallClock(new Date(utcMs), timeZoneId)) - utcMs
  const utcMs = asUtc - offsetAt(asUtc)
  return new Date(asUtc - offsetAt(utcMs))
}

/** 把门店墙钟装进 Dayjs（字段即门店本地时间，仅供日期选择器展示与取值）。 */
export function storeWallClockToDayjs(wall: StoreWallClock): Dayjs {
  return dayjs(new Date(wall.year, wall.month - 1, wall.day, wall.hour, wall.minute, wall.second, wall.millisecond))
}

/** 此刻的门店墙钟（Dayjs 容器），默认悉尼：用于默认范围与「今天 / 近 7 天」预设。 */
export function getStoreNow(now: Date = new Date(), timeZoneId?: string | null): Dayjs {
  return storeWallClockToDayjs(utcToStoreWallClock(now, timeZoneId))
}

/** UTC 时间 → 指定分店墙钟的 Dayjs 容器，用于把一条日志的时间回填到日期选择器。 */
export function utcToStorePickerValue(utc: string | Date, timeZoneId?: string | null): Dayjs {
  return storeWallClockToDayjs(utcToStoreWallClock(typeof utc === 'string' ? new Date(utc) : utc, timeZoneId))
}

/** 日期选择器里的值（门店墙钟）→ UTC ISO 字符串（默认按悉尼）。仅作旧后端兜底，新后端以 fromLocal / toLocal 为准。 */
export function storePickerValueToUtcIso(value: Dayjs, timeZoneId?: string | null): string {
  return storeWallClockToUtc(
    {
      year: value.year(),
      month: value.month() + 1,
      day: value.date(),
      hour: value.hour(),
      minute: value.minute(),
      second: value.second(),
      millisecond: value.millisecond(),
    },
    timeZoneId,
  ).toISOString()
}

/** 日期选择器里的值 → 不带时区的门店本地时间字符串（YYYY-MM-DDTHH:mm:ss），交给后端按各分店时区换算。 */
export function storePickerValueToLocal(value: Dayjs): string {
  return value.format('YYYY-MM-DD[T]HH:mm:ss')
}

/** 按分店时区显示 UTC 时间，默认 `YYYY-MM-DD HH:mm:ss`；空值或非法值显示 —，没有时区按悉尼。 */
export function formatStoreTime(
  value: string | null | undefined,
  timeZoneId?: string | null,
  format: TimeFormat = 'seconds',
): string {
  return formatInStoreTime(value, timeZoneId, format)
}

/** 只要 `HH:mm:ss`（详情里的时间线、同一天内的时刻）。 */
export function formatStoreClock(value: string | null | undefined, timeZoneId?: string | null): string {
  const text = formatStoreTime(value, timeZoneId, 'seconds')
  return text === EMPTY_VALUE ? text : text.slice(-8)
}
