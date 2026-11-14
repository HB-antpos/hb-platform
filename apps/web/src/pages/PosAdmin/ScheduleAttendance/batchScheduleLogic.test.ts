import assert from 'node:assert/strict'

import type { DayAvailability } from './availabilityLogic'
import {
  applyBatchOverrides,
  batchCellKey,
  buildBatchPayloads,
  buildBatchPlan,
  runWithConcurrency,
  summarizePlan,
  summarizeResults,
} from './batchScheduleLogic'
import type { ExistingShift } from './batchScheduleLogic'

const segment = (startMin: number, endMin: number) => ({
  startMin,
  endMin,
  startLabel: `${String(Math.floor(startMin / 60)).padStart(2, '0')}:${String(startMin % 60).padStart(2, '0')}`,
  endLabel: `${String(Math.floor(endMin / 60)).padStart(2, '0')}:${String(endMin % 60).padStart(2, '0')}`,
  allDay: false,
})

const DAYS = ['2026-11-23', '2026-11-24', '2026-11-25', '2026-11-26', '2026-11-27']

const existing: Record<string, ExistingShift[]> = {
  // Charlie 周一周二已有 15:00–17:30；周三有一条已取消的同时段；周四只是首尾相接（13:00–15:00）
  'charlie|2026-11-23': [{ startTime: '15:00:00', endTime: '17:30:00', status: 'Active' }],
  'charlie|2026-11-24': [{ startTime: '15:00', endTime: '17:30', status: 'Draft' }],
  'charlie|2026-11-25': [{ startTime: '15:00', endTime: '17:30', status: 'Cancelled' }],
  'charlie|2026-11-26': [{ startTime: '13:00', endTime: '15:00', status: 'Draft' }],
  // Zoe 周五有 15:30–17:30（部分重叠也算重叠）
  'zoe|2026-11-27': [{ startTime: '15:30', endTime: '17:30', status: 'Active' }],
}
const availability: Record<string, DayAvailability> = {
  // Chaise 周五只上报可上班 12:00–16:00 → 15:00–17:30 超出
  'chaise|2026-11-27': { available: [segment(720, 960)], unavailable: [] },
  // Zoe 周三不能上班 15:00–18:00
  'zoe|2026-11-25': { available: [], unavailable: [segment(900, 1080)] },
}

const plan = buildBatchPlan({
  userGuids: ['chaise', 'charlie', 'zoe'],
  dateKeys: [...DAYS].reverse(), // 故意倒序：结果应按日期升序
  start: '15:00',
  end: '17:30',
  existingOf: (userGuid, dateKey) => existing[`${userGuid}|${dateKey}`] ?? [],
  availabilityOf: (userGuid, dateKey) => availability[`${userGuid}|${dateKey}`],
})

assert.equal(plan.length, 15)
// 顺序：先员工（传入顺序），再日期升序
assert.deepEqual(plan.slice(0, 5).map((cell) => cell.dateKey), DAYS)
assert.deepEqual(Array.from(new Set(plan.map((cell) => cell.userGuid))), ['chaise', 'charlie', 'zoe'])

const cell = (userGuid: string, dateKey: string) => plan.find((item) => item.userGuid === userGuid && item.dateKey === dateKey)!
assert.equal(cell('chaise', '2026-11-23').state, 'new')
assert.equal(cell('chaise', '2026-11-23').include, true)
// 超出可上班时间：照排 + 提醒
assert.equal(cell('chaise', '2026-11-27').state, 'warn')
assert.equal(cell('chaise', '2026-11-27').include, true)
// 已有重叠班次：跳过，带上已有班次的时间（带秒的接口时间也要收敛成 HH:mm）
assert.equal(cell('charlie', '2026-11-23').state, 'skip')
assert.equal(cell('charlie', '2026-11-23').include, false)
assert.equal(cell('charlie', '2026-11-23').existingRange, '15:00–17:30')
assert.equal(cell('charlie', '2026-11-24').state, 'skip')
// 已取消的不算重叠；首尾相接不算重叠
assert.equal(cell('charlie', '2026-11-25').state, 'new')
assert.equal(cell('charlie', '2026-11-26').state, 'new')
// 部分重叠也跳过
assert.equal(cell('zoe', '2026-11-27').state, 'skip')
assert.equal(cell('zoe', '2026-11-27').existingRange, '15:30–17:30')
// 员工标记不能上班：默认不排
assert.equal(cell('zoe', '2026-11-25').state, 'unavailable')
assert.equal(cell('zoe', '2026-11-25').include, false)

// 汇总：创建 = 15 − 跳过 3 − 不排 1 = 11；提醒 1
assert.deepEqual(summarizePlan(plan), { create: 11, skipped: 3, excluded: 1, warnings: 1 })

// 手动改格子：不排→排、排→不排；skip 不能改
const overrides = new Map<string, boolean>([
  [batchCellKey({ userGuid: 'zoe', dateKey: '2026-11-25' }), true],
  [batchCellKey({ userGuid: 'chaise', dateKey: '2026-11-23' }), false],
  [batchCellKey({ userGuid: 'charlie', dateKey: '2026-11-23' }), true],
])
const adjusted = applyBatchOverrides(plan, overrides)
const adj = (userGuid: string, dateKey: string) => adjusted.find((item) => item.userGuid === userGuid && item.dateKey === dateKey)!
assert.equal(adj('zoe', '2026-11-25').include, true)
assert.equal(adj('chaise', '2026-11-23').include, false)
assert.equal(adj('charlie', '2026-11-23').include, false)
assert.deepEqual(summarizePlan(adjusted), { create: 11, skipped: 3, excluded: 1, warnings: 1 })
// 原计划不被修改
assert.equal(cell('zoe', '2026-11-25').include, false)

// 载荷：只含要创建的格子；一律草稿；备注去空白；日期升序；手动用餐次数带上
const payloads = buildBatchPayloads(adjusted, { storeCode: '1002', start: '15:00', end: '17:30', remark: '  补缺口  ', mealChoice: 1 })
assert.equal(payloads.length, 11)
assert.ok(payloads.every((item) => item.status === 'Draft' && item.storeCode === '1002' && item.remark === '补缺口' && item.mealBreakCount === 1))
assert.deepEqual(
  payloads.filter((item) => item.userGuid === 'chaise').map((item) => item.workDate),
  ['2026-11-24', '2026-11-25', '2026-11-26', '2026-11-27'],
)
assert.equal(payloads.some((item) => item.userGuid === 'charlie' && item.workDate === '2026-11-23'), false)
// 自动用餐：不带 mealBreakCount 字段；空备注不带 remark
const auto = buildBatchPayloads(adjusted, { storeCode: '1002', start: '15:00', end: '17:30', remark: '   ', mealChoice: 'auto' })
assert.ok(auto.every((item) => item.mealBreakCount === undefined && item.remark === undefined))

// 结果汇总
assert.deepEqual(
  summarizeResults([
    { userGuid: 'a', dateKey: 'd1', ok: true },
    { userGuid: 'a', dateKey: 'd2', ok: false, error: 'x' },
    { userGuid: 'b', dateKey: 'd1', ok: true },
  ]),
  { created: 2, failed: 1 },
)

// 限并发：最多同时 4 个、全部处理、单个失败不影响其它、每个任务都回调 onSettled
async function main() {
  let running = 0
  let peak = 0
  const done: number[] = []
  const settled: number[] = []
  await runWithConcurrency(
    Array.from({ length: 20 }, (_, index) => index),
    4,
    async (item) => {
      running += 1
      peak = Math.max(peak, running)
      await new Promise((resolve) => setTimeout(resolve, 3))
      running -= 1
      if (item === 7) throw new Error('boom')
      done.push(item)
    },
    (item) => settled.push(item),
  )
  assert.equal(peak, 4)
  assert.equal(done.length, 19)
  assert.equal(done.includes(7), false)
  assert.equal(settled.length, 20)

  // 空列表、并发数大于任务数、非法并发数
  await runWithConcurrency([], 4, async () => undefined)
  const few: number[] = []
  await runWithConcurrency([1, 2], 10, async (item) => { few.push(item) })
  assert.deepEqual(few.sort(), [1, 2])
  const zero: number[] = []
  await runWithConcurrency([1, 2, 3], 0, async (item) => { zero.push(item) })
  assert.equal(zero.length, 3)

  console.log('batchScheduleLogic tests passed')
}

void main()
