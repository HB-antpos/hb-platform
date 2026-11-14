import assert from 'node:assert/strict'

import {
  ALL_STORES_TOKEN,
  STORE_SIDEBAR_DOCK_MIN_WIDTH,
  buildStoreSections,
  filterStoreOptions,
  getRelativeWeekOffset,
  moveActiveIndex,
  parseRecentStores,
  parseSidebarPreference,
  pushRecentStore,
  resolveInitialStore,
  safeStorageGet,
  safeStorageSet,
  shouldDockSidebar,
} from './storePickerLogic'

const stores = [
  { label: 'Bankstown', value: '1003' },
  { label: 'Burwood', value: '1007' },
  { label: 'Parramatta', value: '1026' },
  { label: 'Robinson Road', value: '1042' },
  { label: 'Rhodes', value: '1031' },
]

// 搜索：名称、编码都能搜，忽略大小写与空格
assert.deepEqual(filterStoreOptions(stores, ' robin ').map((s) => s.value), ['1042'])
assert.deepEqual(filterStoreOptions(stores, '102').map((s) => s.value), ['1026'])
assert.deepEqual(filterStoreOptions(stores, 'r').map((s) => s.value), ['1007', '1026', '1042', '1031'])
assert.equal(filterStoreOptions(stores, '').length, 5)
assert.deepEqual(filterStoreOptions(stores, 'zzz'), [])

// 最近使用：放最前、去重、最多 3 家
assert.deepEqual(pushRecentStore([], '1042'), ['1042'])
assert.deepEqual(pushRecentStore(['1003', '1042'], '1042'), ['1042', '1003'])
assert.deepEqual(pushRecentStore(['1003', '1007', '1026'], '1042'), ['1042', '1003', '1007'])
assert.deepEqual(pushRecentStore(['1003'], undefined), ['1003']) // 选「全部分店」不记入最近
assert.deepEqual(pushRecentStore(['a', 'b', 'c', 'd'], 'e', 2), ['e', 'a'])

// 分段：最近使用只保留仍可选的；搜索时只给结果
assert.deepEqual(buildStoreSections(stores, ['1042', 'GONE', '1026'], '').recent.map((s) => s.value), ['1042', '1026'])
assert.equal(buildStoreSections(stores, ['1042'], '').all.length, 5)
assert.deepEqual(buildStoreSections(stores, ['1042'], 'bur'), { recent: [], all: [stores[1]] })
// 分店少（≤ 4 家）时不分「最近使用」
assert.deepEqual(buildStoreSections(stores.slice(0, 4), ['1042'], '').recent, [])

// 键盘高亮：首尾循环
assert.equal(moveActiveIndex(-1, 1, 3), 0)
assert.equal(moveActiveIndex(-1, -1, 3), 2)
assert.equal(moveActiveIndex(2, 1, 3), 0)
assert.equal(moveActiveIndex(0, -1, 3), 2)
assert.equal(moveActiveIndex(0, 1, 0), -1)

// 初始分店
assert.equal(resolveInitialStore(stores, '1026'), '1026')
assert.equal(resolveInitialStore(stores, 'GONE'), undefined) // 记住的分店已不可选
assert.equal(resolveInitialStore(stores, ALL_STORES_TOKEN), undefined)
assert.equal(resolveInitialStore(stores, null), undefined)
assert.equal(resolveInitialStore([stores[3]], null), '1042') // 只有 1 家直接选中
assert.equal(resolveInitialStore([stores[3]], ALL_STORES_TOKEN), '1042')
assert.equal(resolveInitialStore([], '1042'), undefined)

// 侧栏展开
const dock = (over: Partial<Parameters<typeof shouldDockSidebar>[0]> = {}) => shouldDockSidebar({
  phone: false, storeCount: 12, containerWidth: 1400, preference: null, ...over,
})
assert.equal(STORE_SIDEBAR_DOCK_MIN_WIDTH, 1220)
assert.equal(dock(), true)
assert.equal(dock({ containerWidth: STORE_SIDEBAR_DOCK_MIN_WIDTH }), true)
assert.equal(dock({ containerWidth: STORE_SIDEBAR_DOCK_MIN_WIDTH - 1 }), false) // 13 寸屏默认收起
assert.equal(dock({ containerWidth: 900, preference: 'docked' }), true) // 用户固定了就一直展开
assert.equal(dock({ preference: 'floating' }), false)
assert.equal(dock({ phone: true, preference: 'docked' }), false) // 手机永远不展开
assert.equal(dock({ storeCount: 1, preference: 'docked' }), false) // 只有 1 家不显示

assert.equal(parseSidebarPreference('docked'), 'docked')
assert.equal(parseSidebarPreference('floating'), 'floating')
assert.equal(parseSidebarPreference('x'), null)
assert.equal(parseSidebarPreference(null), null)

assert.deepEqual(parseRecentStores('["1042","1003"]'), ['1042', '1003'])
assert.deepEqual(parseRecentStores('["a","b","c","d"]'), ['a', 'b', 'c'])
assert.deepEqual(parseRecentStores('[1,"",null,"1042"]'), ['1042'])
assert.deepEqual(parseRecentStores('{bad json'), [])
assert.deepEqual(parseRecentStores('"1042"'), [])
assert.deepEqual(parseRecentStores(null), [])

// 相对周数
assert.equal(getRelativeWeekOffset('2026-11-16', '2026-11-16'), 0)
assert.equal(getRelativeWeekOffset('2026-11-23', '2026-11-16'), 1)
assert.equal(getRelativeWeekOffset('2026-11-02', '2026-11-16'), -2)
assert.equal(getRelativeWeekOffset('2027-01-04', '2026-12-28'), 1) // 跨年
assert.equal(getRelativeWeekOffset('2026-10-05', '2026-09-28'), 1) // 悉尼夏令时开始那周
assert.equal(getRelativeWeekOffset('bad', '2026-11-16'), 0)

// 存储读写失败不抛错
const broken = { getItem: () => { throw new Error('denied') }, setItem: () => { throw new Error('quota') } }
assert.equal(safeStorageGet(broken, 'k'), null)
assert.doesNotThrow(() => safeStorageSet(broken, 'k', 'v'))
assert.equal(safeStorageGet(null, 'k'), null)
const memory = new Map<string, string>()
const store = { getItem: (k: string) => memory.get(k) ?? null, setItem: (k: string, v: string) => { memory.set(k, v) } }
safeStorageSet(store, 'k', 'v')
assert.equal(safeStorageGet(store, 'k'), 'v')

console.log('storePickerLogic tests passed')
