import assert from 'node:assert/strict'
import {
  getPromotionEditorStoreCodes,
  getPromotionStoreCodes,
  isPromotionWithoutStores,
} from './promotionStoreScope'

assert.equal(isPromotionWithoutStores({ stores: [] }), true, '空 stores 是未绑定分店（收银不生效），不是全部分店')
assert.equal(
  isPromotionWithoutStores({ scopeType: 'Headquarters', stores: [{ id: '1', storeCode: '  ' }] }),
  true,
  '只有空白分店码也算未绑定',
)
assert.equal(isPromotionWithoutStores({ stores: [{ id: '1', storeCode: 'S01' }] }), false, '有具体分店')

assert.deepEqual(getPromotionStoreCodes([{ id: '1', storeCode: ' S01 ' }]), ['S01'], '回填时去掉首尾空白')
assert.deepEqual(
  getPromotionEditorStoreCodes({ scopeType: 'Headquarters', stores: [{ id: '1', storeCode: 'S01' }] }),
  ['S01'],
  '编辑时按实际分店回填，不再因旧的 Headquarters 标记清空',
)
assert.deepEqual(getPromotionEditorStoreCodes({ stores: [] }), [], '未绑定的旧促销回填为空，由必填校验要求补选')

console.log('promotionStoreScope.test: ok')
