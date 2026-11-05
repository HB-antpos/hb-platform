import { BAY_COUNT_RANGE, defaultCopyName, isPlanDeletable, NEW_PLAN_DEFAULTS, planRowActions, planStatusView, statusFilterToQuery } from './planListLogic'

// 方案列表的纯逻辑：状态筛选映射、行操作可用性（权限 + 状态）、默认值。

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

assertEqual(statusFilterToQuery('all'), undefined, '全部 = 不带 status 参数')
assertEqual(statusFilterToQuery('draft'), 0, '草稿 = 0')
assertEqual(statusFilterToQuery('published'), 1, '已发布 = 1')
assertEqual(statusFilterToQuery('archived'), 2, '已归档 = 2')

assertEqual(isPlanDeletable({ status: 0, publishedVersion: null }), true, '草稿且从未发布可以删')
assertEqual(isPlanDeletable({ status: 0, publishedVersion: undefined }), true, '没有 publishedVersion 字段也算从未发布')
assertEqual(isPlanDeletable({ status: 0, publishedVersion: 2 }), false, '发布过的方案被保存后回到草稿，仍不可删')
assertEqual(isPlanDeletable({ status: 1, publishedVersion: 1 }), false, '已发布不可删')
assertEqual(isPlanDeletable({ status: 2, publishedVersion: null }), false, '已归档不可删（先恢复）')

const manage = { canEdit: true, canManage: true }
const editOnly = { canEdit: true, canManage: false }
const viewOnly = { canEdit: false, canManage: false }
const draft = { status: 0 as const, publishedVersion: null }
const archived = { status: 2 as const, publishedVersion: 1 }

assertEqual(planRowActions(draft, manage).primary, 'open', '有编辑权限的草稿：打开')
assertEqual(planRowActions(draft, viewOnly).primary, 'view', '只有查看权限：查看')
assertEqual(planRowActions(archived, manage).primary, 'view', '已归档只能查看')
assertEqual(planRowActions(draft, editOnly).canDuplicate, true, 'Edit 可复制')
assertEqual(planRowActions(draft, viewOnly).canDuplicate, false, '只有 View 不能复制')
assertEqual(planRowActions(draft, editOnly).canArchive, false, 'Edit 不能归档（要 Manage）')
assertEqual(planRowActions(draft, editOnly).canDelete, false, 'Edit 不能删除（要 Manage）')
assertEqual(planRowActions(draft, manage).canDelete, true, 'Manage + 草稿未发布：可删')
assertEqual(planRowActions(draft, manage).canArchive, true, 'Manage 可归档')
assertEqual(planRowActions(draft, manage).canRestore, false, '草稿没有「恢复」')
assertEqual(planRowActions(archived, manage).canRestore, true, 'Manage 可恢复归档方案')
assertEqual(planRowActions(archived, manage).canArchive, false, '已归档不再有「归档」')
assertEqual(planRowActions(archived, manage).canDelete, false, '已归档不可删')
assertEqual(planRowActions(archived, editOnly).canRestore, false, 'Edit 不能恢复（要 Manage）')
assertEqual(planRowActions(archived, editOnly).canDuplicate, true, '归档方案仍可复制')

assertEqual(NEW_PLAN_DEFAULTS.gapXMm, 10, '左右间距默认 10')
assertEqual(NEW_PLAN_DEFAULTS.gapYMm, 10, '上下间距默认 10')
assertEqual(BAY_COUNT_RANGE.min, 1, '节数下限 1')
assertEqual(BAY_COUNT_RANGE.max, 12, '节数上限 12')

assertEqual(defaultCopyName(' 文具挂板 A1 ', ' 副本'), '文具挂板 A1 副本', '复制名称去首尾空格并加后缀')
assertEqual(defaultCopyName('x'.repeat(100), ' copy').length, 100, '超长名称截到 100 个字符以内')
assertEqual(defaultCopyName('x'.repeat(100), ' copy').endsWith(' copy'), true, '截断时后缀保留')

// 批 3：状态展示（列表状态列与设计器页头共用）
const draftNever = planStatusView({ status: 0, publishedVersion: null, hasUnpublishedChanges: false, version: 1 })
assertEqual(draftNever.longKey, 'planogram.plans.status.draft', '从未发布的草稿：草稿')
assertEqual(draftNever.tone, 'orange', '草稿橙色')
assertEqual(draftNever.hasUnpublishedChanges, false, '没有未发布修改')
const publishedNow = planStatusView({ status: 1, publishedVersion: 3, hasUnpublishedChanges: false, version: 3 })
assertEqual(publishedNow.longKey, 'planogram.plans.status.publishedVersion', '已发布：已发布 vN')
assertEqual(publishedNow.version, 3, '版本号取已发布版本')
assertEqual(publishedNow.tone, 'green', '已发布绿色')
assertEqual(publishedNow.shortKey, 'planogram.plans.status.published', '列表胶囊用短文案「已发布」（版本号在版本列）')
const publishedDirty = planStatusView({ status: 0, publishedVersion: 3, hasUnpublishedChanges: true, version: 4 })
assertEqual(publishedDirty.longKey, 'planogram.plans.status.publishedDirty', '已发布后被保存回到草稿：已发布 vN · 有未发布修改')
assertEqual(publishedDirty.version, 3, '提示里的版本号是对外的已发布版本，不是正在编辑的 v4')
assertEqual(publishedDirty.hasUnpublishedChanges, true, '列表据此补「有未发布修改」标签')
assertEqual(publishedDirty.shortKey, 'planogram.plans.status.draft', '列表胶囊仍是草稿')
assertEqual(publishedDirty.tone, 'orange', '橙色')
assertEqual(planStatusView({ status: 0, publishedVersion: 3, hasUnpublishedChanges: false, version: 4 }).longKey, 'planogram.plans.status.draft', '后端没给 hasUnpublishedChanges（缺省 false）就不显示「有未发布修改」')
assertEqual(planStatusView({ status: 2, publishedVersion: 3, hasUnpublishedChanges: true, version: 4 }).longKey, 'planogram.plans.status.archived', '已归档优先')
assertEqual(planStatusView({ status: 2, publishedVersion: null, hasUnpublishedChanges: false, version: 1 }).tone, 'gray', '已归档灰色')
assertEqual(planRowActions(draft, viewOnly).canExportPdf, true, '只有查看权限也能导出 PDF')
assertEqual(planRowActions(archived, viewOnly).canExportPdf, true, '已归档方案也能导出 PDF')
assertEqual(planRowActions(draft, { canEdit: false, canManage: false, canView: false }).canExportPdf, false, '没有查看权限不显示导出')

console.log('planListLogic.test: ok')
