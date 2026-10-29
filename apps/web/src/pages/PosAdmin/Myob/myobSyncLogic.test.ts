import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

import type {
  MyobSyncChange,
  MyobSyncEmployee,
  MyobSyncField,
  MyobSyncFieldStat,
  MyobSyncFailure,
} from '../../../types/myob'

import {
  SYNC_FIELD_GROUPS,
  buildApplyRequest,
  buildInitialChecked,
  buildPreviewRows,
  buildRescope,
  canSubmitSync,
  changeKey,
  checkAllFill,
  countByFilter,
  countFailedChanges,
  countSelectedFields,
  describeFetchAge,
  describeSyncFailure,
  getBatchStatusKey,
  getDefaultSelectedFields,
  getScopeOptions,
  getSyncErrorMessageKey,
  groupFieldStats,
  isFieldUnavailable,
  isUndoAvailable,
  listPendingSensitiveEmployees,
  listSelectedFields,
  resolveScopeGuids,
  selectAllFields,
  selectDiffFields,
  splitWritten,
  summarizeChecked,
  uncheckAll,
} from './myobSyncLogic'

// 数字对齐设计稿 02.png：11 人 / 23 项 / 已勾 20 / 6 项敏感 / 3 项覆盖（覆盖项默认不勾）
const SENSITIVE = new Set(['birthday', 'bank', 'super'])
function change(field: MyobSyncField, kind: 'Fill' | 'Overwrite' = 'Fill'): MyobSyncChange {
  return {
    field,
    sensitive: SENSITIVE.has(field),
    ourDisplay: kind === 'Fill' ? null : 'old',
    myobDisplay: 'new',
    kind,
    // 只补空模式：Fill 恒勾，Overwrite 不勾
    defaultChecked: kind === 'Fill',
    note: null,
    ourHash: `our-${field}`,
    myobHash: `myob-${field}`,
  }
}
function employee(
  userGuid: string,
  changes: MyobSyncChange[],
  extra: Partial<MyobSyncEmployee> = {},
): MyobSyncEmployee {
  return {
    userGuid,
    userName: userGuid.toUpperCase(),
    storeLabels: [],
    employeeType: null,
    hasPendingSensitiveRequest: false,
    sensitiveRevision: 7,
    changes,
    ...extra,
  }
}
const employees: MyobSyncEmployee[] = [
  employee('e1', [change('birthday'), change('phone')]),
  employee('e2', [change('phone', 'Overwrite'), change('address')]),
  employee('e3', [change('name', 'Overwrite'), change('bank'), change('super')], { hasPendingSensitiveRequest: true, sensitiveRevision: 9 }),
  employee('e4', [change('employmentType', 'Overwrite'), change('address')]),
  employee('e5', [change('phone'), change('gender')]),
  employee('e6', [change('phone')]),
  employee('e7', [change('birthday'), change('phone')]),
  employee('e8', [change('birthday'), change('gender')]),
  employee('e9', [change('bank'), change('phone')]),
  employee('e10', [change('gender'), change('address'), change('phone')]),
  employee('e11', [change('address'), change('phone')]),
]

// ───── 统计与默认勾选 ─────
const initial = buildInitialChecked(employees)
assert.equal(Object.keys(initial).length, 23)
const summary = summarizeChecked(employees, initial)
assert.deepEqual(summary, { changeCount: 20, employeeCount: 11, sensitiveCount: 6, overwriteCount: 0 })
assert.deepEqual(countByFilter(employees), { all: 23, overwrite: 3, sensitive: 6 })

// ───── 筛选 + rowSpan ─────
const allRows = buildPreviewRows(employees, 'all')
assert.equal(allRows.length, 23)
assert.equal(allRows[0].rowSpan, 2)
assert.equal(allRows[1].rowSpan, 0)
// 每个员工的 rowSpan 之和 = 该员工可见行数
assert.equal(allRows.reduce((sum, row) => sum + row.rowSpan, 0), 23)

const overwriteRows = buildPreviewRows(employees, 'overwrite')
assert.deepEqual(
  overwriteRows.map((row) => [row.employee.userGuid, row.change.field, row.rowSpan]),
  [['e2', 'phone', 1], ['e3', 'name', 1], ['e4', 'employmentType', 1]],
)
const sensitiveRows = buildPreviewRows(employees, 'sensitive')
assert.equal(sensitiveRows.length, 6)
// e3 有 bank + super 两条敏感项：合并成一个 rowSpan=2
const e3Rows = sensitiveRows.filter((row) => row.employee.userGuid === 'e3')
assert.deepEqual(e3Rows.map((row) => row.rowSpan), [2, 0])
// 没有敏感项的员工整个不出现
assert.ok(!sensitiveRows.some((row) => row.employee.userGuid === 'e6'))

// ───── 批量勾选 ─────
const cleared = uncheckAll(initial)
assert.equal(summarizeChecked(employees, cleared).changeCount, 0)
// 「全部勾选补空项」只动 Fill：覆盖项保持原状态
const overwriteOn = { ...cleared, [changeKey('e2', 'phone')]: true }
const filled = checkAllFill(employees, overwriteOn)
const filledSummary = summarizeChecked(employees, filled)
assert.equal(filledSummary.changeCount, 21)
assert.equal(filledSummary.overwriteCount, 1)

// ───── 提交门槛：敏感项未核对不可提交 ─────
assert.equal(canSubmitSync(summary, false), false)
assert.equal(canSubmitSync(summary, true), true)
assert.equal(canSubmitSync({ changeCount: 0, employeeCount: 0, sensitiveCount: 0, overwriteCount: 0 }, true), false)
// 没勾敏感项时不需要核对
assert.equal(canSubmitSync({ changeCount: 3, employeeCount: 1, sensitiveCount: 0, overwriteCount: 1 }, false), true)

// ───── apply 请求：只含勾选项、带预览版本号与哈希 ─────
const request = buildApplyRequest('c1', employees, initial)
assert.ok(request)
assert.equal(request.connectionGuid, 'c1')
assert.equal(request.items.length, 11)
// e3：敏感项勾选 + 有待审申请 → 整次请求带确认作废标志；name 覆盖项没勾所以不在 changes 里
const e3Item = request.items.find((item) => item.userGuid === 'e3')
assert.deepEqual(e3Item, {
  userGuid: 'e3',
  expectedSensitiveRevision: 9,
  changes: [
    { field: 'bank', ourHash: 'our-bank', myobHash: 'myob-bank' },
    { field: 'super', ourHash: 'our-super', myobHash: 'myob-super' },
  ],
})
assert.equal(request.confirmSupersedePendingSensitiveRequests, true)
assert.equal(request.items.reduce((sum, item) => sum + item.changes.length, 0), 20)

// 只勾了 e3 的非敏感项（name）→ 不需要确认作废
const onlyName = buildApplyRequest('c1', employees, { ...cleared, [changeKey('e3', 'name')]: true })
assert.ok(onlyName)
assert.equal(onlyName.items.length, 1)
assert.equal(onlyName.confirmSupersedePendingSensitiveRequests, false)
// 勾了敏感项但员工没有待审申请 → 也不带标志
const onlyBirthday = buildApplyRequest('c1', employees, { ...cleared, [changeKey('e1', 'birthday')]: true })
assert.equal(onlyBirthday?.confirmSupersedePendingSensitiveRequests, false)
// 没有任何勾选 → null（不发请求）
assert.equal(buildApplyRequest('c1', employees, cleared), null)
// 待审申请点名：只含「有待审申请且含敏感变更」的员工
assert.deepEqual(listPendingSensitiveEmployees(employees).map((item) => item.userGuid), ['e3'])
assert.equal(listPendingSensitiveEmployees([employee('x', [change('phone')], { hasPendingSensitiveRequest: true })]).length, 0)

// ───── 第 1 步：字段分组 / 默认勾选 / 选择操作 ─────
function stat(field: MyobSyncField, fillable: number, differs: number): MyobSyncFieldStat {
  return {
    field,
    sensitive: SENSITIVE.has(field),
    defaultSelected: ['name', 'phone', 'gender', 'address', 'employmentType'].includes(field),
    myobHasValue: fillable + differs,
    fillable,
    differs,
  }
}
// 设计稿 01.png 的数字
const stats: MyobSyncFieldStat[] = [
  stat('name', 0, 1),
  stat('phone', 5, 1),
  stat('email', 0, 2),
  stat('birthday', 2, 0),
  stat('gender', 5, 0),
  stat('address', 4, 0),
  stat('employmentType', 0, 1),
  stat('bank', 2, 0),
  stat('super', 2, 0),
]
assert.deepEqual(getDefaultSelectedFields(stats), ['name', 'phone', 'gender', 'address', 'employmentType'])
assert.equal(selectAllFields(stats).length, 9)
assert.deepEqual(selectDiffFields([stat('name', 0, 0), stat('phone', 1, 0), stat('email', 0, 2)]), ['phone', 'email'])
// 底栏：「已选 8 个字段（含 3 个敏感）」
assert.deepEqual(
  countSelectedFields(stats, new Set<MyobSyncField>(['name', 'phone', 'birthday', 'gender', 'address', 'employmentType', 'bank', 'super'])),
  { fieldCount: 8, sensitiveCount: 3 },
)
assert.deepEqual(countSelectedFields(stats, new Set()), { fieldCount: 0, sensitiveCount: 0 })
// MYOB 读不到的字段（如银行）：不在默认勾选 / 全选 / 只选有差异 / 计数 / 请求字段里
assert.deepEqual(getDefaultSelectedFields(stats, ['name']), ['phone', 'gender', 'address', 'employmentType'])
assert.equal(selectAllFields(stats, ['bank', 'super']).length, 7)
assert.deepEqual(selectDiffFields([stat('phone', 1, 0), stat('bank', 2, 0)], ['bank']), ['phone'])
const withBank = new Set<MyobSyncField>(['phone', 'bank'])
assert.deepEqual(countSelectedFields(stats, withBank, ['bank']), { fieldCount: 1, sensitiveCount: 0 })
assert.deepEqual(listSelectedFields(stats, withBank, ['bank']), ['phone'])
assert.equal(isFieldUnavailable('bank', ['bank']), true)
assert.equal(isFieldUnavailable('phone', ['bank']), false)
// 请求里的字段顺序跟 stats 一致，而不是点选顺序
assert.deepEqual(listSelectedFields(stats, new Set<MyobSyncField>(['super', 'name'])), ['name', 'super'])
const groups = groupFieldStats(stats)
assert.deepEqual(groups.map((group) => [group.key, group.stats.length]), [['basic', 6], ['employment', 3]])
// 后端新增的未知字段并入最后一组，不丢
const withUnknown = groupFieldStats([...stats, { ...stat('name', 0, 0), field: 'newField' as MyobSyncField }])
assert.equal(withUnknown[1].stats.length, 4)
assert.equal(SYNC_FIELD_GROUPS.flatMap((group) => group.fields).length, 9)

// ───── 范围 ─────
assert.deepEqual(getScopeOptions(0, false), ['all'])
assert.deepEqual(getScopeOptions(3, false), ['selected', 'all'])
assert.deepEqual(getScopeOptions(1, true), ['selected'])
assert.equal(resolveScopeGuids('all', ['a', 'b']), undefined)
assert.deepEqual(resolveScopeGuids('selected', ['a', 'b']), ['a', 'b'])
assert.equal(resolveScopeGuids('selected', []), undefined)

// ───── 时间 ─────
const now = Date.parse('2026-10-29T01:00:00Z')
assert.deepEqual(describeFetchAge('2026-10-29T00:58:00', now), { key: 'myob.sync.age.minutes', count: 2 })
assert.deepEqual(describeFetchAge('2026-10-29T00:59:40Z', now), { key: 'myob.sync.age.justNow', count: 0 })
assert.deepEqual(describeFetchAge('2026-10-28T22:00:00Z', now), { key: 'myob.sync.age.hours', count: 3 })
assert.equal(describeFetchAge('', now), null)

// ───── 第 3 步：失败文案 / 折叠 / 撤销 ─────
const failure: MyobSyncFailure = {
  userGuid: 'e3',
  userName: 'Kathryn',
  fields: ['bank', 'super'],
  code: 'MYOB_SYNC_VALUE_CHANGED',
  message: '预览之后她的资料已被别人修改',
}
assert.deepEqual(describeSyncFailure(failure), { text: '预览之后她的资料已被别人修改', messageKey: null })
assert.deepEqual(describeSyncFailure({ ...failure, message: '  ' }), { text: null, messageKey: 'myob.sync.errors.valueChanged' })
assert.deepEqual(describeSyncFailure({ ...failure, message: '', code: 'WHATEVER' }), { text: null, messageKey: null })
assert.equal(getSyncErrorMessageKey('MYOB_SYNC_NO_PERMISSION'), 'myob.sync.errors.noPermission')
assert.equal(getSyncErrorMessageKey(null), null)
assert.equal(countFailedChanges([failure, { ...failure, fields: ['phone'] }]), 3)
assert.deepEqual(buildRescope(failure), { userGuids: ['e3'], fields: ['bank', 'super'] })

const written = Array.from({ length: 10 }, (_, index) => ({
  userGuid: `w${index}`,
  userName: `W${index}`,
  fields: (index < 5 ? ['phone', 'name'] : ['phone']) as MyobSyncField[],
}))
const split = splitWritten(written)
assert.equal(split.head.length, 5)
assert.equal(split.rest.length, 5)
assert.equal(split.restChangeCount, 5)
assert.equal(splitWritten(written.slice(0, 3)).rest.length, 0)

// 没有 batchGuid（全部失败）不显示撤销；过期不显示
assert.equal(isUndoAvailable({ batchGuid: null, undoDeadline: '2026-11-05T00:00:00Z' }, now), false)
assert.equal(isUndoAvailable({ batchGuid: 'b', undoDeadline: '2026-11-05T00:00:00' }, now), true)
assert.equal(isUndoAvailable({ batchGuid: 'b', undoDeadline: '2026-10-28T00:00:00Z' }, now), false)
assert.equal(isUndoAvailable({ batchGuid: 'b', undoDeadline: null }, now), false)

const batch = {
  batchGuid: 'b',
  createdAt: '',
  actorName: null,
  employeeCount: 1,
  changeCount: 1,
  sensitiveCount: 0,
  status: 'Active' as const,
  canUndo: true,
  undoDeadline: '2026-11-05T00:00:00Z',
  undoneAt: null,
  undoneByName: null,
}
assert.equal(getBatchStatusKey(batch, now), 'Active')
assert.equal(getBatchStatusKey({ ...batch, status: 'Undone' }, now), 'Undone')
assert.equal(getBatchStatusKey({ ...batch, undoDeadline: '2026-10-01T00:00:00Z' }, now), 'Expired')

// ───── 文案：zh / en 的 myob.sync.* 键完全一致，且代码里引用的静态键都存在 ─────
type Json = { [key: string]: Json | string }
function flatten(node: Json, prefix = ''): string[] {
  return Object.entries(node).flatMap(([key, value]) =>
    typeof value === 'string' ? [`${prefix}${key}`] : flatten(value, `${prefix}${key}.`),
  )
}
const zh = (JSON.parse(readFileSync(resolve('src/pages/PosAdmin/Myob/myobMessages.zh.json'), 'utf8')) as { myob: { sync: Json } }).myob.sync
const en = (JSON.parse(readFileSync(resolve('src/pages/PosAdmin/Myob/myobMessages.en.json'), 'utf8')) as { myob: { sync: Json } }).myob.sync
assert.deepEqual(flatten(zh).sort(), flatten(en).sort(), 'zh / en 的 myob.sync 键必须一致')
const zhKeys = new Set(flatten(zh).map((key) => `myob.sync.${key}`))
const referenced = [
  ...SYNC_FIELD_GROUPS.flatMap((group) => group.fields.flatMap((field) => [`myob.sync.fields.${field}`, `myob.sync.sources.${field}`])),
  'myob.sync.errors.noPermission',
  'myob.sync.errors.noFields',
  'myob.sync.errors.batchNotFound',
  'myob.sync.errors.batchAlreadyUndone',
  'myob.sync.errors.batchExpired',
  'myob.sync.errors.notBound',
  'myob.sync.errors.cardMissing',
  'myob.sync.errors.valueChanged',
  'myob.sync.errors.versionConflict',
  'myob.sync.errors.pendingNeedsConfirm',
  'myob.sync.errors.writeFailed',
  'myob.sync.step1.unavailable',
  'myob.sync.step2.unavailableBanner',
  'myob.sync.age.justNow',
  'myob.sync.age.minutes',
  'myob.sync.age.hours',
  'myob.sync.age.days',
  'myob.sync.batch.status.Active',
  'myob.sync.batch.status.Undone',
  'myob.sync.batch.status.Expired',
]
for (const key of referenced) {
  assert.ok(zhKeys.has(key), `缺少文案键 ${key}`)
}

console.log('myobSyncLogic.test: ok')
