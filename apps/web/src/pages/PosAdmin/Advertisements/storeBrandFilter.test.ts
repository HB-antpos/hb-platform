import {
  UNBRANDED_STORE_KEY,
  applyScopeSelection,
  buildStoreBrandGroups,
  filterStoresByBrand,
  getScopeSelectionState,
} from './storeBrandFilter'

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)

  if (actualJson !== expectedJson) {
    throw new Error(`${message}. Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

const stores = [
  { value: '1001', label: 'Bankstown', brandName: 'Hot Bargain' },
  { value: '1002', label: 'Campbelltown', brandName: 'hot bargain ' },
  { value: '1003', label: 'Charlestown Square', brandName: 'Hot Bargain' },
  { value: '1004', label: 'Discount General Charlestown', brandName: 'Discount General' },
  { value: '1005', label: 'Forster', brandName: 'Dollar King' },
  { value: '1006', label: 'TestStore' },
]

assertDeepEqual(
  buildStoreBrandGroups(stores),
  [
    { key: 'hot bargain', brandName: 'Hot Bargain', count: 3 },
    { key: 'discount general', brandName: 'Discount General', count: 1 },
    { key: 'dollar king', brandName: 'Dollar King', count: 1 },
    { key: UNBRANDED_STORE_KEY, count: 1 },
  ],
  '品牌应不区分大小写归并、按分店数排序，未设置品牌的组排最后',
)

assertDeepEqual(
  filterStoresByBrand(stores, 'hot bargain').map((store) => store.value),
  ['1001', '1002', '1003'],
  '按品牌筛选应只保留该品牌分店',
)

assertDeepEqual(
  filterStoresByBrand(stores, UNBRANDED_STORE_KEY).map((store) => store.value),
  ['1006'],
  '未设置品牌组应只包含没有品牌的分店',
)

assertDeepEqual(filterStoresByBrand(stores, null).length, stores.length, '全部品牌不做筛选')

assertDeepEqual(
  getScopeSelectionState(['1001', '1005'], ['1001', '1002', '1003']),
  { checked: false, indeterminate: true },
  '范围内部分选中应为半选',
)

assertDeepEqual(
  getScopeSelectionState(['1001', '1002', '1003'], ['1001', '1002', '1003']),
  { checked: true, indeterminate: false },
  '范围内全部选中应为勾选',
)

assertDeepEqual(getScopeSelectionState([], []), { checked: false, indeterminate: false }, '空范围不应显示为勾选')

assertDeepEqual(
  applyScopeSelection(['1005', '1001'], ['1001', '1002', '1003'], true),
  ['1005', '1001', '1002', '1003'],
  '范围内全选应保留范围外已选分店且不重复',
)

assertDeepEqual(
  applyScopeSelection(['1005', '1001', '1002'], ['1001', '1002', '1003'], false),
  ['1005'],
  '范围内取消只移除范围内分店',
)

console.log('storeBrandFilter.test: ok')
