import type {
  MyobPayRateSyncApplyItem,
  MyobPayRateSyncCell,
  MyobPayRateSyncCellKey,
  MyobPayRateSyncKind,
  MyobPayRateSyncRow,
} from '../../../types/myobPayRateSync'

// 「从 MYOB 同步转账时薪」抽屉的纯逻辑：勾选状态、apply 请求、文案键。
// 原则：能不能写由后端标的 canApply 说了算；前端只负责「用户勾了哪些」，
// 且任何情况下都不会把 canApply=false 的格子带进请求（后端也会再校验一遍）。

export const CELL_KEYS: readonly MyobPayRateSyncCellKey[] = ['weekday', 'saturday', 'sunday', 'publicHoliday']

/** 勾选状态：userGuid → 已勾的格（没有键 = 一格都没勾）。 */
export type PayRateSyncSelection = Record<string, MyobPayRateSyncCellKey[]>

export function cellOf(row: MyobPayRateSyncRow, key: MyobPayRateSyncCellKey): MyobPayRateSyncCell {
  return row[key]
}

/** 这一行有没有任何一格能写（决定是否显示勾选框 / 是否算「待处理」）。 */
export function hasApplicableCell(row: MyobPayRateSyncRow): boolean {
  return row.kind !== 'Skip' && CELL_KEYS.some((key) => row[key].canApply)
}

/** 默认勾选：后端标了 defaultSelected 且确实能写的格（平日 Create / Fill 默认勾，覆盖与周末格默认不勾）。 */
export function buildDefaultSelection(rows: readonly MyobPayRateSyncRow[]): PayRateSyncSelection {
  const selection: PayRateSyncSelection = {}
  for (const row of rows) {
    if (row.kind === 'Skip') continue
    const keys = CELL_KEYS.filter((key) => row[key].canApply && row[key].defaultSelected)
    if (keys.length) selection[row.userGuid] = keys
  }
  return selection
}

/** 勾选 / 取消一格；不能写的格忽略，结果始终按固定顺序，便于比较与测试。 */
export function toggleCell(
  selection: PayRateSyncSelection,
  row: MyobPayRateSyncRow,
  key: MyobPayRateSyncCellKey,
  checked: boolean,
): PayRateSyncSelection {
  if (row.kind === 'Skip' || !row[key].canApply) return selection
  const current = new Set(selection[row.userGuid] ?? [])
  if (checked) current.add(key)
  else current.delete(key)
  const next = { ...selection }
  const keys = CELL_KEYS.filter((cell) => current.has(cell))
  if (keys.length) next[row.userGuid] = keys
  else delete next[row.userGuid]
  return next
}

/** 整列勾选 / 取消：只动这一列里能写的格。 */
export function setColumn(
  selection: PayRateSyncSelection,
  rows: readonly MyobPayRateSyncRow[],
  key: MyobPayRateSyncCellKey,
  checked: boolean,
): PayRateSyncSelection {
  return rows.reduce((current, row) => toggleCell(current, row, key, checked), selection)
}

/** 某一列的勾选概况：total = 能写的格数，selected = 已勾数（用于表头全选框的三态）。 */
export function columnState(
  selection: PayRateSyncSelection,
  rows: readonly MyobPayRateSyncRow[],
  key: MyobPayRateSyncCellKey,
): { total: number; selected: number } {
  let total = 0
  let selected = 0
  for (const row of rows) {
    if (row.kind === 'Skip' || !row[key].canApply) continue
    total += 1
    if (selection[row.userGuid]?.includes(key)) selected += 1
  }
  return { total, selected }
}

/** 已勾选的员工数与格数（只数当前预览里仍然能写的格）。 */
export function countSelection(
  selection: PayRateSyncSelection,
  rows: readonly MyobPayRateSyncRow[],
): { employees: number; cells: number } {
  let employees = 0
  let cells = 0
  for (const row of rows) {
    if (row.kind === 'Skip') continue
    const picked = (selection[row.userGuid] ?? []).filter((key) => row[key].canApply)
    if (!picked.length) continue
    employees += 1
    cells += picked.length
  }
  return { employees, cells }
}

/** 构造 apply 请求项：每人带预览时的哈希与勾选的格；一格都没勾的员工不出现。 */
export function buildApplyItems(
  selection: PayRateSyncSelection,
  rows: readonly MyobPayRateSyncRow[],
): MyobPayRateSyncApplyItem[] {
  const items: MyobPayRateSyncApplyItem[] = []
  for (const row of rows) {
    if (row.kind === 'Skip') continue
    const cells = CELL_KEYS.filter((key) => (selection[row.userGuid] ?? []).includes(key) && row[key].canApply)
    if (cells.length) items.push({ userGuid: row.userGuid, hash: row.hash, cells })
  }
  return items
}

/** 这一行是否会被「覆盖」我方已有的价（勾了 Differ 平日格，或周末 / 公假格的我方现值非空）：用于确认框里的提醒。 */
export function countOverwrites(selection: PayRateSyncSelection, rows: readonly MyobPayRateSyncRow[]): number {
  let count = 0
  for (const row of rows) {
    if (row.kind === 'Skip') continue
    for (const key of selection[row.userGuid] ?? []) {
      const cell = row[key]
      if (cell.canApply && cell.ourRate !== null) count += 1
    }
  }
  return count
}

const KIND_ORDER: Record<MyobPayRateSyncKind, number> = { Create: 0, Fill: 1, Differ: 2, Same: 3, Skip: 4 }

/** 展示顺序：先要处理的（新建 / 补空 / 不同），再已一致，最后不参与；同类按姓名。 */
export function sortSyncRows(rows: readonly MyobPayRateSyncRow[]): MyobPayRateSyncRow[] {
  return [...rows].sort((a, b) => KIND_ORDER[a.kind] - KIND_ORDER[b.kind] || a.userName.localeCompare(b.userName))
}

/** 各类别人数，表头摘要用。 */
export function summarizeKinds(rows: readonly MyobPayRateSyncRow[]): Record<MyobPayRateSyncKind, number> {
  const summary: Record<MyobPayRateSyncKind, number> = { Create: 0, Fill: 0, Differ: 0, Same: 0, Skip: 0 }
  for (const row of rows) summary[row.kind] += 1
  return summary
}

const REASON_KEYS = new Set([
  'MYOB_RATE_NOT_HOURLY',
  'MYOB_EMPLOYEE_TERMINATED',
  'MYOB_CARD_MISSING',
  'PAY_MODE_CASH',
  'CATEGORY_NOT_MAPPED',
  'CATEGORY_NOT_FOUND',
  'CATEGORY_NOT_ON_CARD',
  'CATEGORY_NOT_RATE_MULTIPLE',
])

/** 原因码 → 文案键；后端新增的未知码落到 unknown，不显示原始码。 */
export function reasonMessageKey(code: string | null | undefined): string {
  return code && REASON_KEYS.has(code) ? `attendancePayroll.rates.sync.reason.${code}` : 'attendancePayroll.rates.sync.reason.unknown'
}

/** 逐员工失败码 → 文案键；没有专门文案的（如 PERIOD_LOCKED）返回 null，界面直接显示后端 message。 */
const FAILURE_CODES = new Set([
  'MYOB_RATE_SYNC_NOT_IN_PREVIEW',
  'MYOB_RATE_SYNC_NOT_BOUND',
  'MYOB_RATE_SYNC_VALUE_CHANGED',
  'MYOB_RATE_SYNC_NOT_APPLICABLE',
])

export function failureMessageKey(code: string): string | null {
  return FAILURE_CODES.has(code) ? `attendancePayroll.rates.sync.failure.${code}` : null
}

/** 选择发薪公司：只有一家就直接用；多家时优先取传入的偏好（比如该分店多数员工的发薪公司）。 */
export function pickConnection(
  companies: readonly { connectionGuid: string }[],
  preferred?: string | null,
): string | null {
  if (!companies.length) return null
  if (preferred && companies.some((company) => company.connectionGuid === preferred)) return preferred
  return companies[0].connectionGuid
}

/** 该分店员工里最多人使用的发薪公司（作为公司下拉的默认值）；并列取先出现的，没有则 null。 */
export function mostCommonConnection(rows: readonly { effectiveConnectionGuid: string | null }[]): string | null {
  const counts = new Map<string, number>()
  for (const row of rows) {
    if (!row.effectiveConnectionGuid) continue
    counts.set(row.effectiveConnectionGuid, (counts.get(row.effectiveConnectionGuid) ?? 0) + 1)
  }
  let best: string | null = null
  let bestCount = 0
  for (const [guid, count] of counts) {
    if (count > bestCount) {
      best = guid
      bestCount = count
    }
  }
  return best
}
