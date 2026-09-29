import assert from 'node:assert/strict'

import { requiresDelistSupplyNotice } from './delistSupplyNoticeGate'

// 单个编辑：从上架改为下架才要求填写。
assert.equal(requiresDelistSupplyNotice(false, true), true, '上架 → 下架：必须先填写供货说明')
assert.equal(requiresDelistSupplyNotice(false, false), false, '原本下架且保持下架：不强制，已有说明保留')
assert.equal(requiresDelistSupplyNotice(true, true), false, '保持上架：不需要')
assert.equal(requiresDelistSupplyNotice(true, false), false, '下架 → 上架：不需要（后端会关闭说明）')

// 批量场景：原状态混杂，不传原状态。
assert.equal(requiresDelistSupplyNotice(false), true, '批量设为下架：必须先填写供货说明')
assert.equal(requiresDelistSupplyNotice(true), false, '批量设为上架：不需要')
assert.equal(requiresDelistSupplyNotice(undefined), false, '批量修改留空不改状态：不需要')
assert.equal(requiresDelistSupplyNotice(null), false, '清空选择（null）视同不改：不需要')
assert.equal(requiresDelistSupplyNotice(false, null), true, '原状态未知时按需要处理')

console.log('delistSupplyNoticeGate tests passed')
