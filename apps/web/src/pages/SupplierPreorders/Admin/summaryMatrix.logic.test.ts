import assert from 'node:assert/strict'

import type {
  SupplierPreorderAdminSaveLineResult,
  SupplierPreorderMatrix,
  SupplierPreorderMatrixRow,
  SupplierPreorderMatrixTotals,
  SupplierPreorderStoreHistory,
  SupplierPreorderStoreRef,
} from '../../../types/supplierPreorder'
import { makeLineKey } from '../Ordering/lineSaveQueue.logic'

import {
  DEFAULT_CONTAINER_CAPACITY_CBM,
  applyPendingEdits,
  applySaveResult,
  buildStoreDetailRows,
  computeContainerProgress,
  countStoreProgress,
  formatLastYearCartons,
  getLastYearCellValue,
  isMissedCell,
  keyToMove,
  moveCell,
  parseCartonInput,
  sanitizeCartonDraft,
  stepIndex,
  sumStoreDetailRows,
  suggestFillBlanks,
  thisYearRatio,
  toLastYearCartons,
  toMonthDayTime,
} from './summaryMatrix.logic'

// ---------- 输入解析 ----------
assert.deepEqual(parseCartonInput('12'), { ok: true, value: 12 })
assert.deepEqual(parseCartonInput(' 0 '), { ok: true, value: 0 })
assert.deepEqual(parseCartonInput('9999'), { ok: true, value: 9999 })
assert.deepEqual(parseCartonInput('10000'), { ok: false, reason: 'invalid' })
assert.deepEqual(parseCartonInput('1.5'), { ok: false, reason: 'invalid' })
assert.deepEqual(parseCartonInput('-1'), { ok: false, reason: 'invalid' })
assert.deepEqual(parseCartonInput('abc'), { ok: false, reason: 'invalid' })
assert.deepEqual(parseCartonInput('  '), { ok: false, reason: 'empty' })
assert.equal(sanitizeCartonDraft('1a2-3.4567'), '1234')

// ---------- 键盘移动 ----------
assert.equal(keyToMove('Enter'), 'down')
assert.equal(keyToMove('Tab'), 'next')
assert.equal(keyToMove('Tab', true), 'prev')
assert.equal(keyToMove('ArrowLeft'), 'left')
assert.equal(keyToMove('x'), null)
// 3 行 × 4 家店
assert.deepEqual(moveCell({ row: 0, col: 0 }, 'down', 3, 4), { row: 1, col: 0 })
assert.equal(moveCell({ row: 2, col: 0 }, 'down', 3, 4), null)
assert.equal(moveCell({ row: 0, col: 0 }, 'up', 3, 4), null)
assert.deepEqual(moveCell({ row: 0, col: 1 }, 'right', 3, 4), { row: 0, col: 2 })
assert.equal(moveCell({ row: 0, col: 3 }, 'right', 3, 4), null, '方向键不换行')
assert.deepEqual(moveCell({ row: 0, col: 3 }, 'next', 3, 4), { row: 1, col: 0 }, 'Tab 到行尾换到下一行第一家店')
assert.equal(moveCell({ row: 2, col: 3 }, 'next', 3, 4), null)
assert.deepEqual(moveCell({ row: 1, col: 0 }, 'prev', 3, 4), { row: 0, col: 3 })
assert.equal(moveCell({ row: 0, col: 0 }, 'prev', 3, 4), null)
assert.equal(moveCell({ row: 0, col: 0 }, 'down', 0, 4), null)

// ---------- 折合箱与偏差 ----------
assert.equal(toLastYearCartons(158, 24), 158 / 24)
assert.equal(toLastYearCartons(10, 0), 0)
assert.equal(formatLastYearCartons(158, 24), '6.6')
assert.equal(formatLastYearCartons(undefined, 24), null)
assert.equal(formatLastYearCartons(0, 24), null)
assert.equal(isMissedCell(0, 1.5), true)
assert.equal(isMissedCell(0, 1.49), false)
assert.equal(isMissedCell(1, 4), false)
assert.equal(thisYearRatio(6, 24, 158), 144 / 158)
assert.equal(thisYearRatio(6, 24, 0), null)
assert.equal(thisYearRatio(6, 24, undefined), null)
const lyRow = { storeLastYearSales: { a: 48 }, cartonQty: 24, price: 2.5 }
assert.equal(getLastYearCellValue(lyRow, 'a', 'cartons'), 2)
assert.equal(getLastYearCellValue(lyRow, 'a', 'pieces'), 48)
assert.equal(getLastYearCellValue(lyRow, 'a', 'amount'), 120)
assert.equal(getLastYearCellValue(lyRow, 'b', 'cartons'), null)

// ---------- 乐观合计与保存结果 ----------
const emptyTotals = (): SupplierPreorderMatrixTotals => ({
  storeCartons: { a: 5, b: 0 },
  storePieces: { a: 120, b: 0 },
  storeAmount: { a: 300, b: 0 },
  storeCbm: { a: 0.25, b: 0 },
  orderedItemCount: 1,
  totalCartons: 5,
  totalPieces: 120,
  totalAmount: 300,
  totalCbm: 0.25,
})
const makeRow = (itemGuid: string, cartons: Record<string, number>): SupplierPreorderMatrixRow => {
  const total = Object.values(cartons).reduce((sum, value) => sum + value, 0)
  return {
    itemGuid,
    seq: 1,
    supplierItemCode: 'X',
    name: itemGuid,
    price: 2.5,
    isNew: false,
    cartonQty: 24,
    cartonCbm: 0.05,
    storeCartons: cartons,
    totalCartons: total,
    totalPieces: total * 24,
    totalAmount: total * 24 * 2.5,
    totalCbm: total * 0.05,
    storeLastYearSales: {},
    storeMarks: {},
  }
}
const matrix: SupplierPreorderMatrix = {
  stores: [
    { storeGuid: 'a', storeCode: 'A', storeName: 'Alpha' },
    { storeGuid: 'b', storeCode: 'B', storeName: 'Beta' },
  ],
  rows: { items: [makeRow('i1', { a: 5 }), makeRow('i2', {})], total: 2, page: 1, pageSize: 50 },
  totals: emptyTotals(),
  storeStatuses: { a: 'Submitted', b: 'None' },
  counts: { total: 2, ordered: 1, missed: 0, adjusted: 0 },
  categories: [],
  editable: true,
  history: { status: 'Ready', keyDates: [] },
}

assert.equal(applyPendingEdits(matrix, {}), matrix, '没有待保存内容时返回同一对象')
assert.equal(applyPendingEdits(matrix, { [makeLineKey('a', 'i1')]: 5 }), matrix, '值没变化也不重建')
assert.equal(applyPendingEdits(matrix, { [makeLineKey('a', 'missing')]: 3 }), matrix, '不在当前页的商品忽略')

const edited = applyPendingEdits(matrix, { [makeLineKey('b', 'i1')]: 2, [makeLineKey('a', 'i2')]: 3 })
assert.notEqual(edited, matrix)
assert.equal(matrix.rows.items[0].storeCartons.b, undefined, '不能改动入参')
assert.deepEqual(edited.rows.items[0].storeCartons, { a: 5, b: 2 })
assert.equal(edited.rows.items[0].totalCartons, 7)
assert.equal(edited.rows.items[0].totalPieces, 168)
assert.equal(edited.rows.items[0].totalAmount, 420)
assert.equal(edited.rows.items[0].totalCbm, 0.35)
assert.equal(edited.rows.items[1].totalCartons, 3)
assert.equal(edited.totals.storeCartons.b, 2)
assert.equal(edited.totals.storeCartons.a, 8)
assert.equal(edited.totals.totalCartons, 10)
assert.equal(edited.totals.totalPieces, 240)
assert.equal(edited.totals.totalAmount, 600)
assert.equal(edited.totals.orderedItemCount, 2, 'i2 从没订变成有订，已下单款数 +1')
// 改回 0：从格子里移除，已下单款数回落
const cleared = applyPendingEdits(matrix, { [makeLineKey('a', 'i1')]: 0 })
assert.deepEqual(cleared.rows.items[0].storeCartons, {})
assert.equal(cleared.totals.orderedItemCount, 0)
assert.equal(cleared.totals.totalCartons, 0)

const result: SupplierPreorderAdminSaveLineResult = {
  itemGuid: 'i1',
  storeGuid: 'a',
  cartons: 6,
  mark: { originalCartons: 5, adjustedBy: 'sean', adjustedAtUtc: '2026-11-11T00:28:00Z' },
  rowTotalCartons: 6,
  rowTotalPieces: 144,
  rowTotalAmount: 360,
  rowTotalCbm: 0.3,
  totals: { ...emptyTotals(), totalCartons: 6, orderedItemCount: 1 },
  adjustedCount: 4,
}
const saved = applySaveResult(matrix, result)
assert.equal(saved.rows.items[0].storeCartons.a, 6)
assert.equal(saved.rows.items[0].storeMarks.a.originalCartons, 5)
assert.equal(saved.rows.items[0].totalCartons, 6)
assert.equal(saved.rows.items[1], matrix.rows.items[1], '其他行不动')
assert.equal(saved.totals.totalCartons, 6)
assert.equal(saved.counts.adjusted, 4)
assert.equal(saved.counts.ordered, 1)
// 改回分店原值：标记清除；改成 0：格子移除
const reverted = applySaveResult(saved, { ...result, cartons: 0, mark: null, rowTotalCartons: 0, adjustedCount: 3 })
assert.deepEqual(reverted.rows.items[0].storeMarks, {})
assert.equal(reverted.rows.items[0].storeCartons.a, undefined)
assert.equal(reverted.counts.adjusted, 3)

// ---------- 指标条 ----------
const progress = computeContainerProgress(121.95, 67)
assert.equal(progress.fullCount, 1)
assert.equal(progress.fallback, false)
assert.ok(Math.abs(progress.partialRatio - (121.95 - 67) / 67) < 1e-9)
assert.ok(Math.abs(progress.remainingCbm - (134 - 121.95)) < 1e-9)
assert.equal(computeContainerProgress(67, 67).fullCount, 1, '正好整柜算满一柜')
assert.equal(computeContainerProgress(67, 67).remainingCbm, 67)
assert.equal(computeContainerProgress(10, null).capacity, DEFAULT_CONTAINER_CAPACITY_CBM)
assert.equal(computeContainerProgress(10, 0).fallback, true)
assert.equal(computeContainerProgress(0, 33).partialRatio, 0)
assert.deepEqual(
  (({ submitted, draft, none, segments }) => ({ submitted, draft, none, segments }))(
    countStoreProgress(matrix.stores, { a: 'Submitted', b: 'Draft' }),
  ),
  { submitted: 1, draft: 1, none: 0, segments: ['Submitted', 'Draft'] },
)
assert.equal(countStoreProgress(matrix.stores, {}).none, 2, '缺状态按未开始')

// ---------- 抽屉分店明细 ----------
const history = (partial: Partial<SupplierPreorderStoreHistory> & Pick<SupplierPreorderStoreHistory, 'storeName'>): SupplierPreorderStoreHistory => ({
  storeCode: partial.storeName,
  isCurrent: false,
  hasData: true,
  purchaseBatches: 1,
  purchaseQty: 0,
  warehouseQty: 0,
  supplierQty: 0,
  salesQty: 0,
  discountStatus: 'none',
  hasDiscount: false,
  discountRanges: [],
  daily: [],
  batches: [],
  ...partial,
})
const stores: SupplierPreorderStoreRef[] = [
  { storeGuid: 'a', storeCode: 'A', storeName: 'Alpha' },
  { storeGuid: 'b', storeCode: 'B', storeName: 'Beta' },
  { storeGuid: 'c', storeCode: 'C', storeName: 'Gamma' },
  { storeGuid: 'd', storeCode: 'D', storeName: 'Delta' },
]
const histories = [
  history({ storeGuid: 'a', storeName: 'Alpha', purchaseQty: 72, salesQty: 54, sellThrough: 0.75 }),
  history({ storeGuid: 'b', storeName: 'Beta', purchaseQty: 240, salesQty: 158, sellThrough: 0.66 }),
  history({ storeGuid: 'c', storeName: 'Gamma', purchaseQty: 120, salesQty: 83, sellThrough: 0.69 }),
  // d 去年没有记录
  history({ storeGuid: 'x', storeName: 'Closed Store', purchaseQty: 48, salesQty: 41, sellThrough: 0.85 }),
  history({ storeGuid: null, storeName: 'No Data Extra', hasData: false }),
]
const mine: Record<string, number> = { a: 3, b: 6, c: 0, d: 0 }
const rows = buildStoreDetailRows(stores, histories, 24, (guid) => mine[guid] ?? 0)
assert.deepEqual(
  rows.map((row) => row.storeName),
  ['Beta', 'Gamma', 'Alpha', 'Closed Store', 'Delta'],
  '有记录的按销量降序，没开启但有记录的靠后，无记录的店垫底',
)
const byName = Object.fromEntries(rows.map((row) => [row.storeName, row]))
assert.equal(byName.Beta.flag, null)
assert.equal(byName.Gamma.flag, 'missed', '去年折合 3.5 箱今年没订')
assert.equal(byName.Alpha.flag, null, '3 箱 = 72 件，对去年 54 件 = 133%，不到 160%')
mine.a = 4 // 96 件 / 54 = 178%
assert.equal(buildStoreDetailRows(stores, histories, 24, (guid) => mine[guid] ?? 0).find((row) => row.storeName === 'Alpha')?.flag, 'high')
assert.equal(byName.Delta.hasData, false)
assert.equal(byName.Delta.ratio, null)
assert.equal(byName.Delta.flag, null)
assert.equal(byName['Closed Store'].enabled, false)
assert.equal(byName['Closed Store'].storeGuid, null)
assert.equal(byName['Closed Store'].flag, null, '没开启的分店不做偏差提示')
assert.equal(rows.length, 5, '没数据的非开启分店不出现')

const totals = sumStoreDetailRows(rows, 24)
assert.equal(totals.purchaseQty, 72 + 240 + 120 + 48)
assert.equal(totals.salesQty, 54 + 158 + 83 + 41)
assert.equal(totals.thisYear, 9, '没开启的分店今年订货按 0')
assert.equal(totals.sellThrough, totals.salesQty / totals.purchaseQty)
assert.equal(totals.ratio, (9 * 24) / totals.salesQty)

// 填入空白店：只对开启、今年 0、去年有销量的店；至少 1 箱，按销量折合四舍五入
mine.a = 3
const suggestions = suggestFillBlanks(buildStoreDetailRows(stores, histories, 24, (guid) => mine[guid] ?? 0), 24)
assert.deepEqual(suggestions, [{ storeGuid: 'c', storeName: 'Gamma', cartons: 3 }])
const tiny = buildStoreDetailRows(stores, [history({ storeGuid: 'd', storeName: 'Delta', purchaseQty: 24, salesQty: 5 })], 24, () => 0)
assert.deepEqual(
  suggestFillBlanks(tiny, 24).map((item) => item.cartons),
  [1],
  '去年只卖 5 件也至少建议 1 箱',
)
assert.deepEqual(suggestFillBlanks(tiny, 0), [], '箱规无效没有建议')

// ---------- 翻页与时间 ----------
assert.equal(stepIndex(2, 1, 12), 3)
assert.equal(stepIndex(0, -1, 12), null)
assert.equal(stepIndex(11, 1, 12), null)
assert.equal(stepIndex(-1, 1, 12), null)
assert.equal(toMonthDayTime('2026-11-11 10:28'), '11-11 10:28')
assert.equal(toMonthDayTime('--'), '--')
