import assert from 'node:assert/strict'

import { FALLBACK_STAFF_COLOR, STAFF_PALETTE, assignStaffColors, hashString } from './staffColorLogic'

// 哈希稳定：同一个 ID 永远同一个数
assert.equal(hashString('user-1'), hashString('user-1'))
assert.notEqual(hashString('user-1'), hashString('user-2'))

// 色板：16 种，每种底色 / 色条 / 文字色俱全，且互不相同
assert.equal(STAFF_PALETTE.length, 16)
assert.equal(new Set(STAFF_PALETTE.map((item) => item.bar)).size, 16)
STAFF_PALETTE.forEach((item) => {
  assert.match(item.bg, /^#[0-9a-f]{6}$/)
  assert.match(item.bar, /^#[0-9a-f]{6}$/)
  assert.match(item.ink, /^#[0-9a-f]{6}$/)
})
// 红、橙留给状态，色板里不能出现（按色条色相粗略判断：r 远大于 g、b 的纯红 / 纯橙）
STAFF_PALETTE.forEach((item) => {
  const [r, g, b] = [1, 3, 5].map((start) => parseInt(item.bar.slice(start, start + 2), 16))
  const reddish = r > 200 && g < 150 && b < 100
  assert.equal(reddish, false, `${item.bar} 不应是红 / 橙色`)
})

const staff = (count: number) => Array.from({ length: count }, (_, index) => ({ userGuid: `guid-${index}`, name: `Staff ${String(index).padStart(2, '0')}` }))

// 16 人以内一人一色、互不重复
const sixteen = assignStaffColors(staff(16))
assert.equal(sixteen.size, 16)
assert.equal(new Set(Array.from(sixteen.values()).map((item) => item.bar)).size, 16)

// 稳定：同一批人重复分配结果一致；入参顺序不影响结果
const first = assignStaffColors(staff(10))
const second = assignStaffColors(staff(10))
const reversed = assignStaffColors([...staff(10)].reverse())
staff(10).forEach(({ userGuid }) => {
  assert.equal(first.get(userGuid)?.bar, second.get(userGuid)?.bar)
  assert.equal(first.get(userGuid)?.bar, reversed.get(userGuid)?.bar)
})

// 重复 ID 只算一人；没有 ID 的忽略
const dedup = assignStaffColors([{ userGuid: 'a', name: 'A' }, { userGuid: 'a', name: 'A again' }, { userGuid: '', name: 'nobody' }])
assert.equal(dedup.size, 1)

// 超过 16 人：不报错，每个人仍有颜色（只能复用）
const many = assignStaffColors(staff(25))
assert.equal(many.size, 25)
many.forEach((color) => assert.ok(STAFF_PALETTE.includes(color)))

// 撞色顺延：构造两个哈希起点相同的 ID，后者（按姓名靠后）必须拿到不同颜色
let collisionPair: [string, string] | undefined
for (let left = 0; left < 400 && !collisionPair; left += 1) {
  for (let right = left + 1; right < 400; right += 1) {
    if (hashString(`g${left}`) % 16 === hashString(`g${right}`) % 16) {
      collisionPair = [`g${left}`, `g${right}`]
      break
    }
  }
}
assert.ok(collisionPair, '应能找到哈希起点相同的一对 ID')
const [idA, idB] = collisionPair as [string, string]
const pair = assignStaffColors([{ userGuid: idA, name: 'Alice' }, { userGuid: idB, name: 'Bob' }])
assert.notEqual(pair.get(idA)?.bar, pair.get(idB)?.bar)
// 姓名靠前的保留起点颜色，靠后的顺延
assert.equal(pair.get(idA), STAFF_PALETTE[hashString(idA) % 16])

assert.equal(FALLBACK_STAFF_COLOR.bar, '#8c8c8c')

console.log('staffColorLogic tests passed')
