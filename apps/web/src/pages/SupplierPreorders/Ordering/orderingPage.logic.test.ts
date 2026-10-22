import assert from 'node:assert/strict'

import type { SupplierPreorderMyStore } from '../../../types/supplierPreorder'

import {
  DAY_MS,
  PAGE_SIZE_STORAGE_KEY,
  computeClockOffsetMs,
  computeContainerRatio,
  computeLineTotals,
  formatAud,
  formatBusinessDate,
  formatSydneyDateTime,
  getDeadlineState,
  getErrorMessage,
  isDeadlinePassed,
  normalizePageSize,
  parseCartonsInput,
  parseUtcMs,
  pickInitialStoreGuid,
  readStoredPageSize,
  safeStorageGet,
  safeStorageSet,
  stepCartons,
  storeStorageKey,
  type KeyValueStorage,
} from './orderingPage.logic'

// ---------- 时间解析与时钟校正 ----------
assert.equal(parseUtcMs(null), null)
assert.equal(parseUtcMs('  '), null)
assert.equal(parseUtcMs('not-a-date'), null)
assert.equal(parseUtcMs('2026-03-06T12:59:00Z'), Date.UTC(2026, 2, 6, 12, 59))
assert.equal(parseUtcMs('2026-03-06T12:59:00'), Date.UTC(2026, 2, 6, 12, 59), '不带时区的后端时间按 UTC 解析')
assert.equal(parseUtcMs('2026-03-06T23:59:00+11:00'), Date.UTC(2026, 2, 6, 12, 59))

const serverNow = '2026-03-01T00:00:00Z'
const serverMs = Date.UTC(2026, 2, 1)
// 本机慢了 10 分钟：偏差 = +10 分钟
assert.equal(computeClockOffsetMs(serverNow, serverMs - 10 * 60_000), 10 * 60_000)
assert.equal(computeClockOffsetMs(null, 123), 0, '服务器时间缺失时不校正')
assert.equal(computeClockOffsetMs('bad', 123), 0)

// ---------- 截止状态 ----------
const deadline = '2026-03-06T12:59:00Z'
const deadlineMs = Date.UTC(2026, 2, 6, 12, 59)
assert.deepEqual(getDeadlineState(null, serverMs), { kind: 'none', remainingMs: null, daysLeft: null, hoursLeft: null })
const open = getDeadlineState(deadline, deadlineMs - 12 * DAY_MS + 1000)
assert.equal(open.kind, 'open')
assert.equal(open.daysLeft, 12)
const urgent = getDeadlineState(deadline, deadlineMs - 3 * DAY_MS)
assert.equal(urgent.kind, 'urgent', '剩正好 3 天算提醒')
assert.equal(urgent.daysLeft, 3)
assert.equal(getDeadlineState(deadline, deadlineMs - 3 * DAY_MS - 1).kind, 'open', '超过 3 天不提醒')
const hours = getDeadlineState(deadline, deadlineMs - 5 * 60 * 60 * 1000 + 1)
assert.equal(hours.kind, 'urgent')
assert.equal(hours.hoursLeft, 5)
assert.equal(hours.daysLeft, 1)
assert.equal(getDeadlineState(deadline, deadlineMs).kind, 'closed', '到点即截止')
assert.equal(getDeadlineState(deadline, deadlineMs + 1).remainingMs, 0)
assert.equal(getDeadlineState(deadline, deadlineMs - 30 * DAY_MS, true).kind, 'closed', '后端已锁定优先于本地时间')
assert.equal(getDeadlineState(null, serverMs, true).kind, 'closed')

// 本机快了 1 小时：不校正会误判已截止，校正后仍可下单
const localNowFast = deadlineMs + 30 * 60_000
const offset = computeClockOffsetMs('2026-03-06T12:29:00Z', localNowFast)
assert.equal(isDeadlinePassed(deadline, localNowFast), true)
assert.equal(isDeadlinePassed(deadline, localNowFast + offset), false, '按服务器时钟校正后未截止')
assert.equal(isDeadlinePassed(null, localNowFast), false)

// ---------- 悉尼时间显示 ----------
// 3 月悉尼为夏令时 UTC+11
assert.equal(formatSydneyDateTime('2026-03-06T12:59:00Z'), '2026-03-06 23:59')
// 7 月为标准时 UTC+10
assert.equal(formatSydneyDateTime('2026-07-01T14:00:00Z'), '2026-07-02 00:00', '午夜显示 00 而不是 24')
assert.equal(formatSydneyDateTime(null), '--')
assert.equal(formatSydneyDateTime('坏值'), '坏值')
assert.equal(formatBusinessDate('2026-02-05T00:00:00'), '2026-02-05')
assert.equal(formatBusinessDate(undefined), '--')

// ---------- 件数与金额 ----------
assert.deepEqual(computeLineTotals(4, 24, 1.1), { pieces: 96, amount: 105.6 })
assert.deepEqual(computeLineTotals(3, 12, 9.88), { pieces: 36, amount: 355.68 })
assert.deepEqual(computeLineTotals(0, 24, 1.1), { pieces: 0, amount: 0 })
assert.deepEqual(computeLineTotals(-2, 24, 1.1), { pieces: 0, amount: 0 }, '负数按 0')
assert.deepEqual(computeLineTotals(Number.NaN, 24, 1.1), { pieces: 0, amount: 0 })
assert.deepEqual(computeLineTotals(1, 3, 0.1), { pieces: 3, amount: 0.3 }, '金额去掉浮点尾巴')
assert.equal(formatAud(4912.36), '$4,912.36')
assert.equal(formatAud(0), '$0.00')
assert.equal(formatAud(Number.NaN), '$0.00')

// ---------- 箱数输入 ----------
assert.equal(parseCartonsInput(' 12 '), 12)
assert.equal(parseCartonsInput(''), 0, '清空 = 0')
assert.equal(parseCartonsInput('0'), 0)
assert.equal(parseCartonsInput('1.5'), null, '不允许小数')
assert.equal(parseCartonsInput('-1'), null, '不允许负数')
assert.equal(parseCartonsInput('1e3'), null)
assert.equal(parseCartonsInput('abc'), null)
assert.equal(parseCartonsInput('10000'), null, '超过上限')
assert.equal(stepCartons(0, -1), 0)
assert.equal(stepCartons(2, 1), 3)
assert.equal(stepCartons(9999, 1), 9999)

// ---------- 柜容量比例 ----------
assert.equal(computeContainerRatio(41.8, 67), 41.8 / 67)
assert.equal(computeContainerRatio(41.8, null), null)
assert.equal(computeContainerRatio(41.8, 0), null)

// ---------- 分页大小 ----------
assert.equal(normalizePageSize(100), 100)
assert.equal(normalizePageSize('500'), 500)
assert.equal(normalizePageSize(20), 50, '不在允许列表里回到 50')
assert.equal(normalizePageSize('abc'), 50)
assert.equal(normalizePageSize(null), 50)
assert.equal(normalizePageSize(Number.NaN), 50)

// ---------- 本机记忆容错 ----------
const memory = new Map<string, string>()
const goodStorage: KeyValueStorage = {
  getItem: (key) => memory.get(key) ?? null,
  setItem: (key, value) => {
    memory.set(key, value)
  },
}
const brokenStorage: KeyValueStorage = {
  getItem: () => {
    throw new Error('SecurityError')
  },
  setItem: () => {
    throw new Error('QuotaExceededError')
  },
}
safeStorageSet(goodStorage, PAGE_SIZE_STORAGE_KEY, '200')
assert.equal(readStoredPageSize(goodStorage), 200)
safeStorageSet(goodStorage, PAGE_SIZE_STORAGE_KEY, '30')
assert.equal(readStoredPageSize(goodStorage), 50, '存了非法值也回到 50')
assert.equal(safeStorageGet(brokenStorage, 'x'), null, '读取抛异常返回 null')
safeStorageSet(brokenStorage, 'x', '1')
assert.equal(readStoredPageSize(brokenStorage), 50)
assert.equal(readStoredPageSize(null), 50)
assert.equal(storeStorageKey('F1'), 'sp-order:store:F1')

// ---------- 初始分店 ----------
const makeStore = (storeGuid: string, canEdit: boolean): SupplierPreorderMyStore => ({
  storeGuid,
  storeCode: storeGuid,
  storeName: storeGuid,
  canEdit,
  orderStatus: 'None',
  cartons: 0,
  amount: 0,
})
const stores = [makeStore('S1', false), makeStore('S2', true), makeStore('S3', true)]
assert.equal(pickInitialStoreGuid(stores, 'S3'), 'S3', '优先上次记住的分店')
assert.equal(pickInitialStoreGuid(stores, 'GONE'), 'S2', '记住的分店已不在列表：取第一家可编辑的')
assert.equal(pickInitialStoreGuid([makeStore('S1', false)], null), 'S1')
assert.equal(pickInitialStoreGuid([], 'S1'), null)

// ---------- 错误文案 ----------
assert.equal(getErrorMessage({ payload: { message: '已过下单截止时间' }, message: 'X: y' }, '保存失败'), '已过下单截止时间')
assert.equal(getErrorMessage(new Error('SUPPLIER_PREORDER_LOCKED: 订单表已锁定'), '保存失败'), '订单表已锁定')
assert.equal(getErrorMessage(new Error('箱数必须为整数'), '保存失败'), '箱数必须为整数')
assert.equal(getErrorMessage(null, '保存失败'), '保存失败')
assert.equal(getErrorMessage({ message: '' }, '保存失败'), '保存失败')
