import assert from 'node:assert/strict'

import {
  DEFAULT_STAFF_NAME_MODE,
  mergeStaffNameSources,
  parseStaffNameMode,
  readStaffNameMode,
  resolveStaffName,
  resolveStaffNames,
  writeStaffNameMode,
} from './staffNameLogic'
import type { StaffNameSource } from './staffNameLogic'

const person = (patch: Partial<StaffNameSource> & { userGuid: string }): StaffNameSource => ({ username: `${patch.userGuid}-login`, ...patch })

// ---------- 单个员工：昵称 → 名 → 全名 → 登录名 → userGuid ----------
assert.equal(DEFAULT_STAFF_NAME_MODE, 'nickname', '默认显示昵称')
assert.equal(resolveStaffName(person({ userGuid: 'a', nickname: 'Immy', givenName: 'Imogen', fullName: 'Imogen Hitchcock' }), 'nickname'), 'Immy')
assert.equal(resolveStaffName(person({ userGuid: 'a', givenName: 'Taylor', fullName: 'Taylor Keating' }), 'nickname'), 'Taylor', '没填昵称 → 名')
assert.equal(resolveStaffName(person({ userGuid: 'a', fullName: 'Zhan Chen' }), 'nickname'), 'Zhan Chen', '名也没有 → 全名')
assert.equal(resolveStaffName({ userGuid: 'a', username: 'hbstaff07' }, 'nickname'), 'hbstaff07', '全名也没有 → 登录名')
assert.equal(resolveStaffName({ userGuid: 'guid-1' }, 'nickname'), 'guid-1', '什么都没有 → userGuid')
// 空白串当作没填，并去掉首尾空白
assert.equal(resolveStaffName(person({ userGuid: 'a', nickname: '   ', givenName: ' Sam ', fullName: 'Sam Lee' }), 'nickname'), 'Sam')
// 全名模式与改动前一致：fullName → username → userGuid，不看昵称 / 名
assert.equal(resolveStaffName(person({ userGuid: 'a', nickname: 'Immy', givenName: 'Imogen', fullName: 'Imogen Hitchcock' }), 'full'), 'Imogen Hitchcock')
assert.equal(resolveStaffName({ userGuid: 'a', username: 'hbstaff07', nickname: 'Hb' }, 'full'), 'hbstaff07')
assert.equal(resolveStaffName({ userGuid: 'guid-1' }, 'full'), 'guid-1')

// ---------- 一批员工：昵称模式重名时区分 ----------
const names = (sources: StaffNameSource[], mode: 'nickname' | 'full' = 'nickname') => Object.fromEntries(resolveStaffNames(sources, mode))

// 不重名：原样返回
assert.deepEqual(
  names([person({ userGuid: 'a', nickname: 'Cody' }), person({ userGuid: 'b', nickname: 'Immy' })]),
  { a: 'Cody', b: 'Immy' },
)
// 两个 Sam：补姓的首字母（大小写不敏感地识别重名）
assert.deepEqual(
  names([
    person({ userGuid: 'a', nickname: 'Sam', familyName: 'Chen', fullName: 'Sam Chen' }),
    person({ userGuid: 'b', nickname: 'sam', familyName: 'lee', fullName: 'Sam Lee' }),
  ]),
  { a: 'Sam C.', b: 'sam L.' },
)
// 同姓首字母（Chen / Cho）补完仍重复 → 改补登录名后缀
assert.deepEqual(
  names([
    person({ userGuid: 'a', username: 'sam.chen', nickname: 'Sam', familyName: 'Chen' }),
    person({ userGuid: 'b', username: 'sam.cho', nickname: 'Sam', familyName: 'Cho' }),
  ]),
  { a: 'Sam (sam.chen)', b: 'Sam (sam.cho)' },
)
// 都没有姓可补 → 直接补登录名后缀
assert.deepEqual(
  names([
    person({ userGuid: 'a', username: 'sam1', nickname: 'Sam' }),
    person({ userGuid: 'b', username: 'sam2', nickname: 'Sam' }),
  ]),
  { a: 'Sam (sam1)', b: 'Sam (sam2)' },
)
// 只有一人有姓：补完已经能区分（Sam / Sam L.），没姓的那位不用再加后缀
assert.deepEqual(
  names([
    person({ userGuid: 'a', username: 'sam1', nickname: 'Sam' }),
    person({ userGuid: 'b', username: 'sam2', nickname: 'Sam', familyName: 'Lee' }),
  ]),
  { a: 'Sam', b: 'Sam L.' },
)
// 中文姓不是拉丁字母：直接用完整的姓，不加点
assert.deepEqual(
  names([
    person({ userGuid: 'a', nickname: 'Amy', familyName: '陈' }),
    person({ userGuid: 'b', nickname: 'Amy', familyName: '李' }),
  ]),
  { a: 'Amy 陈', b: 'Amy 李' },
)
// 已经是全名兜底的两个同名人：名字本身带姓，不补首字母，直接补登录名后缀
assert.deepEqual(
  names([
    person({ userGuid: 'a', username: 'zc1', fullName: 'Zhan Chen', familyName: 'Chen' }),
    person({ userGuid: 'b', username: 'zc2', fullName: 'Zhan Chen', familyName: 'Chen' }),
  ]),
  { a: 'Zhan Chen (zc1)', b: 'Zhan Chen (zc2)' },
)
// 补完首字母后撞上了别人原本的显示名（有人昵称就叫 "Sam C."）→ 撞上的两人补登录名后缀，另一位不受影响
assert.deepEqual(
  names([
    person({ userGuid: 'a', username: 'u1', nickname: 'Sam', familyName: 'Chen' }),
    person({ userGuid: 'b', username: 'u2', nickname: 'Sam', familyName: 'Lee' }),
    person({ userGuid: 'c', username: 'u3', nickname: 'Sam C.' }),
  ]),
  { a: 'Sam (u1)', b: 'Sam L.', c: 'Sam C. (u3)' },
)
// 全名模式保持原样：同名也不处理
assert.deepEqual(
  names([
    person({ userGuid: 'a', fullName: 'Sam Chen' }),
    person({ userGuid: 'b', fullName: 'Sam Chen' }),
  ], 'full'),
  { a: 'Sam Chen', b: 'Sam Chen' },
)

// ---------- 合并取名原料：店长不在员工管理列表里，要靠可排班员工补上 ----------
{
  const grid = [person({ userGuid: 'staff', nickname: 'Cody', fullName: 'Cody Johnson' })]
  // 可排班员工：店员 + 店长。店长 Zhan Chen 只在这里有昵称；店员 Cody 在两边都有（以员工管理列表为准）
  const employees = [
    person({ userGuid: 'staff', nickname: 'Codee', fullName: 'Cody Johnson' }),
    person({ userGuid: 'manager', nickname: 'Terry', givenName: 'Zhan', familyName: 'Chen', fullName: 'Zhan Chen' }),
  ]
  const scheduleOnly = [{ userGuid: 'left', fullName: 'Left Person' }]

  const merged = mergeStaffNameSources(grid, employees, scheduleOnly)
  assert.deepEqual(merged.map((item) => item.userGuid), ['staff', 'manager', 'left'], '同一个人只保留一份，先传的优先，顺序稳定')
  assert.equal(merged[0].nickname, 'Cody', '员工管理列表里有的人以它为准')
  const shown = Object.fromEntries(resolveStaffNames(merged, 'nickname'))
  assert.equal(shown.manager, 'Terry', '店长的昵称来自可排班员工，看板上要显示昵称')
  assert.equal(shown.left, 'Left Person', '已离职只有班次记录的人仍显示全名')
  // 没合并时的老问题：店长只剩排班记录里的全名
  const without = Object.fromEntries(resolveStaffNames(mergeStaffNameSources(grid, [{ userGuid: 'manager', fullName: 'Zhan Chen' }]), 'nickname'))
  assert.equal(without.manager, 'Zhan Chen', '拿不到店长资料时只能显示全名（这正是合并要解决的）')
  assert.deepEqual(mergeStaffNameSources(), [], '没有任何来源时为空')
  assert.deepEqual(mergeStaffNameSources([], []), [], '空列表合并为空')
}

// ---------- 本机记忆 ----------
assert.equal(parseStaffNameMode('full'), 'full')
assert.equal(parseStaffNameMode('nickname'), 'nickname')
assert.equal(parseStaffNameMode(null), 'nickname', '没存过 → 默认昵称')
assert.equal(parseStaffNameMode('乱七八糟'), 'nickname', '被改坏 → 默认昵称')

const memory = (() => {
  const data = new Map<string, string>()
  return { getItem: (key: string) => data.get(key) ?? null, setItem: (key: string, value: string) => { data.set(key, value) } }
})()
assert.equal(readStaffNameMode(memory), 'nickname')
writeStaffNameMode(memory, 'full')
assert.equal(readStaffNameMode(memory), 'full')
writeStaffNameMode(memory, 'nickname')
assert.equal(readStaffNameMode(memory), 'nickname')
// 存储不可用 / 抛错：静默，回到默认
assert.equal(readStaffNameMode(null), 'nickname')
assert.doesNotThrow(() => writeStaffNameMode(null, 'full'))
const broken = { getItem: () => { throw new Error('denied') }, setItem: () => { throw new Error('quota') } }
assert.equal(readStaffNameMode(broken), 'nickname')
assert.doesNotThrow(() => writeStaffNameMode(broken, 'full'))

console.log('staffNameLogic ok')
