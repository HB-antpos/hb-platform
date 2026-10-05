import {
  KNOWN_STORE_BRANDS,
  cashRegisterFilterFromValue,
  cashRegisterFilterToValue,
  formatTimestampText,
  mergeBrandNames,
} from './storeListLogic'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) {
    throw new Error(message)
  }
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}: expected ${String(expected)}, got ${String(actual)}`)
  }
}

const known = [...KNOWN_STORE_BRANDS]

// 没有新品牌时必须返回同一个数组引用，调用方据此跳过 setState。
assert(mergeBrandNames(known, [{ brandName: 'hot bargain' }, { brandName: '  Dollar King ' }, { brandName: '' }, {}]) === known, '只有已知品牌（大小写/空白不同）时不应产生新数组')

const merged = mergeBrandNames(known, [{ brandName: 'Cheap Mart' }, { brandName: 'cheap mart' }, { brandName: null }])
assertEqual(merged.join('|'), 'Hot Bargain|Discount General|Dollar King|Cheap Mart', '新品牌追加到末尾，并保留首次出现的写法')
assert(merged !== known, '出现新品牌时应返回新数组')
assertEqual(known.length, 3, '不得修改传入的数组')

assertEqual(cashRegisterFilterToValue(undefined), 'all', '未设置收银筛选对应「全部」')
assertEqual(cashRegisterFilterToValue(true), 'enabled', 'true 对应「已启用」')
assertEqual(cashRegisterFilterToValue(false), 'disabled', 'false 对应「未启用」')
assertEqual(cashRegisterFilterFromValue('all'), undefined, '「全部」不应带 isActive 参数')
assertEqual(cashRegisterFilterFromValue('enabled'), true, '「已启用」对应 true')
assertEqual(cashRegisterFilterFromValue('disabled'), false, '「未启用」对应 false，不得被当成未设置')

assertEqual(formatTimestampText('2026-09-28T17:40:12.123'), '2026-09-28 17:40', 'ISO 时间只整理文本，不做时区换算')
assertEqual(formatTimestampText(undefined), '--', '缺失时间显示占位')

console.log('storeListLogic.test: ok')
