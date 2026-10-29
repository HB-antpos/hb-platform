import type { TabItem } from '../types/router'
import {
  getAdjacentTabKey,
  getClosableKeys,
  isClosableTab,
  isLockedTab,
  moveTabWithinGroup,
  normalizeTabOrder,
  setTabLocked,
} from './tabsLogic'

function assert(condition: unknown, message: string) {
  if (!condition) throw new Error(message)
}

function assertKeys(tabs: TabItem[], expected: string[], message: string) {
  const actual = tabs.map((item) => item.key).join(',')
  assert(actual === expected.join(','), `${message}：期望 ${expected.join(',')}，实际 ${actual}`)
}

function tab(key: string, extra: Partial<TabItem> = {}): TabItem {
  return { key, path: key, routePath: key, title: key, closable: true, ...extra }
}

const home = tab('home', { affix: true, closable: false })

// normalizeTabOrder：固定 → 锁定 → 普通，同段内保持相对顺序；固定页签上的 locked 被清掉
{
  const result = normalizeTabOrder([home, tab('a'), tab('b', { locked: true }), tab('c'), tab('d', { locked: true })])
  assertKeys(result, ['home', 'b', 'd', 'a', 'c'], '锁定页签应排到普通页签之前')

  const dirty = normalizeTabOrder([{ ...home, locked: true }, tab('a')])
  assert(dirty[0].locked === undefined, '固定页签不应带 locked')
}

// setTabLocked：锁定排到锁定段末尾，取消锁定排到普通段开头
{
  const base = [home, tab('l1', { locked: true }), tab('a'), tab('b'), tab('c')]
  const locked = setTabLocked(base, 'b', true)
  assertKeys(locked, ['home', 'l1', 'b', 'a', 'c'], '锁定后紧跟已锁定页签')
  assert(isLockedTab(locked[2]), '锁定标记应写入页签')

  const unlocked = setTabLocked([home, tab('l1', { locked: true }), tab('l2', { locked: true }), tab('a')], 'l1', false)
  assertKeys(unlocked, ['home', 'l2', 'l1', 'a'], '取消锁定应落在普通段开头而不是最右边')
  assert(!isLockedTab(unlocked[2]), '取消锁定后标记应清除')

  assert(setTabLocked(base, 'home', true) === base, '固定页签不能锁定')
  assert(setTabLocked(base, 'missing', true) === base, '不存在的页签原样返回')
  assert(setTabLocked(base, 'l1', true) === base, '重复锁定原样返回')
}

// moveTabWithinGroup：只在同组内移动
{
  const base = [home, tab('l1', { locked: true }), tab('l2', { locked: true }), tab('a'), tab('b'), tab('c')]
  assertKeys(moveTabWithinGroup(base, 'a', 'c'), ['home', 'l1', 'l2', 'b', 'c', 'a'], '普通页签向后拖')
  assertKeys(moveTabWithinGroup(base, 'c', 'a'), ['home', 'l1', 'l2', 'c', 'a', 'b'], '普通页签向前拖')
  assertKeys(moveTabWithinGroup(base, 'l2', 'l1'), ['home', 'l2', 'l1', 'a', 'b', 'c'], '锁定组内互换')
  assert(moveTabWithinGroup(base, 'a', 'l1') === base, '普通页签不能拖进锁定组')
  assert(moveTabWithinGroup(base, 'l1', 'b') === base, '锁定页签不能拖出锁定组')
  assert(moveTabWithinGroup(base, 'home', 'a') === base, '固定页签不能拖动')
  assert(moveTabWithinGroup(base, 'a', 'home') === base, '不能拖到固定页签之前')
  assert(moveTabWithinGroup(base, 'a', 'a') === base, '原地不动')
}

// getAdjacentTabKey：键盘 Alt+←/→ 调整顺序
{
  const base = [home, tab('l1', { locked: true }), tab('a'), tab('b')]
  assert(getAdjacentTabKey(base, 'b', 'left') === 'a', '普通页签向左取相邻普通页签')
  assert(getAdjacentTabKey(base, 'a', 'left') === undefined, '普通组最左不能越过分组边界')
  assert(getAdjacentTabKey(base, 'b', 'right') === undefined, '最右没有相邻')
  assert(getAdjacentTabKey(base, 'l1', 'left') === undefined, '锁定组最左不能越过固定页签')
  assert(getAdjacentTabKey(base, 'l1', 'right') === undefined, '锁定页签不能向右越过分组边界')
  assert(getAdjacentTabKey(base, 'home', 'right') === undefined, '固定页签没有键盘排序')
}

// isClosableTab / getClosableKeys：批量关闭跳过固定、锁定与不可关闭页签
{
  const base = [
    home,
    tab('l1', { locked: true }),
    tab('a'),
    tab('fixedByRoute', { closable: false }),
    tab('b'),
    tab('c'),
  ]
  assert(!isClosableTab(home), '固定页签不可关闭')
  assert(!isClosableTab(base[1]), '锁定页签不可关闭')
  assert(!isClosableTab(base[3]), '路由声明不可关闭的页签不在批量关闭范围')
  assert(isClosableTab(base[2]), '普通页签可关闭')

  assert(getClosableKeys(base, 'b', 'others').join() === 'a,c', '关闭其他：保留目标、固定、锁定、不可关闭')
  assert(getClosableKeys(base, 'b', 'left').join() === 'a', '关闭左侧：跳过锁定与不可关闭')
  assert(getClosableKeys(base, 'b', 'right').join() === 'c', '关闭右侧')
  assert(getClosableKeys(base, 'l1', 'others').join() === 'a,b,c', '以锁定页签为目标时，其他普通页签都可关闭')
  assert(getClosableKeys(base, 'missing', 'others').length === 0, '目标不存在时什么都不关')
  assert(getClosableKeys([home, tab('l1', { locked: true }), tab('a')], 'a', 'left').length === 0, '左侧只有锁定页签时无可关闭')
}

console.log('tabsLogic tests passed')
