import type { ReactNode } from 'react'

/** 员工操作日志的数据来源：老收银（POSM.EmployeeLogs）/ 新收银（pos_operation_audit）。 */
export type EmployeeLogSource = 'legacy' | 'pos'

export const EMPLOYEE_LOG_SOURCE_STORAGE_KEY = 'hb.employeeLogs.source'
export const EMPLOYEE_LOG_SOURCE_PARAM = 'source'

/** 合并页传给两个来源页面的统一页头：标题、副标题与来源切换控件。 */
export interface EmployeeLogsHeader {
  title: string
  subtitle: string
  switcher?: ReactNode
}

export function parseEmployeeLogSource(value: string | null | undefined): EmployeeLogSource | null {
  return value === 'legacy' || value === 'pos' ? value : null
}

/**
 * 决定显示哪个来源：只能是当前账号有权限的来源；
 * 优先地址栏 ?source=，其次上次的选择，都没有时默认老收银（约 25 店仍在用）。
 * 两个权限都没有时返回 null。
 */
export function resolveEmployeeLogSource(input: {
  requested?: string | null
  remembered?: string | null
  canLegacy: boolean
  canPos: boolean
}): EmployeeLogSource | null {
  const allowed = (source: EmployeeLogSource | null) =>
    source === 'legacy' ? input.canLegacy : source === 'pos' ? input.canPos : false
  const requested = parseEmployeeLogSource(input.requested)
  if (allowed(requested)) return requested
  const remembered = parseEmployeeLogSource(input.remembered)
  if (allowed(remembered)) return remembered
  if (input.canLegacy) return 'legacy'
  return input.canPos ? 'pos' : null
}
