import { parseEmployeeLogSource, resolveEmployeeLogSource } from './employeeLogsSource'

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

const both = { canLegacy: true, canPos: true }
assertEqual(resolveEmployeeLogSource({ ...both }), 'legacy', '首次进入默认老收银')
assertEqual(resolveEmployeeLogSource({ ...both, remembered: 'pos' }), 'pos', '记住上次的选择')
assertEqual(resolveEmployeeLogSource({ ...both, requested: 'legacy', remembered: 'pos' }), 'legacy', '地址栏优先于记忆')
assertEqual(resolveEmployeeLogSource({ ...both, requested: 'bogus', remembered: 'pos' }), 'pos', '非法地址栏参数忽略')
assertEqual(resolveEmployeeLogSource({ canLegacy: false, canPos: true, requested: 'legacy' }), 'pos', '没有老收银权限时不能通过地址栏进入')
assertEqual(resolveEmployeeLogSource({ canLegacy: true, canPos: false, remembered: 'pos' }), 'legacy', '记住的来源无权限时回到有权限的来源')
assertEqual(resolveEmployeeLogSource({ canLegacy: false, canPos: false }), null, '两个权限都没有时不显示')
assertEqual(parseEmployeeLogSource('pos'), 'pos', '解析合法来源')
assertEqual(parseEmployeeLogSource(undefined), null, '空值解析为 null')

console.log('employee logs source tests passed')
