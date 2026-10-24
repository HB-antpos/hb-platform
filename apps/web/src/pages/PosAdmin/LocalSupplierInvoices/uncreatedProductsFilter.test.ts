import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

import {
  UNCREATED_PRODUCTS_FILTER_KEY,
  buildUncreatedProductsFilter,
} from './uncreatedProductsFilter'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    throw new Error(`${message}。Expected: ${JSON.stringify(expected)}, received: ${JSON.stringify(actual)}`)
  }
}

// 1) 请求条件：关闭时不带条件（请求体与旧版一致），开启时发 true。键名要与后端 NormalizeGridColumnId 认的一致。
assertEqual(buildUncreatedProductsFilter(false), null, '关闭时不发该条件')
assertEqual(
  buildUncreatedProductsFilter(true),
  { key: 'hasUncreatedProducts', value: { filterType: 'text', type: 'equals', filter: 'true' } },
  '开启时按 text equals true 发给后端',
)
assertEqual(UNCREATED_PRODUCTS_FILTER_KEY, 'hasUncreatedProducts', '键名与后端约定一致')

// 2) 源码约束：状态必须进 baseFilterModel（分段计数/分店单数与列表同口径）、参与「清空筛选」与是否显示清空按钮。
const source = readFileSync(resolve('src/pages/PosAdmin/LocalSupplierInvoices/index.tsx'), 'utf8')
const loadStart = source.indexOf('const loadData = async')
const baseStart = source.indexOf('const baseFilterModel', loadStart)
const filterStart = source.indexOf('const filterModel: Record<string, unknown> = { ...baseFilterModel }', baseStart)
assert(loadStart > 0 && baseStart > loadStart && filterStart > baseStart, '找不到 loadData 里的筛选条件拼装')
const baseSection = source.slice(baseStart, filterStart)
assert(
  baseSection.includes('buildUncreatedProductsFilter(hasUncreatedProducts)')
    && baseSection.includes('baseFilterModel[uncreatedFilter.key] = uncreatedFilter.value'),
  '「有商品未创建」必须写进 baseFilterModel，全部/待检测计数与分店单数才与列表同口径',
)

const resetStart = source.indexOf('const handleReset = () =>')
const resetEnd = source.indexOf('\n  }\n', resetStart)
assert(resetStart > 0 && resetEnd > resetStart, '找不到 handleReset')
assert(source.slice(resetStart, resetEnd).includes('setHasUncreatedProducts(false)'), '清空筛选要一并取消「有商品未创建」')

const activeStart = source.indexOf('const hasActiveFilters = Boolean(')
const activeEnd = source.indexOf('\n  )\n', activeStart)
assert(activeStart > 0 && activeEnd > activeStart, '找不到 hasActiveFilters')
assert(source.slice(activeStart, activeEnd).includes('hasUncreatedProducts'), '开启该筛选后要显示「清空筛选」按钮')

const checkboxStart = source.indexOf('className="lsi-uncreated-filter"')
assert(checkboxStart > 0, '筛选栏缺少「有商品未创建」复选项')
const checkboxSection = source.slice(checkboxStart, checkboxStart + 400)
assert(
  checkboxSection.includes('setHasUncreatedProducts(e.target.checked)') && checkboxSection.includes('requestFirstPage(true)'),
  '切换后要回到第一页重新查询（同时清空旧勾选）',
)

console.log('LocalSupplierInvoices uncreatedProductsFilter tests: ok')
