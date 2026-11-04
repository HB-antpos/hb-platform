import type { PlanogramListItemDto, PlanogramStatus } from '../../../../types/planogram'

/** 方案列表的纯逻辑：状态筛选、行操作的可用性规则、新建 / 另存为的默认值。 */

export type PlanStatusFilter = 'all' | 'draft' | 'published' | 'archived'

export function statusFilterToQuery(filter: PlanStatusFilter): PlanogramStatus | undefined {
  switch (filter) {
    case 'draft':
      return 0
    case 'published':
      return 1
    case 'archived':
      return 2
    default:
      return undefined
  }
}

/** 新建方案弹窗的默认值：2 节（常见门店陈列）、左右 / 上下间距 10 mm（决策 #9）。 */
export const NEW_PLAN_DEFAULTS = { bayCount: 2, gapXMm: 10, gapYMm: 10 } as const
export const BAY_COUNT_RANGE = { min: 1, max: 12 } as const
export const GAP_RANGE = { min: 0, max: 200 } as const

/** 仅「草稿且从未发布」的方案可以删除（附录 B.2：否则 PLANOGRAM_NOT_DELETABLE）。 */
export function isPlanDeletable(plan: Pick<PlanogramListItemDto, 'status' | 'publishedVersion'>): boolean {
  return plan.status === 0 && !plan.publishedVersion
}

export interface PlanRowActions {
  /** 主操作：有编辑权限且未归档 = 打开（可编辑）；其余 = 查看（设计器只读）。 */
  primary: 'open' | 'view'
  canDuplicate: boolean
  canArchive: boolean
  canRestore: boolean
  canDelete: boolean
  /** 导出 PDF：查看权限即可（含已归档方案）。 */
  canExportPdf: boolean
}

/**
 * 行操作按权限与状态决定（权限与后端一致：复制 = Edit；归档 / 恢复 / 删除 = Manage）：
 * 已归档的方案只能查看 / 复制 / 恢复，不能打开编辑，也不能删除。
 */
export function planRowActions(plan: Pick<PlanogramListItemDto, 'status' | 'publishedVersion'>, permissions: { canEdit: boolean; canManage: boolean; canView?: boolean }): PlanRowActions {
  const archived = plan.status === 2
  return {
    canExportPdf: permissions.canView !== false,
    primary: permissions.canEdit && !archived ? 'open' : 'view',
    canDuplicate: permissions.canEdit,
    canArchive: permissions.canManage && !archived,
    canRestore: permissions.canManage && archived,
    canDelete: permissions.canManage && isPlanDeletable(plan),
  }
}

/** 另存为 / 复制的默认名称：原名 + 副本后缀，截到 100 个字符以内（名称上限）。 */
export function defaultCopyName(name: string, suffix: string): string {
  const base = name.trim()
  const full = `${base}${suffix}`
  if (full.length <= 100) return full
  return `${base.slice(0, Math.max(0, 100 - suffix.length))}${suffix}`
}

export const PLAN_STATUS_KEYS: Record<PlanogramStatus, string> = {
  0: 'planogram.plans.status.draft',
  1: 'planogram.plans.status.published',
  2: 'planogram.plans.status.archived',
}

export type PlanStatusTone = 'orange' | 'green' | 'gray'

export interface PlanStatusView {
  tone: PlanStatusTone
  /** 列表里状态胶囊的短文案（草稿 / 已发布 / 已归档）。 */
  shortKey: string
  /** 页头标签的完整文案：草稿 / 已发布 vN / 已发布 vN · 有未发布修改 / 已归档。 */
  longKey: string
  /** 完整文案里的 {{version}}（已发布的版本号；其余情况没有）。 */
  version: number | null
  /** 已发布方案被保存后回到草稿（status == 0 且有已发布版本）：列表在胶囊旁补「有未发布修改」。 */
  hasUnpublishedChanges: boolean
}

/**
 * 方案状态的展示：列表状态列与设计器页头共用同一个判定，文案键都以字面量写在这里（契约测试按字面量核对键是否存在）。
 * - 已归档 → 灰；已发布 → 绿「已发布 vN」；
 * - 草稿且有已发布版本（hasUnpublishedChanges）→ 橙「已发布 vN · 有未发布修改」；
 * - 其余草稿 → 橙「草稿」。
 */
export function planStatusView(plan: Pick<PlanogramListItemDto, 'status' | 'publishedVersion' | 'hasUnpublishedChanges' | 'version'>): PlanStatusView {
  if (plan.status === 2) {
    return { tone: 'gray', shortKey: 'planogram.plans.status.archived', longKey: 'planogram.plans.status.archived', version: null, hasUnpublishedChanges: false }
  }
  if (plan.status === 1) {
    return { tone: 'green', shortKey: 'planogram.plans.status.published', longKey: 'planogram.plans.status.publishedVersion', version: plan.publishedVersion ?? plan.version, hasUnpublishedChanges: false }
  }
  if (plan.hasUnpublishedChanges && plan.publishedVersion) {
    return { tone: 'orange', shortKey: 'planogram.plans.status.draft', longKey: 'planogram.plans.status.publishedDirty', version: plan.publishedVersion, hasUnpublishedChanges: true }
  }
  return { tone: 'orange', shortKey: 'planogram.plans.status.draft', longKey: 'planogram.plans.status.draft', version: null, hasUnpublishedChanges: false }
}
