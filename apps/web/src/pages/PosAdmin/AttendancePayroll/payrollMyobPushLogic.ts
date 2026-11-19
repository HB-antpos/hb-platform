// 工资表「推送到 MYOB」的纯逻辑：按分店找公司、按钮状态、行级推送状态。不依赖 React / antd，便于单元测试。

import type { MyobCompanyOption, MyobPreviewRow, MyobTimesheetPreview } from '../../../types/myob'
import { getPushStateView, type PushStateView } from '../Myob/myobPushLogic'

/** 分店映射到哪家 MYOB 公司：一家店最多映射一个公司；没有映射（或公司已停用）返回 null。 */
export function pickCompanyForStore(companies: readonly MyobCompanyOption[], storeCode: string | undefined): MyobCompanyOption | null {
  const code = (storeCode ?? '').trim().toLowerCase()
  if (!code) return null
  return companies.find((company) => company.storeCodes.some((item) => item.trim().toLowerCase() === code)) ?? null
}

export type PayrollPushLoadStatus = 'idle' | 'loading' | 'unmapped' | 'error' | 'ready'

export type PayrollPushButton =
  | { kind: 'hidden' }
  /** 本期还没提交锁定：按钮置灰并提示先锁定（此时还不知道服务器是否对该公司开放，所以不隐藏）。 */
  | { kind: 'needLock' }
  | { kind: 'loading' }
  | { kind: 'unmapped' }
  | { kind: 'error' }
  | { kind: 'ready' }

/**
 * 按钮显示规则（设计图）：
 * - 没有推送 / 编辑工资权限：不显示；
 * - 本期未锁定：灰色 + 提示先提交锁定；
 * - 已锁定：读到预览之前显示加载中；分店没映射公司 / 读取失败：灰色 + 原因；
 * - 服务器没对该公司开放推送（pushEnabled = false）：不显示；否则可点。
 */
export function getPayrollPushButton(input: {
  allowed: boolean
  locked: boolean
  status: PayrollPushLoadStatus
  pushEnabled: boolean | undefined
}): PayrollPushButton {
  if (!input.allowed) return { kind: 'hidden' }
  if (!input.locked) return { kind: 'needLock' }
  if (input.status === 'unmapped') return { kind: 'unmapped' }
  if (input.status === 'error') return { kind: 'error' }
  if (input.status !== 'ready') return { kind: 'loading' }
  return input.pushEnabled ? { kind: 'ready' } : { kind: 'hidden' }
}

/** 工资表「MYOB 推送」列的一行：没有可显示的状态为 null（显示 —）。 */
export interface PayrollPushCell {
  view: PushStateView
}

/**
 * 预览行 → 工资表上每人的推送状态。只列「应该推送」的人（有转账工时、没被拦截）和已经有推送记录的人；
 * 全现金 / 没有转账工时的人不推送，列里显示 —，不显示「未推送」误导人。
 */
export function buildPayrollPushCells(preview: MyobTimesheetPreview | null): Record<string, PayrollPushCell> {
  const cells: Record<string, PayrollPushCell> = {}
  for (const row of preview?.rows ?? []) {
    const cell = cellOf(row)
    if (cell) cells[row.userGuid] = cell
  }
  return cells
}

function cellOf(row: MyobPreviewRow): PayrollPushCell | null {
  const hasRecord = !(row.push.state === 'NotPushed' && row.push.lastStatus === null)
  if (hasRecord) return { view: getPushStateView(row.push) }
  if (row.status === 'Ready' || row.status === 'Blocked') return { view: getPushStateView(row.push) }
  return null
}
