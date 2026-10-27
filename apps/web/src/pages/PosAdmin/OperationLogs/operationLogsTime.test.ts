import { readFileSync } from 'node:fs'

import dayjs from 'dayjs'

import {
  formatStoreClock,
  formatStoreTime,
  getStoreNow,
  storePickerValueToLocal,
  storePickerValueToUtcIso,
  storeWallClockToUtc,
  utcToStorePickerValue,
  utcToStoreWallClock,
} from './operationLogsTime'

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

const wall = (year: number, month: number, day: number, hour: number, minute: number, second: number) => ({
  year, month, day, hour, minute, second, millisecond: 0,
})

// ───────── 悉尼墙钟 ⇄ UTC（夏令时 UTC+11） ─────────

// 现场案例：1018 店换货单 16:24:52（悉尼）= 05:24:52Z；日志页曾因浏览器时区不同查空
assertEqual(storeWallClockToUtc(wall(2026, 10, 27, 16, 24, 52)).toISOString(), '2026-10-27T05:24:52.000Z', '夏令时墙钟转 UTC')
assertEqual(storeWallClockToUtc(wall(2026, 10, 27, 16, 20, 24)).toISOString(), '2026-10-27T05:20:24.000Z', '范围起点')
assertEqual(storeWallClockToUtc(wall(2026, 10, 27, 16, 29, 24)).toISOString(), '2026-10-27T05:29:24.000Z', '范围终点')
assertEqual(JSON.stringify(utcToStoreWallClock(new Date('2026-10-27T05:24:52.123Z'))), JSON.stringify({ ...wall(2026, 10, 27, 16, 24, 52), millisecond: 123 }), 'UTC 转墙钟保留毫秒')

// 标准时间（UTC+10，4 月上旬夏令时结束后）
assertEqual(storeWallClockToUtc(wall(2026, 7, 1, 10, 0, 0)).toISOString(), '2026-07-01T00:00:00.000Z', '标准时间墙钟转 UTC')

// 午夜不能出现 24:00，跨日按悉尼日历日
assertEqual(utcToStoreWallClock(new Date('2026-10-26T13:00:00Z')).hour, 0, '悉尼午夜')
assertEqual(utcToStoreWallClock(new Date('2026-10-26T13:00:00Z')).day, 27, '悉尼午夜已是次日')

// 夏令时切换日（2026-10-04 02:00 悉尼拨快 1 小时；2026-04-05 03:00 拨回）：往返不丢失
assertEqual(storeWallClockToUtc(wall(2026, 10, 4, 1, 59, 59)).toISOString(), '2026-10-03T15:59:59.000Z', '切换前一秒仍是 UTC+10')
assertEqual(storeWallClockToUtc(wall(2026, 10, 4, 3, 0, 0)).toISOString(), '2026-10-03T16:00:00.000Z', '切换后第一秒是 UTC+11')
for (const iso of ['2026-10-03T15:59:59.000Z', '2026-10-03T16:00:00.000Z', '2026-04-04T14:59:59.000Z', '2026-04-04T17:00:00.000Z']) {
  assertEqual(storeWallClockToUtc(utcToStoreWallClock(new Date(iso))).toISOString(), iso, `往返 ${iso}`)
}
// 夏令时结束日 02:00–03:00 在墙钟上出现两次，选择器无法区分，固定取后一次（标准时间 UTC+10）
assertEqual(storeWallClockToUtc(wall(2026, 4, 5, 2, 30, 0)).toISOString(), '2026-04-04T16:30:00.000Z', '重复的一小时取后一次')

// ───────── 日期选择器容器 ─────────

// 选择器里的 16:20:24 是悉尼墙钟，提交即 05:20:24Z，与浏览器时区无关
assertEqual(storePickerValueToUtcIso(dayjs('2026-10-27 16:20:24')), '2026-10-27T05:20:24.000Z', '选择器值转 UTC')
const pickerValue = utcToStorePickerValue('2026-10-27T05:24:52.000Z')
assertEqual(pickerValue.format('YYYY-MM-DD HH:mm:ss'), '2026-10-27 16:24:52', 'UTC 回填到选择器显示悉尼墙钟')
assertEqual(storePickerValueToUtcIso(pickerValue), '2026-10-27T05:24:52.000Z', '回填后再提交不漂移')
// 「查看该员工当天」：按悉尼自然日的起止
const cashierDay = utcToStorePickerValue('2026-10-26T13:30:00Z') // 悉尼 10-27 00:30
assertEqual(storePickerValueToUtcIso(cashierDay.startOf('day')), '2026-10-26T13:00:00.000Z', '悉尼当天 00:00 的 UTC')
assertEqual(storePickerValueToUtcIso(cashierDay.endOf('day')), '2026-10-27T12:59:59.999Z', '悉尼当天 23:59:59.999 的 UTC')

// 默认范围：此刻按悉尼墙钟
const now = getStoreNow(new Date('2026-10-27T05:24:52.000Z'))
assertEqual(now.format('YYYY-MM-DD HH:mm:ss'), '2026-10-27 16:24:52', '门店此刻')
assertEqual(storePickerValueToUtcIso(now.subtract(7, 'day')), '2026-10-20T05:24:52.000Z', '近 7 天起点')

// ───────── 显示 ─────────

assertEqual(formatStoreTime('2026-10-27T05:24:53.033Z'), '2026-10-27 16:24:53', '列表时间按悉尼显示')
assertEqual(formatStoreTime('2026-10-27T05:24:53.033'), '2026-10-27 16:24:53', '缺 Z 后缀按 UTC 解释')
assertEqual(formatStoreClock('2026-10-27T05:24:53.033Z'), '16:24:53', '时间线只显示时分秒')
assertEqual(formatStoreClock('2026-10-26T13:00:00Z'), '00:00:00', '午夜')
assertEqual(formatStoreTime(null), '—', '空值')
assertEqual(formatStoreClock(undefined), '—', '空值时分秒')
assertEqual(formatStoreClock('garbage'), '—', '非法值')

// ───────── 分店时区：昆士兰店（Brisbane，UTC+10 不做夏令时）与悉尼店（UTC+11）同一时刻差 1 小时 ─────────

// 现场实测：1014 店（昆士兰）订单墙钟 17:28:05 对应审计 UTC 07:28:05；1018 店（悉尼）16:53:30 对应 05:53:31
assertEqual(formatStoreTime('2026-10-27T07:28:05.720Z', 'Australia/Brisbane'), '2026-10-27 17:28:05', '昆士兰店按 UTC+10 显示')
assertEqual(formatStoreTime('2026-10-27T05:53:31.003Z', 'Australia/Sydney'), '2026-10-27 16:53:31', '悉尼店按 UTC+11 显示')
assertEqual(formatStoreClock('2026-10-27T07:28:05.720Z', 'Australia/Brisbane'), '17:28:05', '昆士兰店时分秒')
// 没配时区 / 旧后端没返回 / 无法识别：回退悉尼
assertEqual(formatStoreTime('2026-10-27T05:24:53Z', null), '2026-10-27 16:24:53', '缺时区回退悉尼')
assertEqual(formatStoreTime('2026-10-27T05:24:53Z', undefined), '2026-10-27 16:24:53', '未返回时区回退悉尼')
assertEqual(formatStoreTime('2026-10-27T05:24:53Z', 'Not/AZone'), '2026-10-27 16:24:53', '无法识别回退悉尼')
// 标准时间期间两地一致（7 月悉尼也是 UTC+10）
assertEqual(formatStoreTime('2026-07-01T00:00:00Z', 'Australia/Brisbane'), formatStoreTime('2026-07-01T00:00:00Z', 'Australia/Sydney'), '冬令时两地相同')

assertEqual(storeWallClockToUtc(wall(2026, 10, 27, 16, 24, 52), 'Australia/Brisbane').toISOString(), '2026-10-27T06:24:52.000Z', '昆士兰墙钟转 UTC')
assertEqual(storePickerValueToUtcIso(dayjs('2026-10-27 16:24:52'), 'Australia/Brisbane'), '2026-10-27T06:24:52.000Z', '选择器值按昆士兰换算')
// 「查看该员工当天」用这条日志所属分店的自然日：昆士兰店 07:28Z = 17:28 当地，当天 00:00 = 前一天 14:00Z
const brisbaneDay = utcToStorePickerValue('2026-10-27T07:28:05Z', 'Australia/Brisbane')
assertEqual(brisbaneDay.format('YYYY-MM-DD HH:mm:ss'), '2026-10-27 17:28:05', '回填昆士兰墙钟')
assertEqual(storePickerValueToUtcIso(brisbaneDay.startOf('day'), 'Australia/Brisbane'), '2026-10-26T14:00:00.000Z', '昆士兰当天 00:00 的 UTC')

// 交给后端的门店本地时间字符串：不带时区、不受浏览器时区影响
assertEqual(storePickerValueToLocal(dayjs('2026-10-27 16:20:24')), '2026-10-27T16:20:24', '门店本地时间字符串')
assertEqual(storePickerValueToLocal(utcToStorePickerValue('2026-10-27T05:24:52Z')), '2026-10-27T16:24:52', '悉尼墙钟回填后再取字符串')

// ───────── 页面源码约束：不得再按浏览器时区取值 / 显示 ─────────

const pageSource = readFileSync('src/pages/PosAdmin/OperationLogs/index.tsx', 'utf8')
const detailSource = readFileSync('src/pages/PosAdmin/OperationLogs/PosLogDetailPanel.tsx', 'utf8')
for (const [name, source] of [['列表页', pageSource], ['详情面板', detailSource]] as const) {
  assertEqual(/\bdayjs\(/.test(source), false, `${name}不得用 dayjs() 按浏览器时区取时间`)
  assertEqual(source.includes('.toISOString()'), false, `${name}不得把选择器值直接 toISOString`)
}
assertEqual(pageSource.includes('storePickerValueToUtcIso(from)') && pageSource.includes('storePickerValueToUtcIso(to)'), true, '旧后端兜底范围按悉尼换算')
assertEqual(pageSource.includes('storePickerValueToLocal(from)') && pageSource.includes('storePickerValueToLocal(to)'), true, '查询范围以门店本地时间交给后端')
assertEqual(pageSource.includes('formatStoreTime(value, record.storeTimeZoneId)'), true, '列表时间按分店时区显示')
assertEqual(detailSource.includes('record.storeTimeZoneId'), true, '详情时间按分店时区显示')
