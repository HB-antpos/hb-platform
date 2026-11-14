import assert from 'node:assert/strict'

import { PHONE_MAX_SHORT_SIDE, getShellMode, isPhoneDevice, nextAutoDismissed } from './phoneShellLogic'

// 手机：触屏 + 短边 ≤ 600
assert.equal(isPhoneDevice({ coarsePointer: true, maxTouchPoints: 5, shortSide: 390 }), true)
assert.equal(isPhoneDevice({ coarsePointer: false, maxTouchPoints: 5, shortSide: 360 }), true) // 只有 maxTouchPoints 也算触屏
assert.equal(isPhoneDevice({ coarsePointer: true, maxTouchPoints: 0, shortSide: 428 }), true)
assert.equal(isPhoneDevice({ coarsePointer: true, maxTouchPoints: 5, shortSide: PHONE_MAX_SHORT_SIDE }), true)
// 平板、笔记本、桌面不算手机
assert.equal(isPhoneDevice({ coarsePointer: true, maxTouchPoints: 5, shortSide: 768 }), false)
assert.equal(isPhoneDevice({ coarsePointer: true, maxTouchPoints: 5, shortSide: PHONE_MAX_SHORT_SIDE + 1 }), false)
assert.equal(isPhoneDevice({ coarsePointer: false, maxTouchPoints: 0, shortSide: 390 }), false) // 窄的桌面窗口不是手机
assert.equal(isPhoneDevice({ coarsePointer: true, maxTouchPoints: 5, shortSide: 0 }), false) // 取不到尺寸不当手机

const mode = (over: Partial<Parameters<typeof getShellMode>[0]> = {}) => getShellMode({
  phone: true,
  landscape: false,
  requested: false,
  autoDismissed: false,
  ...over,
})

// 非手机：永远 none
assert.equal(mode({ phone: false, landscape: true }), 'none')
assert.equal(mode({ phone: false, requested: true }), 'none')
// 手机竖屏：没点按钮 → none；点了按钮 → rotated（锁定横屏失败时的兜底）
assert.equal(mode(), 'none')
assert.equal(mode({ requested: true }), 'rotated')
// 手机横屏：自动进入 landscape
assert.equal(mode({ landscape: true }), 'landscape')
// 横屏里点了退出：不再自动进入
assert.equal(mode({ landscape: true, autoDismissed: true }), 'none')
// 竖屏点按钮后手机转成横屏 → landscape（即使之前退出过）
assert.equal(mode({ landscape: true, requested: true, autoDismissed: true }), 'landscape')

// 退出标记只在横屏期间有效；回到竖屏就清掉
assert.equal(nextAutoDismissed(true, true), true)
assert.equal(nextAutoDismissed(true, false), false)
assert.equal(nextAutoDismissed(false, true), false)
assert.equal(nextAutoDismissed(false, false), false)

console.log('phoneShellLogic tests passed')
