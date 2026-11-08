import {
  formatLastYearWindowRange,
  hasLastYearSalesWindow,
  parseLastYearWindowDays,
  pickLastYearSalesQuantity,
} from '../lastYearSalesWindow'

function assertEqual<T>(actual: T, expected: T, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

// 本机存储值只认 30 / 60，其它一律回到默认 30 天。
assertEqual(parseLastYearWindowDays('60'), 60, '存储 60 读回 60 天')
assertEqual(parseLastYearWindowDays('30'), 30, '存储 30 读回 30 天')
assertEqual(parseLastYearWindowDays(null), 30, '没存过默认 30 天')
assertEqual(parseLastYearWindowDays('90'), 30, '非法值回到默认 30 天')

// 0 是有效值（去年有数据但没卖），只有缺失才隐藏。
const sample = { lastYearSalesQuantity30Days: 0, lastYearSalesQuantity60Days: 14 }
assertEqual(pickLastYearSalesQuantity(sample, 30), 0, '30 天没卖显示 0')
assertEqual(pickLastYearSalesQuantity(sample, 60), 14, '60 天取 60 天销量')
assertEqual(pickLastYearSalesQuantity({ lastYearSalesQuantity30Days: null }, 30), null, '后端返回 null 隐藏')
assertEqual(pickLastYearSalesQuantity(undefined, 30), null, '旧后端没有字段隐藏')

// 起点 2025-11-09：30 天到 12-08（同年只在终点写年份），60 天跨年到 2026-01-07。
assertEqual(formatLastYearWindowRange('2025-11-09T00:00:00', 30), '9 Nov – 8 Dec 2025', '30 天范围含首尾')
assertEqual(formatLastYearWindowRange('2025-11-09T00:00:00', 60), '9 Nov 2025 – 7 Jan 2026', '60 天跨年两端写年份')
assertEqual(formatLastYearWindowRange('2025-03-29', 30), '29 Mar – 27 Apr 2025', '跨夏令时结束日不偏移')
assertEqual(formatLastYearWindowRange(null, 30), null, '没有起点不显示范围')

assertEqual(hasLastYearSalesWindow({}), false, '没有数据不显示开关')
assertEqual(
  hasLastYearSalesWindow({ P1: { productCode: 'P1', cartQuantity: 0, lastYearWindowStartDate: null } }),
  false,
  '分店未启用或去年没数据不显示开关',
)
assertEqual(
  hasLastYearSalesWindow({
    P1: { productCode: 'P1', cartQuantity: 0 },
    P2: { productCode: 'P2', cartQuantity: 0, lastYearWindowStartDate: '2025-11-09T00:00:00' },
  }),
  true,
  '任一商品带回窗口就显示开关',
)

console.log('lastYearSalesWindow.test: ok')
