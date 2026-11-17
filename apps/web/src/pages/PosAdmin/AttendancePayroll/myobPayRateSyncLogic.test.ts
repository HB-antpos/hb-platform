import assert from 'node:assert/strict'
import type { MyobPayRateSyncCell, MyobPayRateSyncKind, MyobPayRateSyncRow } from '../../../types/myobPayRateSync'
import {
  buildApplyItems,
  buildDefaultSelection,
  columnState,
  countOverwrites,
  countSelection,
  failureMessageKey,
  hasApplicableCell,
  mostCommonConnection,
  pickConnection,
  reasonMessageKey,
  setColumn,
  sortSyncRows,
  summarizeKinds,
  toggleCell,
} from './myobPayRateSyncLogic'

const blank = (): MyobPayRateSyncCell => ({
  ourRate: null, suggested: null, multiplier: null, canApply: false, unavailableReason: null, defaultSelected: false,
})
const cell = (patch: Partial<MyobPayRateSyncCell>): MyobPayRateSyncCell => ({ ...blank(), ...patch })

function row(userGuid: string, kind: MyobPayRateSyncKind, patch: Partial<MyobPayRateSyncRow> = {}): MyobPayRateSyncRow {
  return {
    userGuid, userName: userGuid, employmentType: 'PartTime', kind, skipReason: null, payMode: null,
    myobHourlyRate: 30, rounded: false,
    weekday: blank(), saturday: blank(), sunday: blank(), publicHoliday: blank(),
    hash: `h-${userGuid}`, ...patch,
  }
}

// 新建：平日 + 公假默认勾，周末不勾（周末是建议值）
const create = row('a', 'Create', {
  weekday: cell({ suggested: 30, canApply: true, defaultSelected: true }),
  saturday: cell({ suggested: 37.5, multiplier: 1.25, canApply: true }),
  sunday: cell({ suggested: 45, multiplier: 1.5, canApply: true }),
  publicHoliday: cell({ suggested: 67.5, multiplier: 2.25, canApply: true, defaultSelected: true }),
})
// 覆盖：我方 28，MYOB 30，默认都不勾
const differ = row('b', 'Differ', {
  weekday: cell({ ourRate: 28, suggested: 30, canApply: true }),
})
// 已一致：平日不可写，周六有建议
const same = row('c', 'Same', {
  weekday: cell({ ourRate: 30, suggested: 30 }),
  saturday: cell({ ourRate: 30, suggested: 37.5, canApply: true }),
})
const skip = row('d', 'Skip', {
  skipReason: 'MYOB_RATE_NOT_HOURLY',
  // 即使后端字段异常地带了 canApply，Skip 行也绝不能被勾选 / 提交
  weekday: cell({ suggested: 30, canApply: true, defaultSelected: true }),
})
const rows = [skip, same, differ, create]

assert.equal(hasApplicableCell(create), true)
assert.equal(hasApplicableCell(same), true)
assert.equal(hasApplicableCell(skip), false)
assert.equal(hasApplicableCell(row('e', 'Same', { weekday: cell({ ourRate: 30, suggested: 30 }) })), false)

// 默认勾选：只有 canApply 且 defaultSelected，Skip 不参与
const initial = buildDefaultSelection(rows)
assert.deepEqual(initial, { a: ['weekday', 'publicHoliday'] })

// 勾选 / 取消：固定顺序，不能写的格与 Skip 行忽略
let selection = toggleCell(initial, create, 'saturday', true)
assert.deepEqual(selection.a, ['weekday', 'saturday', 'publicHoliday'])
selection = toggleCell(selection, create, 'weekday', false)
assert.deepEqual(selection.a, ['saturday', 'publicHoliday'])
assert.equal(toggleCell(selection, same, 'weekday', true), selection, '平日已一致，不可勾')
assert.equal(toggleCell(selection, skip, 'weekday', true), selection, 'Skip 行不可勾')
selection = toggleCell(selection, create, 'saturday', false)
selection = toggleCell(selection, create, 'publicHoliday', false)
assert.equal('a' in selection, false, '一格都没勾就从选择里移除')

// 整列
const column = setColumn({}, rows, 'weekday', true)
assert.deepEqual(column, { a: ['weekday'], b: ['weekday'] })
assert.deepEqual(columnState(column, rows, 'weekday'), { total: 2, selected: 2 })
assert.deepEqual(columnState({}, rows, 'saturday'), { total: 2, selected: 0 })
assert.deepEqual(setColumn(column, rows, 'weekday', false), {})

// 统计与覆盖计数：我方有值才算覆盖
const picked = { a: ['weekday', 'saturday'], b: ['weekday'], c: ['saturday'], d: ['weekday'] } as const
assert.deepEqual(countSelection(picked as never, rows), { employees: 3, cells: 4 })
assert.equal(countOverwrites(picked as never, rows), 2, 'b 平日 28→30、c 周六 30→37.5')

// apply 请求项：带预览哈希，Skip 行与已不可写的格不出现
assert.deepEqual(buildApplyItems(picked as never, rows), [
  { userGuid: 'c', hash: 'h-c', cells: ['saturday'] },
  { userGuid: 'b', hash: 'h-b', cells: ['weekday'] },
  { userGuid: 'a', hash: 'h-a', cells: ['weekday', 'saturday'] },
])
assert.deepEqual(buildApplyItems({}, rows), [])

// 排序与摘要
assert.deepEqual(sortSyncRows(rows).map((item) => item.userGuid), ['a', 'b', 'c', 'd'])
assert.deepEqual(summarizeKinds(rows), { Create: 1, Fill: 0, Differ: 1, Same: 1, Skip: 1 })

// 文案键：已知码按码，未知码兜底
assert.equal(reasonMessageKey('CATEGORY_NOT_MAPPED'), 'attendancePayroll.rates.sync.reason.CATEGORY_NOT_MAPPED')
assert.equal(reasonMessageKey('SOMETHING_NEW'), 'attendancePayroll.rates.sync.reason.unknown')
assert.equal(reasonMessageKey(null), 'attendancePayroll.rates.sync.reason.unknown')
assert.equal(failureMessageKey('MYOB_RATE_SYNC_VALUE_CHANGED'), 'attendancePayroll.rates.sync.failure.MYOB_RATE_SYNC_VALUE_CHANGED')
assert.equal(failureMessageKey('PERIOD_LOCKED'), null)

// 发薪公司选择
assert.equal(pickConnection([], 'x'), null)
assert.equal(pickConnection([{ connectionGuid: 'c1' }, { connectionGuid: 'c2' }], 'c2'), 'c2')
assert.equal(pickConnection([{ connectionGuid: 'c1' }, { connectionGuid: 'c2' }], 'gone'), 'c1')
assert.equal(mostCommonConnection([{ effectiveConnectionGuid: 'c1' }, { effectiveConnectionGuid: 'c2' }, { effectiveConnectionGuid: 'c2' }, { effectiveConnectionGuid: null }]), 'c2')
assert.equal(mostCommonConnection([]), null)

console.log('myobPayRateSyncLogic tests passed')
