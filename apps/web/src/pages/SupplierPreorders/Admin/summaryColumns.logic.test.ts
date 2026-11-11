import assert from 'node:assert/strict'

import type { SupplierPreorderStoreRef } from '../../../types/supplierPreorder'

import {
  EMPTY_COLUMN_PREFS,
  collectOrderedStoreGuids,
  finalIndexAfterDrop,
  hideColumn,
  insertIndexAtRow,
  insertIndexAtX,
  loadSummaryView,
  matchesStoreKeyword,
  moveVisibleBy,
  parseSummaryView,
  previewDrop,
  prunePrefs,
  reorderByDrop,
  reorderVisible,
  resetColumns,
  resolveColumns,
  saveSummaryView,
  serializeSummaryView,
  showColumn,
  showOnlyOrdered,
  storeHasOrder,
  summarizeHiddenColumns,
  summaryViewStorageKey,
  type ColumnPrefs,
  type StoredSummaryView,
} from './summaryColumns.logic'

const store = (code: string, name = code): SupplierPreorderStoreRef => ({ storeGuid: `g-${code}`, storeCode: code, storeName: name })
// 矩阵里的开启分店（按店号）：A B C D E；A、B、D 有订单，C、E 没下单
const stores = ['A', 'B', 'C', 'D', 'E'].map((code) => store(code))
const statuses = { 'g-A': 'Submitted', 'g-B': 'Draft', 'g-C': 'None', 'g-D': 'None', 'g-E': 'None' } as const
const storeCartons = { 'g-A': 3, 'g-B': 0, 'g-C': 0, 'g-D': 5, 'g-E': 0 }
const ordered = collectOrderedStoreGuids(stores, { ...statuses }, storeCartons)
const names = (list: SupplierPreorderStoreRef[]) => list.map((item) => item.storeCode).join('')

// ---------- 默认显示规则 ----------
assert.equal(storeHasOrder('g-A', { ...statuses }, storeCartons), true, '已提交算有订单')
assert.equal(storeHasOrder('g-B', { ...statuses }, storeCartons), true, '草稿算有订单（即使箱数 0）')
assert.equal(storeHasOrder('g-D', { ...statuses }, storeCartons), true, '状态 None 但有箱数也算')
assert.equal(storeHasOrder('g-C', { ...statuses }, storeCartons), false)
assert.deepEqual([...ordered].sort(), ['g-A', 'g-B', 'g-D'])

const base = resolveColumns(stores, ordered, EMPTY_COLUMN_PREFS)
assert.equal(names(base.visible), 'ABD', '默认只显示有订单的，按矩阵顺序')
assert.equal(names(base.hiddenStores), 'CE')
assert.equal(names(base.all), 'ABCDE')

// ---------- 重排：移到某位置之前 / 末尾 ----------
assert.equal(finalIndexAfterDrop(2, 0), 0, '向左拖：插入位置即最终位置')
assert.equal(finalIndexAfterDrop(0, 3), 2, '向右拖：先去掉自己，位置前移一位')
assert.equal(finalIndexAfterDrop(1, 1), 1)
assert.equal(finalIndexAfterDrop(1, 2), 1, '拖到自己右边紧邻的缝 = 没动')

const dragged = reorderByDrop(EMPTY_COLUMN_PREFS, base, 'g-D', 0)!
assert.deepEqual(dragged.order, ['g-D', 'g-A', 'g-B', 'g-C', 'g-E'], '显示列在前，未显示列保持相对顺序在后')
assert.equal(names(resolveColumns(stores, ordered, dragged).visible), 'DAB')
assert.equal(reorderByDrop(EMPTY_COLUMN_PREFS, base, 'g-A', 0), null, '落在自己之前 = 没变')
assert.equal(reorderByDrop(EMPTY_COLUMN_PREFS, base, 'g-A', 1), null, '落在自己之后紧邻 = 没变')
assert.equal(names(resolveColumns(stores, ordered, reorderByDrop(EMPTY_COLUMN_PREFS, base, 'g-A', 3)!).visible), 'BDA', '拖到最右')
assert.equal(reorderByDrop(EMPTY_COLUMN_PREFS, base, 'g-C', 0), null, '未显示的列不能拖')
assert.equal(reorderVisible(EMPTY_COLUMN_PREFS, base, 'g-A', 99)!.order[2], 'g-A', 'toIndex 越界按末尾处理')

// ---------- 键盘左右移一列 ----------
const right = moveVisibleBy(EMPTY_COLUMN_PREFS, base, 'g-A', 1)!
assert.equal(right.position, 2)
assert.equal(names(resolveColumns(stores, ordered, right.prefs).visible), 'BAD')
assert.equal(moveVisibleBy(EMPTY_COLUMN_PREFS, base, 'g-A', -1), null, '已在最左')
assert.equal(moveVisibleBy(EMPTY_COLUMN_PREFS, base, 'g-D', 1), null, '已在最右')
const left = moveVisibleBy(EMPTY_COLUMN_PREFS, base, 'g-D', -1)!
assert.equal(left.position, 2)
assert.equal(names(resolveColumns(stores, ordered, left.prefs).visible), 'ADB')

// ---------- 手动显示 / 隐藏 / 新增 ----------
const withC = showColumn(EMPTY_COLUMN_PREFS, base, 'g-C')
assert.equal(names(resolveColumns(stores, ordered, withC).visible), 'ABDC', '手动加的列排在最右')
assert.deepEqual(withC.shown, ['g-C'])
const withoutA = hideColumn(withC, 'g-A')
assert.equal(names(resolveColumns(stores, ordered, withoutA).visible), 'BDC', '有订单的列也能手动隐藏')
const again = showColumn(withoutA, resolveColumns(stores, ordered, withoutA), 'g-A')
assert.equal(names(resolveColumns(stores, ordered, again).visible), 'BDCA', '隐藏后再加回来也在最右')
assert.deepEqual(again.hidden, [])
assert.deepEqual(hideColumn(withC, 'g-C').shown, [], '隐藏会清掉手动显示')
assert.equal(
  names(resolveColumns(stores, ordered, { order: [], shown: ['g-A'], hidden: ['g-A'] }).visible),
  'BD',
  '同时在 shown 与 hidden 时以 hidden 为准',
)

// 新开启的分店（矩阵里多一家 F）：不在手动顺序里 → 排最右；显式加列也在最右
const stores6 = [...stores, store('F')]
const reordered = reorderByDrop(EMPTY_COLUMN_PREFS, base, 'g-D', 0)!
const resolved6 = resolveColumns(stores6, ordered, showColumn(reordered, resolveColumns(stores6, ordered, reordered), 'g-F'))
assert.equal(names(resolved6.visible), 'DABF')

// 「只显示有订单的」保留顺序、清掉手动显示隐藏；「恢复默认」全部清空
const only = showOnlyOrdered(hideColumn(showColumn(reordered, resolveColumns(stores, ordered, reordered), 'g-C'), 'g-A'))
assert.deepEqual([only.shown, only.hidden], [[], []])
assert.equal(names(resolveColumns(stores, ordered, only).visible), 'DAB')
assert.deepEqual(resetColumns(), EMPTY_COLUMN_PREFS)
assert.equal(names(resolveColumns(stores, ordered, resetColumns()).visible), 'ABD')

// 有订单的店后来被手动隐藏，之后再有订单不会自动冒出来
assert.equal(names(resolveColumns(stores, new Set([...ordered, 'g-C']), hideColumn(EMPTY_COLUMN_PREFS, 'g-C')).visible), 'ABD')
assert.equal(names(resolveColumns(stores, new Set([...ordered, 'g-C']), EMPTY_COLUMN_PREFS).visible), 'ABCD', '默认规则：后来有订单的店自动出现')

// ---------- 存储自洽 ----------
const messy: ColumnPrefs = { order: ['g-X', 'g-D', 'g-D', 'g-A'], shown: ['g-Y', 'g-C'], hidden: ['g-Z'] }
const valid = new Set(stores.map((item) => item.storeGuid))
const pruned = prunePrefs(messy, valid)
assert.deepEqual(pruned, { order: ['g-D', 'g-A'], shown: ['g-C'], hidden: [] }, '丢弃已不存在的 guid 并去重')
assert.equal(prunePrefs(pruned, valid), pruned, '没有变化返回原对象')
// 设置里有已不存在的 guid，解析时也不会出现幽灵列
assert.equal(names(resolveColumns(stores, ordered, messy).visible), 'DABC', '幽灵 guid 被忽略：手动顺序 D A 在前，其余按矩阵顺序，手动加的 C 显示')

const hiddenSummary = summarizeHiddenColumns(base, ordered)
assert.deepEqual(hiddenSummary, { count: 2, names: ['C', 'E'], allWithoutOrder: true })
assert.equal(summarizeHiddenColumns(resolveColumns(stores, ordered, hideColumn(EMPTY_COLUMN_PREFS, 'g-A')), ordered).allWithoutOrder, false, '隐藏了有订单的列时措辞要换')
assert.equal(summarizeHiddenColumns(resolveColumns(stores, ordered, withC), ordered).allWithoutOrder, false, '手动加了没订单的列，「显示有订单的」就不准了')
assert.equal(summarizeHiddenColumns(resolveColumns(stores, new Set(stores.map((item) => item.storeGuid)), EMPTY_COLUMN_PREFS), ordered).count, 0)

// ---------- localStorage 容错 ----------
const view: StoredSummaryView = { columns: { order: ['g-D', 'g-A'], shown: ['g-C'], hidden: ['g-B'] }, sort: { sort: 'storeCartonsDesc', storeGuid: 'g-A' } }
assert.deepEqual(parseSummaryView(serializeSummaryView(view)), view, '往返一致')
for (const bad of [null, undefined, '', '{', 'null', '[]', '"x"', '{"v":2,"order":["a"]}', '{"order":["a"]}']) {
  assert.deepEqual(parseSummaryView(bad).columns, EMPTY_COLUMN_PREFS, `坏数据按默认处理：${String(bad)}`)
  assert.equal(parseSummaryView(bad).sort.sort, 'seq')
}
const dirty = parseSummaryView('{"v":1,"order":["a",1,null,"a"],"shown":["x","y"],"hidden":["x"],"sort":{"sort":"bogus","storeGuid":"s"}}')
assert.deepEqual(dirty.columns, { order: ['a'], shown: ['y'], hidden: ['x'] }, '非字符串项丢弃、去重、hidden 优先')
assert.equal(dirty.sort.sort, 'seq', '不认识的排序值回默认')
assert.equal(parseSummaryView('{"v":1,"sort":{"sort":"storeCartonsAsc"}}').sort.sort, 'seq', '分店排序缺店 → 默认')

const memory = new Map<string, string>()
const fakeStorage = { getItem: (key: string) => memory.get(key) ?? null, setItem: (key: string, value: string) => void memory.set(key, value) }
saveSummaryView('form-1', view, fakeStorage)
assert.ok(memory.has(summaryViewStorageKey('form-1')))
assert.deepEqual(loadSummaryView('form-1', fakeStorage), view)
assert.deepEqual(loadSummaryView('form-2', fakeStorage).columns, EMPTY_COLUMN_PREFS, '各订单表互相独立')
const throwing = {
  getItem: () => {
    throw new Error('blocked')
  },
  setItem: () => {
    throw new Error('quota')
  },
}
assert.deepEqual(loadSummaryView('form-1', throwing).columns, EMPTY_COLUMN_PREFS, '读不到就用默认')
assert.doesNotThrow(() => saveSummaryView('form-1', view, throwing), '写失败静默')
assert.doesNotThrow(() => loadSummaryView('form-1', null))

// ---------- 拖拽落点 ----------
const rects = [
  { left: 0, right: 100 },
  { left: 100, right: 200 },
  { left: 200, right: 300 },
]
assert.equal(insertIndexAtX(rects, -50), 0)
assert.equal(insertIndexAtX(rects, 49), 0)
assert.equal(insertIndexAtX(rects, 51), 1, '过了第一列中线才算越过它')
assert.equal(insertIndexAtX(rects, 260), 3, '越过最后一列中线 → 插到最右')
assert.equal(insertIndexAtX([], 10), 0)
assert.equal(insertIndexAtRow(2, 10, { top: 0, bottom: 40 }), 2, '行上半 → 之前')
assert.equal(insertIndexAtRow(2, 30, { top: 0, bottom: 40 }), 3, '行下半 → 之后')

const guids = ['a', 'b', 'c', 'd']
assert.deepEqual(previewDrop(guids, 'd', 1), { changed: true, position: 2, beforeGuid: 'b' }, '把第 4 列拖到第 2 列之前 → 第 2 位')
assert.deepEqual(previewDrop(guids, 'a', 4), { changed: true, position: 4, beforeGuid: null }, '拖到最右')
assert.equal(previewDrop(guids, 'b', 2)!.changed, false)
assert.equal(previewDrop(guids, 'zzz', 0), null)

// ---------- 分店搜索 ----------
const candidates = [
  { storeName: 'Lake Haven', storeCode: '112' },
  { storeName: 'Greenhills', storeCode: '205' },
  { storeName: 'HB Warehouse', storeCode: 'WH' },
]
assert.equal(candidates.filter((item) => matchesStoreKeyword(item, '')).length, 3)
assert.deepEqual(candidates.filter((item) => matchesStoreKeyword(item, ' LAKE ')).map((item) => item.storeCode), ['112'], '不区分大小写、忽略首尾空格')
assert.deepEqual(candidates.filter((item) => matchesStoreKeyword(item, '205')).map((item) => item.storeName), ['Greenhills'], '按店号搜')
assert.deepEqual(candidates.filter((item) => matchesStoreKeyword(item, 'wh')).map((item) => item.storeName), ['HB Warehouse'])
assert.equal(candidates.filter((item) => matchesStoreKeyword(item, 'nothing')).length, 0)

console.log('summaryColumns.logic tests passed')
