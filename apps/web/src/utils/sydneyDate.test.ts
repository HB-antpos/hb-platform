import { formatSydneyDate, formatSydneyIsoDate, getSydneyDateTagColor } from './sydneyDate'

function assertEqual(actual: unknown, expected: unknown, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}，实际: ${String(actual)}，期望: ${String(expected)}`)
  }
}

function main() {
  assertEqual(
    formatSydneyDate('2026-06-26T00:00:00'),
    '2026/06/26',
    '无时区货柜日期应按悉尼业务日期显示且不显示时间',
  )

  assertEqual(
    formatSydneyDate('2026-06-25T14:30:00Z'),
    '2026/06/26',
    '带 UTC 时区的时间戳应转换成悉尼日期',
  )

  assertEqual(formatSydneyDate(undefined), '--', '空日期应显示占位符')
  assertEqual(formatSydneyDate('bad-date'), 'bad-date', '非法日期应保留原值便于排查数据')
  assertEqual(getSydneyDateTagColor('2026-06-26T00:00:00'), 'blue', '悉尼日期应有稳定颜色')
  assertEqual(getSydneyDateTagColor('2026-06-25T00:00:00'), 'cyan', '不同日期应映射到不同颜色')
  assertEqual(getSydneyDateTagColor(undefined), 'default', '空日期不应显示彩色标签')

  // 悉尼 00:00–09:59（AEST，UTC+10）对应 UTC 前一天 14:00–23:59，toISOString 截取会得到前一天。
  assertEqual(formatSydneyIsoDate(new Date('2026-09-28T13:59:00Z')), '2026-09-28', '悉尼前一天 23:59 仍应是前一天')
  assertEqual(formatSydneyIsoDate(new Date('2026-09-28T14:00:00Z')), '2026-09-29', '悉尼 00:00（UTC 仍是前一天）应取悉尼当天')
  assertEqual(formatSydneyIsoDate(new Date('2026-09-28T22:30:00Z')), '2026-09-29', '悉尼早上 08:30 应取悉尼当天')
  assertEqual(formatSydneyIsoDate(new Date('2026-09-28T23:59:00Z')), '2026-09-29', '悉尼 09:59 应取悉尼当天')
  assertEqual(formatSydneyIsoDate(new Date('2026-09-29T00:00:00Z')), '2026-09-29', '悉尼 10:00 起 UTC 与悉尼同日')

  // 夏令时（AEDT，UTC+11）受影响时段延长到 00:00–10:59。
  assertEqual(formatSydneyIsoDate(new Date('2026-12-14T13:00:00Z')), '2026-12-15', '夏令时悉尼 00:00 应取悉尼当天')
  assertEqual(formatSydneyIsoDate(new Date('2026-12-14T23:59:00Z')), '2026-12-15', '夏令时悉尼 10:59 应取悉尼当天')

  // 夏令时切换当天（2026-10-04 02:00 跳到 03:00；2027-04-04 03:00 回拨到 02:00）日期边界仍按悉尼午夜。
  assertEqual(formatSydneyIsoDate(new Date('2026-10-03T14:00:00Z')), '2026-10-04', '夏令时开始当天 00:00（AEST）应取当天')
  assertEqual(formatSydneyIsoDate(new Date('2026-10-04T12:59:00Z')), '2026-10-04', '夏令时开始当天 23:59（AEDT）仍是当天')
  assertEqual(formatSydneyIsoDate(new Date('2026-10-04T13:00:00Z')), '2026-10-05', '夏令时开始次日 00:00（AEDT）应换日')
  assertEqual(formatSydneyIsoDate(new Date('2027-04-03T13:00:00Z')), '2027-04-04', '夏令时结束当天 00:00（AEDT）应取当天')
  assertEqual(formatSydneyIsoDate(new Date('2027-04-04T13:59:00Z')), '2027-04-04', '夏令时结束当天 23:59（AEST）仍是当天')

  assertEqual(/^\d{4}-\d{2}-\d{2}$/.test(formatSydneyIsoDate()), true, '默认取当前时刻的悉尼日期，格式为 YYYY-MM-DD')

  console.log('sydneyDate.test: ok')
}

main()
