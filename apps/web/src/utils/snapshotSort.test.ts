import assert from 'node:assert/strict'
import { arrangeByIds, sortItemIds } from './snapshotSort'

interface Row {
  id: string
  no?: string
  price?: number
  active?: boolean
}

const rows: Row[] = [
  { id: 'a', no: 'HB121-091-11', price: 5.5, active: true },
  { id: 'b', no: 'HB121-091-2', price: 3.5, active: false },
  { id: 'c', no: 'HB121-091-03' },
  { id: 'd', no: ' ', price: 3.5 },
]
const getId = (row: Row) => row.id
const getValue = (row: Row, field: string) => (row as unknown as Record<string, unknown>)[field]
const sort = (field: string, order: 'ascend' | 'descend') => sortItemIds(rows, getId, getValue, field, order)

assert.deepEqual(sort('price', 'ascend'), ['b', 'd', 'a', 'c'], '数值升序：相等保持原序，空值排最后')
assert.deepEqual(sort('price', 'descend'), ['a', 'b', 'd', 'c'], '数值降序：空值仍排最后')
assert.deepEqual(sort('no', 'ascend'), ['b', 'c', 'a', 'd'], '文本自然序（2 < 03 < 11），空白按空值排最后')
assert.deepEqual(sort('no', 'descend'), ['a', 'c', 'b', 'd'], '文本降序，空白仍排最后')
assert.deepEqual(sort('active', 'ascend'), ['b', 'a', 'c', 'd'], '布尔值 false < true，空值排最后')

// getValue 可返回编辑后的值：把 c 的价格改成 1 后它应排最前
const editedValue = (row: Row, field: string) => (row.id === 'c' && field === 'price' ? 1 : getValue(row, field))
assert.deepEqual(sortItemIds(rows, getId, editedValue, 'price', 'ascend'), ['c', 'b', 'd', 'a'], '排序应使用 getValue 给出的编辑后值')

assert.deepEqual(
  arrangeByIds([...rows, { id: 'e' }], getId, ['b', 'zz', 'd', 'a', 'c']).map(getId),
  ['b', 'd', 'a', 'c', 'e'],
  '按记录顺序展示，忽略已删除 id，新增行接在末尾',
)
assert.equal(arrangeByIds(rows, getId, null), rows, '没有排序时原样返回')

console.log('snapshotSort.test: ok')
