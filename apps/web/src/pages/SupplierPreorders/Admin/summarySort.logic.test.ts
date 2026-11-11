import assert from 'node:assert/strict'

import { DEFAULT_SORT, SORT_OPTION_VALUES, isSortValue, isStoreSort, nextStoreSort, normalizeSort, storeSortDirection, toSortQuery } from './summarySort.logic'

// 点分店列头：该店 desc → asc → 回默认序号；换一家店重新从 desc 开始
const first = nextStoreSort(DEFAULT_SORT, 'g1')
assert.deepEqual(first, { sort: 'storeCartonsDesc', storeGuid: 'g1' })
const second = nextStoreSort(first, 'g1')
assert.deepEqual(second, { sort: 'storeCartonsAsc', storeGuid: 'g1' })
assert.deepEqual(nextStoreSort(second, 'g1'), DEFAULT_SORT)
assert.deepEqual(nextStoreSort(second, 'g2'), { sort: 'storeCartonsDesc', storeGuid: 'g2' }, '换店从 desc 开始')
assert.deepEqual(nextStoreSort({ sort: 'priceAsc' }, 'g1'), { sort: 'storeCartonsDesc', storeGuid: 'g1' }, '从下拉里的排序切到店排序')

// 列头箭头
assert.equal(storeSortDirection(first, 'g1'), 'desc')
assert.equal(storeSortDirection(second, 'g1'), 'asc')
assert.equal(storeSortDirection(first, 'g2'), null)
assert.equal(storeSortDirection(DEFAULT_SORT, 'g1'), null)

// 接口参数：默认序号不传；店排序带 sortStoreGuid
assert.deepEqual(toSortQuery(DEFAULT_SORT), {})
assert.deepEqual(toSortQuery({ sort: 'priceDesc' }), { sort: 'priceDesc' })
assert.deepEqual(toSortQuery(first), { sort: 'storeCartonsDesc', sortStoreGuid: 'g1' })
assert.deepEqual(toSortQuery({ sort: 'storeCartonsAsc' }), {}, '缺店的店排序不发请求参数，避免后端 400')

// 容错
assert.equal(isSortValue('ratioAsc'), true)
assert.equal(isSortValue('bogus'), false)
assert.equal(isSortValue(undefined), false)
assert.equal(isStoreSort(first), true)
assert.equal(isStoreSort({ sort: 'seq' }), false)
assert.deepEqual(normalizeSort({ sort: 'storeCartonsDesc', storeGuid: 'gone' }, new Set(['g1'])), DEFAULT_SORT, '店已不在订单表里 → 默认')
assert.deepEqual(normalizeSort(first, new Set(['g1'])), first)
assert.deepEqual(normalizeSort(first, new Set()), first, '矩阵还没加载（集合为空）时不丢弃')
assert.deepEqual(normalizeSort({ sort: 'storeCartonsDesc' }), DEFAULT_SORT)
assert.deepEqual(normalizeSort({ sort: 'priceAsc', storeGuid: 'x' }), { sort: 'priceAsc' }, '非店排序去掉残留的店')
assert.equal(SORT_OPTION_VALUES[0], 'seq', '默认排序排在下拉第一项')
assert.ok(SORT_OPTION_VALUES.every(isSortValue))

console.log('summarySort.logic tests passed')
