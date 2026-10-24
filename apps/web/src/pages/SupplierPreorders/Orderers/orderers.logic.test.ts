import assert from 'node:assert/strict'

import type { SupplierPreorderOrderer, SupplierPreorderStoreRef } from '../../../types/supplierPreorder'

import {
  countCanViewOtherStores,
  filterOrderers,
  formatCandidateLabel,
  getOrdererDisplayName,
  getPersonInitial,
  mergeStoreSelection,
  normalizeStoreSelection,
} from './orderers.logic'

const orion: SupplierPreorderStoreRef = { storeGuid: 'orion', storeCode: '1001', storeName: 'Orion' }
const kawana: SupplierPreorderStoreRef = { storeGuid: 'kawana', storeCode: '1002', storeName: 'Kawana' }
const qld: SupplierPreorderStoreRef = { storeGuid: 'qld', storeCode: '1003', storeName: 'QLD' }

const base: SupplierPreorderOrderer = {
  userGuid: 'u',
  userName: 'user@hb',
  fullName: null,
  isActive: true,
  primaryStoreName: null,
  canViewOtherStores: false,
  stores: [],
  hasOrderEntryPermission: true,
  updatedAtUtc: '2026-02-09T00:00:00Z',
}

const orderers: SupplierPreorderOrderer[] = [
  { ...base, userGuid: 'wang', userName: 'wang@hb', fullName: '王店长', primaryStoreName: 'Orion', stores: [orion] },
  { ...base, userGuid: 'zhang', userName: 'zhang@hb', fullName: '张采购', canViewOtherStores: true, stores: [orion, kawana] },
  { ...base, userGuid: 'zhao', userName: 'zhao@hb', fullName: 'Zhao Manager', canViewOtherStores: true, stores: [qld] },
]

assert.deepEqual(filterOrderers(orderers, { keyword: '', view: 'all' }).map((p) => p.userGuid), ['wang', 'zhang', 'zhao'])
assert.deepEqual(filterOrderers(orderers, { keyword: '', view: 'canView' }).map((p) => p.userGuid), ['zhang', 'zhao'])
assert.deepEqual(filterOrderers(orderers, { keyword: '', storeGuid: 'orion', view: 'all' }).map((p) => p.userGuid), ['wang', 'zhang'])
assert.deepEqual(filterOrderers(orderers, { keyword: '', storeGuid: 'orion', view: 'canView' }).map((p) => p.userGuid), ['zhang'])
// 关键字匹配姓名、账号、主分店、可下单分店（不区分大小写）
assert.deepEqual(filterOrderers(orderers, { keyword: 'ZHAO', view: 'all' }).map((p) => p.userGuid), ['zhao'])
assert.deepEqual(filterOrderers(orderers, { keyword: '采购', view: 'all' }).map((p) => p.userGuid), ['zhang'])
assert.deepEqual(filterOrderers(orderers, { keyword: 'kawana', view: 'all' }).map((p) => p.userGuid), ['zhang'])
assert.deepEqual(filterOrderers(orderers, { keyword: '  ', view: 'all' }).length, 3)

assert.equal(countCanViewOtherStores(orderers), 2)

assert.equal(getOrdererDisplayName({ fullName: '王店长', userName: 'wang@hb' }), '王店长')
assert.equal(getOrdererDisplayName({ fullName: '  ', userName: 'wang@hb' }), 'wang@hb')
assert.equal(getPersonInitial('王店长'), '王')
assert.equal(getPersonInitial('zhao'), 'Z')
assert.equal(getPersonInitial(''), '?')

// 勾选结果按启用分店顺序，丢掉不在列表里的
assert.deepEqual(normalizeStoreSelection(['qld', 'orion', 'gone'], [orion, kawana, qld]), ['orion', 'qld'])
// 编辑时原有的停用分店（不在启用列表里）要保留
assert.deepEqual(mergeStoreSelection(['kawana'], [orion, kawana], ['orion', 'closed-store', 'closed-store']), ['kawana', 'closed-store'])
assert.deepEqual(mergeStoreSelection([], [orion], []), [])

assert.equal(formatCandidateLabel({ fullName: '赵区域经理', userName: 'zhao@hb', primaryStoreName: 'QLD' }), '赵区域经理 (zhao@hb) · QLD')
assert.equal(formatCandidateLabel({ fullName: null, userName: 'buyer@hb', primaryStoreName: null }), 'buyer@hb')
assert.equal(formatCandidateLabel({ fullName: 'buyer@hb', userName: 'buyer@hb', primaryStoreName: null }), 'buyer@hb')
