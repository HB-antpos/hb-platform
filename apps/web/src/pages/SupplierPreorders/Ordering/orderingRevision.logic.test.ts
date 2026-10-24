import assert from 'node:assert/strict'

import type { SupplierPreorderRemovedLine } from '../../../types/supplierPreorder'

import { REMOVED_LINES_PREVIEW_LIMIT, summarizeRemovedLines } from './orderingRevision.logic'

function line(code: string, cartons: number): SupplierPreorderRemovedLine {
  return { itemGuid: `g-${code}`, supplierItemCode: code, name: `Item ${code}`, cartons }
}

// 没有下架商品 / 箱数都是 0：不提示
assert.equal(summarizeRemovedLines(null, false), null)
assert.equal(summarizeRemovedLines([], false), null)
assert.equal(summarizeRemovedLines([line('XD1', 0)], false), null)

// 少于上限：全部列出，没有「展开」
const few = summarizeRemovedLines([line('XD1', 3), line('XD2', 0), line('XD3', 2)], false)
assert.deepEqual(few && { count: few.count, total: few.totalCartons, codes: few.shown.map((item) => item.supplierItemCode), hidden: few.hiddenCount }, {
  count: 2,
  total: 5,
  codes: ['XD1', 'XD3'],
  hidden: 0,
})

// 超过上限：收起时只列前 N 款，展开后全部
const many = Array.from({ length: REMOVED_LINES_PREVIEW_LIMIT + 3 }, (_, index) => line(`XD${index + 1}`, 1))
const collapsed = summarizeRemovedLines(many, false)
assert.equal(collapsed?.shown.length, REMOVED_LINES_PREVIEW_LIMIT)
assert.equal(collapsed?.hiddenCount, 3)
assert.equal(collapsed?.totalCartons, REMOVED_LINES_PREVIEW_LIMIT + 3)
const expanded = summarizeRemovedLines(many, true)
assert.equal(expanded?.shown.length, REMOVED_LINES_PREVIEW_LIMIT + 3)
assert.equal(expanded?.hiddenCount, 0)

// 自定义上限；上限最少列 1 款
assert.equal(summarizeRemovedLines(many, false, 2)?.shown.length, 2)
assert.equal(summarizeRemovedLines(many, false, 0)?.shown.length, 1)

console.log('orderingRevision.logic tests passed')
